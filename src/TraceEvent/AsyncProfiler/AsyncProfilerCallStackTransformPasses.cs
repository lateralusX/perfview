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
    /// Removes a sampled-leaf V2 continuation wrapper when structural stitching selected a V2 synchronous layout
    /// or placed a verified dispatch target immediately beneath that wrapper in the same activation.
    /// Only the first synchronous frame is removed; logical targets, async callers, and dispatch infrastructure are unchanged.
    /// </summary>
    internal sealed class AsyncProfilerSampledV2WrapperTransform
    {
        public void Apply(AsyncProfilerCallStackTransformState context)
        {
            List<AsyncProfilerStitchedFrame> frames = context.Frames;

            // Known targets normally consume their wrapper structurally. Normalize any retained marker
            // in a fallback or caller-assembled layout without changing its logical target.
            if (frames.Count != 0 &&
                frames[0].Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                context.ClassifyBoundary(frames[0].CodeAddress).Kind ==
                    AsyncProfilerCallStackStitchBoundaryKind.V2Dispatcher &&
                (context.Diagnostics.V2SyncLayoutUsed != 0 ||
                 (frames.Count > 1 &&
                  frames[1].Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget &&
                  frames[0].Context != null &&
                  ReferenceEquals(frames[0].Context, frames[1].Context))))
            {
                frames.RemoveAt(0);
                context.Diagnostics.V2LeafWrapperDropped++;
            }
        }
    }

    /// <summary>
    /// Normalizes V1 startup, reentrant, and completed-return frames, and verified completed-V2 dispatch tails
    /// beneath active V1 methods.
    /// </summary>
    /// <remarks>
    /// Runtime profiles supply helper layouts; this pass verifies method identity, activation ownership, and
    /// completion evidence before folding them. Unverified physical work and pending logical callers remain.
    /// A dispatch target or completed logical root does not imply physical application-body execution.
    /// </remarks>
    internal sealed class AsyncProfilerV1PresentationTransform
    {
        public void Apply(AsyncProfilerCallStackTransformState context)
        {
            List<AsyncProfilerStitchedFrame> frames = context.Frames;
            ResolveRecursiveV1Completion(context);
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
                            CollapseV2CompletionHandoff(context, i);
                        }
                        TryApplyReentrantV1Deduplication(context, i);
                    }
                    continue;
                }

                if (frame.Origin != AsyncProfilerStitchedFrameOrigin.Sync ||
                    frame.SyncFrameKind != AsyncProfilerStitchSyncFrameKind.V1StateMachineBody)
                {
                    continue;
                }

                AsyncProfilerRuntimeSequenceMatch startup = context.MatchRuntimeSequence(
                    AsyncProfilerRuntimeSequenceKind.V1Startup, i, frames.Count);
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

        private static void ResolveRecursiveV1Completion(AsyncProfilerCallStackTransformState context)
        {
            if (context.TraceLog == null || context.Segments.Count != 1 ||
                context.Diagnostics.SegmentsProcessed != 1 ||
                context.Diagnostics.BoundariesNotFound != 0 || context.Diagnostics.AdjacencyMismatches != 0 ||
                context.Diagnostics.V1CompletionAlignmentAmbiguous != 1)
            {
                return;
            }
            AsyncProfilerCallStack activation = context.Segments[0];
            if (activation.Frames.Kind != AsyncCallstackKind.StateMachineAsync ||
                activation.Frames.FrameCount < 2 ||
                context.TraceLog.AsyncProfilerCallStacks?.MethodCompletionObserved(activation) != true ||
                activation.GetMethodCompletedFrameCount(context.TimestampQpc) != 1 ||
                activation.GetExceptionCompletedFrameCount(context.TimestampQpc) != 0)
            {
                return;
            }
            MethodIndex method = context.GetMethod(activation.Frames.CodeAddressAt(0));
            if (method == MethodIndex.Invalid || context.GetMethod(activation.Frames.CodeAddressAt(1)) != method)
            {
                return;
            }

            List<AsyncProfilerStitchedFrame> frames = context.Frames;
            int target = frames.FindIndex(frame =>
                frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget &&
                frame.ContextKind == AsyncProfilerStitchedFrameContextKind.Active &&
                ReferenceEquals(frame.Context, activation) && frame.SegmentFrameIndex == 1 &&
                !frame.IsStateMachineBoxCarrier);
            if (target < 2 ||
                context.ClassifyBoundary(frames[target - 1].CodeAddress).Kind !=
                    AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher)
            {
                return;
            }
            ModuleFileIndex owner = context.GetHostModuleOwner(frames[target - 1].CodeAddress);
            AsyncProfilerRuntimeRuleProfile profile = context.GetRuntimeProfile(frames[target - 1].CodeAddress);
            bool Owned(int index)
            {
                AsyncProfilerStitchedFrame frame = frames[index];
                return frame.Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                    frame.ContextKind == AsyncProfilerStitchedFrameContextKind.Active &&
                    ReferenceEquals(frame.Context, activation) &&
                    frame.CodeAddress != CodeAddressIndex.Invalid && frame.Method != MethodIndex.Invalid &&
                    owner != ModuleFileIndex.Invalid && context.GetHostModuleOwner(frame.CodeAddress) == owner &&
                    ReferenceEquals(context.GetRuntimeProfile(frame.CodeAddress), profile);
            }
            if (!Owned(target - 1))
            {
                return;
            }

            int body = target - 2;
            bool returnBox = false;
            for (; body >= 0 && frames[body].SyncFrameKind != AsyncProfilerStitchSyncFrameKind.V1StateMachineBody; body--)
            {
                AsyncProfilerStitchedFrame frame = frames[body];
                if (!Owned(body) ||
                    (!profile.IsV1CompletionUnwindInfrastructure(frame, context.ClassifyBoundary(frame.CodeAddress).Kind) &&
                     frame.SyncFrameKind != AsyncProfilerStitchSyncFrameKind.V1BoxCallback) ||
                    (frame.BoxedStateMachineMethod != MethodIndex.Invalid && frame.BoxedStateMachineMethod != method))
                {
                    return;
                }
                returnBox |= frame.BoxedStateMachineMethod == method;
            }
            if (!returnBox || body < 1 || frames[body].Origin != AsyncProfilerStitchedFrameOrigin.Sync ||
                frames[body].ContextKind != AsyncProfilerStitchedFrameContextKind.Active ||
                !ReferenceEquals(frames[body].Context, activation) ||
                frames[body].CodeAddress == CodeAddressIndex.Invalid || frames[body].Method != method ||
                !Owned(body - 1) ||
                frames[body - 1].SyncFrameKind != AsyncProfilerStitchSyncFrameKind.V1MethodCompletion)
            {
                return;
            }

            int entry = FindV1CompletionPairBeforeBody(context, 0, body);
            if (entry < 0)
            {
                return;
            }
            for (int i = entry; i < body - 1; i++)
            {
                if (!Owned(i))
                {
                    return;
                }
            }
            while (entry > 0 &&
                (frames[entry - 1].SyncFrameKind == AsyncProfilerStitchSyncFrameKind.AwaiterHelper ||
                 frames[entry - 1].SyncFrameKind == AsyncProfilerStitchSyncFrameKind.ContinuationBridge))
            {
                if (!Owned(--entry))
                {
                    return;
                }
            }
            int boxesEnd = entry;
            while (entry > 0 && frames[entry - 1].BoxedStateMachineMethod != MethodIndex.Invalid)
            {
                AsyncProfilerStitchedFrame box = frames[--entry];
                if (!Owned(entry) || box.BoxedStateMachineMethod != method ||
                    box.SyncFrameKind != AsyncProfilerStitchSyncFrameKind.ProfilerHelper ||
                    !profile.IsProfilerDispatchInfrastructure(box.SyncClassification))
                {
                    return;
                }
            }
            if (entry == boxesEnd)
            {
                return;
            }
            for (int i = 0; i < entry; i++)
            {
                if (frames[i].SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1StateMachineBody ||
                    frames[i].BoxedStateMachineMethod != MethodIndex.Invalid)
                {
                    return;
                }
            }

            // Only frame zero can have completed. Two or more completions cannot identify a surviving recursive copy.
            frames[body] = AsyncProfilerStitchedFrame.CreateV1CompletedBody(
                frames[body].CodeAddress, frames[body].Method, activation, 0);
            int removed = target - body - 1;
            frames.RemoveRange(body + 1, removed);
            context.Diagnostics.V1CompletionAlignmentAmbiguous--;
            context.Diagnostics.V1CompletionTailUsed++;
            context.Diagnostics.V1InfrastructureFramesCollapsed += removed;
        }

        private static int FindV1CompletionPairBeforeBody(
            AsyncProfilerCallStackTransformState context, int start, int body)
        {
            for (int i = body - 2; i >= start; i--)
            {
                AsyncProfilerRuntimeSequenceMatch pair = context.MatchRuntimeSequence(
                    AsyncProfilerRuntimeSequenceKind.TaskCompletion, i, body - 1);
                if (pair.IsRejected)
                {
                    return -1;
                }
                if (pair.IsMatch && i + pair.Length == body - 1)
                {
                    return context.MatchRuntimeSequence(
                        AsyncProfilerRuntimeSequenceKind.TaskCompletionChain, i, body).IsRejected ? -1 : i;
                }
            }
            return -1;
        }

        private static void CollapseCompletedV1Unwinds(AsyncProfilerCallStackTransformState context)
        {
            if (context.TraceLog == null)
            {
                return;
            }

            List<AsyncProfilerStitchedFrame> frames = context.Frames;
            for (int start = 0; start < frames.Count;)
            {
                AsyncProfilerCallStack activation = frames[start].Context;
                int end = start + 1;
                while (end < frames.Count && ReferenceEquals(frames[end].Context, activation))
                {
                    end++;
                }
                if (activation != null &&
                    activation.Frames.Kind == AsyncCallstackKind.StateMachineAsync &&
                    context.TraceLog.AsyncProfilerCallStacks?.MethodCompletionObserved(activation) == true &&
                    activation.GetMethodCompletedFrameCount(context.TimestampQpc) > 0)
                {
                    int completed = activation.GetCompletedFrameCount(context.TimestampQpc);
                    bool fullyCompleted = completed == activation.Frames.FrameCount;
                    bool incompleteRoot = end == frames.Count && context.Segments.Count != 0 &&
                        ReferenceEquals(context.Segments[context.Segments.Count - 1], activation);
                    int physicalEnd = start;
                    while (physicalEnd < end && frames[physicalEnd].Origin == AsyncProfilerStitchedFrameOrigin.Sync)
                    {
                        physicalEnd++;
                    }
                    bool partialReturn = incompleteRoot && completed > 0 && completed < activation.Frames.FrameCount &&
                        HasRemainingV1Callers(frames, physicalEnd, end, completed + 1, activation.Frames.FrameCount);
                    int unwindStart = start;
                    int completingPos = -1;
                    bool dispatcherTail = fullyCompleted && physicalEnd == end &&
                        TryMatchFullyCompletedV1DispatcherTail(context, start, end, activation, out unwindStart);
                    bool recursiveCohort = partialReturn &&
                        TryMatchRecursiveV1Unwind(
                            context, start, physicalEnd, activation, completed, preserveLeaf: true, out unwindStart);
                    if (dispatcherTail || recursiveCohort ||
                        ((fullyCompleted && physicalEnd == end || partialReturn) &&
                         TryMatchCompletedV1Unwind(
                             context, start, physicalEnd, activation, completed, out unwindStart, out completingPos)))
                    {
                        AsyncProfilerStitchedFrame completionFrame = recursiveCohort
                            ? AsyncProfilerStitchedFrame.CreateSynthetic("[Async completion unwind]", activation)
                            : (fullyCompleted
                                ? AsyncProfilerStitchedFrame.CreateV1CompletionReturn(activation, dispatcherTail)
                                : AsyncProfilerStitchedFrame.CreateV1CompletedBody(
                                    frames[completingPos].CodeAddress, frames[completingPos].Method, activation, completed - 1));
                        int removed = physicalEnd - unwindStart;
                        int retainedCallers = end - physicalEnd;
                        bool retainMachinery = !recursiveCohort && unwindStart > start &&
                            frames[unwindStart].SyncFrameKind != AsyncProfilerStitchSyncFrameKind.V1StateMachineBody;
                        if (retainMachinery)
                        {
                            CollapseMachinerySpan(frames, unwindStart, removed);
                            unwindStart++;
                        }
                        else
                        {
                            frames.RemoveRange(unwindStart, removed);
                        }
                        frames.Insert(unwindStart, completionFrame);
                        end = unwindStart + 1 + retainedCallers;
                        if (!fullyCompleted)
                        {
                            // Without the dispatcher or a carrier, the next captured method is ancestry,
                            // not a verified dispatch target or a physically running body.
                            frames.Insert(unwindStart + 1, AsyncProfilerStitchedFrame.CreateAsync(activation, completed));
                            end++;
                        }
                        context.Diagnostics.V1CompletionUnwindFramesCollapsed += removed;
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

        private static bool HasRemainingV1Callers(
            List<AsyncProfilerStitchedFrame> frames, int start, int end, int next, int frameCount)
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

        private static bool TryMatchCompletedV1Unwind(
            AsyncProfilerCallStackTransformState context, int start, int end, AsyncProfilerCallStack activation,
            int completed, out int unwindStart, out int completingPos)
        {
            unwindStart = start;
            completingPos = -1;
            if (completed == activation.Frames.FrameCount &&
                TryMatchRecursiveV1Unwind(context, start, end, activation, completed, preserveLeaf: false, out unwindStart))
            {
                return true;
            }
            unwindStart = start;
            TraceLog log = context.TraceLog;
            TraceMethods methods = log.CodeAddresses.Methods;
            AsyncProfilerCallStackFrames captured = activation.Frames;
            var capturedMethods = new MethodIndex[captured.FrameCount];
            for (int i = 0; i < capturedMethods.Length; i++)
            {
                CodeAddressIndex address = captured.CodeAddressAt(i);
                MethodIndex method = address == CodeAddressIndex.Invalid
                    ? MethodIndex.Invalid : log.CodeAddresses.MethodIndex(address);
                if (method == MethodIndex.Invalid ||
                    (i == capturedMethods.Length - 1 &&
                     !AsyncProfilerMethodNames.TryGetLogicalStateMachineMethodName(
                         AsyncProfilerMethodNames.NormalizeMethodName(log.CodeAddresses[address].FullMethodName), out _)))
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
            AsyncProfilerRuntimeRuleProfile runtimeProfile = null;
            for (int i = start; i < end; i++)
            {
                AsyncProfilerStitchedFrame frame = context.Frames[i];
                if (frame.Origin != AsyncProfilerStitchedFrameOrigin.Sync)
                {
                    return false;
                }
                bool body = frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1StateMachineBody;
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
                if (capturedIndex >= completed)
                {
                    return false;
                }

                AsyncProfilerRuntimeRuleProfile profile = context.GetRuntimeProfile(frame.CodeAddress);
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
                    ModuleFileIndex moduleIndex = context.GetHostModuleOwner(frame.CodeAddress);
                    if (moduleIndex == ModuleFileIndex.Invalid)
                    {
                        return false;
                    }
                    if (runtimeModule != ModuleFileIndex.Invalid &&
                        (runtimeModule != moduleIndex || !ReferenceEquals(runtimeProfile, profile)))
                    {
                        return false;
                    }
                    runtimeModule = moduleIndex;
                    runtimeProfile = profile;
                    hasCompletion |= frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1MethodCompletion;
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
                    if (capturedIndex == completed - 1)
                    {
                        completingPos = i;
                    }
                }
            }
            return identities >= 2 && hasBody && hasBox && hasCompletion &&
                (completed == captured.FrameCount || completingPos >= 0);
        }

        private static bool TryMatchFullyCompletedV1DispatcherTail(
            AsyncProfilerCallStackTransformState context, int start, int end, AsyncProfilerCallStack activation,
            out int unwindStart)
        {
            unwindStart = end;
            AsyncProfilerCallStackFrames captured = activation.Frames;
            if (start == end || captured.FrameCount == 0 ||
                activation.GetMethodCompletedFrameCount(context.TimestampQpc) != captured.FrameCount ||
                activation.GetExceptionCompletedFrameCount(context.TimestampQpc) != 0 ||
                context.Diagnostics.SegmentsProcessed != context.Segments.Count ||
                context.Diagnostics.BoundariesNotFound != 0 || context.Diagnostics.AdjacencyMismatches != 0 ||
                context.Diagnostics.V1CompletionAlignmentAmbiguous != 0)
            {
                return false;
            }
            MethodIndex leaf = context.GetMethod(captured.CodeAddressAt(0));
            CodeAddressIndex root = captured.CodeAddressAt(captured.FrameCount - 1);
            AsyncProfilerStitchedFrame tail = context.Frames[end - 1];
            if (leaf == MethodIndex.Invalid || context.GetMethod(root) == MethodIndex.Invalid ||
                !AsyncProfilerMethodNames.TryGetLogicalStateMachineMethodName(
                    AsyncProfilerMethodNames.NormalizeMethodName(
                        context.TraceLog.CodeAddresses[root].FullMethodName), out _) ||
                !context.HasVerifiedDispatchEntry(activation, tail.CodeAddress, requireAsyncAnchor: false))
            {
                return false;
            }
            ModuleFileIndex owner = context.GetHostModuleOwner(tail.CodeAddress);
            AsyncProfilerRuntimeRuleProfile profile = context.GetRuntimeProfile(tail.CodeAddress);
            while (unwindStart > start &&
                context.Frames[unwindStart - 1].BoxedStateMachineMethod != MethodIndex.Invalid)
            {
                AsyncProfilerStitchedFrame frame = context.Frames[--unwindStart];
                if (frame.Origin != AsyncProfilerStitchedFrameOrigin.Sync ||
                    frame.ContextKind != AsyncProfilerStitchedFrameContextKind.Active ||
                    !ReferenceEquals(frame.Context, activation) ||
                    frame.Method == MethodIndex.Invalid || frame.BoxedStateMachineMethod != leaf ||
                    frame.SyncFrameKind != AsyncProfilerStitchSyncFrameKind.ProfilerHelper ||
                    owner == ModuleFileIndex.Invalid || context.GetHostModuleOwner(frame.CodeAddress) != owner ||
                    !ReferenceEquals(context.GetRuntimeProfile(frame.CodeAddress), profile) ||
                    !profile.IsV1CompletionUnwindInfrastructure(frame, context.ClassifyBoundary(frame.CodeAddress).Kind))
                {
                    return false;
                }
            }
            if (unwindStart == end)
            {
                return false;
            }
            for (int i = start; i < unwindStart; i++)
            {
                AsyncProfilerStitchedFrame frame = context.Frames[i];
                AsyncProfilerCallStackStitchBoundaryInfo boundary = context.ClassifyBoundary(frame.CodeAddress);
                if (frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1StateMachineBody ||
                    frame.BoxedStateMachineMethod != MethodIndex.Invalid || boundary.IsDispatchMarker ||
                    boundary.Kind == AsyncProfilerCallStackStitchBoundaryKind.V1DispatcherInfrastructure)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TryMatchRecursiveV1Unwind(
            AsyncProfilerCallStackTransformState context, int start, int end,
            AsyncProfilerCallStack activation, int completed, bool preserveLeaf, out int unwindStart)
        {
            unwindStart = start;
            AsyncProfilerCallStackFrames captured = activation.Frames;
            if (captured.FrameCount < 2 || completed < 2 || completed > captured.FrameCount ||
                activation.GetMethodCompletedFrameCount(context.TimestampQpc) != completed ||
                activation.GetExceptionCompletedFrameCount(context.TimestampQpc) != 0)
            {
                return false;
            }
            MethodIndex method = context.GetMethod(captured.CodeAddressAt(0));
            for (int i = 0; i < captured.FrameCount; i++)
            {
                if (method == MethodIndex.Invalid || context.GetMethod(captured.CodeAddressAt(i)) != method)
                {
                    return false;
                }
            }
            if (!AsyncProfilerMethodNames.TryGetLogicalStateMachineMethodName(
                AsyncProfilerMethodNames.NormalizeMethodName(
                    context.TraceLog.CodeAddresses[captured.CodeAddressAt(captured.FrameCount - 1)].FullMethodName), out _))
            {
                return false;
            }

            int bodies = 0;
            bool entryBox = false;
            ModuleFileIndex runtimeModule = ModuleFileIndex.Invalid;
            AsyncProfilerRuntimeRuleProfile runtimeProfile = null;
            for (int i = start; i < end; i++)
            {
                AsyncProfilerStitchedFrame frame = context.Frames[i];
                if (frame.Origin != AsyncProfilerStitchedFrameOrigin.Sync ||
                    frame.ContextKind != AsyncProfilerStitchedFrameContextKind.Active ||
                    !ReferenceEquals(frame.Context, activation))
                {
                    return false;
                }
                bool body = frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1StateMachineBody;
                AsyncProfilerRuntimeRuleProfile profile = context.GetRuntimeProfile(frame.CodeAddress);
                bool infrastructure = profile.IsV1CompletionUnwindInfrastructure(
                    frame, context.ClassifyBoundary(frame.CodeAddress).Kind);
                bool box = frame.BoxedStateMachineMethod != MethodIndex.Invalid;
                if (frame.CodeAddress == CodeAddressIndex.Invalid || frame.Method == MethodIndex.Invalid ||
                    (body ? frame.Method != method : !infrastructure || (box && frame.BoxedStateMachineMethod != method)))
                {
                    if (bodies != 0)
                    {
                        return false;
                    }
                    unwindStart = i + 1;
                    runtimeModule = ModuleFileIndex.Invalid;
                    runtimeProfile = null;
                    entryBox = false;
                    continue;
                }
                if (body)
                {
                    if (preserveLeaf && bodies == 0)
                    {
                        // The youngest physical copy need not have completed. Keep it and all its callees;
                        // only older copies with their own completion pairs can join the return cohort.
                        if (i <= unwindStart ||
                            context.Frames[i - 1].SyncFrameKind != AsyncProfilerStitchSyncFrameKind.V1MethodCompletion)
                        {
                            return false;
                        }
                        bodies++;
                        unwindStart = i + 1;
                        entryBox = false;
                        continue;
                    }
                    // Match completed copies as a cohort, without inferring their individual captured slots.
                    if (!entryBox || i <= unwindStart ||
                        context.Frames[i - 1].SyncFrameKind != AsyncProfilerStitchSyncFrameKind.V1MethodCompletion ||
                        FindV1CompletionPairBeforeBody(context, unwindStart, i) < 0 ||
                        ++bodies > completed + (preserveLeaf ? 1 : 0))
                    {
                        return false;
                    }
                    entryBox = false;
                    continue;
                }
                ModuleFileIndex owner = context.GetHostModuleOwner(frame.CodeAddress);
                if (owner == ModuleFileIndex.Invalid ||
                    (runtimeModule != ModuleFileIndex.Invalid &&
                     (owner != runtimeModule || !ReferenceEquals(profile, runtimeProfile))))
                {
                    return false;
                }
                runtimeModule = owner;
                runtimeProfile = profile;
                entryBox |= box && frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.ProfilerHelper;
            }
            return bodies >= (preserveLeaf ? 3 : 2);
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

        private static void CollapseV2CompletionHandoff(AsyncProfilerCallStackTransformState context, int currentIndex)
        {
            if (context.TraceLog == null ||
                context.Diagnostics.BoundariesNotFound != 0 ||
                context.Diagnostics.AdjacencyMismatches != 0)
            {
                return;
            }

            List<AsyncProfilerStitchedFrame> frames = context.Frames;
            AsyncProfilerStitchedFrame current = frames[currentIndex];
            if (current.Context == null ||
                current.Context.Frames.Kind != AsyncCallstackKind.StateMachineAsync ||
                current.Method == MethodIndex.Invalid ||
                current.CodeAddress == CodeAddressIndex.Invalid)
            {
                return;
            }

            int handoffStart = currentIndex + 1;
            while (handoffStart < frames.Count &&
                   frames[handoffStart].Origin == AsyncProfilerStitchedFrameOrigin.AsyncCaller &&
                   ReferenceEquals(frames[handoffStart].Context, current.Context))
            {
                handoffStart++;
            }

            AsyncProfilerRuntimeSequenceMatch handoff = context.MatchRuntimeSequence(
                AsyncProfilerRuntimeSequenceKind.V2CompletionHandoff, handoffStart, frames.Count);
            int handoffCount = handoff.Length;
            if (!handoff.IsMatch || handoffCount >= frames.Count - handoffStart)
            {
                return;
            }

            bool unassignedSyncTail = true;
            for (int i = handoffStart; i <= handoffStart + handoffCount; i++)
            {
                if (frames[i].Origin != AsyncProfilerStitchedFrameOrigin.Sync || frames[i].Context != null)
                {
                    unassignedSyncTail = false;
                    break;
                }
            }
            if (!unassignedSyncTail)
            {
                return;
            }

            AsyncProfilerStitchedFrame caller = frames[handoffStart + handoffCount];
            if (caller.Method == MethodIndex.Invalid ||
                caller.CodeAddress == CodeAddressIndex.Invalid ||
                context.ClassifyBoundary(caller.CodeAddress).Kind != AsyncProfilerCallStackStitchBoundaryKind.None)
            {
                return;
            }

            // The completed V2 dispatcher is physical only; never consume a bridge belonging to a live segment.
            frames.RemoveRange(handoffStart, handoffCount);
            context.Diagnostics.V2CompletionHandoffFramesCollapsed += handoffCount;
        }

        private static bool TryApplyReentrantV1Deduplication(
            AsyncProfilerCallStackTransformState context,
            int currentIndex)
        {
            if (context.TraceLog == null)
            {
                return false;
            }

            List<AsyncProfilerStitchedFrame> frames = context.Frames;
            AsyncProfilerStitchedFrame current = frames[currentIndex];
            if (current.Segment == null ||
                current.Segment.Kind != AsyncCallstackKind.StateMachineAsync)
            {
                return false;
            }
            bool dispatchOnly = current.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget &&
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
            AsyncProfilerRuntimeSequenceMatch registration = context.MatchRuntimeSequence(
                AsyncProfilerRuntimeSequenceKind.V1AwaitRegistration, registrationStart, frames.Count);
            int startupMoveNextIndex = registrationStart + registration.Length;
            if (!registration.IsMatch || startupMoveNextIndex >= frames.Count)
            {
                return false;
            }

            AsyncProfilerStitchedFrame startupMoveNext = frames[startupMoveNextIndex];
            if (startupMoveNext.Origin != AsyncProfilerStitchedFrameOrigin.Sync ||
                startupMoveNext.SyncFrameKind != AsyncProfilerStitchSyncFrameKind.V1StateMachineBody ||
                startupMoveNext.CodeAddress == CodeAddressIndex.Invalid ||
                (dispatchOnly
                    ? context.TraceLog.CodeAddresses.MethodIndex(startupMoveNext.CodeAddress)
                    : startupMoveNext.Method) != currentMethod)
            {
                return false;
            }

            AsyncProfilerRuntimeSequenceMatch startup = context.MatchRuntimeSequence(
                AsyncProfilerRuntimeSequenceKind.V1Startup, startupMoveNextIndex, frames.Count);
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
            AsyncProfilerCallStackTransformState context, int targetIndex, int kickoffIndex, MethodIndex startupMethod)
        {
            if (targetIndex == 0 || kickoffIndex + 1 >= context.Frames.Count ||
                context.Diagnostics.SegmentsProcessed != context.Segments.Count ||
                context.Diagnostics.BoundariesNotFound != 0 ||
                context.Diagnostics.AdjacencyMismatches != 0 ||
                context.Diagnostics.V1CompletionAlignmentAmbiguous != 0)
            {
                return false;
            }

            List<AsyncProfilerStitchedFrame> frames = context.Frames;
            AsyncProfilerStitchedFrame target = frames[targetIndex];
            AsyncProfilerStitchedFrame dispatcher = frames[targetIndex - 1];
            AsyncProfilerStitchedFrame outer = frames[kickoffIndex + 1];
            if (target.Context == null ||
                dispatcher.Origin != AsyncProfilerStitchedFrameOrigin.Sync ||
                !ReferenceEquals(dispatcher.Context, target.Context) ||
                context.ClassifyBoundary(dispatcher.CodeAddress).Kind != AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher ||
                !IsSystemPrivateCoreLibSpan(context, frames, targetIndex - 1, 1) ||
                outer.Origin != AsyncProfilerStitchedFrameOrigin.AsyncCurrent ||
                outer.Context == null || outer.CodeAddress == CodeAddressIndex.Invalid ||
                outer.Method == MethodIndex.Invalid || outer.Method == startupMethod ||
                target.Context.Depth != outer.Context.Depth + 1)
            {
                return false;
            }

            int matchingBodies = 0;
            for (int i = targetIndex + 1; i <= kickoffIndex; i++)
            {
                AsyncProfilerStitchedFrame frame = frames[i];
                if (frame.Origin != AsyncProfilerStitchedFrameOrigin.Sync ||
                    frame.Method == MethodIndex.Invalid ||
                    !ReferenceEquals(frame.Context, outer.Context) ||
                    context.ClassifyBoundary(frame.CodeAddress).IsDispatchMarker)
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
            AsyncProfilerStitchedFrame stateMachineMoveNext,
            AsyncProfilerStitchedFrame kickoff)
        {
            if (kickoff.Origin != AsyncProfilerStitchedFrameOrigin.Sync ||
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
                !AsyncProfilerMethodNames.TryGetLogicalStateMachineMethodName(
                    AsyncProfilerMethodNames.NormalizeMethodName(moveNextCodeAddress.FullMethodName),
                    out string logicalMethod))
            {
                return false;
            }

            string kickoffMethod = AsyncProfilerMethodNames.NormalizeMethodName(kickoffCodeAddress.FullMethodName);
            if (string.IsNullOrEmpty(kickoffMethod))
            {
                return false;
            }

            kickoffMethod = AsyncProfilerMethodNames.GetMethodNameWithoutParameters(kickoffMethod);

            return string.Equals(
                logicalMethod,
                kickoffMethod.Replace('+', '.'),
                StringComparison.Ordinal);
        }

        #endregion
    }

    /// <summary>
    /// Groups verified V1/V2 CoreLib machinery into dispatcher markers and folds owned resumption bridges.
    /// </summary>
    /// <remarks>
    /// Opt-in because helper layouts are runtime implementation details, not profiler contracts.
    /// Bridge and prefix folds require consistent activation and captured-runtime ownership. Rejected candidates and
    /// unverified callees stay protected from later helper cleanup; all rules here must share those bounds.
    /// Markers retain physical identity and attribute machinery cost, not application-body execution.
    /// </remarks>
    internal sealed class AsyncProfilerInfrastructureCleanupTransform
    {
        public void Apply(AsyncProfilerCallStackTransformState context)
        {
            List<AsyncProfilerStitchedFrame> frames = context.Frames;
            CollapseProfilerDispatchSpan(context);
            int cleanupEnd = CollapseRuntimeResumptionBridge(context);
            int cleanupStart = CollapseProfilerOwnedPrefix(context, ref cleanupEnd);
            cleanupStart = CollapseBoundedAwaitRegistration(context, cleanupStart, ref cleanupEnd);
            List<(int Start, int End)> rejectedSpans = null;

            // Group the public SetResult trigger only as part of the exact inline-completion chain.
            for (int i = cleanupStart; i < cleanupEnd;)
            {
                if (frames[i].Origin != AsyncProfilerStitchedFrameOrigin.Sync)
                {
                    i++;
                    continue;
                }
                AsyncProfilerRuntimeSequenceMatch chain = context.MatchRuntimeSequence(
                    AsyncProfilerRuntimeSequenceKind.TaskCompletionChain, i, cleanupEnd);
                if (chain.IsMatch && HasSameMachineryOwner(context, i, chain.Length))
                {
                    cleanupEnd -= CollapseMachinerySpan(frames, i, chain.Length);
                    context.Diagnostics.AsyncInfrastructureFramesCollapsed += chain.Length;
                    i++;
                }
                else
                {
                    // A default pass may have rejected this tail. Recheck its layout before individual cleanup;
                    // do not turn a rejected version-specific candidate into a partially erased helper sequence.
                    AsyncProfilerRuntimeSequenceMatch handoff = context.MatchRuntimeSequence(
                        AsyncProfilerRuntimeSequenceKind.V2CompletionHandoff, i, cleanupEnd);
                    int protectedLength = Math.Max(
                        chain.IsRejected || chain.IsMatch ? chain.Length : 0,
                        handoff.IsRejected ? handoff.Length : 0);
                    AsyncProfilerRuntimeSequenceMatch startup = context.MatchRuntimeSequence(
                        AsyncProfilerRuntimeSequenceKind.V1Startup, i, cleanupEnd);
                    AsyncProfilerRuntimeSequenceMatch registration = context.MatchRuntimeSequence(
                        AsyncProfilerRuntimeSequenceKind.V1AwaitRegistration, i, cleanupEnd);
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
                        AsyncProfilerRuntimeSequenceMatch pair = context.MatchRuntimeSequence(
                            AsyncProfilerRuntimeSequenceKind.TaskCompletion, i, cleanupEnd);
                        AsyncProfilerStitchedFrame entry = frames[i];
                        if (pair.IsMatch && entry.Context != null &&
                            entry.ContextKind == AsyncProfilerStitchedFrameContextKind.Active &&
                            HasSameMachineryOwner(context, i, pair.Length) &&
                            context.HasVerifiedV1CompletionAnchor(entry.Context, entry.CodeAddress))
                        {
                            int minimumStart = rejectedSpans != null
                                ? Math.Max(cleanupStart, rejectedSpans[rejectedSpans.Count - 1].End) : cleanupStart;
                            int start = i > minimumStart && frames[i - 1].Method != MethodIndex.Invalid &&
                                IsSyncFrameKind(frames[i - 1],
                                    AsyncProfilerStitchSyncFrameKind.ContinuationBridge) &&
                                HasSameMachineryOwner(context, i - 1, pair.Length + 1) ? i - 1 : i;
                            start = FindProfilerDispatchSpanStart(context, start, frames[start], out int rejectedStart, minimumStart);
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
                            int count = i + pair.Length - start;
                            cleanupEnd -= CollapseMachinerySpan(frames, start, count);
                            context.Diagnostics.AsyncInfrastructureFramesCollapsed += count;
                            context.Diagnostics.ProfilerEnvelopeFramesCollapsed += i - start;
                            i = start + 1;
                        }
                        else
                        {
                            i++;
                        }
                    }
                }
            }

            int rejectedIndex = (rejectedSpans?.Count ?? 0) - 1;
            int mergeEnd = cleanupEnd;
            int faultSpanStart = -1;
            for (int i = cleanupEnd - 1; i >= cleanupStart; i--)
            {
                if (rejectedIndex >= 0 && i < rejectedSpans[rejectedIndex].End)
                {
                    mergeEnd = rejectedSpans[rejectedIndex].Start;
                    i = rejectedSpans[rejectedIndex--].Start;
                    continue;
                }
                AsyncProfilerStitchedFrame frame = frames[i];
                if (frame.Origin != AsyncProfilerStitchedFrameOrigin.Sync)
                {
                    continue;
                }

                bool marker = frame.Presentation == AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames;
                AsyncProfilerCallStackStitchBoundaryKind boundaryKind = context.ClassifyBoundary(frame.CodeAddress).Kind;
                int boundedStart = FindBoundedAsyncInfrastructureStart(context, i, out int rejectedStart, rejectedIndex >= 0
                    ? Math.Max(cleanupStart, rejectedSpans[rejectedIndex].End) : cleanupStart);
                if (rejectedStart >= 0)
                {
                    mergeEnd = rejectedStart;
                    i = rejectedStart;
                    continue;
                }
                if (marker || boundedStart < i ||
                    context.GetRuntimeProfile(frame.CodeAddress).IsOptionalInfrastructureFrame(frame, boundaryKind))
                {
                    int count = i + 1 < mergeEnd &&
                        frames[i + 1].Presentation == AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames &&
                        HasSameMachineryOwner(context, i, 2) ? 2 : 1;
                    bool faultEntry = frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1MethodHelper &&
                        frame.Method != MethodIndex.Invalid && i > cleanupStart &&
                        frames[i - 1].SyncFrameKind == AsyncProfilerStitchSyncFrameKind.FaultHandling;
                    bool joinsFaultSpan = count == 2 && i + 1 == faultSpanStart;
                    int start = boundedStart;
                    if (context.TraceLog != null && frame.Context != null &&
                        frame.ContextKind == AsyncProfilerStitchedFrameContextKind.Active &&
                        (marker || faultEntry || joinsFaultSpan ||
                         context.ClassifyBoundary(frame.CodeAddress).IsDispatchMarker) &&
                        context.HasVerifiedDispatchEntry(frame.Context, frame.CodeAddress, requireAsyncAnchor: false))
                    {
                        int minimumStart = rejectedIndex >= 0
                            ? Math.Max(cleanupStart, rejectedSpans[rejectedIndex].End) : cleanupStart;
                        start = FindProfilerDispatchSpanStart(context, i, frame, out rejectedStart, minimumStart);
                        if (rejectedStart >= 0)
                        {
                            mergeEnd = rejectedStart;
                            i = rejectedStart;
                            continue;
                        }
                    }
                    int helpers = i - start;
                    faultSpanStart = faultEntry || joinsFaultSpan ||
                        frames[start].SyncFrameKind == AsyncProfilerStitchSyncFrameKind.FaultHandling
                        ? start : -1;
                    int removed = CollapseMachinerySpan(frames, start, helpers + count);
                    cleanupEnd -= removed;
                    mergeEnd -= removed;
                    context.Diagnostics.AsyncInfrastructureFramesCollapsed += helpers + (marker ? 0 : 1);
                    context.Diagnostics.ProfilerEnvelopeFramesCollapsed += helpers;
                    i = start;
                }
            }
        }

        #region private

        private static int CollapseBoundedAwaitRegistration(
            AsyncProfilerCallStackTransformState context, int protectedEnd, ref int cleanupEnd)
        {
            if (context.TraceLog == null)
            {
                return protectedEnd;
            }
            List<AsyncProfilerStitchedFrame> frames = context.Frames;
            for (int start = 0; start < protectedEnd; start++)
            {
                AsyncProfilerStitchedFrame entry = frames[start];
                if (!IsSyncFrameKind(entry, AsyncProfilerStitchSyncFrameKind.V1DispatchSetup) ||
                    entry.Context == null || entry.ContextKind != AsyncProfilerStitchedFrameContextKind.Active)
                {
                    continue;
                }
                AsyncProfilerRuntimeSequenceMatch registration = context.MatchRuntimeSequence(
                    AsyncProfilerRuntimeSequenceKind.V1AwaitRegistration, start + 1, protectedEnd);
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
                if (caller.Origin != AsyncProfilerStitchedFrameOrigin.Sync ||
                    caller.ContextKind != AsyncProfilerStitchedFrameContextKind.Active ||
                    !ReferenceEquals(entry.Context, caller.Context) ||
                    caller.CodeAddress == CodeAddressIndex.Invalid || caller.Method == MethodIndex.Invalid ||
                    context.GetHostModuleOwner(caller.CodeAddress) != ModuleFileIndex.Invalid ||
                    context.ClassifyBoundary(caller.CodeAddress).IsDispatchMarker)
                {
                    continue;
                }
                AsyncProfilerRuntimeSequenceMatch match = context.MatchRuntimeSequence(
                    AsyncProfilerRuntimeSequenceKind.ProfilerEntry, start, end);
                if (!match.IsMatch || match.Length != end - start || !HasSameMachineryOwner(context, start, match.Length))
                {
                    continue;
                }
                AsyncProfilerRuntimeRuleProfile profile = context.GetRuntimeProfile(entry.CodeAddress);
                bool eligible = true;
                for (int i = start; i < end; i++)
                {
                    AsyncProfilerStitchedFrame helper = frames[i];
                    AsyncProfilerCallStackStitchBoundaryKind boundary = context.ClassifyBoundary(helper.CodeAddress).Kind;
                    if (helper.Presentation != AsyncProfilerStitchedFramePresentation.Native ||
                        helper.ContextKind != AsyncProfilerStitchedFrameContextKind.Active ||
                        boundary != AsyncProfilerCallStackStitchBoundaryKind.None ||
                        !profile.IsOptionalInfrastructureFrame(helper, boundary))
                    {
                        eligible = false;
                        break;
                    }
                }
                if (!eligible)
                {
                    continue;
                }
                int spanStart = FindProfilerDispatchSpanStart(context, start, entry, out int rejectedStart);
                if (rejectedStart >= 0)
                {
                    return protectedEnd;
                }
                int count = end - spanStart;
                cleanupEnd -= CollapseMachinerySpan(frames, spanStart, count);
                context.Diagnostics.AsyncInfrastructureFramesCollapsed += count;
                context.Diagnostics.ProfilerEnvelopeFramesCollapsed += start - spanStart;
                // The local run and contiguous same-runtime profiler types are proven. Other callees stay protected;
                // the physical caller and independent root-side helpers can use ordinary cleanup.
                return spanStart + 1;
            }
            return protectedEnd;
        }

        private static void CollapseProfilerDispatchSpan(AsyncProfilerCallStackTransformState context)
        {
            if (context.TraceLog == null)
            {
                return;
            }
            List<AsyncProfilerStitchedFrame> frames = context.Frames;
            for (int i = 0; i < frames.Count; i++)
            {
                AsyncProfilerStitchedFrame dispatcher = frames[i];
                if (dispatcher.Origin != AsyncProfilerStitchedFrameOrigin.Sync ||
                    dispatcher.ContextKind == AsyncProfilerStitchedFrameContextKind.ParentContext)
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

                bool marker = dispatcher.Presentation == AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames;
                if (marker
                    ? dispatcher.Context == null ||
                      !context.HasVerifiedV2DispatchPair(dispatcher.Context, dispatcher.CodeAddress)
                    : i + 1 >= frames.Count ||
                      frames[i + 1].Origin != AsyncProfilerStitchedFrameOrigin.Sync ||
                      frames[i + 1].Presentation != AsyncProfilerStitchedFramePresentation.Native ||
                      !ReferenceEquals(dispatcher.Context, frames[i + 1].Context) ||
                      !context.IsV2DispatchPair(dispatcher.CodeAddress, frames[i + 1].CodeAddress) ||
                      (i + 2 < frames.Count &&
                       context.ClassifyBoundary(frames[i + 2].CodeAddress).Kind ==
                           AsyncProfilerCallStackStitchBoundaryKind.V2DispatcherInfrastructure))
                {
                    return;
                }

                AsyncProfilerRuntimeRuleProfile profile = context.GetRuntimeProfile(dispatcher.CodeAddress);
                if (!profile.IsOptionalInfrastructureFrame(dispatcher, boundary) ||
                    (!marker && !profile.IsOptionalInfrastructureFrame(frames[i + 1], boundary)))
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
                    // Retain the leafmost physical identity; the marker does not imply a logical method owner.
                    CollapseMachinerySpan(frames, start, i - start + (marker ? 1 : 2));
                    context.Diagnostics.AsyncInfrastructureFramesCollapsed += i - start + (marker ? 0 : 2);
                }
                return;
            }
        }

        private static int FindProfilerDispatchSpanStart(
            AsyncProfilerCallStackTransformState context, int start, AsyncProfilerStitchedFrame entry,
            out int rejectedStart, int minimumStart = 0)
        {
            rejectedStart = -1;
            AsyncProfilerRuntimeRuleProfile profile = context.GetRuntimeProfile(entry.CodeAddress);
            ModuleFileIndex owner = context.GetHostModuleOwner(entry.CodeAddress);
            while (start > minimumStart)
            {
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
                if (helper.Origin != AsyncProfilerStitchedFrameOrigin.Sync ||
                    helper.Presentation != AsyncProfilerStitchedFramePresentation.Native ||
                    helper.Method == MethodIndex.Invalid ||
                    !ReferenceEquals(helper.Context, entry.Context) ||
                    owner == ModuleFileIndex.Invalid || owner != context.GetHostModuleOwner(helper.CodeAddress) ||
                    !ReferenceEquals(profile, context.GetRuntimeProfile(helper.CodeAddress)) ||
                    context.ClassifyBoundary(helper.CodeAddress).IsDispatchMarker ||
                    !profile.IsProfilerDispatchInfrastructure(helper.SyncClassification))
                {
                    break;
                }
                start--;
            }
            return start;
        }

        private static int FindBoundedAsyncInfrastructureStart(
            AsyncProfilerCallStackTransformState context, int root, out int rejectedStart, int minimumStart)
        {
            rejectedStart = -1;
            AsyncProfilerStitchedFrame entry = context.Frames[root];
            if (context.TraceLog == null || entry.Origin != AsyncProfilerStitchedFrameOrigin.Sync ||
                entry.CodeAddress == CodeAddressIndex.Invalid ||
                entry.Presentation != AsyncProfilerStitchedFramePresentation.Native ||
                entry.Context == null || entry.ContextKind != AsyncProfilerStitchedFrameContextKind.Active ||
                entry.Method == MethodIndex.Invalid ||
                context.Diagnostics.SegmentsProcessed != context.Segments.Count ||
                context.Diagnostics.BoundariesNotFound != 0 || context.Diagnostics.AdjacencyMismatches != 0 ||
                context.Diagnostics.V1CompletionAlignmentAmbiguous != 0)
            {
                return root;
            }
            AsyncProfilerRuntimeRuleProfile profile = context.GetRuntimeProfile(entry.CodeAddress);
            string name = AsyncProfilerMethodNames.NormalizeMethodName(
                context.TraceLog.CodeAddresses[entry.CodeAddress].FullMethodName);
            if (!profile.TryGetOptionalSequenceKind(name, entry.SyncClassification, out AsyncProfilerRuntimeSequenceKind sequence) ||
                !context.HasVerifiedDispatchEntry(entry.Context, entry.CodeAddress, requireAsyncAnchor: false))
            {
                return root;
            }
            ModuleFileIndex owner = context.GetHostModuleOwner(entry.CodeAddress);
            bool uncovered = false;
            int matchedStart = root;
            for (int start = root - 1; start >= minimumStart; start--)
            {
                AsyncProfilerStitchedFrame helper = context.Frames[start];
                AsyncProfilerCallStackStitchBoundaryKind boundary = context.ClassifyBoundary(helper.CodeAddress).Kind;
                if (helper.Origin != AsyncProfilerStitchedFrameOrigin.Sync ||
                    helper.Presentation != AsyncProfilerStitchedFramePresentation.Native ||
                    helper.ContextKind != AsyncProfilerStitchedFrameContextKind.Active ||
                    !ReferenceEquals(helper.Context, entry.Context) || helper.Method == MethodIndex.Invalid ||
                    owner == ModuleFileIndex.Invalid || context.GetHostModuleOwner(helper.CodeAddress) != owner ||
                    !ReferenceEquals(context.GetRuntimeProfile(helper.CodeAddress), profile) ||
                    context.ClassifyBoundary(helper.CodeAddress).IsDispatchMarker ||
                    helper.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1StateMachineBody ||
                    helper.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V2ResumeStub ||
                    helper.BoxedStateMachineMethod != MethodIndex.Invalid)
                {
                    break;
                }
                // Keep established helper layouts on their existing paths, including their diagnostics.
                uncovered |= !profile.IsOptionalInfrastructureFrame(helper, boundary) &&
                    !profile.IsProfilerDispatchInfrastructure(helper.SyncClassification);
                if (!uncovered)
                {
                    continue;
                }
                AsyncProfilerRuntimeSequenceMatch infrastructure = context.MatchRuntimeSequence(
                    sequence, start, root + 1, allowRuntimeInteriors: true);
                if (infrastructure.IsRejected)
                {
                    rejectedStart = start;
                    return root;
                }
                if (infrastructure.IsMatch && infrastructure.Length == root + 1 - start)
                {
                    matchedStart = start;
                }
            }
            return matchedStart;
        }

        private static int CollapseProfilerOwnedPrefix(AsyncProfilerCallStackTransformState context, ref int cleanupEnd)
        {
            if (context.TraceLog == null || context.Segments.Count == 0)
            {
                return 0;
            }

            List<AsyncProfilerStitchedFrame> frames = context.Frames;
            int targetIndex = frames.Count;
            bool candidate = false;
            for (int i = 0; i < frames.Count; i++)
            {
                AsyncProfilerStitchedFrame frame = frames[i];
                if (frame.ContextKind == AsyncProfilerStitchedFrameContextKind.Active &&
                    (frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent ||
                     frame.Origin == AsyncProfilerStitchedFrameOrigin.V1CompletedBody ||
                     frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCaller ||
                     frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget))
                {
                    targetIndex = i;
                    break;
                }
                candidate |= frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.ProfilerEntry;
                // A sampled CreateDispatcher already has individual-helper cleanup; its callees need atomic ownership.
                candidate |= i > 0 &&
                             frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1DispatchSetup;
            }
            int registrationEntry = FindV1AwaitRegistrationEntry(context, targetIndex);
            int completionEntry = FindV1CompletionEntry(context, targetIndex);
            bool completionFallbackOnly = !candidate && registrationEntry < 0 && completionEntry >= 0;
            if (!candidate && registrationEntry < 0 && completionEntry < 0)
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

            AsyncProfilerStitchedFrame target = frames[targetIndex];
            bool needsDispatcher = target.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget &&
                !target.IsStateMachineBoxCarrier;
            if (needsDispatcher)
            {
                if (target.Segment.Kind != AsyncCallstackKind.StateMachineAsync)
                {
                    return targetIndex;
                }
            }
            else if (target.Origin != AsyncProfilerStitchedFrameOrigin.AsyncCurrent &&
                     target.Origin != AsyncProfilerStitchedFrameOrigin.V1CompletedBody &&
                     !target.IsStateMachineBoxCarrier)
            {
                return targetIndex;
            }
            int entryIndex = -1;
            for (int i = targetIndex - 1; i >= 0; i--)
            {
                AsyncProfilerRuntimeSequenceMatch entry = context.MatchRuntimeSequence(
                    AsyncProfilerRuntimeSequenceKind.ProfilerEntry, i, targetIndex);
                if (entry.IsRejected)
                {
                    return targetIndex;
                }
                if (entry.IsMatch && entry.Length == targetIndex - i)
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

            int dispatcherCount = 0;
            for (int i = 0; i < targetIndex; i++)
            {
                AsyncProfilerStitchedFrame frame = frames[i];
                if (frame.Origin != AsyncProfilerStitchedFrameOrigin.Sync ||
                    frame.ContextKind != AsyncProfilerStitchedFrameContextKind.Active ||
                    !ReferenceEquals(frame.Context, target.Context))
                {
                    return targetIndex;
                }
                AsyncProfilerCallStackStitchBoundaryInfo boundary = context.ClassifyBoundary(frame.CodeAddress);
                if (boundary.IsDispatchMarker)
                {
                    if (!needsDispatcher || i < entryIndex ||
                        boundary.Kind != AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher || ++dispatcherCount != 1)
                    {
                        return targetIndex;
                    }
                }
            }
            if (needsDispatcher && dispatcherCount != 1)
            {
                return targetIndex;
            }

            int start = FindProfilerDispatchSpanStart(context, entryIndex, frames[entryIndex], out int rejectedStart);
            if (rejectedStart >= 0)
            {
                return targetIndex;
            }
            int count = targetIndex - start;
            cleanupEnd -= CollapseMachinerySpan(frames, start, count);
            context.Diagnostics.AsyncInfrastructureFramesCollapsed += count;
            context.Diagnostics.ProfilerEnvelopeFramesCollapsed += count;
            // Completion-only entries must not suppress independent helper groups above a contiguity break.
            return completionFallbackOnly ? 0 : start + 1;
        }

        private static int FindV1AwaitRegistrationEntry(
            AsyncProfilerCallStackTransformState context, int targetIndex)
        {
            if (targetIndex == context.Frames.Count)
            {
                return -1;
            }
            AsyncProfilerStitchedFrame target = context.Frames[targetIndex];
            if (target.Origin != AsyncProfilerStitchedFrameOrigin.AsyncCurrent ||
                target.Segment.Kind != AsyncCallstackKind.StateMachineAsync ||
                target.Method == MethodIndex.Invalid || target.CodeAddress == CodeAddressIndex.Invalid)
            {
                return -1;
            }

            int entry = -1;
            for (int i = targetIndex - 1; i >= 0; i--)
            {
                AsyncProfilerRuntimeSequenceMatch registration = context.MatchRuntimeSequence(
                    AsyncProfilerRuntimeSequenceKind.V1AwaitRegistration, i, targetIndex);
                if (registration.IsRejected)
                {
                    return -1;
                }
                if (!registration.IsMatch || registration.Length != targetIndex - i)
                {
                    break;
                }
                entry = i;
            }
            return entry;
        }

        private static int FindV1CompletionEntry(AsyncProfilerCallStackTransformState context, int targetIndex)
        {
            if (targetIndex < 3 || targetIndex == context.Frames.Count ||
                context.Frames[targetIndex].Origin != AsyncProfilerStitchedFrameOrigin.V1CompletedBody ||
                !IsSyncFrameKind(context.Frames[targetIndex - 1],
                    AsyncProfilerStitchSyncFrameKind.V1MethodCompletion) ||
                !context.HasVerifiedV1CompletionAnchor(
                    context.Frames[targetIndex].Context, context.Frames[targetIndex - 1].CodeAddress))
            {
                return -1;
            }
            for (int i = targetIndex - 2; i >= 0; i--)
            {
                AsyncProfilerRuntimeSequenceMatch pair = context.MatchRuntimeSequence(
                    AsyncProfilerRuntimeSequenceKind.TaskCompletion, i, targetIndex - 1);
                if (pair.IsRejected)
                {
                    return -1;
                }
                if (pair.IsMatch && pair.Length == targetIndex - 1 - i)
                {
                    if (context.MatchRuntimeSequence(
                        AsyncProfilerRuntimeSequenceKind.TaskCompletionChain, i, targetIndex).IsRejected)
                    {
                        return -1;
                    }
                    int start = i > 0 && IsSyncFrameKind(context.Frames[i - 1],
                        AsyncProfilerStitchSyncFrameKind.ContinuationBridge) ? i - 1 : i;
                    return HasSameMachineryOwner(context, start, targetIndex - start) ? start : -1;
                }
            }
            return -1;
        }

        private static int CollapseRuntimeResumptionBridge(AsyncProfilerCallStackTransformState context)
        {
            List<AsyncProfilerStitchedFrame> frames = context.Frames;
            if (context.Segments.Count == 0)
            {
                return frames.Count;
            }

            AsyncProfilerCallStack rootContext = context.Segments[context.Segments.Count - 1];
            int rootIndex = -1;
            for (int i = frames.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(frames[i].Context, rootContext) &&
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
                    if (frames[i].Origin != AsyncProfilerStitchedFrameOrigin.Sync ||
                        context.ClassifyBoundary(frames[i].CodeAddress).IsDispatchMarker)
                    {
                        bridgeStart = i + 1;
                    }
                }
            }
            int bridgeEnd = dispatchIndex < frames.Count ? dispatchIndex + 1 : frames.Count;
            AsyncProfilerRuntimeSequenceMatch bridge = context.MatchRuntimeSequence(
                AsyncProfilerRuntimeSequenceKind.RuntimeResumptionBridge, bridgeStart, bridgeEnd);
            if (bridge.Length == 0)
            {
                return frames.Count;
            }

            int matchedEnd = bridgeStart + bridge.Length;
            bool completionPrefix = matchedEnd < bridgeEnd;
            bool completedOuter = !rootPlaced && bridge.IsMatch && !completionPrefix &&
                context.HasVerifiedCompletedV1OuterDispatch(rootContext, frames[bridgeStart].CodeAddress);
            // Rejected bridges remain intact; individual-helper cleanup only visits their leaf-ward prefix.
            if (!bridge.IsMatch || (!rootPlaced && !completedOuter) || context.TraceLog == null ||
                dispatchIndex == frames.Count ||
                context.Diagnostics.SegmentsProcessed != context.Segments.Count ||
                context.Diagnostics.BoundariesNotFound != 0 ||
                context.Diagnostics.AdjacencyMismatches != 0 ||
                (rootContext.Frames.Kind == AsyncCallstackKind.RuntimeAsync &&
                 !HasVerifiedAsyncAnchor(context, rootIndex, rootContext)))
            {
                return bridgeStart;
            }
            if (completionPrefix)
            {
                AsyncProfilerStitchedFrame caller = frames[matchedEnd];
                AsyncProfilerStitchedFrame anchor = frames[dispatchIndex];
                if (context.Diagnostics.V1CompletionAlignmentAmbiguous != 0 ||
                    matchedEnd >= dispatchIndex ||
                    caller.Origin != AsyncProfilerStitchedFrameOrigin.Sync || caller.Context != null ||
                    caller.CodeAddress == CodeAddressIndex.Invalid || caller.Method == MethodIndex.Invalid ||
                    context.TraceLog.CodeAddresses.ModuleFileIndex(caller.CodeAddress) == ModuleFileIndex.Invalid ||
                    context.GetHostModuleOwner(caller.CodeAddress) != ModuleFileIndex.Invalid ||
                    context.ClassifyBoundary(caller.CodeAddress).IsDispatchMarker ||
                    anchor.Origin != AsyncProfilerStitchedFrameOrigin.Sync || anchor.Context != null ||
                    anchor.Method == MethodIndex.Invalid ||
                    context.GetHostModuleOwner(anchor.CodeAddress) != context.GetHostModuleOwner(frames[bridgeStart].CodeAddress) ||
                    !ReferenceEquals(context.GetRuntimeProfile(anchor.CodeAddress),
                        context.GetRuntimeProfile(frames[bridgeStart].CodeAddress)) ||
                    !(context.HasVerifiedDispatchEntry(rootContext, frames[bridgeStart].CodeAddress) ||
                      context.HasVerifiedV1CompletionAnchor(
                          rootContext, frames[bridgeStart].CodeAddress, allowCompletionUnwind: true)))
                {
                    return bridgeStart;
                }
            }
            for (int i = bridgeStart; i < matchedEnd; i++)
            {
                if (frames[i].Origin != AsyncProfilerStitchedFrameOrigin.Sync ||
                    frames[i].Context != null)
                {
                    return bridgeStart;
                }
                AsyncProfilerCallStackStitchBoundaryKind boundary = context.ClassifyBoundary(frames[i].CodeAddress).Kind;
                if (boundary == AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher ||
                    boundary == AsyncProfilerCallStackStitchBoundaryKind.V2Dispatcher)
                {
                    return bridgeStart;
                }
            }

            int count = completionPrefix ? bridge.Length : dispatchIndex - bridgeStart;
            if (completionPrefix)
            {
                CollapseMachinerySpan(frames, bridgeStart, count);
            }
            else
            {
                frames.RemoveRange(bridgeStart, count);
            }
            context.Diagnostics.AsyncInfrastructureFramesCollapsed += count;
            context.Diagnostics.RuntimeResumptionBridgeFramesCollapsed += count;
            return completionPrefix ? bridgeStart : frames.Count;
        }

        #endregion
    }

    /// <summary>
    /// Collapses a fully resolved CoreLib-only synchronous handoff span between independently verified adjacent
    /// active async contexts, for V1/V1, V1/V2, V2/V1, or V2/V2 nesting.
    /// </summary>
    /// <remarks>
    /// Requires a physically current outer anchor and a current or verified dispatch-target inner anchor.
    /// An unresolved, foreign, or ambiguously owned frame preserves the complete bridge. Folding changes only
    /// synchronous machinery; logical roles and captured states remain unchanged.
    /// </remarks>
    internal sealed class AsyncProfilerReentrantHandoffTransform
    {
        public void Apply(AsyncProfilerCallStackTransformState context)
        {
            if (context.TraceLog == null ||
                context.Diagnostics.BoundariesNotFound != 0 ||
                context.Diagnostics.AdjacencyMismatches != 0 ||
                context.Diagnostics.V1CompletionAlignmentAmbiguous != 0)
            {
                return;
            }

            List<AsyncProfilerStitchedFrame> frames = context.Frames;
            for (int currentIndex = 0; currentIndex < frames.Count; currentIndex++)
            {
                AsyncProfilerStitchedFrame outerCurrent = frames[currentIndex];
                if (outerCurrent.Origin != AsyncProfilerStitchedFrameOrigin.AsyncCurrent ||
                    outerCurrent.Context == null ||
                    outerCurrent.Method == MethodIndex.Invalid ||
                    outerCurrent.CodeAddress == CodeAddressIndex.Invalid)
                {
                    continue;
                }

                int handoffStart = currentIndex;
                while (handoffStart > 0 &&
                       frames[handoffStart - 1].Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                       ReferenceEquals(frames[handoffStart - 1].Context, outerCurrent.Context))
                {
                    handoffStart--;
                }

                int handoffCount = currentIndex - handoffStart;
                if (handoffCount == 0 || handoffStart == 0)
                {
                    continue;
                }

                AsyncProfilerCallStack innerContext = frames[handoffStart - 1].Context;
                if (innerContext == null ||
                    innerContext.Depth != outerCurrent.Context.Depth + 1 ||
                    !HasVerifiedAsyncAnchor(context, handoffStart - 1, innerContext) ||
                    !IsSystemPrivateCoreLibSpan(context, frames, handoffStart, handoffCount))
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

    internal static class AsyncProfilerCallStackTransformHelpers
    {
        /// <summary>Replaces a verified machinery span, retaining its leafmost physical identity and ownership.</summary>
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

        internal static bool HasSameMachineryOwner(
            AsyncProfilerCallStackTransformState context, int start, int count)
        {
            AsyncProfilerStitchedFrame first = context.Frames[start];
            AsyncProfilerRuntimeRuleProfile profile = context.GetRuntimeProfile(first.CodeAddress);
            ModuleFileIndex owner = context.GetHostModuleOwner(first.CodeAddress);
            for (int i = start; i < start + count; i++)
            {
                AsyncProfilerStitchedFrame frame = context.Frames[i];
                if (frame.Origin != AsyncProfilerStitchedFrameOrigin.Sync ||
                    !ReferenceEquals(first.Context, frame.Context) ||
                    !ReferenceEquals(profile, context.GetRuntimeProfile(frame.CodeAddress)) ||
                    (context.TraceLog != null &&
                     (owner == ModuleFileIndex.Invalid || owner != context.GetHostModuleOwner(frame.CodeAddress))))
                {
                    return false;
                }
            }
            return true;
        }

        internal static bool HasVerifiedAsyncAnchor(
            AsyncProfilerCallStackTransformState context,
            int contextEnd,
            AsyncProfilerCallStack activation)
        {
            List<AsyncProfilerStitchedFrame> frames = context.Frames;
            bool verified = false;
            for (int i = contextEnd; i >= 0 && ReferenceEquals(frames[i].Context, activation); i--)
            {
                AsyncProfilerStitchedFrame frame = frames[i];
                if (frame.Origin == AsyncProfilerStitchedFrameOrigin.V1CompletedBody)
                {
                    return false;
                }
                verified |= frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent &&
                    frame.Method != MethodIndex.Invalid && frame.CodeAddress != CodeAddressIndex.Invalid;
                if (frame.Origin != AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget)
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
                    verified |= IsSystemPrivateCoreLibSpan(context, frames, i, 1);
                }
                else if (context.TraceLog.AsyncProfilerCallStacks?.MethodCompletionObserved(activation) == true &&
                    activation.GetCompletedFrameCount(context.TimestampQpc) == frame.SegmentFrameIndex &&
                    i > 0 &&
                    frames[i - 1].Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                    ReferenceEquals(frames[i - 1].Context, activation) &&
                    context.ClassifyBoundary(frames[i - 1].CodeAddress).Kind == AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher)
                {
                    verified |= IsSystemPrivateCoreLibSpan(context, frames, i - 1, 1);
                }
            }

            return verified;
        }

        internal static bool IsSystemPrivateCoreLibSpan(
            AsyncProfilerCallStackTransformState context,
            List<AsyncProfilerStitchedFrame> frames,
            int start,
            int count)
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
                if (frames[i].Method == MethodIndex.Invalid ||
                    codeAddressIndex == CodeAddressIndex.Invalid ||
                    owner == ModuleFileIndex.Invalid ||
                    context.GetHostModuleOwner(codeAddressIndex) != owner ||
                    !ReferenceEquals(context.GetRuntimeProfile(codeAddressIndex), profile))
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool IsSyncFrameKind(AsyncProfilerStitchedFrame frame, AsyncProfilerStitchSyncFrameKind kind) =>
            frame.Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
            frame.SyncFrameKind == kind;
    }
}
