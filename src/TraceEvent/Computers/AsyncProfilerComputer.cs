// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

using FastSerialization;

using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>
    /// A compact, deduplicated handle to an <see cref="AsyncCallStackFrames"/> interned in an
    /// <see cref="AsyncCallStacksIndex"/>.
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

        internal void Write(Serializer serializer)
        {
            serializer.Write((byte)Kind);
            serializer.Write(_methodIds.Length);
            for (int i = 0; i < _methodIds.Length; i++)
            {
                serializer.Write((long)_methodIds[i]);
            }
            if (_frameStates == null)
            {
                serializer.Write(-1);
            }
            else
            {
                serializer.Write(_frameStates.Length);
                for (int i = 0; i < _frameStates.Length; i++)
                {
                    serializer.Write(_frameStates[i]);
                }
            }
        }

        internal static AsyncCallStackFrames Read(Deserializer deserializer)
        {
            var kind = (AsyncCallstackKind)deserializer.ReadByte();
            int frameCount = deserializer.ReadInt();
            var methodIds = new ulong[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                methodIds[i] = (ulong)deserializer.ReadInt64();
            }
            int stateCount = deserializer.ReadInt();
            int[] frameStates = null;
            if (stateCount >= 0)
            {
                frameStates = new int[stateCount];
                for (int i = 0; i < stateCount; i++)
                {
                    frameStates[i] = deserializer.ReadInt();
                }
            }
            return new AsyncCallStackFrames(kind, methodIds, frameStates);
        }
    }

    /// <summary>
    /// One recorded run of an async call stack on a thread: the time window <c>[StartQpc, EndQpc)</c> from a
    /// resume callstack to its matching suspend/complete, tagged with the nesting <see cref="Depth"/> and a
    /// reference to the interned <see cref="Frames"/>. Async call stacks nest on a thread, so several can be
    /// active at one instant; <see cref="AsyncCallStacksIndex.GetAsyncCallStacks(AsyncThreadKey, long)"/> returns those covering a
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

        /// <summary>The nesting depth (0 = outermost) of this async call stack on its thread.</summary>
        public int Depth { get; }

        /// <summary>A compact handle to the interned frames (stable for serialization).</summary>
        public AsyncCallStackFramesIndex FramesIndex { get; }

        /// <summary>The interned frames of this async call stack.</summary>
        public AsyncCallStackFrames Frames { get; }

        /// <summary>The continuation-wrapper index captured when this async call stack was emitted.</summary>
        public byte ContinuationIndexBase { get; }

        /// <summary>The continuation-wrapper pool size (<c>ContinuationWrapper.COUNT</c>) in effect for this
        /// activation. Present as a per-run field; 0 if metadata was not seen. Each wrapper-index reset means this many methods completed.</summary>
        public byte WrapperCount { get; }

        public long StartQpc { get; }
        public long EndQpc { get; }

        /// <summary>
        /// The exact number of leaf frames of <see cref="Frames"/> that have completed by <paramref name="qpc"/>,
        /// derived from this async call stack's <c>CompleteMethod</c>/<c>Unwind</c> events. The still-live async stack
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
        /// The number of continuation-wrapper-index resets observed during this async call stack up to
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

        internal void Write(Serializer serializer)
        {
            serializer.Write(Depth);
            serializer.Write((int)FramesIndex);
            serializer.Write((byte)ContinuationIndexBase);
            serializer.Write((byte)WrapperCount);
            serializer.Write(StartQpc);
            serializer.Write(EndQpc);

            serializer.Write(_completions.Length);
            for (int i = 0; i < _completions.Length; i++)
            {
                serializer.Write(_completions[i].Qpc);
                serializer.Write(_completions[i].Delta);
            }

            serializer.Write(_wrapperResets.Length);
            for (int i = 0; i < _wrapperResets.Length; i++)
            {
                serializer.Write(_wrapperResets[i]);
            }
        }

        internal static AsyncCallStack Read(Deserializer deserializer, Func<AsyncCallStackFramesIndex, AsyncCallStackFrames> resolveFrames)
        {
            int depth = deserializer.ReadInt();
            var framesIndex = (AsyncCallStackFramesIndex)deserializer.ReadInt();
            byte continuationIndexBase = deserializer.ReadByte();
            byte wrapperCount = deserializer.ReadByte();
            long startQpc = deserializer.ReadInt64();
            long endQpc = deserializer.ReadInt64();

            int completionCount = deserializer.ReadInt();
            var completions = new CompletionDelta[completionCount];
            for (int i = 0; i < completionCount; i++)
            {
                long qpc = deserializer.ReadInt64();
                int delta = deserializer.ReadInt();
                completions[i] = new CompletionDelta(qpc, delta);
            }

            int wrapperResetCount = deserializer.ReadInt();
            var wrapperResets = new long[wrapperResetCount];
            for (int i = 0; i < wrapperResetCount; i++)
            {
                wrapperResets[i] = deserializer.ReadInt64();
            }

            return new AsyncCallStack(depth, framesIndex, resolveFrames(framesIndex), continuationIndexBase, wrapperCount, startQpc, endQpc, completions, wrapperResets);
        }

        internal readonly struct CompletionDelta
        {
            public readonly long Qpc;
            public readonly int Delta;
            public CompletionDelta(long qpc, int delta) { Qpc = qpc; Delta = delta; }
        }
    }

    /// <summary>
    /// Builds the per-thread active-async-callstack index from the async-profiler sub-event stream
    /// (<see cref="AsyncProfilerTraceEventParser"/>), for both the V2 (RuntimeAsync) and V1
    /// (StateMachineAsync) instrumentation. The recorded, queryable, serializable result is an
    /// <see cref="AsyncCallStacksIndex"/> (see <see cref="Index"/>).
    /// <para>
    /// Model: a thread is <b>ignored until its first <c>ResetAsyncThreadContext</c></b> (live-attach safety);
    /// a reset clears that thread's nesting stack. Async call stacks <b>nest</b> on a thread — a
    /// <b>resume</b> callstack pushes an <see cref="AsyncCallStackBuilder"/>, an <b>append</b> callstack
    /// extends the top one, and a suspend/complete context pops it. In V1 the frames are assembled
    /// incrementally (resume + appends) and are only final at close, so frames are <b>finalized and
    /// interned at pop</b>.
    /// </para>
    /// </summary>
    public sealed class AsyncProfilerComputer : IAsyncProfilerSubEventSink
    {
        // Resume callstacks push a new async call stack onto the thread's nesting stack.
        private static bool IsResumeCallstack(AsyncEventID id) =>
            id == AsyncEventID.ResumeRuntimeAsyncCallstack || id == AsyncEventID.ResumeStateMachineAsyncCallstack;

        // Append callstacks extend the current (top) async call stack's in-progress frames.
        private static bool IsAppendCallstack(AsyncEventID id) =>
            id == AsyncEventID.AppendStateMachineAsyncCallstack;

        private readonly AsyncProfilerTraceEventParser _parser;
        private readonly AsyncProfilerManifest _manifest = new AsyncProfilerManifest();
        private readonly AsyncCallStacksIndex _index = new AsyncCallStacksIndex();
        private readonly Dictionary<AsyncThreadKey, AsyncCallStacks> _threads = new Dictionary<AsyncThreadKey, AsyncCallStacks>();

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

        /// <summary>The built index (recorded async call stacks + interned frames). Serializable and queryable.</summary>
        public AsyncCallStacksIndex Index => _index;

        /// <summary>True once an <c>AsyncProfilerMetadata</c> sub-event has established the QPC frequency.</summary>
        public bool ClockKnown => _qpcFrequency != 0;

        /// <summary>The continuation-wrapper pool size (<c>ContinuationWrapper.COUNT</c>) from metadata; 0 until seen.</summary>
        public byte WrapperCount { get; private set; }

        /// <summary>The number of distinct interned frame lists (useful for asserting dedup).</summary>
        public int DistinctFramesCount => _index.DistinctFramesCount;

        /// <summary>Decodes an <c>AsyncEvents</c> buffer directly into the index (test / manual-drive path).</summary>
        public void Process(byte[] buffer)
        {
            AsyncProfilerTraceEventParser.ParseBuffer(buffer, _manifest, this);
        }

        /// <summary>Resolves an interned frames handle to its frames.</summary>
        public AsyncCallStackFrames GetFrames(AsyncCallStackFramesIndex index) => _index.GetFrames(index);

        /// <summary>
        /// Returns the nested async call stacks active on <paramref name="thread"/> at <paramref name="qpc"/>,
        /// ordered bottom-to-top (ascending depth). Empty if the thread was unarmed or idle at that instant.
        /// </summary>
        public IReadOnlyList<AsyncCallStack> GetAsyncCallStacks(AsyncThreadKey thread, long qpc) => _index.GetAsyncCallStacks(thread, qpc);

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

        void IAsyncProfilerSubEventSink.OnContextResume(in AsyncContextEvent e) { /* the async call stack is pushed by the resume callstack, which carries the frames */ }

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
            // Arm the thread (start handling its events) and drop any in-progress async call stacks: subsequent
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
            _index.Add(key, builder.Kind, builder.MethodIds.ToArray(), builder.FrameStates?.ToArray(),
                builder.Depth, builder.ContinuationIndexBase, WrapperCount, builder.StartQpc, qpc,
                builder.Completions.ToArray(), builder.WrapperResets.ToArray());
        }

        /// <summary>Per-thread build state: the nesting stack of in-progress async call stacks.</summary>
        private sealed class AsyncCallStacks
        {
            public bool Armed;
            private readonly List<AsyncCallStackBuilder> _nesting = new List<AsyncCallStackBuilder>(); // top = last

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

        #endregion
    }
}
