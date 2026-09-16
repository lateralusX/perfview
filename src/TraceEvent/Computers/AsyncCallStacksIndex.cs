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
            GetAsyncCallStacks(thread, qpc, result);
            return result;
        }

        internal void GetAsyncCallStacks(AsyncThreadKey thread, long qpc, List<AsyncCallStack> result)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            result.Clear();
            if (_threads.TryGetValue(thread, out ThreadCallStacks callStacks))
            {
                callStacks.QueryIndex().Stab(qpc, result);
                result.Sort(CompareDepth);
            }
        }

        /// <summary>All recorded async call stacks for a thread, in the order they closed.</summary>
        public IReadOnlyList<AsyncCallStack> GetAsyncCallStacks(AsyncThreadKey thread) =>
            _threads.TryGetValue(thread, out ThreadCallStacks c) ? c.Recorded : Array.Empty<AsyncCallStack>();

        /// <summary>
        /// Records a finalized async call stack, interning its frames. Returns the created
        /// <see cref="AsyncCallStack"/>. Called by <see cref="AsyncProfilerComputer"/> as async call stacks close.
        /// </summary>
        internal AsyncCallStack Add(AsyncThreadKey thread, AsyncCallstackKind kind, ulong[] methodIds, int[] frameStates,
            int depth, byte continuationIndexBase, byte wrapperCount, long startQpc, long endQpc,
            AsyncCallStack.CompletionDelta[] methodCompletions, AsyncCallStack.CompletionDelta[] exceptionCompletions, long[] wrapperResets)
        {
            AsyncCallStackFramesIndex framesIndex = Intern(kind, methodIds, frameStates, thread.ProcessIndex, out AsyncCallStackFrames frames);
            var callStack = new AsyncCallStack(depth, framesIndex, frames, continuationIndexBase, wrapperCount, startQpc, endQpc, methodCompletions, exceptionCompletions, wrapperResets);
            GetOrCreate(thread).Add(callStack);
            return callStack;
        }

        private AsyncCallStackFramesIndex Intern(AsyncCallstackKind kind, ulong[] methodIds, int[] frameStates, ProcessIndex processIndex, out AsyncCallStackFrames frames)
        {
            var key = new FrameKey(processIndex, kind, methodIds, frameStates);
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

                List<AsyncCallStack> recorded = pair.Value.Recorded;
                serializer.Write(recorded.Count);
                for (int i = 0; i < recorded.Count; i++)
                {
                    recorded[i].Write(serializer);
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
                ThreadCallStacks callStacks = GetOrCreate(new AsyncThreadKey(processIndex, osThreadId));

                int count = deserializer.ReadInt();
                for (int i = 0; i < count; i++)
                {
                    callStacks.Add(AsyncCallStack.Read(deserializer, ResolveFrames));
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

        /// <summary>Per-thread recorded async call stacks plus a lazily-built interval index for stabbing queries.</summary>
        private sealed class ThreadCallStacks
        {
            public readonly List<AsyncCallStack> Recorded = new List<AsyncCallStack>();
            private AsyncCallStacksIntervalIndex _index;

            public void Add(AsyncCallStack callStack)
            {
                Recorded.Add(callStack);
                _index = null; // invalidate the cached query index
            }

            public AsyncCallStacksIntervalIndex QueryIndex() => _index ?? (_index = new AsyncCallStacksIntervalIndex(Recorded));
        }

        /// <summary>
        /// An augmented interval tree over a thread's async call stacks (an implicit balanced BST over the
        /// start-sorted array, each node carrying the max end of its subtree) supporting O(log n + k)
        /// stabbing queries.
        /// </summary>
        private sealed class AsyncCallStacksIntervalIndex
        {
            private readonly AsyncCallStack[] _byStart; // ascending StartQpc
            private readonly long[] _maxEnd;            // _maxEnd[i] = max EndQpc over the subtree rooted at position i

            public AsyncCallStacksIntervalIndex(List<AsyncCallStack> callStacks)
            {
                _byStart = callStacks.ToArray();
                Array.Sort(_byStart, (a, b) => a.StartQpc.CompareTo(b.StartQpc));
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
                long max = _byStart[mid].EndQpc;
                if (left > max) max = left;
                if (right > max) max = right;
                _maxEnd[mid] = max;
                return max;
            }

            public void Stab(long qpc, List<AsyncCallStack> result) => Stab(0, _byStart.Length - 1, qpc, result);

            private void Stab(int lo, int hi, long qpc, List<AsyncCallStack> result)
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

                Stab(lo, mid - 1, qpc, result); // left subtree may contain covering async call stacks

                AsyncCallStack a = _byStart[mid];
                if (a.StartQpc <= qpc)
                {
                    if (a.EndQpc > qpc)
                    {
                        result.Add(a);
                    }
                    Stab(mid + 1, hi, qpc, result); // right subtree still may start <= qpc
                }
                // else: right subtree all start after qpc, prune it.
            }
        }

        private readonly struct FrameKey : IEquatable<FrameKey>
        {
            private readonly ProcessIndex _processIndex;
            private readonly AsyncCallstackKind _kind;
            private readonly ulong[] _methodIds;
            private readonly int[] _frameStates;
            private readonly int _hash;

            public FrameKey(ProcessIndex processIndex, AsyncCallstackKind kind, ulong[] methodIds, int[] frameStates)
            {
                _processIndex = processIndex;
                _kind = kind;
                _methodIds = methodIds;
                _frameStates = frameStates;

                int hash = ((int)processIndex * 31) + (int)kind;
                unchecked
                {
                    for (int i = 0; i < methodIds.Length; i++)
                    {
                        hash = (hash * 31) + methodIds[i].GetHashCode();
                    }
                    if (frameStates != null)
                    {
                        for (int i = 0; i < frameStates.Length; i++)
                        {
                            hash = (hash * 31) + frameStates[i];
                        }
                    }
                }
                _hash = hash;
            }

            public bool Equals(FrameKey other)
            {
                if (_processIndex != other._processIndex || _kind != other._kind || _hash != other._hash || _methodIds.Length != other._methodIds.Length)
                {
                    return false;
                }
                for (int i = 0; i < _methodIds.Length; i++)
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
                    if (_frameStates.Length != other._frameStates.Length)
                    {
                        return false;
                    }
                    for (int i = 0; i < _frameStates.Length; i++)
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
