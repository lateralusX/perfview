// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

using FastSerialization;

using Microsoft.Diagnostics.Tracing.Etlx;
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
    /// Identifies the OS thread (within a process instance) an async call stack ran on. OS process ids can be
    /// reused within a trace, so the unique <see cref="Etlx.ProcessIndex"/> is part of the key.
    /// </summary>
    public readonly struct AsyncThreadKey : IEquatable<AsyncThreadKey>
    {
        public readonly ProcessIndex ProcessIndex;
        public readonly ulong OsThreadId;

        public AsyncThreadKey(ProcessIndex processIndex, ulong osThreadId)
        {
            ProcessIndex = processIndex;
            OsThreadId = osThreadId;
        }

        public bool Equals(AsyncThreadKey other) => ProcessIndex == other.ProcessIndex && OsThreadId == other.OsThreadId;
        public override bool Equals(object obj) => obj is AsyncThreadKey o && Equals(o);
        public override int GetHashCode() => ((int)ProcessIndex * 397) ^ OsThreadId.GetHashCode();
        public override string ToString() => "processIndex=" + ProcessIndex + " tid=" + OsThreadId;
    }

    /// <summary>
    /// The frames of an async call stack, ordered leaf-first, interned (deduplicated) across the trace.
    /// <see cref="MethodIdAt"/> is a native IP for <see cref="AsyncCallstackKind.RuntimeAsync"/> and a method
    /// ID matching CLR MethodLoad/MethodDCStart events for <see cref="AsyncCallstackKind.StateMachineAsync"/>;
    /// state-machine frames also carry a
    /// per-frame state (0 for runtime frames).
    /// </summary>
    public sealed class AsyncCallStackFrames
    {
        private readonly ulong[] _methodIds;
        private readonly int[] _frameStates; // null for runtime callstacks
        private CodeAddressIndex[] _codeAddresses; // per-frame resolved code address; null until symbolized

        internal AsyncCallStackFrames(AsyncCallstackKind kind, ulong[] methodIds, int[] frameStates, ProcessIndex processIndex = 0)
        {
            Kind = kind;
            _methodIds = methodIds;
            _frameStates = frameStates;
            ProcessIndex = processIndex;
        }

        public AsyncCallstackKind Kind { get; }
        public int FrameCount => _methodIds.Length;
        public ulong MethodIdAt(int index) => _methodIds[index];
        public int FrameStateAt(int index) => _frameStates != null ? _frameStates[index] : 0;

        /// <summary>The process these frames were captured in. Build-time only (used to resolve addresses); not serialized.</summary>
        internal ProcessIndex ProcessIndex { get; }

        /// <summary>
        /// The resolved <see cref="CodeAddressIndex"/> for frame <paramref name="index"/> (a location in a
        /// method/module in <see cref="TraceLog.CodeAddresses"/>), or <see cref="CodeAddressIndex.Invalid"/> if the
        /// frames were never symbolized or the frame's method could not be resolved. Use it with
        /// <see cref="TraceLog.CodeAddresses"/> to obtain the method name lazily.
        /// </summary>
        public CodeAddressIndex CodeAddressAt(int index) =>
            _codeAddresses != null && (uint)index < (uint)_codeAddresses.Length ? _codeAddresses[index] : CodeAddressIndex.Invalid;

        /// <summary>
        /// Assigns the resolved code address for frame <paramref name="index"/> (called by <see cref="TraceLog"/> at
        /// build time as methods are discovered). Lazily allocates the per-frame array, defaulting unset frames to
        /// <see cref="CodeAddressIndex.Invalid"/>.
        /// </summary>
        internal void SetCodeAddressAt(int index, CodeAddressIndex codeAddress)
        {
            if (_codeAddresses == null)
            {
                _codeAddresses = new CodeAddressIndex[_methodIds.Length];
                for (int i = 0; i < _codeAddresses.Length; i++)
                {
                    _codeAddresses[i] = CodeAddressIndex.Invalid;
                }
            }
            _codeAddresses[index] = codeAddress;
        }

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
            if (_codeAddresses == null)
            {
                serializer.Write(-1);
            }
            else
            {
                serializer.Write(_codeAddresses.Length);
                for (int i = 0; i < _codeAddresses.Length; i++)
                {
                    serializer.Write((int)_codeAddresses[i]);
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
            var frames = new AsyncCallStackFrames(kind, methodIds, frameStates);
            int codeAddrCount = deserializer.ReadInt();
            if (codeAddrCount >= 0)
            {
                var codeAddresses = new CodeAddressIndex[codeAddrCount];
                for (int i = 0; i < codeAddrCount; i++)
                {
                    codeAddresses[i] = (CodeAddressIndex)deserializer.ReadInt();
                }
                frames._codeAddresses = codeAddresses;
            }
            return frames;
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
        private CompletionDelta[] _methodCompletions;    // ascending by Qpc; CompleteMethod events (delta 1)
        private CompletionDelta[] _exceptionCompletions; // ascending by Qpc; Unwind events (delta = unwound frame count)
        private long[] _wrapperResets;                   // ascending

        internal AsyncCallStack(int depth, AsyncCallStackFramesIndex framesIndex, AsyncCallStackFrames frames,
            byte continuationIndexBase, byte wrapperCount, long startQpc, long endQpc,
            CompletionDelta[] methodCompletions, CompletionDelta[] exceptionCompletions, long[] wrapperResets)
        {
            Reset(depth, framesIndex, frames, continuationIndexBase, wrapperCount, startQpc, endQpc,
                methodCompletions, exceptionCompletions, wrapperResets);
        }

        internal void Reset(int depth, AsyncCallStackFramesIndex framesIndex, AsyncCallStackFrames frames,
            byte continuationIndexBase, byte wrapperCount, long startQpc, long endQpc,
            CompletionDelta[] methodCompletions, CompletionDelta[] exceptionCompletions, long[] wrapperResets)
        {
            Depth = depth;
            FramesIndex = framesIndex;
            Frames = frames;
            ContinuationIndexBase = continuationIndexBase;
            WrapperCount = wrapperCount;
            StartQpc = startQpc;
            EndQpc = endQpc;
            _methodCompletions = methodCompletions;
            _exceptionCompletions = exceptionCompletions;
            _wrapperResets = wrapperResets;
        }

        /// <summary>The nesting depth (0 = outermost) of this async call stack on its thread.</summary>
        public int Depth { get; private set; }

        /// <summary>A compact handle to the interned frames (stable for serialization).</summary>
        public AsyncCallStackFramesIndex FramesIndex { get; private set; }

        /// <summary>The interned frames of this async call stack.</summary>
        public AsyncCallStackFrames Frames { get; private set; }

        /// <summary>The continuation-wrapper index captured when this async call stack was emitted.</summary>
        public byte ContinuationIndexBase { get; private set; }

        /// <summary>The continuation-wrapper pool size (<c>ContinuationWrapper.COUNT</c>) in effect for this
        /// activation. Present as a per-run field; 0 if metadata was not seen. Each wrapper-index reset means this many methods completed.</summary>
        public byte WrapperCount { get; private set; }

        public long StartQpc { get; private set; }
        public long EndQpc { get; private set; }

        /// <summary>
        /// The exact number of leaf frames of <see cref="Frames"/> that have completed by <paramref name="qpc"/>,
        /// derived from this async call stack's <c>CompleteMethod</c> and <c>Unwind</c> events (normal completions
        /// plus exceptional unwinds). The still-live async stack is the frames with these leaf frames trimmed. This
        /// is the precise "complete story" and is preferred when those events are present.
        /// </summary>
        public int GetCompletedFrameCount(long qpc) =>
            GetMethodCompletedFrameCount(qpc) + GetExceptionCompletedFrameCount(qpc);

        /// <summary>
        /// The number of leaf frames completed by <paramref name="qpc"/> via normal <c>CompleteMethod</c> events
        /// (each contributes 1). Meaningful when <c>CompleteMethod</c> events were observed in this activation's
        /// metadata/configuration epoch; otherwise this is 0 and the normal completed count must be derived another
        /// way (the continuation-wrapper slot for V2, or the inline-resumed frames on the sync stack for V1).
        /// </summary>
        public int GetMethodCompletedFrameCount(long qpc) => SumDeltasUpTo(_methodCompletions, qpc);

        /// <summary>
        /// The number of leaf frames completed by <paramref name="qpc"/> via <c>Unwind</c> (exception) events (each
        /// contributes its unwound frame count). Exceptional completions leave the sync stack, so this is the only
        /// way to observe them; add it to the normal completed count regardless of how the latter was derived.
        /// </summary>
        public int GetExceptionCompletedFrameCount(long qpc) => SumDeltasUpTo(_exceptionCompletions, qpc);

        private static int SumDeltasUpTo(CompletionDelta[] deltas, long qpc)
        {
            int total = 0;
            for (int i = 0; i < deltas.Length; i++)
            {
                if (deltas[i].Qpc <= qpc)
                {
                    total += deltas[i].Delta;
                }
                else
                {
                    break; // ascending
                }
            }
            return total;
        }

        /// <summary>
        /// The number of completed leaf frames derived from continuation-wrapper-index resets and the current
        /// wrapper slot, relative to the wrapper slot captured at resume (<see cref="ContinuationIndexBase"/>):
        /// <c>GetWrapperResetCount(qpc) * WrapperCount + currentMethodIndex - ContinuationIndexBase</c>
        /// (clamped to &gt;= 0). Wrapper resets only advance every <see cref="WrapperCount"/> completions, so the
        /// caller supplies <paramref name="currentMethodIndex"/> — the current wrapper slot read from the native
        /// sync callstack at <paramref name="qpc"/> — to refine within the current window.
        /// <para>
        /// <see cref="ContinuationIndexBase"/> is the wrapper slot at resume: normally 0, but on late attach it is
        /// the current slot, because the resets that advanced it before attach could not be observed. Subtracting
        /// it makes the count start from that value, so only completions observed since resume are counted.
        /// </para>
        /// Use this overload when <c>CompleteMethod</c> events were not observed in this activation's
        /// metadata/configuration epoch; otherwise prefer <see cref="GetCompletedFrameCount(long)"/>.
        /// </summary>
        public int GetCompletedFrameCount(long qpc, int currentMethodIndex)
        {
            int completed = (GetWrapperResetCount(qpc) * WrapperCount) + currentMethodIndex - ContinuationIndexBase;
            return completed > 0 ? completed : 0;
        }

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

            serializer.Write(_methodCompletions.Length);
            for (int i = 0; i < _methodCompletions.Length; i++)
            {
                serializer.Write(_methodCompletions[i].Qpc);
                serializer.Write(_methodCompletions[i].Delta);
            }

            serializer.Write(_exceptionCompletions.Length);
            for (int i = 0; i < _exceptionCompletions.Length; i++)
            {
                serializer.Write(_exceptionCompletions[i].Qpc);
                serializer.Write(_exceptionCompletions[i].Delta);
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

            int methodCompletionCount = deserializer.ReadInt();
            CompletionDelta[] methodCompletions = methodCompletionCount == 0
                ? Array.Empty<CompletionDelta>()
                : new CompletionDelta[methodCompletionCount];
            for (int i = 0; i < methodCompletionCount; i++)
            {
                long qpc = deserializer.ReadInt64();
                int delta = deserializer.ReadInt();
                methodCompletions[i] = new CompletionDelta(qpc, delta);
            }

            int exceptionCompletionCount = deserializer.ReadInt();
            CompletionDelta[] exceptionCompletions = exceptionCompletionCount == 0
                ? Array.Empty<CompletionDelta>()
                : new CompletionDelta[exceptionCompletionCount];
            for (int i = 0; i < exceptionCompletionCount; i++)
            {
                long qpc = deserializer.ReadInt64();
                int delta = deserializer.ReadInt();
                exceptionCompletions[i] = new CompletionDelta(qpc, delta);
            }

            int wrapperResetCount = deserializer.ReadInt();
            long[] wrapperResets = wrapperResetCount == 0
                ? Array.Empty<long>()
                : new long[wrapperResetCount];
            for (int i = 0; i < wrapperResetCount; i++)
            {
                wrapperResets[i] = deserializer.ReadInt64();
            }

            return new AsyncCallStack(depth, framesIndex, resolveFrames(framesIndex), continuationIndexBase, wrapperCount, startQpc, endQpc, methodCompletions, exceptionCompletions, wrapperResets);
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
    public sealed class AsyncProfilerComputer : IAsyncProfilerSubEventSink, IAsyncProfilerCallstackPayloadSink
    {
        // Resume callstacks push a new async call stack onto the thread's nesting stack.
        private static bool IsResumeCallstack(AsyncEventID id) =>
            id == AsyncEventID.ResumeRuntimeAsyncCallstack || id == AsyncEventID.ResumeStateMachineAsyncCallstack;

        // Append callstacks extend the current (top) async call stack's in-progress frames.
        private static bool IsAppendCallstack(AsyncEventID id) =>
            id == AsyncEventID.AppendStateMachineAsyncCallstack;

        private readonly AsyncProfilerTraceEventParser _parser;
        private readonly Func<AsyncEventsTraceData, ProcessIndex> _processIndexOf;
        private readonly AsyncCallStacksIndex _index = new AsyncCallStacksIndex();
        private readonly Dictionary<AsyncThreadKey, AsyncCallStacks> _threads = new Dictionary<AsyncThreadKey, AsyncCallStacks>();
        private readonly Dictionary<ProcessIndex, ProcessState> _processes = new Dictionary<ProcessIndex, ProcessState>();
        private readonly Stack<AsyncCallStackBuilder> _builderPool = new Stack<AsyncCallStackBuilder>();
        private readonly ulong[] _callstackMethodIds = new ulong[byte.MaxValue];
        private readonly int[] _callstackFrameStates = new int[byte.MaxValue];

        private ProcessIndex _currentProcessIndex;

        /// <summary>
        /// Fired for each frame methodId as a resume/append callstack is processed (i.e. during the event stream,
        /// before end-of-trace rundown), letting the host pre-register the frame's code address so a covering
        /// method load/rundown binds it. This makes frames resolvable even for async call stacks that are only
        /// committed later at <see cref="Finish"/> (still live at capture end). Args: processIndex, methodId, kind.
        /// </summary>
        public Action<ProcessIndex, ulong, AsyncCallstackKind> OnFrameObserved;

        /// <summary>
        /// Binds the computer to a live parser: it resolves each carrying event to a process instance, decodes the
        /// raw <c>AsyncEvents</c> buffer using that process's manifest, and feeds the sub-events into the index.
        /// </summary>
        internal AsyncProfilerComputer(AsyncProfilerTraceEventParser parser, Func<AsyncEventsTraceData, ProcessIndex> processIndexOf)
        {
            _parser = parser ?? throw new ArgumentNullException(nameof(parser));
            _processIndexOf = processIndexOf ?? throw new ArgumentNullException(nameof(processIndexOf));
            _parser.AsyncEvents += OnRawAsyncEvents;
        }

        /// <summary>
        /// Creates a computer with no parser binding, for unit tests or callers that drive the decoder
        /// directly via <see cref="Process(byte[])"/>.
        /// </summary>
        public AsyncProfilerComputer()
        {
        }

        /// <summary>The built index (recorded async call stacks + interned frames). Serializable and queryable.</summary>
        public AsyncCallStacksIndex Index => _index;

        /// <summary>True once an <c>AsyncProfilerMetadata</c> sub-event has established the QPC frequency.</summary>
        public bool ClockKnown => ClockKnownForProcess(0);

        /// <summary>The continuation-wrapper pool size (<c>ContinuationWrapper.COUNT</c>) from metadata; 0 until seen.</summary>
        public byte WrapperCount => GetWrapperCount(0);

        /// <summary>The number of distinct interned frame lists (useful for asserting dedup).</summary>
        public int DistinctFramesCount => _index.DistinctFramesCount;

        /// <summary>Decodes an <c>AsyncEvents</c> buffer directly into the index (test / manual-drive path).</summary>
        public void Process(byte[] buffer)
        {
            Process(buffer, 0);
        }

        /// <summary>Decodes an async-profiler buffer for one process instance into the index.</summary>
        public void Process(byte[] buffer, ProcessIndex processIndex)
        {
            ProcessState process = GetOrCreateProcess(processIndex);
            _currentProcessIndex = processIndex;
            AsyncProfilerTraceEventParser.ParseBuffer(buffer, process.Manifest, this);
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
            return QpcToDateTime(0, qpc);
        }

        /// <summary>True once metadata has established the QPC frequency for <paramref name="processIndex"/>.</summary>
        public bool ClockKnownForProcess(ProcessIndex processIndex) =>
            _processes.TryGetValue(processIndex, out ProcessState process) && process.QpcFrequency != 0;

        /// <summary>The continuation-wrapper pool size currently configured for <paramref name="processIndex"/>.</summary>
        public byte GetWrapperCount(ProcessIndex processIndex) =>
            _processes.TryGetValue(processIndex, out ProcessState process) ? process.WrapperCount : (byte)0;

        /// <summary>
        /// Converts an absolute QPC timestamp to UTC using the current clock synchronization for
        /// <paramref name="processIndex"/>; returns null until that process has emitted metadata.
        /// </summary>
        public DateTime? QpcToDateTime(ProcessIndex processIndex, long qpc)
        {
            if (!_processes.TryGetValue(processIndex, out ProcessState process) || process.QpcFrequency == 0)
            {
                return null;
            }

            long frequency = checked((long)process.QpcFrequency);
            long qpcDelta = qpc - checked((long)process.QpcSync);
            long wholeSeconds = qpcDelta / frequency;
            long remainingQpc = qpcDelta % frequency;
            long utcTicks = checked(
                checked((long)process.UtcSync) +
                checked(wholeSeconds * TimeSpan.TicksPerSecond) +
                checked(remainingQpc * TimeSpan.TicksPerSecond) / frequency);
            return DateTime.FromFileTimeUtc(utcTicks);
        }

        #region sink

        void IAsyncProfilerSubEventSink.OnContextCreate(in AsyncContextEvent e) { /* creation only; the run pushes via its resume callstack */ }

        void IAsyncProfilerSubEventSink.OnContextResume(in AsyncContextEvent e) { /* the async call stack is pushed by the resume callstack, which carries the frames */ }

        void IAsyncProfilerSubEventSink.OnContextSuspend(in AsyncContextEvent e) => CloseTop(ThreadKeyOf(e.OsThreadId), e.TimestampQpc);

        void IAsyncProfilerSubEventSink.OnContextComplete(in AsyncContextEvent e) => CloseTop(ThreadKeyOf(e.OsThreadId), e.TimestampQpc);

        void IAsyncProfilerSubEventSink.OnCallstack(in AsyncCallstackEvent e) => ProcessCallstack(e, reusablePayload: false);

        bool IAsyncProfilerCallstackPayloadSink.TryOnCallstack(AsyncEventID eventId, long timestampQpc,
            in AsyncProfilerBufferHeader header, byte[] buffer, ref int index, int payloadEnd)
        {
            if (!AsyncCallstackEvent.TryReadInto(eventId, timestampQpc, header, buffer, ref index, payloadEnd,
                _callstackMethodIds, _callstackFrameStates, out AsyncCallstackEvent callstack))
            {
                return false;
            }

            ProcessCallstack(callstack, reusablePayload: true);
            return true;
        }

        private void ProcessCallstack(in AsyncCallstackEvent e, bool reusablePayload)
        {
            AsyncCallStacks state = GetOrCreate(ThreadKeyOf(e.OsThreadId));
            if (!state.Armed)
            {
                return;
            }

            bool accepted;
            if (IsResumeCallstack(e.EventId))
            {
                state.Push(RentBuilder(e, CurrentProcess.WrapperCount, reusablePayload));
                accepted = true;
            }
            else if (IsAppendCallstack(e.EventId))
            {
                AsyncCallStackBuilder top = state.Top;
                accepted = top != null && top.Kind == e.Kind && top.DispatcherId == e.DispatcherId;
                if (accepted)
                {
                    top.AddFrames(e);
                }
            }
            else
            {
                accepted = false;
            }

            if (!accepted)
            {
                return;
            }

            // Pre-register each frame's code address now (during the event stream, before end-of-trace rundown),
            // so a covering method load binds it even if this async call stack is only committed later by Finish().
            if (OnFrameObserved != null && e.MethodIds != null)
            {
                for (int i = 0; i < e.FrameCount; i++)
                {
                    if (e.MethodIds[i] != 0)
                    {
                        OnFrameObserved(_currentProcessIndex, e.MethodIds[i], e.Kind);
                    }
                }
            }
        }

        void IAsyncProfilerSubEventSink.OnMethodResume(in AsyncMethodEvent e) { /* a method began running; no frame change */ }

        void IAsyncProfilerSubEventSink.OnMethodComplete(in AsyncMethodEvent e)
        {
            AsyncThreadKey key = ThreadKeyOf(e.OsThreadId);
            AsyncCallStacks state = GetOrCreate(key);
            if (!state.Armed)
            {
                return;
            }

            AsyncCallstackKind kind = e.IsStateMachine ? AsyncCallstackKind.StateMachineAsync : AsyncCallstackKind.RuntimeAsync;
            _index.MarkMethodCompletionObserved(_currentProcessIndex, kind, e.TimestampQpc);
            state.Top?.MethodCompletions.Add(new AsyncCallStack.CompletionDelta(e.TimestampQpc, 1));
        }

        void IAsyncProfilerSubEventSink.OnException(in AsyncUnwindEvent e)
        {
            AsyncThreadKey key = ThreadKeyOf(e.OsThreadId);
            AsyncCallStacks state = GetOrCreate(key);
            if (!state.Armed)
            {
                return;
            }

            AsyncCallstackKind kind = e.IsStateMachine ? AsyncCallstackKind.StateMachineAsync : AsyncCallstackKind.RuntimeAsync;
            _index.MarkExceptionCompletionObserved(_currentProcessIndex, kind, e.TimestampQpc);
            if (e.UnwoundFrameCount > 0)
            {
                state.Top?.ExceptionCompletions.Add(new AsyncCallStack.CompletionDelta(e.TimestampQpc, (int)e.UnwoundFrameCount));
            }
        }

        void IAsyncProfilerSubEventSink.OnResetThreadContext(in AsyncNeutralEvent e)
        {
            // Ignore any reset whose timestamp precedes the first metadata sub-event (until the first metadata is
            // seen, _firstMetadataQpc is long.MaxValue so every reset is gated). Without the manifest we cannot
            // correctly frame the stream, and a reset before the first metadata's QPC belongs to a prior profiler
            // session's leftover buffered data (config changes don't flush existing async buffers). The runtime emits
            // a config revision's metadata before its reset wave, so every valid same-session reset has a QPC greater
            // than (or, on coarse-resolution clocks, equal to) the first metadata's QPC. Gating on QPC rather than
            // stream/delivery order drops foreign leftover even when its buffer is delivered after the metadata
            // buffer (buffers are delivered by buffer timestamp, not force-flush order), while never dropping valid
            // same-session data.
            ProcessState process = CurrentProcess;
            if (e.TimestampQpc < process.FirstMetadataQpc)
            {
                return;
            }

            // Commit any in-progress async call stacks at the reset timestamp before clearing, then arm the thread.
            // Once armed, everything on the thread is our session's data (foreign prior-session leftover was gated
            // out above, and nothing is ever pushed while unarmed), so an open activation was genuinely live from
            // its resume up to this reset. Recording it as [StartQpc, resetQpc) preserves stitch coverage for CPU
            // samples that land in that window instead of orphaning them. On the FIRST (arming) reset the nesting is
            // empty, so this commit is a no-op there; subsequent full callstacks re-establish the state.
            AsyncThreadKey key = ThreadKeyOf(e.OsThreadId);
            AsyncCallStacks state = GetOrCreate(key);
            if (state.Armed)
            {
                CommitOpenActivations(key, state, e.TimestampQpc);
            }
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
            ProcessState process = CurrentProcess;
            _index.StartCompletionAvailabilityEpoch(_currentProcessIndex, e.TimestampQpc);
            if (e.TimestampQpc < process.FirstMetadataQpc)
            {
                process.FirstMetadataQpc = e.TimestampQpc;
            }
            process.QpcFrequency = e.QpcFrequency;
            process.QpcSync = e.QpcSync;
            process.UtcSync = e.UtcSync;
            process.WrapperCount = e.WrapperCount;
        }

        void IAsyncProfilerSubEventSink.OnSyncClock(in AsyncSyncClockEvent e)
        {
            ProcessState process = CurrentProcess;
            process.QpcSync = e.QpcSync;
            process.UtcSync = e.UtcSync;
        }

        void IAsyncProfilerSubEventSink.OnUnknown(in AsyncUnknownEvent e) { }

        void IAsyncProfilerSubEventSink.OnParseError(in AsyncProfilerParseError e) { }

        #endregion

        #region private

        private void OnRawAsyncEvents(AsyncEventsTraceData data)
        {
            Process(data.Buffer, _processIndexOf(data));
        }

        private AsyncThreadKey ThreadKeyOf(ulong osThreadId) =>
            new AsyncThreadKey(_currentProcessIndex, osThreadId);

        private ProcessState CurrentProcess => GetOrCreateProcess(_currentProcessIndex);

        private ProcessState GetOrCreateProcess(ProcessIndex processIndex)
        {
            if (!_processes.TryGetValue(processIndex, out ProcessState process))
            {
                process = new ProcessState();
                _processes[processIndex] = process;
            }
            return process;
        }

        private AsyncCallStacks GetOrCreate(AsyncThreadKey key)
        {
            if (!_threads.TryGetValue(key, out AsyncCallStacks state))
            {
                state = new AsyncCallStacks();
                _threads[key] = state;
            }
            return state;
        }

        private void CloseTop(AsyncThreadKey key, long qpc)
        {
            AsyncCallStacks state = GetOrCreate(key);
            if (!state.Armed || state.Top == null)
            {
                return;
            }

            AsyncCallStackBuilder builder = state.Pop();
            Commit(key, builder, qpc);
        }

        /// <summary>
        /// Commits any async call stacks still open (resumed, but not yet closed by a suspend/complete context
        /// event) when the event stream ends. These were live at capture end - the common case when a trace is
        /// taken while async work is still running - so they are recorded as active through end of trace
        /// (<c>EndQpc = long.MaxValue</c>) and stay queryable. Call once after all events have been processed.
        /// </summary>
        public void Finish()
        {
            foreach (KeyValuePair<AsyncThreadKey, AsyncCallStacks> kv in _threads)
            {
                CommitOpenActivations(kv.Key, kv.Value, long.MaxValue);
            }
        }

        /// <summary>
        /// Pops and records every in-progress async call stack still open on <paramref name="state"/>, closing each
        /// at <paramref name="endQpc"/>. Used both at end of stream (<see cref="Finish"/>, endQpc = long.MaxValue)
        /// and on an armed mid-session reset (endQpc = the reset timestamp).
        /// </summary>
        private void CommitOpenActivations(AsyncThreadKey key, AsyncCallStacks state, long endQpc)
        {
            while (state.Top != null)
            {
                AsyncCallStackBuilder builder = state.Pop();
                Commit(key, builder, endQpc);
            }
        }

        private void Commit(AsyncThreadKey key, AsyncCallStackBuilder builder, long endQpc)
        {
            AsyncCallStack.CompletionDelta[] methodCompletions = builder.GetMethodCompletions();
            AsyncCallStack.CompletionDelta[] exceptionCompletions = builder.GetExceptionCompletions();
            long[] wrapperResets = builder.GetWrapperResets();

            try
            {
                if (builder.HasInternedFrames)
                {
                    _index.Add(key, builder.FramesIndex, builder.Frames,
                        builder.Depth, builder.ContinuationIndexBase, builder.WrapperCount, builder.StartQpc, endQpc,
                        methodCompletions, exceptionCompletions, wrapperResets);
                }
                else
                {
                    _index.Add(key, builder.Kind, builder.GetMethodIds(), builder.GetFrameStates(),
                        builder.Depth, builder.ContinuationIndexBase, builder.WrapperCount, builder.StartQpc, endQpc,
                        methodCompletions, exceptionCompletions, wrapperResets);
                }
            }
            finally
            {
                builder.Clear();
                _builderPool.Push(builder);
            }
        }

        private AsyncCallStackBuilder RentBuilder(in AsyncCallstackEvent e, byte wrapperCount, bool reusablePayload)
        {
            AsyncCallStackBuilder builder = _builderPool.Count != 0
                ? _builderPool.Pop()
                : new AsyncCallStackBuilder();

            if (reusablePayload && _index.TryGetInternedFrames(
                _currentProcessIndex, e.Kind, e.MethodIds, e.FrameStates, e.FrameCount,
                out AsyncCallStackFramesIndex framesIndex, out AsyncCallStackFrames frames))
            {
                builder.Initialize(e, wrapperCount, framesIndex, frames);
            }
            else
            {
                builder.Initialize(e, wrapperCount, copyFrames: reusablePayload);
            }
            return builder;
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

        private sealed class ProcessState
        {
            public readonly AsyncProfilerManifest Manifest = new AsyncProfilerManifest();
            public long FirstMetadataQpc = long.MaxValue;
            public ulong QpcFrequency;
            public ulong QpcSync;
            public ulong UtcSync;
            public byte WrapperCount;
        }

        /// <summary>An in-progress async call stack being assembled on a thread's nesting stack.</summary>
        private sealed class AsyncCallStackBuilder
        {
            public ulong DispatcherId;
            public long StartQpc;
            public byte ContinuationIndexBase;
            public byte WrapperCount;
            public AsyncCallstackKind Kind;
            public int Depth;
            public AsyncCallStackFramesIndex FramesIndex;
            public AsyncCallStackFrames Frames;

            public bool HasInternedFrames => Frames != null;

            public List<AsyncCallStack.CompletionDelta> MethodCompletions =>
                _methodCompletions ?? (_methodCompletions = new List<AsyncCallStack.CompletionDelta>());

            public List<AsyncCallStack.CompletionDelta> ExceptionCompletions =>
                _exceptionCompletions ?? (_exceptionCompletions = new List<AsyncCallStack.CompletionDelta>());

            public List<long> WrapperResets =>
                _wrapperResets ?? (_wrapperResets = new List<long>());

            public void Initialize(in AsyncCallstackEvent e, byte wrapperCount, bool copyFrames)
            {
                InitializeHeader(e, wrapperCount);
                if (!copyFrames)
                {
                    _methodIds = e.MethodIds ?? Array.Empty<ulong>();
                    _frameStates = e.FrameStates;
                    return;
                }

                _methodIds = new ulong[e.FrameCount];
                Array.Copy(e.MethodIds, _methodIds, e.FrameCount);
                if (e.FrameStates != null)
                {
                    _frameStates = new int[e.FrameCount];
                    Array.Copy(e.FrameStates, _frameStates, e.FrameCount);
                }
            }

            public void Initialize(in AsyncCallstackEvent e, byte wrapperCount,
                AsyncCallStackFramesIndex framesIndex, AsyncCallStackFrames frames)
            {
                InitializeHeader(e, wrapperCount);
                FramesIndex = framesIndex;
                Frames = frames;
            }

            public void AddFrames(in AsyncCallstackEvent e)
            {
                if (e.MethodIds == null || e.FrameCount == 0)
                {
                    return;
                }

                bool hasState = e.FrameStates != null;
                if (_methodIdsBuilder == null)
                {
                    int existingCount = HasInternedFrames ? Frames.FrameCount : _methodIds.Length;
                    _methodIdsBuilder = new List<ulong>(existingCount + e.FrameCount);
                    for (int i = 0; i < existingCount; i++)
                    {
                        _methodIdsBuilder.Add(HasInternedFrames ? Frames.MethodIdAt(i) : _methodIds[i]);
                    }

                    if (Kind == AsyncCallstackKind.StateMachineAsync)
                    {
                        // Back-fill zero states for frames previously added without state so arrays stay aligned.
                        _frameStatesBuilder = new List<int>(existingCount + e.FrameCount);
                        for (int i = 0; i < existingCount; i++)
                        {
                            _frameStatesBuilder.Add(HasInternedFrames ? Frames.FrameStateAt(i) : _frameStates[i]);
                        }
                    }

                    FramesIndex = AsyncCallStackFramesIndex.Invalid;
                    Frames = null;
                }
                else if (hasState && _frameStatesBuilder == null)
                {
                    _frameStatesBuilder = new List<int>(_methodIdsBuilder.Count + e.MethodIds.Length);
                    for (int i = 0; i < _methodIdsBuilder.Count; i++)
                    {
                        _frameStatesBuilder.Add(0);
                    }
                }

                for (int i = 0; i < e.FrameCount; i++)
                {
                    _methodIdsBuilder.Add(e.MethodIds[i]);
                    if (_frameStatesBuilder != null)
                    {
                        _frameStatesBuilder.Add(hasState ? e.FrameStates[i] : 0);
                    }
                }
            }

            public ulong[] GetMethodIds() => _methodIdsBuilder?.ToArray() ?? _methodIds;

            public int[] GetFrameStates() => _frameStatesBuilder?.ToArray() ?? _frameStates;

            public AsyncCallStack.CompletionDelta[] GetMethodCompletions() =>
                _methodCompletions?.ToArray() ?? Array.Empty<AsyncCallStack.CompletionDelta>();

            public AsyncCallStack.CompletionDelta[] GetExceptionCompletions() =>
                _exceptionCompletions?.ToArray() ?? Array.Empty<AsyncCallStack.CompletionDelta>();

            public long[] GetWrapperResets() => _wrapperResets?.ToArray() ?? Array.Empty<long>();

            public void Clear()
            {
                DispatcherId = 0;
                StartQpc = 0;
                ContinuationIndexBase = 0;
                WrapperCount = 0;
                Kind = default;
                Depth = 0;
                FramesIndex = AsyncCallStackFramesIndex.Invalid;
                Frames = null;
                _methodIds = null;
                _frameStates = null;
                _methodIdsBuilder = null;
                _frameStatesBuilder = null;
                _methodCompletions?.Clear();
                _exceptionCompletions?.Clear();
                _wrapperResets?.Clear();
            }

            private void InitializeHeader(in AsyncCallstackEvent e, byte wrapperCount)
            {
                DispatcherId = e.DispatcherId;
                StartQpc = e.TimestampQpc;
                ContinuationIndexBase = e.ContinuationIndex;
                WrapperCount = wrapperCount;
                Kind = e.Kind;
                FramesIndex = AsyncCallStackFramesIndex.Invalid;
                Frames = null;
            }

            private ulong[] _methodIds;
            private int[] _frameStates;
            private List<ulong> _methodIdsBuilder;
            private List<int> _frameStatesBuilder;
            private List<AsyncCallStack.CompletionDelta> _methodCompletions;
            private List<AsyncCallStack.CompletionDelta> _exceptionCompletions;
            private List<long> _wrapperResets;
        }

        #endregion
    }
}
