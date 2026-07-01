// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>
    /// A compact, deduplicated handle to an <see cref="AsyncCallStackFrames"/> interned by
    /// <see cref="AsyncProfilerComputer"/>.
    /// </summary>
    public enum AsyncCallStackFramesIndex
    {
        Invalid = -1,
    }

    /// <summary>
    /// Identifies the OS thread (within a process) an async call stack ran on. Async-context ids are
    /// process-wide, so the process is part of the key to stay correct across multi-process traces.
    /// </summary>
    public readonly struct AsyncThreadKey : IEquatable<AsyncThreadKey>
    {
        public readonly int ProcessId;
        public readonly ulong OsThreadId;

        public AsyncThreadKey(int processId, ulong osThreadId)
        {
            ProcessId = processId;
            OsThreadId = osThreadId;
        }

        public bool Equals(AsyncThreadKey other) => ProcessId == other.ProcessId && OsThreadId == other.OsThreadId;
        public override bool Equals(object obj) => obj is AsyncThreadKey o && Equals(o);
        public override int GetHashCode() => (ProcessId * 397) ^ OsThreadId.GetHashCode();
        public override string ToString() => "pid=" + ProcessId + " tid=" + OsThreadId;
    }

    /// <summary>
    /// The frames of an async call stack, ordered leaf-first, interned (deduplicated) across the trace.
    /// <see cref="MethodIdAt"/> is a native IP for <see cref="AsyncCallstackKind.RuntimeAsync"/> and a method
    /// handle for <see cref="AsyncCallstackKind.StateMachineAsync"/>; state-machine frames also carry a
    /// per-frame state (0 for runtime frames).
    /// </summary>
    public sealed class AsyncCallStackFrames
    {
        private readonly ulong[] _methodIds;
        private readonly int[] _frameStates; // null for runtime callstacks

        internal AsyncCallStackFrames(AsyncCallstackKind kind, ulong[] methodIds, int[] frameStates)
        {
            Kind = kind;
            _methodIds = methodIds;
            _frameStates = frameStates;
        }

        public AsyncCallstackKind Kind { get; }
        public int FrameCount => _methodIds.Length;
        public ulong MethodIdAt(int index) => _methodIds[index];
        public int FrameStateAt(int index) => _frameStates != null ? _frameStates[index] : 0;
    }

    /// <summary>
    /// One activation of an async call stack on a thread: the time window <c>[StartQpc, EndQpc)</c> from a
    /// resume callstack to its matching suspend/complete, tagged with the nesting <see cref="Depth"/> and a
    /// reference to the interned <see cref="Frames"/>. Async call stacks nest on a thread, so several can be
    /// active at one instant; <see cref="AsyncProfilerComputer.GetAsyncCallStacks"/> returns those covering a
    /// time, ordered by depth (bottom-to-top).
    /// </summary>
    public sealed class AsyncCallStack
    {
        private readonly CompletionDelta[] _completions; // ascending by Qpc
        private readonly long[] _wrapperResets;          // ascending

        internal AsyncCallStack(int depth, AsyncCallStackFramesIndex framesIndex, AsyncCallStackFrames frames,
            byte continuationIndexBase, byte wrapperCount, long startQpc, long endQpc,
            CompletionDelta[] completions, long[] wrapperResets)
        {
            Depth = depth;
            FramesIndex = framesIndex;
            Frames = frames;
            ContinuationIndexBase = continuationIndexBase;
            WrapperCount = wrapperCount;
            StartQpc = startQpc;
            EndQpc = endQpc;
            _completions = completions;
            _wrapperResets = wrapperResets;
        }

        /// <summary>The nesting depth (0 = outermost) of this activation on its thread.</summary>
        public int Depth { get; }

        /// <summary>A compact handle to the interned frames (stable for serialization).</summary>
        public AsyncCallStackFramesIndex FramesIndex { get; }

        /// <summary>The interned frames of this async call stack.</summary>
        public AsyncCallStackFrames Frames { get; }

        /// <summary>The continuation-wrapper index captured when this activation's callstack was emitted.</summary>
        public byte ContinuationIndexBase { get; }

        /// <summary>The continuation-wrapper pool size (<c>ContinuationWrapper.COUNT</c>) in effect for this
        /// activation; 0 if metadata was not seen. Each wrapper-index reset means this many methods completed.</summary>
        public byte WrapperCount { get; }

        public long StartQpc { get; }
        public long EndQpc { get; }

        /// <summary>
        /// The exact number of leaf frames of <see cref="Frames"/> that have completed by <paramref name="qpc"/>,
        /// derived from this activation's <c>CompleteMethod</c>/<c>Unwind</c> events. The still-live async stack
        /// is the frames with these leaf frames trimmed. This is the precise "complete story" and is preferred
        /// when those events are present.
        /// </summary>
        public int GetCompletedFrameCount(long qpc)
        {
            int total = 0;
            for (int i = 0; i < _completions.Length; i++)
            {
                if (_completions[i].Qpc <= qpc)
                {
                    total += _completions[i].Delta;
                }
                else
                {
                    break; // ascending
                }
            }
            return total;
        }

        /// <summary>
        /// The number of completed leaf frames derived from continuation-wrapper-index resets:
        /// <c>GetWrapperResetCount(qpc) * WrapperCount + currentMethodIndex</c>. Wrapper resets only advance
        /// every <see cref="WrapperCount"/> completions, so the caller supplies <paramref name="currentMethodIndex"/>
        /// — the current wrapper slot read from the native sync callstack at <paramref name="qpc"/> — to refine
        /// within the current window and fully align the native stack with the async call stack. Use this
        /// overload when <c>CompleteMethod</c>/<c>Unwind</c> events are not available; otherwise prefer
        /// <see cref="GetCompletedFrameCount(long)"/>.
        /// </summary>
        public int GetCompletedFrameCount(long qpc, int currentMethodIndex) =>
            GetWrapperResetCount(qpc) * WrapperCount + currentMethodIndex;

        /// <summary>
        /// The number of continuation-wrapper-index resets observed during this activation up to
        /// <paramref name="qpc"/> (this stack's wrapper "generation").
        /// </summary>
        public int GetWrapperResetCount(long qpc)
        {
            int lo = 0, hi = _wrapperResets.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (_wrapperResets[mid] <= qpc)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }
            return lo;
        }

        internal readonly struct CompletionDelta
        {
            public readonly long Qpc;
            public readonly int Delta;
            public CompletionDelta(long qpc, int delta) { Qpc = qpc; Delta = delta; }
        }
    }

    /// <summary>
    /// Builds a per-thread, time-interval index of the active async call stacks from the async-profiler
    /// sub-event stream (<see cref="AsyncProfilerTraceEventParser"/>), for both the V2 (RuntimeAsync) and V1
    /// (StateMachineAsync) instrumentation.
    /// <para>
    /// Model: a thread is <b>ignored until its first <c>ResetAsyncThreadContext</c></b> (live-attach safety);
    /// a reset clears that thread's nesting stack. Async call stacks <b>nest</b> on a thread — a
    /// <b>resume</b> callstack pushes an <see cref="AsyncCallStackBuilder"/>, an <b>append</b> callstack
    /// extends the top one, and a suspend/complete context pops it. In V1 the frames are assembled
    /// incrementally (resume + appends) and are only final at close, so frames are <b>finalized and
    /// interned at pop</b>. Each closed activation becomes an <see cref="AsyncCallStack"/> tagged with its
    /// nesting depth; activations may <b>overlap</b>, and the set covering an instant T (ordered by depth)
    /// is the thread's nested async stack (<see cref="GetAsyncCallStacks"/>).
    /// </para>
    /// </summary>
    public sealed class AsyncProfilerComputer : IAsyncProfilerSubEventSink
    {
        // Resume callstacks push a new activation onto the thread's nesting stack.
        private static bool IsResumeCallstack(AsyncEventID id) =>
            id == AsyncEventID.ResumeRuntimeAsyncCallstack || id == AsyncEventID.ResumeStateMachineAsyncCallstack;

        // Append callstacks extend the current (top) activation's in-progress frames.
        private static bool IsAppendCallstack(AsyncEventID id) =>
            id == AsyncEventID.AppendStateMachineAsyncCallstack;

        private readonly AsyncProfilerTraceEventParser _parser;
        private readonly AsyncProfilerManifest _manifest = new AsyncProfilerManifest();
        private readonly Dictionary<AsyncThreadKey, AsyncCallStacks> _threads = new Dictionary<AsyncThreadKey, AsyncCallStacks>();

        private readonly List<AsyncCallStackFrames> _frames = new List<AsyncCallStackFrames>();
        private readonly Dictionary<FrameKey, AsyncCallStackFramesIndex> _framesIntern = new Dictionary<FrameKey, AsyncCallStackFramesIndex>();

        private AsyncEventsTraceData _currentRawEvent;

        // Clock state (QPC <-> UTC), from AsyncProfilerMetadata / AsyncProfilerSyncClock.
        private ulong _qpcFrequency;
        private ulong _qpcSync;
        private ulong _utcSync;

        /// <summary>
        /// Binds the computer to a live parser: it decodes each raw <c>AsyncEvents</c> buffer (maintaining
        /// the manifest across buffers) and feeds the sub-events into the index.
        /// </summary>
        public AsyncProfilerComputer(AsyncProfilerTraceEventParser parser)
        {
            _parser = parser ?? throw new ArgumentNullException(nameof(parser));
            _parser.AsyncEvents += OnRawAsyncEvents;
        }

        /// <summary>
        /// Creates a computer with no parser binding, for unit tests or callers that drive the decoder
        /// directly via <see cref="Process"/>.
        /// </summary>
        public AsyncProfilerComputer()
        {
        }

        /// <summary>True once an <c>AsyncProfilerMetadata</c> sub-event has established the QPC frequency.</summary>
        public bool ClockKnown => _qpcFrequency != 0;

        /// <summary>The continuation-wrapper pool size (<c>ContinuationWrapper.COUNT</c>) from metadata; 0 until seen.</summary>
        public byte WrapperCount { get; private set; }

        /// <summary>The number of distinct interned frame lists (useful for asserting dedup).</summary>
        public int DistinctFramesCount => _frames.Count;

        /// <summary>Decodes an <c>AsyncEvents</c> buffer directly into the index (test / manual-drive path).</summary>
        public void Process(byte[] buffer)
        {
            AsyncProfilerTraceEventParser.ParseBuffer(buffer, _manifest, this);
        }

        /// <summary>Resolves an interned frames handle to its frames.</summary>
        public AsyncCallStackFrames GetFrames(AsyncCallStackFramesIndex index)
        {
            int i = (int)index;
            return (uint)i < (uint)_frames.Count ? _frames[i] : null;
        }

        /// <summary>
        /// Returns the nested async call stacks active on <paramref name="thread"/> at <paramref name="qpc"/>,
        /// ordered bottom-to-top (ascending depth). Empty if the thread was unarmed or idle at that instant.
        /// </summary>
        public IReadOnlyList<AsyncCallStack> GetAsyncCallStacks(AsyncThreadKey thread, long qpc)
        {
            var result = new List<AsyncCallStack>();
            if (_threads.TryGetValue(thread, out AsyncCallStacks state))
            {
                state.QueryIndex().Stab(qpc, result);
                result.Sort((a, b) => a.Depth.CompareTo(b.Depth));
            }
            return result;
        }

        /// <summary>Converts an absolute QPC timestamp to UTC using the current clock sync; null until <see cref="ClockKnown"/>.</summary>
        public DateTime? QpcToDateTime(long qpc)
        {
            if (_qpcFrequency == 0)
            {
                return null;
            }

            long utcTicks = (long)_utcSync + (qpc - (long)_qpcSync) * TimeSpan.TicksPerSecond / (long)_qpcFrequency;
            return DateTime.FromFileTimeUtc(utcTicks);
        }

        #region sink

        void IAsyncProfilerSubEventSink.OnContextCreate(in AsyncContextEvent e) { /* creation only; the run pushes via its resume callstack */ }

        void IAsyncProfilerSubEventSink.OnContextResume(in AsyncContextEvent e) { /* activation is pushed by the resume callstack, which carries the frames */ }

        void IAsyncProfilerSubEventSink.OnContextSuspend(in AsyncContextEvent e) => CloseTop(ThreadKeyOf(e.OsThreadId), e.TimestampQpc);

        void IAsyncProfilerSubEventSink.OnContextComplete(in AsyncContextEvent e) => CloseTop(ThreadKeyOf(e.OsThreadId), e.TimestampQpc);

        void IAsyncProfilerSubEventSink.OnCallstack(in AsyncCallstackEvent e)
        {
            AsyncCallStacks state = GetOrCreate(ThreadKeyOf(e.OsThreadId));
            if (!state.Armed)
            {
                return;
            }

            if (IsResumeCallstack(e.EventId))
            {
                state.Push(new AsyncCallStackBuilder(e));
            }
            else if (IsAppendCallstack(e.EventId))
            {
                state.Top?.AddFrames(e);
            }
        }

        void IAsyncProfilerSubEventSink.OnMethodResume(in AsyncMethodEvent e) { /* a method began running; no frame change */ }

        void IAsyncProfilerSubEventSink.OnMethodComplete(in AsyncMethodEvent e) => AddCompletion(ThreadKeyOf(e.OsThreadId), e.TimestampQpc, 1);

        void IAsyncProfilerSubEventSink.OnException(in AsyncUnwindEvent e) => AddCompletion(ThreadKeyOf(e.OsThreadId), e.TimestampQpc, (int)e.UnwoundFrameCount);

        void IAsyncProfilerSubEventSink.OnResetThreadContext(in AsyncNeutralEvent e)
        {
            // Arm the thread (start handling its events) and drop any in-progress activations: subsequent
            // full callstacks re-establish the state.
            AsyncCallStacks state = GetOrCreate(ThreadKeyOf(e.OsThreadId));
            state.Armed = true;
            state.ClearNesting();
        }

        void IAsyncProfilerSubEventSink.OnResetContinuationWrapperIndex(in AsyncNeutralEvent e)
        {
            // Wrapper-index resets are scoped to the active (top) async call stack, like Complete/Unwind.
            AsyncCallStacks state = GetOrCreate(ThreadKeyOf(e.OsThreadId));
            if (state.Armed)
            {
                state.Top?.WrapperResets.Add(e.TimestampQpc);
            }
        }

        void IAsyncProfilerSubEventSink.OnMetadata(in AsyncMetadataEvent e)
        {
            _qpcFrequency = e.QpcFrequency;
            _qpcSync = e.QpcSync;
            _utcSync = e.UtcSync;
            WrapperCount = e.WrapperCount;
        }

        void IAsyncProfilerSubEventSink.OnSyncClock(in AsyncSyncClockEvent e)
        {
            _qpcSync = e.QpcSync;
            _utcSync = e.UtcSync;
        }

        void IAsyncProfilerSubEventSink.OnUnknown(in AsyncUnknownEvent e) { }

        void IAsyncProfilerSubEventSink.OnParseError(in AsyncProfilerParseError e) { }

        #endregion

        #region private

        private void OnRawAsyncEvents(AsyncEventsTraceData data)
        {
            _currentRawEvent = data;
            try
            {
                AsyncProfilerTraceEventParser.ParseBuffer(data.Buffer, _manifest, this);
            }
            finally
            {
                _currentRawEvent = null;
            }
        }

        private AsyncThreadKey ThreadKeyOf(ulong osThreadId) =>
            new AsyncThreadKey(_currentRawEvent != null ? _currentRawEvent.ProcessID : 0, osThreadId);

        private AsyncCallStacks GetOrCreate(AsyncThreadKey key)
        {
            if (!_threads.TryGetValue(key, out AsyncCallStacks state))
            {
                state = new AsyncCallStacks();
                _threads[key] = state;
            }
            return state;
        }

        private void AddCompletion(AsyncThreadKey key, long qpc, int delta)
        {
            AsyncCallStacks state = GetOrCreate(key);
            if (state.Armed && delta > 0)
            {
                state.Top?.Completions.Add(new AsyncCallStack.CompletionDelta(qpc, delta));
            }
        }

        private void CloseTop(AsyncThreadKey key, long qpc)
        {
            AsyncCallStacks state = GetOrCreate(key);
            if (!state.Armed || state.Top == null)
            {
                return;
            }

            AsyncCallStackBuilder builder = state.Pop();
            AsyncCallStackFramesIndex framesIndex = Intern(builder, out AsyncCallStackFrames frames);
            state.AddRecorded(new AsyncCallStack(builder.Depth, framesIndex, frames, builder.ContinuationIndexBase,
                WrapperCount, builder.StartQpc, qpc, builder.Completions.ToArray(), builder.WrapperResets.ToArray()));
        }

        private AsyncCallStackFramesIndex Intern(AsyncCallStackBuilder builder, out AsyncCallStackFrames frames)
        {
            ulong[] methodIds = builder.MethodIds.ToArray();
            int[] frameStates = builder.FrameStates?.ToArray();

            var key = new FrameKey(builder.Kind, methodIds, frameStates);
            if (_framesIntern.TryGetValue(key, out AsyncCallStackFramesIndex existing))
            {
                frames = _frames[(int)existing];
                return existing;
            }

            var index = (AsyncCallStackFramesIndex)_frames.Count;
            frames = new AsyncCallStackFrames(builder.Kind, methodIds, frameStates);
            _frames.Add(frames);
            _framesIntern[key] = index;
            return index;
        }

        /// <summary>Per-thread state: the nesting stack of in-progress activations plus the recorded ones.</summary>
        private sealed class AsyncCallStacks
        {
            public bool Armed;
            private readonly List<AsyncCallStackBuilder> _nesting = new List<AsyncCallStackBuilder>(); // top = last
            private readonly List<AsyncCallStack> _recorded = new List<AsyncCallStack>();
            private AsyncCallStackIntervalIndex _index;

            public AsyncCallStackBuilder Top => _nesting.Count > 0 ? _nesting[_nesting.Count - 1] : null;

            public void Push(AsyncCallStackBuilder builder)
            {
                builder.Depth = _nesting.Count;
                _nesting.Add(builder);
            }

            public AsyncCallStackBuilder Pop()
            {
                AsyncCallStackBuilder top = _nesting[_nesting.Count - 1];
                _nesting.RemoveAt(_nesting.Count - 1);
                return top;
            }

            public void ClearNesting() => _nesting.Clear();

            public void AddRecorded(AsyncCallStack activation)
            {
                _recorded.Add(activation);
                _index = null; // invalidate the cached query index
            }

            public AsyncCallStackIntervalIndex QueryIndex() => _index ?? (_index = new AsyncCallStackIntervalIndex(_recorded));
        }

        /// <summary>An in-progress async call stack being assembled on a thread's nesting stack.</summary>
        private sealed class AsyncCallStackBuilder
        {
            public readonly ulong DispatcherId;
            public readonly long StartQpc;
            public readonly byte ContinuationIndexBase;
            public readonly AsyncCallstackKind Kind;
            public int Depth;
            public readonly List<ulong> MethodIds = new List<ulong>();
            public List<int> FrameStates; // null unless a state-machine callstack contributed frames
            public readonly List<AsyncCallStack.CompletionDelta> Completions = new List<AsyncCallStack.CompletionDelta>();
            public readonly List<long> WrapperResets = new List<long>();

            public AsyncCallStackBuilder(in AsyncCallstackEvent e)
            {
                DispatcherId = e.DispatcherId;
                StartQpc = e.TimestampQpc;
                ContinuationIndexBase = e.ContinuationIndex;
                Kind = e.Kind;
                AddFrames(e);
            }

            public void AddFrames(in AsyncCallstackEvent e)
            {
                if (e.MethodIds == null || e.MethodIds.Length == 0)
                {
                    return;
                }

                bool hasState = e.FrameStates != null;
                if (hasState && FrameStates == null)
                {
                    // Back-fill zero states for frames already added without state so arrays stay aligned.
                    FrameStates = new List<int>(MethodIds.Count + e.MethodIds.Length);
                    for (int i = 0; i < MethodIds.Count; i++)
                    {
                        FrameStates.Add(0);
                    }
                }

                for (int i = 0; i < e.MethodIds.Length; i++)
                {
                    MethodIds.Add(e.MethodIds[i]);
                    if (FrameStates != null)
                    {
                        FrameStates.Add(hasState ? e.FrameStates[i] : 0);
                    }
                }
            }
        }

        /// <summary>
        /// An augmented interval tree over a thread's recorded activations (an implicit balanced BST over the
        /// start-sorted array, each node carrying the max end of its subtree) supporting O(log n + k)
        /// stabbing queries.
        /// </summary>
        private sealed class AsyncCallStackIntervalIndex
        {
            private readonly AsyncCallStack[] _byStart; // ascending StartQpc
            private readonly long[] _maxEnd;            // _maxEnd[i] = max EndQpc over the subtree rooted at position i

            public AsyncCallStackIntervalIndex(List<AsyncCallStack> activations)
            {
                _byStart = activations.ToArray();
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

                Stab(lo, mid - 1, qpc, result); // left subtree may contain covering activations

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

        #endregion
    }
}
