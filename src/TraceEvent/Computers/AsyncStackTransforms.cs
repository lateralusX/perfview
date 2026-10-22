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
    /// Structural stitching consumes the marker when its target is known; synchronous fallback layouts
    /// retain it when conservative presentation is disabled.
    /// </para>
    /// </remarks>
    internal sealed class AsyncSampledV2WrapperTransform
    {
        public void Apply(AsyncStackTransformContext context)
        {
            List<StitchedFrame> frames = context.Frames;

            // Known targets normally consume their wrapper structurally. Normalize any retained marker
            // in a fallback or caller-assembled layout without changing its logical target.
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
    /// attributes verified fully completed V1 return spans to their logical root, and removes an exact
    /// completed-V2-dispatch tail beneath a physically matched V1 activation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Edits:</b> a classified generated MoveNext is presented as its logical async method, and adjacent
    /// known builder Start frames are removed. If a same-module kickoff name matches, the startup MoveNext and
    /// Start frames are removed instead, retaining the kickoff. Reentrant deduplication retains the resumed
    /// current/box representation or verified nested dispatch target and removes contiguous await-registration
    /// helpers, the identical startup MoveNext, builder Start frames, and matching kickoff. Profiles supply
    /// the startup and registration spans; the common layouts are described here. Kickoff matching compares logical names with
    /// parameter lists stripped; it is not metadata/token-based or exact-signature resolution.
    /// A dispatch-only target requires its retained same-context V1 dispatcher, resolved target/startup method
    /// identity, and a physically current outer context exactly one depth shallower. The startup span must belong
    /// wholly to that outer context, with one matching body and no intervening boundary. Its captured state and
    /// dispatch role are retained; recursive or unverified layouts are not deduplicated.
    /// </para>
    /// <para>
    /// Under the common profile, the completed V2 tail is the leaf-to-root sequence: one classified Task-continuation or execution-context
    /// bridge, Task.RunContinuations, Task.TrySetResult, and two distinct V2 dispatch-continuation methods.
    /// The selected profile supplies the actual matched span, including any verified layout changes.
    /// All removed frames must resolve to the same captured CoreLib module and be outside live async contexts, with a resolved
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
    /// <see cref="AsyncRuntimeRuleProfile"/>, selected from captured CoreLib metadata before stitching.
    /// </para>
    /// <para>
    /// <b>Ordering and fallback:</b> runs after verified inter-activation handoff folding and before optional
    /// infrastructure cleanup, which could otherwise erase the helpers needed for exact matching.
    /// Its startup, reentrant, and completed-tail rules deliberately share one leaf-to-root traversal.
    /// Missing trace/symbol identity or an incomplete pattern prevents the identity-dependent folds;
    /// classified MoveNext normalization and adjacent builder-Start removal can still run without a TraceLog.
    /// Fully completed V1 return spans additionally require completion events, unique consecutive captured
    /// method identities in physical return order, resolved same-module runtime plumbing, a box carrier, and
    /// builder-completion evidence. Unverified leaf callees remain outside the fold; an interrupted span is retained.
    /// </para>
    /// </remarks>
    internal sealed class AsyncV1PresentationTransform
    {
        public void Apply(AsyncStackTransformContext context)
        {
            List<StitchedFrame> frames = context.Frames;
            CollapseCompletedV1Unwinds(context);
            // Preserve the leaf-to-root interleaving of handoff, deduplication, and startup edits.
            for (int i = 0; i < frames.Count; i++)
            {
                StitchedFrame frame = frames[i];
                if (frame.Origin == StitchedFrameOrigin.AsyncCurrent ||
                    frame.Origin == StitchedFrameOrigin.AsyncDispatchTarget)
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

                AsyncRuntimeSequenceMatch startup = context.MatchRuntimeSequence(
                    AsyncRuntimeSequenceKind.V1Startup, i, frames.Count);
                if (!startup.IsMatch)
                {
                    continue;
                }
                int end = i + startup.Length;
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

        private static void CollapseCompletedV1Unwinds(AsyncStackTransformContext context)
        {
            if (context.TraceLog == null)
            {
                return;
            }

            List<StitchedFrame> frames = context.Frames;
            for (int start = 0; start < frames.Count;)
            {
                AsyncCallStack activation = frames[start].Context;
                int end = start + 1;
                while (end < frames.Count && ReferenceEquals(frames[end].Context, activation))
                {
                    end++;
                }
                if (activation != null &&
                    activation.Frames.Kind == AsyncCallstackKind.StateMachineAsync &&
                    context.TraceLog.AsyncMethodCompletionObserved(activation) &&
                    activation.GetMethodCompletedFrameCount(context.TimestampQpc) > 0 &&
                    activation.GetCompletedFrameCount(context.TimestampQpc) == activation.Frames.FrameCount &&
                    TryMatchCompletedV1Unwind(context, start, end, activation, out int unwindStart))
                {
                    bool incompleteRoot = end == frames.Count && context.Segments.Count != 0 &&
                        ReferenceEquals(context.Segments[0], activation);
                    int removed = end - unwindStart;
                    frames.RemoveRange(unwindStart, removed);
                    frames.Insert(unwindStart, StitchedFrame.CreateAsyncCompletionUnwind(activation));
                    context.Diagnostics.V1CompletionUnwindFramesCollapsed += removed;
                    end = unwindStart + 1;
                    if (incompleteRoot)
                    {
                        frames.Insert(end++, StitchedFrame.CreateIncompleteRoot());
                        context.Diagnostics.IncompleteAsyncRootsNormalized++;
                    }
                }
                start = end;
            }
        }

        private static bool TryMatchCompletedV1Unwind(
            AsyncStackTransformContext context, int start, int end, AsyncCallStack activation, out int unwindStart)
        {
            unwindStart = start;
            TraceLog log = context.TraceLog;
            TraceMethods methods = log.CodeAddresses.Methods;
            AsyncCallStackFrames captured = activation.Frames;
            var capturedMethods = new MethodIndex[captured.FrameCount];
            for (int i = 0; i < capturedMethods.Length; i++)
            {
                CodeAddressIndex address = captured.CodeAddressAt(i);
                MethodIndex method = address == CodeAddressIndex.Invalid
                    ? MethodIndex.Invalid : log.CodeAddresses.MethodIndex(address);
                if (method == MethodIndex.Invalid ||
                    (i == capturedMethods.Length - 1 &&
                     !AsyncStitchBoundary.TryGetLogicalStateMachineMethodName(
                         AsyncStitchBoundary.NormalizeMethodName(log.CodeAddresses[address].FullMethodName), out _)))
                {
                    return false;
                }
                for (int j = 0; j < i; j++)
                {
                    if (SameMethodIdentity(methods, capturedMethods[j], method))
                    {
                        return false;
                    }
                }
                capturedMethods[i] = method;
            }

            int previous = -1;
            int identities = 0;
            bool bodyAtPrevious = false;
            bool hasBody = false;
            bool hasBox = false;
            bool hasCompletion = false;
            ModuleFileIndex runtimeModule = ModuleFileIndex.Invalid;
            AsyncRuntimeRuleProfile runtimeProfile = null;
            for (int i = start; i < end; i++)
            {
                StitchedFrame frame = context.Frames[i];
                if (frame.Origin != StitchedFrameOrigin.Sync)
                {
                    return false;
                }
                bool body = frame.SyncFrameKind == StitchSyncFrameKind.V1StateMachineMoveNext;
                bool box = frame.BoxedStateMachineMethod != MethodIndex.Invalid;
                MethodIndex method = body ? frame.Method : (box ? frame.BoxedStateMachineMethod : MethodIndex.Invalid);
                int capturedIndex = -1;
                for (int j = 0; j < capturedMethods.Length && method != MethodIndex.Invalid; j++)
                {
                    if (SameMethodIdentity(methods, capturedMethods[j], method))
                    {
                        capturedIndex = j;
                        break;
                    }
                }

                AsyncRuntimeRuleProfile profile = context.GetRuntimeProfile(frame.CodeAddress);
                bool infrastructure = profile.IsV1CompletionUnwindInfrastructure(
                    frame, context.ClassifyBoundary(frame.CodeAddress).Kind);
                if ((body && capturedIndex < 0) || (!body && (!infrastructure || (box && capturedIndex < 0))) ||
                    frame.CodeAddress == CodeAddressIndex.Invalid || frame.Method == MethodIndex.Invalid)
                {
                    if (identities != 0)
                    {
                        return false;
                    }
                    // Callees outside the proven return spine retain their physical identity and cost.
                    unwindStart = i + 1;
                    runtimeModule = ModuleFileIndex.Invalid;
                    runtimeProfile = null;
                    hasCompletion = false;
                    continue;
                }

                if (!body)
                {
                    TraceModuleFile module = log.CodeAddresses.ModuleFile(frame.CodeAddress);
                    if (!AsyncRuntimeRuleProfileCache.IsHostModule(module))
                    {
                        return false;
                    }
                    ModuleFileIndex moduleIndex = (module.ManagedModule ?? module).ModuleFileIndex;
                    if (runtimeModule != ModuleFileIndex.Invalid &&
                        (runtimeModule != moduleIndex || !ReferenceEquals(runtimeProfile, profile)))
                    {
                        return false;
                    }
                    runtimeModule = moduleIndex;
                    runtimeProfile = profile;
                    hasCompletion |= frame.SyncFrameKind == StitchSyncFrameKind.V1MethodBuilderCompletion;
                    hasBox |= box;
                }
                if (capturedIndex < 0)
                {
                    continue;
                }
                if (previous < 0 || capturedIndex == previous - 1)
                {
                    identities++;
                    previous = capturedIndex;
                    bodyAtPrevious = false;
                }
                else if (capturedIndex != previous)
                {
                    return false;
                }
                if (body)
                {
                    if (bodyAtPrevious)
                    {
                        return false;
                    }
                    bodyAtPrevious = true;
                    hasBody = true;
                }
            }
            return identities >= 2 && hasBody && hasBox && hasCompletion;
        }

        private static bool SameMethodIdentity(TraceMethods methods, MethodIndex left, MethodIndex right)
        {
            if (left == right)
            {
                return left != MethodIndex.Invalid;
            }
            if (left == MethodIndex.Invalid || right == MethodIndex.Invalid)
            {
                return false;
            }
            TraceMethod first = methods[left];
            TraceMethod second = methods[right];
            return first.MethodToken != 0 && first.MethodToken == second.MethodToken &&
                first.MethodModuleFileIndex == second.MethodModuleFileIndex &&
                string.Equals(first.FullMethodName, second.FullMethodName, StringComparison.Ordinal);
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

            AsyncRuntimeSequenceMatch handoff = context.MatchRuntimeSequence(
                AsyncRuntimeSequenceKind.V2CompletionHandoff, handoffStart, frames.Count);
            int handoffCount = handoff.Length;
            if (!handoff.IsMatch || handoffCount >= frames.Count - handoffStart)
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
            if (!unassignedSyncTail)
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
                current.Segment.Kind != AsyncCallstackKind.StateMachineAsync)
            {
                return false;
            }
            bool dispatchOnly = current.Origin == StitchedFrameOrigin.AsyncDispatchTarget &&
                !current.IsStateMachineBoxCarrier;
            MethodIndex currentMethod = current.IsStateMachineBoxCarrier
                ? current.BoxedStateMachineMethod : current.Method;
            if (dispatchOnly)
            {
                CodeAddressIndex targetAddress = current.Segment.CodeAddressAt(current.SegmentFrameIndex);
                currentMethod = targetAddress == CodeAddressIndex.Invalid
                    ? MethodIndex.Invalid : context.TraceLog.CodeAddresses.MethodIndex(targetAddress);
            }
            if (currentMethod == MethodIndex.Invalid)
            {
                return false;
            }

            int registrationStart = currentIndex + 1;
            AsyncRuntimeSequenceMatch registration = context.MatchRuntimeSequence(
                AsyncRuntimeSequenceKind.V1AwaitRegistration, registrationStart, frames.Count);
            int startupMoveNextIndex = registrationStart + registration.Length;
            if (!registration.IsMatch || startupMoveNextIndex >= frames.Count)
            {
                return false;
            }

            StitchedFrame startupMoveNext = frames[startupMoveNextIndex];
            if (startupMoveNext.Origin != StitchedFrameOrigin.Sync ||
                startupMoveNext.SyncFrameKind != StitchSyncFrameKind.V1StateMachineMoveNext ||
                startupMoveNext.CodeAddress == CodeAddressIndex.Invalid ||
                (dispatchOnly
                    ? context.TraceLog.CodeAddresses.MethodIndex(startupMoveNext.CodeAddress)
                    : startupMoveNext.Method) != currentMethod)
            {
                return false;
            }

            AsyncRuntimeSequenceMatch startup = context.MatchRuntimeSequence(
                AsyncRuntimeSequenceKind.V1Startup, startupMoveNextIndex, frames.Count);
            int kickoffIndex = startupMoveNextIndex + startup.Length;
            if (!startup.IsMatch || startup.Length <= 1 ||
                kickoffIndex >= frames.Count ||
                !IsMatchingV1Kickoff(context.TraceLog, startupMoveNext, frames[kickoffIndex]))
            {
                return false;
            }
            if (dispatchOnly &&
                !IsVerifiedReentrantDispatchTarget(context, currentIndex, kickoffIndex, startupMoveNext.Method))
            {
                return false;
            }

            // Keep the resumed representation and its current/dispatch-target role. The exact match proves that
            // the registration helpers, duplicate startup MoveNext, builder Start frames, and kickoff all belong
            // to the same reentrant activation, so collapse that complete generated span.
            int duplicateFrameCount = kickoffIndex - registrationStart + 1;
            frames.RemoveRange(registrationStart, duplicateFrameCount);
            context.Diagnostics.V1ReentrantFramesCollapsed += duplicateFrameCount;
            return true;
        }

        private static bool IsVerifiedReentrantDispatchTarget(
            AsyncStackTransformContext context, int targetIndex, int kickoffIndex, MethodIndex startupMethod)
        {
            if (targetIndex == 0 || kickoffIndex + 1 >= context.Frames.Count ||
                context.Diagnostics.SegmentsProcessed != context.Segments.Count ||
                context.Diagnostics.BoundariesNotFound != 0 ||
                context.Diagnostics.AdjacencyMismatches != 0 ||
                context.Diagnostics.V1CompletionAlignmentAmbiguous != 0)
            {
                return false;
            }

            List<StitchedFrame> frames = context.Frames;
            StitchedFrame target = frames[targetIndex];
            StitchedFrame dispatcher = frames[targetIndex - 1];
            StitchedFrame outer = frames[kickoffIndex + 1];
            if (target.Context == null ||
                dispatcher.Origin != StitchedFrameOrigin.Sync ||
                !ReferenceEquals(dispatcher.Context, target.Context) ||
                context.ClassifyBoundary(dispatcher.CodeAddress).Kind != AsyncStitchBoundaryKind.V1Dispatcher ||
                !IsSystemPrivateCoreLibSpan(context.TraceLog, frames, targetIndex - 1, 1) ||
                outer.Origin != StitchedFrameOrigin.AsyncCurrent ||
                outer.Context == null || outer.CodeAddress == CodeAddressIndex.Invalid ||
                outer.Method == MethodIndex.Invalid || outer.Method == startupMethod ||
                target.Context.Depth != outer.Context.Depth + 1)
            {
                return false;
            }

            int matchingBodies = 0;
            for (int i = targetIndex + 1; i <= kickoffIndex; i++)
            {
                StitchedFrame frame = frames[i];
                if (frame.Origin != StitchedFrameOrigin.Sync ||
                    frame.Method == MethodIndex.Invalid ||
                    !ReferenceEquals(frame.Context, outer.Context) ||
                    context.ClassifyBoundary(frame.CodeAddress).IsBoundary)
                {
                    return false;
                }
                if (frame.Method == startupMethod)
                {
                    matchingBodies++;
                }
            }
            return matchingBodies == 1;
        }

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
                    AsyncStitchBoundary.NormalizeMethodName(moveNextCodeAddress.FullMethodName),
                    out string logicalMethod))
            {
                return false;
            }

            string kickoffMethod = AsyncStitchBoundary.NormalizeMethodName(kickoffCodeAddress.FullMethodName);
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
    /// active context and a profile-selected scheduler anchor (ThreadPoolWorkQueue.Dispatch in the common
    /// profile), retaining that anchor and historical ancestry.
    /// The complete candidate, including the anchor, must resolve to one captured CoreLib module, contain a profile-recognized
    /// bridge layout (containing adjacent Task.RunContinuations/TrySetResult under the common rules), and have no live-context ownership or V1-dispatcher/V2-wrapper
    /// boundary. V2 additionally requires a physically matched current method or a resolved structural dispatch target.
    /// Rejected candidates stay intact and protected, including their completion chains.
    /// </para>
    /// <para>
    /// Next, a resolved CoreLib AsyncProfiler.ResumeAsyncContext.Resume entry spine must establish ownership
    /// immediately above a physical current/completing method, a verified V1 box carrier, or a retained V1
    /// dispatcher and its target. Every leaf-side prefix frame must be synchronous and belong to that same active
    /// context, with no intervening async boundary. The entire owned prefix, including initialization, GC,
    /// unresolved callees, and callbacks, is attributed to the logical anchor; this is overhead attribution, not
    /// proof that an application's suspended body was executing. Rejected profiler prefixes remain protected too.
    /// CreateDispatcher and a resolved await-registration spine directly above a physical current V1 method
    /// also establish ownership of the dispatcher's callees, without requiring their individual names.
    /// Profiles recognize the entry spine; activation ownership and required dispatcher evidence remain shared.
    /// </para>
    /// <para>
    /// Within the remaining unprotected range, removes profile-matched completion chains before individual known builder,
    /// awaiter, dispatcher-creation, Task, and execution-context helpers. Residual contiguous dispatcher/box spans
    /// become <c>[Async dispatcher frames]</c> markers so their callees do not appear to belong to the scheduler.
    /// Spans belonging to different async contexts are not combined. Isolated
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
    /// Profile matchers can explicitly reject a candidate span; those spans also stay protected during individual cleanup.
    /// </para>
    /// </remarks>
    internal sealed class AsyncInfrastructureCleanupTransform
    {
        public void Apply(AsyncStackTransformContext context)
        {
            List<StitchedFrame> frames = context.Frames;
            int cleanupEnd = CollapseRuntimeResumptionBridge(context);
            int cleanupStart = CollapseAsyncProfilerOwnedPrefix(context, ref cleanupEnd);
            List<(int Start, int End)> rejectedSpans = null;

            // Remove the public SetResult trigger only as part of the exact inline-completion chain. Removing the
            // two implementation frames independently would otherwise leave SetResult as an orphan between the
            // resumed async method and its synchronous caller.
            for (int i = cleanupStart; i < cleanupEnd;)
            {
                if (frames[i].Origin != StitchedFrameOrigin.Sync)
                {
                    i++;
                    continue;
                }
                AsyncRuntimeSequenceMatch chain = context.MatchRuntimeSequence(
                    AsyncRuntimeSequenceKind.TaskCompletionChain, i, cleanupEnd);
                if (chain.IsMatch)
                {
                    frames.RemoveRange(i, chain.Length);
                    cleanupEnd -= chain.Length;
                    context.Diagnostics.AsyncInfrastructureFramesCollapsed += chain.Length;
                }
                else
                {
                    // A default pass may have rejected this tail. Recheck its layout before individual cleanup;
                    // do not turn a rejected version-specific candidate into a partially erased helper sequence.
                    AsyncRuntimeSequenceMatch handoff = context.MatchRuntimeSequence(
                        AsyncRuntimeSequenceKind.V2CompletionHandoff, i, cleanupEnd);
                    int protectedLength = Math.Max(
                        chain.IsRejected ? chain.Length : 0,
                        handoff.IsRejected ? handoff.Length : 0);
                    AsyncRuntimeSequenceMatch startup = context.MatchRuntimeSequence(
                        AsyncRuntimeSequenceKind.V1Startup, i, cleanupEnd);
                    AsyncRuntimeSequenceMatch registration = context.MatchRuntimeSequence(
                        AsyncRuntimeSequenceKind.V1AwaitRegistration, i, cleanupEnd);
                    protectedLength = Math.Max(protectedLength,
                        Math.Max(startup.IsRejected ? startup.Length : 0,
                            registration.IsRejected ? registration.Length : 0));
                    if (protectedLength != 0)
                    {
                        if (rejectedSpans == null)
                        {
                            rejectedSpans = new List<(int Start, int End)>();
                        }
                        rejectedSpans.Add((i, i + protectedLength));
                        i += protectedLength;
                    }
                    else
                    {
                        i++;
                    }
                }
            }

            int rejectedIndex = (rejectedSpans?.Count ?? 0) - 1;
            for (int i = cleanupEnd - 1; i >= cleanupStart; i--)
            {
                if (rejectedIndex >= 0 && i < rejectedSpans[rejectedIndex].End)
                {
                    i = rejectedSpans[rejectedIndex--].Start;
                    continue;
                }
                StitchedFrame frame = frames[i];
                if (frame.Origin != StitchedFrameOrigin.Sync ||
                    frame.Presentation == StitchedFramePresentation.AsyncDispatcherFrames)
                {
                    continue;
                }

                AsyncStitchBoundaryKind boundaryKind = context.ClassifyBoundary(frame.CodeAddress).Kind;
                if (context.GetRuntimeProfile(frame.CodeAddress).IsOptionalInfrastructureFrame(frame, boundaryKind))
                {
                    if (boundaryKind != AsyncStitchBoundaryKind.None)
                    {
                        frames[i] = frame.WithPresentation(StitchedFramePresentation.AsyncDispatcherFrames);
                        if (i + 1 < frames.Count &&
                            frames[i + 1].Presentation == StitchedFramePresentation.AsyncDispatcherFrames &&
                            ReferenceEquals(frame.Context, frames[i + 1].Context))
                        {
                            frames.RemoveAt(i + 1);
                        }
                    }
                    else
                    {
                        frames.RemoveAt(i);
                    }
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
                // A sampled CreateDispatcher already has individual-helper cleanup; its callees need atomic ownership.
                candidate |= i > 0 &&
                             frame.SyncFrameKind == StitchSyncFrameKind.V1DispatcherCreationInfrastructure;
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
            bool needsDispatcher = target.Origin == StitchedFrameOrigin.AsyncDispatchTarget &&
                !target.IsStateMachineBoxCarrier;
            if (needsDispatcher)
            {
                if (target.Segment.Kind != AsyncCallstackKind.StateMachineAsync)
                {
                    return targetIndex;
                }
            }
            else if (target.Origin != StitchedFrameOrigin.AsyncCurrent &&
                     target.Origin != StitchedFrameOrigin.AsyncCompleting &&
                     !target.IsStateMachineBoxCarrier)
            {
                return targetIndex;
            }
            int entryIndex = -1;
            for (int i = targetIndex - 1; i >= 0; i--)
            {
                AsyncRuntimeSequenceMatch entry = context.MatchRuntimeSequence(
                    AsyncRuntimeSequenceKind.AsyncProfilerEntry, i, targetIndex);
                if (entry.IsMatch && entry.Length == targetIndex - i)
                {
                    entryIndex = i;
                    break;
                }
            }
            if (entryIndex < 0)
            {
                return targetIndex;
            }

            // Only the entry spine needs symbols. Everything leafward is callee work owned by that invocation.
            // A physical current/completing anchor remains intact even when stitching already removed its dispatcher.
            int dispatcherCount = 0;
            for (int i = 0; i < targetIndex; i++)
            {
                StitchedFrame frame = frames[i];
                if (frame.Origin != StitchedFrameOrigin.Sync ||
                    frame.ContextKind != StitchedFrameContextKind.Active ||
                    !ReferenceEquals(frame.Context, target.Context))
                {
                    return targetIndex;
                }
                AsyncStitchBoundaryInfo boundary = context.ClassifyBoundary(frame.CodeAddress);
                if (boundary.IsBoundary)
                {
                    if (!needsDispatcher || i < entryIndex ||
                        boundary.Kind != AsyncStitchBoundaryKind.V1Dispatcher || ++dispatcherCount != 1)
                    {
                        return targetIndex;
                    }
                }
            }
            if (needsDispatcher && dispatcherCount != 1)
            {
                return targetIndex;
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
            if (context.Segments.Count == 0)
            {
                return frames.Count;
            }

            AsyncCallStack rootContext = context.Segments[0];
            int rootIndex = -1;
            for (int i = frames.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(frames[i].Context, rootContext) &&
                    frames[i].ContextKind == StitchedFrameContextKind.Active)
                {
                    rootIndex = i;
                    break;
                }
            }
            int bridgeStart = rootIndex + 1;
            while (bridgeStart < frames.Count &&
                   frames[bridgeStart].Origin == StitchedFrameOrigin.AsyncContextParent)
            {
                bridgeStart++;
            }

            int dispatchIndex = frames.Count;
            for (int i = bridgeStart; i < frames.Count; i++)
            {
                if (context.GetRuntimeProfile(frames[i].CodeAddress).IsRuntimeResumptionAnchor(frames[i]))
                {
                    dispatchIndex = i;
                    break;
                }
            }
            if (dispatchIndex == frames.Count && rootIndex >= 0)
            {
                for (int i = 0; i <= rootIndex; i++)
                {
                    if (context.GetRuntimeProfile(frames[i].CodeAddress).IsRuntimeResumptionAnchor(frames[i]))
                    {
                        dispatchIndex = i;
                        break;
                    }
                }
            }
            // An unplaced root can still expose a physical bridge candidate. Identify it only for protection.
            bool rootPlaced = rootIndex >= 0 && bridgeStart <= dispatchIndex;
            if (!rootPlaced)
            {
                bridgeStart = 0;
                for (int i = 0; i < dispatchIndex; i++)
                {
                    if (frames[i].Origin != StitchedFrameOrigin.Sync ||
                        context.ClassifyBoundary(frames[i].CodeAddress).IsBoundary)
                    {
                        bridgeStart = i + 1;
                    }
                }
            }
            int bridgeEnd = dispatchIndex < frames.Count ? dispatchIndex + 1 : frames.Count;
            AsyncRuntimeSequenceMatch bridge = context.MatchRuntimeSequence(
                AsyncRuntimeSequenceKind.RuntimeResumptionBridge, bridgeStart, bridgeEnd);
            if (bridge.Length == 0)
            {
                return frames.Count;
            }

            // Rejected bridges remain intact; individual-helper cleanup only visits their leaf-ward prefix.
            if (!bridge.IsMatch || !rootPlaced || context.TraceLog == null ||
                dispatchIndex == frames.Count || bridge.Length != bridgeEnd - bridgeStart ||
                context.Diagnostics.SegmentsProcessed != context.Segments.Count ||
                context.Diagnostics.BoundariesNotFound != 0 ||
                context.Diagnostics.AdjacencyMismatches != 0 ||
                (rootContext.Frames.Kind == AsyncCallstackKind.RuntimeAsync &&
                 !HasVerifiedAsyncAnchor(context, rootIndex, rootContext)))
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
