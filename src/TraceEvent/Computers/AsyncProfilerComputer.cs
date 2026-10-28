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
    /// A compact, deduplicated handle to an <see cref="AsyncProfilerCallStackFrames"/> interned in an
    /// <see cref="AsyncProfilerCallStacksIndex"/>.
    /// </summary>
    internal enum AsyncProfilerCallStackFramesIndex
    {
        /// <summary>No interned frame list.</summary>
        Invalid = -1,
    }

    /// <summary>
    /// Identifies the OS thread (within a process instance) an async call stack ran on. OS process ids can be
    /// reused within a trace, so the unique <see cref="Etlx.ProcessIndex"/> is part of the key.
    /// </summary>
    internal readonly struct AsyncProfilerThreadKey : IEquatable<AsyncProfilerThreadKey>
    {
        /// <summary>The unique process instance in the owning trace, not its reusable OS process id.</summary>
        public readonly ProcessIndex ProcessIndex;
        /// <summary>The OS thread id recorded in the async-profiler buffer header.</summary>
        public readonly ulong OsThreadId;

        /// <summary>Creates a key for a process instance and OS thread.</summary>
        public AsyncProfilerThreadKey(ProcessIndex processIndex, ulong osThreadId)
        {
            ProcessIndex = processIndex;
            OsThreadId = osThreadId;
        }

        public bool Equals(AsyncProfilerThreadKey other) => ProcessIndex == other.ProcessIndex && OsThreadId == other.OsThreadId;
        public override bool Equals(object obj) => obj is AsyncProfilerThreadKey o && Equals(o);
        public override int GetHashCode() => ((int)ProcessIndex * 397) ^ OsThreadId.GetHashCode();
        public override string ToString() => "processIndex=" + ProcessIndex + " tid=" + OsThreadId;
    }

    /// <summary>
    /// The frames of an async call stack, ordered leaf-first, interned (deduplicated) across the trace.
    /// <see cref="MethodIdAt"/> returns native code IPs for both instrumentation families: runtime-async
    /// continuation IPs for V2 and state-machine <c>MoveNext</c> body IPs for V1. V1 frames also carry raw signed
    /// compiler states. Method ids are not CLR MethodLoad identifiers or MethodDesc handles.
    /// </summary>
    internal sealed class AsyncProfilerCallStackFrames
    {
        private readonly ulong[] _methodIds;
        private readonly int[] _frameStates; // null for runtime callstacks
        private CodeAddressIndex[] _codeAddresses; // per-frame resolved code address; null until symbolized

        internal AsyncProfilerCallStackFrames(AsyncCallstackKind kind, ulong[] methodIds, int[] frameStates, ProcessIndex processIndex = 0)
        {
            Kind = kind;
            _methodIds = methodIds;
            _frameStates = frameStates;
            ProcessIndex = processIndex;
        }

        /// <summary>The V1 or V2 instrumentation family that emitted these frames.</summary>
        public AsyncCallstackKind Kind { get; }
        /// <summary>The number of frames, ordered leaf-to-root.</summary>
        public int FrameCount => _methodIds.Length;
        /// <summary>The native code IP emitted for a frame, or 0 for a runtime-emitted sentinel.</summary>
        public ulong MethodIdAt(int index) => _methodIds[index];
        /// <summary>The raw signed V1 compiler state for a frame, not an IL/native offset; 0 for V2.</summary>
        public int FrameStateAt(int index) => _frameStates != null ? _frameStates[index] : 0;

        /// <summary>The process these frames were captured in. Build-time only (used to resolve addresses); not serialized.</summary>
        internal ProcessIndex ProcessIndex { get; }

        /// <summary>
        /// The registered <see cref="CodeAddressIndex"/> for frame <paramref name="index"/> in the originating
        /// <see cref="TraceLog.CodeAddresses"/> table, or <see cref="CodeAddressIndex.Invalid"/> if no address was
        /// registered or the index is out of range. A valid code-address index does not guarantee a resolved
        /// method or module; use <see cref="TraceCodeAddresses.MethodIndex"/> to check method resolution.
        /// Names and source information remain owned by the originating TraceLog, not this frame list.
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

        internal static AsyncProfilerCallStackFrames Read(Deserializer deserializer)
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
            var frames = new AsyncProfilerCallStackFrames(kind, methodIds, frameStates);
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
    /// active at one instant; <see cref="AsyncProfilerCallStacksIndex.GetCallStacks(AsyncProfilerThreadKey, long)"/> returns those covering a
    /// time, ordered by depth (bottom-to-top).
    /// A reset or decode failure can close the interval early; an activation still open at the end of the stream
    /// is finalized by <see cref="AsyncProfilerComputer.Finish"/> with <see cref="EndQpc"/> equal to
    /// <see cref="long.MaxValue"/>.
    /// </summary>
    /// <remarks>
    /// Public index and TraceLog queries return stable activation objects that can be retained across later queries
    /// and <see cref="TraceLog.ReleaseAsyncProfilerCallStacks"/>. Internal allocation-saving queries used during stack
    /// generation reuse activation objects; contexts supplied to presentation callbacks have that shorter lifetime.
    /// </remarks>
    internal sealed class AsyncProfilerCallStack
    {
        private CompletionDelta[] _methodCompletions;    // ascending by Qpc; CompleteMethod events (delta 1)
        private CompletionDelta[] _exceptionCompletions; // ascending by Qpc; Unwind events (delta = unwound frame count)
        private long[] _wrapperResets;                   // ascending

        internal AsyncProfilerCallStack(int depth, AsyncProfilerCallStackFramesIndex framesIndex, AsyncProfilerCallStackFrames frames,
            byte continuationIndexBase, byte wrapperCount, long startQpc, long endQpc,
            CompletionDelta[] methodCompletions, CompletionDelta[] exceptionCompletions, long[] wrapperResets)
            : this(default, 0, depth, framesIndex, frames,
                  continuationIndexBase, wrapperCount, startQpc, endQpc,
                  methodCompletions, exceptionCompletions, wrapperResets)
        {
        }

        internal AsyncProfilerCallStack(AsyncProfilerThreadKey thread, ulong dispatcherId,
            int depth, AsyncProfilerCallStackFramesIndex framesIndex, AsyncProfilerCallStackFrames frames,
            byte continuationIndexBase, byte wrapperCount, long startQpc, long endQpc,
            CompletionDelta[] methodCompletions, CompletionDelta[] exceptionCompletions, long[] wrapperResets)
        {
            Reset(thread, dispatcherId, depth, framesIndex, frames,
                continuationIndexBase, wrapperCount, startQpc, endQpc,
                methodCompletions, exceptionCompletions, wrapperResets);
        }

        internal void Reset(AsyncProfilerThreadKey thread, ulong dispatcherId,
            int depth, AsyncProfilerCallStackFramesIndex framesIndex, AsyncProfilerCallStackFrames frames,
            byte continuationIndexBase, byte wrapperCount, long startQpc, long endQpc,
            CompletionDelta[] methodCompletions, CompletionDelta[] exceptionCompletions, long[] wrapperResets)
        {
            Thread = thread;
            DispatcherId = dispatcherId;
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

        /// <summary>The process/thread on which this activation resumed.</summary>
        public AsyncProfilerThreadKey Thread { get; private set; }

        /// <summary>The dispatcher associated with this activation.</summary>
        public ulong DispatcherId { get; private set; }

        /// <summary>The nesting depth (0 = outermost) of this async call stack on its thread.</summary>
        public int Depth { get; private set; }

        /// <summary>A compact handle to the interned frames, scoped to the originating async index.</summary>
        public AsyncProfilerCallStackFramesIndex FramesIndex { get; private set; }

        /// <summary>The interned frames of this async call stack.</summary>
        public AsyncProfilerCallStackFrames Frames { get; private set; }

        /// <summary>The continuation-wrapper index captured when this async call stack was emitted.</summary>
        public byte ContinuationIndexBase { get; private set; }

        /// <summary>The continuation-wrapper pool size (<c>ContinuationWrapper.COUNT</c>) in effect for this
        /// activation. Present as a per-run field; 0 if metadata was not seen. Each wrapper-index reset means this many methods completed.</summary>
        public byte WrapperCount { get; private set; }

        /// <summary>The inclusive resume timestamp in the trace's QPC domain.</summary>
        public long StartQpc { get; private set; }
        /// <summary>The exclusive end timestamp in the trace's QPC domain; <see cref="long.MaxValue"/> if open at stream end.</summary>
        public long EndQpc { get; private set; }

        /// <summary>
        /// The recorded number of completed leaf frames at or before <paramref name="qpc"/>, summed from
        /// <c>CompleteMethod</c> and <c>Unwind</c> events. Missing completion events are not inferred.
        /// <see cref="AsyncProfilerCallStackStitcher"/> selects the appropriate event or physical-stack fallback policy.
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
        /// contributes its unwound frame count). For V1, add this to recorded normal completions, or reconcile it
        /// with physical inline matches that may include still-finishing exceptional methods.
        /// The V2 wrapper-index fallback already counts exceptional unwinds, so
        /// adding these deltas to that fallback would double-count them.
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
        /// Use this overload for V2 when <c>CompleteMethod</c> events were not observed in this activation's
        /// metadata/configuration epoch; otherwise prefer <see cref="GetCompletedFrameCount(long)"/>.
        /// Wrapper advancement includes exceptional unwinds, so do not add the recorded exception count again.
        /// V1 completion counting instead uses normal-completion events or physical <c>MoveNext</c> matches,
        /// plus recorded exception deltas.
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
            serializer.Write((int)Thread.ProcessIndex);
            serializer.Write((long)Thread.OsThreadId);
            serializer.Write((long)DispatcherId);
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

        internal static AsyncProfilerCallStack Read(Deserializer deserializer, Func<AsyncProfilerCallStackFramesIndex, AsyncProfilerCallStackFrames> resolveFrames)
        {
            ProcessIndex processIndex = (ProcessIndex)deserializer.ReadInt();
            ulong osThreadId = (ulong)deserializer.ReadInt64();
            ulong dispatcherId = (ulong)deserializer.ReadInt64();
            int depth = deserializer.ReadInt();
            var framesIndex = (AsyncProfilerCallStackFramesIndex)deserializer.ReadInt();
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

            return new AsyncProfilerCallStack(
                new AsyncProfilerThreadKey(processIndex, osThreadId), dispatcherId,
                depth, framesIndex, resolveFrames(framesIndex),
                continuationIndexBase, wrapperCount, startQpc, endQpc,
                methodCompletions, exceptionCompletions, wrapperResets);
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
    /// <see cref="AsyncProfilerCallStacksIndex"/> (see <see cref="Index"/>).
    /// <para>
    /// Each process instance owns its manifest and clock state. A thread is ignored until usable metadata and a
    /// <c>ResetAsyncThreadContext</c> at or after that metadata's first QPC have been received. An armed reset
    /// commits open activations at the reset timestamp before clearing that thread's nesting stack.
    /// Async call stacks nest on a thread: a resume callstack pushes an activation, an append callstack
    /// extends the top one, and a suspend/complete context pops it. In V1 the frames are assembled
    /// incrementally (resume + appends) and are only final at close, so frames are <b>finalized and
    /// interned at pop</b>.
    /// </para>
    /// </summary>
    internal sealed class AsyncProfilerComputer : ISubEventSink, ICallstackPayloadSink
    {
        // Resume callstacks push a new async call stack onto the thread's nesting stack.
        private static bool IsResumeCallstack(AsyncEventID id) =>
            id == AsyncEventID.ResumeRuntimeAsyncCallstack || id == AsyncEventID.ResumeStateMachineAsyncCallstack;

        // Append callstacks extend the current (top) async call stack's in-progress frames.
        private static bool IsAppendCallstack(AsyncEventID id) =>
            id == AsyncEventID.AppendStateMachineAsyncCallstack;

        private readonly AsyncProfilerTraceEventParser _parser;
        private readonly Func<AsyncProfilerTraceEvent, ProcessIndex> _processIndexOf;
        private readonly AsyncProfilerCallStacksIndex _index = new AsyncProfilerCallStacksIndex();
        private readonly Dictionary<AsyncProfilerThreadKey, CallStacks> _threads = new Dictionary<AsyncProfilerThreadKey, CallStacks>();
        private readonly Dictionary<ProcessIndex, ProcessState> _processes = new Dictionary<ProcessIndex, ProcessState>();
        private readonly Stack<CallStackBuilder> _builderPool = new Stack<CallStackBuilder>();
        private readonly ulong[] _callstackMethodIds = new ulong[byte.MaxValue];
        private readonly int[] _callstackFrameStates = new int[byte.MaxValue];

        private ProcessIndex _currentProcessIndex;

        /// <summary>
        /// Fired for each nonzero frame methodId in an accepted resume/append callstack (during the event stream,
        /// before end-of-trace rundown), letting the host pre-register the frame's code address so a covering
        /// method load/rundown binds it. This makes frames resolvable even for async call stacks that are only
        /// committed later at <see cref="Finish"/> (still live at capture end). Arguments: processIndex,
        /// native code IP (for both V1 and V2), instrumentation kind.
        /// </summary>
        public Action<ProcessIndex, ulong, AsyncCallstackKind> OnFrameObserved;

        /// <summary>
        /// Reports each decode failure and its process instance after invalidating uncertain activation state.
        /// </summary>
        public event Action<ProcessIndex, ParseError> ParseError;

        /// <summary>
        /// Binds the computer to a live parser: it resolves each carrying event to a process instance, decodes the
        /// raw <c>AsyncEvents</c> buffer using that process's manifest, and feeds the sub-events into the index.
        /// </summary>
        internal AsyncProfilerComputer(AsyncProfilerTraceEventParser parser, Func<AsyncProfilerTraceEvent, ProcessIndex> processIndexOf)
        {
            _parser = parser ?? throw new ArgumentNullException(nameof(parser));
            _processIndexOf = processIndexOf ?? throw new ArgumentNullException(nameof(processIndexOf));
            _parser.AsyncEvents += OnRawTraceEvent;
        }

        /// <summary>
        /// Creates a computer with no parser binding, for unit tests or callers that drive the decoder
        /// directly via <see cref="Process(byte[])"/>.
        /// </summary>
        public AsyncProfilerComputer()
        {
        }

        /// <summary>
        /// The queryable, serializable index of finalized activations and interned frames. Call <see cref="Finish"/>
        /// after processing the stream to include activations that have not yet suspended or completed.
        /// </summary>
        public AsyncProfilerCallStacksIndex Index => _index;

        /// <summary>True while usable metadata provides the QPC frequency for the default process instance (index 0).</summary>
        public bool ClockKnown => ClockKnownForProcess(0);

        /// <summary>The wrapper pool size for the default process instance (index 0), or 0 without usable metadata.</summary>
        public byte WrapperCount => GetWrapperCount(0);

        /// <summary>The number of distinct interned frame lists (useful for asserting dedup).</summary>
        public int DistinctFramesCount => _index.DistinctFramesCount;

        /// <summary>The number of decode failures reported by this computer.</summary>
        public long ParseErrorCount { get; private set; }

        /// <summary>
        /// Decodes an async-profiler buffer for the default process instance (index 0). For multiple processes,
        /// use <see cref="Process(byte[], ProcessIndex)"/> so their manifests and activation state remain separate.
        /// </summary>
        public void Process(byte[] buffer)
        {
            Process(buffer, 0);
        }

        /// <summary>Decodes an async-profiler buffer for one process instance into the index.</summary>
        /// <param name="buffer">The raw buffer from an <c>AsyncEvents</c> event.</param>
        /// <param name="processIndex">The unique process instance in the trace, not its reusable OS process id.</param>
        /// <remarks>
        /// Processing is synchronous; reuse the same computer and process index for that process's buffers.
        /// On a decode failure, open activations on the affected thread are closed at the failing event's QPC,
        /// or the last decoded QPC if its timestamp is unavailable, and the thread remains unarmed until a valid
        /// reset. An unreadable header discards open activations throughout the process; malformed metadata
        /// closes all of that process's open prefixes where the error QPC is known.
        /// Unreadable headers and malformed metadata require fresh metadata before
        /// resets can re-arm that process. Previously finalized intervals and other processes are retained.
        /// Failures are exposed by <see cref="ParseError"/> and <see cref="ParseErrorCount"/>.
        /// </remarks>
        public void Process(byte[] buffer, ProcessIndex processIndex)
        {
            ProcessState process = GetOrCreateProcess(processIndex);
            _currentProcessIndex = processIndex;
            AsyncProfilerTraceEventParser.ParseBuffer(buffer, process.Manifest, this);
        }

        /// <summary>Resolves an interned frames handle in this computer's index; null when out of range.</summary>
        public AsyncProfilerCallStackFrames GetFrames(AsyncProfilerCallStackFramesIndex index) => _index.GetFrames(index);

        /// <summary>
        /// Returns the nested async call stacks active on <paramref name="thread"/> at <paramref name="qpc"/>,
        /// ordered bottom-to-top (ascending depth). Empty if the thread was unarmed or idle at that instant.
        /// </summary>
        /// <remarks>Returns finalized intervals only, with stable activation objects that later queries do not overwrite.</remarks>
        public IReadOnlyList<AsyncProfilerCallStack> GetCallStacks(AsyncProfilerThreadKey thread, long qpc) => _index.GetCallStacks(thread, qpc);

        /// <summary>Converts QPC to UTC for the default process instance (index 0); null without usable metadata.</summary>
        public DateTime? QpcToDateTime(long qpc)
        {
            return QpcToDateTime(0, qpc);
        }

        /// <summary>True while usable metadata provides the QPC frequency for <paramref name="processIndex"/>.</summary>
        public bool ClockKnownForProcess(ProcessIndex processIndex) =>
            _processes.TryGetValue(processIndex, out ProcessState process) && process.QpcFrequency != 0;

        /// <summary>The wrapper pool size for <paramref name="processIndex"/>, or 0 without usable metadata.</summary>
        public byte GetWrapperCount(ProcessIndex processIndex) =>
            _processes.TryGetValue(processIndex, out ProcessState process) ? process.WrapperCount : (byte)0;

        /// <summary>
        /// Converts an absolute QPC timestamp to UTC using the current clock synchronization for
        /// <paramref name="processIndex"/>; returns null when that process has no usable metadata.
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

        void ISubEventSink.OnContextCreate(in AsyncContextEvent e)
        {
            AsyncProfilerThreadKey creationThread = ThreadKeyOf(e.OsThreadId);
            CallStacks state = GetOrCreate(creationThread);
            if (state.Armed)
            {
                state.RecordDispatcherCreation(
                    _index, creationThread, e.DispatcherId, e.ParentDispatcherId, e.TimestampQpc);
            }
        }

        void ISubEventSink.OnContextResume(in AsyncContextEvent e) { /* the async call stack is pushed by the resume callstack, which carries the frames */ }

        void ISubEventSink.OnContextSuspend(in AsyncContextEvent e) => CloseTop(ThreadKeyOf(e.OsThreadId), e.TimestampQpc);

        void ISubEventSink.OnContextComplete(in AsyncContextEvent e) => CloseTop(ThreadKeyOf(e.OsThreadId), e.TimestampQpc);

        void ISubEventSink.OnCallstack(in AsyncCallstackEvent e) => ProcessCallstack(e, reusablePayload: false);

        bool ICallstackPayloadSink.TryOnCallstack(AsyncEventID eventId, long timestampQpc,
            in BufferHeader header, byte[] buffer, ref int index, int payloadEnd)
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
            AsyncProfilerThreadKey thread = ThreadKeyOf(e.OsThreadId);
            CallStacks state = GetOrCreate(thread);
            if (!state.Armed)
            {
                return;
            }

            if (e.EventId == AsyncEventID.CreateRuntimeAsyncCallstack)
            {
                state.RecordDispatcherCreation(
                    _index, thread, e.DispatcherId, e.ParentDispatcherId, e.TimestampQpc);
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
                CallStackBuilder top = state.Top;
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

        void ISubEventSink.OnMethodResume(in AsyncMethodEvent e) { /* a method began running; no frame change */ }

        void ISubEventSink.OnMethodComplete(in AsyncMethodEvent e)
        {
            AsyncProfilerThreadKey key = ThreadKeyOf(e.OsThreadId);
            CallStacks state = GetOrCreate(key);
            if (!state.Armed)
            {
                return;
            }

            AsyncCallstackKind kind = e.IsStateMachine ? AsyncCallstackKind.StateMachineAsync : AsyncCallstackKind.RuntimeAsync;
            _index.MarkMethodCompletionObserved(_currentProcessIndex, kind, e.TimestampQpc);
            state.Top?.MethodCompletions.Add(new AsyncProfilerCallStack.CompletionDelta(e.TimestampQpc, 1));
        }

        void ISubEventSink.OnException(in AsyncUnwindEvent e)
        {
            AsyncProfilerThreadKey key = ThreadKeyOf(e.OsThreadId);
            CallStacks state = GetOrCreate(key);
            if (!state.Armed)
            {
                return;
            }

            AsyncCallstackKind kind = e.IsStateMachine ? AsyncCallstackKind.StateMachineAsync : AsyncCallstackKind.RuntimeAsync;
            _index.MarkExceptionCompletionObserved(_currentProcessIndex, kind, e.TimestampQpc);
            if (e.UnwoundFrameCount > 0)
            {
                state.Top?.ExceptionCompletions.Add(new AsyncProfilerCallStack.CompletionDelta(e.TimestampQpc, (int)e.UnwoundFrameCount));
            }
        }

        void ISubEventSink.OnResetThreadContext(in AsyncNeutralEvent e)
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
            AsyncProfilerThreadKey key = ThreadKeyOf(e.OsThreadId);
            CallStacks state = GetOrCreate(key);
            if (state.Armed)
            {
                CommitOpenActivations(key, state, e.TimestampQpc);
            }
            state.Armed = true;
            state.ClearNesting();
        }

        void ISubEventSink.OnResetContinuationWrapperIndex(in AsyncNeutralEvent e)
        {
            // Wrapper-index resets are scoped to the active (top) async call stack, like Complete/Unwind.
            CallStacks state = GetOrCreate(ThreadKeyOf(e.OsThreadId));
            if (state.Armed)
            {
                state.Top?.WrapperResets.Add(e.TimestampQpc);
            }
        }

        void ISubEventSink.OnMetadata(in AsyncMetadataEvent e)
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

        void ISubEventSink.OnSyncClock(in AsyncSyncClockEvent e)
        {
            ProcessState process = CurrentProcess;
            process.QpcSync = e.QpcSync;
            process.UtcSync = e.UtcSync;
        }

        void ISubEventSink.OnUnknown(in AsyncUnknownEvent e) { }

        void ISubEventSink.OnParseError(in ParseError e)
        {
            ParseErrorCount++;
            bool processWide = !e.BufferHeader.HasValue || e.EventId == AsyncEventID.AsyncProfilerMetadata;
            if (processWide)
            {
                foreach (KeyValuePair<AsyncProfilerThreadKey, CallStacks> thread in _threads)
                {
                    if (thread.Key.ProcessIndex == _currentProcessIndex)
                    {
                        InvalidateThread(thread.Key, thread.Value, e.TimestampQpc);
                    }
                }
                ProcessState process = CurrentProcess;
                process.FirstMetadataQpc = long.MaxValue;
                process.QpcFrequency = 0;
                process.QpcSync = 0;
                process.UtcSync = 0;
                process.WrapperCount = 0;
            }
            else
            {
                AsyncProfilerThreadKey key = ThreadKeyOf(e.BufferHeader.Value.OsThreadId);
                if (_threads.TryGetValue(key, out CallStacks state))
                {
                    InvalidateThread(key, state, e.TimestampQpc);
                }
            }
            ParseError?.Invoke(_currentProcessIndex, e);
        }

        #endregion

        #region private

        private void OnRawTraceEvent(AsyncProfilerTraceEvent data)
        {
            Process(data.Buffer, _processIndexOf(data));
        }

        private AsyncProfilerThreadKey ThreadKeyOf(ulong osThreadId) =>
            new AsyncProfilerThreadKey(_currentProcessIndex, osThreadId);

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

        private CallStacks GetOrCreate(AsyncProfilerThreadKey key)
        {
            if (!_threads.TryGetValue(key, out CallStacks state))
            {
                state = new CallStacks();
                _threads[key] = state;
            }
            return state;
        }

        private void InvalidateThread(AsyncProfilerThreadKey key, CallStacks state, long? endQpc)
        {
            state.Armed = false;
            while (state.Top != null)
            {
                CallStackBuilder builder = state.Pop();
                if (endQpc.HasValue && endQpc.Value > builder.StartQpc)
                {
                    Commit(key, builder, endQpc.Value);
                }
                else
                {
                    builder.Clear();
                    _builderPool.Push(builder);
                }
            }
        }

        private void CloseTop(AsyncProfilerThreadKey key, long qpc)
        {
            CallStacks state = GetOrCreate(key);
            if (!state.Armed || state.Top == null)
            {
                return;
            }

            CallStackBuilder builder = state.Pop();
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
            foreach (KeyValuePair<AsyncProfilerThreadKey, CallStacks> kv in _threads)
            {
                CommitOpenActivations(kv.Key, kv.Value, long.MaxValue);
            }
        }

        /// <summary>
        /// Pops and records every in-progress async call stack still open on <paramref name="state"/>, closing each
        /// at <paramref name="endQpc"/>. Used both at end of stream (<see cref="Finish"/>, endQpc = long.MaxValue)
        /// and on an armed mid-session reset (endQpc = the reset timestamp).
        /// </summary>
        private void CommitOpenActivations(AsyncProfilerThreadKey key, CallStacks state, long endQpc)
        {
            while (state.Top != null)
            {
                CallStackBuilder builder = state.Pop();
                Commit(key, builder, endQpc);
            }
        }

        private void Commit(AsyncProfilerThreadKey key, CallStackBuilder builder, long endQpc)
        {
            AsyncProfilerCallStack.CompletionDelta[] methodCompletions = builder.GetMethodCompletions();
            AsyncProfilerCallStack.CompletionDelta[] exceptionCompletions = builder.GetExceptionCompletions();
            long[] wrapperResets = builder.GetWrapperResets();

            try
            {
                if (builder.HasInternedFrames)
                {
                    _index.Add(key, builder.FramesIndex, builder.Frames,
                        builder.DispatcherId, builder.Depth, builder.ContinuationIndexBase,
                        builder.WrapperCount, builder.StartQpc, endQpc,
                        methodCompletions, exceptionCompletions, wrapperResets);
                }
                else
                {
                    _index.Add(key, builder.Kind, builder.GetMethodIds(), builder.GetFrameStates(),
                        builder.DispatcherId, builder.Depth, builder.ContinuationIndexBase,
                        builder.WrapperCount, builder.StartQpc, endQpc,
                        methodCompletions, exceptionCompletions, wrapperResets);
                }
            }
            finally
            {
                builder.Clear();
                _builderPool.Push(builder);
            }
        }

        private CallStackBuilder RentBuilder(in AsyncCallstackEvent e, byte wrapperCount, bool reusablePayload)
        {
            CallStackBuilder builder = _builderPool.Count != 0
                ? _builderPool.Pop()
                : new CallStackBuilder();

            if (reusablePayload && _index.TryGetInternedFrames(
                _currentProcessIndex, e.Kind, e.MethodIds, e.FrameStates, e.FrameCount,
                out AsyncProfilerCallStackFramesIndex framesIndex, out AsyncProfilerCallStackFrames frames))
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
        private sealed class CallStacks
        {
            public bool Armed;
            private readonly List<CallStackBuilder> _nesting = new List<CallStackBuilder>(); // top = last

            public CallStackBuilder Top => _nesting.Count > 0 ? _nesting[_nesting.Count - 1] : null;

            public void Push(CallStackBuilder builder)
            {
                builder.Depth = _nesting.Count;
                _nesting.Add(builder);
            }

            public CallStackBuilder Pop()
            {
                CallStackBuilder top = _nesting[_nesting.Count - 1];
                _nesting.RemoveAt(_nesting.Count - 1);
                return top;
            }

            public void RecordDispatcherCreation(
                AsyncProfilerCallStacksIndex index,
                AsyncProfilerThreadKey thread,
                ulong dispatcherId,
                ulong parentDispatcherId,
                long creationQpc)
            {
                // Both V2 creation forms describe the same creation at the same QPC, possibly in separate buffers.
                if (_lastCreatedDispatcherId == dispatcherId &&
                    _lastCreatedParentDispatcherId == parentDispatcherId &&
                    _lastCreationQpc == creationQpc)
                {
                    return;
                }

                index.AddDispatcherInfo(thread, dispatcherId, parentDispatcherId, creationQpc);
                _lastCreatedDispatcherId = dispatcherId;
                _lastCreatedParentDispatcherId = parentDispatcherId;
                _lastCreationQpc = creationQpc;
            }

            public void ClearNesting() => _nesting.Clear();

            private ulong _lastCreatedDispatcherId;
            private ulong _lastCreatedParentDispatcherId;
            private long _lastCreationQpc;
        }

        private sealed class ProcessState
        {
            public readonly Manifest Manifest = new Manifest();
            public long FirstMetadataQpc = long.MaxValue;
            public ulong QpcFrequency;
            public ulong QpcSync;
            public ulong UtcSync;
            public byte WrapperCount;
        }

        /// <summary>An in-progress async call stack being assembled on a thread's nesting stack.</summary>
        private sealed class CallStackBuilder
        {
            public ulong DispatcherId;
            public long StartQpc;
            public byte ContinuationIndexBase;
            public byte WrapperCount;
            public AsyncCallstackKind Kind;
            public int Depth;
            public AsyncProfilerCallStackFramesIndex FramesIndex;
            public AsyncProfilerCallStackFrames Frames;

            public bool HasInternedFrames => Frames != null;

            public List<AsyncProfilerCallStack.CompletionDelta> MethodCompletions =>
                _methodCompletions ?? (_methodCompletions = new List<AsyncProfilerCallStack.CompletionDelta>());

            public List<AsyncProfilerCallStack.CompletionDelta> ExceptionCompletions =>
                _exceptionCompletions ?? (_exceptionCompletions = new List<AsyncProfilerCallStack.CompletionDelta>());

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
                AsyncProfilerCallStackFramesIndex framesIndex, AsyncProfilerCallStackFrames frames)
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

                    FramesIndex = AsyncProfilerCallStackFramesIndex.Invalid;
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

            public AsyncProfilerCallStack.CompletionDelta[] GetMethodCompletions() =>
                _methodCompletions?.ToArray() ?? Array.Empty<AsyncProfilerCallStack.CompletionDelta>();

            public AsyncProfilerCallStack.CompletionDelta[] GetExceptionCompletions() =>
                _exceptionCompletions?.ToArray() ?? Array.Empty<AsyncProfilerCallStack.CompletionDelta>();

            public long[] GetWrapperResets() => _wrapperResets?.ToArray() ?? Array.Empty<long>();

            public void Clear()
            {
                DispatcherId = 0;
                StartQpc = 0;
                ContinuationIndexBase = 0;
                WrapperCount = 0;
                Kind = default;
                Depth = 0;
                FramesIndex = AsyncProfilerCallStackFramesIndex.Invalid;
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
                FramesIndex = AsyncProfilerCallStackFramesIndex.Invalid;
                Frames = null;
            }

            private ulong[] _methodIds;
            private int[] _frameStates;
            private List<ulong> _methodIdsBuilder;
            private List<int> _frameStatesBuilder;
            private List<AsyncProfilerCallStack.CompletionDelta> _methodCompletions;
            private List<AsyncProfilerCallStack.CompletionDelta> _exceptionCompletions;
            private List<long> _wrapperResets;
        }

        #endregion
    }
}
