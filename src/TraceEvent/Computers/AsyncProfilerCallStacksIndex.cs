// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Threading;

using FastSerialization;

using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>Recorded dispatcher metadata, including its parent and the thread and time of its creation.</summary>
    internal sealed class AsyncProfilerDispatcherInfo
    {
        internal AsyncProfilerDispatcherInfo(
            AsyncProfilerThreadKey creationThread,
            ulong dispatcherId,
            ulong parentDispatcherId,
            long creationQpc)
        {
            CreationThread = creationThread;
            DispatcherId = dispatcherId;
            ParentDispatcherId = parentDispatcherId;
            CreationQpc = creationQpc;
        }

        /// <summary>The thread on which this dispatcher was created.</summary>
        public AsyncProfilerThreadKey CreationThread { get; }

        /// <summary>The recorded dispatcher's identifier.</summary>
        public ulong DispatcherId { get; }

        /// <summary>The active parent dispatcher, or 0 when the create event recorded no parent.</summary>
        public ulong ParentDispatcherId { get; }

        /// <summary>The creation timestamp in the trace QPC domain.</summary>
        public long CreationQpc { get; }
    }

    /// <summary>
    /// The recorded, queryable, serializable result of <see cref="AsyncProfilerIndexBuilder"/>: an index over all
    /// the per-thread <see cref="AsyncProfilerCallStack"/> runs, plus a deduplicated table of
    /// <see cref="AsyncProfilerCallStackFrames"/>.
    /// <para>
    /// Async call stacks on a thread may overlap (they nest), so a point query returns the ordered nested
    /// stack. Times are in the trace's QPC domain (the async buffer's internal QPC matches the containing
    /// ETW/EventPipe file's timestamps, so no conversion is needed). This type is
    /// <see cref="IFastSerializable"/> so it can be persisted into (and lazily loaded from) an ETLX/TraceLog
    /// cache; the per-thread interval trees used for querying are rebuilt on demand and are not serialized.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Activations returned by public queries are stable objects, not the reusable activation slots used by
    /// internal stack-generation queries. Frame and code-address handles are scoped to this index and its
    /// originating TraceLog. Retaining public results can keep their backing index data alive after
    /// <see cref="TraceLog.ReleaseAsyncProfilerCallStacks"/> releases the TraceLog-owned reference.
    /// </remarks>
    internal sealed class AsyncProfilerCallStacksIndex : IFastSerializable
    {
        private const int SerializationVersion = 1;

        private readonly List<AsyncProfilerCallStackFrames> _internedCallStackFrames = new List<AsyncProfilerCallStackFrames>();
        private readonly Dictionary<FrameKey, AsyncProfilerCallStackFramesIndex> _frameKeyToIndex = new Dictionary<FrameKey, AsyncProfilerCallStackFramesIndex>();
        private readonly Dictionary<AsyncProfilerThreadKey, ThreadCallStacks> _threads = new Dictionary<AsyncProfilerThreadKey, ThreadCallStacks>();
        private readonly List<DispatcherRecord> _dispatchers =
            new List<DispatcherRecord>();
        private readonly object _dispatcherIndexLock = new object();
        private Dictionary<DispatcherKey, List<int>> _dispatchersById;
        private Dictionary<DispatcherKey, List<int>> _dispatchersByParentId;

        private readonly Dictionary<ProcessIndex, List<CompletionAvailabilityEpoch>> _completionAvailability =
            new Dictionary<ProcessIndex, List<CompletionAvailabilityEpoch>>();

        /// <summary>
        /// Invoked (if set) the first time a distinct <see cref="AsyncProfilerCallStackFrames"/> is interned, so a build-time
        /// consumer (<see cref="TraceLog"/>) can begin symbolizing its frames as they are discovered. Not used after load.
        /// </summary>
        internal Action<AsyncProfilerCallStackFrames> OnFrameInterned { get; set; }

        /// <summary>The number of distinct interned frame lists.</summary>
        public int DistinctFramesCount => _internedCallStackFrames.Count;

        /// <summary>True if no async call stacks were recorded (used to avoid persisting an empty index).</summary>
        public bool IsEmpty => _threads.Count == 0;

        internal bool HasDispatcherRecords => _dispatchers.Count != 0;

        /// <summary>The threads that have at least one recorded async call stack.</summary>
        public IEnumerable<AsyncProfilerThreadKey> Threads => _threads.Keys;

        /// <summary>
        /// True if a <c>CompleteMethod</c> event for <paramref name="kind"/> was observed in any configuration
        /// epoch of the default process instance (index 0). Use <see cref="MethodCompletionObserved(AsyncProfilerCallStack)"/>
        /// to determine the policy for a particular activation.
        /// </summary>
        public bool MethodCompletionObserved(AsyncCallstackKind kind) => MethodCompletionObserved(0, kind);

        /// <summary>
        /// True if a <c>CompleteMethod</c> event for <paramref name="kind"/> was observed in any configuration
        /// epoch of <paramref name="processIndex"/>. This is an observation, not proof that a keyword was enabled
        /// or that every completion was captured. The stitcher uses the activation's own epoch to choose between
        /// recorded normal-completion counts and the V2 wrapper or V1 physical-frame fallback.
        /// </summary>
        public bool MethodCompletionObserved(ProcessIndex processIndex, AsyncCallstackKind kind)
        {
            if (!_completionAvailability.TryGetValue(processIndex, out List<CompletionAvailabilityEpoch> epochs))
            {
                return false;
            }

            for (int i = 0; i < epochs.Count; i++)
            {
                if (epochs[i].MethodObserved(kind))
                {
                    return true;
                }
            }
            return false;
        }

        internal bool MethodCompletionObserved(ProcessIndex processIndex, AsyncCallstackKind kind, long activationStartQpc)
        {
            CompletionAvailabilityEpoch epoch = FindCompletionEpoch(processIndex, activationStartQpc);
            return epoch != null && epoch.MethodObserved(kind);
        }

        /// <summary>
        /// True if an <c>Unwind</c> event for <paramref name="kind"/> was observed in any configuration epoch
        /// of the default process instance (index 0).
        /// </summary>
        public bool ExceptionCompletionObserved(AsyncCallstackKind kind) => ExceptionCompletionObserved(0, kind);

        /// <summary>
        /// True if an <c>Unwind</c> event for <paramref name="kind"/> was observed in any configuration epoch
        /// of <paramref name="processIndex"/>. V1 needs these deltas because unwound frames leave the physical
        /// stack. V2 wrapper-based counting already includes unwinds; do not add exception deltas to that fallback.
        /// </summary>
        public bool ExceptionCompletionObserved(ProcessIndex processIndex, AsyncCallstackKind kind)
        {
            if (!_completionAvailability.TryGetValue(processIndex, out List<CompletionAvailabilityEpoch> epochs))
            {
                return false;
            }

            for (int i = 0; i < epochs.Count; i++)
            {
                if (epochs[i].ExceptionObserved(kind))
                {
                    return true;
                }
            }
            return false;
        }

        internal void StartCompletionAvailabilityEpoch(ProcessIndex processIndex, long startQpc)
        {
            List<CompletionAvailabilityEpoch> epochs = GetCompletionEpochs(processIndex);
            int index = FindCompletionEpochIndex(epochs, startQpc);
            if (index >= 0 && epochs[index].StartQpc == startQpc)
            {
                return;
            }
            epochs.Insert(index + 1, new CompletionAvailabilityEpoch(startQpc));
        }

        /// <summary>
        /// Records that a <c>CompleteMethod</c> (normal completion) event of the given <paramref name="kind"/> was
        /// seen in the current metadata/configuration epoch. Called by <see cref="AsyncProfilerIndexBuilder"/> as it
        /// processes events. The stitcher selects the epoch containing an activation's start QPC, so observations
        /// from another keyword configuration cannot change that activation's completion-count policy.
        /// </summary>
        internal void MarkMethodCompletionObserved(ProcessIndex processIndex, AsyncCallstackKind kind, long timestampQpc)
        {
            FindCompletionEpoch(processIndex, timestampQpc)?.MarkMethodObserved(kind);
        }

        /// <summary>
        /// Records that an <c>Unwind</c> (exceptional completion) event of the given <paramref name="kind"/> was seen
        /// in the stream. Called by <see cref="AsyncProfilerIndexBuilder"/> as it processes events. This uses the same
        /// metadata/configuration epoch scoping as normal method-completion observation.
        /// </summary>
        internal void MarkExceptionCompletionObserved(ProcessIndex processIndex, AsyncCallstackKind kind, long timestampQpc)
        {
            FindCompletionEpoch(processIndex, timestampQpc)?.MarkExceptionObserved(kind);
        }

        /// <summary>Resolves an interned frames handle to its frames (null if out of range).</summary>
        public AsyncProfilerCallStackFrames GetFrames(AsyncProfilerCallStackFramesIndex index)
        {
            int i = (int)index;
            return (uint)i < (uint)_internedCallStackFrames.Count ? _internedCallStackFrames[i] : null;
        }

        /// <summary>
        /// Returns the nested async call stacks active on <paramref name="thread"/> at <paramref name="timestampQpc"/>,
        /// ordered bottom-to-top (ascending depth). Intervals are half-open: <c>StartQpc &lt;= timestampQpc &lt; EndQpc</c>.
        /// Empty if the thread has no recorded async call stack covering that instant.
        /// </summary>
        /// <remarks>The returned list and activation objects are not overwritten by subsequent queries.</remarks>
        public IReadOnlyList<AsyncProfilerCallStack> GetCallStacks(AsyncProfilerThreadKey thread, long timestampQpc)
        {
            var result = new List<AsyncProfilerCallStack>();
            if (_threads.TryGetValue(thread, out ThreadCallStacks callStacks))
            {
                callStacks.Query(timestampQpc, result, this, cacheMaterialized: true);
                result.Sort(CompareDepth);
            }
            return result;
        }

        /// <summary>
        /// Returns the innermost async call stack for <paramref name="dispatcherId"/> that was running on
        /// <paramref name="thread"/> at <paramref name="timestampQpc"/>, or null when no matching resume interval covers the
        /// timestamp.
        /// </summary>
        public AsyncProfilerCallStack GetCallStack(
            AsyncProfilerThreadKey thread,
            ulong dispatcherId,
            long timestampQpc)
        {
            IReadOnlyList<AsyncProfilerCallStack> callStacks = GetCallStacks(thread, timestampQpc);
            for (int i = callStacks.Count - 1; i >= 0; i--)
            {
                if (callStacks[i].DispatcherId == dispatcherId)
                {
                    return callStacks[i];
                }
            }
            return null;
        }

        /// <summary>
        /// True when a normal <c>CompleteMethod</c> event was observed for <paramref name="callStack"/>'s process,
        /// instrumentation kind, and configuration epoch. This selects the stitcher's event-counting policy;
        /// it does not prove every completion was captured.
        /// </summary>
        public bool MethodCompletionObserved(AsyncProfilerCallStack callStack)
        {
            if (callStack == null)
            {
                throw new ArgumentNullException(nameof(callStack));
            }
            return MethodCompletionObserved(
                callStack.Thread.ProcessIndex,
                callStack.Frames.Kind,
                callStack.StartQpc);
        }

        /// <summary>
        /// True when an exceptional <c>Unwind</c> event was observed for this activation's process,
        /// instrumentation kind, and configuration epoch. This is not proof every unwind was captured.
        /// </summary>
        public bool ExceptionCompletionObserved(AsyncProfilerCallStack callStack)
        {
            if (callStack == null)
            {
                throw new ArgumentNullException(nameof(callStack));
            }
            CompletionAvailabilityEpoch epoch = FindCompletionEpoch(
                callStack.Thread.ProcessIndex, callStack.StartQpc);
            return epoch != null && epoch.ExceptionObserved(callStack.Frames.Kind);
        }

        /// <summary>
        /// Returns the latest recorded metadata for <paramref name="dispatcherId"/> with a creation QPC at or before
        /// <paramref name="timestampQpc"/> in <paramref name="processIndex"/>, or null when no such metadata was recorded.
        /// </summary>
        public AsyncProfilerDispatcherInfo GetDispatcherInfo(
            ProcessIndex processIndex,
            ulong dispatcherId,
            long timestampQpc)
        {
            int dispatcherIndex = GetDispatcherIndex(processIndex, dispatcherId, timestampQpc);
            return dispatcherIndex < 0 ? null : MaterializeDispatcherInfo(_dispatchers[dispatcherIndex]);
        }

        /// <summary>Returns the dispatcher metadata associated with <paramref name="callStack"/>, if captured.</summary>
        public AsyncProfilerDispatcherInfo GetDispatcherInfo(AsyncProfilerCallStack callStack)
        {
            if (callStack == null)
            {
                throw new ArgumentNullException(nameof(callStack));
            }
            return GetDispatcherInfo(
                callStack.Thread.ProcessIndex,
                callStack.DispatcherId,
                callStack.StartQpc);
        }

        /// <summary>Returns metadata for all recorded children of a parent dispatcher, ordered by creation QPC.</summary>
        public IReadOnlyList<AsyncProfilerDispatcherInfo> GetChildDispatcherInfos(
            ProcessIndex processIndex,
            ulong parentDispatcherId)
        {
            EnsureDispatcherIndexes();
            var key = new DispatcherKey(processIndex, parentDispatcherId);
            if (!_dispatchersByParentId.TryGetValue(key, out List<int> dispatcherIndices))
            {
                return Array.Empty<AsyncProfilerDispatcherInfo>();
            }

            var result = new AsyncProfilerDispatcherInfo[dispatcherIndices.Count];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = MaterializeDispatcherInfo(_dispatchers[dispatcherIndices[i]]);
            }
            return result;
        }

        /// <summary>
        /// Returns metadata for the children created while <paramref name="parent"/> was running, ordered by creation QPC.
        /// </summary>
        public IReadOnlyList<AsyncProfilerDispatcherInfo> GetChildDispatcherInfos(AsyncProfilerCallStack parent)
        {
            if (parent == null)
            {
                throw new ArgumentNullException(nameof(parent));
            }

            IReadOnlyList<AsyncProfilerDispatcherInfo> candidates = GetChildDispatcherInfos(
                parent.Thread.ProcessIndex,
                parent.DispatcherId);
            if (candidates.Count == 0)
            {
                return candidates;
            }

            var result = new List<AsyncProfilerDispatcherInfo>();
            for (int i = 0; i < candidates.Count; i++)
            {
                AsyncProfilerDispatcherInfo dispatcher = candidates[i];
                if (dispatcher.CreationThread.Equals(parent.Thread) &&
                    parent.StartQpc <= dispatcher.CreationQpc &&
                    dispatcher.CreationQpc < parent.EndQpc)
                {
                    result.Add(dispatcher);
                }
            }
            return result;
        }

        /// <summary>
        /// Returns the parent async call stack recorded by <paramref name="child"/>'s create event, as it existed at
        /// the creation QPC, or null when the create event or parent resume interval was not captured.
        /// </summary>
        public AsyncProfilerCallStack GetParentCallStack(AsyncProfilerCallStack child) =>
            TryGetParentCallStack(child, out AsyncProfilerCallStack parent, out _) ? parent : null;

        /// <summary>
        /// Resolves the parent async call stack recorded by <paramref name="child"/>'s create event and returns the
        /// creation QPC. The parent is historical, not necessarily active at the child's sample time.
        /// Recorded completion deltas can be queried at that QPC when the required events were captured.
        /// Intervening synchronous frames are not represented by this relationship.
        /// </summary>
        public bool TryGetParentCallStack(
            AsyncProfilerCallStack child,
            out AsyncProfilerCallStack parent,
            out long parentQpc)
        {
            if (child == null)
            {
                throw new ArgumentNullException(nameof(child));
            }

            AsyncProfilerDispatcherInfo dispatcher = GetDispatcherInfo(child);
            parentQpc = dispatcher?.CreationQpc ?? 0;
            parent = dispatcher == null || dispatcher.ParentDispatcherId == 0
                ? null
                : GetCallStack(dispatcher.CreationThread, dispatcher.ParentDispatcherId, dispatcher.CreationQpc);
            return parent != null;
        }

        internal bool TryGetParentCallStack(
            AsyncProfilerCallStack child,
            List<AsyncProfilerCallStack> candidates,
            out AsyncProfilerCallStack parent,
            out long parentQpc)
        {
            if (child == null)
            {
                throw new ArgumentNullException(nameof(child));
            }
            if (candidates == null)
            {
                throw new ArgumentNullException(nameof(candidates));
            }

            int dispatcherIndex = GetDispatcherIndex(
                child.Thread.ProcessIndex, child.DispatcherId, child.StartQpc);
            if (dispatcherIndex < 0)
            {
                parent = null;
                parentQpc = 0;
                return false;
            }

            DispatcherRecord dispatcher = _dispatchers[dispatcherIndex];
            parentQpc = dispatcher.CreationQpc;
            if (dispatcher.ParentDispatcherId == 0 ||
                !_threads.TryGetValue(dispatcher.CreationThread, out ThreadCallStacks callStacks))
            {
                parent = null;
                return false;
            }

            candidates.Clear();
            callStacks.Query(
                dispatcher.CreationQpc, candidates, this, cacheMaterialized: true);
            candidates.Sort(CompareDepth);
            for (int i = candidates.Count - 1; i >= 0; i--)
            {
                if (candidates[i].DispatcherId == dispatcher.ParentDispatcherId)
                {
                    parent = candidates[i];
                    return true;
                }
            }

            parent = null;
            return false;
        }

        /// <summary>
        /// Allocation-saving query for stack generation. The caller owns and reuses the result list; its activation
        /// slots are overwritten by subsequent queries. Consumers must not retain those activation objects.
        /// </summary>
        /// <remarks>Public completion-observation properties are not populated here. Public callback adapters
        /// populate them before exposing the borrowed activations.</remarks>
        internal void GetCallStacks(AsyncProfilerThreadKey thread, long timestampQpc, List<AsyncProfilerCallStack> result)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            if (_threads.TryGetValue(thread, out ThreadCallStacks callStacks))
            {
                callStacks.Query(timestampQpc, result, this, cacheMaterialized: false);
                result.Sort(CompareDepth);
            }
            else
            {
                result.Clear();
            }
        }

        /// <summary>All finalized async call stacks for a thread, in the order they were recorded.</summary>
        /// <remarks>
        /// For a recorded thread this is an index-backed view, not a snapshot of its count during construction;
        /// unknown threads return an empty list. Individual activation objects are stable. Call <see cref="AsyncProfilerIndexBuilder.Finish"/> before
        /// querying a computer-built index if open-at-stream-end activations must be included.
        /// </remarks>
        public IReadOnlyList<AsyncProfilerCallStack> GetCallStacks(AsyncProfilerThreadKey thread) =>
            _threads.TryGetValue(thread, out ThreadCallStacks c)
                ? (IReadOnlyList<AsyncProfilerCallStack>)new ThreadCallStackList(this, c)
                : Array.Empty<AsyncProfilerCallStack>();

        /// <summary>
        /// Whether this is the thread's unique latest activation begun by the sample time. A later, already
        /// closed activation can leave its dispatcher physically above the surviving activation.
        /// </summary>
        internal bool IsLatestActivation(AsyncProfilerCallStack callStack, long timestampQpc) =>
            _threads.TryGetValue(callStack.Thread, out ThreadCallStacks c) &&
            c.QueryIndex().IsLatestActivation(callStack, timestampQpc);

        /// <summary>
        /// Records a finalized async call stack, interning its frames.
        /// Called by <see cref="AsyncProfilerIndexBuilder"/> as async call stacks close.
        /// </summary>
        internal void Add(AsyncProfilerThreadKey thread, AsyncCallstackKind kind, ulong[] methodIds, int[] frameStates,
            ulong dispatcherId, int depth, byte continuationIndexBase, byte wrapperCount, long startQpc, long endQpc,
            AsyncProfilerCallStack.CompletionDelta[] methodCompletions, AsyncProfilerCallStack.CompletionDelta[] exceptionCompletions, long[] wrapperResets)
        {
            AsyncProfilerCallStackFramesIndex framesIndex = Intern(kind, methodIds, frameStates, thread.ProcessIndex, out AsyncProfilerCallStackFrames frames);
            Add(thread, framesIndex, frames, dispatcherId, depth,
                continuationIndexBase, wrapperCount, startQpc, endQpc,
                methodCompletions, exceptionCompletions, wrapperResets);
        }

        internal void Add(AsyncProfilerThreadKey thread, AsyncProfilerCallStackFramesIndex framesIndex, AsyncProfilerCallStackFrames frames,
            ulong dispatcherId, int depth, byte continuationIndexBase, byte wrapperCount, long startQpc, long endQpc,
            AsyncProfilerCallStack.CompletionDelta[] methodCompletions, AsyncProfilerCallStack.CompletionDelta[] exceptionCompletions, long[] wrapperResets)
        {
            GetOrCreate(thread).Add(framesIndex, dispatcherId, depth,
                continuationIndexBase, wrapperCount, startQpc, endQpc,
                methodCompletions, exceptionCompletions, wrapperResets);
        }

        internal void AddDispatcherInfo(
            AsyncProfilerThreadKey creationThread,
            ulong dispatcherId,
            ulong parentDispatcherId,
            long creationQpc)
        {
            if (dispatcherId == 0)
            {
                return;
            }

            _dispatchers.Add(new DispatcherRecord(
                creationThread, dispatcherId, parentDispatcherId, creationQpc));
            InvalidateDispatcherIndexes();
        }

        internal bool TryGetInternedFrames(ProcessIndex processIndex, AsyncCallstackKind kind,
            ulong[] methodIds, int[] frameStates, int frameCount,
            out AsyncProfilerCallStackFramesIndex framesIndex, out AsyncProfilerCallStackFrames frames)
        {
            var key = new FrameKey(processIndex, kind, methodIds, frameStates, frameCount);
            if (_frameKeyToIndex.TryGetValue(key, out framesIndex))
            {
                frames = _internedCallStackFrames[(int)framesIndex];
                return true;
            }

            frames = null;
            return false;
        }

        private AsyncProfilerCallStackFramesIndex Intern(AsyncCallstackKind kind, ulong[] methodIds, int[] frameStates, ProcessIndex processIndex, out AsyncProfilerCallStackFrames frames)
        {
            var key = new FrameKey(processIndex, kind, methodIds, frameStates, methodIds.Length);
            if (_frameKeyToIndex.TryGetValue(key, out AsyncProfilerCallStackFramesIndex existing))
            {
                frames = _internedCallStackFrames[(int)existing];
                return existing;
            }

            var index = (AsyncProfilerCallStackFramesIndex)_internedCallStackFrames.Count;
            frames = new AsyncProfilerCallStackFrames(kind, methodIds, frameStates, processIndex);
            _internedCallStackFrames.Add(frames);
            _frameKeyToIndex[key] = index;
            OnFrameInterned?.Invoke(frames);
            return index;
        }

        private ThreadCallStacks GetOrCreate(AsyncProfilerThreadKey key)
        {
            if (!_threads.TryGetValue(key, out ThreadCallStacks callStacks))
            {
                callStacks = new ThreadCallStacks(key);
                _threads[key] = callStacks;
            }
            return callStacks;
        }

        private void InvalidateDispatcherIndexes()
        {
            lock (_dispatcherIndexLock)
            {
                _dispatchersById = null;
                _dispatchersByParentId = null;
            }
        }

        private void EnsureDispatcherIndexes()
        {
            if (_dispatchersById != null)
            {
                return;
            }

            lock (_dispatcherIndexLock)
            {
                if (_dispatchersById != null)
                {
                    return;
                }

                var byId = new Dictionary<DispatcherKey, List<int>>();
                var byParentId = new Dictionary<DispatcherKey, List<int>>();
                for (int i = 0; i < _dispatchers.Count; i++)
                {
                    DispatcherRecord dispatcher = _dispatchers[i];
                    AddDispatcherIndex(
                        byId,
                        new DispatcherKey(dispatcher.CreationThread.ProcessIndex, dispatcher.DispatcherId), i);
                    if (dispatcher.ParentDispatcherId != 0)
                    {
                        AddDispatcherIndex(
                            byParentId,
                            new DispatcherKey(dispatcher.CreationThread.ProcessIndex, dispatcher.ParentDispatcherId), i);
                    }
                }

                foreach (List<int> dispatcherIndices in byId.Values)
                {
                    dispatcherIndices.Sort((left, right) =>
                        _dispatchers[left].CreationQpc.CompareTo(_dispatchers[right].CreationQpc));
                }
                foreach (List<int> dispatcherIndices in byParentId.Values)
                {
                    dispatcherIndices.Sort((left, right) =>
                        _dispatchers[left].CreationQpc.CompareTo(_dispatchers[right].CreationQpc));
                }

                _dispatchersByParentId = byParentId;
                _dispatchersById = byId;
            }
        }

        private static void AddDispatcherIndex(
            Dictionary<DispatcherKey, List<int>> index,
            DispatcherKey key,
            int dispatcherIndex)
        {
            if (!index.TryGetValue(key, out List<int> dispatcherIndices))
            {
                dispatcherIndices = new List<int>();
                index.Add(key, dispatcherIndices);
            }
            dispatcherIndices.Add(dispatcherIndex);
        }

        private int GetDispatcherIndex(
            ProcessIndex processIndex,
            ulong dispatcherId,
            long timestampQpc)
        {
            EnsureDispatcherIndexes();
            var key = new DispatcherKey(processIndex, dispatcherId);
            if (!_dispatchersById.TryGetValue(key, out List<int> dispatcherIndices))
            {
                return -1;
            }

            int lo = 0;
            int hi = dispatcherIndices.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (_dispatchers[dispatcherIndices[mid]].CreationQpc <= timestampQpc)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }
            return lo == 0 ? -1 : dispatcherIndices[lo - 1];
        }

        private static AsyncProfilerDispatcherInfo MaterializeDispatcherInfo(DispatcherRecord dispatcher) =>
            new AsyncProfilerDispatcherInfo(
                dispatcher.CreationThread,
                dispatcher.DispatcherId,
                dispatcher.ParentDispatcherId,
                dispatcher.CreationQpc);

        void IFastSerializable.ToStream(Serializer serializer)
        {
            serializer.Write(SerializationVersion);

            serializer.Write(_internedCallStackFrames.Count);
            for (int i = 0; i < _internedCallStackFrames.Count; i++)
            {
                _internedCallStackFrames[i].Write(serializer);
            }

            serializer.Write(_threads.Count);
            foreach (KeyValuePair<AsyncProfilerThreadKey, ThreadCallStacks> pair in _threads)
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
            foreach (KeyValuePair<ProcessIndex, List<CompletionAvailabilityEpoch>> pair in _completionAvailability)
            {
                serializer.Write((int)pair.Key);
                serializer.Write(pair.Value.Count);
                for (int i = 0; i < pair.Value.Count; i++)
                {
                    serializer.Write(pair.Value[i].StartQpc);
                    serializer.Write(pair.Value[i].Flags);
                }
            }

            serializer.Write(_dispatchers.Count);
            for (int i = 0; i < _dispatchers.Count; i++)
            {
                DispatcherRecord dispatcher = _dispatchers[i];
                serializer.Write((int)dispatcher.CreationThread.ProcessIndex);
                serializer.Write((long)dispatcher.CreationThread.OsThreadId);
                serializer.Write((long)dispatcher.DispatcherId);
                serializer.Write((long)dispatcher.ParentDispatcherId);
                serializer.Write(dispatcher.CreationQpc);
            }
        }

        void IFastSerializable.FromStream(Deserializer deserializer)
        {
            int version = deserializer.ReadInt();
            if (version != SerializationVersion)
            {
                throw new SerializationException("Unsupported AsyncProfilerCallStacksIndex serialization version " + version);
            }

            _internedCallStackFrames.Clear();
            _frameKeyToIndex.Clear();
            _threads.Clear();
            _completionAvailability.Clear();
            _dispatchers.Clear();
            InvalidateDispatcherIndexes();

            int frameCount = deserializer.ReadInt();
            for (int i = 0; i < frameCount; i++)
            {
                _internedCallStackFrames.Add(AsyncProfilerCallStackFrames.Read(deserializer));
            }

            int threadCount = deserializer.ReadInt();
            for (int t = 0; t < threadCount; t++)
            {
                ProcessIndex processIndex = (ProcessIndex)deserializer.ReadInt();
                ulong osThreadId = (ulong)deserializer.ReadInt64();
                int count = deserializer.ReadInt();
                var thread = new AsyncProfilerThreadKey(processIndex, osThreadId);
                var callStacks = new ThreadCallStacks(thread, count);
                _threads[thread] = callStacks;
                for (int i = 0; i < count; i++)
                {
                    callStacks.ReadAndAdd(deserializer);
                }
            }

            int processCount = deserializer.ReadInt();
            for (int i = 0; i < processCount; i++)
            {
                ProcessIndex processIndex = (ProcessIndex)deserializer.ReadInt();
                int epochCount = deserializer.ReadInt();
                var epochs = new List<CompletionAvailabilityEpoch>(epochCount);
                for (int e = 0; e < epochCount; e++)
                {
                    epochs.Add(new CompletionAvailabilityEpoch(deserializer.ReadInt64(), deserializer.ReadByte()));
                }
                _completionAvailability[processIndex] = epochs;
            }

            int dispatcherCount = deserializer.ReadInt();
            for (int i = 0; i < dispatcherCount; i++)
            {
                var creationThread = new AsyncProfilerThreadKey(
                    (ProcessIndex)deserializer.ReadInt(),
                    (ulong)deserializer.ReadInt64());
                _dispatchers.Add(new DispatcherRecord(
                    creationThread,
                    (ulong)deserializer.ReadInt64(),
                    (ulong)deserializer.ReadInt64(),
                    deserializer.ReadInt64()));
            }
        }

        internal void PopulateCompletionObservations(AsyncProfilerCallStack callStack)
        {
            CompletionAvailabilityEpoch epoch = FindCompletionEpoch(callStack.Thread.ProcessIndex, callStack.StartQpc);
            AsyncCallstackKind kind = callStack.Frames.Kind;
            callStack.MethodCompletionObserved = epoch != null && epoch.MethodObserved(kind);
            callStack.ExceptionCompletionObserved = epoch != null && epoch.ExceptionObserved(kind);
        }

        private AsyncProfilerCallStackFrames ResolveFrames(AsyncProfilerCallStackFramesIndex index) => _internedCallStackFrames[(int)index];

        private static int CompareDepth(AsyncProfilerCallStack left, AsyncProfilerCallStack right) =>
            left.Depth.CompareTo(right.Depth);

        private List<CompletionAvailabilityEpoch> GetCompletionEpochs(ProcessIndex processIndex)
        {
            if (!_completionAvailability.TryGetValue(processIndex, out List<CompletionAvailabilityEpoch> epochs))
            {
                epochs = new List<CompletionAvailabilityEpoch>();
                _completionAvailability[processIndex] = epochs;
            }
            return epochs;
        }

        private CompletionAvailabilityEpoch FindCompletionEpoch(ProcessIndex processIndex, long timestampQpc)
        {
            if (!_completionAvailability.TryGetValue(processIndex, out List<CompletionAvailabilityEpoch> epochs))
            {
                return null;
            }
            int index = FindCompletionEpochIndex(epochs, timestampQpc);
            return index >= 0 ? epochs[index] : null;
        }

        private static int FindCompletionEpochIndex(List<CompletionAvailabilityEpoch> epochs, long timestampQpc)
        {
            int lo = 0;
            int hi = epochs.Count;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (epochs[mid].StartQpc <= timestampQpc)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }
            return lo - 1;
        }

        private sealed class CompletionAvailabilityEpoch
        {
            private const byte RuntimeMethod = 1 << 0;
            private const byte StateMachineMethod = 1 << 1;
            private const byte RuntimeException = 1 << 2;
            private const byte StateMachineException = 1 << 3;

            public CompletionAvailabilityEpoch(long startQpc)
            {
                StartQpc = startQpc;
            }

            public CompletionAvailabilityEpoch(long startQpc, byte flags)
            {
                StartQpc = startQpc;
                Flags = flags;
            }

            public long StartQpc { get; }
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

        private readonly struct CallStackRecord
        {
            public CallStackRecord(ulong dispatcherId, int depth,
                AsyncProfilerCallStackFramesIndex framesIndex, int timelinesIndex,
                byte continuationIndexBase, byte wrapperCount, long startQpc, long endQpc)
            {
                DispatcherId = dispatcherId;
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
            public readonly ulong DispatcherId;
            public readonly int Depth;
            public readonly AsyncProfilerCallStackFramesIndex FramesIndex;
            public readonly int TimelinesIndex;
            public readonly byte ContinuationIndexBase;
            public readonly byte WrapperCount;
        }

        private readonly struct DispatcherRecord
        {
            public DispatcherRecord(AsyncProfilerThreadKey creationThread, ulong dispatcherId,
                ulong parentDispatcherId, long creationQpc)
            {
                CreationThread = creationThread;
                DispatcherId = dispatcherId;
                ParentDispatcherId = parentDispatcherId;
                CreationQpc = creationQpc;
            }

            public readonly AsyncProfilerThreadKey CreationThread;
            public readonly ulong DispatcherId;
            public readonly ulong ParentDispatcherId;
            public readonly long CreationQpc;
        }

        private sealed class CallStackTimelines
        {
            public CallStackTimelines(AsyncProfilerCallStack.CompletionDelta[] methodCompletions,
                AsyncProfilerCallStack.CompletionDelta[] exceptionCompletions, long[] wrapperResets)
            {
                MethodCompletions = methodCompletions;
                ExceptionCompletions = exceptionCompletions;
                WrapperResets = wrapperResets;
            }

            public readonly AsyncProfilerCallStack.CompletionDelta[] MethodCompletions;
            public readonly AsyncProfilerCallStack.CompletionDelta[] ExceptionCompletions;
            public readonly long[] WrapperResets;
        }

        /// <summary>Per-thread compact async call stack records plus a lazily-built interval index.</summary>
        private sealed class ThreadCallStacks
        {
            private readonly AsyncProfilerThreadKey _thread;
            private readonly CallStackRecordCollection _recorded;
            private readonly object _queryLock = new object();
            private List<CallStackTimelines> _timelines;
            private Dictionary<int, AsyncProfilerCallStack> _materialized;
            private CallStacksIntervalIndex _index;

            public ThreadCallStacks(AsyncProfilerThreadKey thread)
            {
                _thread = thread;
                _recorded = new CallStackRecordCollection();
            }

            public ThreadCallStacks(AsyncProfilerThreadKey thread, int capacity)
            {
                _thread = thread;
                _recorded = new CallStackRecordCollection(capacity);
            }

            public int Count => _recorded.Count;

            public void Add(AsyncProfilerCallStackFramesIndex framesIndex, ulong dispatcherId, int depth,
                byte continuationIndexBase, byte wrapperCount, long startQpc, long endQpc,
                AsyncProfilerCallStack.CompletionDelta[] methodCompletions,
                AsyncProfilerCallStack.CompletionDelta[] exceptionCompletions, long[] wrapperResets)
            {
                int timelinesIndex = AddTimelines(methodCompletions, exceptionCompletions, wrapperResets);
                _recorded.Add(new CallStackRecord(dispatcherId, depth, framesIndex, timelinesIndex,
                    continuationIndexBase, wrapperCount, startQpc, endQpc));
                _index = null; // invalidate the cached query index
            }

            public void ReadAndAdd(Deserializer deserializer)
            {
                ulong dispatcherId = (ulong)deserializer.ReadInt64();
                int depth = deserializer.ReadInt();
                var framesIndex = (AsyncProfilerCallStackFramesIndex)deserializer.ReadInt();
                byte continuationIndexBase = deserializer.ReadByte();
                byte wrapperCount = deserializer.ReadByte();
                long startQpc = deserializer.ReadInt64();
                long endQpc = deserializer.ReadInt64();

                AsyncProfilerCallStack.CompletionDelta[] methodCompletions = ReadCompletions(deserializer);
                AsyncProfilerCallStack.CompletionDelta[] exceptionCompletions = ReadCompletions(deserializer);
                long[] wrapperResets = ReadQpcs(deserializer);
                Add(framesIndex, dispatcherId, depth,
                    continuationIndexBase, wrapperCount, startQpc, endQpc,
                    methodCompletions, exceptionCompletions, wrapperResets);
            }

            public void Write(Serializer serializer, int index)
            {
                CallStackRecord record = _recorded[index];
                GetTimelines(record.TimelinesIndex, out AsyncProfilerCallStack.CompletionDelta[] methodCompletions,
                    out AsyncProfilerCallStack.CompletionDelta[] exceptionCompletions, out long[] wrapperResets);
                serializer.Write((long)record.DispatcherId);
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

            public AsyncProfilerCallStack Materialize(int index, AsyncProfilerCallStackFrames resolveFrames, AsyncProfilerCallStack reusable = null)
            {
                CallStackRecord record = _recorded[index];
                GetTimelines(record.TimelinesIndex, out AsyncProfilerCallStack.CompletionDelta[] methodCompletions,
                    out AsyncProfilerCallStack.CompletionDelta[] exceptionCompletions, out long[] wrapperResets);
                if (reusable == null)
                {
                    return new AsyncProfilerCallStack(_thread, record.DispatcherId,
                        record.Depth, record.FramesIndex, resolveFrames,
                        record.ContinuationIndexBase, record.WrapperCount, record.StartQpc, record.EndQpc,
                        methodCompletions, exceptionCompletions, wrapperResets);
                }
                reusable.Reset(_thread, record.DispatcherId,
                    record.Depth, record.FramesIndex, resolveFrames,
                    record.ContinuationIndexBase, record.WrapperCount, record.StartQpc, record.EndQpc,
                    methodCompletions, exceptionCompletions, wrapperResets);
                return reusable;
            }

            public AsyncProfilerCallStackFramesIndex FramesIndexAt(int index) => _recorded[index].FramesIndex;

            public void Query(long timestampQpc, List<AsyncProfilerCallStack> result,
                AsyncProfilerCallStacksIndex owner, bool cacheMaterialized)
            {
                if (cacheMaterialized)
                {
                    lock (_queryLock)
                    {
                        QueryCore(timestampQpc, result, owner, cacheMaterialized: true);
                    }
                    return;
                }

                QueryCore(timestampQpc, result, owner, cacheMaterialized: false);
            }

            private void QueryCore(long timestampQpc, List<AsyncProfilerCallStack> result,
                AsyncProfilerCallStacksIndex owner, bool cacheMaterialized)
            {
                int count = 0;
                QueryIndex().Stab(timestampQpc, this, result, owner, cacheMaterialized, ref count);
                if (result.Count > count)
                {
                    result.RemoveRange(count, result.Count - count);
                }
            }

            public AsyncProfilerCallStack GetOrCreateMaterialized(int index, AsyncProfilerCallStackFrames frames)
            {
                if (_materialized == null)
                {
                    _materialized = new Dictionary<int, AsyncProfilerCallStack>();
                }
                if (!_materialized.TryGetValue(index, out AsyncProfilerCallStack callStack))
                {
                    callStack = Materialize(index, frames);
                    _materialized[index] = callStack;
                }
                return callStack;
            }

            public AsyncProfilerCallStack GetOrCreateMaterializedThreadSafe(int index, AsyncProfilerCallStackFrames frames)
            {
                lock (_queryLock)
                {
                    return GetOrCreateMaterialized(index, frames);
                }
            }

            public CallStacksIntervalIndex QueryIndex()
            {
                CallStacksIntervalIndex index = Volatile.Read(ref _index);
                if (index != null)
                {
                    return index;
                }

                lock (_queryLock)
                {
                    index = _index;
                    if (index == null)
                    {
                        index = new CallStacksIntervalIndex(_recorded);
                        Volatile.Write(ref _index, index);
                    }
                    return index;
                }
            }

            private int AddTimelines(AsyncProfilerCallStack.CompletionDelta[] methodCompletions,
                AsyncProfilerCallStack.CompletionDelta[] exceptionCompletions, long[] wrapperResets)
            {
                if (methodCompletions.Length == 0 && exceptionCompletions.Length == 0 && wrapperResets.Length == 0)
                {
                    return -1;
                }

                if (_timelines == null)
                {
                    _timelines = new List<CallStackTimelines>();
                }
                int index = _timelines.Count;
                _timelines.Add(new CallStackTimelines(methodCompletions, exceptionCompletions, wrapperResets));
                return index;
            }

            private void GetTimelines(int index, out AsyncProfilerCallStack.CompletionDelta[] methodCompletions,
                out AsyncProfilerCallStack.CompletionDelta[] exceptionCompletions, out long[] wrapperResets)
            {
                if (index < 0)
                {
                    methodCompletions = Array.Empty<AsyncProfilerCallStack.CompletionDelta>();
                    exceptionCompletions = Array.Empty<AsyncProfilerCallStack.CompletionDelta>();
                    wrapperResets = Array.Empty<long>();
                    return;
                }
                CallStackTimelines timelines = _timelines[index];
                methodCompletions = timelines.MethodCompletions;
                exceptionCompletions = timelines.ExceptionCompletions;
                wrapperResets = timelines.WrapperResets;
            }

            private static AsyncProfilerCallStack.CompletionDelta[] ReadCompletions(Deserializer deserializer)
            {
                int count = deserializer.ReadInt();
                if (count == 0)
                {
                    return Array.Empty<AsyncProfilerCallStack.CompletionDelta>();
                }
                var result = new AsyncProfilerCallStack.CompletionDelta[count];
                for (int i = 0; i < count; i++)
                {
                    result[i] = new AsyncProfilerCallStack.CompletionDelta(deserializer.ReadInt64(), deserializer.ReadInt());
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

            private static void Write(Serializer serializer, AsyncProfilerCallStack.CompletionDelta[] values)
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

        private sealed class CallStackRecordCollection
        {
            private const int ChunkShift = 12;
            private const int ChunkSize = 1 << ChunkShift;
            private const int ChunkMask = ChunkSize - 1;

            private readonly CallStackRecord[] _items;
            private readonly List<CallStackRecord[]> _chunks;
            private int _count;

            public CallStackRecordCollection()
            {
                _chunks = new List<CallStackRecord[]>();
            }

            public CallStackRecordCollection(int capacity)
            {
                _items = new CallStackRecord[capacity];
            }

            public int Count => _count;

            public CallStackRecord this[int index]
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

            public void Add(CallStackRecord record)
            {
                if (_items != null)
                {
                    _items[_count++] = record;
                    return;
                }

                int chunkIndex = _count >> ChunkShift;
                if (chunkIndex == _chunks.Count)
                {
                    _chunks.Add(new CallStackRecord[ChunkSize]);
                }
                _chunks[chunkIndex][_count & ChunkMask] = record;
                _count++;
            }

        }

        /// <summary>
        /// An augmented interval tree over a thread's async call stacks (an implicit balanced BST over the
        /// start-sorted array, each node carrying the max end of its subtree) supporting O(log n + k)
        /// stabbing queries.
        /// </summary>
        private sealed class CallStacksIntervalIndex
        {
            private readonly int[] _byStart;            // indexes into the compact records, ascending StartQpc
            private readonly long[] _maxEnd;            // _maxEnd[i] = max EndQpc over the subtree rooted at position i
            private readonly CallStackRecordCollection _records;

            public CallStacksIntervalIndex(CallStackRecordCollection callStacks)
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

            public void Stab(long timestampQpc, ThreadCallStacks callStacks, List<AsyncProfilerCallStack> result,
                AsyncProfilerCallStacksIndex owner,
                bool cacheMaterialized, ref int count) =>
                Stab(0, _byStart.Length - 1, timestampQpc, callStacks, result, owner, cacheMaterialized, ref count);

            public bool IsLatestActivation(AsyncProfilerCallStack callStack, long timestampQpc)
            {
                int lo = 0;
                int hi = _byStart.Length;
                while (lo < hi)
                {
                    int mid = lo + ((hi - lo) >> 1);
                    if (_records[_byStart[mid]].StartQpc <= timestampQpc)
                    {
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid;
                    }
                }
                if (lo == 0)
                {
                    return false;
                }
                CallStackRecord latest = _records[_byStart[lo - 1]];
                return latest.StartQpc == callStack.StartQpc && latest.EndQpc == callStack.EndQpc &&
                    latest.EndQpc > timestampQpc && latest.DispatcherId == callStack.DispatcherId &&
                    latest.Depth == callStack.Depth && latest.FramesIndex == callStack.FramesIndex &&
                    (lo < 2 || _records[_byStart[lo - 2]].StartQpc != latest.StartQpc);
            }

            private void Stab(int lo, int hi, long timestampQpc, ThreadCallStacks callStacks,
                List<AsyncProfilerCallStack> result, AsyncProfilerCallStacksIndex owner,
                bool cacheMaterialized, ref int count)
            {
                if (lo > hi)
                {
                    return;
                }
                int mid = (lo + hi) >> 1;
                if (_maxEnd[mid] <= timestampQpc)
                {
                    return; // nothing in this subtree ends after timestampQpc
                }

                Stab(lo, mid - 1, timestampQpc, callStacks, result, owner, cacheMaterialized, ref count);

                int recordIndex = _byStart[mid];
                CallStackRecord a = _records[recordIndex];
                if (a.StartQpc <= timestampQpc)
                {
                    if (a.EndQpc > timestampQpc)
                    {
                        AsyncProfilerCallStack materialized;
                        if (cacheMaterialized)
                        {
                            materialized = callStacks.GetOrCreateMaterialized(
                                recordIndex, owner.ResolveFrames(a.FramesIndex));
                            owner.PopulateCompletionObservations(materialized);
                            result.Add(materialized);
                        }
                        else
                        {
                            AsyncProfilerCallStack reusable = count < result.Count ? result[count] : null;
                            materialized = callStacks.Materialize(
                                recordIndex, owner.ResolveFrames(a.FramesIndex), reusable);
                            if (reusable == null)
                            {
                                result.Add(materialized);
                            }
                        }
                        count++;
                    }
                    Stab(mid + 1, hi, timestampQpc, callStacks, result, owner, cacheMaterialized, ref count);
                }
                // else: right subtree all start after timestampQpc, prune it.
            }

        }

        private sealed class ThreadCallStackList : IReadOnlyList<AsyncProfilerCallStack>
        {
            private readonly AsyncProfilerCallStacksIndex _owner;
            private readonly ThreadCallStacks _callStacks;

            public ThreadCallStackList(AsyncProfilerCallStacksIndex owner, ThreadCallStacks callStacks)
            {
                _owner = owner;
                _callStacks = callStacks;
            }

            public int Count => _callStacks.Count;

            public AsyncProfilerCallStack this[int index]
            {
                get
                {
                    AsyncProfilerCallStack result = _callStacks.GetOrCreateMaterializedThreadSafe(
                        index, _owner.ResolveFrames(_callStacks.FramesIndexAt(index)));
                    _owner.PopulateCompletionObservations(result);
                    return result;
                }
            }

            public IEnumerator<AsyncProfilerCallStack> GetEnumerator()
            {
                for (int i = 0; i < Count; i++)
                {
                    yield return this[i];
                }
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private readonly struct DispatcherKey : IEquatable<DispatcherKey>
        {
            public DispatcherKey(ProcessIndex processIndex, ulong dispatcherId)
            {
                ProcessIndex = processIndex;
                DispatcherId = dispatcherId;
            }

            public ProcessIndex ProcessIndex { get; }
            public ulong DispatcherId { get; }

            public bool Equals(DispatcherKey other) =>
                ProcessIndex == other.ProcessIndex &&
                DispatcherId == other.DispatcherId;

            public override bool Equals(object obj) => obj is DispatcherKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((int)ProcessIndex * 397) ^ DispatcherId.GetHashCode();
                }
            }
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
