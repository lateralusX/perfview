// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

using static Microsoft.Diagnostics.Tracing.Computers.AsyncProfilerRuntimeRuleProfile;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>How a stitched frame relates to the sampled physical stack and captured async call stacks.</summary>
    internal enum AsyncProfilerStitchedFrameOrigin : byte
    {
        /// <summary>A frame retained from the sampled physical stack without a matched logical async frame.</summary>
        Sync = 0,

        /// <summary>
        /// An async method body matched on the sampled physical stack.
        /// Retains the sampled code address alongside the captured async identity.
        /// </summary>
        AsyncCurrent,

        /// <summary>
        /// An async frame taken from a captured call stack rather than matched on the physical stack.
        /// In stitched output, it provides logical caller ancestry without claiming current execution.
        /// </summary>
        AsyncCaller,

        /// <summary>
        /// A logical frame from the child's historical parent call stack, linked through dispatcher creation.
        /// This relationship does not establish that the parent is executing at sample time.
        /// </summary>
        AsyncContextParent,

        /// <summary>
        /// An async resumption target identified from dispatcher or entry evidence.
        /// No separate method body was matched; the sample may still be in runtime dispatch code.
        /// </summary>
        AsyncDispatchTarget,

        /// <summary>
        /// A V1 method body still on the sampled physical stack after its async operation completed.
        /// The invocation is still finishing completion work.
        /// </summary>
        V1CompletedBody,

        /// <summary>
        /// A marker for missing physical callers at the root of a reconstructed async stack.
        /// Rendered as <c>BROKEN</c> without inventing a caller or execution host.
        /// </summary>
        IncompletePhysicalRoot,

        /// <summary>
        /// Verified return work represented by the completed V1 call stack's logical root.
        /// The root body need not be present on the sampled physical stack.
        /// </summary>
        V1CompletionReturn,

        /// <summary>
        /// The historical parent frame inferred as current when the child was created, not at sample time.
        /// Its position is inferred from completion records, not from sampled parent execution.
        /// </summary>
        AsyncContextParentCurrent,

        /// <summary>A display label without physical or captured async-method identity.</summary>
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
    /// Shared roles used by stitching and presentation. Runtime profiles recognize the implementing methods
    /// and keep any finer matching details separate. These roles are not dispatch boundaries.
    /// </summary>
    internal enum AsyncProfilerStitchSyncFrameKind : byte
    {
        /// <summary>No shared async role; a runtime profile may still recognize this frame.</summary>
        None = 0,

        /// <summary>The physical body of a compiler-generated V1 state machine.</summary>
        V1StateMachineBody,

        /// <summary>A generated stub preparing to enter a V2 async method.</summary>
        V2ResumeStub,

        /// <summary>A runtime helper managing a V1 method, without proving that method completed.</summary>
        V1MethodHelper,

        /// <summary>An awaiter helper, removable only within a separately verified runtime span.</summary>
        AwaiterHelper,

        /// <summary>A helper transferring execution between continuations or execution contexts.</summary>
        ContinuationBridge,

        /// <summary>A fault-recording helper, not evidence that the current async method completed.</summary>
        FaultHandling,

        /// <summary>A helper completing the current V1 method's own result, not a nested operation's result.</summary>
        V1MethodCompletion,

        /// <summary>Profiler capture, emission, or resumption work that can identify an entry candidate.</summary>
        ProfilerEntry,

        /// <summary>Other profiler, diagnostics, event-source, or box support work.</summary>
        ProfilerHelper,

        /// <summary>Setup for a V1 dispatcher before await registration.</summary>
        V1DispatchSetup,

        /// <summary>A V1 box callback carrying method identity, but not a resumption carrier.</summary>
        V1BoxCallback,
    }

    /// <summary>
    /// One frame of a native (CPU-sample) call stack, as consumed by <see cref="AsyncProfilerCallStackStitcher"/>.
    /// Carries the sampled code address, canonical method identity, and optional runtime classification.
    /// </summary>
    internal readonly struct AsyncProfilerStitchSyncFrame
    {
        public AsyncProfilerStitchSyncFrame(CodeAddressIndex codeAddress, MethodIndex method)
            : this(codeAddress, method, AsyncProfilerStitchSyncFrameKind.None)
        {
        }

        public AsyncProfilerStitchSyncFrame(CodeAddressIndex codeAddress, MethodIndex method,
            FrameClassification classification)
            : this(codeAddress, method, classification, MethodIndex.Invalid)
        {
        }

        /// <summary>Creates a native frame with an optional uniquely resolved boxed state-machine method.</summary>
        public AsyncProfilerStitchSyncFrame(CodeAddressIndex codeAddress, MethodIndex method,
            FrameClassification classification, MethodIndex boxedStateMachineMethod)
        {
            CodeAddress = codeAddress;
            Method = method;
            Classification = classification;
            BoxedStateMachineMethod = boxedStateMachineMethod;
        }

        /// <summary>The frame's code address (used to intern it and to classify it as an async boundary).</summary>
        public readonly CodeAddressIndex CodeAddress;

        /// <summary>
        /// The canonical method used for V1/V2 identity matching, or <see cref="MethodIndex.Invalid"/>.
        /// </summary>
        public readonly MethodIndex Method;

        /// <summary>The frame's shared async role.</summary>
        public AsyncProfilerStitchSyncFrameKind Kind => Classification.Kind;

        /// <summary>Cached recognition, including details interpreted only by the runtime profile.</summary>
        public readonly FrameClassification Classification;

        /// <summary>
        /// The uniquely resolved state-machine method carried by a CoreLib box execution frame, or
        /// <see cref="MethodIndex.Invalid"/>. Only the leafmost execution carrier preceding any physical
        /// async application body can use this identity for V1 alignment.
        /// </summary>
        public readonly MethodIndex BoxedStateMachineMethod;
    }

    /// <summary>
    /// A physical frame, logical async frame, display label, or missing-root marker in a stitched call stack.
    /// </summary>
    internal readonly struct AsyncProfilerStitchedFrame : IEquatable<AsyncProfilerStitchedFrame>
    {
        /// <summary>Preserves a physical frame and its runtime classification.</summary>
        public static AsyncProfilerStitchedFrame CreateSync(AsyncProfilerStitchSyncFrame frame,
            AsyncProfilerCallStack context) =>
            new AsyncProfilerStitchedFrame(AsyncProfilerStitchedFrameOrigin.Sync, frame.CodeAddress, frame.Method,
                null, -1, frame.Classification, AsyncProfilerStitchedFramePresentation.Native,
                context, frame.BoxedStateMachineMethod);

        /// <summary>
        /// Displays a physical runtime frame as <c>[Async dispatcher frames]</c>, retaining its identity.
        /// </summary>
        public static AsyncProfilerStitchedFrame CreateAsyncDispatcherFrames(AsyncProfilerStitchSyncFrame frame,
            AsyncProfilerCallStack context) =>
            CreateSync(frame, context).WithPresentation(AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames);

        /// <summary>
        /// Creates a physically matched current async frame. With a box identity, creates a resumption target instead.
        /// In that case, the supplied address and method identify the physical helper, not the application body.
        /// </summary>
        public static AsyncProfilerStitchedFrame CreateAsyncCurrent(CodeAddressIndex codeAddress, MethodIndex method,
            AsyncProfilerCallStack context, int segmentFrameIndex,
            MethodIndex boxedStateMachineMethod = MethodIndex.Invalid) =>
            new AsyncProfilerStitchedFrame(boxedStateMachineMethod == MethodIndex.Invalid
                ? AsyncProfilerStitchedFrameOrigin.AsyncCurrent
                : AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget,
                codeAddress, method, context.Frames, segmentFrameIndex, AsyncProfilerStitchSyncFrameKind.None,
                GetAsyncPresentation(context), context, boxedStateMachineMethod);

        /// <summary>Preserves a sampled V1 method body after its async operation has completed.</summary>
        public static AsyncProfilerStitchedFrame CreateV1CompletedBody(CodeAddressIndex codeAddress, MethodIndex method,
            AsyncProfilerCallStack context, int segmentFrameIndex) =>
            new AsyncProfilerStitchedFrame(AsyncProfilerStitchedFrameOrigin.V1CompletedBody, codeAddress, method,
                context.Frames, segmentFrameIndex, AsyncProfilerStitchSyncFrameKind.None,
                AsyncProfilerStitchedFramePresentation.LogicalStateMachineMethod, context);

        /// <summary>
        /// Attributes verified return work to the completed V1 root method.
        /// The method body need not be physically present.
        /// </summary>
        public static AsyncProfilerStitchedFrame CreateV1CompletionReturn(AsyncProfilerCallStack context,
            bool isDispatcherTailUnwind = false) =>
            new AsyncProfilerStitchedFrame(AsyncProfilerStitchedFrameOrigin.V1CompletionReturn,
                context.Frames.CodeAddressAt(context.Frames.FrameCount - 1),
                MethodIndex.Invalid, context.Frames, context.Frames.FrameCount - 1,
                AsyncProfilerStitchSyncFrameKind.None, AsyncProfilerStitchedFramePresentation.LogicalStateMachineMethod,
                context, isDispatcherTailUnwind: isDispatcherTailUnwind);

        /// <summary>Creates a logical frame from captured async data.</summary>
        public static AsyncProfilerStitchedFrame CreateAsync(AsyncProfilerCallStack context, int segmentFrameIndex) =>
            new AsyncProfilerStitchedFrame(AsyncProfilerStitchedFrameOrigin.AsyncCaller,
                context.Frames.CodeAddressAt(segmentFrameIndex), MethodIndex.Invalid,
                context.Frames, segmentFrameIndex, AsyncProfilerStitchSyncFrameKind.None,
                GetAsyncPresentation(context), context);

        /// <summary>
        /// Creates a historical parent frame linked through child creation.
        /// <paramref name="isCurrent"/> marks the parent frame inferred as current at child creation,
        /// not at sample time.
        /// </summary>
        public static AsyncProfilerStitchedFrame CreateAsyncContextParent(AsyncProfilerCallStack context,
            int segmentFrameIndex, bool isCurrent = false) =>
            new AsyncProfilerStitchedFrame(isCurrent
                ? AsyncProfilerStitchedFrameOrigin.AsyncContextParentCurrent
                : AsyncProfilerStitchedFrameOrigin.AsyncContextParent,
                context.Frames.CodeAddressAt(segmentFrameIndex), MethodIndex.Invalid,
                context.Frames, segmentFrameIndex, AsyncProfilerStitchSyncFrameKind.None,
                GetAsyncPresentation(context), context);

        /// <summary>Creates a logical resumption target without a physically matched method body.</summary>
        public static AsyncProfilerStitchedFrame CreateAsyncDispatchTarget(AsyncProfilerCallStack context,
            int segmentFrameIndex) =>
            new AsyncProfilerStitchedFrame(AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget,
                context.Frames.CodeAddressAt(segmentFrameIndex), MethodIndex.Invalid,
                context.Frames, segmentFrameIndex, AsyncProfilerStitchSyncFrameKind.None,
                GetAsyncPresentation(context), context);

        /// <summary>
        /// Identifies a V1 resumption target through a physical box helper, without claiming body execution.
        /// </summary>
        public static AsyncProfilerStitchedFrame CreateAsyncDispatchTarget(AsyncProfilerStitchSyncFrame carrier,
            AsyncProfilerCallStack context, int segmentFrameIndex) =>
            new AsyncProfilerStitchedFrame(AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget,
                carrier.CodeAddress, carrier.Method, context.Frames, segmentFrameIndex,
                AsyncProfilerStitchSyncFrameKind.None, AsyncProfilerStitchedFramePresentation.LogicalStateMachineMethod,
                context, carrier.BoxedStateMachineMethod);

        /// <summary>Marks missing physical callers without inventing a caller or execution host.</summary>
        public static AsyncProfilerStitchedFrame CreateIncompletePhysicalRoot() =>
            new AsyncProfilerStitchedFrame(AsyncProfilerStitchedFrameOrigin.IncompletePhysicalRoot,
                CodeAddressIndex.Invalid, MethodIndex.Invalid, null, -1,
                AsyncProfilerStitchSyncFrameKind.None, AsyncProfilerStitchedFramePresentation.Native, null);

        /// <summary>Creates a display label, optionally associated with an async call stack.</summary>
        internal static AsyncProfilerStitchedFrame CreateSynthetic(string displayName,
            AsyncProfilerCallStack context = null) =>
            new AsyncProfilerStitchedFrame(displayName, context);

        /// <summary>How this frame relates to physical and captured async frames.</summary>
        public readonly AsyncProfilerStitchedFrameOrigin Origin;

        /// <summary>
        /// The code address, or <see cref="CodeAddressIndex.Invalid"/> when unavailable.
        /// A valid address may still have unresolved symbols.
        /// </summary>
        public readonly CodeAddressIndex CodeAddress;

        /// <summary>
        /// The canonical physical method identity, or <see cref="MethodIndex.Invalid"/> when unavailable.
        /// </summary>
        public readonly MethodIndex Method;

        /// <summary>
        /// The canonical application method identified by a V1 box helper, or <see cref="MethodIndex.Invalid"/>.
        /// <see cref="Method"/> and <see cref="CodeAddress"/> identify the physical helper, not the application method.
        /// </summary>
        public readonly MethodIndex BoxedStateMachineMethod;

        /// <summary>
        /// True when a V1 resumption target was identified through a physical box helper.
        /// This does not mean the application body was sampled.
        /// </summary>
        public bool IsStateMachineBoxCarrier =>
            (Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent ||
             Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget) &&
            BoxedStateMachineMethod != MethodIndex.Invalid;

        /// <summary>
        /// Captured async frames, or null when this frame has no captured async identity.
        /// Captured identity can be available even when <see cref="CodeAddress"/> is invalid.
        /// </summary>
        public AsyncProfilerCallStackFrames Segment => m_segmentOrDisplayName as AsyncProfilerCallStackFrames;

        /// <summary>
        /// The captured frame's index within <see cref="Segment"/>, or -1 when there is no captured frame.
        /// </summary>
        public readonly int SegmentFrameIndex;

        /// <summary>For a sync-origin frame, its shared async role.</summary>
        public readonly AsyncProfilerStitchSyncFrameKind SyncFrameKind;

        /// <summary>The shared role and profile-only matching details for a synchronous frame.</summary>
        public FrameClassification SyncClassification => new FrameClassification(SyncFrameKind, m_syncFrameHelper);

        /// <summary>How this frame should be displayed.</summary>
        public readonly AsyncProfilerStitchedFramePresentation Presentation;

        /// <summary>The associated async call stack, or null.</summary>
        public readonly AsyncProfilerCallStack Context;

        /// <summary>The synthetic display label, or null for other frames.</summary>
        internal string DisplayName => m_segmentOrDisplayName as string;

        /// <summary>Whether this frame belongs to an active context, a historical parent context, or neither.</summary>
        public AsyncProfilerStitchedFrameContextKind ContextKind
        {
            get
            {
                if (Context == null)
                {
                    return AsyncProfilerStitchedFrameContextKind.None;
                }

                if (Origin == AsyncProfilerStitchedFrameOrigin.AsyncContextParent ||
                    Origin == AsyncProfilerStitchedFrameOrigin.AsyncContextParentCurrent)
                {
                    return AsyncProfilerStitchedFrameContextKind.ParentContext;
                }

                return AsyncProfilerStitchedFrameContextKind.Active;
            }
        }

        /// <summary>
        /// True when V1 return work was verified from a remaining dispatcher box, without a matched method body.
        /// </summary>
        internal bool IsDispatcherTailUnwind => m_isDispatcherTailUnwind;

        /// <summary>Returns a copy with a different presentation, keeping all other metadata.</summary>
        public AsyncProfilerStitchedFrame WithPresentation(AsyncProfilerStitchedFramePresentation presentation) =>
            new AsyncProfilerStitchedFrame(this, presentation);

        public bool Equals(AsyncProfilerStitchedFrame other) =>
            Origin == other.Origin &&
            CodeAddress == other.CodeAddress &&
            Method == other.Method &&
            BoxedStateMachineMethod == other.BoxedStateMachineMethod &&
            SegmentFrameIndex == other.SegmentFrameIndex &&
            SyncClassification.Equals(other.SyncClassification) &&
            Presentation == other.Presentation &&
            m_isDispatcherTailUnwind == other.m_isDispatcherTailUnwind &&
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
                hash = (hash * 397) ^ SyncClassification.GetHashCode();
                hash = (hash * 397) ^ (int)Presentation;
                hash = (hash * 397) ^ m_isDispatcherTailUnwind.GetHashCode();
                hash = (hash * 397) ^ (Context?.GetHashCode() ?? 0);
                return (hash * 397) ^ (m_segmentOrDisplayName?.GetHashCode() ?? 0);
            }
        }

        #region private

        private AsyncProfilerStitchedFrame(AsyncProfilerStitchedFrameOrigin origin,
            CodeAddressIndex codeAddress, MethodIndex method,
            AsyncProfilerCallStackFrames segment, int segmentFrameIndex, FrameClassification syncClassification,
            AsyncProfilerStitchedFramePresentation presentation, AsyncProfilerCallStack context,
            MethodIndex boxedStateMachineMethod = MethodIndex.Invalid, bool isDispatcherTailUnwind = false)
        {
            Origin = origin;
            CodeAddress = codeAddress;
            Method = method;
            BoxedStateMachineMethod = boxedStateMachineMethod;
            m_segmentOrDisplayName = segment;
            SegmentFrameIndex = segmentFrameIndex;
            SyncFrameKind = syncClassification.Kind;
            m_syncFrameHelper = syncClassification.Helper;
            Presentation = presentation;
            Context = context;
            m_isDispatcherTailUnwind = isDispatcherTailUnwind;
        }

        private AsyncProfilerStitchedFrame(string displayName, AsyncProfilerCallStack context)
            : this(AsyncProfilerStitchedFrameOrigin.Synthetic, CodeAddressIndex.Invalid, MethodIndex.Invalid, null, -1,
                AsyncProfilerStitchSyncFrameKind.None, AsyncProfilerStitchedFramePresentation.Native, context)
        {
            m_segmentOrDisplayName = displayName;
        }

        private AsyncProfilerStitchedFrame(AsyncProfilerStitchedFrame frame,
            AsyncProfilerStitchedFramePresentation presentation)
        {
            this = frame;
            Presentation = presentation;
        }

        private static AsyncProfilerStitchedFramePresentation GetAsyncPresentation(AsyncProfilerCallStack context) =>
            context.Frames.Kind == AsyncCallstackKind.StateMachineAsync
                ? AsyncProfilerStitchedFramePresentation.LogicalStateMachineMethod
                : AsyncProfilerStitchedFramePresentation.Native;

        // Synthetic labels reuse the segment reference slot to keep ordinary stitched frames compact.
        private readonly object m_segmentOrDisplayName;
        private readonly bool m_isDispatcherTailUnwind;

        // Separate byte fields avoid .NET Framework's padding for an embedded classification struct.
        private readonly HelperKind m_syncFrameHelper;

        #endregion
    }

    /// <summary>
    /// The output range and dispatch-boundary metadata for one active async call stack.
    /// </summary>
    internal readonly struct AsyncProfilerCallStackPlacement
    {
        internal AsyncProfilerCallStackPlacement(AsyncProfilerCallStack callStack,
            int leafIndex, int rootIndexExclusive, CodeAddressIndex instrumentedDispatch = CodeAddressIndex.Invalid,
            CodeAddressIndex dispatch = CodeAddressIndex.Invalid)
        {
            CallStack = callStack;
            LeafIndex = leafIndex;
            RootIndexExclusive = rootIndexExclusive;
            InstrumentedDispatch = instrumentedDispatch;
            Dispatch = dispatch;
        }

        internal AsyncProfilerCallStack CallStack { get; }

        internal int LeafIndex { get; }

        internal int RootIndexExclusive { get; }

        // Retain the consumed V1 dispatcher or V2 pair for lazy verification, without enlarging every physical frame.
        internal CodeAddressIndex InstrumentedDispatch { get; }

        internal CodeAddressIndex Dispatch { get; }
    }

    /// <summary>The result of a stitch: the merged frames (leaf-to-root) and the diagnostics.</summary>
    internal sealed class AsyncProfilerStitchResult
    {
        /// <summary>Creates a result retaining the supplied frame list and diagnostics without copying them.</summary>
        public AsyncProfilerStitchResult(IReadOnlyList<AsyncProfilerStitchedFrame> frames,
            AsyncProfilerCallStackDiagnostics diagnostics)
            : this(frames, diagnostics, Array.Empty<AsyncProfilerCallStackPlacement>())
        {
        }

        internal AsyncProfilerStitchResult(IReadOnlyList<AsyncProfilerStitchedFrame> frames,
            AsyncProfilerCallStackDiagnostics diagnostics,
            IReadOnlyList<AsyncProfilerCallStackPlacement> segmentPlacements)
        {
            Frames = frames ?? throw new ArgumentNullException(nameof(frames));
            Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            SegmentPlacements = segmentPlacements ?? throw new ArgumentNullException(nameof(segmentPlacements));
        }

        /// <summary>The stitched frames, leaf-to-root (from the native leaf to the thread/process root).</summary>
        public IReadOnlyList<AsyncProfilerStitchedFrame> Frames { get; }

        /// <summary>
        /// Counters and bounded messages for stitching, fallbacks, and any subsequent presentation transforms.
        /// </summary>
        public AsyncProfilerCallStackDiagnostics Diagnostics { get; }

        internal IReadOnlyList<AsyncProfilerCallStackPlacement> SegmentPlacements { get; }
    }

    /// <summary>
    /// Combines a sampled physical call stack with the async call stacks active at that time.
    /// Restores async callers that are missing from the sampled stack.
    /// </summary>
    /// <remarks>
    /// Inputs and output are ordered leaf-to-root.
    /// Without usable async frames, the physical stack is kept unchanged.
    /// Presentation cleanup is handled separately by
    /// <see cref="AsyncProfilerCallStackTransformPipeline"/>.
    /// The <c>Stitch</c> overloads create new output lists and do not modify their inputs.
    /// </remarks>
    internal static class AsyncProfilerCallStackStitcher
    {
        /// <summary>
        /// Merges the physical stack with the async call stacks active at the sample QPC. Both inputs are
        /// leaf-to-root. Uses the supplied cache for boundaries and the index for completion observations and history.
        /// </summary>
        /// <param name="syncLeafToRoot">The native sync stack, leaf-to-root.</param>
        /// <param name="segmentsLeafToRoot">
        /// The async segments active at <paramref name="timestampQpc"/>, leaf-to-root.
        /// </param>
        /// <param name="timestampQpc">The sample time, in the trace's QPC domain.</param>
        /// <param name="boundaries">The per-trace boundary-method cache.</param>
        /// <param name="index">
        /// Provides per-call-stack completion observations and history for wrapper-free V2 targets.
        /// </param>
        /// <param name="processIndex">The process instance that emitted the sampled stack.</param>
        /// <param name="methodOf">
        /// Resolves code addresses to the same canonical method identities used by physical frames.
        /// </param>
        /// <param name="trace">Adds per-segment stitching details to the diagnostics when true.</param>
        public static AsyncProfilerStitchResult Stitch(IReadOnlyList<AsyncProfilerStitchSyncFrame> syncLeafToRoot,
            IReadOnlyList<AsyncProfilerCallStack> segmentsLeafToRoot, long timestampQpc,
            AsyncProfilerCallStackStitchBoundaryCache boundaries, AsyncProfilerCallStacksIndex index,
            ProcessIndex processIndex, Func<CodeAddressIndex, MethodIndex> methodOf, bool trace = false)
        {
            if (boundaries is null)
            {
                throw new ArgumentNullException(nameof(boundaries));
            }

            if (index is null)
            {
                throw new ArgumentNullException(nameof(index));
            }

            if (syncLeafToRoot is null)
            {
                throw new ArgumentNullException(nameof(syncLeafToRoot));
            }

            if (methodOf is null)
            {
                throw new ArgumentNullException(nameof(methodOf));
            }

            var diagnostics = new AsyncProfilerCallStackDiagnostics();
            var output = new List<AsyncProfilerStitchedFrame>(syncLeafToRoot.Count + 8);
            var placements = new List<AsyncProfilerCallStackPlacement>(segmentsLeafToRoot?.Count ?? 0);

            StitchInto(syncLeafToRoot, segmentsLeafToRoot, timestampQpc, boundaries.Classify, null,
                index.MethodCompletionObserved, processIndex, methodOf, trace, output, diagnostics, placements,
                boundaries.GetRuntimeProfile, boundaries.RuntimeProfiles.GetProcessProfile(processIndex),
                boundaries.TraceLog, index, boundaries.RuntimeProfiles);

            return new AsyncProfilerStitchResult(output, diagnostics, placements);
        }

        /// <summary>
        /// Merges physical and active async call stacks, both leaf-to-root, using supplied boundary and
        /// completion-observation delegates. Also supports synthetic code addresses in tests.
        /// </summary>
        /// <param name="syncLeafToRoot">The native sync stack, leaf-to-root.</param>
        /// <param name="segmentsLeafToRoot">
        /// The async segments active at <paramref name="timestampQpc"/>, leaf-to-root.
        /// </param>
        /// <param name="timestampQpc">The sample time, in the trace's QPC domain.</param>
        /// <param name="classify">
        /// Classifies a sync frame's <see cref="CodeAddressIndex"/> as an async dispatch boundary.
        /// </param>
        /// <param name="methodCompletionObserved">
        /// Reports whether normal-completion events were observed for each kind.
        /// The caller must select observations valid for the supplied call stacks' configuration epochs.
        /// V1 also uses recorded exception counts; V2 wrapper progress already includes exceptions.
        /// </param>
        /// <param name="methodOf">
        /// Resolves code addresses to the same canonical method identities used by physical frames.
        /// </param>
        /// <param name="trace">Adds per-segment stitching details to the diagnostics when true.</param>
        public static AsyncProfilerStitchResult Stitch(IReadOnlyList<AsyncProfilerStitchSyncFrame> syncLeafToRoot,
            IReadOnlyList<AsyncProfilerCallStack> segmentsLeafToRoot, long timestampQpc,
            Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classify,
            Func<AsyncCallstackKind, bool> methodCompletionObserved,
            Func<CodeAddressIndex, MethodIndex> methodOf, bool trace = false)
        {
            if (syncLeafToRoot is null)
            {
                throw new ArgumentNullException(nameof(syncLeafToRoot));
            }

            if (classify is null)
            {
                throw new ArgumentNullException(nameof(classify));
            }

            if (methodCompletionObserved is null)
            {
                throw new ArgumentNullException(nameof(methodCompletionObserved));
            }

            if (methodOf is null)
            {
                throw new ArgumentNullException(nameof(methodOf));
            }

            var diagnostics = new AsyncProfilerCallStackDiagnostics();
            var output = new List<AsyncProfilerStitchedFrame>(syncLeafToRoot.Count + 8);
            var placements = new List<AsyncProfilerCallStackPlacement>(segmentsLeafToRoot?.Count ?? 0);

            StitchInto(syncLeafToRoot, segmentsLeafToRoot, timestampQpc, classify, methodCompletionObserved, null,
                ProcessIndex.Invalid, methodOf, trace, output, diagnostics, placements, null, null, null);

            return new AsyncProfilerStitchResult(output, diagnostics, placements);
        }

        internal static void StitchInto(IReadOnlyList<AsyncProfilerStitchSyncFrame> syncLeafToRoot,
            IReadOnlyList<AsyncProfilerCallStack> segmentsLeafToRoot, long timestampQpc,
            Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classify,
            Func<ProcessIndex, AsyncCallstackKind, long, bool> methodCompletionObserved,
            ProcessIndex processIndex, Func<CodeAddressIndex, MethodIndex> methodOf, bool trace,
            List<AsyncProfilerStitchedFrame> output, AsyncProfilerCallStackDiagnostics diagnostics)
        {
            StitchInto(syncLeafToRoot, segmentsLeafToRoot, timestampQpc, classify, methodCompletionObserved,
                processIndex, methodOf, trace, output, diagnostics, null);
        }

        internal static void StitchInto(IReadOnlyList<AsyncProfilerStitchSyncFrame> syncLeafToRoot,
            IReadOnlyList<AsyncProfilerCallStack> segmentsLeafToRoot, long timestampQpc,
            Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classify,
            Func<ProcessIndex, AsyncCallstackKind, long, bool> methodCompletionObserved,
            ProcessIndex processIndex, Func<CodeAddressIndex, MethodIndex> methodOf, bool trace,
            List<AsyncProfilerStitchedFrame> output, AsyncProfilerCallStackDiagnostics diagnostics,
            List<AsyncProfilerCallStackPlacement> placements,
            Func<CodeAddressIndex, AsyncProfilerRuntimeRuleProfile> runtimeProfileResolver = null,
            AsyncProfilerRuntimeRuleProfile runtimeProfile = null, TraceLog runtimeTraceLog = null,
            AsyncProfilerCallStacksIndex index = null, AsyncProfilerRuntimeRuleProfileCache runtimeProfiles = null)
        {
            if (syncLeafToRoot is null)
            {
                throw new ArgumentNullException(nameof(syncLeafToRoot));
            }

            if (classify is null)
            {
                throw new ArgumentNullException(nameof(classify));
            }

            if (methodCompletionObserved is null)
            {
                throw new ArgumentNullException(nameof(methodCompletionObserved));
            }

            if (methodOf is null)
            {
                throw new ArgumentNullException(nameof(methodOf));
            }

            if (output is null)
            {
                throw new ArgumentNullException(nameof(output));
            }

            if (diagnostics is null)
            {
                throw new ArgumentNullException(nameof(diagnostics));
            }

            StitchInto(syncLeafToRoot, segmentsLeafToRoot, timestampQpc, classify, null, methodCompletionObserved,
                processIndex, methodOf, trace, output, diagnostics, placements,
                runtimeProfileResolver, runtimeProfile, runtimeTraceLog, index, runtimeProfiles);
        }

        /// <summary>Checks that the frames are the expected V2 dispatch methods from the same runtime.</summary>
        internal static bool IsV2DispatchPair(TraceLog traceLog, AsyncProfilerRuntimeRuleProfileCache runtimeProfiles,
            CodeAddressIndex instrumented, CodeAddressIndex dispatch)
        {
            if (instrumented == CodeAddressIndex.Invalid || dispatch == CodeAddressIndex.Invalid)
            {
                return false;
            }

            TraceCodeAddresses addresses = traceLog.CodeAddresses;
            MethodIndex instrumentedMethod = addresses.MethodIndex(instrumented);
            MethodIndex dispatchMethod = addresses.MethodIndex(dispatch);
            if (instrumentedMethod == MethodIndex.Invalid || dispatchMethod == MethodIndex.Invalid ||
                instrumentedMethod == dispatchMethod)
            {
                return false;
            }

            ModuleFileIndex instrumentedModule = addresses.ModuleFileIndex(instrumented);
            ModuleFileIndex dispatchModule = addresses.ModuleFileIndex(dispatch);
            ModuleFileIndex owner = runtimeProfiles.GetHostModuleOwner(instrumentedModule);
            if (owner == ModuleFileIndex.Invalid || owner != runtimeProfiles.GetHostModuleOwner(dispatchModule))
            {
                return false;
            }

            AsyncProfilerRuntimeRuleProfile instrumentedProfile = runtimeProfiles.GetProfile(instrumentedModule);
            AsyncProfilerRuntimeRuleProfile dispatchProfile = runtimeProfiles.GetProfile(dispatchModule);
            if (!ReferenceEquals(instrumentedProfile, dispatchProfile))
            {
                return false;
            }

            string instrumentedName =
                AsyncProfilerMethodNames.NormalizeMethodName(addresses[instrumented].FullMethodName);
            string dispatchName =
                AsyncProfilerMethodNames.NormalizeMethodName(addresses[dispatch].FullMethodName);

            string instrumentedMethodName = AsyncProfilerMethodNames.GetBareMethodName(instrumentedName);
            string dispatchMethodName = AsyncProfilerMethodNames.GetBareMethodName(dispatchName);
            if (instrumentedMethodName != AsyncProfilerCallStackStitchBoundary.InstrumentedDispatchContinuationsName ||
                dispatchMethodName != AsyncProfilerCallStackStitchBoundary.DispatchContinuationsName)
            {
                return false;
            }

            return AsyncProfilerMethodNames.GetDeclaringTypeName(instrumentedName) ==
                AsyncProfilerMethodNames.GetDeclaringTypeName(dispatchName);
        }

        /// <summary>True when the list is nonempty and every call stack has captured frames.</summary>
        internal static bool HasUsableSegments(IReadOnlyList<AsyncProfilerCallStack> segments)
        {
            if (segments == null || segments.Count == 0)
            {
                return false;
            }

            for (int i = 0; i < segments.Count; i++)
            {
                if (segments[i].Frames.FrameCount == 0)
                {
                    return false;
                }
            }

            return true;
        }

        #region private

        /// <summary>
        /// Builds the stitched call stack for one sample, reusing the supplied output buffers.
        /// Matches each active async call stack to its physical frames, then adds its missing async callers.
        /// </summary>
        private static void StitchInto(IReadOnlyList<AsyncProfilerStitchSyncFrame> syncLeafToRoot,
            IReadOnlyList<AsyncProfilerCallStack> segmentsLeafToRoot, long timestampQpc,
            Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classify,
            Func<AsyncCallstackKind, bool> methodCompletionObserved,
            Func<ProcessIndex, AsyncCallstackKind, long, bool> methodCompletionObservedByProcess,
            ProcessIndex processIndex, Func<CodeAddressIndex, MethodIndex> methodOf, bool trace,
            List<AsyncProfilerStitchedFrame> output, AsyncProfilerCallStackDiagnostics diagnostics,
            List<AsyncProfilerCallStackPlacement> placements,
            Func<CodeAddressIndex, AsyncProfilerRuntimeRuleProfile> runtimeProfileResolver,
            AsyncProfilerRuntimeRuleProfile runtimeProfile, TraceLog runtimeTraceLog,
            AsyncProfilerCallStacksIndex index = null, AsyncProfilerRuntimeRuleProfileCache runtimeProfiles = null)
        {
            output.Clear();
            diagnostics.Clear();
            placements?.Clear();

            runtimeProfile = runtimeProfile ?? AsyncProfilerRuntimeRuleProfile.Common;
            runtimeProfiles = runtimeProfiles ??
                (runtimeTraceLog == null
                    ? null
                    : new AsyncProfilerRuntimeRuleProfileCache(runtimeTraceLog.ModuleFiles));

            // Without usable logical frames, retain every physical frame so coverage gaps remain visible.
            if (!HasUsableSegments(segmentsLeafToRoot))
            {
                for (int i = 0; i < syncLeafToRoot.Count; i++)
                {
                    output.Add(AsyncProfilerStitchedFrame.CreateSync(syncLeafToRoot[i], null));
                }

                return;
            }

            // Both inputs run leaf-to-root. syncPosition is the first physical frame not yet handled.
            int syncPosition = 0;
            int firstSegmentIndex = 0;

            // First handle samples taken inside V2 dispatch code rather than an application method.
            // A dispatch entry may identify the next async method without showing that its body ran.
            if (segmentsLeafToRoot[0].Frames.Kind == AsyncCallstackKind.RuntimeAsync)
            {
                int wrapperPosition = FindFirstV2Wrapper(syncLeafToRoot, classify, out bool hasDispatchContinuation);
                bool wrapperFound = wrapperPosition < syncLeafToRoot.Count;

                if (wrapperFound && IsSampledV2WrapperEntry(syncLeafToRoot, wrapperPosition) &&
                    !HasV2DispatchTarget(segmentsLeafToRoot[firstSegmentIndex], timestampQpc,
                        classify(syncLeafToRoot[wrapperPosition].CodeAddress).WrapperIndex,
                        methodCompletionObserved, methodCompletionObservedByProcess, processIndex))
                {
                    // This entry has no known async target. Keep its physical frames, and continue only if
                    // the next enclosing call stack has a verified physical match.
                    int physicalEnd = syncLeafToRoot.Count;
                    if (segmentsLeafToRoot.Count > 1)
                    {
                        AsyncProfilerRuntimeRuleProfile entryProfile = runtimeProfileResolver == null
                            ? runtimeProfile
                            : runtimeProfileResolver(syncLeafToRoot[wrapperPosition].CodeAddress);
                        int entryEnd = wrapperPosition + 1 + MatchDispatchInfrastructure(entryProfile,
                            AsyncCallstackKind.RuntimeAsync, syncLeafToRoot, wrapperPosition, wrapperPosition + 1,
                            classify, runtimeProfileResolver, runtimeTraceLog, runtimeProfiles);

                        AsyncProfilerCallStack outer = segmentsLeafToRoot[firstSegmentIndex + 1];
                        bool outerVerified;
                        if (outer.Frames.Kind == AsyncCallstackKind.RuntimeAsync)
                        {
                            outerVerified = FindEnclosingV2CurrentSyncFrame(outer, syncLeafToRoot,
                                entryEnd, timestampQpc, classify,
                                methodCompletionObserved, methodCompletionObservedByProcess,
                                processIndex, methodOf) >= 0;
                        }
                        else
                        {
                            outerVerified = FindBoundary(syncLeafToRoot, entryEnd, outer.Frames.Kind, classify, out _) <
                                syncLeafToRoot.Count;
                        }

                        if (outerVerified)
                        {
                            physicalEnd = entryEnd;
                        }
                    }

                    diagnostics.V2SyncLayoutUsed++;
                    for (int i = 0; i < physicalEnd; i++)
                    {
                        output.Add(AsyncProfilerStitchedFrame.CreateSync(syncLeafToRoot[i], null));
                    }

                    if (trace)
                    {
                        diagnostics.Note(physicalEnd == syncLeafToRoot.Count
                            ? "V2 not stitched: sampled dispatch entry has no identifiable target; used the sync layout."
                            : "Inner V2 not stitched: sampled dispatch entry has no identifiable target; " +
                                "retained its entry slice and continued with the enclosing call stack.");
                    }

                    if (physicalEnd == syncLeafToRoot.Count)
                    {
                        return;
                    }

                    // The entry work has no async target, so do not record a placement for it.
                    diagnostics.SegmentsProcessed++;
                    syncPosition = physicalEnd;
                    firstSegmentIndex++;
                }

                if (!wrapperFound && hasDispatchContinuation)
                {
                    // Without a wrapper, only a verified dispatch pair and recorded progress can identify a target.
                    AsyncProfilerCallStack segment = segmentsLeafToRoot[0];
                    if (segmentsLeafToRoot.Count == 1 &&
                        TryFindV2DispatchTargetWithoutWrapper(segment, syncLeafToRoot, timestampQpc,
                            classify, methodCompletionObserved,
                            methodCompletionObservedByProcess, processIndex, methodOf,
                            runtimeTraceLog, runtimeProfiles, index,
                            out int dispatchStart, out int dispatchEnd, out int completedFrameCount))
                    {
                        // Keep sampled callees, represent the dispatch pair, and add the target's async callers.
                        for (int i = 0; i < dispatchStart; i++)
                        {
                            output.Add(AsyncProfilerStitchedFrame.CreateSync(syncLeafToRoot[i], segment));
                        }

                        output.Add(AsyncProfilerStitchedFrame.CreateAsyncDispatcherFrames(syncLeafToRoot[dispatchStart],
                            segment));
                        output.Add(AsyncProfilerStitchedFrame.CreateAsyncDispatchTarget(segment, completedFrameCount));

                        for (int k = completedFrameCount + 1; k < segment.Frames.FrameCount; k++)
                        {
                            output.Add(AsyncProfilerStitchedFrame.CreateAsync(segment, k));
                        }

                        placements?.Add(new AsyncProfilerCallStackPlacement(segment, 0, output.Count,
                            syncLeafToRoot[dispatchStart].CodeAddress, syncLeafToRoot[dispatchStart + 1].CodeAddress));

                        for (int i = dispatchEnd; i < syncLeafToRoot.Count; i++)
                        {
                            output.Add(AsyncProfilerStitchedFrame.CreateSync(syncLeafToRoot[i], null));
                        }

                        diagnostics.SegmentsProcessed++;
                        diagnostics.V2InfrastructureFramesCollapsed += dispatchEnd - dispatchStart;
                        if (trace)
                        {
                            diagnostics.Note(
                                $"V2 dispatch target without wrapper: completed={completedFrameCount}; " +
                                "represented the verified dispatch pair with an async dispatcher marker.");
                        }

                        return;
                    }

                    diagnostics.V2SyncLayoutUsed++;
                    for (int i = 0; i < syncLeafToRoot.Count; i++)
                    {
                        output.Add(AsyncProfilerStitchedFrame.CreateSync(syncLeafToRoot[i], null));
                    }

                    if (trace)
                    {
                        diagnostics.Note(
                            "V2 not stitched: no continuation-wrapper root-ward of the leaf; used the sync layout.");
                    }

                    return;
                }
            }

            // Process each active call stack against the unhandled physical frames at syncPosition.
            for (int segmentIndex = firstSegmentIndex; segmentIndex < segmentsLeafToRoot.Count; segmentIndex++)
            {
                AsyncProfilerCallStack segment = segmentsLeafToRoot[segmentIndex];
                int segmentLeafIndex = output.Count;
                diagnostics.SegmentsProcessed++;
                AsyncProfilerCallStackFrames frames = segment.Frames;
                AsyncCallstackKind kind = frames.Kind;
                int boundaryPosition = FindBoundary(syncLeafToRoot, syncPosition, kind, classify,
                    out AsyncProfilerCallStackStitchBoundaryInfo boundaryInfo);
                bool boundaryFound = boundaryPosition < syncLeafToRoot.Count;
                AsyncProfilerRuntimeRuleProfile segmentProfile = boundaryFound && runtimeProfileResolver != null
                    ? runtimeProfileResolver(syncLeafToRoot[boundaryPosition].CodeAddress)
                    : runtimeProfile;

                if (!boundaryFound)
                {
                    diagnostics.BoundariesNotFound++;
                    if (diagnostics.CanRetainMessage)
                    {
                        diagnostics.Note(
                            $"No {kind} dispatch boundary found at or root-ward of sync frame {syncPosition}; " +
                            "recovering a unique physical alignment when available, " +
                            "otherwise appending logical async frames.");
                    }
                    else
                    {
                        diagnostics.MessagesDropped++;
                    }
                }

                // Stop the physical slice just before its dispatcher. If that boundary is missing,
                // do not take frames belonging to a verified enclosing V2 call stack.
                int syncEnd = boundaryPosition;
                if (!boundaryFound &&
                    kind == AsyncCallstackKind.StateMachineAsync &&
                    segmentIndex + 1 < segmentsLeafToRoot.Count)
                {
                    for (int outerIndex = segmentIndex + 1; outerIndex < segmentsLeafToRoot.Count; outerIndex++)
                    {
                        AsyncProfilerCallStack outer = segmentsLeafToRoot[outerIndex];
                        if (outer.Frames.Kind != AsyncCallstackKind.RuntimeAsync)
                        {
                            continue;
                        }

                        int outerCurrentSyncPosition = FindEnclosingV2CurrentSyncFrame(outer,
                            syncLeafToRoot, syncPosition, timestampQpc, classify,
                            methodCompletionObserved, methodCompletionObservedByProcess, processIndex, methodOf);
                        if (outerCurrentSyncPosition >= 0)
                        {
                            syncEnd = outerCurrentSyncPosition;
                        }

                        break;
                    }
                }

                // Count finished async frames. This is also the current captured-frame index, if one remains.
                // An enclosing call stack may be fully complete while its dispatcher is still on the stack.
                // The leaf call stack can instead retain its final physical method while it returns.
                bool allowFullyCompleted = boundaryFound && segmentIndex > 0;
                int completedFrameCount = ComputeCompletedFrameCount(segment, frames, kind, boundaryInfo,
                    boundaryFound, allowFullyCompleted, timestampQpc, syncLeafToRoot, syncPosition, syncEnd,
                    methodCompletionObserved, methodCompletionObservedByProcess, processIndex, methodOf,
                    diagnostics, segmentProfile,
                    out bool completionObserved, out int recoveredSyncEnd,
                    out bool v1CompletionAmbiguous, out bool v1CompletionTailVerified);

                bool fullyCompleted = allowFullyCompleted && completedFrameCount == frames.FrameCount;

                // A zero-IP first continuation still occupies a slot; do not treat its successor as current.
                bool initialV2Dispatch = kind == AsyncCallstackKind.RuntimeAsync &&
                    boundaryFound && boundaryPosition > syncPosition && completedFrameCount == 0 &&
                    frames.InstructionPointerAt(0) == 0 &&
                    frames.CodeAddressAt(0) == CodeAddressIndex.Invalid &&
                    GetV2DispatchTargetFrameIndex(segment, timestampQpc, boundaryInfo.WrapperIndex,
                        methodCompletionObserved, methodCompletionObservedByProcess, processIndex) == 0;

                // A resolved native entry can identify a target without being a sampled application body.
                bool nativeV2DispatchTarget = kind == AsyncCallstackKind.RuntimeAsync &&
                    boundaryFound && !fullyCompleted &&
                    HasV2NativeDispatchEntry(frames, completedFrameCount, syncLeafToRoot,
                        syncPosition, boundaryPosition, classify, methodOf, segmentProfile,
                        runtimeTraceLog, runtimeProfiles) &&
                    HasV2DispatchTarget(segment, timestampQpc, boundaryInfo.WrapperIndex,
                        methodCompletionObserved, methodCompletionObservedByProcess, processIndex);

                // Find the sampled current method, or a V1 box that identifies its pending target.
                // V2 dispatch-entry evidence alone must not be treated as a sampled application body.
                int currentSyncPosition = -1;
                if (!fullyCompleted && !v1CompletionAmbiguous && !nativeV2DispatchTarget && !initialV2Dispatch)
                {
                    if (kind == AsyncCallstackKind.StateMachineAsync)
                    {
                        currentSyncPosition = FindCurrentSyncFrame(frames, completedFrameCount,
                            syncLeafToRoot, syncPosition, syncEnd, methodOf,
                            boundaryFound || recoveredSyncEnd >= 0, segmentProfile);
                    }
                    else
                    {
                        currentSyncPosition = FindCurrentV2SyncFrame(frames, completedFrameCount,
                            syncLeafToRoot, syncPosition, boundaryPosition,
                            methodOf, boundaryFound, diagnostics);
                    }
                }

                // Completion counts cannot identify the current occurrence of a repeated V1 method.
                if (kind == AsyncCallstackKind.StateMachineAsync &&
                    v1CompletionTailVerified && currentSyncPosition >= 0)
                {
                    MethodIndex currentMethod = methodOf(frames.CodeAddressAt(completedFrameCount));
                    for (int k = 0; k < completedFrameCount; k++)
                    {
                        if (methodOf(frames.CodeAddressAt(k)) == currentMethod)
                        {
                            currentSyncPosition = -1;
                            v1CompletionAmbiguous = true;
                            diagnostics.V1CompletionAlignmentAmbiguous++;
                            diagnostics.Note(
                                "V1 completion counts cannot distinguish repeated current/completed occurrences; " +
                                "retained the physical slice.");
                            break;
                        }
                    }
                }

                // A completed V1 method can still be physically present while returning into its caller.
                int completingSyncPosition = -1;
                if (kind == AsyncCallstackKind.StateMachineAsync && boundaryFound &&
                    !fullyCompleted && !v1CompletionAmbiguous && v1CompletionTailVerified && currentSyncPosition < 0 &&
                    TryFindV1CompletingFrame(frames, completedFrameCount, syncLeafToRoot,
                        syncPosition, boundaryPosition, methodOf, out completingSyncPosition))
                {
                    diagnostics.V1CompletionTailUsed++;
                }

                // A unique physical match can recover the root call stack when its dispatcher was truncated.
                bool incompleteRoot = segmentIndex == segmentsLeafToRoot.Count - 1 &&
                    !boundaryFound && !v1CompletionAmbiguous &&
                    TryMatchTruncatedRoot(segment, syncLeafToRoot, syncPosition,
                        timestampQpc, completionObserved, completedFrameCount, currentSyncPosition,
                        recoveredSyncEnd, classify, methodOf, segmentProfile,
                        out completedFrameCount, out currentSyncPosition);

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

                int anchorSyncPosition = currentSyncPosition >= 0 ? currentSyncPosition : completingSyncPosition;
                bool recoveredInner = !boundaryFound && syncEnd < boundaryPosition &&
                    recoveredSyncEnd >= 0 && recoveredSyncEnd <= syncEnd &&
                    anchorSyncPosition >= 0 && !v1CompletionAmbiguous;

                // Keep sampled frames through the running or finishing method, when that match is verified.
                // Async callers added below replace the older V1 transition frames up to the dispatcher.
                int physicalEnd = syncEnd;
                if (fullyCompleted)
                {
                    physicalEnd = syncPosition;
                }
                else if (anchorSyncPosition >= 0 &&
                    (incompleteRoot || recoveredInner ||
                     (kind == AsyncCallstackKind.StateMachineAsync && boundaryFound)))
                {
                    physicalEnd = anchorSyncPosition + 1;
                }

                bool hasDispatchTarget = completedFrameCount < frames.FrameCount &&
                    (frames.CodeAddressAt(completedFrameCount) != CodeAddressIndex.Invalid ||
                     frames.InstructionPointerAt(completedFrameCount) != 0);
                bool sampledV2ResumeStub = kind == AsyncCallstackKind.RuntimeAsync &&
                    boundaryFound && boundaryPosition == syncPosition + 1 &&
                    IsV2ResumeStub(syncLeafToRoot[syncPosition]);

                if (kind == AsyncCallstackKind.RuntimeAsync && boundaryFound && !fullyCompleted &&
                    boundaryPosition > syncPosition && IsV2ResumeStub(syncLeafToRoot[boundaryPosition - 1]) &&
                    (currentSyncPosition >= syncPosition ||
                     nativeV2DispatchTarget ||
                     (sampledV2ResumeStub && hasDispatchTarget)))
                {
                    // Consume only the single stub connecting a matched body or verified entry work to its wrapper.
                    physicalEnd--;
                    diagnostics.V2InfrastructureFramesCollapsed++;
                }

                for (int i = syncPosition; i < physicalEnd; i++)
                {
                    AsyncProfilerStitchSyncFrame physicalFrame = syncLeafToRoot[i];
                    AsyncProfilerStitchedFrame frame;
                    if (i == currentSyncPosition)
                    {
                        if (kind == AsyncCallstackKind.StateMachineAsync &&
                            physicalFrame.Method != methodOf(frames.CodeAddressAt(completedFrameCount)))
                        {
                            frame = AsyncProfilerStitchedFrame.CreateAsyncDispatchTarget(physicalFrame,
                                segment, completedFrameCount);
                        }
                        else
                        {
                            frame = AsyncProfilerStitchedFrame.CreateAsyncCurrent(physicalFrame.CodeAddress,
                                physicalFrame.Method, segment, completedFrameCount);
                        }
                    }
                    else if (i == completingSyncPosition)
                    {
                        frame = AsyncProfilerStitchedFrame.CreateV1CompletedBody(physicalFrame.CodeAddress,
                            physicalFrame.Method, segment, completedFrameCount - 1);
                    }
                    else
                    {
                        frame = AsyncProfilerStitchedFrame.CreateSync(physicalFrame, segment);
                    }

                    if (frame.IsStateMachineBoxCarrier)
                    {
                        // The box identifies a pending target, not an executing application body.
                        output.Add(AsyncProfilerStitchedFrame.CreateAsyncDispatcherFrames(physicalFrame, segment));
                    }

                    output.Add(frame);
                }

                if (initialV2Dispatch)
                {
                    // Show the retained entry as dispatcher frames rather than an application method.
                    output[output.Count - 1] = output[output.Count - 1]
                        .WithPresentation(AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames);
                }

                // A known V2 target replaces its wrapper/stub, after any verified native entry work.
                // V1 retains its dispatcher as physical evidence.
                // Neither target claims that the application body is physically executing.
                if (boundaryFound &&
                    (kind == AsyncCallstackKind.StateMachineAsync ||
                     boundaryPosition == 0 || sampledV2ResumeStub || nativeV2DispatchTarget) &&
                    !fullyCompleted && currentSyncPosition < 0 && hasDispatchTarget)
                {
                    if (kind == AsyncCallstackKind.RuntimeAsync && boundaryPosition == 0)
                    {
                        diagnostics.V2LeafWrapperDropped++;
                    }
                    else if (kind == AsyncCallstackKind.StateMachineAsync && completingSyncPosition < 0)
                    {
                        output.Add(AsyncProfilerStitchedFrame.CreateSync(syncLeafToRoot[boundaryPosition], segment));
                    }
                    else if (nativeV2DispatchTarget)
                    {
                        int entry = IsV2ResumeStub(syncLeafToRoot[boundaryPosition - 1])
                            ? boundaryPosition - 1
                            : boundaryPosition;
                        output.Add(AsyncProfilerStitchedFrame.CreateAsyncDispatcherFrames(syncLeafToRoot[entry],
                            segment));
                    }

                    output.Add(AsyncProfilerStitchedFrame.CreateAsyncDispatchTarget(segment, completedFrameCount));
                }

                // Dispatch-only entry work does not establish that the pending logical continuation has resumed.
                int remainingStart = initialV2Dispatch ? frames.FrameCount : completedFrameCount + 1;
                for (int k = remainingStart; k < frames.FrameCount; k++)
                {
                    output.Add(AsyncProfilerStitchedFrame.CreateAsync(segment, k));
                }

                if (trace && diagnostics.CanRetainMessage)
                {
                    string boundaryDesc;
                    if (!boundaryFound)
                    {
                        boundaryDesc = "no-boundary";
                    }
                    else if (boundaryInfo.Kind == AsyncProfilerCallStackStitchBoundaryKind.V2Dispatcher)
                    {
                        boundaryDesc = $"{boundaryInfo.Kind}[{boundaryInfo.WrapperIndex}]@sync{boundaryPosition}";
                    }
                    else
                    {
                        boundaryDesc = $"{boundaryInfo.Kind}@sync{boundaryPosition}";
                    }

                    int splicedCount = frames.FrameCount - remainingStart;
                    if (splicedCount < 0)
                    {
                        splicedCount = 0;
                    }

                    string emittedSync = fullyCompleted
                        ? "none (fully completed)"
                        : $"[{syncPosition}..{boundaryPosition})";
                    diagnostics.Note(
                        $"segment[{diagnostics.SegmentsProcessed - 1}] kind={kind} boundary={boundaryDesc} " +
                        $"emittedSync={emittedSync} " +
                        $"completed={completedFrameCount} spliced=[{remainingStart}..{frames.FrameCount}) " +
                        $"({splicedCount} frame(s)) frameCount={frames.FrameCount}");
                }
                else if (trace)
                {
                    diagnostics.MessagesDropped++;
                }

                // Continue after the matched physical slice and its dispatcher, if present.
                if (recoveredInner)
                {
                    syncPosition = recoveredSyncEnd;
                }
                else if (boundaryFound)
                {
                    syncPosition = boundaryPosition + 1;
                }
                else
                {
                    syncPosition = syncEnd;
                }

                CodeAddressIndex instrumentedDispatch = CodeAddressIndex.Invalid;
                CodeAddressIndex dispatch = CodeAddressIndex.Invalid;
                if (boundaryFound)
                {
                    if (kind == AsyncCallstackKind.StateMachineAsync)
                    {
                        dispatch = syncLeafToRoot[boundaryPosition].CodeAddress;
                    }

                    // Consume verified runtime frames next to this dispatcher before matching the enclosing call stack.
                    // Otherwise they could be mistaken for physical work between the call stacks.
                    int infrastructureCount = MatchDispatchInfrastructure(segmentProfile, kind,
                        syncLeafToRoot, boundaryPosition, syncPosition,
                        classify, runtimeProfileResolver, runtimeTraceLog, runtimeProfiles);
                    if (kind == AsyncCallstackKind.RuntimeAsync && infrastructureCount == 2)
                    {
                        instrumentedDispatch = syncLeafToRoot[syncPosition].CodeAddress;
                        dispatch = syncLeafToRoot[syncPosition + 1].CodeAddress;
                    }

                    syncPosition += infrastructureCount;
                    if (kind == AsyncCallstackKind.StateMachineAsync)
                    {
                        diagnostics.V1InfrastructureFramesCollapsed += infrastructureCount;
                    }
                    else
                    {
                        diagnostics.V2InfrastructureFramesCollapsed += infrastructureCount;
                    }

                    // A surviving dispatcher can still have its required callers cut off inside the
                    // runtime infrastructure.
                    incompleteRoot = segmentIndex == segmentsLeafToRoot.Count - 1 &&
                        kind == AsyncCallstackKind.StateMachineAsync &&
                        syncPosition == syncLeafToRoot.Count &&
                        AreDispatcherAndCallersResolved(syncLeafToRoot, boundaryPosition,
                            runtimeTraceLog, runtimeProfiles);
                }

                // Record the async span before adding BROKEN, which marks missing physical callers.
                if (!initialV2Dispatch)
                {
                    placements?.Add(new AsyncProfilerCallStackPlacement(segment, segmentLeafIndex,
                        output.Count, instrumentedDispatch, dispatch));
                }

                if (incompleteRoot)
                {
                    output.Add(AsyncProfilerStitchedFrame.CreateIncompletePhysicalRoot());
                    diagnostics.IncompleteAsyncRootsNormalized++;
                }
            }

            // Keep the physical callers left after the last async call stack.
            for (int i = syncPosition; i < syncLeafToRoot.Count; i++)
            {
                output.Add(AsyncProfilerStitchedFrame.CreateSync(syncLeafToRoot[i], null));
            }
        }

        private static bool AreDispatcherAndCallersResolved(IReadOnlyList<AsyncProfilerStitchSyncFrame> frames,
            int boundaryIndex, TraceLog traceLog,
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

        private static int MatchDispatchInfrastructure(AsyncProfilerRuntimeRuleProfile profile, AsyncCallstackKind kind,
            IReadOnlyList<AsyncProfilerStitchSyncFrame> frames, int boundaryIndex, int start,
            Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classify,
            Func<CodeAddressIndex, AsyncProfilerRuntimeRuleProfile> runtimeProfileResolver,
            TraceLog traceLog, AsyncProfilerRuntimeRuleProfileCache runtimeProfiles)
        {
            AsyncProfilerRuntimeSequenceMatch match = profile.MatchDispatchInfrastructure(kind, frames,
                start, frames.Count, classify);
            if (match.Length > frames.Count - start)
            {
                throw new InvalidOperationException(
                    "A runtime infrastructure matcher returned a span outside its candidate range.");
            }

            if (!match.IsMatch)
            {
                return 0;
            }

            ModuleFileIndex owner = traceLog == null
                ? ModuleFileIndex.Invalid
                : runtimeProfiles.GetHostModuleOwner(
                    traceLog.CodeAddresses.ModuleFileIndex(frames[boundaryIndex].CodeAddress));

            for (int i = start; i < start + match.Length; i++)
            {
                AsyncProfilerCallStackStitchBoundaryInfo boundary = classify(frames[i].CodeAddress);
                if ((boundary.IsDispatchMarker &&
                     (kind != AsyncCallstackKind.RuntimeAsync ||
                      boundary.Kind != AsyncProfilerCallStackStitchBoundaryKind.V2DispatcherInfrastructure)) ||
                    (runtimeProfileResolver != null &&
                     !ReferenceEquals(profile, runtimeProfileResolver(frames[i].CodeAddress))))
                {
                    return 0;
                }

                if (traceLog != null)
                {
                    if (frames[i].Method == MethodIndex.Invalid ||
                        owner == ModuleFileIndex.Invalid ||
                        owner != runtimeProfiles.GetHostModuleOwner(
                            traceLog.CodeAddresses.ModuleFileIndex(frames[i].CodeAddress)))
                    {
                        return 0;
                    }
                }
            }

            return match.Length;
        }

        private static bool HasV2NativeDispatchEntry(AsyncProfilerCallStackFrames frames, int completed,
            IReadOnlyList<AsyncProfilerStitchSyncFrame> sync, int pSync, int boundaryPos,
            Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classify,
            Func<CodeAddressIndex, MethodIndex> methodOf, AsyncProfilerRuntimeRuleProfile profile,
            TraceLog traceLog, AsyncProfilerRuntimeRuleProfileCache runtimeProfiles)
        {
            if (traceLog == null || boundaryPos <= pSync ||
                completed < 0 || completed >= frames.FrameCount ||
                sync[boundaryPos].Method == MethodIndex.Invalid)
            {
                return false;
            }

            int entryPos = GetV2CurrentSyncCandidateIndex(sync, pSync, boundaryPos);
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
                ? MethodIndex.Invalid
                : methodOf(target);
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

        private static bool HasV2DispatchTarget(AsyncProfilerCallStack segment, long timestampQpc, int wrapperIndex,
            Func<AsyncCallstackKind, bool> methodCompletionObserved,
            Func<ProcessIndex, AsyncCallstackKind, long, bool> methodCompletionObservedByProcess,
            ProcessIndex processIndex)
        {
            int completed = GetV2DispatchTargetFrameIndex(segment, timestampQpc, wrapperIndex,
                methodCompletionObserved, methodCompletionObservedByProcess, processIndex);

            return completed >= 0 &&
                (segment.Frames.CodeAddressAt(completed) != CodeAddressIndex.Invalid ||
                 segment.Frames.InstructionPointerAt(completed) != 0);
        }

        private static int GetV2DispatchTargetFrameIndex(AsyncProfilerCallStack segment,
            long timestampQpc, int wrapperIndex,
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

        private static bool TryFindV2DispatchTargetWithoutWrapper(AsyncProfilerCallStack segment,
            IReadOnlyList<AsyncProfilerStitchSyncFrame> sync, long timestampQpc,
            Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classify,
            Func<AsyncCallstackKind, bool> methodCompletionObserved,
            Func<ProcessIndex, AsyncCallstackKind, long, bool> methodCompletionObservedByProcess,
            ProcessIndex processIndex, Func<CodeAddressIndex, MethodIndex> methodOf,
            TraceLog traceLog, AsyncProfilerRuntimeRuleProfileCache runtimeProfiles, AsyncProfilerCallStacksIndex index,
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
                !HasResolvedAsyncFrame(segment.Frames, completed, methodOf) ||
                index == null || !index.IsLatestCallStack(segment, timestampQpc))
            {
                return false;
            }

            // The history guard rules out a later, popped inner context whose dispatcher could still be above
            // this call stack. Earlier, popped dispatchers may remain root-ward and are left unassociated.
            for (int i = 0; i < sync.Count; i++)
            {
                AsyncProfilerCallStackStitchBoundaryKind kind = classify(sync[i].CodeAddress).Kind;
                if (kind == AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher ||
                    kind == AsyncProfilerCallStackStitchBoundaryKind.V2Dispatcher)
                {
                    return false;
                }

                if (dispatchEnd < 0 && kind == AsyncProfilerCallStackStitchBoundaryKind.V2DispatcherInfrastructure)
                {
                    dispatchStart = i;
                    dispatchEnd = i + 2;
                    if (!IsV2DispatchPairCandidate(sync, dispatchStart, classify))
                    {
                        return false;
                    }

                    if (traceLog != null &&
                        !IsV2DispatchPair(traceLog, runtimeProfiles, sync[i].CodeAddress, sync[i + 1].CodeAddress))
                    {
                        return false;
                    }
                }

                // A physical occurrence of a remaining method needs its own body/call-stack alignment,
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

        private static bool HasResolvedAsyncFrame(AsyncProfilerCallStackFrames frames, int frameIndex,
            Func<CodeAddressIndex, MethodIndex> methodOf) =>
            frameIndex >= 0 && frameIndex < frames.FrameCount &&
            frames.CodeAddressAt(frameIndex) != CodeAddressIndex.Invalid &&
            methodOf(frames.CodeAddressAt(frameIndex)) != MethodIndex.Invalid;

        private static bool IsV2DispatchPairCandidate(IReadOnlyList<AsyncProfilerStitchSyncFrame> sync,
            int start, Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classify)
        {
            // The caller has already classified the first frame as V2 dispatch infrastructure.
            int end = start + 2;
            return end < sync.Count &&
                classify(sync[start + 1].CodeAddress).Kind ==
                    AsyncProfilerCallStackStitchBoundaryKind.V2DispatcherInfrastructure &&
                classify(sync[end].CodeAddress).Kind !=
                    AsyncProfilerCallStackStitchBoundaryKind.V2DispatcherInfrastructure &&
                sync[start].Method != MethodIndex.Invalid &&
                sync[start + 1].Method != MethodIndex.Invalid &&
                sync[start].Method != sync[start + 1].Method;
        }

        private static int FindEnclosingV2CurrentSyncFrame(AsyncProfilerCallStack segment,
            IReadOnlyList<AsyncProfilerStitchSyncFrame> sync, int pSync, long timestampQpc,
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

            int boundaryPos = FindBoundary(sync, pSync, frames.Kind, classify,
                out AsyncProfilerCallStackStitchBoundaryInfo boundaryInfo);
            int currentSyncPos = GetV2CurrentSyncCandidateIndex(sync, pSync, boundaryPos);
            if (boundaryPos == sync.Count ||
                currentSyncPos < pSync ||
                sync[currentSyncPos].Method == MethodIndex.Invalid)
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
                ? currentSyncPos
                : -1;
        }

        private static bool TryMatchTruncatedRoot(AsyncProfilerCallStack segment,
            IReadOnlyList<AsyncProfilerStitchSyncFrame> sync, int pSync, long timestampQpc,
            bool completionObserved, int completed, int currentSyncPos, int recoveredTailPos,
            Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classify,
            Func<CodeAddressIndex, MethodIndex> methodOf, AsyncProfilerRuntimeRuleProfile profile,
            out int normalizedCompleted, out int normalizedCurrentSyncPos)
        {
            normalizedCompleted = completed;
            normalizedCurrentSyncPos = currentSyncPos;
            AsyncProfilerCallStackFrames frames = segment.Frames;
            int recoveredCompleted = completed;
            if (recoveredTailPos < 0 &&
                !TryRecoverTruncatedCompletionCount(frames, sync, pSync, sync.Count, methodOf, profile,
                    out recoveredCompleted, out _))
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

            int recoveredCurrentSyncPos = FindCurrentSyncFrame(frames, recoveredCompleted,
                sync, pSync, sync.Count, methodOf, frames.Kind == AsyncCallstackKind.StateMachineAsync, profile);
            if (recoveredCurrentSyncPos < 0 ||
                (frames.Kind == AsyncCallstackKind.StateMachineAsync && recoveredCurrentSyncPos != currentSyncPos))
            {
                return false;
            }

            // The required non-inlined dispatcher must be below this uniquely matched invocation. Its absence
            // places the captured root-side span inside the async call stack, not in its host. An unresolved frame
            // could conceal that boundary; an observed boundary could belong to an intact outer segment.
            for (int i = recoveredCurrentSyncPos + 1; i < sync.Count; i++)
            {
                if (sync[i].Method == MethodIndex.Invalid)
                {
                    return false;
                }

                AsyncProfilerCallStackStitchBoundaryKind kind = classify(sync[i].CodeAddress).Kind;
                if (kind == AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher ||
                    kind == AsyncProfilerCallStackStitchBoundaryKind.V2Dispatcher ||
                    kind == AsyncProfilerCallStackStitchBoundaryKind.V2DispatcherInfrastructure)
                {
                    return false;
                }
            }

            normalizedCompleted = recoveredCompleted;
            normalizedCurrentSyncPos = recoveredCurrentSyncPos;
            return true;
        }

        /// <summary>
        /// Returns the first V2 dispatcher's position (its continuation wrapper), or <c>sync.Count</c> when absent.
        /// Also reports whether V2 dispatcher infrastructure was encountered before it.
        /// </summary>
        private static int FindFirstV2Wrapper(IReadOnlyList<AsyncProfilerStitchSyncFrame> sync,
            Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classify,
            out bool hasDispatchContinuation)
        {
            hasDispatchContinuation = false;
            for (int i = 0; i < sync.Count; i++)
            {
                AsyncProfilerCallStackStitchBoundaryKind kind = classify(sync[i].CodeAddress).Kind;
                if (kind == AsyncProfilerCallStackStitchBoundaryKind.V2Dispatcher)
                {
                    return i;
                }

                if (kind == AsyncProfilerCallStackStitchBoundaryKind.V2DispatcherInfrastructure)
                {
                    hasDispatchContinuation = true;
                }
            }

            return sync.Count;
        }

        private static bool IsSampledV2WrapperEntry(IReadOnlyList<AsyncProfilerStitchSyncFrame> sync,
            int wrapperPosition) =>
            wrapperPosition == 0 || (wrapperPosition == 1 && IsV2ResumeStub(sync[0]));

        /// <summary>
        /// Finds the first sync frame at/root-ward of <paramref name="pSync"/> that is the dispatch boundary matching
        /// <paramref name="segmentKind"/>. Returns its position, or <c>sync.Count</c> if none is found.
        /// </summary>
        private static int FindBoundary(IReadOnlyList<AsyncProfilerStitchSyncFrame> sync,
            int pSync, AsyncCallstackKind segmentKind,
            Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classify,
            out AsyncProfilerCallStackStitchBoundaryInfo boundaryInfo)
        {
            for (int i = pSync; i < sync.Count; i++)
            {
                AsyncProfilerCallStackStitchBoundaryInfo info = classify(sync[i].CodeAddress);
                if (BoundaryMatchesCallStackKind(info.Kind, segmentKind))
                {
                    boundaryInfo = info;
                    return i;
                }
            }

            boundaryInfo = AsyncProfilerCallStackStitchBoundaryInfo.None;
            return sync.Count;
        }

        /// <summary>Whether a dispatcher boundary matches an async segment's kind (V2 vs V1).</summary>
        private static bool BoundaryMatchesCallStackKind(AsyncProfilerCallStackStitchBoundaryKind boundaryKind,
            AsyncCallstackKind segmentKind)
        {
            switch (segmentKind)
            {
                case AsyncCallstackKind.RuntimeAsync: // V2
                    // The V2 dispatcher boundary is its continuation wrapper, which identifies the resumption position.
                    // A sampled-leaf wrapper identifies a dispatch target, not a physical body. Dispatch-continuation
                    // infrastructure alone is not a splice boundary.
                    return boundaryKind == AsyncProfilerCallStackStitchBoundaryKind.V2Dispatcher;

                case AsyncCallstackKind.StateMachineAsync: // V1
                    return boundaryKind == AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Selects recorded completion counts when normal-completion events were observed for this call stack.
        /// Otherwise V1 reconciles ordered physical matches with unwind counts, while V2 uses wrapper progress.
        /// Wrapper progress already includes exceptions; V1 physical and unwind evidence must not be double-counted.
        /// </summary>
        private static int ComputeCompletedFrameCount(AsyncProfilerCallStack segment,
            AsyncProfilerCallStackFrames frames, AsyncCallstackKind kind,
            AsyncProfilerCallStackStitchBoundaryInfo boundaryInfo,
            bool boundaryFound, bool allowFullyCompleted, long timestampQpc,
            IReadOnlyList<AsyncProfilerStitchSyncFrame> sync, int pSync, int boundaryPos,
            Func<AsyncCallstackKind, bool> methodCompletionObserved,
            Func<ProcessIndex, AsyncCallstackKind, long, bool> methodCompletionObservedByProcess,
            ProcessIndex processIndex, Func<CodeAddressIndex, MethodIndex> methodOf,
            AsyncProfilerCallStackDiagnostics diagnostics, AsyncProfilerRuntimeRuleProfile profile,
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
                    int slot = boundaryInfo.Kind == AsyncProfilerCallStackStitchBoundaryKind.V2Dispatcher
                        ? boundaryInfo.WrapperIndex
                        : 0;
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
                        TryRecoverTruncatedCompletionCount(frames, sync, pSync, boundaryPos, methodOf, profile,
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
                        TryRecoverTruncatedCompletionCount(frames, sync, pSync, boundaryPos, methodOf, profile,
                            out completed, out recoveredTailPos))
                    {
                        diagnostics.V1TruncatedSuffixFallbackUsed++;
                    }
                    else
                    {
                        completed = InferV1CompletedFrameCount(frames, sync, pSync, boundaryPos,
                            exceptionCompleted, boundaryFound, allowFullyCompleted, methodOf, profile,
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
                    diagnostics.Note(
                        "V1 completion records overlap physical matches ambiguously; retained the physical slice.");
                }
            }

            int maxIndex = allowFullyCompleted || kind == AsyncCallstackKind.StateMachineAsync
                ? frames.FrameCount
                : frames.FrameCount - 1;
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
        private static bool TryRecoverTruncatedCompletionCount(AsyncProfilerCallStackFrames frames,
            IReadOnlyList<AsyncProfilerStitchSyncFrame> sync, int pSync, int boundaryPos,
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
                    // The unique physical body sequence independently proves the truncated call stack.
                    // Its next inline-resumed method can be represented by one leaf-side execution carrier.
                    completed++;
                }
            }

            return true;
        }

        /// <summary>
        /// Matches V1 methods from the dispatcher toward the physical leaf, following captured frame order.
        /// The final match is normally current; earlier matches have completed. Recorded exception counts
        /// bound how many missing async frames may be skipped.
        /// Only a uniquely resolved CoreLib box carrier before any physical async body can identify the final
        /// resumption target inside a verified dispatcher boundary. Other box frames are not counted.
        /// Unresolved or ambiguous matches do not justify additional completion guesses.
        /// </summary>
        private static int InferV1CompletedFrameCount(AsyncProfilerCallStackFrames frames,
            IReadOnlyList<AsyncProfilerStitchSyncFrame> sync, int pSync, int boundaryPos,
            int exceptionCompleted, bool boundaryFound, bool allowFullyCompleted,
            Func<CodeAddressIndex, MethodIndex> methodOf, AsyncProfilerRuntimeRuleProfile profile,
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
                ? FindV1BoxCarrier(frames, sync, pSync, boundaryPos, methodOf, profile)
                : -1;

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

        private static bool TryFindV1CompletingFrame(AsyncProfilerCallStackFrames frames, int completed,
            IReadOnlyList<AsyncProfilerStitchSyncFrame> sync, int pSync, int boundaryPos,
            Func<CodeAddressIndex, MethodIndex> methodOf,
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

        private static int FindCurrentSyncFrame(AsyncProfilerCallStackFrames frames, int completed,
            IReadOnlyList<AsyncProfilerStitchSyncFrame> sync, int pSync, int boundaryPos,
            Func<CodeAddressIndex, MethodIndex> methodOf, bool allowBoxCarrier, AsyncProfilerRuntimeRuleProfile profile)
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
            int carrier = allowBoxCarrier
                ? FindV1BoxCarrier(frames, sync, pSync, boundaryPos, methodOf, profile)
                : -1;
            for (int i = pSync; i < boundaryPos; i++)
            {
                if (MatchesV1Method(sync[i], current, i == carrier))
                {
                    return i;
                }
            }

            return -1;
        }

        private static int FindV1BoxCarrier(AsyncProfilerCallStackFrames frames,
            IReadOnlyList<AsyncProfilerStitchSyncFrame> sync, int pSync, int boundaryPos,
            Func<CodeAddressIndex, MethodIndex> methodOf, AsyncProfilerRuntimeRuleProfile profile)
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

        private static bool MatchesV1Method(AsyncProfilerStitchSyncFrame frame,
            MethodIndex expected, bool allowBoxCarrier) =>
            frame.Method == expected ||
            (allowBoxCarrier &&
             frame.CodeAddress != CodeAddressIndex.Invalid &&
             frame.Method != MethodIndex.Invalid &&
             frame.BoxedStateMachineMethod == expected);

        private static int FindCurrentV2SyncFrame(AsyncProfilerCallStackFrames frames, int completed,
            IReadOnlyList<AsyncProfilerStitchSyncFrame> sync, int pSync, int boundaryPos,
            Func<CodeAddressIndex, MethodIndex> methodOf, bool boundaryFound,
            AsyncProfilerCallStackDiagnostics diagnostics)
        {
            int currentSyncPos = GetV2CurrentSyncCandidateIndex(sync, pSync, boundaryPos);
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
                    diagnostics.Note(
                        $"V2 current async method (segment frame {completed}) did not match the sync frame " +
                        $"leaf-ward of the boundary and any recognized resume stub (frame {currentSyncPos}).");
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

        private static int GetV2CurrentSyncCandidateIndex(IReadOnlyList<AsyncProfilerStitchSyncFrame> sync,
            int pSync, int boundaryPos)
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

        #endregion
    }
}
