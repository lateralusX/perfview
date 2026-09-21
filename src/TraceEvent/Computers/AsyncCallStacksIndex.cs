// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

using FastSerialization;

using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>
    /// The recorded, queryable, serializable result of <see cref="AsyncProfilerComputer"/>: an index over all
    /// the per-thread <see cref="AsyncCallStack"/> runs, plus a deduplicated table of
    /// <see cref="AsyncCallStackFrames"/>.
    /// <para>
    /// Async call stacks on a thread may overlap (they nest), so a point query returns the ordered nested
    /// stack. Times are in the trace's QPC domain (the async buffer's internal QPC matches the containing
    /// ETW/EventPipe file's timestamps, so no conversion is needed). This type is
    /// <see cref="IFastSerializable"/> so it can be persisted into (and lazily loaded from) an ETLX/TraceLog
    /// cache; the per-thread interval trees used for querying are rebuilt on demand and are not serialized.
    /// </para>
    /// </summary>
    public sealed class AsyncCallStacksIndex : IFastSerializable
    {
        private const int SerializationVersion = 1;

        private readonly List<AsyncCallStackFrames> _internedAsyncCallStackFrames = new List<AsyncCallStackFrames>();
        private readonly Dictionary<FrameKey, AsyncCallStackFramesIndex> _frameKeyToIndex = new Dictionary<FrameKey, AsyncCallStackFramesIndex>();
        private readonly Dictionary<AsyncThreadKey, ThreadCallStacks> _threads = new Dictionary<AsyncThreadKey, ThreadCallStacks>();

        private readonly Dictionary<ProcessIndex, CompletionAvailability> _completionAvailability =
            new Dictionary<ProcessIndex, CompletionAvailability>();

        /// <summary>
        /// Invoked (if set) the first time a distinct <see cref="AsyncCallStackFrames"/> is interned, so a build-time
        /// consumer (<see cref="TraceLog"/>) can begin symbolizing its frames as they are discovered. Not used after load.
        /// </summary>
        internal Action<AsyncCallStackFrames> OnFrameInterned { get; set; }

        /// <summary>The number of distinct interned frame lists.</summary>
        public int DistinctFramesCount => _internedAsyncCallStackFrames.Count;

        /// <summary>True if no async call stacks were recorded (used to avoid persisting an empty index).</summary>
        public bool IsEmpty => _threads.Count == 0;

        /// <summary>The threads that have at least one recorded async call stack.</summary>
        public IEnumerable<AsyncThreadKey> Threads => _threads.Keys;

        /// <summary>
        /// True if the trace contained any <c>CompleteMethod</c> (normal completion) event for async call stacks of
        /// the given <paramref name="kind"/>. These events are keyword-gated, so this distinguishes "CompleteMethod
        /// events were not being emitted" from "they were emitted, but nothing has completed yet" — a distinction a
        /// per-stack completion count cannot make. When true, <see cref="AsyncCallStack.GetMethodCompletedFrameCount"/>
        /// is the authoritative normal-completed count (0 genuinely means "nothing completed yet"); when false, the
        /// normal completed count must be derived another way (the continuation-wrapper slot for V2, or the
        /// inline-resumed frames on the sync stack for V1). Exceptional completions are tracked separately (see
        /// <see cref="ExceptionCompletionObserved(ProcessIndex, AsyncCallstackKind)"/>) because unwound frames leave the sync stack.
        /// </summary>
        public bool MethodCompletionObserved(AsyncCallstackKind kind) => MethodCompletionObserved(0, kind);

        public bool MethodCompletionObserved(ProcessIndex processIndex, AsyncCallstackKind kind) =>
            _completionAvailability.TryGetValue(processIndex, out CompletionAvailability availability) &&
            availability.MethodObserved(kind);

        /// <summary>
        /// True if the trace contained any <c>Unwind</c> (exceptional completion) event for async call stacks of the
        /// given <paramref name="kind"/>. Exceptional completions leave the sync stack, so they can only be observed
        /// from these events; <see cref="AsyncCallStack.GetExceptionCompletedFrameCount"/> should be added to the
        /// normal completed count regardless of how the latter was derived.
        /// </summary>
        public bool ExceptionCompletionObserved(AsyncCallstackKind kind) => ExceptionCompletionObserved(0, kind);

        public bool ExceptionCompletionObserved(ProcessIndex processIndex, AsyncCallstackKind kind) =>
            _completionAvailability.TryGetValue(processIndex, out CompletionAvailability availability) &&
            availability.ExceptionObserved(kind);

        /// <summary>
        /// Records that a <c>CompleteMethod</c> (normal completion) event of the given <paramref name="kind"/> was
        /// seen in the stream. Called by <see cref="AsyncProfilerComputer"/> as it processes events. This is a
        /// process-level, per-kind fact (the events are keyword-gated), so it is only reliable after the whole stream
        /// has been processed — do not stamp it onto individual <see cref="AsyncCallStack"/>s at close time.
        /// </summary>
        internal void MarkMethodCompletionObserved(ProcessIndex processIndex, AsyncCallstackKind kind)
        {
            CompletionAvailability availability = GetCompletionAvailability(processIndex);
            availability.MarkMethodObserved(kind);
        }

        /// <summary>
        /// Records that an <c>Unwind</c> (exceptional completion) event of the given <paramref name="kind"/> was seen
        /// in the stream. Called by <see cref="AsyncProfilerComputer"/> as it processes events. Same process-level,
        /// per-kind semantics as <see cref="MarkMethodCompletionObserved(ProcessIndex, AsyncCallstackKind)"/>.
        /// </summary>
        internal void MarkExceptionCompletionObserved(ProcessIndex processIndex, AsyncCallstackKind kind)
        {
            CompletionAvailability availability = GetCompletionAvailability(processIndex);
            availability.MarkExceptionObserved(kind);
        }

        /// <summary>Resolves an interned frames handle to its frames (null if out of range).</summary>
        public AsyncCallStackFrames GetFrames(AsyncCallStackFramesIndex index)
        {
            int i = (int)index;
            return (uint)i < (uint)_internedAsyncCallStackFrames.Count ? _internedAsyncCallStackFrames[i] : null;
        }

        /// <summary>
        /// Returns the nested async call stacks active on <paramref name="thread"/> at <paramref name="qpc"/>,
        /// ordered bottom-to-top (ascending depth). Empty if the thread has no async call stack covering that instant.
        /// </summary>
        public IReadOnlyList<AsyncCallStack> GetAsyncCallStacks(AsyncThreadKey thread, long qpc)
        {
            var result = new List<AsyncCallStack>();
            if (_threads.TryGetValue(thread, out ThreadCallStacks callStacks))
            {
                callStacks.Query(qpc, result, this, cacheMaterialized: true);
                result.Sort(CompareDepth);
            }
            return result;
        }

        internal void GetAsyncCallStacks(AsyncThreadKey thread, long qpc, List<AsyncCallStack> result)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            if (_threads.TryGetValue(thread, out ThreadCallStacks callStacks))
            {
                callStacks.Query(qpc, result, this, cacheMaterialized: false);
                result.Sort(CompareDepth);
            }
            else
            {
                result.Clear();
            }
        }

        /// <summary>All recorded async call stacks for a thread, in the order they closed.</summary>
        public IReadOnlyList<AsyncCallStack> GetAsyncCallStacks(AsyncThreadKey thread) =>
            _threads.TryGetValue(thread, out ThreadCallStacks c)
                ? (IReadOnlyList<AsyncCallStack>)new ThreadCallStackList(this, c)
                : Array.Empty<AsyncCallStack>();

        /// <summary>
        /// Records a finalized async call stack, interning its frames. Returns the created
        /// <see cref="AsyncCallStack"/>. Called by <see cref="AsyncProfilerComputer"/> as async call stacks close.
        /// </summary>
        internal void Add(AsyncThreadKey thread, AsyncCallstackKind kind, ulong[] methodIds, int[] frameStates,
            int depth, byte continuationIndexBase, byte wrapperCount, long startQpc, long endQpc,
            AsyncCallStack.CompletionDelta[] methodCompletions, AsyncCallStack.CompletionDelta[] exceptionCompletions, long[] wrapperResets)
        {
            AsyncCallStackFramesIndex framesIndex = Intern(kind, methodIds, frameStates, thread.ProcessIndex, out AsyncCallStackFrames frames);
            Add(thread, framesIndex, frames, depth, continuationIndexBase, wrapperCount, startQpc, endQpc,
                methodCompletions, exceptionCompletions, wrapperResets);
        }

        internal void Add(AsyncThreadKey thread, AsyncCallStackFramesIndex framesIndex, AsyncCallStackFrames frames,
            int depth, byte continuationIndexBase, byte wrapperCount, long startQpc, long endQpc,
            AsyncCallStack.CompletionDelta[] methodCompletions, AsyncCallStack.CompletionDelta[] exceptionCompletions, long[] wrapperResets)
        {
            GetOrCreate(thread).Add(framesIndex, depth, continuationIndexBase, wrapperCount, startQpc, endQpc,
                methodCompletions, exceptionCompletions, wrapperResets);
        }

        internal bool TryGetInternedFrames(ProcessIndex processIndex, AsyncCallstackKind kind,
            ulong[] methodIds, int[] frameStates, int frameCount,
            out AsyncCallStackFramesIndex framesIndex, out AsyncCallStackFrames frames)
        {
            var key = new FrameKey(processIndex, kind, methodIds, frameStates, frameCount);
            if (_frameKeyToIndex.TryGetValue(key, out framesIndex))
            {
                frames = _internedAsyncCallStackFrames[(int)framesIndex];
                return true;
            }

            frames = null;
            return false;
        }

        private AsyncCallStackFramesIndex Intern(AsyncCallstackKind kind, ulong[] methodIds, int[] frameStates, ProcessIndex processIndex, out AsyncCallStackFrames frames)
        {
            var key = new FrameKey(processIndex, kind, methodIds, frameStates, methodIds.Length);
            if (_frameKeyToIndex.TryGetValue(key, out AsyncCallStackFramesIndex existing))
            {
                frames = _internedAsyncCallStackFrames[(int)existing];
                return existing;
            }

            var index = (AsyncCallStackFramesIndex)_internedAsyncCallStackFrames.Count;
            frames = new AsyncCallStackFrames(kind, methodIds, frameStates, processIndex);
            _internedAsyncCallStackFrames.Add(frames);
            _frameKeyToIndex[key] = index;
            OnFrameInterned?.Invoke(frames);
            return index;
        }

        private ThreadCallStacks GetOrCreate(AsyncThreadKey key)
        {
            if (!_threads.TryGetValue(key, out ThreadCallStacks callStacks))
            {
                callStacks = new ThreadCallStacks();
                _threads[key] = callStacks;
            }
            return callStacks;
        }

        void IFastSerializable.ToStream(Serializer serializer)
        {
            serializer.Write(SerializationVersion);

            serializer.Write(_internedAsyncCallStackFrames.Count);
            for (int i = 0; i < _internedAsyncCallStackFrames.Count; i++)
            {
                _internedAsyncCallStackFrames[i].Write(serializer);
            }

            serializer.Write(_threads.Count);
            foreach (KeyValuePair<AsyncThreadKey, ThreadCallStacks> pair in _threads)
            {
                serializer.Write((int)pair.Key.ProcessIndex);
                serializer.Write((long)pair.Key.OsThreadId);

                ThreadCallStacks recorded = pair.Value;
                serializer.Write(recorded.Count);
                for (int i = 0; i < recorded.Count; i++)
                {
                    recorded.Write(serializer, i);
                }
            }

            serializer.Write(_completionAvailability.Count);
            foreach (KeyValuePair<ProcessIndex, CompletionAvailability> pair in _completionAvailability)
            {
                serializer.Write((int)pair.Key);
                serializer.Write(pair.Value.Flags);
            }
        }

        void IFastSerializable.FromStream(Deserializer deserializer)
        {
            int version = deserializer.ReadInt();
            if (version != SerializationVersion)
            {
                throw new SerializationException("Unsupported AsyncCallStacksIndex serialization version " + version);
            }

            _internedAsyncCallStackFrames.Clear();
            _frameKeyToIndex.Clear();
            _threads.Clear();
            _completionAvailability.Clear();

            int frameCount = deserializer.ReadInt();
            for (int i = 0; i < frameCount; i++)
            {
                _internedAsyncCallStackFrames.Add(AsyncCallStackFrames.Read(deserializer));
            }

            int threadCount = deserializer.ReadInt();
            for (int t = 0; t < threadCount; t++)
            {
                ProcessIndex processIndex = (ProcessIndex)deserializer.ReadInt();
                ulong osThreadId = (ulong)deserializer.ReadInt64();
                int count = deserializer.ReadInt();
                var callStacks = new ThreadCallStacks(count);
                _threads[new AsyncThreadKey(processIndex, osThreadId)] = callStacks;
                for (int i = 0; i < count; i++)
                {
                    callStacks.ReadAndAdd(deserializer);
                }
            }

            int processCount = deserializer.ReadInt();
            for (int i = 0; i < processCount; i++)
            {
                ProcessIndex processIndex = (ProcessIndex)deserializer.ReadInt();
                _completionAvailability[processIndex] = new CompletionAvailability(deserializer.ReadByte());
            }
        }

        private AsyncCallStackFrames ResolveFrames(AsyncCallStackFramesIndex index) => _internedAsyncCallStackFrames[(int)index];

        private static int CompareDepth(AsyncCallStack left, AsyncCallStack right) =>
            left.Depth.CompareTo(right.Depth);

        private CompletionAvailability GetCompletionAvailability(ProcessIndex processIndex)
        {
            if (!_completionAvailability.TryGetValue(processIndex, out CompletionAvailability availability))
            {
                availability = new CompletionAvailability();
                _completionAvailability[processIndex] = availability;
            }
            return availability;
        }

        private sealed class CompletionAvailability
        {
            private const byte RuntimeMethod = 1 << 0;
            private const byte StateMachineMethod = 1 << 1;
            private const byte RuntimeException = 1 << 2;
            private const byte StateMachineException = 1 << 3;

            public CompletionAvailability()
            {
            }

            public CompletionAvailability(byte flags)
            {
                Flags = flags;
            }

            public byte Flags { get; private set; }

            public bool MethodObserved(AsyncCallstackKind kind) =>
                (Flags & (kind == AsyncCallstackKind.StateMachineAsync ? StateMachineMethod : RuntimeMethod)) != 0;

            public bool ExceptionObserved(AsyncCallstackKind kind) =>
                (Flags & (kind == AsyncCallstackKind.StateMachineAsync ? StateMachineException : RuntimeException)) != 0;

            public void MarkMethodObserved(AsyncCallstackKind kind)
            {
                Flags |= kind == AsyncCallstackKind.StateMachineAsync ? StateMachineMethod : RuntimeMethod;
            }

            public void MarkExceptionObserved(AsyncCallstackKind kind)
            {
                Flags |= kind == AsyncCallstackKind.StateMachineAsync ? StateMachineException : RuntimeException;
            }
        }

        private readonly struct AsyncCallStackRecord
        {
            public AsyncCallStackRecord(int depth, AsyncCallStackFramesIndex framesIndex, int timelinesIndex,
                byte continuationIndexBase, byte wrapperCount, long startQpc, long endQpc)
            {
                Depth = depth;
                FramesIndex = framesIndex;
                TimelinesIndex = timelinesIndex;
                ContinuationIndexBase = continuationIndexBase;
                WrapperCount = wrapperCount;
                StartQpc = startQpc;
                EndQpc = endQpc;
            }

            public readonly long StartQpc;
            public readonly long EndQpc;
            public readonly int Depth;
            public readonly AsyncCallStackFramesIndex FramesIndex;
            public readonly int TimelinesIndex;
            public readonly byte ContinuationIndexBase;
            public readonly byte WrapperCount;
        }

        private sealed class AsyncCallStackTimelines
        {
            public AsyncCallStackTimelines(AsyncCallStack.CompletionDelta[] methodCompletions,
                AsyncCallStack.CompletionDelta[] exceptionCompletions, long[] wrapperResets)
            {
                MethodCompletions = methodCompletions;
                ExceptionCompletions = exceptionCompletions;
                WrapperResets = wrapperResets;
            }

            public readonly AsyncCallStack.CompletionDelta[] MethodCompletions;
            public readonly AsyncCallStack.CompletionDelta[] ExceptionCompletions;
            public readonly long[] WrapperResets;
        }

        /// <summary>Per-thread compact async call stack records plus a lazily-built interval index.</summary>
        private sealed class ThreadCallStacks
        {
            private readonly AsyncCallStackRecordCollection _recorded;
            private List<AsyncCallStackTimelines> _timelines;
            private Dictionary<int, AsyncCallStack> _materialized;
            private AsyncCallStacksIntervalIndex _index;

            public ThreadCallStacks()
            {
                _recorded = new AsyncCallStackRecordCollection();
            }

            public ThreadCallStacks(int capacity)
            {
                _recorded = new AsyncCallStackRecordCollection(capacity);
            }

            public int Count => _recorded.Count;

            public void Add(AsyncCallStackFramesIndex framesIndex, int depth,
                byte continuationIndexBase, byte wrapperCount, long startQpc, long endQpc,
                AsyncCallStack.CompletionDelta[] methodCompletions,
                AsyncCallStack.CompletionDelta[] exceptionCompletions, long[] wrapperResets)
            {
                int timelinesIndex = AddTimelines(methodCompletions, exceptionCompletions, wrapperResets);
                _recorded.Add(new AsyncCallStackRecord(depth, framesIndex, timelinesIndex,
                    continuationIndexBase, wrapperCount, startQpc, endQpc));
                _index = null; // invalidate the cached query index
            }

            public void ReadAndAdd(Deserializer deserializer)
            {
                int depth = deserializer.ReadInt();
                var framesIndex = (AsyncCallStackFramesIndex)deserializer.ReadInt();
                byte continuationIndexBase = deserializer.ReadByte();
                byte wrapperCount = deserializer.ReadByte();
                long startQpc = deserializer.ReadInt64();
                long endQpc = deserializer.ReadInt64();

                AsyncCallStack.CompletionDelta[] methodCompletions = ReadCompletions(deserializer);
                AsyncCallStack.CompletionDelta[] exceptionCompletions = ReadCompletions(deserializer);
                long[] wrapperResets = ReadQpcs(deserializer);
                Add(framesIndex, depth, continuationIndexBase, wrapperCount, startQpc, endQpc,
                    methodCompletions, exceptionCompletions, wrapperResets);
            }

            public void Write(Serializer serializer, int index)
            {
                _recorded.Freeze();
                AsyncCallStackRecord record = _recorded[index];
                GetTimelines(record.TimelinesIndex, out AsyncCallStack.CompletionDelta[] methodCompletions,
                    out AsyncCallStack.CompletionDelta[] exceptionCompletions, out long[] wrapperResets);
                serializer.Write(record.Depth);
                serializer.Write((int)record.FramesIndex);
                serializer.Write(record.ContinuationIndexBase);
                serializer.Write(record.WrapperCount);
                serializer.Write(record.StartQpc);
                serializer.Write(record.EndQpc);
                Write(serializer, methodCompletions);
                Write(serializer, exceptionCompletions);
                Write(serializer, wrapperResets);
            }

            public AsyncCallStack Materialize(int index, AsyncCallStackFrames resolveFrames, AsyncCallStack reusable = null)
            {
                AsyncCallStackRecord record = _recorded[index];
                GetTimelines(record.TimelinesIndex, out AsyncCallStack.CompletionDelta[] methodCompletions,
                    out AsyncCallStack.CompletionDelta[] exceptionCompletions, out long[] wrapperResets);
                if (reusable == null)
                {
                    return new AsyncCallStack(record.Depth, record.FramesIndex, resolveFrames,
                        record.ContinuationIndexBase, record.WrapperCount, record.StartQpc, record.EndQpc,
                        methodCompletions, exceptionCompletions, wrapperResets);
                }
                reusable.Reset(record.Depth, record.FramesIndex, resolveFrames,
                    record.ContinuationIndexBase, record.WrapperCount, record.StartQpc, record.EndQpc,
                    methodCompletions, exceptionCompletions, wrapperResets);
                return reusable;
            }

            public AsyncCallStackFramesIndex FramesIndexAt(int index) => _recorded[index].FramesIndex;

            public void Query(long qpc, List<AsyncCallStack> result,
                AsyncCallStacksIndex owner, bool cacheMaterialized)
            {
                int count = 0;
                QueryIndex().Stab(qpc, this, result, owner, cacheMaterialized, ref count);
                if (result.Count > count)
                {
                    result.RemoveRange(count, result.Count - count);
                }
            }

            public AsyncCallStack GetOrCreateMaterialized(int index, AsyncCallStackFrames frames)
            {
                if (_materialized == null)
                {
                    _materialized = new Dictionary<int, AsyncCallStack>();
                }
                if (!_materialized.TryGetValue(index, out AsyncCallStack callStack))
                {
                    callStack = Materialize(index, frames);
                    _materialized[index] = callStack;
                }
                return callStack;
            }

            public AsyncCallStacksIntervalIndex QueryIndex() =>
                _index ?? (_index = new AsyncCallStacksIntervalIndex(_recorded));

            private int AddTimelines(AsyncCallStack.CompletionDelta[] methodCompletions,
                AsyncCallStack.CompletionDelta[] exceptionCompletions, long[] wrapperResets)
            {
                if (methodCompletions.Length == 0 && exceptionCompletions.Length == 0 && wrapperResets.Length == 0)
                {
                    return -1;
                }

                if (_timelines == null)
                {
                    _timelines = new List<AsyncCallStackTimelines>();
                }
                int index = _timelines.Count;
                _timelines.Add(new AsyncCallStackTimelines(methodCompletions, exceptionCompletions, wrapperResets));
                return index;
            }

            private void GetTimelines(int index, out AsyncCallStack.CompletionDelta[] methodCompletions,
                out AsyncCallStack.CompletionDelta[] exceptionCompletions, out long[] wrapperResets)
            {
                if (index < 0)
                {
                    methodCompletions = Array.Empty<AsyncCallStack.CompletionDelta>();
                    exceptionCompletions = Array.Empty<AsyncCallStack.CompletionDelta>();
                    wrapperResets = Array.Empty<long>();
                    return;
                }
                AsyncCallStackTimelines timelines = _timelines[index];
                methodCompletions = timelines.MethodCompletions;
                exceptionCompletions = timelines.ExceptionCompletions;
                wrapperResets = timelines.WrapperResets;
            }

            private static AsyncCallStack.CompletionDelta[] ReadCompletions(Deserializer deserializer)
            {
                int count = deserializer.ReadInt();
                if (count == 0)
                {
                    return Array.Empty<AsyncCallStack.CompletionDelta>();
                }
                var result = new AsyncCallStack.CompletionDelta[count];
                for (int i = 0; i < count; i++)
                {
                    result[i] = new AsyncCallStack.CompletionDelta(deserializer.ReadInt64(), deserializer.ReadInt());
                }
                return result;
            }

            private static long[] ReadQpcs(Deserializer deserializer)
            {
                int count = deserializer.ReadInt();
                if (count == 0)
                {
                    return Array.Empty<long>();
                }
                var result = new long[count];
                for (int i = 0; i < count; i++)
                {
                    result[i] = deserializer.ReadInt64();
                }
                return result;
            }

            private static void Write(Serializer serializer, AsyncCallStack.CompletionDelta[] values)
            {
                serializer.Write(values.Length);
                for (int i = 0; i < values.Length; i++)
                {
                    serializer.Write(values[i].Qpc);
                    serializer.Write(values[i].Delta);
                }
            }

            private static void Write(Serializer serializer, long[] values)
            {
                serializer.Write(values.Length);
                for (int i = 0; i < values.Length; i++)
                {
                    serializer.Write(values[i]);
                }
            }
        }

        private sealed class AsyncCallStackRecordCollection
        {
            private const int ChunkShift = 12;
            private const int ChunkSize = 1 << ChunkShift;
            private const int ChunkMask = ChunkSize - 1;

            private AsyncCallStackRecord[] _items;
            private List<AsyncCallStackRecord[]> _chunks;
            private int _count;

            public AsyncCallStackRecordCollection()
            {
                _chunks = new List<AsyncCallStackRecord[]>();
            }

            public AsyncCallStackRecordCollection(int capacity)
            {
                _items = new AsyncCallStackRecord[capacity];
            }

            public int Count => _count;

            public AsyncCallStackRecord this[int index]
            {
                get
                {
                    if ((uint)index >= (uint)_count)
                    {
                        throw new ArgumentOutOfRangeException(nameof(index));
                    }
                    return _items != null
                        ? _items[index]
                        : _chunks[index >> ChunkShift][index & ChunkMask];
                }
            }

            public void Add(AsyncCallStackRecord record)
            {
                if (_items != null)
                {
                    _items[_count++] = record;
                    return;
                }

                int chunkIndex = _count >> ChunkShift;
                if (chunkIndex == _chunks.Count)
                {
                    _chunks.Add(new AsyncCallStackRecord[ChunkSize]);
                }
                _chunks[chunkIndex][_count & ChunkMask] = record;
                _count++;
            }

            public void Freeze()
            {
                if (_items != null)
                {
                    return;
                }

                _items = new AsyncCallStackRecord[_count];
                int destination = 0;
                for (int i = 0; i < _chunks.Count; i++)
                {
                    int count = Math.Min(ChunkSize, _count - destination);
                    Array.Copy(_chunks[i], 0, _items, destination, count);
                    destination += count;
                }
                _chunks = null;
            }
        }

        /// <summary>
        /// An augmented interval tree over a thread's async call stacks (an implicit balanced BST over the
        /// start-sorted array, each node carrying the max end of its subtree) supporting O(log n + k)
        /// stabbing queries.
        /// </summary>
        private sealed class AsyncCallStacksIntervalIndex
        {
            private readonly int[] _byStart;            // indexes into the compact records, ascending StartQpc
            private readonly long[] _maxEnd;            // _maxEnd[i] = max EndQpc over the subtree rooted at position i
            private readonly AsyncCallStackRecordCollection _records;

            public AsyncCallStacksIntervalIndex(AsyncCallStackRecordCollection callStacks)
            {
                _records = callStacks;
                _byStart = new int[callStacks.Count];
                for (int i = 0; i < _byStart.Length; i++)
                {
                    _byStart[i] = i;
                }
                Array.Sort(_byStart, (a, b) => callStacks[a].StartQpc.CompareTo(callStacks[b].StartQpc));
                _maxEnd = new long[_byStart.Length];
                Build(0, _byStart.Length - 1);
            }

            private long Build(int lo, int hi)
            {
                if (lo > hi)
                {
                    return long.MinValue;
                }
                int mid = (lo + hi) >> 1;
                long left = Build(lo, mid - 1);
                long right = Build(mid + 1, hi);
                long max = _records[_byStart[mid]].EndQpc;
                if (left > max) max = left;
                if (right > max) max = right;
                _maxEnd[mid] = max;
                return max;
            }

            public void Stab(long qpc, ThreadCallStacks callStacks, List<AsyncCallStack> result,
                AsyncCallStacksIndex owner,
                bool cacheMaterialized, ref int count) =>
                Stab(0, _byStart.Length - 1, qpc, callStacks, result, owner, cacheMaterialized, ref count);

            private void Stab(int lo, int hi, long qpc, ThreadCallStacks callStacks,
                List<AsyncCallStack> result, AsyncCallStacksIndex owner,
                bool cacheMaterialized, ref int count)
            {
                if (lo > hi)
                {
                    return;
                }
                int mid = (lo + hi) >> 1;
                if (_maxEnd[mid] <= qpc)
                {
                    return; // nothing in this subtree ends after qpc
                }

                Stab(lo, mid - 1, qpc, callStacks, result, owner, cacheMaterialized, ref count);

                int recordIndex = _byStart[mid];
                AsyncCallStackRecord a = _records[recordIndex];
                if (a.StartQpc <= qpc)
                {
                    if (a.EndQpc > qpc)
                    {
                        AsyncCallStack materialized;
                        if (cacheMaterialized)
                        {
                            materialized = callStacks.GetOrCreateMaterialized(
                                recordIndex, owner.ResolveFrames(a.FramesIndex));
                            result.Add(materialized);
                        }
                        else
                        {
                            AsyncCallStack reusable = count < result.Count ? result[count] : null;
                            materialized = callStacks.Materialize(
                                recordIndex, owner.ResolveFrames(a.FramesIndex), reusable);
                            if (reusable == null)
                            {
                                result.Add(materialized);
                            }
                        }
                        count++;
                    }
                    Stab(mid + 1, hi, qpc, callStacks, result, owner, cacheMaterialized, ref count);
                }
                // else: right subtree all start after qpc, prune it.
            }

        }

        private sealed class ThreadCallStackList : IReadOnlyList<AsyncCallStack>
        {
            private readonly AsyncCallStacksIndex _owner;
            private readonly ThreadCallStacks _callStacks;

            public ThreadCallStackList(AsyncCallStacksIndex owner, ThreadCallStacks callStacks)
            {
                _owner = owner;
                _callStacks = callStacks;
            }

            public int Count => _callStacks.Count;

            public AsyncCallStack this[int index]
            {
                get
                {
                    return _callStacks.GetOrCreateMaterialized(
                        index, _owner.ResolveFrames(_callStacks.FramesIndexAt(index)));
                }
            }

            public IEnumerator<AsyncCallStack> GetEnumerator()
            {
                for (int i = 0; i < Count; i++)
                {
                    yield return this[i];
                }
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private readonly struct FrameKey : IEquatable<FrameKey>
        {
            private readonly ProcessIndex _processIndex;
            private readonly AsyncCallstackKind _kind;
            private readonly ulong[] _methodIds;
            private readonly int[] _frameStates;
            private readonly int _frameCount;
            private readonly int _hash;

            public FrameKey(ProcessIndex processIndex, AsyncCallstackKind kind, ulong[] methodIds, int[] frameStates, int frameCount)
            {
                _processIndex = processIndex;
                _kind = kind;
                _methodIds = methodIds;
                _frameStates = frameStates;
                _frameCount = frameCount;

                int hash = ((int)processIndex * 31) + (int)kind;
                unchecked
                {
                    for (int i = 0; i < frameCount; i++)
                    {
                        hash = (hash * 31) + methodIds[i].GetHashCode();
                    }
                    if (frameStates != null)
                    {
                        for (int i = 0; i < frameCount; i++)
                        {
                            hash = (hash * 31) + frameStates[i];
                        }
                    }
                }
                _hash = hash;
            }

            public bool Equals(FrameKey other)
            {
                if (_processIndex != other._processIndex || _kind != other._kind || _hash != other._hash || _frameCount != other._frameCount)
                {
                    return false;
                }
                for (int i = 0; i < _frameCount; i++)
                {
                    if (_methodIds[i] != other._methodIds[i])
                    {
                        return false;
                    }
                }
                if ((_frameStates == null) != (other._frameStates == null))
                {
                    return false;
                }
                if (_frameStates != null)
                {
                    for (int i = 0; i < _frameCount; i++)
                    {
                        if (_frameStates[i] != other._frameStates[i])
                        {
                            return false;
                        }
                    }
                }
                return true;
            }

            public override bool Equals(object obj) => obj is FrameKey o && Equals(o);
            public override int GetHashCode() => _hash;
        }
    }
}
