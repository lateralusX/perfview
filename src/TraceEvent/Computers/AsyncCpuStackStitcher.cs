// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>Where a <see cref="StitchedFrame"/> in a stitched call stack came from.</summary>
    public enum StitchedFrameOrigin : byte
    {
        /// <summary>A real frame taken verbatim from the native (CPU-sample) sync stack.</summary>
        Sync = 0,

        /// <summary>The physically present current async frame matched to an active async-profiler segment. It
        /// retains the sampled native code address while carrying the segment's logical async identity.</summary>
        AsyncCurrent,

        /// <summary>A suspended-ancestry frame spliced in from an async call stack segment (an async frame that
        /// is <b>not</b> physically present on the native sync stack because it is awaiting).</summary>
        AsyncRemaining,

        /// <summary>A historical parent-context frame reached through a dispatcher creation relationship.</summary>
        AsyncContextParent,

        /// <summary>
        /// The indexed target of a verified dispatcher, V1 box carrier, or sampled V2 wrapper when no separate physical
        /// application body was matched. This identifies resumption context, not whether the body has started
        /// or finished; any V1 compiler-state presentation remains captured.
        /// </summary>
        AsyncDispatchTarget,

        /// <summary>
        /// A physically present V1 method whose async operation has completed according to completion records,
        /// but whose invocation is still executing completion machinery. Retains the sampled code address.
        /// </summary>
        AsyncCompleting,

        /// <summary>
        /// The unavailable physical root of a uniquely recovered async activation. Rendered as <c>BROKEN</c>;
        /// it has no code address or async-method identity and does not imply a particular execution host.
        /// </summary>
        IncompleteRoot,
    }

    /// <summary>The async-context membership of a <see cref="StitchedFrame"/>.</summary>
    public enum StitchedFrameContextKind
    {
        /// <summary>The frame does not belong to an async context.</summary>
        None = 0,

        /// <summary>The frame belongs to an async context active on the sampled execution stack.</summary>
        Active,

        /// <summary>The frame belongs to a historical creation-parent context.</summary>
        HistoricalParent,
    }

    /// <summary>How a <see cref="StitchedFrame"/> should be presented when it is interned.</summary>
    public enum StitchedFramePresentation : byte
    {
        /// <summary>Use the frame's native symbol without presentation changes.</summary>
        Native = 0,

        /// <summary>
        /// Present a compiler-generated V1 state-machine <c>MoveNext</c> frame as its logical source async method.
        /// The original code address remains attached to the frame for source and identity correlation.
        /// </summary>
        LogicalStateMachineMethod,
    }

    /// <summary>
    /// Additional classification carried by a native sync frame for completion counting and presentation.
    /// Includes V1 startup machinery and CoreLib infrastructure shared by V1 and V2; these are not dispatch boundaries.
    /// </summary>
    public enum StitchSyncFrameKind
    {
        /// <summary>An ordinary native sync frame.</summary>
        None = 0,

        /// <summary>A compiler-generated async state-machine <c>MoveNext</c> frame.</summary>
        V1StateMachineMoveNext,

        /// <summary>A known CoreLib async method-builder <c>Start</c> frame.</summary>
        V1MethodBuilderStart,

        /// <summary>
        /// A known CoreLib async method-builder <c>AwaitUnsafeOnCompleted</c> registration frame. This is runtime
        /// implementation machinery; user awaiter and custom-builder methods with similar names are not classified.
        /// </summary>
        V1MethodBuilderAwaitUnsafeOnCompleted,

        /// <summary>
        /// Another method declared on a known CoreLib async method-builder type, such as
        /// <c>GetStateMachineBox</c>, <c>SetException</c>, or <c>SetResult</c>. These are runtime implementation
        /// frames and are only removed by the opt-in async-infrastructure cleanup transform.
        /// </summary>
        V1MethodBuilderInfrastructure,

        /// <summary>
        /// A registration method on a known CoreLib awaiter type, such as <c>OnCompleted</c>,
        /// <c>UnsafeOnCompleted</c>, or the state-machine-box-aware <c>AwaitUnsafeOnCompleted</c>. User awaiters
        /// and result-consumption methods such as <c>GetResult</c> are not classified.
        /// </summary>
        V1AwaiterRegistrationInfrastructure,

        /// <summary>
        /// Exact CoreLib Task-continuation or execution-context plumbing that connects physical async dispatch,
        /// such as <c>AwaitTaskContinuation.RunOrScheduleAction</c> or <c>ExecutionContext.RunInternal</c>. These frames are retained
        /// structurally. They are removed by the opt-in async-infrastructure cleanup transform, or as part of a
        /// default all-CoreLib handoff span between exactly matched nested RuntimeAsync activations.
        /// </summary>
        SystemPrivateCoreLibAsyncBridgeInfrastructure,

        /// <summary><c>Task.RunContinuations</c>, distinguished for exact opt-in completion-chain cleanup.</summary>
        SystemPrivateCoreLibTaskRunContinuations,

        /// <summary><c>Task.TrySetResult</c> or <c>Task&lt;T&gt;.TrySetResult</c>, distinguished for exact opt-in completion-chain cleanup.</summary>
        SystemPrivateCoreLibTaskTrySetResult,

        /// <summary>
        /// <c>TaskCompletionSource.SetResult</c> or <c>TaskCompletionSource&lt;T&gt;.SetResult</c>.
        /// Retained by per-frame cleanup; removed only as part of a proven default all-CoreLib handoff span or
        /// an exact adjacent task-completion chain or verified resumption bridge recognized by opt-in cleanup.
        /// </summary>
        SystemPrivateCoreLibTaskCompletionSourceSetResult,

        /// <summary>
        /// A known CoreLib method-builder frame that completes the currently executing V1 state machine's own
        /// result. When it is directly leaf-ward of the final matched <c>MoveNext</c>, that frame has completed
        /// rather than merely remaining as the caller of a nested async segment.
        /// </summary>
        V1MethodBuilderCompletion,

        /// <summary>The CoreLib thread-pool dispatch method, retained as the root of a runtime resumption bridge.</summary>
        SystemPrivateCoreLibThreadPoolDispatch,

        /// <summary>CoreLib state-machine callstack capture, identifying a candidate profiler-owned prefix.</summary>
        AsyncProfilerCallstackCapture,

        /// <summary>CoreLib profiler callstack emission, identifying a candidate profiler-owned prefix.</summary>
        AsyncProfilerCallstackEmission,

        /// <summary>CoreLib profiler context resumption, anchoring optional profiler-prefix ownership attribution.</summary>
        AsyncProfilerContextResume,

        /// <summary>Known profiler diagnostics, initialization, reset, or box helpers; not required for prefix ownership.</summary>
        AsyncProfilerInfrastructure,

        /// <summary>
        /// CoreLib <c>AsyncStateMachineDispatcherInfo.CreateDispatcher</c> setup, removed only by optional
        /// async-infrastructure cleanup. This is not an async splice boundary.
        /// </summary>
        V1DispatcherCreationInfrastructure,
    }

    /// <summary>
    /// One frame of a native (CPU-sample) call stack, as consumed by <see cref="AsyncCpuStackStitcher"/>.
    /// Carries both the <see cref="CodeAddressIndex"/> (for interning the frame into the output stack source)
    /// and the <see cref="MethodIndex"/> (for completion counting and current-frame identity matching) so the stitcher does no
    /// symbol lookups of its own.
    /// </summary>
    public readonly struct StitchSyncFrame
    {
        /// <summary>The frame's code address (used to intern it and to classify it as an async boundary).</summary>
        public readonly CodeAddressIndex CodeAddress;

        /// <summary>The resolved method used for V1/V2 identity matching, or <see cref="MethodIndex.Invalid"/>.</summary>
        public readonly MethodIndex Method;

        /// <summary>Optional async-related synchronous-frame classification.</summary>
        public readonly StitchSyncFrameKind Kind;

        /// <summary>
        /// The uniquely resolved state-machine method carried by a CoreLib box execution frame, or
        /// <see cref="MethodIndex.Invalid"/>. Only the leafmost execution carrier preceding any physical
        /// async application body can use this identity for V1 alignment.
        /// </summary>
        public readonly MethodIndex BoxedStateMachineMethod;

        public StitchSyncFrame(CodeAddressIndex codeAddress, MethodIndex method)
            : this(codeAddress, method, StitchSyncFrameKind.None)
        {
        }

        public StitchSyncFrame(CodeAddressIndex codeAddress, MethodIndex method, StitchSyncFrameKind kind)
            : this(codeAddress, method, kind, MethodIndex.Invalid)
        {
        }

        /// <summary>Creates a native frame with an optional uniquely resolved boxed state-machine method.</summary>
        public StitchSyncFrame(
            CodeAddressIndex codeAddress, MethodIndex method, StitchSyncFrameKind kind,
            MethodIndex boxedStateMachineMethod)
        {
            CodeAddress = codeAddress;
            Method = method;
            Kind = kind;
            BoxedStateMachineMethod = boxedStateMachineMethod;
        }
    }

    /// <summary>
    /// One frame of a stitched call stack: a real sync frame, an async-ancestry frame spliced in for a
    /// suspended await, or an explicit incomplete physical root. For an async frame, <see cref="CodeAddress"/> may be
    /// <see cref="CodeAddressIndex.Invalid"/> (unsymbolized or a synthetic <c>0</c>-IP async frame), in which
    /// case the emitter applies the async leaf policy using <see cref="Segment"/> / <see cref="SegmentFrameIndex"/>.
    /// </summary>
    public readonly struct StitchedFrame
    {
        /// <summary>Where this frame came from.</summary>
        public readonly StitchedFrameOrigin Origin;

        /// <summary>The registered code address, or <see cref="CodeAddressIndex.Invalid"/> when unavailable.
        /// A valid index does not imply the method is symbolized.</summary>
        public readonly CodeAddressIndex CodeAddress;

        /// <summary>
        /// The canonical managed method for a physically present frame, or <see cref="MethodIndex.Invalid"/> when
        /// unavailable. This preserves method identity for conservative transforms without requiring another symbol
        /// lookup.
        /// </summary>
        public readonly MethodIndex Method;

        /// <summary>
        /// The canonical application method represented by a matched V1 box carrier, or
        /// <see cref="MethodIndex.Invalid"/>. <see cref="Method"/> and <see cref="CodeAddress"/> retain the
        /// sampled runtime helper's physical identity.
        /// </summary>
        public readonly MethodIndex BoxedStateMachineMethod;

        /// <summary>True when a V1 resumption target was matched through a physical box execution frame.
        /// This does not establish application-body execution.</summary>
        public bool IsStateMachineBoxCarrier =>
            (Origin == StitchedFrameOrigin.AsyncCurrent || Origin == StitchedFrameOrigin.AsyncDispatchTarget) &&
            BoxedStateMachineMethod != MethodIndex.Invalid;

        /// <summary>For an async-origin frame, the source async segment; otherwise null.</summary>
        public readonly AsyncCallStackFrames Segment;

        /// <summary>For an async-origin frame, its index within <see cref="Segment"/>; otherwise -1.</summary>
        public readonly int SegmentFrameIndex;

        /// <summary>For a sync-origin frame, its optional async-related synchronous-frame classification.</summary>
        public readonly StitchSyncFrameKind SyncFrameKind;

        /// <summary>The presentation requested for this frame.</summary>
        public readonly StitchedFramePresentation Presentation;

        /// <summary>The async activation to which this frame belongs, or null for physical root frames.</summary>
        public readonly AsyncCallStack Context;

        /// <summary>How <see cref="Context"/> participates in the stitched stack.</summary>
        public StitchedFrameContextKind ContextKind =>
            Context == null
                ? StitchedFrameContextKind.None
                : (Origin == StitchedFrameOrigin.AsyncContextParent
                    ? StitchedFrameContextKind.HistoricalParent
                    : StitchedFrameContextKind.Active);

        private StitchedFrame(
            StitchedFrameOrigin origin,
            CodeAddressIndex codeAddress,
            MethodIndex method,
            AsyncCallStackFrames segment,
            int segmentFrameIndex,
            StitchSyncFrameKind syncFrameKind,
            StitchedFramePresentation presentation,
            AsyncCallStack context,
            MethodIndex boxedStateMachineMethod = MethodIndex.Invalid)
        {
            Origin = origin;
            CodeAddress = codeAddress;
            Method = method;
            BoxedStateMachineMethod = boxedStateMachineMethod;
            Segment = segment;
            SegmentFrameIndex = segmentFrameIndex;
            SyncFrameKind = syncFrameKind;
            Presentation = presentation;
            Context = context;
        }

        /// <summary>Creates a stitched frame that preserves a native sync frame.</summary>
        public static StitchedFrame CreateSync(
            StitchSyncFrame frame,
            AsyncCallStack context) =>
            new StitchedFrame(
                StitchedFrameOrigin.Sync, frame.CodeAddress, frame.Method, null, -1, frame.Kind,
                StitchedFramePresentation.Native, context);

        /// <summary>
        /// Creates a physically present current frame with its logical async identity. Supplying a V1 boxed
        /// state-machine identity instead creates a resumption target, retaining the helper's sampled address
        /// and captured compiler state without claiming application-body execution.
        /// </summary>
        public static StitchedFrame CreateAsyncCurrent(
            CodeAddressIndex codeAddress,
            MethodIndex method,
            AsyncCallStack context,
            int segmentFrameIndex,
            MethodIndex boxedStateMachineMethod = MethodIndex.Invalid) =>
            new StitchedFrame(
                boxedStateMachineMethod == MethodIndex.Invalid
                    ? StitchedFrameOrigin.AsyncCurrent : StitchedFrameOrigin.AsyncDispatchTarget,
                codeAddress, method, context.Frames, segmentFrameIndex,
                StitchSyncFrameKind.None,
                context.Frames.Kind == AsyncCallstackKind.StateMachineAsync
                    ? StitchedFramePresentation.LogicalStateMachineMethod
                    : StitchedFramePresentation.Native,
                context, boxedStateMachineMethod);

        /// <summary>Creates a physically present V1 frame verified as logically completed.</summary>
        public static StitchedFrame CreateAsyncCompleting(
            CodeAddressIndex codeAddress,
            MethodIndex method,
            AsyncCallStack context,
            int segmentFrameIndex) =>
            new StitchedFrame(
                StitchedFrameOrigin.AsyncCompleting, codeAddress, method, context.Frames, segmentFrameIndex,
                StitchSyncFrameKind.None, StitchedFramePresentation.LogicalStateMachineMethod, context);

        /// <summary>Creates a suspended-ancestry frame from an async call-stack segment.</summary>
        public static StitchedFrame CreateAsync(AsyncCallStack context, int segmentFrameIndex) =>
            new StitchedFrame(
                StitchedFrameOrigin.AsyncRemaining,
                context.Frames.CodeAddressAt(segmentFrameIndex),
                MethodIndex.Invalid,
                context.Frames,
                segmentFrameIndex,
                StitchSyncFrameKind.None,
                context.Frames.Kind == AsyncCallstackKind.StateMachineAsync
                    ? StitchedFramePresentation.LogicalStateMachineMethod
                    : StitchedFramePresentation.Native,
                context);

        /// <summary>Creates a historical parent-context frame reached through dispatcher creation ancestry.</summary>
        public static StitchedFrame CreateAsyncContextParent(
            AsyncCallStack context, int segmentFrameIndex) =>
            new StitchedFrame(
                StitchedFrameOrigin.AsyncContextParent,
                context.Frames.CodeAddressAt(segmentFrameIndex),
                MethodIndex.Invalid,
                context.Frames,
                segmentFrameIndex,
                StitchSyncFrameKind.None,
                context.Frames.Kind == AsyncCallstackKind.StateMachineAsync
                    ? StitchedFramePresentation.LogicalStateMachineMethod
                    : StitchedFramePresentation.Native,
                context);

        /// <summary>Creates an indexed dispatch target without claiming a physically matched current method.</summary>
        public static StitchedFrame CreateAsyncDispatchTarget(AsyncCallStack context, int segmentFrameIndex) =>
            new StitchedFrame(
                StitchedFrameOrigin.AsyncDispatchTarget,
                context.Frames.CodeAddressAt(segmentFrameIndex),
                MethodIndex.Invalid,
                context.Frames,
                segmentFrameIndex,
                StitchSyncFrameKind.None,
                context.Frames.Kind == AsyncCallstackKind.StateMachineAsync
                    ? StitchedFramePresentation.LogicalStateMachineMethod
                    : StitchedFramePresentation.Native,
                context);

        /// <summary>Creates a physically present V1 box resumption target, retaining its captured compiler state
        /// without claiming that the application body has started.</summary>
        public static StitchedFrame CreateAsyncDispatchTarget(
            StitchSyncFrame carrier, AsyncCallStack context, int segmentFrameIndex) =>
            new StitchedFrame(
                StitchedFrameOrigin.AsyncDispatchTarget, carrier.CodeAddress, carrier.Method,
                context.Frames, segmentFrameIndex, StitchSyncFrameKind.None,
                StitchedFramePresentation.LogicalStateMachineMethod, context, carrier.BoxedStateMachineMethod);

        /// <summary>Creates an incomplete physical-root marker without inventing a caller or execution host.</summary>
        public static StitchedFrame CreateIncompleteRoot() =>
            new StitchedFrame(
                StitchedFrameOrigin.IncompleteRoot, CodeAddressIndex.Invalid, MethodIndex.Invalid, null, -1,
                StitchSyncFrameKind.None, StitchedFramePresentation.Native, null);

        /// <summary>Returns this frame with a different presentation while preserving its structural identity.</summary>
        public StitchedFrame WithPresentation(StitchedFramePresentation presentation) =>
            new StitchedFrame(
                Origin, CodeAddress, Method, Segment, SegmentFrameIndex, SyncFrameKind, presentation,
                Context, BoxedStateMachineMethod);
    }

    /// <summary>The complete leaf-to-root output span emitted for one active async activation.</summary>
    internal readonly struct AsyncSegmentPlacement
    {
        internal AsyncSegmentPlacement(
            AsyncCallStack activation, int leafIndex, int rootIndexExclusive)
        {
            Activation = activation;
            LeafIndex = leafIndex;
            RootIndexExclusive = rootIndexExclusive;
        }

        internal AsyncCallStack Activation { get; }
        internal int LeafIndex { get; }
        internal int RootIndexExclusive { get; }
    }

    /// <summary>
    /// Counters and bounded messages gathered while stitching and applying presentation transforms.
    /// Includes successful processing and expected fallbacks as well as soft anomalies; nonzero counters do not
    /// by themselves indicate failure. Diagnostic conditions do not throw.
    /// </summary>
    public sealed class StitchDiagnostics
    {
        private const int MaximumMessageCount = 1_024;

        /// <summary>Number of async segments consumed.</summary>
        public int SegmentsProcessed;

        /// <summary>Segments whose expected sync-stack dispatch boundary was not found. Stitching may recover a unique
        /// physical alignment; otherwise stitching degrades conservatively.</summary>
        public int BoundariesNotFound;

        /// <summary>V2 segments where the current async method did not match the sync frame directly leaf-ward of the
        /// boundary (the next lower leaf-&gt;root index, boundaryPos-1).</summary>
        public int AdjacencyMismatches;

        /// <summary>V2 segments whose completed-frame count came from the wrapper slot because normal
        /// <c>CompleteMethod</c> events were not observed in their configuration epoch.</summary>
        public int V2WrapperFallbackUsed;

        /// <summary>V1 segments whose normal completed-frame count came from physical frames because
        /// <c>CompleteMethod</c> events were not observed in their configuration epoch.</summary>
        public int V1InlineFallbackUsed;

        /// <summary>V1 segments whose dispatcher boundary was truncated, but whose current position was recovered
        /// from a unique contiguous sequence of state-machine methods retained on the synchronous stack.</summary>
        public int V1TruncatedSuffixFallbackUsed;

        /// <summary>Outermost V2 activations whose missing wrapper position was recovered from a unique ordered
        /// physical-method sequence consistent with recorded completion and wrapper-reset evidence.</summary>
        public int V2TruncatedSuffixFallbackUsed;

        /// <summary>Outermost activations whose uniquely recovered, resolved physical span ended before the
        /// dispatcher. Their incomplete caller fragment was replaced by an explicit <c>BROKEN</c> root.</summary>
        public int IncompleteAsyncRootsNormalized;

        /// <summary>V1 segments anchored by a physically present, record-verified completing method.</summary>
        public int V1CompletionTailUsed;

        /// <summary>V1 segments whose completion evidence cannot be safely aligned to a physical anchor,
        /// including repeated occurrences or records beyond the matched prefix. Their physical slices are retained.</summary>
        public int V1CompletionAlignmentAmbiguous;

        /// <summary>V2 samples whose inner activation retained its sync layout because no continuation-wrapper
        /// sat root-ward of the sampled leaf (at a higher leaf-&gt;root index): either the wrapper was the leaf itself,
        /// or there was no wrapper but the sample sat in dispatch-continuation plumbing. These frames are dispatch
        /// machinery with no identifiable indexed dispatch target, so no inner ancestry is spliced.
        /// Independently verified enclosing activations may still be stitched.
        /// Enabled presentation transforms may subsequently normalize that layout.</summary>
        public int V2SyncLayoutUsed;

        /// <summary>V2 samples where the conservative presentation transform dropped a sampled-leaf continuation
        /// wrapper, leaving its indexed target or the retained dispatch plumbing as the leaf.</summary>
        public int V2LeafWrapperDropped;

        /// <summary>V2 dispatch-continuation plumbing frames (<c>InstrumentedDispatchContinuations</c> /
        /// <c>DispatchContinuations</c>) collapsed root-ward of a stitched continuation-wrapper, since the spliced
        /// logical ancestry already represents that machinery.</summary>
        public int V2PlumbingFramesCollapsed;

        /// <summary>V1 CoreLib async-state-machine-box frames collapsed contiguously root-ward of a consumed
        /// dispatcher boundary.</summary>
        public int V1InfrastructureFramesCollapsed;

        /// <summary>Physical V1 state-machine <c>MoveNext</c> frames presented as their logical source methods by
        /// the conservative presentation transform.</summary>
        public int V1SynchronousMoveNextFramesNormalized;

        /// <summary>
        /// Duplicate V1 startup representations removed from an exact synchronously reentrant activation pattern
        /// by the conservative presentation transform.
        /// </summary>
        public int V1ReentrantFramesCollapsed;

        /// <summary>Generated V1 startup frames removed by the conservative presentation transform. When an exact
        /// matching kickoff follows <c>stateMachine.MoveNext -&gt; methodBuilder.Start+</c>, the generated
        /// <c>MoveNext</c> and builder frames are folded into that native kickoff so its full signature is retained;
        /// otherwise only the known builder frames are removed.</summary>
        public int V1SynchronousStartupFramesCollapsed;

        /// <summary>Recognized V1/V2 runtime-infrastructure frames removed by
        /// <see cref="AsyncStackTransformPipeline.EnableAsyncInfrastructureCleanup"/>.
        /// Does not include frames removed by structural stitching or conservative normalization.</summary>
        public int AsyncInfrastructureFramesCollapsed;

        /// <summary>Runtime-owned frames atomically folded between a matched async activation and thread-pool
        /// dispatch by the optional cleanup. A subset of <see cref="AsyncInfrastructureFramesCollapsed"/>.</summary>
        public int RuntimeResumptionBridgeFramesCollapsed;

        /// <summary>Synchronous leaf-side frames owned by a verified profiler entry, including unresolved callees,
        /// atomically attributed to the nearest active logical async frame.
        /// A subset of <see cref="AsyncInfrastructureFramesCollapsed"/>.</summary>
        public int AsyncProfilerEnvelopeFramesCollapsed;

        /// <summary>Resolved System.Private.CoreLib-only synchronous handoff frames collapsed between verified
        /// adjacent V1/V2 activations. Dispatch targets retain their role and captured V1 state.</summary>
        public int AsyncReentrantHandoffFramesCollapsed;

        /// <summary>The RuntimeAsync-to-RuntimeAsync subset of
        /// <see cref="AsyncReentrantHandoffFramesCollapsed"/>.</summary>
        public int V2ReentrantHandoffFramesCollapsed;

        /// <summary>Resolved CoreLib completion-bridge and V2 dispatch frames removed by conservative
        /// presentation when the remaining physical dispatcher resumes an exactly matched V1 activation.</summary>
        public int V2CompletionHandoffFramesCollapsed;

        /// <summary>Human-readable notes: the anomalies above, plus per-segment happy-path trace steps when
        /// stitching is run with tracing enabled.</summary>
        public IReadOnlyList<string> Messages => m_messages ?? (IReadOnlyList<string>)Array.Empty<string>();

        /// <summary>Number of diagnostic messages omitted after the bounded message collection reached capacity.</summary>
        public int MessagesDropped;

        internal int MessageLimit { get; set; } = MaximumMessageCount;

        internal int RemainingMessageCapacity =>
            Math.Max(0, MessageLimit - (m_messages?.Count ?? 0));

        internal bool CanRetainMessage => RemainingMessageCapacity != 0;

        internal void Note(string message)
        {
            if (m_messages == null)
            {
                m_messages = new List<string>();
            }

            if (m_messages.Count < MessageLimit)
            {
                m_messages.Add(message);
            }
            else
            {
                MessagesDropped++;
            }
        }

        internal void Clear()
        {
            SegmentsProcessed = 0;
            BoundariesNotFound = 0;
            AdjacencyMismatches = 0;
            V2WrapperFallbackUsed = 0;
            V1InlineFallbackUsed = 0;
            V1TruncatedSuffixFallbackUsed = 0;
            V2TruncatedSuffixFallbackUsed = 0;
            IncompleteAsyncRootsNormalized = 0;
            V1CompletionTailUsed = 0;
            V1CompletionAlignmentAmbiguous = 0;
            V2SyncLayoutUsed = 0;
            V2LeafWrapperDropped = 0;
            V2PlumbingFramesCollapsed = 0;
            V1InfrastructureFramesCollapsed = 0;
            V1SynchronousMoveNextFramesNormalized = 0;
            V1ReentrantFramesCollapsed = 0;
            V1SynchronousStartupFramesCollapsed = 0;
            AsyncInfrastructureFramesCollapsed = 0;
            RuntimeResumptionBridgeFramesCollapsed = 0;
            AsyncProfilerEnvelopeFramesCollapsed = 0;
            AsyncReentrantHandoffFramesCollapsed = 0;
            V2ReentrantHandoffFramesCollapsed = 0;
            V2CompletionHandoffFramesCollapsed = 0;
            MessagesDropped = 0;
            m_messages?.Clear();
        }

        internal void AddTo(StitchDiagnostics target)
        {
            target.SegmentsProcessed += SegmentsProcessed;
            target.BoundariesNotFound += BoundariesNotFound;
            target.AdjacencyMismatches += AdjacencyMismatches;
            target.V2WrapperFallbackUsed += V2WrapperFallbackUsed;
            target.V1InlineFallbackUsed += V1InlineFallbackUsed;
            target.V1TruncatedSuffixFallbackUsed += V1TruncatedSuffixFallbackUsed;
            target.V2TruncatedSuffixFallbackUsed += V2TruncatedSuffixFallbackUsed;
            target.IncompleteAsyncRootsNormalized += IncompleteAsyncRootsNormalized;
            target.V1CompletionTailUsed += V1CompletionTailUsed;
            target.V1CompletionAlignmentAmbiguous += V1CompletionAlignmentAmbiguous;
            target.V2SyncLayoutUsed += V2SyncLayoutUsed;
            target.V2LeafWrapperDropped += V2LeafWrapperDropped;
            target.V2PlumbingFramesCollapsed += V2PlumbingFramesCollapsed;
            target.V1InfrastructureFramesCollapsed += V1InfrastructureFramesCollapsed;
            target.V1SynchronousMoveNextFramesNormalized += V1SynchronousMoveNextFramesNormalized;
            target.V1ReentrantFramesCollapsed += V1ReentrantFramesCollapsed;
            target.V1SynchronousStartupFramesCollapsed += V1SynchronousStartupFramesCollapsed;
            target.AsyncInfrastructureFramesCollapsed += AsyncInfrastructureFramesCollapsed;
            target.RuntimeResumptionBridgeFramesCollapsed += RuntimeResumptionBridgeFramesCollapsed;
            target.AsyncProfilerEnvelopeFramesCollapsed += AsyncProfilerEnvelopeFramesCollapsed;
            target.AsyncReentrantHandoffFramesCollapsed += AsyncReentrantHandoffFramesCollapsed;
            target.V2ReentrantHandoffFramesCollapsed += V2ReentrantHandoffFramesCollapsed;
            target.V2CompletionHandoffFramesCollapsed += V2CompletionHandoffFramesCollapsed;
            target.MessagesDropped += MessagesDropped;
            if (m_messages != null)
            {
                for (int i = 0; i < m_messages.Count; i++)
                {
                    target.Note(m_messages[i]);
                }
            }
        }

        #region private

        private List<string> m_messages;

        #endregion
    }

    /// <summary>The result of a stitch: the merged frames (leaf-&gt;root) and the diagnostics.</summary>
    public sealed class StitchResult
    {
        /// <summary>Creates a result retaining the supplied frame list and diagnostics without copying them.</summary>
        public StitchResult(IReadOnlyList<StitchedFrame> frames, StitchDiagnostics diagnostics)
            : this(frames, diagnostics, Array.Empty<AsyncSegmentPlacement>())
        {
        }

        internal StitchResult(
            IReadOnlyList<StitchedFrame> frames,
            StitchDiagnostics diagnostics,
            IReadOnlyList<AsyncSegmentPlacement> segmentPlacements)
        {
            Frames = frames ?? throw new ArgumentNullException(nameof(frames));
            Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            SegmentPlacements =
                segmentPlacements ?? throw new ArgumentNullException(nameof(segmentPlacements));
        }

        /// <summary>The stitched frames, leaf-&gt;root (the native leaf first, the thread/process root last).</summary>
        public IReadOnlyList<StitchedFrame> Frames { get; }

        /// <summary>Counters and bounded messages for stitching, fallbacks, and any subsequent presentation transforms.</summary>
        public StitchDiagnostics Diagnostics { get; }

        internal IReadOnlyList<AsyncSegmentPlacement> SegmentPlacements { get; }
    }

    /// <summary>
    /// The pure async-CPU stitch core: merges a native (CPU-sample) sync stack with the async call stack
    /// segments active at the same instant into a single logical call stack, splicing each suspended await's
    /// ancestry in place of its dispatch machinery.
    /// <para>
    /// This is an interleaved, segment-lockstep, index-aware two-pointer replacement walk:
    /// one pointer down the sync stack (leaf-&gt;root), one down the async segment list (leaf-&gt;root),
    /// consumed in strict lockstep. For each segment the walk (a) finds the sync frame that is the segment's
    /// dispatch boundary, (b) emits the real sync frames leaf-ward of it (they are the segment's already-completed
    /// and currently-running frames, physically present on the native stack), then (c) splices the segment's
    /// root-ward remaining frames (the suspended ancestry) in place of the dispatch machinery.
    /// If a V1 boundary is verified but its indexed current method has no separate physical match, the result
    /// retains that dispatcher and inserts an explicit <see cref="StitchedFrameOrigin.AsyncDispatchTarget"/>.
    /// This preserves the known logical chain without asserting that the target body is physically executing.
    /// A missing inner V1 boundary does not consume a verified enclosing V2 current frame. Unique inner
    /// alignment replaces only its completed transition tail, retaining the inter-context physical span
    /// for the enclosing activation.
    /// A sampled V2 wrapper identifies an indexed dispatch target through completion records or its
    /// activation-relative wrapper position, without claiming a physically executing application body.
    /// When that target is unavailable, its physical entry slice is retained; independently verified
    /// enclosing activations can still be stitched.
    /// A missing outermost dispatcher can instead be recovered from a unique ordered physical-method match.
    /// If its completion position agrees and the root-ward span is resolved and contains no other dispatcher,
    /// that incomplete span is replaced by <see cref="StitchedFrameOrigin.IncompleteRoot"/>. This does not
    /// depend on the captured frame count, helper names, or a thread-pool root. Ambiguous or unresolved spans
    /// and callers beyond an observed dispatcher are retained. V2 recovery without method-completion events
    /// must also respect recorded wrapper-reset and exception-completion lower bounds.
    /// </para>
    /// <para>
    /// This type is intentionally free of any <c>StackSource</c> / interning dependency: it consumes materialized
    /// <see cref="StitchSyncFrame"/>s and produces a flat <see cref="StitchResult"/> so it can be unit-tested in
    /// isolation. A separate emitter turns the result into a <c>MutableTraceEventStackSource</c> call stack.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Structural boundary replacement is separate from <see cref="AsyncStackTransformPipeline"/>'s optional
    /// presentation rules. A null/empty segment list or a segment with no frames preserves the supplied physical
    /// stack. Public <c>Stitch</c> overloads allocate their output lists and do not mutate the supplied stacks.
    /// </remarks>
    public static class AsyncCpuStackStitcher
    {
        /// <summary>
        /// Stitches <paramref name="syncLeafToRoot"/> (a native CPU-sample stack, leaf-&gt;root) with the async
        /// call stacks <paramref name="segmentsRootToLeaf"/> active at <paramref name="qpc"/> (as returned by
        /// <see cref="AsyncCallStacksIndex.GetAsyncCallStacks(AsyncThreadKey, long)"/>, i.e. depth-ascending =
        /// root-&gt;leaf). Convenience overload over
        /// <see cref="Stitch(IReadOnlyList{StitchSyncFrame}, IReadOnlyList{AsyncCallStack}, long, Func{CodeAddressIndex, AsyncStitchBoundaryInfo}, Func{AsyncCallstackKind, bool}, Func{CodeAddressIndex, MethodIndex}, bool)"/>
        /// that sources boundary classification from a <see cref="AsyncStitchBoundaryCache"/> and the
        /// completion-events-observed predicate from an <see cref="AsyncCallStacksIndex"/>.
        /// </summary>
        /// <param name="syncLeafToRoot">The native sync stack, leaf frame first, thread/process root last.</param>
        /// <param name="segmentsRootToLeaf">The async segments active at <paramref name="qpc"/>, root-&gt;leaf
        /// (they are reversed internally to leaf-&gt;root to walk in lockstep with the sync stack).</param>
        /// <param name="qpc">The sample time, in the trace's QPC domain.</param>
        /// <param name="boundaries">The per-trace boundary-method cache.</param>
        /// <param name="index">The async index, queried for whether completion events were observed in each
        /// activation's metadata/configuration epoch.</param>
        /// <param name="processIndex">The process instance that emitted the sampled stack.</param>
        /// <param name="methodOf">Maps a <see cref="CodeAddressIndex"/> to its <see cref="MethodIndex"/>
        /// (typically <c>traceLog.CodeAddresses.MethodIndex</c>); used for the V1 identity match and V2 adjacency check.</param>
        /// <param name="trace">When true, a per-segment happy-path note (kind / boundary / completed count /
        /// spliced range) is appended to <see cref="StitchDiagnostics.Messages"/> in addition to the anomaly
        /// notes. Off by default; intended for debugging the stitch flow.</param>
        public static StitchResult Stitch(
            IReadOnlyList<StitchSyncFrame> syncLeafToRoot,
            IReadOnlyList<AsyncCallStack> segmentsRootToLeaf,
            long qpc,
            AsyncStitchBoundaryCache boundaries,
            AsyncCallStacksIndex index,
            ProcessIndex processIndex,
            Func<CodeAddressIndex, MethodIndex> methodOf,
            bool trace = false)
        {
            if (boundaries is null) throw new ArgumentNullException(nameof(boundaries));
            if (index is null) throw new ArgumentNullException(nameof(index));
            if (syncLeafToRoot is null) throw new ArgumentNullException(nameof(syncLeafToRoot));
            if (methodOf is null) throw new ArgumentNullException(nameof(methodOf));

            var diagnostics = new StitchDiagnostics();
            var output = new List<StitchedFrame>(syncLeafToRoot.Count + 8);
            var placements = new List<AsyncSegmentPlacement>(segmentsRootToLeaf?.Count ?? 0);
            StitchInto(syncLeafToRoot, segmentsRootToLeaf, qpc, boundaries.Classify, null,
                index.MethodCompletionObserved, processIndex, methodOf, trace, output, diagnostics, placements,
                boundaries.GetRuntimeProfile, boundaries.RuntimeProfiles.GetProcessProfile(processIndex),
                boundaries.TraceLog);
            return new StitchResult(output, diagnostics, placements);
        }

        /// <summary>
        /// Stitches <paramref name="syncLeafToRoot"/> (a native CPU-sample stack, leaf-&gt;root) with the async
        /// call stacks <paramref name="segmentsRootToLeaf"/> active at <paramref name="qpc"/> (depth-ascending =
        /// root-&gt;leaf). This overload takes the boundary classifier and completion-events predicate as delegates
        /// so it has no dependency on <see cref="AsyncStitchBoundaryCache"/> / <see cref="AsyncCallStacksIndex"/>
        /// and can be unit-tested with synthetic code addresses.
        /// </summary>
        /// <param name="syncLeafToRoot">The native sync stack, leaf frame first, thread/process root last.</param>
        /// <param name="segmentsRootToLeaf">The async segments active at <paramref name="qpc"/>, root-&gt;leaf.</param>
        /// <param name="qpc">The sample time, in the trace's QPC domain.</param>
        /// <param name="classify">Classifies a sync frame's <see cref="CodeAddressIndex"/> as an async dispatch boundary.</param>
        /// <param name="methodCompletionObserved">True if <c>CompleteMethod</c> (normal completion) events were
        /// observed for the supplied segments' kind and configuration (see
        /// <see cref="AsyncCallStacksIndex.MethodCompletionObserved(AsyncCallStack)"/>).
        /// V1 always adds recorded exception deltas. V2 adds them on the event-counting path, not on the wrapper
        /// fallback, whose index already includes unwinds. Use the index/cache overload for per-activation epoch selection.</param>
        /// <param name="methodOf">Maps a <see cref="CodeAddressIndex"/> to its <see cref="MethodIndex"/>; used for
        /// the V1 identity match and V2 adjacency check.</param>
        /// <param name="trace">When true, appends a per-segment happy-path note (kind / boundary / completed count /
        /// spliced range) to <see cref="StitchDiagnostics.Messages"/>. Off by default; for debugging the flow.</param>
        public static StitchResult Stitch(
            IReadOnlyList<StitchSyncFrame> syncLeafToRoot,
            IReadOnlyList<AsyncCallStack> segmentsRootToLeaf,
            long qpc,
            Func<CodeAddressIndex, AsyncStitchBoundaryInfo> classify,
            Func<AsyncCallstackKind, bool> methodCompletionObserved,
            Func<CodeAddressIndex, MethodIndex> methodOf,
            bool trace = false)
        {
            if (syncLeafToRoot is null) throw new ArgumentNullException(nameof(syncLeafToRoot));
            if (classify is null) throw new ArgumentNullException(nameof(classify));
            if (methodCompletionObserved is null) throw new ArgumentNullException(nameof(methodCompletionObserved));
            if (methodOf is null) throw new ArgumentNullException(nameof(methodOf));

            var diagnostics = new StitchDiagnostics();
            var output = new List<StitchedFrame>(syncLeafToRoot.Count + 8);
            var placements = new List<AsyncSegmentPlacement>(segmentsRootToLeaf?.Count ?? 0);
            StitchInto(syncLeafToRoot, segmentsRootToLeaf, qpc, classify, methodCompletionObserved, null,
                ProcessIndex.Invalid, methodOf, trace, output, diagnostics, placements, null, null, null);
            return new StitchResult(output, diagnostics, placements);
        }

        internal static void StitchInto(
            IReadOnlyList<StitchSyncFrame> syncLeafToRoot,
            IReadOnlyList<AsyncCallStack> segmentsRootToLeaf,
            long qpc,
            Func<CodeAddressIndex, AsyncStitchBoundaryInfo> classify,
            Func<ProcessIndex, AsyncCallstackKind, long, bool> methodCompletionObserved,
            ProcessIndex processIndex,
            Func<CodeAddressIndex, MethodIndex> methodOf,
            bool trace,
            List<StitchedFrame> output,
            StitchDiagnostics diagnostics)
        {
            StitchInto(
                syncLeafToRoot, segmentsRootToLeaf, qpc, classify, methodCompletionObserved,
                processIndex, methodOf, trace, output, diagnostics, null);
        }

        internal static void StitchInto(
            IReadOnlyList<StitchSyncFrame> syncLeafToRoot,
            IReadOnlyList<AsyncCallStack> segmentsRootToLeaf,
            long qpc,
            Func<CodeAddressIndex, AsyncStitchBoundaryInfo> classify,
            Func<ProcessIndex, AsyncCallstackKind, long, bool> methodCompletionObserved,
            ProcessIndex processIndex,
            Func<CodeAddressIndex, MethodIndex> methodOf,
            bool trace,
            List<StitchedFrame> output,
            StitchDiagnostics diagnostics,
            List<AsyncSegmentPlacement> placements,
            Func<CodeAddressIndex, AsyncRuntimeRuleProfile> runtimeProfileResolver = null,
            AsyncRuntimeRuleProfile runtimeProfile = null,
            TraceLog runtimeTraceLog = null)
        {
            if (syncLeafToRoot is null) throw new ArgumentNullException(nameof(syncLeafToRoot));
            if (classify is null) throw new ArgumentNullException(nameof(classify));
            if (methodCompletionObserved is null) throw new ArgumentNullException(nameof(methodCompletionObserved));
            if (methodOf is null) throw new ArgumentNullException(nameof(methodOf));
            if (output is null) throw new ArgumentNullException(nameof(output));
            if (diagnostics is null) throw new ArgumentNullException(nameof(diagnostics));

            StitchInto(syncLeafToRoot, segmentsRootToLeaf, qpc, classify, null, methodCompletionObserved,
                processIndex, methodOf, trace, output, diagnostics, placements,
                runtimeProfileResolver, runtimeProfile, runtimeTraceLog);
        }

        private static void StitchInto(
            IReadOnlyList<StitchSyncFrame> syncLeafToRoot,
            IReadOnlyList<AsyncCallStack> segmentsRootToLeaf,
            long qpc,
            Func<CodeAddressIndex, AsyncStitchBoundaryInfo> classify,
            Func<AsyncCallstackKind, bool> methodCompletionObserved,
            Func<ProcessIndex, AsyncCallstackKind, long, bool> methodCompletionObservedByProcess,
            ProcessIndex processIndex,
            Func<CodeAddressIndex, MethodIndex> methodOf,
            bool trace,
            List<StitchedFrame> output,
            StitchDiagnostics diagnostics,
            List<AsyncSegmentPlacement> placements,
            Func<CodeAddressIndex, AsyncRuntimeRuleProfile> runtimeProfileResolver,
            AsyncRuntimeRuleProfile runtimeProfile,
            TraceLog runtimeTraceLog)
        {
            output.Clear();
            diagnostics.Clear();
            placements?.Clear();
            runtimeProfile = runtimeProfile ?? AsyncRuntimeRuleProfile.Common;

            // With no segment there is no suspended ancestry to splice. An empty segment is also unusable: it may
            // identify an active async context, but it has no current frame with which to align a physical boundary.
            // In either case emit the sync stack verbatim so no continuation-wrapper or dispatcher frame is removed
            // without logical ancestry to replace it.
            if (segmentsRootToLeaf is null || segmentsRootToLeaf.Count == 0 || HasEmptySegment(segmentsRootToLeaf))
            {
                for (int i = 0; i < syncLeafToRoot.Count; i++)
                {
                    output.Add(StitchedFrame.CreateSync(
                        syncLeafToRoot[i], null));
                }
                return;
            }

            int pSync = 0;
            int firstSegmentIndex = segmentsRootToLeaf.Count - 1;

            // A sampled V2 wrapper can identify its dispatch target without a physical body match.
            // Unavailable target identity or position retains the sync-layout fallback:
            //   * Case 2 - the wrapper is the sampled leaf, but its indexed target cannot be identified.
            //     Retain only the inner entry slice when an enclosing activation is independently verified.
            //   * Case 3 - no wrapper anywhere, but the sample sits in dispatch-continuation plumbing
            //     (InstrumentedDispatchContinuations / DispatchContinuations): emit the sync layout unchanged.
            // A V2 segment whose sync stack carries no async-boundary frame at all is a different anomaly (e.g. a
            // truncated stack); it falls through to the append-only degradation in the loop below, unchanged.
            if (segmentsRootToLeaf[segmentsRootToLeaf.Count - 1].Frames.Kind == AsyncCallstackKind.RuntimeAsync)
            {
                int wrapperPos = FirstWrapperIndex(syncLeafToRoot, classify, out bool hasDispatchContinuation);
                bool wrapperFound = wrapperPos < syncLeafToRoot.Count;

                if (wrapperFound && wrapperPos == 0 &&
                    !HasV2DispatchTarget(segmentsRootToLeaf[firstSegmentIndex], qpc,
                        classify(syncLeafToRoot[0].CodeAddress).WrapperIndex,
                        methodCompletionObserved, methodCompletionObservedByProcess, processIndex))
                {
                    int physicalEnd = syncLeafToRoot.Count;
                    if (firstSegmentIndex > 0)
                    {
                        AsyncRuntimeRuleProfile entryProfile = runtimeProfileResolver == null ? runtimeProfile :
                            runtimeProfileResolver(syncLeafToRoot[0].CodeAddress);
                        int entryEnd = 1 + MatchDispatchPlumbing(entryProfile, AsyncCallstackKind.RuntimeAsync,
                            syncLeafToRoot, 0, 1, classify, runtimeProfileResolver, runtimeTraceLog);

                        AsyncCallStack outer = segmentsRootToLeaf[firstSegmentIndex - 1];
                        bool outerVerified = outer.Frames.Kind == AsyncCallstackKind.RuntimeAsync
                            ? FindEnclosingV2CurrentSyncFrame(
                                outer, syncLeafToRoot, entryEnd, qpc, classify,
                                methodCompletionObserved, methodCompletionObservedByProcess, processIndex, methodOf) >= 0
                            : FindBoundary(syncLeafToRoot, entryEnd, outer.Frames.Kind, classify, out _) <
                                syncLeafToRoot.Count;
                        if (outerVerified)
                        {
                            physicalEnd = entryEnd;
                        }
                    }
                    diagnostics.V2SyncLayoutUsed++;
                    for (int i = 0; i < physicalEnd; i++)
                    {
                        output.Add(StitchedFrame.CreateSync(
                            syncLeafToRoot[i], null));
                    }
                    if (trace)
                    {
                        diagnostics.Note(physicalEnd == syncLeafToRoot.Count
                            ? "V2 not stitched: continuation-wrapper is the sampled leaf; used the sync layout."
                            : "Inner V2 not stitched: continuation-wrapper is the sampled leaf; retained its entry slice and continued with the enclosing activation.");
                    }
                    if (physicalEnd == syncLeafToRoot.Count)
                    {
                        return;
                    }

                    // No logical inner anchor or ancestry placement: entry work does not prove body execution.
                    diagnostics.SegmentsProcessed++;
                    pSync = physicalEnd;
                    firstSegmentIndex--;
                }

                if (!wrapperFound && hasDispatchContinuation) // Case 3: sync layout unchanged.
                {
                    diagnostics.V2SyncLayoutUsed++;
                    for (int i = 0; i < syncLeafToRoot.Count; i++)
                    {
                        output.Add(StitchedFrame.CreateSync(
                            syncLeafToRoot[i], null));
                    }
                    if (trace)
                    {
                        diagnostics.Note("V2 not stitched: no continuation-wrapper root-ward of the leaf; used the sync layout.");
                    }
                    return;
                }
            }

            for (int segmentIndex = firstSegmentIndex; segmentIndex >= 0; segmentIndex--)
            {
                AsyncCallStack segment = segmentsRootToLeaf[segmentIndex];
                int segmentLeafIndex = output.Count;
                diagnostics.SegmentsProcessed++;
                AsyncCallStackFrames frames = segment.Frames;
                AsyncCallstackKind kind = frames.Kind;
                int boundaryPos = FindBoundary(syncLeafToRoot, pSync, kind, classify, out AsyncStitchBoundaryInfo boundaryInfo);
                bool boundaryFound = boundaryPos < syncLeafToRoot.Count;
                AsyncRuntimeRuleProfile segmentProfile = boundaryFound && runtimeProfileResolver != null
                    ? runtimeProfileResolver(syncLeafToRoot[boundaryPos].CodeAddress) : runtimeProfile;
                if (!boundaryFound)
                {
                    diagnostics.BoundariesNotFound++;
                    if (diagnostics.CanRetainMessage)
                    {
                        diagnostics.Note($"No {kind} dispatch boundary found at or root-ward of sync frame {pSync}; recovering a unique physical alignment when available, otherwise splicing ancestry append-only.");
                    }
                    else
                    {
                        diagnostics.MessagesDropped++;
                    }
                }

                int syncEnd = boundaryPos;
                if (!boundaryFound && kind == AsyncCallstackKind.StateMachineAsync && segmentIndex > 0)
                {
                    for (int outerIndex = segmentIndex - 1; outerIndex >= 0; outerIndex--)
                    {
                        AsyncCallStack outer = segmentsRootToLeaf[outerIndex];
                        if (outer.Frames.Kind != AsyncCallstackKind.RuntimeAsync)
                        {
                            continue;
                        }
                        int outerCurrentSyncPos = FindEnclosingV2CurrentSyncFrame(
                            outer, syncLeafToRoot, pSync, qpc, classify,
                            methodCompletionObserved, methodCompletionObservedByProcess, processIndex, methodOf);
                        if (outerCurrentSyncPos >= 0)
                        {
                            // Neither completion matching nor emission may consume the verified enclosing activation.
                            syncEnd = outerCurrentSyncPos;
                        }
                        break;
                    }
                }

                // A non-leaf segment can finish all of its methods while its dispatcher invocation remains
                // physically on the stack and synchronously resumes the next segment. Remove that completed outer
                // segment; a leaf activation can instead retain its physically finishing final method.
                bool allowFullyCompleted = boundaryFound && segmentIndex < segmentsRootToLeaf.Count - 1;
                int completed = ComputeCompletedCount(segment, frames, kind, boundaryInfo, boundaryFound,
                    allowFullyCompleted, qpc,
                    syncLeafToRoot, pSync, syncEnd, methodCompletionObserved,
                    methodCompletionObservedByProcess,                     processIndex, methodOf, diagnostics, segmentProfile,
                    out bool completionObserved, out int recoveredTailPos,
                    out bool v1CompletionAmbiguous, out bool v1CompletionTailVerified);
                bool fullyCompleted = allowFullyCompleted && completed == frames.FrameCount;

                int currentSyncPos = -1;
                if (!fullyCompleted && !v1CompletionAmbiguous)
                {
                    currentSyncPos = kind == AsyncCallstackKind.StateMachineAsync
                        ? FindCurrentSyncFrame(
                            frames, completed, syncLeafToRoot, pSync, syncEnd, methodOf,
                            boundaryFound || recoveredTailPos >= 0, segmentProfile)
                        : FindCurrentV2SyncFrame(
                            frames, completed, syncLeafToRoot, boundaryPos, methodOf, boundaryFound, diagnostics);
                }

                if (kind == AsyncCallstackKind.StateMachineAsync && v1CompletionTailVerified && currentSyncPos >= 0)
                {
                    MethodIndex currentMethod = methodOf(frames.CodeAddressAt(completed));
                    for (int k = 0; k < completed; k++)
                    {
                        if (methodOf(frames.CodeAddressAt(k)) == currentMethod)
                        {
                            currentSyncPos = -1;
                            v1CompletionAmbiguous = true;
                            diagnostics.V1CompletionAlignmentAmbiguous++;
                            diagnostics.Note("V1 completion counts cannot distinguish repeated current/completed occurrences; retained the physical slice.");
                            break;
                        }
                    }
                }

                int completingSyncPos = -1;
                if (kind == AsyncCallstackKind.StateMachineAsync && boundaryFound &&
                    !fullyCompleted && !v1CompletionAmbiguous && v1CompletionTailVerified && currentSyncPos < 0 &&
                    TryFindV1CompletionTail(
                        frames, completed, syncLeafToRoot, pSync, boundaryPos, methodOf,
                        out completingSyncPos))
                {
                    diagnostics.V1CompletionTailUsed++;
                }
                bool incompleteRoot = segmentIndex == 0 &&
                    !boundaryFound && !v1CompletionAmbiguous &&
                    TryNormalizeIncompleteRoot(
                        segment, syncLeafToRoot, pSync, qpc, completionObserved,
                        completed, currentSyncPos, recoveredTailPos, classify, methodOf, segmentProfile,
                        out completed, out currentSyncPos);
                if (kind == AsyncCallstackKind.RuntimeAsync)
                {
                    if (incompleteRoot)
                    {
                        diagnostics.V2TruncatedSuffixFallbackUsed++;
                    }
                    else if (!completionObserved)
                    {
                        diagnostics.V2WrapperFallbackUsed++;
                    }
                }
                int anchorSyncPos = currentSyncPos >= 0 ? currentSyncPos : completingSyncPos;
                bool recoveredInner = !boundaryFound && syncEnd < boundaryPos &&
                    recoveredTailPos >= 0 && recoveredTailPos <= syncEnd &&
                    anchorSyncPos >= 0 && !v1CompletionAmbiguous;

                // Retain the sampled prefix and one physical running/completing anchor. For V1, the older inline
                // transition tail through the verified dispatcher is represented by the logical ancestry.
                int physicalEnd = fullyCompleted
                    ? pSync
                    : (incompleteRoot || recoveredInner ||
                       (kind == AsyncCallstackKind.StateMachineAsync && boundaryFound)) &&
                      anchorSyncPos >= 0
                        ? anchorSyncPos + 1
                        : syncEnd;
                for (int i = pSync; i < physicalEnd; i++)
                {
                    output.Add(i == currentSyncPos
                        ? kind == AsyncCallstackKind.StateMachineAsync &&
                          syncLeafToRoot[i].Method != methodOf(frames.CodeAddressAt(completed))
                            ? StitchedFrame.CreateAsyncDispatchTarget(syncLeafToRoot[i], segment, completed)
                            : StitchedFrame.CreateAsyncCurrent(
                                syncLeafToRoot[i].CodeAddress, syncLeafToRoot[i].Method, segment, completed)
                        : i == completingSyncPos
                            ? StitchedFrame.CreateAsyncCompleting(
                                syncLeafToRoot[i].CodeAddress, syncLeafToRoot[i].Method, segment, completed - 1)
                            : StitchedFrame.CreateSync(
                                syncLeafToRoot[i], segment));
                }

                // Retain sampled dispatch machinery separately from its known logical target. Neither a V1
                // dispatcher nor a sampled V2 wrapper proves that the application body is physically executing.
                // A V1 completing anchor replaces the boundary, but its next target is still not running.
                if (boundaryFound && (kind == AsyncCallstackKind.StateMachineAsync || boundaryPos == 0) &&
                    !fullyCompleted && currentSyncPos < 0 && completed < frames.FrameCount &&
                    (frames.CodeAddressAt(completed) != CodeAddressIndex.Invalid || frames.MethodIdAt(completed) != 0))
                {
                    if (completingSyncPos < 0)
                    {
                        output.Add(StitchedFrame.CreateSync(syncLeafToRoot[boundaryPos], segment));
                    }
                    output.Add(StitchedFrame.CreateAsyncDispatchTarget(segment, completed));
                }

                // Physical anchors were emitted above; the unexecuted remainder is reconstructed as ancestry.
                for (int k = completed + 1; k < frames.FrameCount; k++)
                {
                    output.Add(StitchedFrame.CreateAsync(segment, k));
                }

                placements?.Add(
                    new AsyncSegmentPlacement(segment, segmentLeafIndex, output.Count));

                if (trace && diagnostics.CanRetainMessage)
                {
                    string boundaryDesc = boundaryFound
                        ? (boundaryInfo.Kind == AsyncStitchBoundaryKind.V2ContinuationWrapper
                            ? $"{boundaryInfo.Kind}[{boundaryInfo.WrapperIndex}]@sync{boundaryPos}"
                            : $"{boundaryInfo.Kind}@sync{boundaryPos}")
                        : "no-boundary";
                    int splicedCount = frames.FrameCount - (completed + 1);
                    if (splicedCount < 0) splicedCount = 0;
                    diagnostics.Note($"segment[{diagnostics.SegmentsProcessed - 1}] kind={kind} boundary={boundaryDesc} " +
                        $"emittedSync={(fullyCompleted ? "none (fully completed)" : $"[{pSync}..{boundaryPos})")} " +
                        $"completed={completed} spliced=[{completed + 1}..{frames.FrameCount}) ({splicedCount} frame(s)) frameCount={frames.FrameCount}");
                }
                else if (trace)
                {
                    diagnostics.MessagesDropped++;
                }

                // Skip the boundary frame itself (the dispatch machinery the ancestry replaces); keep other frames.
                pSync = recoveredInner
                    ? recoveredTailPos
                    : (boundaryFound ? boundaryPos + 1 : syncEnd);
                if (incompleteRoot)
                {
                    output.Add(StitchedFrame.CreateIncompleteRoot());
                    diagnostics.IncompleteAsyncRootsNormalized++;
                }
                if (boundaryFound)
                {
                    // Collapse the dispatch-continuation plumbing for this segment immediately, rather than waiting
                    // until all segments are exhausted. An inner V2 segment may be followed by a preserved bridge
                    // and then an outer V1/V2 segment; leaking these frames into that bridge breaks lockstep alignment.
                    int plumbingCount = MatchDispatchPlumbing(segmentProfile, kind,
                        syncLeafToRoot, boundaryPos, pSync, classify, runtimeProfileResolver, runtimeTraceLog);
                    pSync += plumbingCount;
                    if (kind == AsyncCallstackKind.StateMachineAsync)
                    {
                        diagnostics.V1InfrastructureFramesCollapsed += plumbingCount;
                    }
                    else
                    {
                        diagnostics.V2PlumbingFramesCollapsed += plumbingCount;
                    }
                }
            }

            // Emit the clean sync tail (includes the thread/process root).
            for (int i = pSync; i < syncLeafToRoot.Count; i++)
            {
                output.Add(StitchedFrame.CreateSync(
                    syncLeafToRoot[i], null));
            }
        }

        private static int MatchDispatchPlumbing(
            AsyncRuntimeRuleProfile profile, AsyncCallstackKind kind, IReadOnlyList<StitchSyncFrame> frames,
            int boundaryIndex, int start, Func<CodeAddressIndex, AsyncStitchBoundaryInfo> classify,
            Func<CodeAddressIndex, AsyncRuntimeRuleProfile> runtimeProfileResolver, TraceLog traceLog)
        {
            AsyncRuntimeSequenceMatch match = profile.MatchDispatchPlumbing(kind, frames, start, frames.Count, classify);
            if (match.Length > frames.Count - start)
            {
                throw new InvalidOperationException("A runtime plumbing matcher returned a span outside its candidate range.");
            }
            if (!match.IsMatch)
            {
                return 0;
            }
            TraceModuleFile owner = traceLog?.CodeAddresses.ModuleFile(frames[boundaryIndex].CodeAddress);
            for (int i = start; i < start + match.Length; i++)
            {
                AsyncStitchBoundaryInfo boundary = classify(frames[i].CodeAddress);
                if ((boundary.IsBoundary &&
                     (kind != AsyncCallstackKind.RuntimeAsync ||
                      boundary.Kind != AsyncStitchBoundaryKind.V2DispatchContinuation)) ||
                    (runtimeProfileResolver != null && !ReferenceEquals(profile, runtimeProfileResolver(frames[i].CodeAddress))))
                {
                    return 0;
                }
                if (traceLog != null)
                {
                    TraceModuleFile module = traceLog.CodeAddresses.ModuleFile(frames[i].CodeAddress);
                    if (frames[i].Method == MethodIndex.Invalid ||
                        !AsyncRuntimeRuleProfileCache.IsHostModule(owner) ||
                        !AsyncRuntimeRuleProfileCache.IsHostModule(module) ||
                        (owner.ManagedModule ?? owner).ModuleFileIndex !=
                            (module.ManagedModule ?? module).ModuleFileIndex)
                    {
                        return 0;
                    }
                }
            }
            return match.Length;
        }

        private static bool HasV2DispatchTarget(
            AsyncCallStack segment, long qpc, int wrapperIndex,
            Func<AsyncCallstackKind, bool> methodCompletionObserved,
            Func<ProcessIndex, AsyncCallstackKind, long, bool> methodCompletionObservedByProcess,
            ProcessIndex processIndex)
        {
            bool completionObserved = methodCompletionObserved != null
                ? methodCompletionObserved(segment.Frames.Kind)
                : methodCompletionObservedByProcess(processIndex, segment.Frames.Kind, segment.StartQpc);
            if (!completionObserved &&
                (segment.WrapperCount == 0 || wrapperIndex < 0 || wrapperIndex >= segment.WrapperCount))
            {
                return false;
            }

            // Do not clamp an out-of-range position into a plausible target at either end of the call stack.
            long completed = completionObserved
                ? segment.GetCompletedFrameCount(qpc)
                : (long)segment.GetWrapperResetCount(qpc) * segment.WrapperCount +
                    wrapperIndex - segment.ContinuationIndexBase;
            return completed >= 0 && completed < segment.Frames.FrameCount &&
                (segment.Frames.CodeAddressAt((int)completed) != CodeAddressIndex.Invalid ||
                 segment.Frames.MethodIdAt((int)completed) != 0);
        }

        private static int FindEnclosingV2CurrentSyncFrame(
            AsyncCallStack segment, IReadOnlyList<StitchSyncFrame> sync, int pSync, long qpc,
            Func<CodeAddressIndex, AsyncStitchBoundaryInfo> classify,
            Func<AsyncCallstackKind, bool> methodCompletionObserved,
            Func<ProcessIndex, AsyncCallstackKind, long, bool> methodCompletionObservedByProcess,
            ProcessIndex processIndex, Func<CodeAddressIndex, MethodIndex> methodOf)
        {
            AsyncCallStackFrames frames = segment.Frames;
            if (frames.Kind != AsyncCallstackKind.RuntimeAsync)
            {
                return -1;
            }

            int boundaryPos = FindBoundary(sync, pSync, frames.Kind, classify, out AsyncStitchBoundaryInfo boundaryInfo);
            int currentSyncPos = boundaryPos - 1;
            if (boundaryPos == sync.Count || currentSyncPos < pSync || sync[currentSyncPos].Method == MethodIndex.Invalid)
            {
                return -1;
            }

            bool completionObserved = methodCompletionObserved != null
                ? methodCompletionObserved(frames.Kind)
                : methodCompletionObservedByProcess(processIndex, frames.Kind, segment.StartQpc);
            int completed = completionObserved
                ? segment.GetCompletedFrameCount(qpc)
                : segment.GetCompletedFrameCount(qpc, boundaryInfo.WrapperIndex);
            if (completed < 0 || completed >= frames.FrameCount)
            {
                return -1;
            }

            return sync[currentSyncPos].Method == methodOf(frames.CodeAddressAt(completed))
                ? currentSyncPos : -1;
        }

        private static bool TryNormalizeIncompleteRoot(
            AsyncCallStack segment, IReadOnlyList<StitchSyncFrame> sync, int pSync, long qpc,
            bool completionObserved, int completed, int currentSyncPos, int recoveredTailPos,
            Func<CodeAddressIndex, AsyncStitchBoundaryInfo> classify,
            Func<CodeAddressIndex, MethodIndex> methodOf,
            AsyncRuntimeRuleProfile profile,
            out int normalizedCompleted, out int normalizedCurrentSyncPos)
        {
            normalizedCompleted = completed;
            normalizedCurrentSyncPos = currentSyncPos;
            AsyncCallStackFrames frames = segment.Frames;
            int recoveredCompleted = completed;
            if (recoveredTailPos < 0 &&
                !TryRecoverTruncatedCompleted(
                    frames, sync, pSync, sync.Count, methodOf, profile, out recoveredCompleted, out _))
            {
                return false;
            }

            if (completionObserved)
            {
                if (recoveredCompleted != segment.GetCompletedFrameCount(qpc))
                {
                    return false;
                }
            }
            else if (frames.Kind == AsyncCallstackKind.RuntimeAsync)
            {
                if (recoveredCompleted < segment.GetCompletedFrameCount(qpc, 0) ||
                    recoveredCompleted < segment.GetExceptionCompletedFrameCount(qpc))
                {
                    return false;
                }
            }
            else if (recoveredCompleted != completed || currentSyncPos < 0)
            {
                return false;
            }

            int recoveredCurrentSyncPos =
                FindCurrentSyncFrame(frames, recoveredCompleted, sync, pSync, sync.Count, methodOf,
                    frames.Kind == AsyncCallstackKind.StateMachineAsync, profile);
            if (recoveredCurrentSyncPos < 0 ||
                (frames.Kind == AsyncCallstackKind.StateMachineAsync && recoveredCurrentSyncPos != currentSyncPos))
            {
                return false;
            }

            // The required non-inlined dispatcher must be below this uniquely matched invocation. Its absence
            // places the captured root-side span inside the activation, not in its host. An unresolved frame
            // could conceal that boundary; an observed boundary could belong to an intact outer segment.
            for (int i = recoveredCurrentSyncPos + 1; i < sync.Count; i++)
            {
                if (sync[i].Method == MethodIndex.Invalid)
                {
                    return false;
                }

                AsyncStitchBoundaryKind kind = classify(sync[i].CodeAddress).Kind;
                if (kind == AsyncStitchBoundaryKind.V1Dispatcher ||
                    kind == AsyncStitchBoundaryKind.V2ContinuationWrapper ||
                    kind == AsyncStitchBoundaryKind.V2DispatchContinuation)
                {
                    return false;
                }
            }

            normalizedCompleted = recoveredCompleted;
            normalizedCurrentSyncPos = recoveredCurrentSyncPos;
            return true;
        }

        private static bool HasEmptySegment(IReadOnlyList<AsyncCallStack> segments)
        {
            for (int i = 0; i < segments.Count; i++)
            {
                if (segments[i].Frames.FrameCount == 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Scans the sync stack (leaf-&gt;root) for the first V2 continuation-wrapper frame, used by the V2 gate to
        /// decide whether the CPU is physically inside a resumed continuation body (a wrapper root-ward of the leaf).
        /// Returns the wrapper's index, or <c>sync.Count</c> if none is present. <paramref name="hasDispatchContinuation"/>
        /// is set true when a V2 dispatch-continuation plumbing frame was seen before the first wrapper (only
        /// meaningful when no wrapper is found, distinguishing dispatch plumbing from a boundary-less stack).
        /// </summary>
        private static int FirstWrapperIndex(
            IReadOnlyList<StitchSyncFrame> sync, Func<CodeAddressIndex, AsyncStitchBoundaryInfo> classify,
            out bool hasDispatchContinuation)
        {
            hasDispatchContinuation = false;
            for (int i = 0; i < sync.Count; i++)
            {
                AsyncStitchBoundaryKind kind = classify(sync[i].CodeAddress).Kind;
                if (kind == AsyncStitchBoundaryKind.V2ContinuationWrapper)
                {
                    return i;
                }
                if (kind == AsyncStitchBoundaryKind.V2DispatchContinuation)
                {
                    hasDispatchContinuation = true;
                }
            }

            return sync.Count;
        }

        /// <summary>
        /// Finds the first sync frame at/root-ward of <paramref name="pSync"/> that is the dispatch boundary matching
        /// <paramref name="segmentKind"/>. Returns its position, or <c>sync.Count</c> if none is found.
        /// </summary>
        private static int FindBoundary(
            IReadOnlyList<StitchSyncFrame> sync, int pSync, AsyncCallstackKind segmentKind,
            Func<CodeAddressIndex, AsyncStitchBoundaryInfo> classify, out AsyncStitchBoundaryInfo boundaryInfo)
        {
            for (int i = pSync; i < sync.Count; i++)
            {
                AsyncStitchBoundaryInfo info = classify(sync[i].CodeAddress);
                if (MatchesKind(info.Kind, segmentKind))
                {
                    boundaryInfo = info;
                    return i;
                }
            }

            boundaryInfo = AsyncStitchBoundaryInfo.None;
            return sync.Count;
        }

        /// <summary>Whether a boundary kind matches an async segment's kind (V2 vs V1).</summary>
        private static bool MatchesKind(AsyncStitchBoundaryKind boundaryKind, AsyncCallstackKind segmentKind)
        {
            switch (segmentKind)
            {
                case AsyncCallstackKind.RuntimeAsync: // V2
                    // Only a continuation-wrapper identifies the V2 resumption position. It may be the sampled
                    // leaf, identifying a dispatch target rather than a physical body. A bare dispatch-continuation frame
                    // is dispatch plumbing, handled by the sync-layout gate in Stitch, not a splice boundary.
                    return boundaryKind == AsyncStitchBoundaryKind.V2ContinuationWrapper;
                case AsyncCallstackKind.StateMachineAsync: // V1
                    return boundaryKind == AsyncStitchBoundaryKind.V1Dispatcher;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The number of leaf-ward frames of <paramref name="segment"/> that have completed by
        /// <paramref name="qpc"/>. Completions come from two independent mechanisms that are combined:
        /// <list type="bullet">
        ///   <item><b>Normal completions</b>: if <c>CompleteMethod</c> events were emitted for this kind
        ///   (<paramref name="methodCompletionObserved"/>), <see cref="AsyncCallStack.GetMethodCompletedFrameCount"/>
        ///   is authoritative; else V2 derives them from the continuation-wrapper slot (which already includes
        ///   exceptional unwinds), and V1 counts the inline-resumed <c>MoveNext</c> frames on the sync stack.</item>
        ///   <item><b>Exceptional completions</b>: V1 reconciles <c>Unwind</c> counts with ordered physical matches,
        ///   since exceptional frames may have disappeared or may still be physically finishing. Overlapping
        ///   evidence is not counted twice. V2 wrapper positions already include exceptional completions.</item>
        /// </list>
        /// V2 leaf segments retain a valid current index. V1 may reach <c>FrameCount</c> while the final method
        /// is still physically finishing. Fully completed outer segments with a verified boundary are removed.
        /// </summary>
        private static int ComputeCompletedCount(
            AsyncCallStack segment, AsyncCallStackFrames frames, AsyncCallstackKind kind,
            AsyncStitchBoundaryInfo boundaryInfo, bool boundaryFound, bool allowFullyCompleted, long qpc,
            IReadOnlyList<StitchSyncFrame> sync, int pSync, int boundaryPos,
            Func<AsyncCallstackKind, bool> methodCompletionObserved,
            Func<ProcessIndex, AsyncCallstackKind, long, bool> methodCompletionObservedByProcess,
            ProcessIndex processIndex,
            Func<CodeAddressIndex, MethodIndex> methodOf, StitchDiagnostics diagnostics,
            AsyncRuntimeRuleProfile profile,
            out bool completionObserved, out int recoveredTailPos,
            out bool v1CompletionAmbiguous, out bool v1CompletionTailVerified)
        {
            recoveredTailPos = -1;
            v1CompletionAmbiguous = false;
            v1CompletionTailVerified = false;
            completionObserved = methodCompletionObserved != null
                ? methodCompletionObserved(kind)
                : methodCompletionObservedByProcess(processIndex, kind, segment.StartQpc);
            int completed;
            if (kind == AsyncCallstackKind.RuntimeAsync) // V2
            {
                if (completionObserved)
                {
                    // CompleteMethod events observed: the summed deltas (normal + any exceptional unwind) are the
                    // authoritative completed count.
                    completed = segment.GetCompletedFrameCount(qpc);
                }
                else
                {
                    // Derive from the continuation-wrapper slot; the wrapper index advances on every completion,
                    // including exceptional unwinds, so no separate exception term is added.
                    int slot = boundaryInfo.Kind == AsyncStitchBoundaryKind.V2ContinuationWrapper ? boundaryInfo.WrapperIndex : 0;
                    completed = segment.GetCompletedFrameCount(qpc, slot);
                }
            }
            else // V1: reconcile physical inline progress with recorded completions.
            {
                if (completionObserved)
                {
                    completed = segment.GetCompletedFrameCount(qpc);
                    v1CompletionTailVerified = completed > 0 && completed <= frames.FrameCount;
                    if (!boundaryFound &&
                        TryRecoverTruncatedCompleted(frames, sync, pSync, boundaryPos, methodOf, profile,
                            out int recoveredCompleted, out int tailPos) &&
                        recoveredCompleted == completed)
                    {
                        recoveredTailPos = tailPos;
                    }
                }
                else
                {
                    // No CompleteMethod events: the normal completions are the inline-resumed MoveNext frames
                    // physically on the sync stack.
                    int exceptionCompleted = segment.GetExceptionCompletedFrameCount(qpc);
                    if (!boundaryFound && exceptionCompleted == 0 &&
                        TryRecoverTruncatedCompleted(frames, sync, pSync, boundaryPos, methodOf, profile,
                            out completed, out recoveredTailPos))
                    {
                        diagnostics.V1TruncatedSuffixFallbackUsed++;
                    }
                    else
                    {
                        completed = CountV1Completed(
                            frames, sync, pSync, boundaryPos, exceptionCompleted, boundaryFound, allowFullyCompleted, methodOf, profile,
                            out v1CompletionAmbiguous);
                    }
                    diagnostics.V1InlineFallbackUsed++;
                    v1CompletionTailVerified = !v1CompletionAmbiguous &&
                        exceptionCompleted > 0 && completed == exceptionCompleted &&
                        completed <= frames.FrameCount;
                }

                if (v1CompletionAmbiguous)
                {
                    diagnostics.V1CompletionAlignmentAmbiguous++;
                    diagnostics.Note("V1 completion records overlap physical matches ambiguously; retained the physical slice.");
                }
            }

            int maxIndex = allowFullyCompleted || kind == AsyncCallstackKind.StateMachineAsync
                ? frames.FrameCount : frames.FrameCount - 1;
            if (completed < 0)
            {
                completed = 0;
            }
            else if (completed > maxIndex)
            {
                completed = maxIndex;
            }
            return completed;
        }

        /// <summary>
        /// Recovers a current-frame position when the root side of the synchronous sample was truncated before
        /// the dispatcher boundary. Only resolved methods present in the indexed async segment participate.
        /// The retained physical methods must form one uniquely positioned contiguous sequence of at least two
        /// frames in the async segment; recursive/repeated or otherwise ambiguous layouts are rejected.
        /// </summary>
        private static bool TryRecoverTruncatedCompleted(
            AsyncCallStackFrames frames, IReadOnlyList<StitchSyncFrame> sync, int pSync, int boundaryPos,
            Func<CodeAddressIndex, MethodIndex> methodOf, AsyncRuntimeRuleProfile profile,
            out int completed, out int rootTailPos)
        {
            completed = 0;
            rootTailPos = -1;
            int frameCount = frames.FrameCount;
            if (frameCount < 2)
            {
                return false;
            }

            var segmentMethods = new MethodIndex[frameCount];
            var methodSet = new HashSet<MethodIndex>();
            for (int i = 0; i < frameCount; i++)
            {
                MethodIndex method = methodOf(frames.CodeAddressAt(i));
                if (method == MethodIndex.Invalid)
                {
                    return false;
                }

                segmentMethods[i] = method;
                methodSet.Add(method);
            }

            var physicalMethods = new List<MethodIndex>();
            int rootmostPhysicalMethodPos = -1;
            for (int i = boundaryPos - 1; i >= pSync; i--)
            {
                MethodIndex method = sync[i].Method;
                if (methodSet.Contains(method))
                {
                    if (rootmostPhysicalMethodPos < 0)
                    {
                        rootmostPhysicalMethodPos = i;
                    }
                    physicalMethods.Add(method);
                }
            }

            if (physicalMethods.Count < 2 || physicalMethods.Count > frameCount)
            {
                return false;
            }

            int matchStart = -1;
            int maxStart = frameCount - physicalMethods.Count;
            for (int start = 0; start <= maxStart; start++)
            {
                int physicalIndex = 0;
                while (physicalIndex < physicalMethods.Count &&
                       segmentMethods[start + physicalIndex] == physicalMethods[physicalIndex])
                {
                    physicalIndex++;
                }

                if (physicalIndex != physicalMethods.Count)
                {
                    continue;
                }

                if (matchStart >= 0)
                {
                    return false;
                }

                matchStart = start;
            }

            if (matchStart < 0)
            {
                return false;
            }

            completed = matchStart + physicalMethods.Count - 1;
            rootTailPos = rootmostPhysicalMethodPos + 1;
            if (frames.Kind == AsyncCallstackKind.StateMachineAsync && completed + 1 < frameCount)
            {
                int carrier = FindV1BoxCarrier(frames, sync, pSync, boundaryPos, methodOf, profile);
                if (carrier >= 0 && sync[carrier].BoxedStateMachineMethod == segmentMethods[completed + 1])
                {
                    // The unique physical body sequence independently proves the truncated activation.
                    // Its next inline-resumed method can be represented by one leaf-side execution carrier.
                    completed++;
                }
            }
            return true;
        }

        /// <summary>
        /// Counts the V1 completed frames by matching the segment's already-completed frames against the
        /// inline-resumed <c>MoveNext</c> frames on the sync stack. In a V1 inline-resume cascade the segment frames
        /// appear in order from the dispatch boundary toward the physical leaf. The final matched state-machine
        /// frame is normally current; every earlier match has completed. We therefore match <c>segment[0,1,2,...]</c>
        /// against the complete boundary slice (from <c>boundaryPos-1</c> through <c>pSync</c>), skipping
        /// synchronous calls and transition machinery; the physical completion position is the final match's
        /// segment index. This ordered (positional) match is robust to a state machine legitimately recurring
        /// within one segment, unlike a
        /// set-membership count.
        /// The leafmost uniquely resolved CoreLib box execution carrier preceding any physical async application
        /// body can match its boxed state-machine method as the final resumption target inside a verified dispatcher
        /// boundary. Synchronous callees above that carrier remain physical; other box frames are not counted.
        /// <para>
        /// Exceptionally completed frames may be absent or still physically finishing. The known exception count
        /// bounds the number of async frames that may be skipped while aligning the next physical match.
        /// This supports exceptions before or between normally completed inline frames without allowing an
        /// unbounded search through unrelated async ancestry.
        /// </para>
        /// <para>
        /// If an async frame cannot be verified (for example a <c>0</c>-IP frame), matching stops conservatively,
        /// leaving the remaining frames as async ancestry.
        /// </para>
        /// </summary>
        private static int CountV1Completed(
            AsyncCallStackFrames frames, IReadOnlyList<StitchSyncFrame> sync, int pSync, int boundaryPos,
            int exceptionCompleted, bool boundaryFound, bool allowFullyCompleted, Func<CodeAddressIndex, MethodIndex> methodOf,
            AsyncRuntimeRuleProfile profile,
            out bool ambiguous)
        {
            ambiguous = false;
            int frameCount = frames.FrameCount;
            int nextSegmentFrame = 0;
            int matched = 0;
            int exceptionsSkipped = 0;
            int leafmostMatchedSyncPos = -1;
            int resolvedCandidate = -1;
            MethodIndex expected = MethodIndex.Invalid;
            int carrier = boundaryFound
                ? FindV1BoxCarrier(frames, sync, pSync, boundaryPos, methodOf, profile) : -1;

            for (int i = boundaryPos - 1; i >= pSync && nextSegmentFrame < frameCount; i--)
            {
                int candidate = nextSegmentFrame;
                int candidateExceptions = 0;
                int availableExceptions = exceptionCompleted - exceptionsSkipped;
                while (candidate < frameCount && candidateExceptions <= availableExceptions)
                {
                    if (candidate != resolvedCandidate)
                    {
                        expected = methodOf(frames.CodeAddressAt(candidate));
                        resolvedCandidate = candidate;
                    }
                    if (expected == MethodIndex.Invalid)
                    {
                        if (candidateExceptions < availableExceptions)
                        {
                            candidate++;
                            candidateExceptions++;
                            continue;
                        }

                        // The next verifiable physical frame cannot identify this async frame. Every previously
                        // matched frame completed; preserve the physical slice because the current frame is unknown.
                        ambiguous = exceptionCompleted > nextSegmentFrame;
                        return Math.Max(nextSegmentFrame, exceptionCompleted);
                    }

                    if (MatchesV1Method(sync[i], expected, i == carrier))
                    {
                        exceptionsSkipped += candidateExceptions;
                        nextSegmentFrame = candidate + 1;
                        matched++;
                        leafmostMatchedSyncPos = i;
                        break;
                    }

                    candidate++;
                    candidateExceptions++;
                }
            }

            // Normally the final matched physical state-machine frame is current. For a non-leaf segment, however,
            // its final MoveNext may have completed and synchronously resumed the inner segment while its dispatcher
            // invocation remains on the physical stack. A CoreLib method-builder completion frame leaf-ward of that
            // final MoveNext proves this shape without requiring CompleteMethod events.
            if (exceptionCompleted == exceptionsSkipped &&
                allowFullyCompleted && nextSegmentFrame == frameCount && leafmostMatchedSyncPos > pSync &&
                profile.HasV1CompletionEvidence(sync, pSync, leafmostMatchedSyncPos))
            {
                return nextSegmentFrame;
            }

            if (matched == 0)
            {
                return exceptionCompleted;
            }

            if (exceptionCompleted == exceptionsSkipped)
            {
                return nextSegmentFrame - 1;
            }

            // Retain the existing physical-current inference unless records cover the complete matched prefix.
            // Exceptions already accounted for by that physical position must not advance it a second time.
            if (exceptionCompleted < nextSegmentFrame)
            {
                return nextSegmentFrame - 1;
            }

            // Counts covering the entire prefix establish completion of the final physical match too.
            if (exceptionCompleted == nextSegmentFrame)
            {
                return exceptionCompleted;
            }

            ambiguous = true;
            return exceptionCompleted;
        }

        private static bool TryFindV1CompletionTail(
            AsyncCallStackFrames frames, int completed, IReadOnlyList<StitchSyncFrame> sync,
            int pSync, int boundaryPos, Func<CodeAddressIndex, MethodIndex> methodOf,
            out int completingSyncPos)
        {
            completingSyncPos = -1;
            if (completed <= 0 || completed > frames.FrameCount)
            {
                return false;
            }

            MethodIndex anchor = methodOf(frames.CodeAddressAt(completed - 1));
            if (anchor == MethodIndex.Invalid)
            {
                return false;
            }

            // Counts do not identify recursive occurrences. Require unique identities for every matched method,
            // and ordered completed matches, so no physical copy is assigned an invented completion status.
            int previousFrame = -1;
            int anchorSyncPos = -1;
            for (int i = boundaryPos - 1; i >= pSync; i--)
            {
                MethodIndex method = sync[i].Method;
                if (method == MethodIndex.Invalid)
                {
                    continue;
                }
                int match = -1;
                for (int k = 0; k < frames.FrameCount; k++)
                {
                    if (methodOf(frames.CodeAddressAt(k)) != method)
                    {
                        continue;
                    }
                    if (match >= 0)
                    {
                        return false;
                    }
                    match = k;
                }
                if (match < 0)
                {
                    continue;
                }
                if (match <= previousFrame || match >= completed || sync[i].CodeAddress == CodeAddressIndex.Invalid)
                {
                    return false;
                }
                previousFrame = match;
                if (method == anchor)
                {
                    anchorSyncPos = i;
                }
            }
            completingSyncPos = anchorSyncPos;
            return completingSyncPos >= 0;
        }

        private static int FindCurrentSyncFrame(
            AsyncCallStackFrames frames, int completed, IReadOnlyList<StitchSyncFrame> sync,
            int pSync, int boundaryPos, Func<CodeAddressIndex, MethodIndex> methodOf,
            bool allowBoxCarrier, AsyncRuntimeRuleProfile profile)
        {
            if (completed < 0 || completed >= frames.FrameCount)
            {
                return -1;
            }

            MethodIndex current = methodOf(frames.CodeAddressAt(completed));
            if (current == MethodIndex.Invalid)
            {
                // Without a current-method identity we cannot distinguish synchronous leaf calls from the V1
                // transition chain. Preserve the complete physical slice rather than risk deleting user frames.
                return -1;
            }

            // Pick the leaf-most matching physical frame. Completed copies of the same state machine, if any,
            // occur root-ward during an inline-resume cascade.
            int carrier = allowBoxCarrier ? FindV1BoxCarrier(frames, sync, pSync, boundaryPos, methodOf, profile) : -1;
            for (int i = pSync; i < boundaryPos; i++)
            {
                if (MatchesV1Method(sync[i], current, i == carrier))
                {
                    return i;
                }
            }

            return -1;
        }

        private static int FindV1BoxCarrier(
            AsyncCallStackFrames frames, IReadOnlyList<StitchSyncFrame> sync,
            int pSync, int boundaryPos, Func<CodeAddressIndex, MethodIndex> methodOf, AsyncRuntimeRuleProfile profile)
        {
            int carrier = -1;
            for (int i = pSync; i < boundaryPos; i++)
            {
                StitchSyncFrame frame = sync[i];
                if (frame.BoxedStateMachineMethod != MethodIndex.Invalid &&
                    profile.IsV1BoxCarrierCandidate(sync, pSync, boundaryPos, i))
                {
                    if (frame.CodeAddress == CodeAddressIndex.Invalid || frame.Method == MethodIndex.Invalid)
                    {
                        return -1;
                    }
                    carrier = i;
                    break;
                }
            }
            for (int i = pSync; i < carrier; i++)
            {
                StitchSyncFrame frame = sync[i];
                if (frame.Method != MethodIndex.Invalid)
                {
                    for (int k = 0; k < frames.FrameCount; k++)
                    {
                        if (frame.Method == methodOf(frames.CodeAddressAt(k)))
                        {
                            // A visible application body takes precedence over its caller's box wrappers.
                            return -1;
                        }
                    }
                }
            }
            return carrier;
        }

        private static bool MatchesV1Method(StitchSyncFrame frame, MethodIndex expected, bool allowBoxCarrier) =>
            frame.Method == expected ||
            (allowBoxCarrier &&
             frame.CodeAddress != CodeAddressIndex.Invalid &&
             frame.Method != MethodIndex.Invalid &&
             frame.BoxedStateMachineMethod == expected);

        private static int FindCurrentV2SyncFrame(
            AsyncCallStackFrames frames,
            int completed,
            IReadOnlyList<StitchSyncFrame> sync,
            int boundaryPos,
            Func<CodeAddressIndex, MethodIndex> methodOf,
            bool boundaryFound,
            StitchDiagnostics diagnostics)
        {
            int currentSyncPos = boundaryPos - 1;
            if (currentSyncPos < 0 || completed < 0 || completed >= frames.FrameCount)
            {
                return -1;
            }

            MethodIndex expected = methodOf(frames.CodeAddressAt(completed));
            MethodIndex actual = sync[currentSyncPos].Method;
            if (boundaryFound &&
                expected != MethodIndex.Invalid && actual != MethodIndex.Invalid && expected != actual)
            {
                diagnostics.AdjacencyMismatches++;
                if (diagnostics.CanRetainMessage)
                {
                    diagnostics.Note($"V2 current async method (segment frame {completed}) did not match the sync frame directly leaf-ward of the boundary (frame {currentSyncPos}).");
                }
                else
                {
                    diagnostics.MessagesDropped++;
                }
            }
            return expected != MethodIndex.Invalid && expected == actual
                ? currentSyncPos
                : -1;
        }

    }
}
