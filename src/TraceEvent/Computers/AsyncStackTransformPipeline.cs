// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>
    /// Per-sample context supplied to an async stack presentation transform. Structural stitching has already
    /// completed, and <see cref="Frames"/> contains the complete stitched stack leaf-to-root.
    /// </summary>
    public readonly struct AsyncStackTransformContext
    {
        internal AsyncStackTransformContext(
            TraceLog traceLog,
            IReadOnlyList<AsyncCallStack> segments,
            long timestampQpc,
            Func<CodeAddressIndex, AsyncStitchBoundaryInfo> classifyBoundary,
            StitchDiagnostics diagnostics,
            List<StitchedFrame> frames)
        {
            TraceLog = traceLog;
            Segments = segments;
            TimestampQpc = timestampQpc;
            m_classifyBoundary = classifyBoundary;
            Diagnostics = diagnostics;
            Frames = frames;
        }

        /// <summary>
        /// The trace whose code addresses are referenced by <see cref="Frames"/>, or null if a standalone
        /// <see cref="AsyncStackTransformPipeline.Transform"/> call supplies no trace context. Built-in transforms
        /// requiring module or symbol metadata fail open when this is null.
        /// </summary>
        public TraceLog TraceLog { get; }

        /// <summary>The active async segments used by structural stitching, ordered root-to-leaf.
        /// During computer callbacks, the list and its activation objects are reused and must not be retained.</summary>
        public IReadOnlyList<AsyncCallStack> Segments { get; }

        /// <summary>The sample timestamp in the trace QPC domain.</summary>
        public long TimestampQpc { get; }

        /// <summary>Diagnostics for this sample. Built-in and custom transforms may add presentation counters.</summary>
        public StitchDiagnostics Diagnostics { get; }

        /// <summary>The complete mutable stitched stack, leaf-to-root.</summary>
        public List<StitchedFrame> Frames { get; }

        /// <summary>Classifies a frame using the runtime boundary-name contract for this trace.</summary>
        public AsyncStitchBoundaryInfo ClassifyBoundary(CodeAddressIndex codeAddress) =>
            m_classifyBoundary(codeAddress);

        #region private

        private readonly Func<CodeAddressIndex, AsyncStitchBoundaryInfo> m_classifyBoundary;

        #endregion
    }

    /// <summary>A presentation transform applied to a structurally stitched async call stack.</summary>
    /// <remarks>
    /// The context, its segment list, and the mutable frame list are owned and reused by the computer and must not
    /// be retained after the callback returns. Copy any identity values needed later; copying a frame alone does
    /// not detach its <see cref="StitchedFrame.Context"/> from the computer's reusable activation object.
    /// </remarks>
    public delegate void AsyncStackTransform(AsyncStackTransformContext context);

    /// <summary>
    /// Ordered presentation pipeline for structurally stitched async call stacks. The mandatory structural
    /// stitcher always runs before this pipeline. Conservative normalization is enabled by default;
    /// runtime-specific async-infrastructure cleanup is opt-in; custom transforms run last in registration order.
    /// </summary>
    /// <remarks>
    /// Configure the host computer's pipeline before stack generation. <see cref="Transform"/> applies only
    /// conservative normalization, async-infrastructure cleanup, and custom callbacks to an existing structural result.
    /// Context ancestry and frame annotations are separate host-builder/emitter options.
    /// </remarks>
    public sealed class AsyncStackTransformPipeline
    {
        /// <summary>Creates the default pipeline with conservative presentation normalization enabled.</summary>
        public AsyncStackTransformPipeline()
        {
            EnableConservativeTransforms = true;
            m_customTransforms = new List<AsyncStackTransform>();
        }

        /// <summary>
        /// Enables default strong-signal presentation transforms that fail open when their exact frame pattern is
        /// absent. This includes V1 synchronous-startup normalization, exact reentrant-activation deduplication, and
        /// V2 sampled-leaf wrapper cleanup. It also collapses an all-System.Private.CoreLib synchronous handoff span
        /// between exactly matched adjacent RuntimeAsync activations with verified dispatcher boundaries,
        /// and an exact classified completion bridge
        /// from a remaining V2 dispatcher to a matched V1 activation. Unresolved, non-CoreLib, or incomplete
        /// handoffs preserve the complete span. Disabling this does not disable structural boundary replacement
        /// or verified incomplete-root normalization to <c>BROKEN</c>.
        /// </summary>
        public bool EnableConservativeTransforms { get; set; }

        /// <summary>
        /// Enables the host computer's insertion of missing historical creation-parent contexts before presentation.
        /// Requires captured creation relationships and parent resume intervals; unavailable ancestry is not inferred.
        /// Disabled by default. This option is not applied by standalone <see cref="Transform"/> calls.
        /// </summary>
        public bool EnableContextAncestry { get; set; }

        /// <summary>
        /// Prefixes active async-profiler frames with <c>[Async V1]</c> or <c>[Async V2]</c> when they are
        /// interned. Retained synchronous frames keep their ordinary names, and this option does not add sync/async
        /// transition frames. V1 dispatch targets use the same names and annotations as ordinary active V1 frames;
        /// their dispatch role is retained in frame metadata rather than the name. Disabled by default.
        /// Matched current V1 frames display inferred running state <c>-1</c>, and record-verified physical
        /// completing frames display <c>-2</c>; other V1 frames display
        /// their captured compiler state. Raw captured states remain available in frame metadata.
        /// The host emitter applies this option; standalone
        /// <see cref="Transform"/> calls do not produce frame names.
        /// </summary>
        public bool EnableActiveAsyncFrameAnnotations { get; set; }

        /// <summary>The maximum number of missing parent contexts inserted for one active segment when
        /// <see cref="EnableContextAncestry"/> is enabled.</summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
        public int MaximumContextAncestryDepth
        {
            get => m_maximumContextAncestryDepth;
            set
            {
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(MaximumContextAncestryDepth));
                }
                m_maximumContextAncestryDepth = value;
            }
        }

        /// <summary>
        /// Enables presentation cleanup of recognized V1/V2 async runtime infrastructure, including method builders,
        /// state-machine boxes, awaiter registration, async dispatchers, and completion bridges. This is not general
        /// framework filtering: it targets known helpers owned by System.Private.CoreLib. Disabled by default because
        /// these implementation details are not part of the async-profiler contract.
        /// Before individual-helper cleanup, it atomically folds fully resolved CoreLib resumption bridges from
        /// a matched async activation to <c>ThreadPoolWorkQueue.Dispatch</c>, retaining that dispatch anchor.
        /// V1 requires a verified dispatcher boundary; V2 also requires a physically matched current async method.
        /// Bridges require adjacent <c>Task.RunContinuations -&gt; Task.TrySetResult</c> evidence. Interrupted,
        /// unresolved, or non-CoreLib candidates are retained intact, including their recognized helpers.
        /// Outside rejected candidates, it removes the exact adjacent
        /// <c>Task.RunContinuations -&gt; Task.TrySetResult -&gt; TaskCompletionSource.SetResult</c> chain.
        /// A resolved CoreLib profiler <c>Resume</c> immediately above a verified V1 dispatcher or physically
        /// matched current/completing async method or V1 box carrier establishes ownership of the synchronous
        /// leaf-side prefix.
        /// That entire prefix, including unresolved frames, initialization, GC, and callbacks invoked by the
        /// profiler, is atomically attributed to the nearest active logical async frame. Capture and emission
        /// helpers need not be visible because they may inline. All prefix frames must belong to the same active
        /// activation; cleanup never crosses another async boundary or a physically matched application anchor.
        /// A registered V1 target without a physical current-method match keeps its dispatch-target metadata,
        /// even when its retained physical dispatcher is removed by cleanup.
        /// This is overhead attribution, not evidence that a suspended caller was physically executing.
        /// Missing, unresolved, foreign, interrupted, or context-inconsistent ownership evidence leaves the
        /// candidate prefix protected from general helper cleanup; no partial profiler tail is removed.
        /// Outside rejected candidates, the exact CoreLib <c>AsyncStateMachineDispatcherInfo.CreateDispatcher</c>
        /// helper is removed individually, preserving the application kickoff frame.
        /// Application frames outside profiler-owned prefixes, thread roots, isolated
        /// <c>TaskCompletionSource.SetResult</c> frames, and other profiler work outside a verified prefix are retained.
        /// This option is independent of structural stitching and
        /// <see cref="EnableConservativeTransforms"/>.
        /// </summary>
        public bool EnableAsyncInfrastructureCleanup { get; set; }

        /// <summary>Adds a custom transform that runs after all enabled built-in transforms.</summary>
        public void Add(AsyncStackTransform transform)
        {
            if (transform is null)
            {
                throw new ArgumentNullException(nameof(transform));
            }

            m_customTransforms.Add(transform);
        }

        /// <summary>Removes all custom transforms. Built-in enablement is unchanged.</summary>
        public void ClearCustomTransforms() => m_customTransforms.Clear();

        /// <summary>
        /// Applies this pipeline to a structural stitch result without requiring a
        /// <see cref="SampleProfilerThreadTimeComputer"/>. The input frame list is not modified; its frames are
        /// copied into a mutable output list. Presentation diagnostics are added to the result's existing
        /// <see cref="StitchResult.Diagnostics"/> instance, which is shared by both results. This method does not
        /// insert historical ancestry or intern/annotate output frames.
        /// </summary>
        /// <param name="structuralResult">The result produced by <see cref="AsyncCpuStackStitcher"/>.</param>
        /// <param name="segments">The active async segments used to produce the structural result.</param>
        /// <param name="timestampQpc">The sample timestamp in the trace QPC domain.</param>
        /// <param name="classifyBoundary">Classifies runtime boundary methods for the trace.</param>
        /// <param name="traceLog">
        /// Optional trace context exposed to custom transforms and used by built-in rules that require module or
        /// symbol metadata. Those built-in rules fail open when it is null.
        /// </param>
        public StitchResult Transform(
            StitchResult structuralResult,
            IReadOnlyList<AsyncCallStack> segments,
            long timestampQpc,
            Func<CodeAddressIndex, AsyncStitchBoundaryInfo> classifyBoundary,
            TraceLog traceLog = null)
        {
            if (structuralResult is null)
            {
                throw new ArgumentNullException(nameof(structuralResult));
            }
            if (segments is null)
            {
                throw new ArgumentNullException(nameof(segments));
            }
            if (classifyBoundary is null)
            {
                throw new ArgumentNullException(nameof(classifyBoundary));
            }

            var frames = new List<StitchedFrame>(structuralResult.Frames);
            var context = new AsyncStackTransformContext(
                traceLog,
                segments,
                timestampQpc,
                classifyBoundary,
                structuralResult.Diagnostics,
                frames);
            ApplyInPlace(context);
            return new StitchResult(frames, structuralResult.Diagnostics);
        }

        internal void ApplyInPlace(AsyncStackTransformContext context)
        {
            if (EnableConservativeTransforms)
            {
                ApplyConservativeTransforms(context);
            }

            if (EnableAsyncInfrastructureCleanup)
            {
                ApplyAsyncInfrastructureCleanup(context);
            }

            for (int i = 0; i < m_customTransforms.Count; i++)
            {
                m_customTransforms[i](context);
            }
        }

        private static void ApplyConservativeTransforms(AsyncStackTransformContext context)
        {
            List<StitchedFrame> frames = context.Frames;

            // The structural V2 gate preserves the physical sync layout when the wrapper itself is the sampled
            // leaf. Removing that one known transition frame is presentation-only and intentionally opt-out-able.
            if (context.Diagnostics.V2SyncLayoutUsed != 0 &&
                frames.Count != 0 &&
                frames[0].Origin == StitchedFrameOrigin.Sync &&
                context.ClassifyBoundary(frames[0].CodeAddress).Kind ==
                    AsyncStitchBoundaryKind.V2ContinuationWrapper)
            {
                frames.RemoveAt(0);
                context.Diagnostics.V2LeafWrapperDropped++;
            }

            CollapseV2ReentrantHandoffs(context);

            for (int i = 0; i < frames.Count; i++)
            {
                StitchedFrame frame = frames[i];
                if (frame.Origin == StitchedFrameOrigin.AsyncCurrent || frame.IsStateMachineBoxCarrier)
                {
                    if (frame.Segment != null && frame.Segment.Kind == AsyncCallstackKind.StateMachineAsync)
                    {
                        if (frame.Origin == StitchedFrameOrigin.AsyncCurrent)
                        {
                            CollapseV2CompletionHandoff(context, i);
                        }
                        TryApplyReentrantV1Deduplication(context, i);
                    }
                    continue;
                }

                if (frame.Origin != StitchedFrameOrigin.Sync ||
                    frame.SyncFrameKind != StitchSyncFrameKind.V1StateMachineMoveNext)
                {
                    continue;
                }

                int end = i + 1;
                while (end < frames.Count &&
                       frames[end].Origin == StitchedFrameOrigin.Sync &&
                       frames[end].SyncFrameKind == StitchSyncFrameKind.V1MethodBuilderStart)
                {
                    end++;
                }

                int builderCount = end - i - 1;
                if (builderCount != 0 &&
                    end < frames.Count &&
                    context.TraceLog != null &&
                    IsMatchingV1Kickoff(context.TraceLog, frame, frames[end]))
                {
                    int generatedFrameCount = builderCount + 1;
                    frames.RemoveRange(i, generatedFrameCount);
                    context.Diagnostics.V1SynchronousStartupFramesCollapsed += generatedFrameCount;
                    continue;
                }

                frames[i] = frame.WithPresentation(StitchedFramePresentation.LogicalStateMachineMethod);
                context.Diagnostics.V1SynchronousMoveNextFramesNormalized++;

                if (end == i + 1)
                {
                    continue;
                }

                frames.RemoveRange(i + 1, builderCount);
                context.Diagnostics.V1SynchronousStartupFramesCollapsed += builderCount;
            }
        }

        private static void CollapseV2ReentrantHandoffs(AsyncStackTransformContext context)
        {
            if (context.TraceLog == null ||
                context.Diagnostics.BoundariesNotFound != 0 ||
                context.Diagnostics.AdjacencyMismatches != 0)
            {
                return;
            }

            List<StitchedFrame> frames = context.Frames;
            for (int currentIndex = 0; currentIndex < frames.Count; currentIndex++)
            {
                StitchedFrame outerCurrent = frames[currentIndex];
                if (outerCurrent.Origin != StitchedFrameOrigin.AsyncCurrent ||
                    outerCurrent.Context == null ||
                    outerCurrent.Context.Frames.Kind != AsyncCallstackKind.RuntimeAsync)
                {
                    continue;
                }

                int handoffStart = currentIndex;
                while (handoffStart > 0 &&
                       frames[handoffStart - 1].Origin == StitchedFrameOrigin.Sync &&
                       ReferenceEquals(frames[handoffStart - 1].Context, outerCurrent.Context))
                {
                    handoffStart--;
                }

                int handoffCount = currentIndex - handoffStart;
                if (handoffCount == 0 || handoffStart == 0)
                {
                    continue;
                }

                AsyncCallStack innerContext = frames[handoffStart - 1].Context;
                if (innerContext == null ||
                    innerContext.Frames.Kind != AsyncCallstackKind.RuntimeAsync ||
                    innerContext.Depth != outerCurrent.Context.Depth + 1 ||
                    !HasAsyncCurrent(frames, handoffStart - 1, innerContext) ||
                    !IsSystemPrivateCoreLibSpan(context.TraceLog, frames, handoffStart, handoffCount))
                {
                    continue;
                }

                frames.RemoveRange(handoffStart, handoffCount);
                context.Diagnostics.V2ReentrantHandoffFramesCollapsed += handoffCount;
                currentIndex = handoffStart;
            }
        }

        private static bool HasAsyncCurrent(
            List<StitchedFrame> frames,
            int contextEnd,
            AsyncCallStack context)
        {
            for (int i = contextEnd; i >= 0 && ReferenceEquals(frames[i].Context, context); i--)
            {
                if (frames[i].Origin == StitchedFrameOrigin.AsyncCurrent)
                {
                    return true;
                }
            }

            return false;
        }

        private static void CollapseV2CompletionHandoff(AsyncStackTransformContext context, int currentIndex)
        {
            if (context.TraceLog == null ||
                context.Diagnostics.BoundariesNotFound != 0 ||
                context.Diagnostics.AdjacencyMismatches != 0)
            {
                return;
            }

            List<StitchedFrame> frames = context.Frames;
            const int handoffCount = 5;
            StitchedFrame current = frames[currentIndex];
            if (current.Context == null ||
                current.Context.Frames.Kind != AsyncCallstackKind.StateMachineAsync ||
                current.Method == MethodIndex.Invalid ||
                current.CodeAddress == CodeAddressIndex.Invalid)
            {
                return;
            }

            int handoffStart = currentIndex + 1;
            while (handoffStart < frames.Count &&
                   frames[handoffStart].Origin == StitchedFrameOrigin.AsyncRemaining &&
                   ReferenceEquals(frames[handoffStart].Context, current.Context))
            {
                handoffStart++;
            }

            if (handoffStart + handoffCount >= frames.Count ||
                !IsSyncFrameKind(frames[handoffStart],
                    StitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure) ||
                !IsSyncFrameKind(frames[handoffStart + 1],
                    StitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations) ||
                !IsSyncFrameKind(frames[handoffStart + 2],
                    StitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult))
            {
                return;
            }

            bool unassignedSyncTail = true;
            for (int i = handoffStart; i <= handoffStart + handoffCount; i++)
            {
                if (frames[i].Origin != StitchedFrameOrigin.Sync || frames[i].Context != null)
                {
                    unassignedSyncTail = false;
                    break;
                }
            }
            if (!unassignedSyncTail ||
                !IsSystemPrivateCoreLibSpan(context.TraceLog, frames, handoffStart, handoffCount) ||
                context.ClassifyBoundary(frames[handoffStart + 3].CodeAddress).Kind !=
                    AsyncStitchBoundaryKind.V2DispatchContinuation ||
                context.ClassifyBoundary(frames[handoffStart + 4].CodeAddress).Kind !=
                    AsyncStitchBoundaryKind.V2DispatchContinuation ||
                frames[handoffStart + 3].Method == frames[handoffStart + 4].Method)
            {
                return;
            }

            StitchedFrame caller = frames[handoffStart + handoffCount];
            if (caller.Method == MethodIndex.Invalid ||
                caller.CodeAddress == CodeAddressIndex.Invalid ||
                context.ClassifyBoundary(caller.CodeAddress).Kind != AsyncStitchBoundaryKind.None)
            {
                return;
            }

            // The completed V2 dispatcher is physical only; never consume a bridge belonging to a live segment.
            frames.RemoveRange(handoffStart, handoffCount);
            context.Diagnostics.V2CompletionHandoffFramesCollapsed += handoffCount;
        }

        private static bool IsSystemPrivateCoreLibSpan(
            TraceLog traceLog,
            List<StitchedFrame> frames,
            int start,
            int count)
        {
            int end = start + count;
            for (int i = start; i < end; i++)
            {
                CodeAddressIndex codeAddressIndex = frames[i].CodeAddress;
                if (frames[i].Method == MethodIndex.Invalid ||
                    codeAddressIndex == CodeAddressIndex.Invalid ||
                    !string.Equals(
                        traceLog.CodeAddresses[codeAddressIndex].ModuleName,
                        AsyncStitchBoundary.HostModuleName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryApplyReentrantV1Deduplication(
            AsyncStackTransformContext context,
            int currentIndex)
        {
            if (context.TraceLog == null)
            {
                return false;
            }

            List<StitchedFrame> frames = context.Frames;
            StitchedFrame current = frames[currentIndex];
            if (current.Segment == null ||
                current.Segment.Kind != AsyncCallstackKind.StateMachineAsync ||
                current.Method == MethodIndex.Invalid)
            {
                return false;
            }

            int registrationStart = currentIndex + 1;
            int startupMoveNextIndex = registrationStart;
            while (startupMoveNextIndex < frames.Count &&
                   IsV1AwaitRegistrationFrame(frames[startupMoveNextIndex]))
            {
                startupMoveNextIndex++;
            }

            if (startupMoveNextIndex == registrationStart || startupMoveNextIndex >= frames.Count)
            {
                return false;
            }

            StitchedFrame startupMoveNext = frames[startupMoveNextIndex];
            MethodIndex currentMethod = current.IsStateMachineBoxCarrier
                ? current.BoxedStateMachineMethod
                : current.Method;
            if (startupMoveNext.Origin != StitchedFrameOrigin.Sync ||
                startupMoveNext.SyncFrameKind != StitchSyncFrameKind.V1StateMachineMoveNext ||
                startupMoveNext.Method != currentMethod)
            {
                return false;
            }

            int builderStart = startupMoveNextIndex + 1;
            int kickoffIndex = builderStart;
            while (kickoffIndex < frames.Count &&
                   frames[kickoffIndex].Origin == StitchedFrameOrigin.Sync &&
                   frames[kickoffIndex].SyncFrameKind == StitchSyncFrameKind.V1MethodBuilderStart)
            {
                kickoffIndex++;
            }

            if (kickoffIndex == builderStart ||
                kickoffIndex >= frames.Count ||
                !IsMatchingV1Kickoff(context.TraceLog, startupMoveNext, frames[kickoffIndex]))
            {
                return false;
            }

            // Keep the resumed AsyncCurrent representation. The exact match proves that the intervening CoreLib
            // await-registration frames, duplicate startup MoveNext, builder Start frames, and kickoff all belong
            // to the same reentrant activation, so collapse that complete generated span.
            int duplicateFrameCount = kickoffIndex - registrationStart + 1;
            frames.RemoveRange(registrationStart, duplicateFrameCount);
            context.Diagnostics.V1ReentrantFramesCollapsed += duplicateFrameCount;
            return true;
        }

        private static bool IsV1AwaitRegistrationFrame(StitchedFrame frame) =>
            frame.Origin == StitchedFrameOrigin.Sync &&
            (frame.SyncFrameKind == StitchSyncFrameKind.V1MethodBuilderAwaitUnsafeOnCompleted ||
             frame.SyncFrameKind == StitchSyncFrameKind.V1AwaiterRegistrationInfrastructure);

        private static bool IsMatchingV1Kickoff(
            TraceLog traceLog,
            StitchedFrame stateMachineMoveNext,
            StitchedFrame kickoff)
        {
            if (kickoff.Origin != StitchedFrameOrigin.Sync ||
                stateMachineMoveNext.CodeAddress == CodeAddressIndex.Invalid ||
                kickoff.CodeAddress == CodeAddressIndex.Invalid)
            {
                return false;
            }

            TraceCodeAddress moveNextCodeAddress = traceLog.CodeAddresses[stateMachineMoveNext.CodeAddress];
            TraceCodeAddress kickoffCodeAddress = traceLog.CodeAddresses[kickoff.CodeAddress];
            if (!string.Equals(
                    moveNextCodeAddress.ModuleName,
                    kickoffCodeAddress.ModuleName,
                    StringComparison.OrdinalIgnoreCase) ||
                !AsyncStitchBoundary.TryGetLogicalStateMachineMethodName(
                    moveNextCodeAddress.FullMethodName, out string logicalMethod))
            {
                return false;
            }

            string kickoffMethod = kickoffCodeAddress.FullMethodName;
            if (string.IsNullOrEmpty(kickoffMethod))
            {
                return false;
            }

            int parameters = kickoffMethod.IndexOf('(');
            if (parameters >= 0)
            {
                kickoffMethod = kickoffMethod.Substring(0, parameters);
            }

            return string.Equals(
                logicalMethod,
                kickoffMethod.Replace('+', '.'),
                StringComparison.Ordinal);
        }

        private static void ApplyAsyncInfrastructureCleanup(AsyncStackTransformContext context)
        {
            List<StitchedFrame> frames = context.Frames;
            int cleanupEnd = CollapseRuntimeResumptionBridge(context);
            int cleanupStart = CollapseAsyncProfilerOwnedPrefix(context, ref cleanupEnd);

            // Remove the public SetResult trigger only as part of the exact inline-completion chain. Removing the
            // two implementation frames independently would otherwise leave SetResult as an orphan between the
            // resumed async method and its synchronous caller.
            for (int i = cleanupStart; i + 2 < cleanupEnd;)
            {
                if (IsSyncFrameKind(frames[i], StitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations) &&
                    IsSyncFrameKind(frames[i + 1], StitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult) &&
                    IsSyncFrameKind(
                        frames[i + 2],
                        StitchSyncFrameKind.SystemPrivateCoreLibTaskCompletionSourceSetResult))
                {
                    frames.RemoveRange(i, 3);
                    cleanupEnd -= 3;
                    context.Diagnostics.AsyncInfrastructureFramesCollapsed += 3;
                }
                else
                {
                    i++;
                }
            }

            for (int i = cleanupEnd - 1; i >= cleanupStart; i--)
            {
                StitchedFrame frame = frames[i];
                if (frame.Origin != StitchedFrameOrigin.Sync)
                {
                    continue;
                }

                AsyncStitchBoundaryKind boundaryKind = context.ClassifyBoundary(frame.CodeAddress).Kind;
                if (boundaryKind == AsyncStitchBoundaryKind.V1Dispatcher ||
                    boundaryKind == AsyncStitchBoundaryKind.V1DispatcherInfrastructure ||
                    boundaryKind == AsyncStitchBoundaryKind.V2ContinuationWrapper ||
                    boundaryKind == AsyncStitchBoundaryKind.V2DispatchContinuation ||
                    frame.SyncFrameKind == StitchSyncFrameKind.V1MethodBuilderStart ||
                    frame.SyncFrameKind == StitchSyncFrameKind.V1MethodBuilderAwaitUnsafeOnCompleted ||
                    frame.SyncFrameKind == StitchSyncFrameKind.V1MethodBuilderInfrastructure ||
                    frame.SyncFrameKind == StitchSyncFrameKind.V1MethodBuilderCompletion ||
                    frame.SyncFrameKind == StitchSyncFrameKind.V1AwaiterRegistrationInfrastructure ||
                    frame.SyncFrameKind == StitchSyncFrameKind.V1DispatcherCreationInfrastructure ||
                    frame.SyncFrameKind == StitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure ||
                    frame.SyncFrameKind == StitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations ||
                    frame.SyncFrameKind == StitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult)
                {
                    frames.RemoveAt(i);
                    context.Diagnostics.AsyncInfrastructureFramesCollapsed++;
                }
            }
        }

        private static int CollapseAsyncProfilerOwnedPrefix(AsyncStackTransformContext context, ref int cleanupEnd)
        {
            if (context.TraceLog == null || context.Segments.Count == 0)
            {
                return 0;
            }

            List<StitchedFrame> frames = context.Frames;
            int targetIndex = frames.Count;
            bool candidate = false;
            for (int i = 0; i < frames.Count; i++)
            {
                StitchedFrame frame = frames[i];
                if (frame.ContextKind == StitchedFrameContextKind.Active &&
                    (frame.Origin == StitchedFrameOrigin.AsyncCurrent ||
                     frame.Origin == StitchedFrameOrigin.AsyncCompleting ||
                     frame.Origin == StitchedFrameOrigin.AsyncRemaining ||
                     frame.Origin == StitchedFrameOrigin.AsyncDispatchTarget))
                {
                    targetIndex = i;
                    break;
                }
                candidate |= frame.SyncFrameKind == StitchSyncFrameKind.AsyncProfilerCallstackCapture ||
                             frame.SyncFrameKind == StitchSyncFrameKind.AsyncProfilerCallstackEmission ||
                             frame.SyncFrameKind == StitchSyncFrameKind.AsyncProfilerContextResume;
            }
            if (!candidate)
            {
                return 0;
            }

            // Keep rejected prefixes, including box helpers, out of subsequent individual-frame cleanup.
            if (targetIndex == frames.Count ||
                context.Diagnostics.SegmentsProcessed != context.Segments.Count ||
                context.Diagnostics.BoundariesNotFound != 0 ||
                context.Diagnostics.AdjacencyMismatches != 0)
            {
                return targetIndex;
            }

            StitchedFrame target = frames[targetIndex];
            int entryIndex = targetIndex - 1;
            int dispatcherIndex = -1;
            if (target.Origin == StitchedFrameOrigin.AsyncDispatchTarget && !target.IsStateMachineBoxCarrier)
            {
                dispatcherIndex = entryIndex;
                if (target.Segment.Kind != AsyncCallstackKind.StateMachineAsync ||
                    dispatcherIndex < 0 ||
                    context.ClassifyBoundary(frames[dispatcherIndex].CodeAddress).Kind !=
                        AsyncStitchBoundaryKind.V1Dispatcher)
                {
                    return targetIndex;
                }
                entryIndex--;
            }
            else if (target.Origin != StitchedFrameOrigin.AsyncCurrent &&
                     target.Origin != StitchedFrameOrigin.AsyncCompleting &&
                     !target.IsStateMachineBoxCarrier)
            {
                return targetIndex;
            }
            if (entryIndex < 0 ||
                !IsSyncFrameKind(frames[entryIndex], StitchSyncFrameKind.AsyncProfilerContextResume) ||
                !IsSystemPrivateCoreLibSpan(context.TraceLog, frames, entryIndex, targetIndex - entryIndex))
            {
                return targetIndex;
            }

            // Only the entry spine needs symbols. Everything leafward is callee work owned by that invocation.
            // A physical current/completing anchor remains intact even when stitching already removed its dispatcher.
            for (int i = 0; i < targetIndex; i++)
            {
                StitchedFrame frame = frames[i];
                if (frame.Origin != StitchedFrameOrigin.Sync ||
                    frame.ContextKind != StitchedFrameContextKind.Active ||
                    !ReferenceEquals(frame.Context, target.Context) ||
                    (i != dispatcherIndex &&
                     context.ClassifyBoundary(frame.CodeAddress).IsBoundary))
                {
                    return targetIndex;
                }
            }

            frames.RemoveRange(0, targetIndex);
            cleanupEnd -= targetIndex;
            context.Diagnostics.AsyncInfrastructureFramesCollapsed += targetIndex;
            context.Diagnostics.AsyncProfilerEnvelopeFramesCollapsed += targetIndex;
            return 0;
        }

        private static int CollapseRuntimeResumptionBridge(AsyncStackTransformContext context)
        {
            List<StitchedFrame> frames = context.Frames;
            if (context.TraceLog == null ||
                context.Segments.Count == 0 ||
                context.Diagnostics.SegmentsProcessed != context.Segments.Count ||
                context.Diagnostics.BoundariesNotFound != 0 ||
                context.Diagnostics.AdjacencyMismatches != 0)
            {
                return frames.Count;
            }

            AsyncCallStack rootContext = context.Segments[0];
            int rootIndex = -1;
            bool currentMatched = false;
            for (int i = frames.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(frames[i].Context, rootContext) &&
                    frames[i].ContextKind == StitchedFrameContextKind.Active)
                {
                    if (rootIndex < 0)
                    {
                        rootIndex = i;
                    }
                    currentMatched |= frames[i].Origin == StitchedFrameOrigin.AsyncCurrent &&
                                      frames[i].Method != MethodIndex.Invalid;
                }
            }
            if (rootIndex < 0 ||
                (rootContext.Frames.Kind == AsyncCallstackKind.RuntimeAsync && !currentMatched))
            {
                return frames.Count;
            }

            int bridgeStart = rootIndex + 1;
            while (bridgeStart < frames.Count &&
                   frames[bridgeStart].Origin == StitchedFrameOrigin.AsyncContextParent)
            {
                bridgeStart++;
            }

            int dispatchIndex = frames.Count;
            bool candidate = false;
            bool completionPair = false;
            for (int i = bridgeStart; i < frames.Count; i++)
            {
                if (frames[i].SyncFrameKind == StitchSyncFrameKind.SystemPrivateCoreLibThreadPoolDispatch)
                {
                    dispatchIndex = i;
                    break;
                }
                if (frames[i].SyncFrameKind == StitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure ||
                    frames[i].SyncFrameKind == StitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations ||
                    frames[i].SyncFrameKind == StitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult)
                {
                    candidate = true;
                }
                if (i + 1 < frames.Count &&
                    IsSyncFrameKind(frames[i], StitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations) &&
                    IsSyncFrameKind(frames[i + 1], StitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult))
                {
                    completionPair = true;
                }
            }
            if (!candidate)
            {
                return frames.Count;
            }

            // Rejected bridges remain intact; individual-helper cleanup only visits their leaf-ward prefix.
            if (!completionPair ||
                dispatchIndex == frames.Count ||
                !IsSystemPrivateCoreLibSpan(
                    context.TraceLog, frames, bridgeStart, dispatchIndex - bridgeStart + 1))
            {
                return bridgeStart;
            }
            for (int i = bridgeStart; i <= dispatchIndex; i++)
            {
                if (frames[i].Origin != StitchedFrameOrigin.Sync ||
                    frames[i].Context != null)
                {
                    return bridgeStart;
                }
                AsyncStitchBoundaryKind boundary = context.ClassifyBoundary(frames[i].CodeAddress).Kind;
                if (boundary == AsyncStitchBoundaryKind.V1Dispatcher ||
                    boundary == AsyncStitchBoundaryKind.V2ContinuationWrapper)
                {
                    return bridgeStart;
                }
            }

            int count = dispatchIndex - bridgeStart;
            frames.RemoveRange(bridgeStart, count);
            context.Diagnostics.AsyncInfrastructureFramesCollapsed += count;
            context.Diagnostics.RuntimeResumptionBridgeFramesCollapsed += count;
            return frames.Count;
        }

        private static bool IsSyncFrameKind(StitchedFrame frame, StitchSyncFrameKind kind) =>
            frame.Origin == StitchedFrameOrigin.Sync &&
            frame.SyncFrameKind == kind;

        #region private

        private int m_maximumContextAncestryDepth = AsyncContextAncestryAugmenter.DefaultMaximumDepth;
        private readonly List<AsyncStackTransform> m_customTransforms;

        #endregion
    }
}
