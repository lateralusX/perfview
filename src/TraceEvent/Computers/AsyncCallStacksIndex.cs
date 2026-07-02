// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

using FastSerialization;

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

        private readonly List<AsyncCallStackFrames> _frames = new List<AsyncCallStackFrames>();
        private readonly Dictionary<FrameKey, AsyncCallStackFramesIndex> _framesIntern = new Dictionary<FrameKey, AsyncCallStackFramesIndex>();
        private readonly Dictionary<AsyncThreadKey, ThreadCallStacks> _threads = new Dictionary<AsyncThreadKey, ThreadCallStacks>();

        /// <summary>The number of distinct interned frame lists.</summary>
        public int DistinctFramesCount => _frames.Count;

        /// <summary>True if no async call stacks were recorded (used to avoid persisting an empty index).</summary>
        public bool IsEmpty => _threads.Count == 0;

        /// <summary>The threads that have at least one recorded async call stack.</summary>
        public IEnumerable<AsyncThreadKey> Threads => _threads.Keys;

        /// <summary>Resolves an interned frames handle to its frames (null if out of range).</summary>
        public AsyncCallStackFrames GetFrames(AsyncCallStackFramesIndex index)
        {
            int i = (int)index;
            return (uint)i < (uint)_frames.Count ? _frames[i] : null;
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
                callStacks.QueryIndex().Stab(qpc, result);
                result.Sort((a, b) => a.Depth.CompareTo(b.Depth));
            }
            return result;
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
            AsyncCallStack.CompletionDelta[] completions, long[] wrapperResets)
        {
            AsyncCallStackFramesIndex framesIndex = Intern(kind, methodIds, frameStates, out AsyncCallStackFrames frames);
            var callStack = new AsyncCallStack(depth, framesIndex, frames, continuationIndexBase, wrapperCount, startQpc, endQpc, completions, wrapperResets);
            GetOrCreate(thread).Add(callStack);
            return callStack;
        }

        private AsyncCallStackFramesIndex Intern(AsyncCallstackKind kind, ulong[] methodIds, int[] frameStates, out AsyncCallStackFrames frames)
        {
            var key = new FrameKey(kind, methodIds, frameStates);
            if (_framesIntern.TryGetValue(key, out AsyncCallStackFramesIndex existing))
            {
                frames = _frames[(int)existing];
                return existing;
            }

            var index = (AsyncCallStackFramesIndex)_frames.Count;
            frames = new AsyncCallStackFrames(kind, methodIds, frameStates);
            _frames.Add(frames);
            _framesIntern[key] = index;
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

            serializer.Write(_frames.Count);
            for (int i = 0; i < _frames.Count; i++)
            {
                _frames[i].Write(serializer);
            }

            serializer.Write(_threads.Count);
            foreach (KeyValuePair<AsyncThreadKey, ThreadCallStacks> pair in _threads)
            {
                serializer.Write(pair.Key.ProcessId);
                serializer.Write((long)pair.Key.OsThreadId);

                List<AsyncCallStack> recorded = pair.Value.Recorded;
                serializer.Write(recorded.Count);
                for (int i = 0; i < recorded.Count; i++)
                {
                    recorded[i].Write(serializer);
                }
            }
        }

        void IFastSerializable.FromStream(Deserializer deserializer)
        {
            int version = deserializer.ReadInt();
            if (version != SerializationVersion)
            {
                throw new SerializationException("Unsupported AsyncCallStacksIndex serialization version " + version);
            }

            _frames.Clear();
            _framesIntern.Clear();
            _threads.Clear();

            int frameCount = deserializer.ReadInt();
            for (int i = 0; i < frameCount; i++)
            {
                _frames.Add(AsyncCallStackFrames.Read(deserializer));
            }

            int threadCount = deserializer.ReadInt();
            for (int t = 0; t < threadCount; t++)
            {
                int processId = deserializer.ReadInt();
                ulong osThreadId = (ulong)deserializer.ReadInt64();
                ThreadCallStacks callStacks = GetOrCreate(new AsyncThreadKey(processId, osThreadId));

                int count = deserializer.ReadInt();
                for (int i = 0; i < count; i++)
                {
                    callStacks.Add(AsyncCallStack.Read(deserializer, ResolveFrames));
                }
            }
        }

        private AsyncCallStackFrames ResolveFrames(AsyncCallStackFramesIndex index) => _frames[(int)index];

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
            private readonly AsyncCallstackKind _kind;
            private readonly ulong[] _methodIds;
            private readonly int[] _frameStates;
            private readonly int _hash;

            public FrameKey(AsyncCallstackKind kind, ulong[] methodIds, int[] frameStates)
            {
                _kind = kind;
                _methodIds = methodIds;
                _frameStates = frameStates;

                int hash = (int)kind;
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
                if (_kind != other._kind || _hash != other._hash || _methodIds.Length != other._methodIds.Length)
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
