// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

using static Microsoft.Diagnostics.Tracing.Computers.AsyncStackTransformHelpers;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>
    /// Removes a sampled-leaf V2 continuation wrapper when structural stitching selected a V2 synchronous layout
    /// or placed a verified dispatch target immediately beneath that wrapper in the same activation.
    /// Only the first synchronous frame is removed; logical targets, ancestry, and dispatch plumbing are unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Contract-based inputs:</b> wrapper recognition uses the profiler's stable
    /// <c>Continuation_Wrapper_0</c> through <c>Continuation_Wrapper_31</c> boundary-name contract exposed by
    /// <see cref="AsyncStitchBoundary"/>. Target identity and progress have already been established by the
    /// stitcher; this pass neither decodes records nor infers that the target's body was physically executing.
    /// </para>
    /// <para>
    /// <b>Presentation policy:</b> hiding the sampled marker is a tool choice, not a runtime guarantee.
    /// No Task, awaiter, builder, or dispatcher-helper layout is required, and no direct TraceLog lookup is needed.
    /// An unclassified wrapper or a missing structural layout/target condition leaves the frame unchanged.
    /// </para>
    /// <para>
    /// <b>Ordering:</b> the first default conservative pass, before
    /// <see cref="AsyncReentrantHandoffTransform"/> and <see cref="AsyncV1PresentationTransform"/>.
    /// Structural stitching retains the marker so disabling conservative presentation can expose it.
    /// </para>
    /// </remarks>
    internal sealed class AsyncSampledV2WrapperTransform
    {
        public void Apply(AsyncStackTransformContext context)
        {
            List<StitchedFrame> frames = context.Frames;

            // Keep sampled wrapper provenance structurally, including when a logical dispatch target is known.
            // Removing that one transition frame is presentation-only and intentionally opt-out-able.
            if (frames.Count != 0 &&
                frames[0].Origin == StitchedFrameOrigin.Sync &&
                context.ClassifyBoundary(frames[0].CodeAddress).Kind ==
                    AsyncStitchBoundaryKind.V2ContinuationWrapper &&
                (context.Diagnostics.V2SyncLayoutUsed != 0 ||
                 (frames.Count > 1 &&
                  frames[1].Origin == StitchedFrameOrigin.AsyncDispatchTarget &&
                  frames[0].Context != null &&
                  ReferenceEquals(frames[0].Context, frames[1].Context))))
            {
                frames.RemoveAt(0);
                context.Diagnostics.V2LeafWrapperDropped++;
            }
        }
    }

    /// <summary>
    /// Normalizes synchronous V1 startup frames, deduplicates exact reentrant V1 activation scaffolding, and
    /// removes an exact completed-V2-dispatch tail beneath a physically matched V1 activation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Edits:</b> a classified generated MoveNext is presented as its logical async method, and adjacent
    /// known builder Start frames are removed. If a same-module kickoff name matches, the startup MoveNext and
    /// Start frames are removed instead, retaining the kickoff. Reentrant deduplication retains the resumed
    /// current/box representation and removes contiguous await-registration helpers, the identical startup
    /// MoveNext, builder Start frames, and matching kickoff. Kickoff matching compares logical names with
    /// parameter lists stripped; it is not metadata/token-based or exact-signature resolution.
    /// </para>
    /// <para>
    /// The completed V2 tail must be the leaf-to-root sequence: one classified Task-continuation or execution-context
    /// bridge, Task.RunContinuations, Task.TrySetResult, and two distinct V2 dispatch-continuation methods.
    /// The five removed frames must be resolved, CoreLib-owned, and outside live async contexts, with a resolved
    /// ordinary synchronous caller retained beneath them. Missing boundaries or adjacency mismatches block this fold.
    /// </para>
    /// <para>
    /// <b>Inputs and contracts:</b> current/box identity and remaining-frame provenance come from structural
    /// stitching. The V2 DispatchContinuations and InstrumentedDispatchContinuations names are profiler boundary
    /// contracts. <b>Implementation dependencies:</b> box-carrier recognition, generated
    /// <c>&lt;Method&gt;d__N.MoveNext</c> naming, CoreLib method-builder Start and AwaitUnsafeOnCompleted methods,
    /// known awaiter-registration methods,
    /// Task completion helpers, and ExecutionContext/AwaitTaskContinuation bridge shapes are compiler/runtime
    /// conventions, not async-profiler guarantees. Their recognition is centralized in
    /// <see cref="AsyncStitchBoundary.ClassifySynchronousFrame(string, bool)"/>.
    /// </para>
    /// <para>
    /// <b>Ordering and fallback:</b> runs after verified inter-activation handoff folding and before optional
    /// infrastructure cleanup, which could otherwise erase the helpers needed for exact matching.
    /// Its startup, reentrant, and completed-tail rules deliberately share one leaf-to-root traversal.
    /// Missing trace/symbol identity or an incomplete pattern prevents the identity-dependent folds;
    /// classified MoveNext normalization and adjacent builder-Start removal can still run without a TraceLog.
    /// </para>
    /// </remarks>
    internal sealed class AsyncV1PresentationTransform
    {
        public void Apply(AsyncStackTransformContext context)
        {
            List<StitchedFrame> frames = context.Frames;
            // Preserve the leaf-to-root interleaving of handoff, deduplication, and startup edits.
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

        #region private

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

        #endregion
    }

    /// <summary>
    /// Opt-in cleanup of recognized V1/V2 CoreLib machinery, with atomic resumption-bridge and profiler-prefix
    /// ownership checks protecting uncertain spans from later individual-helper removal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Edits and protection:</b> first attempts to fold the synchronous resumption bridge between the root
    /// active context and ThreadPoolWorkQueue.Dispatch, retaining that dispatch anchor and historical ancestry.
    /// The complete candidate, including the anchor, must resolve to CoreLib, contain an adjacent
    /// Task.RunContinuations/TrySetResult pair, and have no live-context ownership or V1-dispatcher/V2-wrapper
    /// boundary. V2 additionally requires a physically matched current method. Rejected candidates stay protected.
    /// An independently verified root-adjacent RunContinuations/TrySetResult/TaskCompletionSource.SetResult
    /// triple may still be removed without consuming an application callback or exposing the rest of that bridge.
    /// </para>
    /// <para>
    /// Next, a resolved CoreLib AsyncProfiler.ResumeAsyncContext.Resume entry spine must establish ownership
    /// immediately above a physical current/completing method, a verified V1 box carrier, or a retained V1
    /// dispatcher and its target. Every leaf-side prefix frame must be synchronous and belong to that same active
    /// context, with no intervening async boundary. The entire owned prefix, including initialization, GC,
    /// unresolved callees, and callbacks, is attributed to the logical anchor; this is overhead attribution, not
    /// proof that an application's suspended body was executing. Rejected profiler prefixes remain protected too.
    /// </para>
    /// <para>
    /// Within the remaining unprotected range, removes exact completion triples before individual known builder,
    /// awaiter, box, dispatcher, dispatcher-creation, Task, and execution-context helpers. Isolated
    /// TaskCompletionSource.SetResult frames and application/thread roots outside an owned prefix are retained.
    /// This is not a general CoreLib include/exclude filter.
    /// </para>
    /// <para>
    /// <b>Contract versus implementation:</b> structural activation/provenance and the V1 dispatcher, V2 wrapper,
    /// and V2 dispatch-continuation boundary names are contract-based evidence. CoreLib box families, method builders,
    /// awaiters, Task/TaskCompletionSource completion layout, ThreadPoolWorkQueue.Dispatch, ExecutionContext and
    /// AwaitTaskContinuation helpers, AsyncStateMachineDispatcherInfo.CreateDispatcher, and profiler
    /// capture/emission/Resume helper names and call order are implementation details, not profiler contracts.
    /// These dependencies explain why this pass is disabled by default even though it also recognizes contract frames.
    /// </para>
    /// <para>
    /// <b>Ordering and fallback:</b> runs after all enabled conservative passes, before custom callbacks, and
    /// can also run with conservative passes disabled. Bridge/prefix checks require a TraceLog and complete,
    /// consistent structural evidence; unrecognized layouts are not guessed. Classified helper cleanup can still
    /// run without a TraceLog. All cleanup rules must share the adjusted protected bounds in this component;
    /// separating their execution without carrying that protection would permit unsafe partial removal.
    /// </para>
    /// </remarks>
    internal sealed class AsyncInfrastructureCleanupTransform
    {
        public void Apply(AsyncStackTransformContext context)
        {
            List<StitchedFrame> frames = context.Frames;
            int cleanupEnd = CollapseRuntimeResumptionBridge(context);
            int cleanupStart = CollapseAsyncProfilerOwnedPrefix(context, ref cleanupEnd);

            // Remove the public SetResult trigger only as part of the exact inline-completion chain. Removing the
            // two implementation frames independently would otherwise leave SetResult as an orphan between the
            // resumed async method and its synchronous caller.
            for (int i = cleanupStart; i + 2 < cleanupEnd;)
            {
                if (IsInlineCompletionChain(frames, i))
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

        #region private

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
                // The root-adjacent completion chain has independent ownership evidence. Do not consume the
                // application callback or relax protection of the remaining resumption bridge.
                if (dispatchIndex < frames.Count &&
                    bridgeStart + 2 < dispatchIndex &&
                    context.Diagnostics.V1CompletionAlignmentAmbiguous == 0 &&
                    HasVerifiedAsyncAnchor(context, rootIndex, rootContext) &&
                    IsInlineCompletionChain(frames, bridgeStart) &&
                    IsSystemPrivateCoreLibSpan(context.TraceLog, frames, bridgeStart, 3) &&
                    frames[bridgeStart].Context == null &&
                    frames[bridgeStart + 1].Context == null &&
                    frames[bridgeStart + 2].Context == null)
                {
                    frames.RemoveRange(bridgeStart, 3);
                    context.Diagnostics.AsyncInfrastructureFramesCollapsed += 3;
                }
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

        private static bool IsInlineCompletionChain(List<StitchedFrame> frames, int start) =>
            IsSyncFrameKind(frames[start], StitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations) &&
            IsSyncFrameKind(frames[start + 1], StitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult) &&
            IsSyncFrameKind(frames[start + 2], StitchSyncFrameKind.SystemPrivateCoreLibTaskCompletionSourceSetResult);

        #endregion
    }

    /// <summary>
    /// Collapses a fully resolved CoreLib-only synchronous handoff span between independently verified adjacent
    /// active async contexts, for V1/V1, V1/V2, V2/V1, or V2/V2 nesting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Required evidence:</b> the outer anchor must be a physically matched AsyncCurrent with method/address
    /// identity. The bridge must be contiguous synchronous frames owned by that outer context, and the inner
    /// context must be exactly one depth deeper. Its anchor must be physically current or a resolved, verified
    /// dispatch target; any inner AsyncCompleting frame rejects the fold. A V1 target additionally needs a
    /// resolved CoreLib box carrier, or recorded completion progress matching its exact frame index plus an
    /// immediately preceding, same-context CoreLib V1 dispatcher. A V2 target relies on the stitcher's verified
    /// sampled-wrapper projection, not evidence of body execution.
    /// </para>
    /// <para>
    /// <b>Contract-based inputs:</b> activation nesting/progress comes from profiler records, while structural
    /// provenance and boundary recognition preserve their meaning in the stitched frames. The retained V1
    /// dispatcher and the V2 wrapper used upstream are profiler name contracts.
    /// <b>Implementation dependency:</b> treating the resolved CoreLib-only span between those anchors as
    /// removable handoff machinery is a presentation policy, not a profiler guarantee. V1 box-carrier recognition
    /// also depends on CoreLib implementation types. Unlike the optional completion-chain rules, this pass does
    /// not require particular Task, awaiter, or execution-context helper names inside the bridge.
    /// </para>
    /// <para>
    /// <b>Ordering and fallback:</b> runs after sampled-wrapper cleanup, before V1 presentation and optional
    /// helper removal, while typed contexts and required V1 dispatcher evidence are intact. Missing TraceLog,
    /// structural boundary/adjacency failures, or ambiguous V1 completion alignment disable folding.
    /// Unresolved or non-CoreLib bridge frames, including application callbacks, preserve that complete span.
    /// Only synchronous bridge frames are deleted; logical roles and captured states are not changed.
    /// </para>
    /// </remarks>
    internal sealed class AsyncReentrantHandoffTransform
    {
        public void Apply(AsyncStackTransformContext context)
        {
            if (context.TraceLog == null ||
                context.Diagnostics.BoundariesNotFound != 0 ||
                context.Diagnostics.AdjacencyMismatches != 0 ||
                context.Diagnostics.V1CompletionAlignmentAmbiguous != 0)
            {
                return;
            }

            List<StitchedFrame> frames = context.Frames;
            for (int currentIndex = 0; currentIndex < frames.Count; currentIndex++)
            {
                StitchedFrame outerCurrent = frames[currentIndex];
                if (outerCurrent.Origin != StitchedFrameOrigin.AsyncCurrent ||
                    outerCurrent.Context == null ||
                    outerCurrent.Method == MethodIndex.Invalid ||
                    outerCurrent.CodeAddress == CodeAddressIndex.Invalid)
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
                    innerContext.Depth != outerCurrent.Context.Depth + 1 ||
                    !HasVerifiedAsyncAnchor(context, handoffStart - 1, innerContext) ||
                    !IsSystemPrivateCoreLibSpan(context.TraceLog, frames, handoffStart, handoffCount))
                {
                    continue;
                }

                frames.RemoveRange(handoffStart, handoffCount);
                context.Diagnostics.AsyncReentrantHandoffFramesCollapsed += handoffCount;
                if (innerContext.Frames.Kind == AsyncCallstackKind.RuntimeAsync &&
                    outerCurrent.Context.Frames.Kind == AsyncCallstackKind.RuntimeAsync)
                {
                    context.Diagnostics.V2ReentrantHandoffFramesCollapsed += handoffCount;
                }
                currentIndex = handoffStart;
            }
        }
    }

    internal static class AsyncStackTransformHelpers
    {
        internal static bool HasVerifiedAsyncAnchor(
            AsyncStackTransformContext context,
            int contextEnd,
            AsyncCallStack activation)
        {
            List<StitchedFrame> frames = context.Frames;
            bool verified = false;
            for (int i = contextEnd; i >= 0 && ReferenceEquals(frames[i].Context, activation); i--)
            {
                StitchedFrame frame = frames[i];
                if (frame.Origin == StitchedFrameOrigin.AsyncCompleting)
                {
                    return false;
                }
                verified |= frame.Origin == StitchedFrameOrigin.AsyncCurrent &&
                    frame.Method != MethodIndex.Invalid && frame.CodeAddress != CodeAddressIndex.Invalid;
                if (frame.Origin != StitchedFrameOrigin.AsyncDispatchTarget)
                {
                    continue;
                }

                CodeAddressIndex targetAddress = frame.Segment.CodeAddressAt(frame.SegmentFrameIndex);
                if (targetAddress == CodeAddressIndex.Invalid ||
                    context.TraceLog.CodeAddresses.MethodIndex(targetAddress) == MethodIndex.Invalid)
                {
                    continue;
                }
                if (activation.Frames.Kind == AsyncCallstackKind.RuntimeAsync)
                {
                    verified = true;
                }
                else if (frame.IsStateMachineBoxCarrier)
                {
                    verified |= IsSystemPrivateCoreLibSpan(context.TraceLog, frames, i, 1);
                }
                else if (context.TraceLog.AsyncMethodCompletionObserved(activation) &&
                    activation.GetCompletedFrameCount(context.TimestampQpc) == frame.SegmentFrameIndex &&
                    i > 0 &&
                    frames[i - 1].Origin == StitchedFrameOrigin.Sync &&
                    ReferenceEquals(frames[i - 1].Context, activation) &&
                    context.ClassifyBoundary(frames[i - 1].CodeAddress).Kind == AsyncStitchBoundaryKind.V1Dispatcher)
                {
                    verified |= IsSystemPrivateCoreLibSpan(context.TraceLog, frames, i - 1, 1);
                }
            }

            return verified;
        }

        internal static bool IsSystemPrivateCoreLibSpan(
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

        internal static bool IsSyncFrameKind(StitchedFrame frame, StitchSyncFrameKind kind) =>
            frame.Origin == StitchedFrameOrigin.Sync &&
            frame.SyncFrameKind == kind;
    }
}
