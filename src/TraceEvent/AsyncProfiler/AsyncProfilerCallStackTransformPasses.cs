// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

using static Microsoft.Diagnostics.Tracing.Computers.AsyncProfilerCallStackTransformHelpers;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>
    /// Removes a sampled V2 continuation wrapper when stitching kept the physical stack
    /// or the next frame is a dispatch target from the same async call stack.
    /// Other frames are left unchanged.
    /// </summary>
    internal sealed class AsyncProfilerSampledV2WrapperTransformPass
    {
        public void Apply(AsyncProfilerCallStackTransformState context)
        {
            List<AsyncProfilerStitchedFrame> frames = context.Frames;
            if (frames.Count == 0)
            {
                return;
            }

            bool isSampledV2Wrapper =
                frames[0].Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                context.ClassifyBoundary(frames[0].CodeAddress).Kind == AsyncProfilerCallStackStitchBoundaryKind.V2Dispatcher;

            bool hasMatchingDispatchTarget =
                frames.Count > 1 && frames[1].Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget &&
                frames[0].Context != null && ReferenceEquals(frames[0].Context, frames[1].Context);

            if (isSampledV2Wrapper && (context.StitchingState.UsesV2SynchronousLayout || hasMatchingDispatchTarget))
            {
                frames.RemoveAt(0);
                context.Diagnostics.V2LeafWrapperDropped++;
            }
        }
    }

    /// <summary>
    /// Removes verified runtime helpers between an enclosing async method and its nested call stack.
    /// </summary>
    /// <remarks>
    /// Supports V1 and V2. The enclosing method must be physically running; the nested
    /// call stack must have a verified running method or dispatch target.
    /// Only removes physical frames from the enclosing call stack that resolve to one
    /// runtime. Logical frames and captured states remain unchanged.
    /// </remarks>
    internal sealed class AsyncProfilerReentrantHandoffTransformPass
    {
        /// <summary>Removes verified handoff spans while preserving the async frames on both sides.</summary>
        public void Apply(AsyncProfilerCallStackTransformState context)
        {
            bool canVerifyHandoff =
                context.TraceLog != null &&
                context.StitchingState.HasConsistentPhysicalAlignment &&
                context.StitchingState.UnresolvedV1CompletionCount == 0;
            if (!canVerifyHandoff)
            {
                return;
            }

            List<AsyncProfilerStitchedFrame> frames = context.Frames;
            for (int currentIndex = 0; currentIndex < frames.Count; currentIndex++)
            {
                AsyncProfilerStitchedFrame enclosingCurrent = frames[currentIndex];
                bool hasResolvedEnclosingCurrent =
                    enclosingCurrent.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent &&
                    enclosingCurrent.Context != null &&
                    enclosingCurrent.Method != MethodIndex.Invalid &&
                    enclosingCurrent.CodeAddress != CodeAddressIndex.Invalid;
                if (!hasResolvedEnclosingCurrent)
                {
                    continue;
                }

                // Collect adjacent physical frames from the enclosing call stack.
                int handoffStart = currentIndex;
                while (handoffStart > 0 &&
                       frames[handoffStart - 1].Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                       ReferenceEquals(frames[handoffStart - 1].Context, enclosingCurrent.Context))
                {
                    handoffStart--;
                }

                int handoffFrameCount = currentIndex - handoffStart;
                bool hasHandoffWithPrecedingFrame = handoffFrameCount != 0 && handoffStart != 0;
                if (!hasHandoffWithPrecedingFrame)
                {
                    continue;
                }

                // Require a directly nested call stack with a verified method or dispatch target.
                AsyncProfilerCallStack nestedCallStack = frames[handoffStart - 1].Context;
                bool hasDirectNestedCallStack =
                    nestedCallStack != null &&
                    nestedCallStack.Depth == enclosingCurrent.Context.Depth + 1;
                bool hasVerifiedNestedAnchor =
                    hasDirectNestedCallStack &&
                    HasVerifiedAsyncAnchor(context, handoffStart - 1, nestedCallStack);
                bool canRemoveHandoff =
                    hasVerifiedNestedAnchor &&
                    IsResolvedRuntimeSpan(context, frames, handoffStart, handoffFrameCount);
                if (!canRemoveHandoff)
                {
                    continue;
                }

                frames.RemoveRange(handoffStart, handoffFrameCount);
                context.Diagnostics.AsyncReentrantHandoffFramesCollapsed += handoffFrameCount;
                bool isV2ToV2Handoff =
                    nestedCallStack.Frames.Kind == AsyncCallstackKind.RuntimeAsync &&
                    enclosingCurrent.Context.Frames.Kind == AsyncCallstackKind.RuntimeAsync;
                if (isV2ToV2Handoff)
                {
                    context.Diagnostics.V2ReentrantHandoffFramesCollapsed += handoffFrameCount;
                }

                // The enclosing method moved to handoffStart; resume with the following frame.
                currentIndex = handoffStart;
            }
        }
    }

    /// <summary>
    /// Presents V1 state-machine frames as logical async methods and removes redundant
    /// startup, completion, and dispatch frames. Keeps logical callers and any physical
    /// frames that cannot be safely matched.
    /// </summary>
    internal sealed class AsyncProfilerV1PresentationTransformPass
    {
        public void Apply(AsyncProfilerCallStackTransformState context)
        {
            List<AsyncProfilerStitchedFrame> frames = context.Frames;

            // Identify the finishing call when recursive V1 frames are ambiguous.
            ResolveRecursiveV1Completion(context);

            // Remove verified return helpers while preserving the logical methods.
            CollapseCompletedV1Unwinds(context);

            // Preserve the leaf-to-root interleaving of handoff, deduplication, and startup edits.
            for (int i = 0; i < frames.Count; i++)
            {
                AsyncProfilerStitchedFrame frame = frames[i];
                if (frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent ||
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget)
                {
                    if (frame.Segment != null && frame.Segment.Kind == AsyncCallstackKind.StateMachineAsync)
                    {
                        if (frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent)
                        {
                            // Remove V2 completion helpers that resumed the current V1 method.
                            CollapseV2CompletionHandoff(context, i);
                        }

                        // Remove duplicate startup frames already represented by this async frame.
                        TryApplyReentrantV1Deduplication(context, i);
                    }

                    continue;
                }

                if (frame.Origin != AsyncProfilerStitchedFrameOrigin.Sync || frame.SyncFrameKind != AsyncProfilerStitchSyncFrameKind.V1StateMachineBody)
                {
                    continue;
                }

                // Display the logical method and fold its startup helpers when their layout matches.
                AsyncProfilerRuntimeSequenceMatch startup = context.MatchRuntimeSequence(AsyncProfilerRuntimeSequenceKind.V1Startup, i, frames.Count);
                if (!startup.IsMatch)
                {
                    continue;
                }

                int end = i + startup.Length;
                int builderCount = end - i - 1;
                bool canFoldStartupIntoKickoff = builderCount != 0 && end < frames.Count && context.TraceLog != null && IsMatchingV1Kickoff(context.TraceLog, frame, frames[end]);

                if (canFoldStartupIntoKickoff)
                {
                    int generatedFrameCount = builderCount + 1;
                    frames.RemoveRange(i, generatedFrameCount);
                    context.Diagnostics.V1SynchronousStartupFramesCollapsed += generatedFrameCount;
                    continue;
                }

                frames[i] = frame.WithPresentation(AsyncProfilerStitchedFramePresentation.LogicalStateMachineMethod);
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

        /// <summary>
        /// Identifies which recursive V1 call is finishing and removes its return helpers.
        /// Leaves the remaining recursive calls unchanged.
        /// </summary>
        private static void ResolveRecursiveV1Completion(AsyncProfilerCallStackTransformState context)
        {
            bool hasSingleAmbiguousCallStack =
                context.TraceLog != null && context.Segments.Count == 1 &&
                context.StitchingState.ProcessedCallStackCount == 1 &&
                context.StitchingState.HasConsistentPhysicalAlignment &&
                context.StitchingState.UnresolvedV1CompletionCount == 1;
            if (!hasSingleAmbiguousCallStack)
            {
                return;
            }

            // With several completed recursive calls, we cannot tell which copy remains.
            AsyncProfilerCallStack callStack = context.Segments[0];
            bool hasSingleNormalCompletion =
                callStack.Frames.Kind == AsyncCallstackKind.StateMachineAsync &&
                callStack.Frames.FrameCount >= 2 &&
                context.TraceLog.AsyncProfilerCallStacks?.MethodCompletionObserved(callStack) == true &&
                callStack.GetMethodCompletedFrameCount(context.TimestampQpc) == 1 &&
                callStack.GetExceptionCompletedFrameCount(context.TimestampQpc) == 0;
            if (!hasSingleNormalCompletion)
            {
                return;
            }

            MethodIndex recursiveMethod = context.GetMethod(callStack.Frames.CodeAddressAt(0));
            bool hasMatchingRecursiveFrames =
                recursiveMethod != MethodIndex.Invalid &&
                context.GetMethod(callStack.Frames.CodeAddressAt(1)) == recursiveMethod;
            if (!hasMatchingRecursiveFrames)
            {
                return;
            }

            // Locate the next recursive call and the dispatcher immediately before it.
            List<AsyncProfilerStitchedFrame> frames = context.Frames;
            int target = frames.FindIndex(frame =>
                frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget &&
                frame.ContextKind == AsyncProfilerStitchedFrameContextKind.Active &&
                ReferenceEquals(frame.Context, callStack) && frame.SegmentFrameIndex == 1 &&
                !frame.IsStateMachineBoxCarrier);
            bool hasExpectedDispatcher =
                target >= 2 &&
                context.ClassifyBoundary(frames[target - 1].CodeAddress).Kind == AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher;
            if (!hasExpectedDispatcher)
            {
                return;
            }

            ModuleFileIndex owner = context.GetHostModuleOwner(frames[target - 1].CodeAddress);
            AsyncProfilerRuntimeRuleProfile profile = context.GetRuntimeProfile(frames[target - 1].CodeAddress);

            if (!IsOwnedPhysicalFrame(context, frames[target - 1], callStack, owner, profile))
            {
                return;
            }

            // Follow the return helpers back to the physical method that is finishing.
            int body = target - 2;
            bool hasMatchingReturnBox = false;
            for (; body >= 0 && frames[body].SyncFrameKind != AsyncProfilerStitchSyncFrameKind.V1StateMachineBody; body--)
            {
                AsyncProfilerStitchedFrame frame = frames[body];
                bool isOwnedReturnHelper =
                    IsOwnedPhysicalFrame(context, frame, callStack, owner, profile) &&
                    (profile.IsV1CompletionUnwindInfrastructure(frame, context.ClassifyBoundary(frame.CodeAddress).Kind) || frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1BoxCallback);
                bool isMatchingReturnHelper =
                    isOwnedReturnHelper &&
                    (frame.BoxedStateMachineMethod == MethodIndex.Invalid || frame.BoxedStateMachineMethod == recursiveMethod);
                if (!isMatchingReturnHelper)
                {
                    return;
                }

                hasMatchingReturnBox |= frame.BoxedStateMachineMethod == recursiveMethod;
            }

            if (!hasMatchingReturnBox || body < 1)
            {
                return;
            }

            bool isMatchingRecursiveBody =
                frames[body].Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                frames[body].ContextKind == AsyncProfilerStitchedFrameContextKind.Active &&
                ReferenceEquals(frames[body].Context, callStack) &&
                frames[body].CodeAddress != CodeAddressIndex.Invalid && frames[body].Method == recursiveMethod;
            bool hasOwnedCompletionHelper =
                isMatchingRecursiveBody &&
                IsOwnedPhysicalFrame(context, frames[body - 1], callStack, owner, profile) &&
                frames[body - 1].SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1MethodCompletion;
            if (!hasOwnedCompletionHelper)
            {
                return;
            }

            // Verify that the completion path leads back to an entry box for the same method.
            int entry = FindV1CompletionPairBeforeBody(context, 0, body);
            if (entry < 0)
            {
                return;
            }

            // Require every helper in the completion span to belong to this call stack and runtime.
            for (int i = entry; i < body - 1; i++)
            {
                if (!IsOwnedPhysicalFrame(context, frames[i], callStack, owner, profile))
                {
                    return;
                }
            }

            // Check adjacent awaiter and continuation helpers before the completion entry.
            while (entry > 0 &&
                (frames[entry - 1].SyncFrameKind == AsyncProfilerStitchSyncFrameKind.AwaiterHelper ||
                 frames[entry - 1].SyncFrameKind == AsyncProfilerStitchSyncFrameKind.ContinuationBridge))
            {
                if (!IsOwnedPhysicalFrame(context, frames[--entry], callStack, owner, profile))
                {
                    return;
                }
            }

            // Verify that the preceding entry boxes identify the same recursive method.
            int boxesEnd = entry;
            while (entry > 0 && frames[entry - 1].BoxedStateMachineMethod != MethodIndex.Invalid)
            {
                AsyncProfilerStitchedFrame box = frames[--entry];
                bool isMatchingEntryBox =
                    IsOwnedPhysicalFrame(context, box, callStack, owner, profile) &&
                    box.BoxedStateMachineMethod == recursiveMethod &&
                    box.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.ProfilerHelper &&
                    profile.IsProfilerDispatchInfrastructure(box.SyncClassification);
                if (!isMatchingEntryBox)
                {
                    return;
                }
            }

            if (entry == boxesEnd)
            {
                return;
            }

            // An earlier state-machine body or box would make the finishing call ambiguous again.
            for (int i = 0; i < entry; i++)
            {
                if (frames[i].SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1StateMachineBody ||
                    frames[i].BoxedStateMachineMethod != MethodIndex.Invalid)
                {
                    return;
                }
            }

            // Mark captured frame zero as completed, leaving the pending recursive calls in place.
            frames[body] = AsyncProfilerStitchedFrame.CreateV1CompletedBody(frames[body].CodeAddress, frames[body].Method, callStack, 0);
            int removed = target - body - 1;
            frames.RemoveRange(body + 1, removed);
            context.StitchingState.UnresolvedV1CompletionCount--;
            context.Diagnostics.V1CompletionAlignmentAmbiguous = context.StitchingState.UnresolvedV1CompletionCount;
            context.Diagnostics.V1CompletionTailUsed++;
            context.Diagnostics.V1InfrastructureFramesCollapsed += removed;
        }

        /// <summary>Checks that a resolved physical frame belongs to the call stack and captured runtime.</summary>
        private static bool IsOwnedPhysicalFrame(AsyncProfilerCallStackTransformState context,
            AsyncProfilerStitchedFrame frame, AsyncProfilerCallStack callStack,
            ModuleFileIndex owner, AsyncProfilerRuntimeRuleProfile profile)
        {
            bool belongsToCallStack =
                frame.Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                frame.ContextKind == AsyncProfilerStitchedFrameContextKind.Active &&
                ReferenceEquals(frame.Context, callStack);

            bool hasResolvedIdentity =
                belongsToCallStack &&
                frame.CodeAddress != CodeAddressIndex.Invalid && frame.Method != MethodIndex.Invalid;

            bool hasMatchingRuntimeOwner =
                hasResolvedIdentity &&
                owner != ModuleFileIndex.Invalid && context.GetHostModuleOwner(frame.CodeAddress) == owner;

            bool hasMatchingRuntimeProfile =
                hasMatchingRuntimeOwner &&
                ReferenceEquals(context.GetRuntimeProfile(frame.CodeAddress), profile);

            return hasMatchingRuntimeProfile;
        }

        /// <summary>
        /// Finds the task-completion pair before the V1 method body, or returns -1 if it cannot be verified.
        /// </summary>
        private static int FindV1CompletionPairBeforeBody(AsyncProfilerCallStackTransformState context, int start, int body)
        {
            for (int i = body - 2; i >= start; i--)
            {
                AsyncProfilerRuntimeSequenceMatch pair = context.MatchRuntimeSequence(AsyncProfilerRuntimeSequenceKind.TaskCompletion, i, body - 1);
                if (pair.IsRejected)
                {
                    return -1;
                }

                if (pair.IsMatch && i + pair.Length == body - 1)
                {
                    return context.MatchRuntimeSequence(AsyncProfilerRuntimeSequenceKind.TaskCompletionChain, i, body).IsRejected ? -1 : i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Replaces verified V1 completion-return frames without treating completed methods as executing bodies.
        /// </summary>
        private static void CollapseCompletedV1Unwinds(AsyncProfilerCallStackTransformState context)
        {
            if (context.TraceLog == null)
            {
                return;
            }

            List<AsyncProfilerStitchedFrame> frames = context.Frames;
            for (int start = 0; start < frames.Count;)
            {
                // Work on one contiguous call-stack context at a time; end is exclusive.
                AsyncProfilerCallStack callStack = frames[start].Context;
                int end = start + 1;
                while (end < frames.Count && ReferenceEquals(frames[end].Context, callStack))
                {
                    end++;
                }

                bool hasObservedV1MethodCompletions =
                    callStack != null && callStack.Frames.Kind == AsyncCallstackKind.StateMachineAsync &&
                    context.TraceLog.AsyncProfilerCallStacks?.MethodCompletionObserved(callStack) == true &&
                    callStack.GetMethodCompletedFrameCount(context.TimestampQpc) > 0;

                if (hasObservedV1MethodCompletions)
                {
                    int completed = callStack.GetCompletedFrameCount(context.TimestampQpc);
                    bool fullyCompleted = completed == callStack.Frames.FrameCount;
                    bool incompleteRoot = end == frames.Count && context.Segments.Count != 0 && ReferenceEquals(context.Segments[context.Segments.Count - 1], callStack);
                    int physicalEnd = start;
                    while (physicalEnd < end && frames[physicalEnd].Origin == AsyncProfilerStitchedFrameOrigin.Sync)
                    {
                        physicalEnd++;
                    }

                    // Match the physical prefix before replacing it. Pending logical callers must survive the edit.
                    bool partialReturn =
                        incompleteRoot && completed > 0 && completed < callStack.Frames.FrameCount &&
                        HasRemainingV1Callers(frames, physicalEnd, end, completed + 1, callStack.Frames.FrameCount);
                    int unwindStart = start;
                    int completingPos = -1;
                    bool hasMatchingDispatcherTail = fullyCompleted && physicalEnd == end && TryMatchFullyCompletedV1DispatcherTail(context, start, end, callStack, out unwindStart);
                    bool hasMatchingRecursiveUnwind = partialReturn && TryMatchRecursiveV1Unwind(context, start, physicalEnd, callStack, completed, preserveLeaf: true, out unwindStart);
                    bool canMatchCompletedUnwind = (fullyCompleted && physicalEnd == end) || partialReturn;
                    bool hasMatchingCompletionUnwind =
                        hasMatchingDispatcherTail || hasMatchingRecursiveUnwind ||
                        (canMatchCompletedUnwind && TryMatchCompletedV1Unwind(context, start, physicalEnd, callStack, completed, out unwindStart, out completingPos));

                    if (hasMatchingCompletionUnwind)
                    {
                        AsyncProfilerStitchedFrame completionFrame;
                        if (hasMatchingRecursiveUnwind)
                        {
                            completionFrame = AsyncProfilerStitchedFrame.CreateSynthetic("[Async completion unwind]", callStack);
                        }
                        else if (fullyCompleted)
                        {
                            completionFrame = AsyncProfilerStitchedFrame.CreateV1CompletionReturn(callStack, hasMatchingDispatcherTail);
                        }
                        else
                        {
                            completionFrame = AsyncProfilerStitchedFrame.CreateV1CompletedBody(frames[completingPos].CodeAddress, frames[completingPos].Method, callStack, completed - 1);
                        }

                        int matchedFrameCount = physicalEnd - unwindStart;
                        int retainedCallers = end - physicalEnd;
                        int insertionIndex = unwindStart;
                        bool retainMachinery = !hasMatchingRecursiveUnwind && unwindStart > start && frames[unwindStart].SyncFrameKind != AsyncProfilerStitchSyncFrameKind.V1StateMachineBody;
                        if (retainMachinery)
                        {
                            CollapseMachinerySpan(frames, unwindStart, matchedFrameCount);
                            insertionIndex++;
                        }
                        else
                        {
                            frames.RemoveRange(unwindStart, matchedFrameCount);
                        }

                        // Keep the exclusive context end in sync with removals and insertions.
                        frames.Insert(insertionIndex, completionFrame);
                        end = insertionIndex + 1 + retainedCallers;
                        if (!fullyCompleted)
                        {
                            // Without the dispatcher or a carrier, the next captured method is ancestry,
                            // not a verified dispatch target or a physically running body.
                            frames.Insert(insertionIndex + 1, AsyncProfilerStitchedFrame.CreateAsync(callStack, completed));
                            end++;
                        }

                        context.Diagnostics.V1CompletionUnwindFramesCollapsed += matchedFrameCount;
                        if (incompleteRoot)
                        {
                            frames.Insert(end++, AsyncProfilerStitchedFrame.CreateIncompletePhysicalRoot());
                            context.Diagnostics.IncompleteAsyncRootsNormalized++;
                        }
                    }
                }

                start = end;
            }
        }

        /// <summary>
        /// Checks that the span contains exactly the expected remaining V1 callers, in captured order.
        /// </summary>
        private static bool HasRemainingV1Callers(List<AsyncProfilerStitchedFrame> frames, int start, int end, int next, int frameCount)
        {
            for (int i = start; i < end; i++, next++)
            {
                if (frames[i].Origin != AsyncProfilerStitchedFrameOrigin.AsyncCaller ||
                    frames[i].SegmentFrameIndex != next)
                {
                    return false;
                }
            }

            return next == frameCount;
        }

        /// <summary>
        /// Finds a verified V1 completion-return span without changing the frames.
        /// For a partial return, also locates the finishing method body.
        /// </summary>
        private static bool TryMatchCompletedV1Unwind(AsyncProfilerCallStackTransformState context,
            int start, int end, AsyncProfilerCallStack callStack, int completed,
            out int unwindStart, out int completingPos)
        {
            unwindStart = start;
            completingPos = -1;
            // Handle repeated recursive methods before requiring unique captured methods.
            bool hasMatchingRecursiveUnwind =
                completed == callStack.Frames.FrameCount &&
                TryMatchRecursiveV1Unwind(context, start, end, callStack, completed, preserveLeaf: false, out unwindStart);
            if (hasMatchingRecursiveUnwind)
            {
                return true;
            }

            // A failed recursive match may have moved the start; restart the general match.
            unwindStart = start;
            TraceLog log = context.TraceLog;
            TraceMethods methods = log.CodeAddresses.Methods;
            AsyncProfilerCallStackFrames captured = callStack.Frames;
            var capturedMethods = new MethodIndex[captured.FrameCount];

            // Resolve captured methods and require a recognizable V1 state-machine root.
            for (int i = 0; i < capturedMethods.Length; i++)
            {
                CodeAddressIndex address = captured.CodeAddressAt(i);
                MethodIndex method = address == CodeAddressIndex.Invalid ? MethodIndex.Invalid : log.CodeAddresses.MethodIndex(address);
                bool hasResolvedMethod = method != MethodIndex.Invalid;
                bool isUnrecognizedRootMethod =
                    hasResolvedMethod && i == capturedMethods.Length - 1 &&
                    !AsyncProfilerMethodNames.TryGetLogicalStateMachineMethodName(AsyncProfilerMethodNames.NormalizeMethodName(log.CodeAddresses[address].FullMethodName), out _);
                if (!hasResolvedMethod || isUnrecognizedRootMethod)
                {
                    return false;
                }

                // Repeated methods cannot be assigned unique captured positions in this match.
                for (int j = 0; j < i; j++)
                {
                    if (SameMethodIdentity(methods, capturedMethods[j], method))
                    {
                        return false;
                    }
                }

                capturedMethods[i] = method;
            }

            int previousCapturedIndex = -1;
            int matchedMethodCount = 0;
            bool hasBodyForMatchedMethod = false;
            bool hasBody = false;
            bool hasBox = false;
            bool hasCompletion = false;
            ModuleFileIndex runtimeModule = ModuleFileIndex.Invalid;
            AsyncProfilerRuntimeRuleProfile runtimeProfile = null;

            // Match physical bodies and box-carried methods to the captured completed frames.
            for (int i = start; i < end; i++)
            {
                AsyncProfilerStitchedFrame frame = context.Frames[i];
                if (frame.Origin != AsyncProfilerStitchedFrameOrigin.Sync)
                {
                    return false;
                }

                bool isStateMachineBody = frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1StateMachineBody;
                bool isStateMachineBox = frame.BoxedStateMachineMethod != MethodIndex.Invalid;
                MethodIndex method = isStateMachineBody ? frame.Method : (isStateMachineBox ? frame.BoxedStateMachineMethod : MethodIndex.Invalid);
                int capturedIndex = -1;
                for (int j = 0; j < capturedMethods.Length && method != MethodIndex.Invalid; j++)
                {
                    if (SameMethodIdentity(methods, capturedMethods[j], method))
                    {
                        capturedIndex = j;
                        break;
                    }
                }

                // A method that has not completed cannot belong to the return span.
                if (capturedIndex >= completed)
                {
                    return false;
                }

                AsyncProfilerRuntimeRuleProfile profile = context.GetRuntimeProfile(frame.CodeAddress);
                bool isCompletionHelper = profile.IsV1CompletionUnwindInfrastructure(frame, context.ClassifyBoundary(frame.CodeAddress).Kind);
                bool hasUnmatchedStateMachineMethod = (isStateMachineBody || isStateMachineBox) && capturedIndex < 0;
                bool isUnexpectedHelper = !isStateMachineBody && !isCompletionHelper;
                bool hasUnresolvedIdentity = frame.CodeAddress == CodeAddressIndex.Invalid || frame.Method == MethodIndex.Invalid;
                if (hasUnmatchedStateMachineMethod || isUnexpectedHelper || hasUnresolvedIdentity)
                {
                    // Unrelated callees may precede the span, but cannot interrupt a match.
                    if (matchedMethodCount != 0)
                    {
                        return false;
                    }

                    // Callees outside the verified completion-return sequence retain their physical identity and cost.
                    unwindStart = i + 1;
                    runtimeModule = ModuleFileIndex.Invalid;
                    runtimeProfile = null;
                    hasCompletion = false;
                    continue;
                }

                // All return helpers must belong to the same runtime module and profile.
                if (!isStateMachineBody)
                {
                    ModuleFileIndex moduleIndex = context.GetHostModuleOwner(frame.CodeAddress);
                    if (moduleIndex == ModuleFileIndex.Invalid)
                    {
                        return false;
                    }

                    bool hasConflictingRuntime = runtimeModule != ModuleFileIndex.Invalid && (runtimeModule != moduleIndex || !ReferenceEquals(runtimeProfile, profile));
                    if (hasConflictingRuntime)
                    {
                        return false;
                    }

                    runtimeModule = moduleIndex;
                    runtimeProfile = profile;
                    hasCompletion |= frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1MethodCompletion;
                    hasBox |= isStateMachineBox;
                }

                if (capturedIndex < 0)
                {
                    continue;
                }

                // Visit methods in reverse captured order, allowing helpers for the same method.
                bool startsNewMethodMatch = previousCapturedIndex < 0 || capturedIndex == previousCapturedIndex - 1;
                if (startsNewMethodMatch)
                {
                    matchedMethodCount++;
                    previousCapturedIndex = capturedIndex;
                    hasBodyForMatchedMethod = false;
                }
                else if (capturedIndex != previousCapturedIndex)
                {
                    return false;
                }

                // A method may have several helpers, but only one physical body in the span.
                if (isStateMachineBody)
                {
                    if (hasBodyForMatchedMethod)
                    {
                        return false;
                    }

                    hasBodyForMatchedMethod = true;
                    hasBody = true;
                    if (capturedIndex == completed - 1)
                    {
                        completingPos = i;
                    }
                }
            }

            bool hasRequiredCompletionEvidence = matchedMethodCount >= 2 && hasBody && hasBox && hasCompletion;
            bool hasRequiredFinishingBody = completed == captured.FrameCount || completingPos >= 0;
            return hasRequiredCompletionEvidence && hasRequiredFinishingBody;
        }

        /// <summary>
        /// Finds a verified V1 dispatcher tail after all captured methods have completed normally.
        /// Unrelated physical callees before the tail are excluded from the match.
        /// </summary>
        private static bool TryMatchFullyCompletedV1DispatcherTail(AsyncProfilerCallStackTransformState context,
            int start, int end, AsyncProfilerCallStack callStack, out int unwindStart)
        {
            unwindStart = end;
            AsyncProfilerCallStackFrames captured = callStack.Frames;

            // Require exact normal completion and unambiguous stitching before matching the tail.
            bool hasFramesToMatch = start != end && captured.FrameCount != 0;
            if (!hasFramesToMatch)
            {
                return false;
            }

            bool hasFullNormalCompletion =
                callStack.GetMethodCompletedFrameCount(context.TimestampQpc) == captured.FrameCount &&
                callStack.GetExceptionCompletedFrameCount(context.TimestampQpc) == 0;
            if (!hasFullNormalCompletion)
            {
                return false;
            }

            bool hasUnambiguousStitching =
                context.StitchingState.ProcessedCallStackCount == context.Segments.Count &&
                context.StitchingState.HasConsistentPhysicalAlignment &&
                context.StitchingState.UnresolvedV1CompletionCount == 0;
            if (!hasUnambiguousStitching)
            {
                return false;
            }

            // Verify the captured methods and the dispatcher entry before inspecting its box helpers.
            MethodIndex leafMethod = context.GetMethod(captured.CodeAddressAt(0));
            CodeAddressIndex rootAddress = captured.CodeAddressAt(captured.FrameCount - 1);
            AsyncProfilerStitchedFrame tail = context.Frames[end - 1];
            bool hasResolvedMethods = leafMethod != MethodIndex.Invalid && context.GetMethod(rootAddress) != MethodIndex.Invalid;
            if (!hasResolvedMethods)
            {
                return false;
            }

            string rootNormalizedMethodName = AsyncProfilerMethodNames.NormalizeMethodName(context.TraceLog.CodeAddresses[rootAddress].FullMethodName);
            bool hasRecognizedV1Root = AsyncProfilerMethodNames.TryGetLogicalStateMachineMethodName(rootNormalizedMethodName, out _);
            bool hasMatchingDispatchEntry = hasRecognizedV1Root && context.HasVerifiedDispatchEntry(callStack, tail.CodeAddress, requireAsyncAnchor: false);
            if (!hasMatchingDispatchEntry)
            {
                return false;
            }

            ModuleFileIndex owner = context.GetHostModuleOwner(tail.CodeAddress);
            AsyncProfilerRuntimeRuleProfile profile = context.GetRuntimeProfile(tail.CodeAddress);

            // Match adjacent box helpers from this call stack, runtime, and completed leaf method.
            while (unwindStart > start && context.Frames[unwindStart - 1].BoxedStateMachineMethod != MethodIndex.Invalid)
            {
                AsyncProfilerStitchedFrame frame = context.Frames[--unwindStart];
                bool belongsToCallStack =
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                    frame.ContextKind == AsyncProfilerStitchedFrameContextKind.Active && ReferenceEquals(frame.Context, callStack);
                bool isMatchingBoxHelper =
                    belongsToCallStack && frame.Method != MethodIndex.Invalid && frame.BoxedStateMachineMethod == leafMethod &&
                    frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.ProfilerHelper;
                bool hasMatchingRuntime =
                    isMatchingBoxHelper && owner != ModuleFileIndex.Invalid && context.GetHostModuleOwner(frame.CodeAddress) == owner &&
                    ReferenceEquals(context.GetRuntimeProfile(frame.CodeAddress), profile);
                bool isVerifiedReturnHelper =
                    hasMatchingRuntime && profile.IsV1CompletionUnwindInfrastructure(frame, context.ClassifyBoundary(frame.CodeAddress).Kind);
                if (!isVerifiedReturnHelper)
                {
                    return false;
                }
            }

            // At least one matching box helper must be present.
            if (unwindStart == end)
            {
                return false;
            }

            // Another async body, box, or dispatcher before the tail would make this match ambiguous.
            for (int i = start; i < unwindStart; i++)
            {
                AsyncProfilerStitchedFrame frame = context.Frames[i];
                AsyncProfilerCallStackStitchBoundaryInfo boundary = context.ClassifyBoundary(frame.CodeAddress);
                bool hasStateMachineFrame =
                    frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1StateMachineBody ||
                    frame.BoxedStateMachineMethod != MethodIndex.Invalid;
                bool hasDispatchBoundary =
                    boundary.IsDispatchMarker ||
                    boundary.Kind == AsyncProfilerCallStackStitchBoundaryKind.V1DispatcherInfrastructure;
                if (hasStateMachineFrame || hasDispatchBoundary)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Matches repeated V1 method bodies and their completion helpers as a group.
        /// Optionally preserves the leaf body and its callees without assigning individual captured slots.
        /// </summary>
        private static bool TryMatchRecursiveV1Unwind(AsyncProfilerCallStackTransformState context, int start, int end,
            AsyncProfilerCallStack callStack, int completed, bool preserveLeaf, out int unwindStart)
        {
            unwindStart = start;
            AsyncProfilerCallStackFrames captured = callStack.Frames;

            // Require at least two recorded normal completions within the captured call stack.
            bool hasEnoughCompletedFrames = captured.FrameCount >= 2 && completed >= 2 && completed <= captured.FrameCount;
            if (!hasEnoughCompletedFrames)
            {
                return false;
            }

            bool hasExactNormalCompletion =
                callStack.GetMethodCompletedFrameCount(context.TimestampQpc) == completed &&
                callStack.GetExceptionCompletedFrameCount(context.TimestampQpc) == 0;
            if (!hasExactNormalCompletion)
            {
                return false;
            }

            // Every captured frame must identify the same recursive method.
            MethodIndex recursiveMethod = context.GetMethod(captured.CodeAddressAt(0));
            for (int i = 0; i < captured.FrameCount; i++)
            {
                bool hasMatchingMethod = recursiveMethod != MethodIndex.Invalid && context.GetMethod(captured.CodeAddressAt(i)) == recursiveMethod;
                if (!hasMatchingMethod)
                {
                    return false;
                }
            }

            string normalizedMethodName = AsyncProfilerMethodNames.NormalizeMethodName(context.TraceLog.CodeAddresses[captured.CodeAddressAt(captured.FrameCount - 1)].FullMethodName);
            bool hasRecognizedV1Root = AsyncProfilerMethodNames.TryGetLogicalStateMachineMethodName(normalizedMethodName, out _);
            if (!hasRecognizedV1Root)
            {
                return false;
            }

            int matchedBodyCount = 0;
            bool hasEntryBox = false;
            ModuleFileIndex runtimeModule = ModuleFileIndex.Invalid;
            AsyncProfilerRuntimeRuleProfile runtimeProfile = null;

            // Follow one contiguous return span belonging to this call stack.
            for (int i = start; i < end; i++)
            {
                AsyncProfilerStitchedFrame frame = context.Frames[i];
                bool belongsToCallStack =
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                    frame.ContextKind == AsyncProfilerStitchedFrameContextKind.Active &&
                    ReferenceEquals(frame.Context, callStack);
                if (!belongsToCallStack)
                {
                    return false;
                }

                bool isStateMachineBody = frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1StateMachineBody;
                AsyncProfilerRuntimeRuleProfile profile = context.GetRuntimeProfile(frame.CodeAddress);
                bool isCompletionHelper = profile.IsV1CompletionUnwindInfrastructure(frame, context.ClassifyBoundary(frame.CodeAddress).Kind);
                bool isStateMachineBox = frame.BoxedStateMachineMethod != MethodIndex.Invalid;
                bool hasUnresolvedIdentity = frame.CodeAddress == CodeAddressIndex.Invalid || frame.Method == MethodIndex.Invalid;
                bool hasMismatchedStateMachineBody = isStateMachineBody && frame.Method != recursiveMethod;
                bool hasMismatchedStateMachineBox = !isStateMachineBody && isStateMachineBox && frame.BoxedStateMachineMethod != recursiveMethod;
                bool isUnexpectedHelper = !isStateMachineBody && !isCompletionHelper;
                if (hasUnresolvedIdentity || hasMismatchedStateMachineBody || isUnexpectedHelper || hasMismatchedStateMachineBox)
                {
                    // Preserve unrelated callees before the first body, but reject gaps after it.
                    if (matchedBodyCount != 0)
                    {
                        return false;
                    }
                    unwindStart = i + 1;
                    runtimeModule = ModuleFileIndex.Invalid;
                    runtimeProfile = null;
                    hasEntryBox = false;
                    continue;
                }

                if (isStateMachineBody)
                {
                    bool shouldPreserveLeafBody = preserveLeaf && matchedBodyCount == 0;
                    if (shouldPreserveLeafBody)
                    {
                        // Keep the leaf body and its callees; this copy need not have completed.
                        bool hasPrecedingCompletionHelper =
                            i > unwindStart &&
                            context.Frames[i - 1].SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1MethodCompletion;
                        if (!hasPrecedingCompletionHelper)
                        {
                            return false;
                        }

                        matchedBodyCount++;
                        unwindStart = i + 1;
                        hasEntryBox = false;
                        continue;
                    }

                    // Match completed copies as a group, without inferring their individual captured slots.
                    bool hasMatchingCompletionEntry =
                        hasEntryBox && i > unwindStart &&
                        context.Frames[i - 1].SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1MethodCompletion;
                    if (!hasMatchingCompletionEntry)
                    {
                        return false;
                    }

                    bool hasMatchingCompletionPair = FindV1CompletionPairBeforeBody(context, unwindStart, i) >= 0;
                    if (!hasMatchingCompletionPair)
                    {
                        return false;
                    }

                    // The preserved leaf counts as a physical body, not an additional completion.
                    matchedBodyCount++;
                    bool exceedsExpectedBodyCount = matchedBodyCount > completed + (preserveLeaf ? 1 : 0);
                    if (exceedsExpectedBodyCount)
                    {
                        return false;
                    }

                    hasEntryBox = false;
                    continue;
                }

                ModuleFileIndex owner = context.GetHostModuleOwner(frame.CodeAddress);
                bool hasResolvedRuntimeOwner = owner != ModuleFileIndex.Invalid;
                bool hasConflictingRuntime =
                    hasResolvedRuntimeOwner && runtimeModule != ModuleFileIndex.Invalid &&
                    (owner != runtimeModule || !ReferenceEquals(profile, runtimeProfile));
                if (!hasResolvedRuntimeOwner || hasConflictingRuntime)
                {
                    return false;
                }

                // Helpers for every matched copy must share the same runtime owner and profile.
                runtimeModule = owner;
                runtimeProfile = profile;
                hasEntryBox |= isStateMachineBox && frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.ProfilerHelper;
            }

            // Require two completed bodies, plus the preserved leaf when requested.
            int requiredBodyCount = preserveLeaf ? 3 : 2;
            return matchedBodyCount >= requiredBodyCount;
        }

        /// <summary>
        /// Matches valid method indices, including code versions with the same module, metadata token, and full name.
        /// </summary>
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

            bool hasMatchingMethodToken = first.MethodToken != 0 && first.MethodToken == second.MethodToken;
            bool hasMatchingModule = hasMatchingMethodToken && first.MethodModuleFileIndex == second.MethodModuleFileIndex;
            bool hasMatchingFullName = hasMatchingModule && string.Equals(first.FullMethodName, second.FullMethodName, StringComparison.Ordinal);

            return hasMatchingFullName;
        }

        /// <summary>
        /// Removes verified V2 completion helpers that resumed a V1 continuation inline.
        /// Preserves the V1 logical callers and the physical caller after the handoff.
        /// </summary>
        private static void CollapseV2CompletionHandoff(AsyncProfilerCallStackTransformState context, int currentIndex)
        {
            bool canVerifyHandoff = context.TraceLog != null && context.StitchingState.HasConsistentPhysicalAlignment;
            if (!canVerifyHandoff)
            {
                return;
            }

            List<AsyncProfilerStitchedFrame> frames = context.Frames;
            AsyncProfilerStitchedFrame current = frames[currentIndex];
            bool hasResolvedV1CurrentFrame =
                current.Context != null &&
                current.Context.Frames.Kind == AsyncCallstackKind.StateMachineAsync &&
                current.Method != MethodIndex.Invalid && current.CodeAddress != CodeAddressIndex.Invalid;
            if (!hasResolvedV1CurrentFrame)
            {
                return;
            }

            // Skip the V1 logical callers; the completion helpers must follow them.
            int handoffStart = currentIndex + 1;
            while (handoffStart < frames.Count &&
                   frames[handoffStart].Origin == AsyncProfilerStitchedFrameOrigin.AsyncCaller &&
                   ReferenceEquals(frames[handoffStart].Context, current.Context))
            {
                handoffStart++;
            }

            // Match the V2 completion helpers without including their caller.
            AsyncProfilerRuntimeSequenceMatch handoff = context.MatchRuntimeSequence(AsyncProfilerRuntimeSequenceKind.V2CompletionHandoff, handoffStart, frames.Count);
            int handoffCount = handoff.Length;
            bool hasHandoffWithFollowingCaller = handoff.IsMatch && handoffCount < frames.Count - handoffStart;
            if (!hasHandoffWithFollowingCaller)
            {
                return;
            }

            // The helpers and their caller must be physical and unassigned to any async call stack.
            bool isUnassignedPhysicalSpan = true;
            for (int i = handoffStart; i <= handoffStart + handoffCount; i++)
            {
                bool isUnassignedPhysicalFrame = frames[i].Origin == AsyncProfilerStitchedFrameOrigin.Sync && frames[i].Context == null;
                if (!isUnassignedPhysicalFrame)
                {
                    isUnassignedPhysicalSpan = false;
                    break;
                }
            }

            if (!isUnassignedPhysicalSpan)
            {
                return;
            }

            // Keep the following caller, and reject it if unresolved or another async boundary.
            AsyncProfilerStitchedFrame caller = frames[handoffStart + handoffCount];
            bool hasResolvedCaller = caller.Method != MethodIndex.Invalid && caller.CodeAddress != CodeAddressIndex.Invalid;
            bool hasNonBoundaryCaller =
                hasResolvedCaller &&
                context.ClassifyBoundary(caller.CodeAddress).Kind == AsyncProfilerCallStackStitchBoundaryKind.None;
            if (!hasNonBoundaryCaller)
            {
                return;
            }

            // The completed V2 dispatcher is physical only; never consume a bridge belonging to a live segment.
            frames.RemoveRange(handoffStart, handoffCount);
            context.Diagnostics.V2CompletionHandoffFramesCollapsed += handoffCount;
        }

        /// <summary>
        /// Removes duplicate V1 startup frames from a synchronously reentrant call.
        /// Keeps the resumed frame and its current or dispatch-target role.
        /// </summary>
        private static bool TryApplyReentrantV1Deduplication(AsyncProfilerCallStackTransformState context, int currentIndex)
        {
            if (context.TraceLog == null)
            {
                return false;
            }

            List<AsyncProfilerStitchedFrame> frames = context.Frames;
            AsyncProfilerStitchedFrame current = frames[currentIndex];
            bool hasCapturedV1Frames = current.Segment != null && current.Segment.Kind == AsyncCallstackKind.StateMachineAsync;
            if (!hasCapturedV1Frames)
            {
                return false;
            }

            // Targets without a box carrier need their method resolved from the captured frame.
            bool isDispatchTargetWithoutCarrier = current.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget && !current.IsStateMachineBoxCarrier;
            MethodIndex currentMethod = current.IsStateMachineBoxCarrier ? current.BoxedStateMachineMethod : current.Method;
            if (isDispatchTargetWithoutCarrier)
            {
                CodeAddressIndex targetAddress = current.Segment.CodeAddressAt(current.SegmentFrameIndex);
                currentMethod = context.GetMethod(targetAddress);
            }

            if (currentMethod == MethodIndex.Invalid)
            {
                return false;
            }

            // Find the await-registration helpers and the physical MoveNext immediately after them.
            int registrationStart = currentIndex + 1;
            AsyncProfilerRuntimeSequenceMatch registration = context.MatchRuntimeSequence(AsyncProfilerRuntimeSequenceKind.V1AwaitRegistration, registrationStart, frames.Count);
            int startupMoveNextIndex = registrationStart + registration.Length;
            bool hasRegistrationWithFollowingFrame = registration.IsMatch && startupMoveNextIndex < frames.Count;
            if (!hasRegistrationWithFollowingFrame)
            {
                return false;
            }

            AsyncProfilerStitchedFrame startupMoveNext = frames[startupMoveNextIndex];
            bool hasPhysicalV1StartupBody =
                startupMoveNext.Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                startupMoveNext.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1StateMachineBody &&
                startupMoveNext.CodeAddress != CodeAddressIndex.Invalid;
            if (!hasPhysicalV1StartupBody)
            {
                return false;
            }

            // Use the same resolver for captured and physical code versions when matching a target.
            MethodIndex startupMethodIdentity = isDispatchTargetWithoutCarrier ? context.GetMethod(startupMoveNext.CodeAddress) : startupMoveNext.Method;
            if (startupMethodIdentity != currentMethod)
            {
                return false;
            }

            // Match the startup helpers and the kickoff for that same V1 method.
            AsyncProfilerRuntimeSequenceMatch startup = context.MatchRuntimeSequence(AsyncProfilerRuntimeSequenceKind.V1Startup, startupMoveNextIndex, frames.Count);
            int kickoffIndex = startupMoveNextIndex + startup.Length;
            bool hasStartupWithKickoff = startup.IsMatch && startup.Length > 1 && kickoffIndex < frames.Count;
            bool hasMatchingKickoff = hasStartupWithKickoff && IsMatchingV1Kickoff(context.TraceLog, startupMoveNext, frames[kickoffIndex]);
            if (!hasMatchingKickoff)
            {
                return false;
            }

            // A target without a box carrier also needs a verified nested dispatch relationship.
            bool hasRequiredDispatchEvidence = !isDispatchTargetWithoutCarrier || IsVerifiedReentrantDispatchTarget(context, currentIndex, kickoffIndex, startupMoveNext.Method);
            if (!hasRequiredDispatchEvidence)
            {
                return false;
            }

            // Keep the resumed representation and its current/dispatch-target role. The exact match proves that
            // the registration helpers, duplicate startup MoveNext, builder Start frames, and kickoff all belong
            // to the same reentrant call stack, so collapse that complete generated span.
            int duplicateFrameCount = kickoffIndex - registrationStart + 1;
            frames.RemoveRange(registrationStart, duplicateFrameCount);
            context.Diagnostics.V1ReentrantFramesCollapsed += duplicateFrameCount;
            return true;
        }

        /// <summary>
        /// Verifies a nested V1 dispatch target against its dispatcher and enclosing running method.
        /// Requires exactly one matching startup method in the intervening physical frames.
        /// </summary>
        private static bool IsVerifiedReentrantDispatchTarget(AsyncProfilerCallStackTransformState context,
            int targetIndex, int kickoffIndex, MethodIndex startupMethod)
        {
            bool hasSurroundingFrames = targetIndex != 0 && kickoffIndex + 1 < context.Frames.Count;
            if (!hasSurroundingFrames)
            {
                return false;
            }

            bool hasUnambiguousStitching =
                context.StitchingState.ProcessedCallStackCount == context.Segments.Count &&
                context.StitchingState.HasConsistentPhysicalAlignment &&
                context.StitchingState.UnresolvedV1CompletionCount == 0;
            if (!hasUnambiguousStitching)
            {
                return false;
            }

            List<AsyncProfilerStitchedFrame> frames = context.Frames;
            AsyncProfilerStitchedFrame target = frames[targetIndex];
            AsyncProfilerStitchedFrame dispatcher = frames[targetIndex - 1];
            AsyncProfilerStitchedFrame enclosingCurrent = frames[kickoffIndex + 1];

            // Verify that the physical dispatcher belongs to the target's call stack.
            bool hasMatchingDispatcherContext =
                target.Context != null &&
                dispatcher.Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                ReferenceEquals(dispatcher.Context, target.Context);
            bool hasVerifiedDispatcher =
                hasMatchingDispatcherContext &&
                context.ClassifyBoundary(dispatcher.CodeAddress).Kind == AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher &&
                IsResolvedRuntimeSpan(context, frames, targetIndex - 1, 1);
            if (!hasVerifiedDispatcher)
            {
                return false;
            }

            // The startup path must belong to a different running method one nesting level above the target.
            bool hasDistinctEnclosingCurrent =
                enclosingCurrent.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent &&
                enclosingCurrent.Context != null && enclosingCurrent.CodeAddress != CodeAddressIndex.Invalid &&
                enclosingCurrent.Method != MethodIndex.Invalid && enclosingCurrent.Method != startupMethod;
            if (!hasDistinctEnclosingCurrent)
            {
                return false;
            }

            bool hasExpectedNesting = target.Context.Depth == enclosingCurrent.Context.Depth + 1;
            if (!hasExpectedNesting)
            {
                return false;
            }

            // Keep the full startup span in the enclosing call stack, without another dispatcher.
            int matchingStartupFrames = 0;
            for (int i = targetIndex + 1; i <= kickoffIndex; i++)
            {
                AsyncProfilerStitchedFrame frame = frames[i];
                bool belongsToEnclosingCallStack =
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                    frame.Method != MethodIndex.Invalid &&
                    ReferenceEquals(frame.Context, enclosingCurrent.Context);
                bool hasInterveningDispatcher =
                    belongsToEnclosingCallStack &&
                    context.ClassifyBoundary(frame.CodeAddress).IsDispatchMarker;

                if (!belongsToEnclosingCallStack || hasInterveningDispatcher)
                {
                    return false;
                }

                if (frame.Method == startupMethod)
                {
                    matchingStartupFrames++;
                }
            }

            // Repeated startup-method frames would make the duplicate body ambiguous.
            return matchingStartupFrames == 1;
        }

        /// <summary>
        /// Matches a physical V1 kickoff frame to its state-machine MoveNext frame.
        /// </summary>
        private static bool IsMatchingV1Kickoff(TraceLog traceLog,
            AsyncProfilerStitchedFrame stateMachineMoveNext, AsyncProfilerStitchedFrame kickoff)
        {
            bool canMatchKickoff =
                kickoff.Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                stateMachineMoveNext.CodeAddress != CodeAddressIndex.Invalid &&
                kickoff.CodeAddress != CodeAddressIndex.Invalid;
            if (!canMatchKickoff)
            {
                return false;
            }

            TraceCodeAddress moveNextCodeAddress = traceLog.CodeAddresses[stateMachineMoveNext.CodeAddress];
            TraceCodeAddress kickoffCodeAddress = traceLog.CodeAddresses[kickoff.CodeAddress];
            bool hasMatchingModuleName = string.Equals(moveNextCodeAddress.ModuleName, kickoffCodeAddress.ModuleName, StringComparison.OrdinalIgnoreCase);
            if (!hasMatchingModuleName)
            {
                return false;
            }

            // Match the logical method name rather than the generated MoveNext name.
            string normalizedMethodName = AsyncProfilerMethodNames.NormalizeMethodName(moveNextCodeAddress.FullMethodName);
            bool hasLogicalMethodName = AsyncProfilerMethodNames.TryGetLogicalStateMachineMethodName(normalizedMethodName, out string logicalMethod);
            if (!hasLogicalMethodName)
            {
                return false;
            }

            string kickoffMethod = AsyncProfilerMethodNames.NormalizeMethodName(kickoffCodeAddress.FullMethodName);
            if (string.IsNullOrEmpty(kickoffMethod))
            {
                return false;
            }

            // Compare names without parameters, using dots for nested types.
            kickoffMethod = AsyncProfilerMethodNames.GetMethodNameWithoutParameters(kickoffMethod);
            kickoffMethod = kickoffMethod.Replace('+', '.');

            bool hasMatchingMethodName = string.Equals(logicalMethod, kickoffMethod, StringComparison.Ordinal);
            return hasMatchingMethodName;
        }

        #endregion
    }

    /// <summary>
    /// Groups verified async runtime helpers as <c>[Async dispatcher frames]</c> or removes them.
    /// Leaves unrelated frames and helper sequences that cannot be verified unchanged.
    /// </summary>
    /// <remarks>
    /// Optional because helper layouts vary by runtime. Each marker keeps a physical frame's
    /// identity; it does not mean an application method is executing.
    /// </remarks>
    internal sealed class AsyncProfilerInfrastructureCleanupTransformPass
    {
        public void Apply(AsyncProfilerCallStackTransformState context)
        {
            List<AsyncProfilerStitchedFrame> frames = context.Frames;

            // Handle complete dispatch and resumption sequences before individual helpers.
            // Later edits stay within [cleanupStart, cleanupEnd), using the current list indices.
            CollapseProfilerDispatchSpan(context);
            int cleanupEnd = CollapseRuntimeResumptionBridge(context);
            int cleanupStart = CollapseProfilerOwnedPrefix(context, ref cleanupEnd);
            cleanupStart = CollapseBoundedAwaitRegistration(context, cleanupStart, ref cleanupEnd);
            List<(int Start, int End)> rejectedSpans = null;

            // Scan leaf-to-root and keep rejected sequences intact. Never extend a later edit
            // back across a protected range, so its saved indices stay valid.
            for (int i = cleanupStart; i < cleanupEnd;)
            {
                if (frames[i].Origin != AsyncProfilerStitchedFrameOrigin.Sync)
                {
                    i++;
                    continue;
                }

                // Include a public SetResult call only when the full completion chain is verified.
                AsyncProfilerRuntimeSequenceMatch chain =
                    context.MatchRuntimeSequence(AsyncProfilerRuntimeSequenceKind.TaskCompletionChain, i, cleanupEnd);
                bool hasOwnedCompletionChain = chain.IsMatch && HasSameMachineryOwner(context, i, chain.Length);
                if (hasOwnedCompletionChain)
                {
                    cleanupEnd -= CollapseMachinerySpan(frames, i, chain.Length);
                    context.Diagnostics.AsyncInfrastructureFramesCollapsed += chain.Length;
                    i++;
                    continue;
                }

                // Protect rejected layouts from partial cleanup. A chain with mismatched ownership
                // must also stay intact, even though its frame layout matched.
                AsyncProfilerRuntimeSequenceMatch handoff = context.MatchRuntimeSequence(AsyncProfilerRuntimeSequenceKind.V2CompletionHandoff, i, cleanupEnd);
                int protectedChainLength = chain.IsRejected || chain.IsMatch ? chain.Length : 0;
                int protectedHandoffLength = handoff.IsRejected ? handoff.Length : 0;
                int protectedLength = Math.Max(protectedChainLength, protectedHandoffLength);
                AsyncProfilerRuntimeSequenceMatch startup = context.MatchRuntimeSequence(AsyncProfilerRuntimeSequenceKind.V1Startup, i, cleanupEnd);
                AsyncProfilerRuntimeSequenceMatch registration = context.MatchRuntimeSequence(AsyncProfilerRuntimeSequenceKind.V1AwaitRegistration, i, cleanupEnd);
                int protectedStartupLength = startup.IsRejected ? startup.Length : 0;
                int protectedRegistrationLength = registration.IsRejected ? registration.Length : 0;
                protectedLength = Math.Max(protectedLength, Math.Max(protectedStartupLength, protectedRegistrationLength));
                if (protectedLength != 0)
                {
                    if (rejectedSpans == null)
                    {
                        rejectedSpans = new List<(int Start, int End)>();
                    }

                    rejectedSpans.Add((i, i + protectedLength));
                    i += protectedLength;
                    continue;
                }

                AsyncProfilerRuntimeSequenceMatch pair = context.MatchRuntimeSequence(AsyncProfilerRuntimeSequenceKind.TaskCompletion, i, cleanupEnd);
                AsyncProfilerStitchedFrame entry = frames[i];
                bool hasVerifiedCompletionPair =
                    pair.IsMatch && entry.Context != null &&
                    entry.ContextKind == AsyncProfilerStitchedFrameContextKind.Active &&
                    HasSameMachineryOwner(context, i, pair.Length) &&
                    context.HasVerifiedV1CompletionAnchor(entry.Context, entry.CodeAddress);
                if (!hasVerifiedCompletionPair)
                {
                    i++;
                    continue;
                }

                // Extend toward the leaf only as far as the last protected range allows.
                int minimumStart = rejectedSpans != null ? Math.Max(cleanupStart, rejectedSpans[rejectedSpans.Count - 1].End) : cleanupStart;
                bool hasMatchingContinuationBridge =
                    i > minimumStart && frames[i - 1].Method != MethodIndex.Invalid &&
                    IsSyncFrameKind(frames[i - 1], AsyncProfilerStitchSyncFrameKind.ContinuationBridge) &&
                    HasSameMachineryOwner(context, i - 1, pair.Length + 1);
                int spanStart = hasMatchingContinuationBridge ? i - 1 : i;
                spanStart = FindProfilerDispatchSpanStart(context, spanStart, frames[spanStart], out int rejectedStart, minimumStart);
                if (rejectedStart >= 0)
                {
                    if (rejectedSpans == null)
                    {
                        rejectedSpans = new List<(int Start, int End)>();
                    }

                    rejectedSpans.Add((rejectedStart, i + pair.Length));
                    i += pair.Length;
                    continue;
                }

                int matchedFrameCount = i + pair.Length - spanStart;
                cleanupEnd -= CollapseMachinerySpan(frames, spanStart, matchedFrameCount);
                context.Diagnostics.AsyncInfrastructureFramesCollapsed += matchedFrameCount;
                context.Diagnostics.ProfilerEnvelopeFramesCollapsed += i - spanStart;
                i = spanStart + 1;
            }

            // Scan root-to-leaf so removals leave unvisited indices unchanged.
            // mergeEnd stops marker merging at protected ranges; faultSpanStart tracks a fault-handling group.
            int rejectedIndex = (rejectedSpans?.Count ?? 0) - 1;
            int mergeEnd = cleanupEnd;
            int faultSpanStart = -1;
            for (int i = cleanupEnd - 1; i >= cleanupStart; i--)
            {
                bool isInRejectedSpan = rejectedIndex >= 0 && i < rejectedSpans[rejectedIndex].End;
                if (isInRejectedSpan)
                {
                    mergeEnd = rejectedSpans[rejectedIndex].Start;
                    i = mergeEnd;
                    rejectedIndex--;
                    continue;
                }

                AsyncProfilerStitchedFrame frame = frames[i];
                if (frame.Origin != AsyncProfilerStitchedFrameOrigin.Sync)
                {
                    continue;
                }

                bool isDispatcherMarker = frame.Presentation == AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames;
                AsyncProfilerCallStackStitchBoundaryKind boundaryKind = context.ClassifyBoundary(frame.CodeAddress).Kind;
                int minimumStart = rejectedIndex >= 0 ? Math.Max(cleanupStart, rejectedSpans[rejectedIndex].End) : cleanupStart;
                int boundedStart = FindBoundedAsyncInfrastructureStart(context, i, out int rejectedStart, minimumStart);
                if (rejectedStart >= 0)
                {
                    mergeEnd = rejectedStart;
                    i = rejectedStart;
                    continue;
                }

                bool canGroupFrame =
                    isDispatcherMarker || boundedStart < i ||
                    context.GetRuntimeProfile(frame.CodeAddress).IsOptionalInfrastructureFrame(frame, boundaryKind);
                if (!canGroupFrame)
                {
                    continue;
                }

                // Merge the following marker only when it belongs to the same call stack and runtime.
                bool canMergeWithFollowingMarker =
                    i + 1 < mergeEnd &&
                    frames[i + 1].Presentation == AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames &&
                    HasSameMachineryOwner(context, i, 2);
                int trailingFrameCount = canMergeWithFollowingMarker ? 2 : 1;
                bool isFaultEntry =
                    frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1MethodHelper &&
                    frame.Method != MethodIndex.Invalid && i > cleanupStart &&
                    frames[i - 1].SyncFrameKind == AsyncProfilerStitchSyncFrameKind.FaultHandling;
                bool joinsFaultSpan = trailingFrameCount == 2 && i + 1 == faultSpanStart;
                int spanStart = boundedStart;

                // Extend into profiler helpers only with verified dispatch evidence from an active call stack.
                bool canVerifyCallStack =
                    context.TraceLog != null && frame.Context != null &&
                    frame.ContextKind == AsyncProfilerStitchedFrameContextKind.Active;
                bool hasDispatchEvidence =
                    canVerifyCallStack &&
                    (isDispatcherMarker || isFaultEntry || joinsFaultSpan ||
                     context.ClassifyBoundary(frame.CodeAddress).IsDispatchMarker);
                bool canExtendDispatchSpan =
                    hasDispatchEvidence &&
                    context.HasVerifiedDispatchEntry(frame.Context, frame.CodeAddress, requireAsyncAnchor: false);
                if (canExtendDispatchSpan)
                {
                    spanStart = FindProfilerDispatchSpanStart(context, i, frame, out rejectedStart, minimumStart);
                    if (rejectedStart >= 0)
                    {
                        mergeEnd = rejectedStart;
                        i = rejectedStart;
                        continue;
                    }
                }

                int precedingHelperCount = i - spanStart;
                bool isFaultSpan =
                    isFaultEntry || joinsFaultSpan ||
                    frames[spanStart].SyncFrameKind == AsyncProfilerStitchSyncFrameKind.FaultHandling;
                faultSpanStart = isFaultSpan ? spanStart : -1;
                int removed = CollapseMachinerySpan(frames, spanStart, precedingHelperCount + trailingFrameCount);
                cleanupEnd -= removed;
                mergeEnd -= removed;
                context.Diagnostics.AsyncInfrastructureFramesCollapsed += precedingHelperCount + (isDispatcherMarker ? 0 : 1);
                context.Diagnostics.ProfilerEnvelopeFramesCollapsed += precedingHelperCount;
                i = spanStart;
            }
        }

        #region private

        /// <summary>
        /// Groups a verified V1 await-registration sequence while keeping unrelated callees protected.
        /// </summary>
        /// <returns>Where later cleanup may start, with cleanupEnd adjusted for removed frames.</returns>
        private static int CollapseBoundedAwaitRegistration(AsyncProfilerCallStackTransformState context,
            int protectedEnd, ref int cleanupEnd)
        {
            if (context.TraceLog == null)
            {
                return protectedEnd;
            }

            List<AsyncProfilerStitchedFrame> frames = context.Frames;
            for (int start = 0; start < protectedEnd; start++)
            {
                AsyncProfilerStitchedFrame entry = frames[start];
                bool hasActiveSetupFrame =
                    IsSyncFrameKind(entry, AsyncProfilerStitchSyncFrameKind.V1DispatchSetup) &&
                    entry.Context != null && entry.ContextKind == AsyncProfilerStitchedFrameContextKind.Active;
                if (!hasActiveSetupFrame)
                {
                    continue;
                }

                AsyncProfilerRuntimeSequenceMatch registration =
                    context.MatchRuntimeSequence(AsyncProfilerRuntimeSequenceKind.V1AwaitRegistration, start + 1, protectedEnd);
                if (!registration.IsMatch)
                {
                    continue;
                }

                int end = start + 1 + registration.Length;
                if (end >= protectedEnd)
                {
                    continue;
                }

                AsyncProfilerStitchedFrame caller = frames[end];
                bool belongsToCallStack =
                    caller.Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                    caller.ContextKind == AsyncProfilerStitchedFrameContextKind.Active &&
                    ReferenceEquals(entry.Context, caller.Context);
                bool hasResolvedCaller =
                    belongsToCallStack &&
                    caller.CodeAddress != CodeAddressIndex.Invalid && caller.Method != MethodIndex.Invalid;
                bool hasApplicationCaller =
                    hasResolvedCaller &&
                    context.GetHostModuleOwner(caller.CodeAddress) == ModuleFileIndex.Invalid &&
                    !context.ClassifyBoundary(caller.CodeAddress).IsDispatchMarker;
                if (!hasApplicationCaller)
                {
                    continue;
                }

                AsyncProfilerRuntimeSequenceMatch match =
                    context.MatchRuntimeSequence(AsyncProfilerRuntimeSequenceKind.ProfilerEntry, start, end);
                bool hasCompleteOwnedEntry =
                    match.IsMatch && match.Length == end - start &&
                    HasSameMachineryOwner(context, start, match.Length);
                if (!hasCompleteOwnedEntry)
                {
                    continue;
                }

                AsyncProfilerRuntimeRuleProfile profile = context.GetRuntimeProfile(entry.CodeAddress);
                // Keep boundaries and already-grouped frames out of this optional helper sequence.
                bool hasOnlyOptionalHelpers = true;
                for (int i = start; i < end; i++)
                {
                    AsyncProfilerStitchedFrame helper = frames[i];
                    AsyncProfilerCallStackStitchBoundaryKind boundary = context.ClassifyBoundary(helper.CodeAddress).Kind;
                    bool isOptionalHelper =
                        helper.Presentation == AsyncProfilerStitchedFramePresentation.Native &&
                        helper.ContextKind == AsyncProfilerStitchedFrameContextKind.Active &&
                        boundary == AsyncProfilerCallStackStitchBoundaryKind.None &&
                        profile.IsOptionalInfrastructureFrame(helper, boundary);
                    if (!isOptionalHelper)
                    {
                        hasOnlyOptionalHelpers = false;
                        break;
                    }
                }

                if (!hasOnlyOptionalHelpers)
                {
                    continue;
                }

                int spanStart = FindProfilerDispatchSpanStart(context, start, entry, out int rejectedStart);
                if (rejectedStart >= 0)
                {
                    return protectedEnd;
                }

                int matchedFrameCount = end - spanStart;
                cleanupEnd -= CollapseMachinerySpan(frames, spanStart, matchedFrameCount);
                context.Diagnostics.AsyncInfrastructureFramesCollapsed += matchedFrameCount;
                context.Diagnostics.ProfilerEnvelopeFramesCollapsed += start - spanStart;

                // Keep unrelated callees before the marker protected; resume cleanup at its caller.
                return spanStart + 1;
            }

            return protectedEnd;
        }

        /// <summary>Groups adjacent profiler helpers with the first verified V2 dispatch entry.</summary>
        private static void CollapseProfilerDispatchSpan(AsyncProfilerCallStackTransformState context)
        {
            if (context.TraceLog == null)
            {
                return;
            }

            List<AsyncProfilerStitchedFrame> frames = context.Frames;
            // Stop at the first boundary or logical frame; later dispatchers belong to other spans.
            for (int i = 0; i < frames.Count; i++)
            {
                AsyncProfilerStitchedFrame dispatcher = frames[i];
                bool isPhysicalNonParentFrame =
                    dispatcher.Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                    dispatcher.ContextKind != AsyncProfilerStitchedFrameContextKind.ParentContext;
                if (!isPhysicalNonParentFrame)
                {
                    return;
                }
                AsyncProfilerCallStackStitchBoundaryKind boundary = context.ClassifyBoundary(dispatcher.CodeAddress).Kind;
                if (boundary == AsyncProfilerCallStackStitchBoundaryKind.None)
                {
                    if (dispatcher.Presentation != AsyncProfilerStitchedFramePresentation.Native)
                    {
                        return;
                    }

                    continue;
                }

                if (boundary != AsyncProfilerCallStackStitchBoundaryKind.V2DispatcherInfrastructure)
                {
                    return;
                }

                bool isDispatcherMarker = dispatcher.Presentation == AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames;
                if (isDispatcherMarker)
                {
                    bool hasVerifiedMarker =
                        dispatcher.Context != null &&
                        context.HasVerifiedV2DispatchPair(dispatcher.Context, dispatcher.CodeAddress);
                    if (!hasVerifiedMarker)
                    {
                        return;
                    }
                }
                else
                {
                    // A raw entry must contain exactly the expected dispatch pair.
                    bool hasFollowingPhysicalFrame =
                        i + 1 < frames.Count &&
                        frames[i + 1].Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                        frames[i + 1].Presentation == AsyncProfilerStitchedFramePresentation.Native &&
                        ReferenceEquals(dispatcher.Context, frames[i + 1].Context);
                    bool hasVerifiedDispatchPair =
                        hasFollowingPhysicalFrame &&
                        context.IsV2DispatchPair(dispatcher.CodeAddress, frames[i + 1].CodeAddress);
                    bool hasExtraDispatchFrame =
                        hasVerifiedDispatchPair && i + 2 < frames.Count &&
                        context.ClassifyBoundary(frames[i + 2].CodeAddress).Kind == AsyncProfilerCallStackStitchBoundaryKind.V2DispatcherInfrastructure;
                    if (!hasVerifiedDispatchPair || hasExtraDispatchFrame)
                    {
                        return;
                    }
                }

                AsyncProfilerRuntimeRuleProfile profile = context.GetRuntimeProfile(dispatcher.CodeAddress);
                bool canGroupDispatcher = profile.IsOptionalInfrastructureFrame(dispatcher, boundary);
                bool hasOnlyOptionalDispatchFrames = canGroupDispatcher && (isDispatcherMarker || profile.IsOptionalInfrastructureFrame(frames[i + 1], boundary));
                if (!hasOnlyOptionalDispatchFrames)
                {
                    return;
                }

                int start = FindProfilerDispatchSpanStart(context, i, dispatcher, out int rejectedStart);
                if (rejectedStart >= 0)
                {
                    return;
                }
                if (start < i)
                {
                    // Keep the first physical frame as the marker, without claiming application execution.
                    CollapseMachinerySpan(frames, start, i - start + (isDispatcherMarker ? 1 : 2));
                    context.Diagnostics.AsyncInfrastructureFramesCollapsed += i - start + (isDispatcherMarker ? 0 : 2);
                }

                return;
            }
        }

        /// <summary>
        /// Extends a dispatcher span toward the leaf through adjacent, owned runtime helpers.
        /// Reports rejected sequences separately so the caller can leave them untouched.
        /// </summary>
        private static int FindProfilerDispatchSpanStart(AsyncProfilerCallStackTransformState context,
            int start, AsyncProfilerStitchedFrame entry,
            out int rejectedStart, int minimumStart = 0)
        {
            rejectedStart = -1;
            AsyncProfilerRuntimeRuleProfile profile = context.GetRuntimeProfile(entry.CodeAddress);
            ModuleFileIndex owner = context.GetHostModuleOwner(entry.CodeAddress);
            while (start > minimumStart)
            {
                // Try a complete sequence before considering individual profiler helpers.
                int infrastructureStart = FindBoundedAsyncInfrastructureStart(context, start, out rejectedStart, minimumStart);
                if (rejectedStart >= 0)
                {
                    return start;
                }

                if (infrastructureStart < start)
                {
                    start = infrastructureStart;
                    continue;
                }

                AsyncProfilerStitchedFrame helper = context.Frames[start - 1];
                bool isOwnedPhysicalHelper =
                    helper.Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                    helper.Presentation == AsyncProfilerStitchedFramePresentation.Native &&
                    helper.Method != MethodIndex.Invalid &&
                    ReferenceEquals(helper.Context, entry.Context);
                bool hasMatchingRuntime =
                    isOwnedPhysicalHelper &&
                    owner != ModuleFileIndex.Invalid && owner == context.GetHostModuleOwner(helper.CodeAddress) &&
                    ReferenceEquals(profile, context.GetRuntimeProfile(helper.CodeAddress));
                bool isProfilerHelper =
                    hasMatchingRuntime &&
                    !context.ClassifyBoundary(helper.CodeAddress).IsDispatchMarker &&
                    profile.IsProfilerDispatchInfrastructure(helper.SyncClassification);
                if (!isProfilerHelper)
                {
                    break;
                }

                start--;
            }

            return start;
        }

        /// <summary>
        /// Finds a complete helper sequence ending at the entry frame, without crossing minimumStart.
        /// Returns the entry index when no sequence matches and reports rejected candidates separately.
        /// </summary>
        private static int FindBoundedAsyncInfrastructureStart(AsyncProfilerCallStackTransformState context,
            int entryIndex, out int rejectedStart, int minimumStart)
        {
            rejectedStart = -1;
            AsyncProfilerStitchedFrame entry = context.Frames[entryIndex];
            bool hasPhysicalEntry =
                context.TraceLog != null && entry.Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                entry.CodeAddress != CodeAddressIndex.Invalid &&
                entry.Presentation == AsyncProfilerStitchedFramePresentation.Native;
            bool hasResolvedActiveEntry =
                hasPhysicalEntry && entry.Context != null &&
                entry.ContextKind == AsyncProfilerStitchedFrameContextKind.Active &&
                entry.Method != MethodIndex.Invalid;
            if (!hasResolvedActiveEntry)
            {
                return entryIndex;
            }

            bool hasUnambiguousStitching =
                context.StitchingState.ProcessedCallStackCount == context.Segments.Count &&
                context.StitchingState.HasConsistentPhysicalAlignment &&
                context.StitchingState.UnresolvedV1CompletionCount == 0;
            if (!hasUnambiguousStitching)
            {
                return entryIndex;
            }

            AsyncProfilerRuntimeRuleProfile profile = context.GetRuntimeProfile(entry.CodeAddress);
            string entryMethodName = AsyncProfilerMethodNames.NormalizeMethodName(context.TraceLog.CodeAddresses[entry.CodeAddress].FullMethodName);
            bool hasSupportedSequence = profile.TryGetOptionalSequenceKind(entryMethodName, entry.SyncClassification, out AsyncProfilerRuntimeSequenceKind sequence);
            bool hasVerifiedDispatchEntry = hasSupportedSequence && context.HasVerifiedDispatchEntry(entry.Context, entry.CodeAddress, requireAsyncAnchor: false);
            if (!hasVerifiedDispatchEntry)
            {
                return entryIndex;
            }

            ModuleFileIndex owner = context.GetHostModuleOwner(entry.CodeAddress);
            bool hasSequenceOnlyHelper = false;
            int matchedStart = entryIndex;
            // Keep searching toward the leaf for the longest complete sequence.
            for (int start = entryIndex - 1; start >= minimumStart; start--)
            {
                AsyncProfilerStitchedFrame helper = context.Frames[start];
                AsyncProfilerCallStackStitchBoundaryKind boundary = context.ClassifyBoundary(helper.CodeAddress).Kind;
                bool isPhysicalHelper =
                    helper.Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                    helper.Presentation == AsyncProfilerStitchedFramePresentation.Native &&
                    helper.ContextKind == AsyncProfilerStitchedFrameContextKind.Active;
                bool belongsToCallStack = isPhysicalHelper && ReferenceEquals(helper.Context, entry.Context) && helper.Method != MethodIndex.Invalid;
                bool hasMatchingRuntime =
                    belongsToCallStack &&
                    owner != ModuleFileIndex.Invalid && context.GetHostModuleOwner(helper.CodeAddress) == owner &&
                    ReferenceEquals(context.GetRuntimeProfile(helper.CodeAddress), profile);
                bool isNonBoundaryHelper = hasMatchingRuntime && !context.ClassifyBoundary(helper.CodeAddress).IsDispatchMarker;
                bool canExtendSequence =
                    isNonBoundaryHelper &&
                    helper.SyncFrameKind != AsyncProfilerStitchSyncFrameKind.V1StateMachineBody &&
                    helper.SyncFrameKind != AsyncProfilerStitchSyncFrameKind.V2ResumeStub &&
                    helper.BoxedStateMachineMethod == MethodIndex.Invalid;
                if (!canExtendSequence)
                {
                    break;
                }

                // Leave individually recognized helpers on their usual cleanup paths.
                // Match the whole sequence only after a helper needs that extra proof.
                hasSequenceOnlyHelper |=
                    !profile.IsOptionalInfrastructureFrame(helper, boundary) &&
                    !profile.IsProfilerDispatchInfrastructure(helper.SyncClassification);
                if (!hasSequenceOnlyHelper)
                {
                    continue;
                }

                AsyncProfilerRuntimeSequenceMatch infrastructure = context.MatchRuntimeSequence(sequence, start, entryIndex + 1, allowRuntimeInteriors: true);
                if (infrastructure.IsRejected)
                {
                    rejectedStart = start;
                    return entryIndex;
                }

                bool hasCompleteSequence = infrastructure.IsMatch && infrastructure.Length == entryIndex + 1 - start;
                if (hasCompleteSequence)
                {
                    matchedStart = start;
                }
            }

            return matchedStart;
        }

        /// <summary>
        /// Groups owned runtime helpers before the first active logical frame.
        /// Keeps unrelated callees protected from later cleanup.
        /// </summary>
        /// <returns>Where later cleanup may start, with cleanupEnd adjusted for removed frames.</returns>
        private static int CollapseProfilerOwnedPrefix(AsyncProfilerCallStackTransformState context, ref int cleanupEnd)
        {
            if (context.TraceLog == null || context.Segments.Count == 0)
            {
                return 0;
            }

            List<AsyncProfilerStitchedFrame> frames = context.Frames;
            int targetIndex = frames.Count;
            bool hasProfilerEntryCandidate = false;
            for (int i = 0; i < frames.Count; i++)
            {
                AsyncProfilerStitchedFrame frame = frames[i];
                bool isActiveLogicalFrame =
                    frame.ContextKind == AsyncProfilerStitchedFrameContextKind.Active &&
                    (frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent ||
                     frame.Origin == AsyncProfilerStitchedFrameOrigin.V1CompletedBody ||
                     frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCaller ||
                     frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget);
                if (isActiveLogicalFrame)
                {
                    targetIndex = i;
                    break;
                }

                hasProfilerEntryCandidate |= frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.ProfilerEntry;
                // A leading CreateDispatcher has individual-helper cleanup. Only a later
                // setup frame can justify grouping the callees before it.
                hasProfilerEntryCandidate |= i > 0 && frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1DispatchSetup;
            }

            int registrationEntry = FindV1AwaitRegistrationEntry(context, targetIndex);
            int completionEntry = FindV1CompletionEntry(context, targetIndex);
            bool hasOnlyCompletionEntry = !hasProfilerEntryCandidate && registrationEntry < 0 && completionEntry >= 0;
            bool hasEntryCandidate = hasProfilerEntryCandidate || registrationEntry >= 0 || completionEntry >= 0;
            if (!hasEntryCandidate)
            {
                return 0;
            }

            // Protect the whole prefix when its target or physical alignment is unverified.
            bool hasConsistentTarget =
                targetIndex != frames.Count &&
                context.StitchingState.ProcessedCallStackCount == context.Segments.Count &&
                context.StitchingState.HasConsistentPhysicalAlignment;
            if (!hasConsistentTarget)
            {
                return targetIndex;
            }

            AsyncProfilerStitchedFrame target = frames[targetIndex];
            bool needsDispatcher = target.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget && !target.IsStateMachineBoxCarrier;
            if (needsDispatcher)
            {
                if (target.Segment.Kind != AsyncCallstackKind.StateMachineAsync)
                {
                    return targetIndex;
                }
            }
            else
            {
                bool hasPhysicalAnchor =
                    target.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent ||
                    target.Origin == AsyncProfilerStitchedFrameOrigin.V1CompletedBody ||
                    target.IsStateMachineBoxCarrier;
                if (!hasPhysicalAnchor)
                {
                    return targetIndex;
                }
            }

            // Prefer a complete profiler entry; registration and completion entries are fallbacks.
            int entryIndex = -1;
            for (int i = targetIndex - 1; i >= 0; i--)
            {
                AsyncProfilerRuntimeSequenceMatch entry = context.MatchRuntimeSequence(AsyncProfilerRuntimeSequenceKind.ProfilerEntry, i, targetIndex);
                if (entry.IsRejected)
                {
                    return targetIndex;
                }

                bool hasCompleteProfilerEntry = entry.IsMatch && entry.Length == targetIndex - i;
                if (hasCompleteProfilerEntry)
                {
                    entryIndex = i;
                    break;
                }
            }

            if (entryIndex < 0)
            {
                entryIndex = registrationEntry >= 0 ? registrationEntry : completionEntry;
                if (entryIndex < 0)
                {
                    return targetIndex;
                }
            }

            // Every prefix frame must belong to the target. A pending V1 target also needs one dispatcher.
            int dispatcherCount = 0;
            for (int i = 0; i < targetIndex; i++)
            {
                AsyncProfilerStitchedFrame frame = frames[i];
                bool belongsToTargetCallStack =
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                    frame.ContextKind == AsyncProfilerStitchedFrameContextKind.Active &&
                    ReferenceEquals(frame.Context, target.Context);
                if (!belongsToTargetCallStack)
                {
                    return targetIndex;
                }

                AsyncProfilerCallStackStitchBoundaryInfo boundary = context.ClassifyBoundary(frame.CodeAddress);
                if (boundary.IsDispatchMarker)
                {
                    bool isExpectedDispatcher = needsDispatcher && i >= entryIndex && boundary.Kind == AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher;
                    if (!isExpectedDispatcher)
                    {
                        return targetIndex;
                    }

                    dispatcherCount++;
                    if (dispatcherCount != 1)
                    {
                        return targetIndex;
                    }
                }
            }

            if (needsDispatcher && dispatcherCount != 1)
            {
                return targetIndex;
            }

            int spanStart = FindProfilerDispatchSpanStart(context, entryIndex, frames[entryIndex], out int rejectedStart);
            if (rejectedStart >= 0)
            {
                return targetIndex;
            }

            int matchedFrameCount = targetIndex - spanStart;
            cleanupEnd -= CollapseMachinerySpan(frames, spanStart, matchedFrameCount);
            context.Diagnostics.AsyncInfrastructureFramesCollapsed += matchedFrameCount;
            context.Diagnostics.ProfilerEnvelopeFramesCollapsed += matchedFrameCount;

            // A completion-only match leaves independent helper groups available to later cleanup.
            return hasOnlyCompletionEntry ? 0 : spanStart + 1;
        }

        /// <summary>Finds the contiguous await-registration helpers immediately before a running V1 method.</summary>
        /// <returns>The first matching helper index, or -1 if the entry cannot be verified.</returns>
        private static int FindV1AwaitRegistrationEntry(AsyncProfilerCallStackTransformState context, int targetIndex)
        {
            if (targetIndex == context.Frames.Count)
            {
                return -1;
            }

            AsyncProfilerStitchedFrame target = context.Frames[targetIndex];
            bool hasV1CurrentFrame =
                target.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent &&
                target.Segment.Kind == AsyncCallstackKind.StateMachineAsync;
            bool hasResolvedCurrentMethod =
                hasV1CurrentFrame &&
                target.Method != MethodIndex.Invalid && target.CodeAddress != CodeAddressIndex.Invalid;
            if (!hasResolvedCurrentMethod)
            {
                return -1;
            }

            // Extend toward the leaf only while each candidate reaches the running method.
            int entryIndex = -1;
            for (int i = targetIndex - 1; i >= 0; i--)
            {
                AsyncProfilerRuntimeSequenceMatch registration = context.MatchRuntimeSequence(AsyncProfilerRuntimeSequenceKind.V1AwaitRegistration, i, targetIndex);
                if (registration.IsRejected)
                {
                    return -1;
                }

                bool hasFullRegistration = registration.IsMatch && registration.Length == targetIndex - i;
                if (!hasFullRegistration)
                {
                    break;
                }

                entryIndex = i;
            }

            return entryIndex;
        }

        /// <summary>
        /// Finds the task-completion sequence before a finishing V1 method.
        /// Includes an adjacent continuation bridge when it shares the same owner.
        /// </summary>
        /// <returns>The first matching helper index, or -1 if the entry cannot be verified.</returns>
        private static int FindV1CompletionEntry(AsyncProfilerCallStackTransformState context, int targetIndex)
        {
            bool hasCompletionPrefix = targetIndex >= 3 && targetIndex != context.Frames.Count;
            if (!hasCompletionPrefix)
            {
                return -1;
            }

            bool hasCompletingBody =
                context.Frames[targetIndex].Origin == AsyncProfilerStitchedFrameOrigin.V1CompletedBody &&
                IsSyncFrameKind(context.Frames[targetIndex - 1], AsyncProfilerStitchSyncFrameKind.V1MethodCompletion);
            bool hasVerifiedCompletion =
                hasCompletingBody &&
                context.HasVerifiedV1CompletionAnchor(context.Frames[targetIndex].Context, context.Frames[targetIndex - 1].CodeAddress);
            if (!hasVerifiedCompletion)
            {
                return -1;
            }

            for (int i = targetIndex - 2; i >= 0; i--)
            {
                AsyncProfilerRuntimeSequenceMatch pair = context.MatchRuntimeSequence(AsyncProfilerRuntimeSequenceKind.TaskCompletion, i, targetIndex - 1);
                if (pair.IsRejected)
                {
                    return -1;
                }

                bool hasCompletePair = pair.IsMatch && pair.Length == targetIndex - 1 - i;
                if (hasCompletePair)
                {
                    AsyncProfilerRuntimeSequenceMatch chain = context.MatchRuntimeSequence(
                        AsyncProfilerRuntimeSequenceKind.TaskCompletionChain, i, targetIndex);
                    if (chain.IsRejected)
                    {
                        return -1;
                    }

                    bool hasAdjacentBridge = i > 0 && IsSyncFrameKind(context.Frames[i - 1], AsyncProfilerStitchSyncFrameKind.ContinuationBridge);
                    int start = hasAdjacentBridge ? i - 1 : i;
                    return HasSameMachineryOwner(context, start, targetIndex - start) ? start : -1;
                }
            }

            return -1;
        }

        /// <summary>
        /// Removes verified scheduling helpers after the root async call stack.
        /// A partial match keeps a dispatcher marker and protects the remaining callers.
        /// </summary>
        /// <returns>
        /// The exclusive end for later cleanup, excluding rejected or partially grouped bridge frames.
        /// </returns>
        private static int CollapseRuntimeResumptionBridge(AsyncProfilerCallStackTransformState context)
        {
            List<AsyncProfilerStitchedFrame> frames = context.Frames;
            if (context.Segments.Count == 0)
            {
                return frames.Count;
            }

            // Find the root call stack and the runtime scheduling anchor among its callers.
            AsyncProfilerCallStack rootCallStack = context.Segments[context.Segments.Count - 1];
            int rootIndex = -1;
            for (int i = frames.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(frames[i].Context, rootCallStack) &&
                    frames[i].ContextKind == AsyncProfilerStitchedFrameContextKind.Active)
                {
                    rootIndex = i;
                    break;
                }
            }

            int bridgeStart = rootIndex + 1;
            while (bridgeStart < frames.Count &&
                   frames[bridgeStart].ContextKind == AsyncProfilerStitchedFrameContextKind.ParentContext)
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

            // If no caller contains the scheduling anchor, also check the root's retained frames.
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

            // An unplaced root can identify a candidate to protect, but does not prove it may be removed.
            bool hasPlacedRoot = rootIndex >= 0 && bridgeStart <= dispatchIndex;
            if (!hasPlacedRoot)
            {
                bridgeStart = 0;
                for (int i = 0; i < dispatchIndex; i++)
                {
                    bool isPhysicalSpanBreak =
                        frames[i].Origin != AsyncProfilerStitchedFrameOrigin.Sync ||
                        context.ClassifyBoundary(frames[i].CodeAddress).IsDispatchMarker;
                    if (isPhysicalSpanBreak)
                    {
                        bridgeStart = i + 1;
                    }
                }
            }

            int bridgeEnd = dispatchIndex < frames.Count ? dispatchIndex + 1 : frames.Count;
            AsyncProfilerRuntimeSequenceMatch bridge = context.MatchRuntimeSequence(AsyncProfilerRuntimeSequenceKind.RuntimeResumptionBridge, bridgeStart, bridgeEnd);
            if (bridge.Length == 0)
            {
                return frames.Count;
            }

            // Accept only a completely verified bridge, or a verified completion prefix with a physical caller.
            int matchedEnd = bridgeStart + bridge.Length;
            bool hasCompletionPrefix = matchedEnd < bridgeEnd;
            bool hasCompletedRootDispatch =
                !hasPlacedRoot && bridge.IsMatch && !hasCompletionPrefix &&
                context.HasVerifiedCompletedV1OuterDispatch(rootCallStack, frames[bridgeStart].CodeAddress);
            // Rejected bridges remain intact; individual-helper cleanup only visits frames before them.
            bool hasMatchedRootBridge =
                bridge.IsMatch && (hasPlacedRoot || hasCompletedRootDispatch) &&
                context.TraceLog != null && dispatchIndex != frames.Count;
            if (!hasMatchedRootBridge)
            {
                return bridgeStart;
            }

            bool hasConsistentStitching =
                context.StitchingState.ProcessedCallStackCount == context.Segments.Count &&
                context.StitchingState.HasConsistentPhysicalAlignment;
            if (!hasConsistentStitching)
            {
                return bridgeStart;
            }

            bool hasRequiredRootAnchor =
                rootCallStack.Frames.Kind != AsyncCallstackKind.RuntimeAsync ||
                HasVerifiedAsyncAnchor(context, rootIndex, rootCallStack);
            if (!hasRequiredRootAnchor)
            {
                return bridgeStart;
            }

            if (hasCompletionPrefix)
            {
                AsyncProfilerStitchedFrame caller = frames[matchedEnd];
                AsyncProfilerStitchedFrame anchor = frames[dispatchIndex];
                bool hasUnambiguousCompletionPrefix =
                    context.StitchingState.UnresolvedV1CompletionCount == 0 &&
                    matchedEnd < dispatchIndex;
                if (!hasUnambiguousCompletionPrefix)
                {
                    return bridgeStart;
                }

                // Keep a resolved application caller after the partial runtime sequence.
                bool hasResolvedPhysicalCaller =
                    caller.Origin == AsyncProfilerStitchedFrameOrigin.Sync && caller.Context == null &&
                    caller.CodeAddress != CodeAddressIndex.Invalid && caller.Method != MethodIndex.Invalid;
                bool hasApplicationCaller =
                    hasResolvedPhysicalCaller &&
                    context.TraceLog.CodeAddresses.ModuleFileIndex(caller.CodeAddress) != ModuleFileIndex.Invalid &&
                    context.GetHostModuleOwner(caller.CodeAddress) == ModuleFileIndex.Invalid &&
                    !context.ClassifyBoundary(caller.CodeAddress).IsDispatchMarker;
                if (!hasApplicationCaller)
                {
                    return bridgeStart;
                }

                // The scheduling anchor must share the bridge's runtime and verified call-stack owner.
                bool hasResolvedPhysicalAnchor =
                    anchor.Origin == AsyncProfilerStitchedFrameOrigin.Sync && anchor.Context == null &&
                    anchor.Method != MethodIndex.Invalid;
                bool hasMatchingAnchorRuntime =
                    hasResolvedPhysicalAnchor &&
                    context.GetHostModuleOwner(anchor.CodeAddress) == context.GetHostModuleOwner(frames[bridgeStart].CodeAddress) &&
                    ReferenceEquals(context.GetRuntimeProfile(anchor.CodeAddress), context.GetRuntimeProfile(frames[bridgeStart].CodeAddress));
                if (!hasMatchingAnchorRuntime)
                {
                    return bridgeStart;
                }

                bool hasVerifiedBridgeOwner =
                    context.HasVerifiedDispatchEntry(rootCallStack, frames[bridgeStart].CodeAddress) ||
                    context.HasVerifiedV1CompletionAnchor(rootCallStack, frames[bridgeStart].CodeAddress, allowCompletionUnwind: true);
                if (!hasVerifiedBridgeOwner)
                {
                    return bridgeStart;
                }
            }

            for (int i = bridgeStart; i < matchedEnd; i++)
            {
                bool isUnassignedPhysicalFrame = frames[i].Origin == AsyncProfilerStitchedFrameOrigin.Sync && frames[i].Context == null;
                if (!isUnassignedPhysicalFrame)
                {
                    return bridgeStart;
                }

                AsyncProfilerCallStackStitchBoundaryKind boundary = context.ClassifyBoundary(frames[i].CodeAddress).Kind;
                bool isDispatcher = boundary == AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher || boundary == AsyncProfilerCallStackStitchBoundaryKind.V2Dispatcher;
                if (isDispatcher)
                {
                    return bridgeStart;
                }
            }

            // A full bridge keeps its scheduling anchor. A partial bridge keeps one marker
            // and leaves its remaining callers outside subsequent cleanup.
            int bridgeFrameCount = hasCompletionPrefix ? bridge.Length : dispatchIndex - bridgeStart;
            if (hasCompletionPrefix)
            {
                CollapseMachinerySpan(frames, bridgeStart, bridgeFrameCount);
            }
            else
            {
                frames.RemoveRange(bridgeStart, bridgeFrameCount);
            }

            context.Diagnostics.AsyncInfrastructureFramesCollapsed += bridgeFrameCount;
            context.Diagnostics.RuntimeResumptionBridgeFramesCollapsed += bridgeFrameCount;
            return hasCompletionPrefix ? bridgeStart : frames.Count;
        }

        #endregion
    }

    /// <summary>
    /// Shared frame checks and dispatcher-marker edits for the transform passes.
    /// </summary>
    internal static class AsyncProfilerCallStackTransformHelpers
    {
        /// <summary>
        /// Replaces a verified helper span with one dispatcher marker, keeping the first frame's identity and ownership.
        /// </summary>
        /// <returns>The number of removed list entries; one entry remains as the dispatcher marker.</returns>
        internal static int CollapseMachinerySpan(List<AsyncProfilerStitchedFrame> frames, int start, int count)
        {
            if (count <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            frames[start] = frames[start].WithPresentation(AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames);
            frames.RemoveRange(start + 1, count - 1);
            return count - 1;
        }

        /// <summary>
        /// Checks that physical frames share a call-stack context and runtime profile.
        /// Also requires the same resolved runtime owner when trace metadata is available.
        /// </summary>
        internal static bool HasSameMachineryOwner(AsyncProfilerCallStackTransformState context, int start, int count)
        {
            AsyncProfilerStitchedFrame first = context.Frames[start];
            AsyncProfilerRuntimeRuleProfile profile = context.GetRuntimeProfile(first.CodeAddress);
            ModuleFileIndex owner = context.GetHostModuleOwner(first.CodeAddress);
            for (int i = start; i < start + count; i++)
            {
                AsyncProfilerStitchedFrame frame = context.Frames[i];
                bool hasMatchingPhysicalContext =
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                    ReferenceEquals(first.Context, frame.Context);
                bool hasMatchingRuntimeProfile =
                    hasMatchingPhysicalContext &&
                    ReferenceEquals(profile, context.GetRuntimeProfile(frame.CodeAddress));
                if (!hasMatchingRuntimeProfile)
                {
                    return false;
                }

                // Without trace metadata, only the context and profile can be checked.
                if (context.TraceLog != null)
                {
                    bool hasMatchingRuntimeOwner =
                        owner != ModuleFileIndex.Invalid &&
                        owner == context.GetHostModuleOwner(frame.CodeAddress);
                    if (!hasMatchingRuntimeOwner)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Checks for a verified current method or dispatch target in the call stack's contiguous frame span.
        /// </summary>
        internal static bool HasVerifiedAsyncAnchor(AsyncProfilerCallStackTransformState context,
            int contextEnd, AsyncProfilerCallStack callStack)
        {
            List<AsyncProfilerStitchedFrame> frames = context.Frames;
            bool hasVerifiedAnchor = false;

            // Scan the full span: a completed V1 body invalidates any anchor already found.
            for (int i = contextEnd; i >= 0 && ReferenceEquals(frames[i].Context, callStack); i--)
            {
                AsyncProfilerStitchedFrame frame = frames[i];
                if (frame.Origin == AsyncProfilerStitchedFrameOrigin.V1CompletedBody)
                {
                    return false;
                }

                bool hasResolvedCurrentMethod =
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent &&
                    frame.Method != MethodIndex.Invalid && frame.CodeAddress != CodeAddressIndex.Invalid;
                hasVerifiedAnchor |= hasResolvedCurrentMethod;
                if (frame.Origin != AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget)
                {
                    continue;
                }

                // Dispatch targets need a resolved captured method, not just a physical helper address.
                CodeAddressIndex targetAddress = frame.Segment.CodeAddressAt(frame.SegmentFrameIndex);
                bool hasResolvedDispatchTarget =
                    targetAddress != CodeAddressIndex.Invalid &&
                    context.TraceLog.CodeAddresses.MethodIndex(targetAddress) != MethodIndex.Invalid;
                if (!hasResolvedDispatchTarget)
                {
                    continue;
                }

                if (callStack.Frames.Kind == AsyncCallstackKind.RuntimeAsync)
                {
                    // V2 identifies the target directly through its captured native address.
                    hasVerifiedAnchor = true;
                }
                else if (frame.IsStateMachineBoxCarrier)
                {
                    // A V1 box must also be a resolved runtime frame.
                    hasVerifiedAnchor |= IsResolvedRuntimeSpan(context, frames, i, 1);
                }
                else
                {
                    // Other V1 targets need matching completion progress and an adjacent owned dispatcher.
                    bool hasMatchingCompletionProgress =
                        context.TraceLog.AsyncProfilerCallStacks?.MethodCompletionObserved(callStack) == true &&
                        callStack.GetCompletedFrameCount(context.TimestampQpc) == frame.SegmentFrameIndex;
                    bool hasMatchingDispatcherContext =
                        hasMatchingCompletionProgress && i > 0 &&
                        frames[i - 1].Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                        ReferenceEquals(frames[i - 1].Context, callStack);
                    bool hasPrecedingDispatcher =
                        hasMatchingDispatcherContext &&
                        context.ClassifyBoundary(frames[i - 1].CodeAddress).Kind == AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher;
                    if (hasPrecedingDispatcher)
                    {
                        hasVerifiedAnchor |= IsResolvedRuntimeSpan(context, frames, i - 1, 1);
                    }
                }
            }

            return hasVerifiedAnchor;
        }

        /// <summary>
        /// Checks that every frame has a resolved method and the same runtime owner and profile.
        /// </summary>
        internal static bool IsResolvedRuntimeSpan(AsyncProfilerCallStackTransformState context,
            List<AsyncProfilerStitchedFrame> frames, int start, int count)
        {
            if (context.TraceLog == null)
            {
                return false;
            }

            ModuleFileIndex owner = context.GetHostModuleOwner(frames[start].CodeAddress);
            AsyncProfilerRuntimeRuleProfile profile = context.GetRuntimeProfile(frames[start].CodeAddress);
            int end = start + count;
            for (int i = start; i < end; i++)
            {
                CodeAddressIndex codeAddressIndex = frames[i].CodeAddress;
                bool hasResolvedRuntimeFrame =
                    frames[i].Method != MethodIndex.Invalid &&
                    codeAddressIndex != CodeAddressIndex.Invalid &&
                    owner != ModuleFileIndex.Invalid;
                bool hasMatchingRuntime =
                    hasResolvedRuntimeFrame &&
                    context.GetHostModuleOwner(codeAddressIndex) == owner &&
                    ReferenceEquals(context.GetRuntimeProfile(codeAddressIndex), profile);
                if (!hasMatchingRuntime)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Checks that a physical frame has the requested classification.</summary>
        internal static bool IsSyncFrameKind(AsyncProfilerStitchedFrame frame, AsyncProfilerStitchSyncFrameKind kind) =>
            frame.Origin == AsyncProfilerStitchedFrameOrigin.Sync && frame.SyncFrameKind == kind;
    }
}
