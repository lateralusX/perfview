// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>Where a <see cref="AsyncProfilerStitchedFrame"/> in a stitched call stack came from.</summary>
    internal enum AsyncProfilerStitchedFrameOrigin : byte
    {
        /// <summary>A real frame taken verbatim from the native (CPU-sample) sync stack.</summary>
        Sync = 0,

        /// <summary>The physically present current async frame matched to an active async-profiler segment. It
        /// retains the sampled native code address while carrying the segment's logical async identity.</summary>
        AsyncCurrent,

        /// <summary>A suspended async caller frame spliced in from an async call stack segment (an async frame that
        /// is <b>not</b> physically present on the native sync stack because it is awaiting).</summary>
        AsyncRemaining,

        /// <summary>A historical parent-context frame reached through a dispatcher creation relationship.</summary>
        AsyncContextParent,

        /// <summary>
        /// The indexed target of a verified dispatcher, V1 box carrier, or sampled V2 wrapper/resume stub when no separate physical
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

        /// <summary>
        /// The logical root of a fully completed V1 activation, to which a verified physical return-unwind
        /// span is attributed. Does not claim that the root method is physically present or executing.
        /// </summary>
        AsyncCompletionUnwind,

        /// <summary>
        /// The inferred current logical frame of a historical parent at child creation, based on recorded
        /// normal completions and exceptional unwinds. Retains its captured IP and compiler state.
        /// </summary>
        AsyncContextParentCurrent,

        /// <summary>A caller-supplied presentation label with no physical or async identity.</summary>
        Synthetic,
    }

    /// <summary>The async-context membership of a <see cref="AsyncProfilerStitchedFrame"/>.</summary>
    internal enum AsyncProfilerStitchedFrameContextKind
    {
        /// <summary>The frame does not belong to an async context.</summary>
        None = 0,

        /// <summary>The frame belongs to an async context active on the sampled execution stack.</summary>
        Active,

        /// <summary>The frame belongs to a historical creation-parent context.</summary>
        ParentContext,
    }

    /// <summary>How a <see cref="AsyncProfilerStitchedFrame"/> should be presented when it is interned.</summary>
    internal enum AsyncProfilerStitchedFramePresentation : byte
    {
        /// <summary>Use the frame's native symbol without presentation changes.</summary>
        Native = 0,

        /// <summary>
        /// Present a compiler-generated V1 state-machine <c>MoveNext</c> frame as its logical source async method.
        /// The original code address remains attached to the frame for source and identity correlation.
        /// </summary>
        LogicalStateMachineMethod,

        /// <summary>
        /// Represent a contiguous span of recognized V1/V2 runtime machinery as
        /// <c>[Async dispatcher frames]</c>, retaining the span's leafmost physical frame identity.
        /// </summary>
        AsyncDispatcherFrames,
    }

    /// <summary>
    /// Additional classification carried by a native sync frame for completion counting and presentation.
    /// Includes generated resume stubs, V1 startup machinery, and shared CoreLib infrastructure; these are not dispatch boundaries.
    /// </summary>
    internal enum AsyncProfilerStitchSyncFrameKind
    {
        /// <summary>An ordinary native sync frame.</summary>
        None = 0,

        /// <summary>A compiler-generated async state-machine <c>MoveNext</c> frame.</summary>
        V1StateMachineMoveNext,

        /// <summary>A profile-recognized runtime async resume stub; not an independent dispatch boundary.</summary>
        V2ResumeStub,

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

        /// <summary>A known CoreLib Task or ValueTask awaiter's readiness check, not registration or a dispatch boundary.</summary>
        V1AwaiterCompletionCheck,

        /// <summary>
        /// Other methods and nested helpers on profile-recognized CoreLib awaiter families. Classification
        /// alone does not permit removal; a verified dispatcher span or completed return spine is required.
        /// </summary>
        SystemPrivateCoreLibAwaiterInfrastructure,

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
        ProfilerCallstackCapture,

        /// <summary>CoreLib profiler callstack emission, identifying a candidate profiler-owned prefix.</summary>
        ProfilerCallstackEmission,

        /// <summary>CoreLib profiler context resumption, anchoring optional profiler-prefix ownership attribution.</summary>
        ProfilerContextResume,

        /// <summary>Known profiler, diagnostics, event-source, or box helpers; not required for prefix ownership.</summary>
        ProfilerInfrastructure,

        /// <summary>Generic runtime initialization helpers, not members of a profiler-owned helper family.</summary>
        RuntimeInitializationInfrastructure,

        /// <summary>
        /// CoreLib <c>AsyncStateMachineDispatcherInfo.CreateDispatcher</c> setup, removed only by optional
        /// async-infrastructure cleanup. This is not an async splice boundary.
        /// </summary>
        V1DispatcherCreationInfrastructure,

        /// <summary>A CoreLib box execution-context callback. Its state-machine identity is available for
        /// completed-return ownership, but it is not a V1 resumption carrier.</summary>
        V1StateMachineBoxExecutionContextCallback,
    }

    /// <summary>
    /// One frame of a native (CPU-sample) call stack, as consumed by <see cref="AsyncProfilerCallStackStitcher"/>.
    /// Carries both the <see cref="CodeAddressIndex"/> (for interning the frame into the output stack source)
    /// and the <see cref="MethodIndex"/> (for completion counting and current-frame identity matching) so the stitcher does no
    /// symbol lookups of its own.
    /// </summary>
    internal readonly struct AsyncProfilerStitchSyncFrame
    {
        /// <summary>The frame's code address (used to intern it and to classify it as an async boundary).</summary>
        public readonly CodeAddressIndex CodeAddress;

        /// <summary>The resolved method used for V1/V2 identity matching, or <see cref="MethodIndex.Invalid"/>.</summary>
        public readonly MethodIndex Method;

        /// <summary>Optional async-related synchronous-frame classification.</summary>
        public readonly AsyncProfilerStitchSyncFrameKind Kind;

        /// <summary>
        /// The uniquely resolved state-machine method carried by a CoreLib box execution frame, or
        /// <see cref="MethodIndex.Invalid"/>. Only the leafmost execution carrier preceding any physical
        /// async application body can use this identity for V1 alignment.
        /// </summary>
        public readonly MethodIndex BoxedStateMachineMethod;

        public AsyncProfilerStitchSyncFrame(CodeAddressIndex codeAddress, MethodIndex method)
            : this(codeAddress, method, AsyncProfilerStitchSyncFrameKind.None)
        {
        }

        public AsyncProfilerStitchSyncFrame(CodeAddressIndex codeAddress, MethodIndex method, AsyncProfilerStitchSyncFrameKind kind)
            : this(codeAddress, method, kind, MethodIndex.Invalid)
        {
        }

        /// <summary>Creates a native frame with an optional uniquely resolved boxed state-machine method.</summary>
        public AsyncProfilerStitchSyncFrame(
            CodeAddressIndex codeAddress, MethodIndex method, AsyncProfilerStitchSyncFrameKind kind,
            MethodIndex boxedStateMachineMethod)
        {
            CodeAddress = codeAddress;
            Method = method;
            Kind = kind;
            BoxedStateMachineMethod = boxedStateMachineMethod;
        }
    }

    /// <summary>
    /// One frame of a stitched call stack: a real sync frame, a logical async frame spliced in for a
    /// suspended await, or an explicit incomplete physical root. For an async frame, <see cref="CodeAddress"/> may be
    /// <see cref="CodeAddressIndex.Invalid"/> (unsymbolized or a synthetic <c>0</c>-IP async frame), in which
    /// case the emitter applies the async leaf policy using <see cref="Segment"/> / <see cref="SegmentFrameIndex"/>.
    /// </summary>
    internal readonly struct AsyncProfilerStitchedFrame : IEquatable<AsyncProfilerStitchedFrame>
    {
        /// <summary>Where this frame came from.</summary>
        public readonly AsyncProfilerStitchedFrameOrigin Origin;

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
        /// The canonical application method carried by a resolved physical V1 box execution frame, or
        /// <see cref="MethodIndex.Invalid"/>. Also retained on synchronous frames for presentation ownership
        /// checks. <see cref="Method"/> and <see cref="CodeAddress"/> retain the runtime helper's physical identity.
        /// </summary>
        public readonly MethodIndex BoxedStateMachineMethod;

        /// <summary>True when a V1 resumption target was matched through a physical box execution frame.
        /// This does not establish application-body execution.</summary>
        public bool IsStateMachineBoxCarrier =>
            (Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent || Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget) &&
            BoxedStateMachineMethod != MethodIndex.Invalid;

        /// <summary>For an async-origin frame, the source async segment; otherwise null.</summary>
        public AsyncProfilerCallStackFrames Segment => m_segmentOrDisplayName as AsyncProfilerCallStackFrames;

        /// <summary>For an async-origin frame, its index within <see cref="Segment"/>; otherwise -1.</summary>
        public readonly int SegmentFrameIndex;

        /// <summary>For a sync-origin frame, its optional async-related synchronous-frame classification.</summary>
        public readonly AsyncProfilerStitchSyncFrameKind SyncFrameKind;

        /// <summary>The presentation requested for this frame.</summary>
        public readonly AsyncProfilerStitchedFramePresentation Presentation;

        /// <summary>The async activation to which this frame belongs, or null for physical root frames.</summary>
        public readonly AsyncProfilerCallStack Context;

        /// <summary>The display label of a custom synthetic frame; otherwise null.</summary>
        internal string DisplayName => m_segmentOrDisplayName as string;

        /// <summary>How <see cref="Context"/> participates in the stitched stack.</summary>
        public AsyncProfilerStitchedFrameContextKind ContextKind =>
            Context == null
                ? AsyncProfilerStitchedFrameContextKind.None
                : (Origin == AsyncProfilerStitchedFrameOrigin.AsyncContextParent ||
                   Origin == AsyncProfilerStitchedFrameOrigin.AsyncContextParentCurrent
                    ? AsyncProfilerStitchedFrameContextKind.ParentContext
                    : AsyncProfilerStitchedFrameContextKind.Active);

        private AsyncProfilerStitchedFrame(
            AsyncProfilerStitchedFrameOrigin origin,
            CodeAddressIndex codeAddress,
            MethodIndex method,
            AsyncProfilerCallStackFrames segment,
            int segmentFrameIndex,
            AsyncProfilerStitchSyncFrameKind syncFrameKind,
            AsyncProfilerStitchedFramePresentation presentation,
            AsyncProfilerCallStack context,
            MethodIndex boxedStateMachineMethod = MethodIndex.Invalid)
        {
            Origin = origin;
            CodeAddress = codeAddress;
            Method = method;
            BoxedStateMachineMethod = boxedStateMachineMethod;
            m_segmentOrDisplayName = segment;
            SegmentFrameIndex = segmentFrameIndex;
            SyncFrameKind = syncFrameKind;
            Presentation = presentation;
            Context = context;
        }

        private AsyncProfilerStitchedFrame(string displayName)
            : this(
                AsyncProfilerStitchedFrameOrigin.Synthetic, CodeAddressIndex.Invalid, MethodIndex.Invalid, null, -1,
                AsyncProfilerStitchSyncFrameKind.None, AsyncProfilerStitchedFramePresentation.Native, null)
        {
            m_segmentOrDisplayName = displayName;
        }

        private AsyncProfilerStitchedFrame(
            AsyncProfilerStitchedFrame frame, AsyncProfilerStitchedFramePresentation presentation)
        {
            this = frame;
            Presentation = presentation;
        }

        /// <summary>Creates a stitched frame that preserves a native sync frame.</summary>
        public static AsyncProfilerStitchedFrame CreateSync(
            AsyncProfilerStitchSyncFrame frame,
            AsyncProfilerCallStack context) =>
            new AsyncProfilerStitchedFrame(
                AsyncProfilerStitchedFrameOrigin.Sync, frame.CodeAddress, frame.Method, null, -1, frame.Kind,
                AsyncProfilerStitchedFramePresentation.Native, context, frame.BoxedStateMachineMethod);

        /// <summary>Represents consumed machinery without attributing its callees to an application body.</summary>
        public static AsyncProfilerStitchedFrame CreateAsyncDispatcherFrames(
            AsyncProfilerStitchSyncFrame frame, AsyncProfilerCallStack context) =>
            CreateSync(frame, context).WithPresentation(AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames);

        /// <summary>
        /// Creates a physically present current frame with its logical async identity. Supplying a V1 boxed
        /// state-machine identity instead creates a resumption target, retaining the helper's sampled address
        /// and captured compiler state without claiming application-body execution.
        /// </summary>
        public static AsyncProfilerStitchedFrame CreateAsyncCurrent(
            CodeAddressIndex codeAddress,
            MethodIndex method,
            AsyncProfilerCallStack context,
            int segmentFrameIndex,
            MethodIndex boxedStateMachineMethod = MethodIndex.Invalid) =>
            new AsyncProfilerStitchedFrame(
                boxedStateMachineMethod == MethodIndex.Invalid
                    ? AsyncProfilerStitchedFrameOrigin.AsyncCurrent : AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget,
                codeAddress, method, context.Frames, segmentFrameIndex,
                AsyncProfilerStitchSyncFrameKind.None,
                context.Frames.Kind == AsyncCallstackKind.StateMachineAsync
                    ? AsyncProfilerStitchedFramePresentation.LogicalStateMachineMethod
                    : AsyncProfilerStitchedFramePresentation.Native,
                context, boxedStateMachineMethod);

        /// <summary>Creates a physically present V1 frame verified as logically completed.</summary>
        public static AsyncProfilerStitchedFrame CreateAsyncCompleting(
            CodeAddressIndex codeAddress,
            MethodIndex method,
            AsyncProfilerCallStack context,
            int segmentFrameIndex) =>
            new AsyncProfilerStitchedFrame(
                AsyncProfilerStitchedFrameOrigin.AsyncCompleting, codeAddress, method, context.Frames, segmentFrameIndex,
                AsyncProfilerStitchSyncFrameKind.None, AsyncProfilerStitchedFramePresentation.LogicalStateMachineMethod, context);

        /// <summary>Attributes verified V1 return-unwind work to the completed activation's logical root.</summary>
        public static AsyncProfilerStitchedFrame CreateAsyncCompletionUnwind(AsyncProfilerCallStack context) =>
            new AsyncProfilerStitchedFrame(
                AsyncProfilerStitchedFrameOrigin.AsyncCompletionUnwind,
                context.Frames.CodeAddressAt(context.Frames.FrameCount - 1),
                MethodIndex.Invalid, context.Frames, context.Frames.FrameCount - 1,
                AsyncProfilerStitchSyncFrameKind.None, AsyncProfilerStitchedFramePresentation.LogicalStateMachineMethod, context);

        /// <summary>Creates a suspended async caller frame from an async call-stack segment.</summary>
        public static AsyncProfilerStitchedFrame CreateAsync(AsyncProfilerCallStack context, int segmentFrameIndex) =>
            new AsyncProfilerStitchedFrame(
                AsyncProfilerStitchedFrameOrigin.AsyncRemaining,
                context.Frames.CodeAddressAt(segmentFrameIndex),
                MethodIndex.Invalid,
                context.Frames,
                segmentFrameIndex,
                AsyncProfilerStitchSyncFrameKind.None,
                context.Frames.Kind == AsyncCallstackKind.StateMachineAsync
                    ? AsyncProfilerStitchedFramePresentation.LogicalStateMachineMethod
                    : AsyncProfilerStitchedFramePresentation.Native,
                context);

        /// <summary>Creates a historical parent-context frame reached through dispatcher creation relationships.
        /// A current parent frame reflects inferred logical progress, not physically matched execution.</summary>
        public static AsyncProfilerStitchedFrame CreateAsyncContextParent(
            AsyncProfilerCallStack context, int segmentFrameIndex, bool isCurrent = false) =>
            new AsyncProfilerStitchedFrame(
                isCurrent
                    ? AsyncProfilerStitchedFrameOrigin.AsyncContextParentCurrent
                    : AsyncProfilerStitchedFrameOrigin.AsyncContextParent,
                context.Frames.CodeAddressAt(segmentFrameIndex),
                MethodIndex.Invalid,
                context.Frames,
                segmentFrameIndex,
                AsyncProfilerStitchSyncFrameKind.None,
                context.Frames.Kind == AsyncCallstackKind.StateMachineAsync
                    ? AsyncProfilerStitchedFramePresentation.LogicalStateMachineMethod
                    : AsyncProfilerStitchedFramePresentation.Native,
                context);

        /// <summary>Creates an indexed dispatch target without claiming a physically matched current method.</summary>
        public static AsyncProfilerStitchedFrame CreateAsyncDispatchTarget(AsyncProfilerCallStack context, int segmentFrameIndex) =>
            new AsyncProfilerStitchedFrame(
                AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget,
                context.Frames.CodeAddressAt(segmentFrameIndex),
                MethodIndex.Invalid,
                context.Frames,
                segmentFrameIndex,
                AsyncProfilerStitchSyncFrameKind.None,
                context.Frames.Kind == AsyncCallstackKind.StateMachineAsync
                    ? AsyncProfilerStitchedFramePresentation.LogicalStateMachineMethod
                    : AsyncProfilerStitchedFramePresentation.Native,
                context);

        /// <summary>Creates a physically present V1 box resumption target, retaining its captured compiler state
        /// without claiming that the application body has started.</summary>
        public static AsyncProfilerStitchedFrame CreateAsyncDispatchTarget(
            AsyncProfilerStitchSyncFrame carrier, AsyncProfilerCallStack context, int segmentFrameIndex) =>
            new AsyncProfilerStitchedFrame(
                AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget, carrier.CodeAddress, carrier.Method,
                context.Frames, segmentFrameIndex, AsyncProfilerStitchSyncFrameKind.None,
                AsyncProfilerStitchedFramePresentation.LogicalStateMachineMethod, context, carrier.BoxedStateMachineMethod);

        /// <summary>Creates an incomplete physical-root marker without inventing a caller or execution host.</summary>
        public static AsyncProfilerStitchedFrame CreateIncompleteRoot() =>
            new AsyncProfilerStitchedFrame(
                AsyncProfilerStitchedFrameOrigin.IncompleteRoot, CodeAddressIndex.Invalid, MethodIndex.Invalid, null, -1,
                AsyncProfilerStitchSyncFrameKind.None, AsyncProfilerStitchedFramePresentation.Native, null);

        internal static AsyncProfilerStitchedFrame CreateSynthetic(string displayName) =>
            new AsyncProfilerStitchedFrame(displayName);

        /// <summary>Returns this frame with a different presentation while preserving its structural identity.</summary>
        public AsyncProfilerStitchedFrame WithPresentation(AsyncProfilerStitchedFramePresentation presentation) =>
            new AsyncProfilerStitchedFrame(this, presentation);

        public bool Equals(AsyncProfilerStitchedFrame other) =>
            Origin == other.Origin &&
            CodeAddress == other.CodeAddress &&
            Method == other.Method &&
            BoxedStateMachineMethod == other.BoxedStateMachineMethod &&
            SegmentFrameIndex == other.SegmentFrameIndex &&
            SyncFrameKind == other.SyncFrameKind &&
            Presentation == other.Presentation &&
            ReferenceEquals(Context, other.Context) &&
            object.Equals(m_segmentOrDisplayName, other.m_segmentOrDisplayName);

        public override bool Equals(object obj) =>
            obj is AsyncProfilerStitchedFrame other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = (int)Origin;
                hash = (hash * 397) ^ (int)CodeAddress;
                hash = (hash * 397) ^ (int)Method;
                hash = (hash * 397) ^ (int)BoxedStateMachineMethod;
                hash = (hash * 397) ^ SegmentFrameIndex;
                hash = (hash * 397) ^ (int)SyncFrameKind;
                hash = (hash * 397) ^ (int)Presentation;
                hash = (hash * 397) ^ (Context?.GetHashCode() ?? 0);
                return (hash * 397) ^ (m_segmentOrDisplayName?.GetHashCode() ?? 0);
            }
        }

        // Synthetic labels reuse the segment reference slot to keep ordinary stitched frames compact.
        private readonly object m_segmentOrDisplayName;
    }

    /// <summary>The complete leaf-to-root output span emitted for one active async activation.</summary>
    internal readonly struct AsyncProfilerSegmentPlacement
    {
        internal AsyncProfilerSegmentPlacement(
            AsyncProfilerCallStack activation, int leafIndex, int rootIndexExclusive,
            CodeAddressIndex instrumentedDispatch = CodeAddressIndex.Invalid,
            CodeAddressIndex dispatch = CodeAddressIndex.Invalid)
        {
            Activation = activation;
            LeafIndex = leafIndex;
            RootIndexExclusive = rootIndexExclusive;
            InstrumentedDispatch = instrumentedDispatch;
            Dispatch = dispatch;
        }

        internal AsyncProfilerCallStack Activation { get; }
        internal int LeafIndex { get; }
        internal int RootIndexExclusive { get; }
        // Retain the consumed V1 dispatcher or V2 pair for lazy verification, without enlarging every physical frame.
        internal CodeAddressIndex InstrumentedDispatch { get; }
        internal CodeAddressIndex Dispatch { get; }
    }

    /// <summary>
    /// Counters and bounded messages gathered while stitching and applying presentation transforms.
    /// Includes successful processing and expected fallbacks as well as soft anomalies; nonzero counters do not
    /// by themselves indicate failure. Diagnostic conditions do not throw.
    /// </summary>
    internal sealed class AsyncProfilerCallStackDiagnostics
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

        /// <summary>Physical V1 return-unwind frames normalized by default presentation using a record-verified
        /// completed activation or a truncated completed prefix with a physical finishing body.</summary>
        public int V1CompletionUnwindFramesCollapsed;

        /// <summary>V1 segments whose completion evidence cannot be safely aligned to a physical anchor,
        /// including repeated occurrences or records beyond the matched prefix. Their physical slices are retained.</summary>
        public int V1CompletionAlignmentAmbiguous;

        /// <summary>V2 samples whose inner activation retained its sync layout: either a sampled wrapper/resume
        /// stub had no identifiable indexed dispatch target, or the sample sat in dispatch-continuation plumbing
        /// without a wrapper or another verified target. No inner logical frames are spliced.
        /// Independently verified enclosing activations may still be stitched.
        /// Enabled presentation transforms may subsequently normalize that layout.</summary>
        public int V2SyncLayoutUsed;

        /// <summary>V2 sampled-leaf continuation wrappers consumed by structural dispatch-target projection
        /// or dropped from a retained synchronous layout by conservative presentation.</summary>
        public int V2LeafWrapperDropped;

        /// <summary>V2 dispatch-continuation plumbing collapsed root-ward of a stitched continuation-wrapper,
        /// and verified resume stubs consumed immediately leaf-ward of it. The reconstructed async stack
        /// represents that machinery.</summary>
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

        /// <summary>Recognized V1/V2 runtime-infrastructure frames hidden or represented by dispatcher markers by
        /// <see cref="AsyncProfilerCallStackTransformPipeline.EnableInfrastructureCleanup"/>.
        /// Does not include frames removed by structural stitching or conservative normalization.</summary>
        public int AsyncInfrastructureFramesCollapsed;

        /// <summary>Runtime-owned frames atomically folded between a matched async activation and thread-pool
        /// dispatch by the optional cleanup. A subset of <see cref="AsyncInfrastructureFramesCollapsed"/>.</summary>
        public int RuntimeResumptionBridgeFramesCollapsed;

        /// <summary>Resolved profiler helpers and a verified entry spine represented by a dispatcher marker,
        /// preserving native and unrecognized callees.
        /// A subset of <see cref="AsyncInfrastructureFramesCollapsed"/>.</summary>
        public int ProfilerEnvelopeFramesCollapsed;

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
            V1CompletionUnwindFramesCollapsed = 0;
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
            ProfilerEnvelopeFramesCollapsed = 0;
            AsyncReentrantHandoffFramesCollapsed = 0;
            V2ReentrantHandoffFramesCollapsed = 0;
            V2CompletionHandoffFramesCollapsed = 0;
            MessagesDropped = 0;
            m_messages?.Clear();
        }

        internal void AddTo(AsyncProfilerCallStackDiagnostics target)
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
            target.V1CompletionUnwindFramesCollapsed += V1CompletionUnwindFramesCollapsed;
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
            target.ProfilerEnvelopeFramesCollapsed += ProfilerEnvelopeFramesCollapsed;
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
    internal sealed class AsyncProfilerStitchResult
    {
        /// <summary>Creates a result retaining the supplied frame list and diagnostics without copying them.</summary>
        public AsyncProfilerStitchResult(IReadOnlyList<AsyncProfilerStitchedFrame> frames, AsyncProfilerCallStackDiagnostics diagnostics)
            : this(frames, diagnostics, Array.Empty<AsyncProfilerSegmentPlacement>())
        {
        }

        internal AsyncProfilerStitchResult(
            IReadOnlyList<AsyncProfilerStitchedFrame> frames,
            AsyncProfilerCallStackDiagnostics diagnostics,
            IReadOnlyList<AsyncProfilerSegmentPlacement> segmentPlacements)
        {
            Frames = frames ?? throw new ArgumentNullException(nameof(frames));
            Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            SegmentPlacements =
                segmentPlacements ?? throw new ArgumentNullException(nameof(segmentPlacements));
        }

        /// <summary>The stitched frames, leaf-&gt;root (the native leaf first, the thread/process root last).</summary>
        public IReadOnlyList<AsyncProfilerStitchedFrame> Frames { get; }

        /// <summary>Counters and bounded messages for stitching, fallbacks, and any subsequent presentation transforms.</summary>
        public AsyncProfilerCallStackDiagnostics Diagnostics { get; }

        internal IReadOnlyList<AsyncProfilerSegmentPlacement> SegmentPlacements { get; }
    }

    /// <summary>
    /// The pure async-CPU stitch core: merges a native (CPU-sample) sync stack with the async call stack
    /// segments active at the same instant into a single logical call stack, splicing each suspended await's
    /// logical callers in place of its dispatch machinery.
    /// <para>
    /// This is an interleaved, segment-lockstep, index-aware two-pointer replacement walk:
    /// one pointer down the sync stack (leaf-&gt;root), one down the async segment list (leaf-&gt;root),
    /// consumed in strict lockstep. For each segment the walk (a) finds the sync frame that is the segment's
    /// dispatch boundary, (b) emits the real sync frames leaf-ward of it (they are the segment's already-completed
    /// and currently-running frames, physically present on the native stack), then (c) splices the segment's
    /// root-ward remaining frames (the suspended async callers) in place of the dispatch machinery.
    /// If a V1 boundary is verified but its indexed current method has no separate physical match, the result
    /// retains that dispatcher and inserts an explicit <see cref="AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget"/>.
    /// This preserves the known logical chain without asserting that the target body is physically executing.
    /// A missing inner V1 boundary does not consume a verified enclosing V2 current frame. Unique inner
    /// alignment replaces only its completed transition tail, retaining the inter-context physical span
    /// for the enclosing activation.
    /// A sampled V2 wrapper identifies an indexed dispatch target through completion records or its
    /// activation-relative wrapper position, without claiming a physically executing application body.
    /// When that target is unavailable, its physical entry slice is retained; independently verified
    /// enclosing activations can still be stitched.
    /// At verified captured position zero with a zero diagnostic IP, the continuation entry is instead
    /// represented as <see cref="AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames"/>. Physical
    /// callees remain intact; pending logical frames and historical creation parents are not projected.
    /// Without a wrapper, available method-completion counting can identify the remaining target, including
    /// frame zero, of a single active V2 segment at its innermost complete dispatch-continuation pair, provided
    /// index history rules out an intervening activation. The verified dispatch pair becomes one
    /// <see cref="AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames"/> marker, while physical callees
    /// and root-ward callers remain intact. The captured target address is logical provenance, not a sampled
    /// application IP.
    /// A missing outermost dispatcher can instead be recovered from a unique ordered physical-method match.
    /// If its completion position agrees and the root-ward span is resolved and contains no other dispatcher,
    /// that incomplete span is replaced by <see cref="AsyncProfilerStitchedFrameOrigin.IncompleteRoot"/>. This does not
    /// depend on the captured frame count, helper names, or a thread-pool root. A surviving outermost V1
    /// dispatcher also marks an incomplete root when the capture ends at that resolved dispatcher or within its
    /// fully resolved, same-runtime-profile caller-side plumbing. Arbitrary managed/native callers, unresolved
    /// spans, and unrecognized callers beyond an observed dispatcher are retained. V2 recovery without
    /// method-completion events must also respect recorded wrapper-reset and exception-completion lower bounds.
    /// </para>
    /// <para>
    /// This type is intentionally free of any <c>StackSource</c> / interning dependency: it consumes materialized
    /// <see cref="AsyncProfilerStitchSyncFrame"/>s and produces a flat <see cref="AsyncProfilerStitchResult"/> so it can be unit-tested in
    /// isolation. A separate emitter turns the result into a <c>MutableTraceEventStackSource</c> call stack.
    /// </para>
    /// </summary>
    /// <remarks>
    /// Structural boundary replacement is separate from <see cref="AsyncProfilerCallStackTransformPipeline"/>'s optional
    /// presentation rules. A null/empty segment list or a segment with no frames preserves the supplied physical
    /// stack. Public <c>Stitch</c> overloads allocate their output lists and do not mutate the supplied stacks.
    /// </remarks>
    internal static class AsyncProfilerCallStackStitcher
    {
        /// <summary>
        /// Stitches <paramref name="syncLeafToRoot"/> (a native CPU-sample stack, leaf-&gt;root) with the async
        /// call stacks <paramref name="segmentsRootToLeaf"/> active at <paramref name="timestampQpc"/> (as returned by
        /// <see cref="AsyncProfilerCallStacksIndex.GetCallStacks(AsyncProfilerThreadKey, long)"/>, i.e. depth-ascending =
        /// root-&gt;leaf). Convenience overload over
        /// <see cref="Stitch(IReadOnlyList{AsyncProfilerStitchSyncFrame}, IReadOnlyList{AsyncProfilerCallStack}, long, Func{CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo}, Func{AsyncCallstackKind, bool}, Func{CodeAddressIndex, MethodIndex}, bool)"/>
        /// that sources boundary classification from a <see cref="AsyncProfilerCallStackStitchBoundaryCache"/> and the
        /// completion-events-observed predicate from an <see cref="AsyncProfilerCallStacksIndex"/>.
        /// </summary>
        /// <param name="syncLeafToRoot">The native sync stack, leaf frame first, thread/process root last.</param>
        /// <param name="segmentsRootToLeaf">The async segments active at <paramref name="timestampQpc"/>, root-&gt;leaf
        /// (they are reversed internally to leaf-&gt;root to walk in lockstep with the sync stack).</param>
        /// <param name="timestampQpc">The sample time, in the trace's QPC domain.</param>
        /// <param name="boundaries">The per-trace boundary-method cache.</param>
        /// <param name="index">The async index, queried for whether completion events were observed in each
        /// activation's metadata/configuration epoch, and for activation history when projecting a V2 target
        /// without a wrapper.</param>
        /// <param name="processIndex">The process instance that emitted the sampled stack.</param>
        /// <param name="methodOf">Maps a <see cref="CodeAddressIndex"/> to its <see cref="MethodIndex"/>
        /// (typically <c>traceLog.CodeAddresses.MethodIndex</c>); used for the V1 identity match and V2 adjacency check.</param>
        /// <param name="trace">When true, a per-segment happy-path note (kind / boundary / completed count /
        /// spliced range) is appended to <see cref="AsyncProfilerCallStackDiagnostics.Messages"/> in addition to the anomaly
        /// notes. Off by default; intended for debugging the stitch flow.</param>
        public static AsyncProfilerStitchResult Stitch(
            IReadOnlyList<AsyncProfilerStitchSyncFrame> syncLeafToRoot,
            IReadOnlyList<AsyncProfilerCallStack> segmentsRootToLeaf,
            long timestampQpc,
            AsyncProfilerCallStackStitchBoundaryCache boundaries,
            AsyncProfilerCallStacksIndex index,
            ProcessIndex processIndex,
            Func<CodeAddressIndex, MethodIndex> methodOf,
            bool trace = false)
        {
            if (boundaries is null) throw new ArgumentNullException(nameof(boundaries));
            if (index is null) throw new ArgumentNullException(nameof(index));
            if (syncLeafToRoot is null) throw new ArgumentNullException(nameof(syncLeafToRoot));
            if (methodOf is null) throw new ArgumentNullException(nameof(methodOf));

            var diagnostics = new AsyncProfilerCallStackDiagnostics();
            var output = new List<AsyncProfilerStitchedFrame>(syncLeafToRoot.Count + 8);
            var placements = new List<AsyncProfilerSegmentPlacement>(segmentsRootToLeaf?.Count ?? 0);
            StitchInto(syncLeafToRoot, segmentsRootToLeaf, timestampQpc, boundaries.Classify, null,
                index.MethodCompletionObserved, processIndex, methodOf, trace, output, diagnostics, placements,
                boundaries.GetRuntimeProfile, boundaries.RuntimeProfiles.GetProcessProfile(processIndex),
                boundaries.TraceLog, index, boundaries.RuntimeProfiles);
            return new AsyncProfilerStitchResult(output, diagnostics, placements);
        }

        /// <summary>
        /// Stitches <paramref name="syncLeafToRoot"/> (a native CPU-sample stack, leaf-&gt;root) with the async
        /// call stacks <paramref name="segmentsRootToLeaf"/> active at <paramref name="timestampQpc"/> (depth-ascending =
        /// root-&gt;leaf). This overload takes the boundary classifier and completion-events predicate as delegates
        /// so it has no dependency on <see cref="AsyncProfilerCallStackStitchBoundaryCache"/> / <see cref="AsyncProfilerCallStacksIndex"/>
        /// and can be unit-tested with synthetic code addresses.
        /// </summary>
        /// <param name="syncLeafToRoot">The native sync stack, leaf frame first, thread/process root last.</param>
        /// <param name="segmentsRootToLeaf">The async segments active at <paramref name="timestampQpc"/>, root-&gt;leaf.</param>
        /// <param name="timestampQpc">The sample time, in the trace's QPC domain.</param>
        /// <param name="classify">Classifies a sync frame's <see cref="CodeAddressIndex"/> as an async dispatch boundary.</param>
        /// <param name="methodCompletionObserved">True if <c>CompleteMethod</c> (normal completion) events were
        /// observed for the supplied segments' kind and configuration (see
        /// <see cref="AsyncProfilerCallStacksIndex.MethodCompletionObserved(AsyncProfilerCallStack)"/>).
        /// V1 always adds recorded exception deltas. V2 adds them on the event-counting path, not on the wrapper
        /// fallback, whose index already includes unwinds. Use the index/cache overload for per-activation epoch selection.</param>
        /// <param name="methodOf">Maps a <see cref="CodeAddressIndex"/> to its <see cref="MethodIndex"/>; used for
        /// the V1 identity match and V2 adjacency check.</param>
        /// <param name="trace">When true, appends a per-segment happy-path note (kind / boundary / completed count /
        /// spliced range) to <see cref="AsyncProfilerCallStackDiagnostics.Messages"/>. Off by default; for debugging the flow.</param>
        public static AsyncProfilerStitchResult Stitch(
            IReadOnlyList<AsyncProfilerStitchSyncFrame> syncLeafToRoot,
            IReadOnlyList<AsyncProfilerCallStack> segmentsRootToLeaf,
            long timestampQpc,
            Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classify,
            Func<AsyncCallstackKind, bool> methodCompletionObserved,
            Func<CodeAddressIndex, MethodIndex> methodOf,
            bool trace = false)
        {
            if (syncLeafToRoot is null) throw new ArgumentNullException(nameof(syncLeafToRoot));
            if (classify is null) throw new ArgumentNullException(nameof(classify));
            if (methodCompletionObserved is null) throw new ArgumentNullException(nameof(methodCompletionObserved));
            if (methodOf is null) throw new ArgumentNullException(nameof(methodOf));

            var diagnostics = new AsyncProfilerCallStackDiagnostics();
            var output = new List<AsyncProfilerStitchedFrame>(syncLeafToRoot.Count + 8);
            var placements = new List<AsyncProfilerSegmentPlacement>(segmentsRootToLeaf?.Count ?? 0);
            StitchInto(syncLeafToRoot, segmentsRootToLeaf, timestampQpc, classify, methodCompletionObserved, null,
                ProcessIndex.Invalid, methodOf, trace, output, diagnostics, placements, null, null, null);
            return new AsyncProfilerStitchResult(output, diagnostics, placements);
        }

        internal static void StitchInto(
            IReadOnlyList<AsyncProfilerStitchSyncFrame> syncLeafToRoot,
            IReadOnlyList<AsyncProfilerCallStack> segmentsRootToLeaf,
            long timestampQpc,
            Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classify,
            Func<ProcessIndex, AsyncCallstackKind, long, bool> methodCompletionObserved,
            ProcessIndex processIndex,
            Func<CodeAddressIndex, MethodIndex> methodOf,
            bool trace,
            List<AsyncProfilerStitchedFrame> output,
            AsyncProfilerCallStackDiagnostics diagnostics)
        {
            StitchInto(
                syncLeafToRoot, segmentsRootToLeaf, timestampQpc, classify, methodCompletionObserved,
                processIndex, methodOf, trace, output, diagnostics, null);
        }

        internal static void StitchInto(
            IReadOnlyList<AsyncProfilerStitchSyncFrame> syncLeafToRoot,
            IReadOnlyList<AsyncProfilerCallStack> segmentsRootToLeaf,
            long timestampQpc,
            Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classify,
            Func<ProcessIndex, AsyncCallstackKind, long, bool> methodCompletionObserved,
            ProcessIndex processIndex,
            Func<CodeAddressIndex, MethodIndex> methodOf,
            bool trace,
            List<AsyncProfilerStitchedFrame> output,
            AsyncProfilerCallStackDiagnostics diagnostics,
            List<AsyncProfilerSegmentPlacement> placements,
            Func<CodeAddressIndex, AsyncProfilerRuntimeRuleProfile> runtimeProfileResolver = null,
            AsyncProfilerRuntimeRuleProfile runtimeProfile = null,
            TraceLog runtimeTraceLog = null,
            AsyncProfilerCallStacksIndex index = null,
            AsyncProfilerRuntimeRuleProfileCache runtimeProfiles = null)
        {
            if (syncLeafToRoot is null) throw new ArgumentNullException(nameof(syncLeafToRoot));
            if (classify is null) throw new ArgumentNullException(nameof(classify));
            if (methodCompletionObserved is null) throw new ArgumentNullException(nameof(methodCompletionObserved));
            if (methodOf is null) throw new ArgumentNullException(nameof(methodOf));
            if (output is null) throw new ArgumentNullException(nameof(output));
            if (diagnostics is null) throw new ArgumentNullException(nameof(diagnostics));

            StitchInto(syncLeafToRoot, segmentsRootToLeaf, timestampQpc, classify, null, methodCompletionObserved,
                processIndex, methodOf, trace, output, diagnostics, placements,
                runtimeProfileResolver, runtimeProfile, runtimeTraceLog, index, runtimeProfiles);
        }

        private static void StitchInto(
            IReadOnlyList<AsyncProfilerStitchSyncFrame> syncLeafToRoot,
            IReadOnlyList<AsyncProfilerCallStack> segmentsRootToLeaf,
            long timestampQpc,
            Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classify,
            Func<AsyncCallstackKind, bool> methodCompletionObserved,
            Func<ProcessIndex, AsyncCallstackKind, long, bool> methodCompletionObservedByProcess,
            ProcessIndex processIndex,
            Func<CodeAddressIndex, MethodIndex> methodOf,
            bool trace,
            List<AsyncProfilerStitchedFrame> output,
            AsyncProfilerCallStackDiagnostics diagnostics,
            List<AsyncProfilerSegmentPlacement> placements,
            Func<CodeAddressIndex, AsyncProfilerRuntimeRuleProfile> runtimeProfileResolver,
            AsyncProfilerRuntimeRuleProfile runtimeProfile,
            TraceLog runtimeTraceLog,
            AsyncProfilerCallStacksIndex index = null,
            AsyncProfilerRuntimeRuleProfileCache runtimeProfiles = null)
        {
            output.Clear();
            diagnostics.Clear();
            placements?.Clear();
            runtimeProfile = runtimeProfile ?? AsyncProfilerRuntimeRuleProfile.Common;
            runtimeProfiles = runtimeProfiles ??
                (runtimeTraceLog == null ? null : new AsyncProfilerRuntimeRuleProfileCache(runtimeTraceLog.ModuleFiles));

            // With no segment there are no suspended async callers to splice. An empty segment is also unusable: it may
            // identify an active async context, but it has no current frame with which to align a physical boundary.
            // In either case emit the sync stack verbatim so no continuation-wrapper or dispatcher frame is removed
            // without logical async frames to replace it.
            if (segmentsRootToLeaf is null || segmentsRootToLeaf.Count == 0 || HasEmptySegment(segmentsRootToLeaf))
            {
                for (int i = 0; i < syncLeafToRoot.Count; i++)
                {
                    output.Add(AsyncProfilerStitchedFrame.CreateSync(
                        syncLeafToRoot[i], null));
                }
                return;
            }

            int pSync = 0;
            int firstSegmentIndex = segmentsRootToLeaf.Count - 1;

            // A sampled V2 wrapper or resume stub can identify its dispatch target without a physical body match.
            // Unavailable target identity or position retains the sync-layout fallback:
            //   * Case 2 - the wrapper or resume stub is the sampled leaf, but its indexed target cannot be identified.
            //     Retain only the inner entry slice when an enclosing activation is independently verified.
            //   * Case 3 - no wrapper anywhere, but the sample sits in dispatch-continuation plumbing:
            //     retain the sync layout unless completion counting establishes a unique remaining target.
            // A V2 segment whose sync stack carries no async-boundary frame at all is a different anomaly (e.g. a
            // truncated stack); it falls through to the append-only degradation in the loop below, unchanged.
            if (segmentsRootToLeaf[segmentsRootToLeaf.Count - 1].Frames.Kind == AsyncCallstackKind.RuntimeAsync)
            {
                int wrapperPos = FirstWrapperIndex(syncLeafToRoot, classify, out bool hasDispatchContinuation);
                bool wrapperFound = wrapperPos < syncLeafToRoot.Count;

                if (wrapperFound &&
                    (wrapperPos == 0 || (wrapperPos == 1 && IsV2ResumeStub(syncLeafToRoot[0]))) &&
                    !HasV2DispatchTarget(segmentsRootToLeaf[firstSegmentIndex], timestampQpc,
                        classify(syncLeafToRoot[wrapperPos].CodeAddress).WrapperIndex,
                        methodCompletionObserved, methodCompletionObservedByProcess, processIndex))
                {
                    int physicalEnd = syncLeafToRoot.Count;
                    if (firstSegmentIndex > 0)
                    {
                        AsyncProfilerRuntimeRuleProfile entryProfile = runtimeProfileResolver == null ? runtimeProfile :
                            runtimeProfileResolver(syncLeafToRoot[wrapperPos].CodeAddress);
                        int entryEnd = wrapperPos + 1 + MatchDispatchPlumbing(entryProfile, AsyncCallstackKind.RuntimeAsync,
                            syncLeafToRoot, wrapperPos, wrapperPos + 1, classify, runtimeProfileResolver, runtimeTraceLog, runtimeProfiles);

                        AsyncProfilerCallStack outer = segmentsRootToLeaf[firstSegmentIndex - 1];
                        bool outerVerified = outer.Frames.Kind == AsyncCallstackKind.RuntimeAsync
                            ? FindEnclosingV2CurrentSyncFrame(
                                outer, syncLeafToRoot, entryEnd, timestampQpc, classify,
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
                        output.Add(AsyncProfilerStitchedFrame.CreateSync(
                            syncLeafToRoot[i], null));
                    }
                    if (trace)
                    {
                        diagnostics.Note(physicalEnd == syncLeafToRoot.Count
                            ? "V2 not stitched: sampled dispatch entry has no identifiable target; used the sync layout."
                            : "Inner V2 not stitched: sampled dispatch entry has no identifiable target; retained its entry slice and continued with the enclosing activation.");
                    }
                    if (physicalEnd == syncLeafToRoot.Count)
                    {
                        return;
                    }

                    // No logical inner anchor or async-frame placement: entry work does not prove body execution.
                    diagnostics.SegmentsProcessed++;
                    pSync = physicalEnd;
                    firstSegmentIndex--;
                }

                if (!wrapperFound && hasDispatchContinuation)
                {
                    AsyncProfilerCallStack segment = segmentsRootToLeaf[0];
                    if (segmentsRootToLeaf.Count == 1 &&
                        TryFindV2DispatchTargetWithoutWrapper(
                            segment, syncLeafToRoot, timestampQpc, classify, methodCompletionObserved,
                            methodCompletionObservedByProcess, processIndex, methodOf, runtimeTraceLog, runtimeProfiles, index,
                            out int dispatchStart, out int dispatchEnd, out int completed))
                    {
                        for (int i = 0; i < dispatchStart; i++)
                        {
                            output.Add(AsyncProfilerStitchedFrame.CreateSync(syncLeafToRoot[i], segment));
                        }
                        output.Add(AsyncProfilerStitchedFrame.CreateAsyncDispatcherFrames(syncLeafToRoot[dispatchStart], segment));
                        output.Add(AsyncProfilerStitchedFrame.CreateAsyncDispatchTarget(segment, completed));
                        for (int k = completed + 1; k < segment.Frames.FrameCount; k++)
                        {
                            output.Add(AsyncProfilerStitchedFrame.CreateAsync(segment, k));
                        }
                        placements?.Add(new AsyncProfilerSegmentPlacement(segment, 0, output.Count,
                            syncLeafToRoot[dispatchStart].CodeAddress, syncLeafToRoot[dispatchStart + 1].CodeAddress));
                        for (int i = dispatchEnd; i < syncLeafToRoot.Count; i++)
                        {
                            output.Add(AsyncProfilerStitchedFrame.CreateSync(syncLeafToRoot[i], null));
                        }
                        diagnostics.SegmentsProcessed++;
                        diagnostics.V2PlumbingFramesCollapsed += dispatchEnd - dispatchStart;
                        if (trace)
                        {
                            diagnostics.Note($"V2 dispatch target without wrapper: completed={completed}; represented the verified dispatch pair with an async dispatcher marker.");
                        }
                        return;
                    }

                    diagnostics.V2SyncLayoutUsed++;
                    for (int i = 0; i < syncLeafToRoot.Count; i++)
                    {
                        output.Add(AsyncProfilerStitchedFrame.CreateSync(
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
                AsyncProfilerCallStack segment = segmentsRootToLeaf[segmentIndex];
                int segmentLeafIndex = output.Count;
                diagnostics.SegmentsProcessed++;
                AsyncProfilerCallStackFrames frames = segment.Frames;
                AsyncCallstackKind kind = frames.Kind;
                int boundaryPos = FindBoundary(syncLeafToRoot, pSync, kind, classify, out AsyncProfilerCallStackStitchBoundaryInfo boundaryInfo);
                bool boundaryFound = boundaryPos < syncLeafToRoot.Count;
                AsyncProfilerRuntimeRuleProfile segmentProfile = boundaryFound && runtimeProfileResolver != null
                    ? runtimeProfileResolver(syncLeafToRoot[boundaryPos].CodeAddress) : runtimeProfile;
                if (!boundaryFound)
                {
                    diagnostics.BoundariesNotFound++;
                    if (diagnostics.CanRetainMessage)
                    {
                        diagnostics.Note($"No {kind} dispatch boundary found at or root-ward of sync frame {pSync}; recovering a unique physical alignment when available, otherwise appending logical async frames.");
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
                        AsyncProfilerCallStack outer = segmentsRootToLeaf[outerIndex];
                        if (outer.Frames.Kind != AsyncCallstackKind.RuntimeAsync)
                        {
                            continue;
                        }
                        int outerCurrentSyncPos = FindEnclosingV2CurrentSyncFrame(
                            outer, syncLeafToRoot, pSync, timestampQpc, classify,
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
                    allowFullyCompleted, timestampQpc,
                    syncLeafToRoot, pSync, syncEnd, methodCompletionObserved,
                    methodCompletionObservedByProcess,                     processIndex, methodOf, diagnostics, segmentProfile,
                    out bool completionObserved, out int recoveredTailPos,
                    out bool v1CompletionAmbiguous, out bool v1CompletionTailVerified);
                bool fullyCompleted = allowFullyCompleted && completed == frames.FrameCount;
                bool initialV2Dispatch = kind == AsyncCallstackKind.RuntimeAsync &&
                    boundaryFound && boundaryPos > pSync && completed == 0 &&
                    frames.MethodIdAt(0) == 0 && frames.CodeAddressAt(0) == CodeAddressIndex.Invalid &&
                    GetV2DispatchPosition(segment, timestampQpc, boundaryInfo.WrapperIndex,
                        methodCompletionObserved, methodCompletionObservedByProcess, processIndex) == 0;

                // Native entry work is not a mismatched managed body, even when its PDB supplies a MethodIndex.
                bool nativeV2DispatchTarget = kind == AsyncCallstackKind.RuntimeAsync && boundaryFound && !fullyCompleted &&
                    HasV2NativeDispatchEntry(
                        frames, completed, syncLeafToRoot, pSync, boundaryPos, classify, segmentProfile, runtimeTraceLog, runtimeProfiles) &&
                    HasV2DispatchTarget(segment, timestampQpc, boundaryInfo.WrapperIndex,
                        methodCompletionObserved, methodCompletionObservedByProcess, processIndex);
                int currentSyncPos = -1;
                if (!fullyCompleted && !v1CompletionAmbiguous && !nativeV2DispatchTarget && !initialV2Dispatch)
                {
                    currentSyncPos = kind == AsyncCallstackKind.StateMachineAsync
                        ? FindCurrentSyncFrame(
                            frames, completed, syncLeafToRoot, pSync, syncEnd, methodOf,
                            boundaryFound || recoveredTailPos >= 0, segmentProfile)
                        : FindCurrentV2SyncFrame(
                            frames, completed, syncLeafToRoot, pSync, boundaryPos, methodOf, boundaryFound, diagnostics);
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
                        segment, syncLeafToRoot, pSync, timestampQpc, completionObserved,
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
                // transition tail through the verified dispatcher is represented by the logical async stack.
                int physicalEnd = fullyCompleted
                    ? pSync
                    : (incompleteRoot || recoveredInner ||
                       (kind == AsyncCallstackKind.StateMachineAsync && boundaryFound)) &&
                      anchorSyncPos >= 0
                        ? anchorSyncPos + 1
                        : syncEnd;
                bool hasDispatchTarget = completed < frames.FrameCount &&
                    (frames.CodeAddressAt(completed) != CodeAddressIndex.Invalid || frames.MethodIdAt(completed) != 0);
                bool sampledV2ResumeStub = kind == AsyncCallstackKind.RuntimeAsync &&
                    boundaryFound && boundaryPos == pSync + 1 && IsV2ResumeStub(syncLeafToRoot[pSync]);
                if (kind == AsyncCallstackKind.RuntimeAsync && boundaryFound && !fullyCompleted &&
                    boundaryPos > pSync && IsV2ResumeStub(syncLeafToRoot[boundaryPos - 1]) &&
                    (currentSyncPos >= pSync || nativeV2DispatchTarget || (sampledV2ResumeStub && hasDispatchTarget)))
                {
                    // Consume only the single stub connecting a matched body or verified entry work to its wrapper.
                    physicalEnd--;
                    diagnostics.V2PlumbingFramesCollapsed++;
                }
                for (int i = pSync; i < physicalEnd; i++)
                {
                    AsyncProfilerStitchedFrame frame = i == currentSyncPos
                        ? kind == AsyncCallstackKind.StateMachineAsync &&
                          syncLeafToRoot[i].Method != methodOf(frames.CodeAddressAt(completed))
                            ? AsyncProfilerStitchedFrame.CreateAsyncDispatchTarget(syncLeafToRoot[i], segment, completed)
                            : AsyncProfilerStitchedFrame.CreateAsyncCurrent(
                                syncLeafToRoot[i].CodeAddress, syncLeafToRoot[i].Method, segment, completed)
                        : i == completingSyncPos
                            ? AsyncProfilerStitchedFrame.CreateAsyncCompleting(
                                syncLeafToRoot[i].CodeAddress, syncLeafToRoot[i].Method, segment, completed - 1)
                            : AsyncProfilerStitchedFrame.CreateSync(
                                syncLeafToRoot[i], segment);
                    if (frame.IsStateMachineBoxCarrier)
                    {
                        // The box identifies a pending target, not an executing application body.
                        output.Add(AsyncProfilerStitchedFrame.CreateAsyncDispatcherFrames(syncLeafToRoot[i], segment));
                    }
                    output.Add(frame);
                }
                if (initialV2Dispatch)
                {
                    // A zero diagnostic IP still occupies a real continuation slot; do not promote its successor.
                    output[output.Count - 1] = output[output.Count - 1]
                        .WithPresentation(AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames);
                }

                // A known V2 target replaces its wrapper/stub, after any verified native entry work.
                // V1 retains its dispatcher as physical evidence.
                // Neither target claims that the application body is physically executing.
                if (boundaryFound &&
                    (kind == AsyncCallstackKind.StateMachineAsync || boundaryPos == 0 || sampledV2ResumeStub || nativeV2DispatchTarget) &&
                    !fullyCompleted && currentSyncPos < 0 && hasDispatchTarget)
                {
                    if (kind == AsyncCallstackKind.RuntimeAsync && boundaryPos == 0)
                    {
                        diagnostics.V2LeafWrapperDropped++;
                    }
                    else if (kind == AsyncCallstackKind.StateMachineAsync && completingSyncPos < 0)
                    {
                        output.Add(AsyncProfilerStitchedFrame.CreateSync(syncLeafToRoot[boundaryPos], segment));
                    }
                    else if (nativeV2DispatchTarget)
                    {
                        int entry = IsV2ResumeStub(syncLeafToRoot[boundaryPos - 1]) ? boundaryPos - 1 : boundaryPos;
                        output.Add(AsyncProfilerStitchedFrame.CreateAsyncDispatcherFrames(syncLeafToRoot[entry], segment));
                    }
                    output.Add(AsyncProfilerStitchedFrame.CreateAsyncDispatchTarget(segment, completed));
                }

                // Dispatch-only entry work does not establish that the pending logical continuation has resumed.
                int remainingStart = initialV2Dispatch ? frames.FrameCount : completed + 1;
                for (int k = remainingStart; k < frames.FrameCount; k++)
                {
                    output.Add(AsyncProfilerStitchedFrame.CreateAsync(segment, k));
                }

                if (trace && diagnostics.CanRetainMessage)
                {
                    string boundaryDesc = boundaryFound
                        ? (boundaryInfo.Kind == AsyncProfilerCallStackStitchBoundaryKind.V2ContinuationWrapper
                            ? $"{boundaryInfo.Kind}[{boundaryInfo.WrapperIndex}]@sync{boundaryPos}"
                            : $"{boundaryInfo.Kind}@sync{boundaryPos}")
                        : "no-boundary";
                    int splicedCount = frames.FrameCount - remainingStart;
                    if (splicedCount < 0) splicedCount = 0;
                    diagnostics.Note($"segment[{diagnostics.SegmentsProcessed - 1}] kind={kind} boundary={boundaryDesc} " +
                        $"emittedSync={(fullyCompleted ? "none (fully completed)" : $"[{pSync}..{boundaryPos})")} " +
                        $"completed={completed} spliced=[{remainingStart}..{frames.FrameCount}) ({splicedCount} frame(s)) frameCount={frames.FrameCount}");
                }
                else if (trace)
                {
                    diagnostics.MessagesDropped++;
                }

                // Skip the boundary frame itself (the dispatch machinery the async frames replace); keep other frames.
                pSync = recoveredInner
                    ? recoveredTailPos
                    : (boundaryFound ? boundaryPos + 1 : syncEnd);
                CodeAddressIndex instrumentedDispatch = CodeAddressIndex.Invalid;
                CodeAddressIndex dispatch = CodeAddressIndex.Invalid;
                if (boundaryFound)
                {
                    if (kind == AsyncCallstackKind.StateMachineAsync)
                    {
                        dispatch = syncLeafToRoot[boundaryPos].CodeAddress;
                    }
                    // Collapse the dispatch-continuation plumbing for this segment immediately, rather than waiting
                    // until all segments are exhausted. An inner V2 segment may be followed by a preserved bridge
                    // and then an outer V1/V2 segment; leaking these frames into that bridge breaks lockstep alignment.
                    int plumbingCount = MatchDispatchPlumbing(segmentProfile, kind,
                        syncLeafToRoot, boundaryPos, pSync, classify, runtimeProfileResolver, runtimeTraceLog, runtimeProfiles);
                    if (kind == AsyncCallstackKind.RuntimeAsync && plumbingCount == 2)
                    {
                        instrumentedDispatch = syncLeafToRoot[pSync].CodeAddress;
                        dispatch = syncLeafToRoot[pSync + 1].CodeAddress;
                    }
                    pSync += plumbingCount;
                    if (kind == AsyncCallstackKind.StateMachineAsync)
                    {
                        diagnostics.V1InfrastructureFramesCollapsed += plumbingCount;
                    }
                    else
                    {
                        diagnostics.V2PlumbingFramesCollapsed += plumbingCount;
                    }

                    // A surviving dispatcher can still have its required callers cut off inside the runtime plumbing.
                    incompleteRoot = segmentIndex == 0 && kind == AsyncCallstackKind.StateMachineAsync &&
                        pSync == syncLeafToRoot.Count &&
                        IsResolvedRuntimeRootSpan(syncLeafToRoot, boundaryPos, runtimeTraceLog, runtimeProfiles);
                }
                if (!initialV2Dispatch)
                {
                    placements?.Add(new AsyncProfilerSegmentPlacement(
                        segment, segmentLeafIndex, output.Count, instrumentedDispatch, dispatch));
                }
                if (incompleteRoot)
                {
                    output.Add(AsyncProfilerStitchedFrame.CreateIncompleteRoot());
                    diagnostics.IncompleteAsyncRootsNormalized++;
                }
            }

            // Emit the clean sync tail (includes the thread/process root).
            for (int i = pSync; i < syncLeafToRoot.Count; i++)
            {
                output.Add(AsyncProfilerStitchedFrame.CreateSync(
                    syncLeafToRoot[i], null));
            }
        }

        private static bool IsResolvedRuntimeRootSpan(
            IReadOnlyList<AsyncProfilerStitchSyncFrame> frames, int boundaryIndex, TraceLog traceLog,
            AsyncProfilerRuntimeRuleProfileCache runtimeProfiles)
        {
            for (int i = boundaryIndex; i < frames.Count; i++)
            {
                if (frames[i].CodeAddress == CodeAddressIndex.Invalid || frames[i].Method == MethodIndex.Invalid)
                {
                    return false;
                }
            }
            return traceLog == null ||
                runtimeProfiles.IsHostModule(traceLog.CodeAddresses.ModuleFileIndex(frames[boundaryIndex].CodeAddress));
        }

        private static int MatchDispatchPlumbing(
            AsyncProfilerRuntimeRuleProfile profile, AsyncCallstackKind kind, IReadOnlyList<AsyncProfilerStitchSyncFrame> frames,
            int boundaryIndex, int start, Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classify,
            Func<CodeAddressIndex, AsyncProfilerRuntimeRuleProfile> runtimeProfileResolver, TraceLog traceLog,
            AsyncProfilerRuntimeRuleProfileCache runtimeProfiles)
        {
            AsyncProfilerRuntimeSequenceMatch match = profile.MatchDispatchPlumbing(kind, frames, start, frames.Count, classify);
            if (match.Length > frames.Count - start)
            {
                throw new InvalidOperationException("A runtime plumbing matcher returned a span outside its candidate range.");
            }
            if (!match.IsMatch)
            {
                return 0;
            }
            ModuleFileIndex owner = traceLog == null ? ModuleFileIndex.Invalid :
                runtimeProfiles.GetHostModuleOwner(traceLog.CodeAddresses.ModuleFileIndex(frames[boundaryIndex].CodeAddress));
            for (int i = start; i < start + match.Length; i++)
            {
                AsyncProfilerCallStackStitchBoundaryInfo boundary = classify(frames[i].CodeAddress);
                if ((boundary.IsBoundary &&
                     (kind != AsyncCallstackKind.RuntimeAsync ||
                      boundary.Kind != AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation)) ||
                    (runtimeProfileResolver != null && !ReferenceEquals(profile, runtimeProfileResolver(frames[i].CodeAddress))))
                {
                    return 0;
                }
                if (traceLog != null)
                {
                    if (frames[i].Method == MethodIndex.Invalid ||
                        owner == ModuleFileIndex.Invalid ||
                        owner != runtimeProfiles.GetHostModuleOwner(traceLog.CodeAddresses.ModuleFileIndex(frames[i].CodeAddress)))
                    {
                        return 0;
                    }
                }
            }
            return match.Length;
        }

        private static bool HasV2NativeDispatchEntry(
            AsyncProfilerCallStackFrames frames, int completed, IReadOnlyList<AsyncProfilerStitchSyncFrame> sync,
            int pSync, int boundaryPos, Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classify,
            AsyncProfilerRuntimeRuleProfile profile, TraceLog traceLog,
            AsyncProfilerRuntimeRuleProfileCache runtimeProfiles)
        {
            if (traceLog == null || boundaryPos <= pSync || completed < 0 || completed >= frames.FrameCount ||
                sync[boundaryPos].Method == MethodIndex.Invalid)
            {
                return false;
            }
            int entryPos = GetV2CurrentSyncPosition(sync, pSync, boundaryPos);
            if (entryPos < pSync || sync[entryPos].CodeAddress == CodeAddressIndex.Invalid)
            {
                return false;
            }
            ModuleFileIndex module = traceLog.CodeAddresses.ModuleFileIndex(sync[entryPos].CodeAddress);
            if (module == ModuleFileIndex.Invalid)
            {
                return false;
            }
            CodeAddressIndex target = frames.CodeAddressAt(completed);
            MethodIndex targetMethod = target == CodeAddressIndex.Invalid
                ? MethodIndex.Invalid : traceLog.CodeAddresses.MethodIndex(target);
            ModuleFileIndex owner = runtimeProfiles.GetHostModuleOwner(
                traceLog.CodeAddresses.ModuleFileIndex(sync[boundaryPos].CodeAddress));
            if (targetMethod == MethodIndex.Invalid || owner == ModuleFileIndex.Invalid)
            {
                return false;
            }

            if (!profile.IsV2NativeDispatchModule(traceLog.ModuleFiles[module], traceLog.ModuleFiles[owner]))
            {
                return false;
            }

            // Only the adjacent entry needs native-module evidence. Its callees remain physical work,
            // unless a body occurrence or another dispatch entry makes this target attribution ambiguous.
            for (int i = pSync; i <= entryPos; i++)
            {
                if (sync[i].Method == targetMethod ||
                    sync[i].Kind == AsyncProfilerStitchSyncFrameKind.V2ResumeStub ||
                    classify(sync[i].CodeAddress).Kind != AsyncProfilerCallStackStitchBoundaryKind.None)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool HasV2DispatchTarget(
            AsyncProfilerCallStack segment, long timestampQpc, int wrapperIndex,
            Func<AsyncCallstackKind, bool> methodCompletionObserved,
            Func<ProcessIndex, AsyncCallstackKind, long, bool> methodCompletionObservedByProcess,
            ProcessIndex processIndex)
        {
            int completed = GetV2DispatchPosition(segment, timestampQpc, wrapperIndex,
                methodCompletionObserved, methodCompletionObservedByProcess, processIndex);
            return completed >= 0 &&
                (segment.Frames.CodeAddressAt(completed) != CodeAddressIndex.Invalid ||
                 segment.Frames.MethodIdAt(completed) != 0);
        }

        private static int GetV2DispatchPosition(
            AsyncProfilerCallStack segment, long timestampQpc, int wrapperIndex,
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
                return -1;
            }

            // Do not clamp an out-of-range position into a plausible target at either end of the call stack.
            long completed = completionObserved
                ? segment.GetCompletedFrameCount(timestampQpc)
                : (long)segment.GetWrapperResetCount(timestampQpc) * segment.WrapperCount +
                    wrapperIndex - segment.ContinuationIndexBase;
            return completed >= 0 && completed < segment.Frames.FrameCount ? (int)completed : -1;
        }

        private static bool TryFindV2DispatchTargetWithoutWrapper(
            AsyncProfilerCallStack segment, IReadOnlyList<AsyncProfilerStitchSyncFrame> sync, long timestampQpc,
            Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classify,
            Func<AsyncCallstackKind, bool> methodCompletionObserved,
            Func<ProcessIndex, AsyncCallstackKind, long, bool> methodCompletionObservedByProcess,
            ProcessIndex processIndex, Func<CodeAddressIndex, MethodIndex> methodOf, TraceLog traceLog,
            AsyncProfilerRuntimeRuleProfileCache runtimeProfiles, AsyncProfilerCallStacksIndex index,
            out int dispatchStart, out int dispatchEnd, out int completed)
        {
            dispatchStart = -1;
            dispatchEnd = -1;
            completed = segment.GetCompletedFrameCount(timestampQpc);
            bool completionObserved = methodCompletionObserved != null
                ? methodCompletionObserved(segment.Frames.Kind)
                : methodCompletionObservedByProcess(processIndex, segment.Frames.Kind, segment.StartQpc);
            if (!completionObserved || segment.Depth != 0 ||
                timestampQpc < segment.StartQpc || timestampQpc >= segment.EndQpc ||
                completed < 0 || completed >= segment.Frames.FrameCount ||
                segment.Frames.CodeAddressAt(completed) == CodeAddressIndex.Invalid ||
                methodOf(segment.Frames.CodeAddressAt(completed)) == MethodIndex.Invalid ||
                index == null || !index.IsLatestActivation(segment, timestampQpc))
            {
                return false;
            }

            // The history guard rules out a later, popped inner context whose dispatcher could still be above
            // this activation. Earlier, popped dispatchers may remain rootward and are left unassociated.
            for (int i = 0; i < sync.Count; i++)
            {
                AsyncProfilerCallStackStitchBoundaryKind kind = classify(sync[i].CodeAddress).Kind;
                if (kind == AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher ||
                    kind == AsyncProfilerCallStackStitchBoundaryKind.V2ContinuationWrapper)
                {
                    return false;
                }
                if (dispatchEnd < 0 && kind == AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation)
                {
                    dispatchStart = i;
                    dispatchEnd = i + 2;
                    if (dispatchEnd >= sync.Count ||
                        classify(sync[i + 1].CodeAddress).Kind != AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation ||
                        classify(sync[dispatchEnd].CodeAddress).Kind == AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation ||
                        sync[i].Method == MethodIndex.Invalid || sync[i + 1].Method == MethodIndex.Invalid ||
                        sync[i].Method == sync[i + 1].Method)
                    {
                        return false;
                    }
                    if (traceLog != null &&
                        !IsV2DispatchPair(traceLog, runtimeProfiles, sync[i].CodeAddress, sync[i + 1].CodeAddress))
                    {
                        return false;
                    }
                }

                // A physical occurrence of a remaining method needs its own body/activation alignment,
                // rather than a second representation inferred solely from completion records.
                if (sync[i].Method != MethodIndex.Invalid)
                {
                    for (int k = completed; k < segment.Frames.FrameCount; k++)
                    {
                        if (sync[i].Method == methodOf(segment.Frames.CodeAddressAt(k)))
                        {
                            return false;
                        }
                    }
                }
            }
            return dispatchEnd >= 0;
        }

        /// <summary>Verifies the ordered, distinct V2 dispatch methods and their captured CoreLib ownership.</summary>
        internal static bool IsV2DispatchPair(
            TraceLog traceLog, AsyncProfilerRuntimeRuleProfileCache runtimeProfiles,
            CodeAddressIndex instrumented, CodeAddressIndex dispatch)
        {
            if (instrumented == CodeAddressIndex.Invalid || dispatch == CodeAddressIndex.Invalid)
            {
                return false;
            }
            TraceCodeAddresses addresses = traceLog.CodeAddresses;
            MethodIndex instrumentedMethod = addresses.MethodIndex(instrumented);
            MethodIndex dispatchMethod = addresses.MethodIndex(dispatch);
            ModuleFileIndex instrumentedModule = addresses.ModuleFileIndex(instrumented);
            ModuleFileIndex dispatchModule = addresses.ModuleFileIndex(dispatch);
            ModuleFileIndex owner = runtimeProfiles.GetHostModuleOwner(instrumentedModule);
            if (instrumentedMethod == MethodIndex.Invalid || dispatchMethod == MethodIndex.Invalid ||
                instrumentedMethod == dispatchMethod ||
                owner == ModuleFileIndex.Invalid || owner != runtimeProfiles.GetHostModuleOwner(dispatchModule) ||
                !ReferenceEquals(runtimeProfiles.GetProfile(instrumentedModule), runtimeProfiles.GetProfile(dispatchModule)))
            {
                return false;
            }
            string instrumentedName = AsyncProfilerCallStackStitchBoundary.NormalizeMethodName(addresses[instrumented].FullMethodName);
            string dispatchName = AsyncProfilerCallStackStitchBoundary.NormalizeMethodName(addresses[dispatch].FullMethodName);
            return AsyncProfilerCallStackStitchBoundary.GetBareMethodName(instrumentedName) ==
                    AsyncProfilerCallStackStitchBoundary.InstrumentedDispatchContinuationsName &&
                AsyncProfilerCallStackStitchBoundary.GetBareMethodName(dispatchName) ==
                    AsyncProfilerCallStackStitchBoundary.DispatchContinuationsName &&
                AsyncProfilerCallStackStitchBoundary.GetDeclaringTypeName(instrumentedName) ==
                    AsyncProfilerCallStackStitchBoundary.GetDeclaringTypeName(dispatchName);
        }

        private static int FindEnclosingV2CurrentSyncFrame(
            AsyncProfilerCallStack segment, IReadOnlyList<AsyncProfilerStitchSyncFrame> sync, int pSync, long timestampQpc,
            Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classify,
            Func<AsyncCallstackKind, bool> methodCompletionObserved,
            Func<ProcessIndex, AsyncCallstackKind, long, bool> methodCompletionObservedByProcess,
            ProcessIndex processIndex, Func<CodeAddressIndex, MethodIndex> methodOf)
        {
            AsyncProfilerCallStackFrames frames = segment.Frames;
            if (frames.Kind != AsyncCallstackKind.RuntimeAsync)
            {
                return -1;
            }

            int boundaryPos = FindBoundary(sync, pSync, frames.Kind, classify, out AsyncProfilerCallStackStitchBoundaryInfo boundaryInfo);
            int currentSyncPos = GetV2CurrentSyncPosition(sync, pSync, boundaryPos);
            if (boundaryPos == sync.Count || currentSyncPos < pSync || sync[currentSyncPos].Method == MethodIndex.Invalid)
            {
                return -1;
            }

            bool completionObserved = methodCompletionObserved != null
                ? methodCompletionObserved(frames.Kind)
                : methodCompletionObservedByProcess(processIndex, frames.Kind, segment.StartQpc);
            int completed = completionObserved
                ? segment.GetCompletedFrameCount(timestampQpc)
                : segment.GetCompletedFrameCount(timestampQpc, boundaryInfo.WrapperIndex);
            if (completed < 0 || completed >= frames.FrameCount)
            {
                return -1;
            }

            return sync[currentSyncPos].Method == methodOf(frames.CodeAddressAt(completed))
                ? currentSyncPos : -1;
        }

        private static bool TryNormalizeIncompleteRoot(
            AsyncProfilerCallStack segment, IReadOnlyList<AsyncProfilerStitchSyncFrame> sync, int pSync, long timestampQpc,
            bool completionObserved, int completed, int currentSyncPos, int recoveredTailPos,
            Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classify,
            Func<CodeAddressIndex, MethodIndex> methodOf,
            AsyncProfilerRuntimeRuleProfile profile,
            out int normalizedCompleted, out int normalizedCurrentSyncPos)
        {
            normalizedCompleted = completed;
            normalizedCurrentSyncPos = currentSyncPos;
            AsyncProfilerCallStackFrames frames = segment.Frames;
            int recoveredCompleted = completed;
            if (recoveredTailPos < 0 &&
                !TryRecoverTruncatedCompleted(
                    frames, sync, pSync, sync.Count, methodOf, profile, out recoveredCompleted, out _))
            {
                return false;
            }

            if (completionObserved)
            {
                if (recoveredCompleted != segment.GetCompletedFrameCount(timestampQpc))
                {
                    return false;
                }
            }
            else if (frames.Kind == AsyncCallstackKind.RuntimeAsync)
            {
                if (recoveredCompleted < segment.GetCompletedFrameCount(timestampQpc, 0) ||
                    recoveredCompleted < segment.GetExceptionCompletedFrameCount(timestampQpc))
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

                AsyncProfilerCallStackStitchBoundaryKind kind = classify(sync[i].CodeAddress).Kind;
                if (kind == AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher ||
                    kind == AsyncProfilerCallStackStitchBoundaryKind.V2ContinuationWrapper ||
                    kind == AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation)
                {
                    return false;
                }
            }

            normalizedCompleted = recoveredCompleted;
            normalizedCurrentSyncPos = recoveredCurrentSyncPos;
            return true;
        }

        private static bool HasEmptySegment(IReadOnlyList<AsyncProfilerCallStack> segments)
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
            IReadOnlyList<AsyncProfilerStitchSyncFrame> sync, Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classify,
            out bool hasDispatchContinuation)
        {
            hasDispatchContinuation = false;
            for (int i = 0; i < sync.Count; i++)
            {
                AsyncProfilerCallStackStitchBoundaryKind kind = classify(sync[i].CodeAddress).Kind;
                if (kind == AsyncProfilerCallStackStitchBoundaryKind.V2ContinuationWrapper)
                {
                    return i;
                }
                if (kind == AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation)
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
            IReadOnlyList<AsyncProfilerStitchSyncFrame> sync, int pSync, AsyncCallstackKind segmentKind,
            Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classify, out AsyncProfilerCallStackStitchBoundaryInfo boundaryInfo)
        {
            for (int i = pSync; i < sync.Count; i++)
            {
                AsyncProfilerCallStackStitchBoundaryInfo info = classify(sync[i].CodeAddress);
                if (MatchesKind(info.Kind, segmentKind))
                {
                    boundaryInfo = info;
                    return i;
                }
            }

            boundaryInfo = AsyncProfilerCallStackStitchBoundaryInfo.None;
            return sync.Count;
        }

        /// <summary>Whether a boundary kind matches an async segment's kind (V2 vs V1).</summary>
        private static bool MatchesKind(AsyncProfilerCallStackStitchBoundaryKind boundaryKind, AsyncCallstackKind segmentKind)
        {
            switch (segmentKind)
            {
                case AsyncCallstackKind.RuntimeAsync: // V2
                    // Only a continuation-wrapper identifies the V2 resumption position. It may be the sampled
                    // leaf, identifying a dispatch target rather than a physical body. A bare dispatch-continuation frame
                    // is dispatch plumbing, handled by the sync-layout gate in Stitch, not a splice boundary.
                    return boundaryKind == AsyncProfilerCallStackStitchBoundaryKind.V2ContinuationWrapper;
                case AsyncCallstackKind.StateMachineAsync: // V1
                    return boundaryKind == AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The number of leaf-ward frames of <paramref name="segment"/> that have completed by
        /// <paramref name="timestampQpc"/>. Completions come from two independent mechanisms that are combined:
        /// <list type="bullet">
        ///   <item><b>Normal completions</b>: if <c>CompleteMethod</c> events were emitted for this kind
        ///   (<paramref name="methodCompletionObserved"/>), <see cref="AsyncProfilerCallStack.GetMethodCompletedFrameCount"/>
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
            AsyncProfilerCallStack segment, AsyncProfilerCallStackFrames frames, AsyncCallstackKind kind,
            AsyncProfilerCallStackStitchBoundaryInfo boundaryInfo, bool boundaryFound, bool allowFullyCompleted, long timestampQpc,
            IReadOnlyList<AsyncProfilerStitchSyncFrame> sync, int pSync, int boundaryPos,
            Func<AsyncCallstackKind, bool> methodCompletionObserved,
            Func<ProcessIndex, AsyncCallstackKind, long, bool> methodCompletionObservedByProcess,
            ProcessIndex processIndex,
            Func<CodeAddressIndex, MethodIndex> methodOf, AsyncProfilerCallStackDiagnostics diagnostics,
            AsyncProfilerRuntimeRuleProfile profile,
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
                    completed = segment.GetCompletedFrameCount(timestampQpc);
                }
                else
                {
                    // Derive from the continuation-wrapper slot; the wrapper index advances on every completion,
                    // including exceptional unwinds, so no separate exception term is added.
                    int slot = boundaryInfo.Kind == AsyncProfilerCallStackStitchBoundaryKind.V2ContinuationWrapper ? boundaryInfo.WrapperIndex : 0;
                    completed = segment.GetCompletedFrameCount(timestampQpc, slot);
                }
            }
            else // V1: reconcile physical inline progress with recorded completions.
            {
                if (completionObserved)
                {
                    completed = segment.GetCompletedFrameCount(timestampQpc);
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
                    int exceptionCompleted = segment.GetExceptionCompletedFrameCount(timestampQpc);
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
            AsyncProfilerCallStackFrames frames, IReadOnlyList<AsyncProfilerStitchSyncFrame> sync, int pSync, int boundaryPos,
            Func<CodeAddressIndex, MethodIndex> methodOf, AsyncProfilerRuntimeRuleProfile profile,
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
        /// unbounded search through unrelated async callers.
        /// </para>
        /// <para>
        /// If an async frame cannot be verified (for example a <c>0</c>-IP frame), matching stops conservatively,
        /// leaving the remaining frames as logical async callers.
        /// </para>
        /// </summary>
        private static int CountV1Completed(
            AsyncProfilerCallStackFrames frames, IReadOnlyList<AsyncProfilerStitchSyncFrame> sync, int pSync, int boundaryPos,
            int exceptionCompleted, bool boundaryFound, bool allowFullyCompleted, Func<CodeAddressIndex, MethodIndex> methodOf,
            AsyncProfilerRuntimeRuleProfile profile,
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
            AsyncProfilerCallStackFrames frames, int completed, IReadOnlyList<AsyncProfilerStitchSyncFrame> sync,
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
            AsyncProfilerCallStackFrames frames, int completed, IReadOnlyList<AsyncProfilerStitchSyncFrame> sync,
            int pSync, int boundaryPos, Func<CodeAddressIndex, MethodIndex> methodOf,
            bool allowBoxCarrier, AsyncProfilerRuntimeRuleProfile profile)
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
            AsyncProfilerCallStackFrames frames, IReadOnlyList<AsyncProfilerStitchSyncFrame> sync,
            int pSync, int boundaryPos, Func<CodeAddressIndex, MethodIndex> methodOf, AsyncProfilerRuntimeRuleProfile profile)
        {
            int carrier = -1;
            for (int i = pSync; i < boundaryPos; i++)
            {
                AsyncProfilerStitchSyncFrame frame = sync[i];
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
                AsyncProfilerStitchSyncFrame frame = sync[i];
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

        private static bool MatchesV1Method(AsyncProfilerStitchSyncFrame frame, MethodIndex expected, bool allowBoxCarrier) =>
            frame.Method == expected ||
            (allowBoxCarrier &&
             frame.CodeAddress != CodeAddressIndex.Invalid &&
             frame.Method != MethodIndex.Invalid &&
             frame.BoxedStateMachineMethod == expected);

        private static int FindCurrentV2SyncFrame(
            AsyncProfilerCallStackFrames frames,
            int completed,
            IReadOnlyList<AsyncProfilerStitchSyncFrame> sync,
            int pSync,
            int boundaryPos,
            Func<CodeAddressIndex, MethodIndex> methodOf,
            bool boundaryFound,
            AsyncProfilerCallStackDiagnostics diagnostics)
        {
            int currentSyncPos = GetV2CurrentSyncPosition(sync, pSync, boundaryPos);
            if (currentSyncPos < pSync || completed < 0 || completed >= frames.FrameCount)
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
                    diagnostics.Note($"V2 current async method (segment frame {completed}) did not match the sync frame leaf-ward of the boundary and any recognized resume stub (frame {currentSyncPos}).");
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

        private static int GetV2CurrentSyncPosition(
            IReadOnlyList<AsyncProfilerStitchSyncFrame> sync, int pSync, int boundaryPos)
        {
            int position = boundaryPos - 1;
            if (boundaryPos < sync.Count && position >= pSync && IsV2ResumeStub(sync[position]))
            {
                position--;
            }
            return position;
        }

        private static bool IsV2ResumeStub(AsyncProfilerStitchSyncFrame frame) =>
            frame.Kind == AsyncProfilerStitchSyncFrameKind.V2ResumeStub &&
            frame.CodeAddress != CodeAddressIndex.Invalid && frame.Method != MethodIndex.Invalid;

    }
}
