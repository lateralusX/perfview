// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

using static Microsoft.Diagnostics.Tracing.Computers.AsyncProfilerCallStackTransformHelpers;
using static Microsoft.Diagnostics.Tracing.Computers.AsyncProfilerCallStackStitchBoundary;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>
    /// Supplies implementation-dependent frame classifications and evidence before structural stitching,
    /// and layout recognition or complete presentation stages afterward. Profiler boundary
    /// contracts are shared by every profile; version selection never substitutes for identity, ownership,
    /// activation, or layout validation.
    /// </summary>
    /// <remarks>
    /// The common profile retains the existing evidence-checked rules when captured metadata is missing,
    /// conflicting, or unsupported. CoreCLR 11.0 is the first explicitly identified profile and currently
    /// shares those rules. Override narrow classifications, layouts, or structural evidence for verified
    /// differences. A substantially different implementation can replace a presentation stage entirely.
    /// Event semantics, method identity, ambiguity checks, and pipeline flags/order remain shared.
    /// Generated application-name decoding follows compiler conventions separately; a captured CoreLib
    /// version does not identify the compiler that built an application.
    /// </remarks>
    internal class AsyncProfilerRuntimeRuleProfile
    {
        protected AsyncProfilerRuntimeRuleProfile(string name)
        {
            Name = name;
        }

        /// <summary>Selects rules from captured CoreLib metadata, never from the analysis host.</summary>
        public static AsyncProfilerRuntimeRuleProfile Select(
            string fileVersion, string productVersion, RuntimeSku runtimeFlavor)
        {
            Version file = ParseVersion(fileVersion);
            Version product = ParseVersion(productVersion);
            if (file != null && product != null &&
                (file.Major != product.Major || file.Minor != product.Minor))
            {
                return Common;
            }

            Version version = product ?? file;
            return runtimeFlavor == RuntimeSku.CoreClr &&
                   version != null && version.Major == 11 && version.Minor == 0
                ? CoreClr11
                : Common;
        }

        /// <summary>Rules shared with unrecognized runtimes; not an alias for the newest supported runtime.</summary>
        public static AsyncProfilerRuntimeRuleProfile Common => s_common;

        /// <summary>Rules verified against the CoreCLR 11.0 async-profiler implementation.</summary>
        public static AsyncProfilerRuntimeRuleProfile CoreClr11 => s_coreClr11;

        /// <summary>A diagnostic name for the selected rule set.</summary>
        public string Name { get; }

        /// <summary>Recognizes shared contract boundaries and profile-specific V1 dispatcher plumbing.</summary>
        public AsyncProfilerCallStackStitchBoundaryInfo ClassifyBoundary(string frameName)
        {
            AsyncProfilerCallStackStitchBoundaryKind kind =
                AsyncProfilerCallStackStitchBoundary.ClassifyContractBoundary(frameName, out int wrapperIndex);
            if (kind == AsyncProfilerCallStackStitchBoundaryKind.None && IsV1DispatcherInfrastructure(frameName))
            {
                kind = AsyncProfilerCallStackStitchBoundaryKind.V1DispatcherInfrastructure;
            }
            return new AsyncProfilerCallStackStitchBoundaryInfo(kind, wrapperIndex);
        }

        /// <summary>Classifies generated application methods and CoreLib helpers for stitching and presentation.</summary>
        public virtual AsyncProfilerStitchSyncFrameKind ClassifySynchronousFrame(string frameName, bool isHostModule)
        {
            if (string.IsNullOrEmpty(frameName))
            {
                return AsyncProfilerStitchSyncFrameKind.None;
            }
            string method = GetBareMethodName(frameName);
            if (IsStateMachineMoveNext(frameName, method))
            {
                return AsyncProfilerStitchSyncFrameKind.V1StateMachineMoveNext;
            }

            if (!isHostModule)
            {
                return AsyncProfilerStitchSyncFrameKind.None;
            }

            AsyncProfilerStitchSyncFrameKind profilerKind = ClassifyProfilerInfrastructure(frameName, method);
            if (profilerKind != AsyncProfilerStitchSyncFrameKind.None)
            {
                return profilerKind;
            }

            if (method == "ExecutionContextCallback" && IsCommonV1DispatcherInfrastructure(frameName))
            {
                return AsyncProfilerStitchSyncFrameKind.V1StateMachineBoxExecutionContextCallback;
            }

            AsyncProfilerStitchSyncFrameKind bridgeKind = ClassifySystemPrivateCoreLibAsyncBridge(frameName, method);
            if (bridgeKind != AsyncProfilerStitchSyncFrameKind.None)
            {
                return bridgeKind;
            }

            if (!IsRuntimeCompilerServicesMethod(frameName))
            {
                return AsyncProfilerStitchSyncFrameKind.None;
            }

            string builderType = GetDeclaringTypeName(frameName);
            if (builderType is null)
            {
                return AsyncProfilerStitchSyncFrameKind.None;
            }

            int genericArity = builderType.IndexOf('`');
            if (genericArity >= 0)
            {
                builderType = builderType.Substring(0, genericArity);
            }

            if ((IsKnownV1AwaiterType(builderType) || IsKnownStateMachineBoxAwareAwaiter(frameName)) &&
                IsAwaiterRegistrationMethod(method))
            {
                return AsyncProfilerStitchSyncFrameKind.V1AwaiterRegistrationInfrastructure;
            }

            if (method == "SetExistingTaskResult" && builderType == "AsyncTaskMethodBuilder")
            {
                return AsyncProfilerStitchSyncFrameKind.V1MethodBuilderCompletion;
            }

            if (method == "AwaitUnsafeOnCompleted" && IsKnownV1MethodBuilderType(builderType))
            {
                return AsyncProfilerStitchSyncFrameKind.V1MethodBuilderAwaitUnsafeOnCompleted;
            }

            if (method != "Start")
            {
                return IsKnownV1MethodBuilderType(builderType)
                    ? AsyncProfilerStitchSyncFrameKind.V1MethodBuilderInfrastructure
                    : AsyncProfilerStitchSyncFrameKind.None;
            }

            return IsKnownV1MethodBuilderType(builderType)
                ? AsyncProfilerStitchSyncFrameKind.V1MethodBuilderStart
                : AsyncProfilerStitchSyncFrameKind.None;
        }

        /// <summary>Recognizes a constructed V1 execution/callback box's state-machine type; identity resolution
        /// and resumption-carrier approval are separate.</summary>
        public virtual bool TryGetBoxedStateMachineTypeName(
            string frameName, bool isHostModule, out string stateMachineType)
        {
            stateMachineType = null;
            string method = GetBareMethodName(frameName);
            if (!isHostModule ||
                (method != "MoveNext" && method != "InstrumentedMoveNext" && method != "ExecutionContextCallback") ||
                !IsCommonV1DispatcherInfrastructure(frameName))
            {
                return false;
            }

            string declaringType = GetDeclaringTypeName(frameName);
            int argumentsStart = declaringType.IndexOf('[');
            if (argumentsStart < 0 ||
                !declaringType.Substring(0, argumentsStart).EndsWith(
                    AsyncStateMachineBoxTypeSuffix + "`1", StringComparison.Ordinal) ||
                declaringType[declaringType.Length - 1] != ']')
            {
                return false;
            }

            // Nested boxes include their builder's type arguments. TStateMachine is the final argument;
            // commas inside its own constructed generic type must not split that argument.
            int argumentStart = argumentsStart + 1;
            int depth = 0;
            for (int i = argumentStart; i < declaringType.Length - 1; i++)
            {
                char c = declaringType[i];
                if (c == '[')
                {
                    depth++;
                }
                else if (c == ']')
                {
                    if (--depth < 0)
                    {
                        return false;
                    }
                }
                else if (c == ',' && depth == 0)
                {
                    if (i == argumentStart)
                    {
                        return false;
                    }
                    argumentStart = i + 1;
                }
            }

            if (depth != 0 || argumentStart >= declaringType.Length - 1)
            {
                return false;
            }

            stateMachineType = declaringType.Substring(
                argumentStart, declaringType.Length - 1 - argumentStart).Trim();
            return stateMachineType.Length != 0;
        }

        /// <summary>
        /// Supplies one complete presentation stage. Overrides should cache delegates, preserve unrelated
        /// contexts and callbacks, and retain the shared ownership/identity checks used by common stages.
        /// Ambiguous runtime ownership selects common stages rather than a version-specific replacement.
        /// </summary>
        public virtual AsyncProfilerCallStackTransform GetTransform(AsyncProfilerRuntimeTransformKind kind)
        {
            switch (kind)
            {
                case AsyncProfilerRuntimeTransformKind.SampledV2Wrapper: return s_sampledV2Wrapper;
                case AsyncProfilerRuntimeTransformKind.ReentrantHandoff: return s_reentrantHandoff;
                case AsyncProfilerRuntimeTransformKind.V1Presentation: return s_v1Presentation;
                case AsyncProfilerRuntimeTransformKind.InfrastructureCleanup: return s_infrastructureCleanup;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        /// <summary>
        /// Recognizes root-side plumbing after a consumed contract boundary. Matching does not authorize
        /// crossing another splice boundary; the stitcher validates bounds and boundary ownership.
        /// </summary>
        public virtual AsyncProfilerRuntimeSequenceMatch MatchDispatchPlumbing(
            AsyncCallstackKind kind, IReadOnlyList<AsyncProfilerStitchSyncFrame> frames, int start, int end,
            Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classify)
        {
            AsyncProfilerCallStackStitchBoundaryKind expected = kind == AsyncCallstackKind.StateMachineAsync
                ? AsyncProfilerCallStackStitchBoundaryKind.V1DispatcherInfrastructure
                : AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation;
            int next = start;
            while (next < end && classify(frames[next].CodeAddress).Kind == expected)
            {
                next++;
            }
            return next == start ? default : AsyncProfilerRuntimeSequenceMatch.Match(next - start);
        }

        /// <summary>
        /// Supplies physical completion evidence only after the stitcher has matched the complete V1 segment
        /// and reconciled exception counts. The common rule accepts a leaf-side builder-completion helper.
        /// </summary>
        public virtual bool HasV1CompletionEvidence(IReadOnlyList<AsyncProfilerStitchSyncFrame> frames, int start, int end)
        {
            for (int i = end - 1; i >= start; i--)
            {
                if (frames[i].Kind == AsyncProfilerStitchSyncFrameKind.V1MethodBuilderCompletion)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Recognizes return-unwind plumbing after full completion has independently been recorded. Captured
        /// method order, box identities, and runtime-module ownership are checked by the presentation transform.
        /// </summary>
        public virtual bool IsV1CompletionUnwindInfrastructure(
            AsyncProfilerStitchedFrame frame, AsyncProfilerCallStackStitchBoundaryKind boundary) =>
            (boundary == AsyncProfilerCallStackStitchBoundaryKind.V1DispatcherInfrastructure &&
             frame.BoxedStateMachineMethod != MethodIndex.Invalid) ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1MethodBuilderCompletion;

        /// <summary>
        /// Filters candidate V1 carriers. Resolved identity and physical-application-body precedence are
        /// checked independently by the stitcher and cannot be relaxed by this predicate.
        /// </summary>
        public virtual bool IsV1BoxCarrierCandidate(
            IReadOnlyList<AsyncProfilerStitchSyncFrame> frames, int start, int end, int index) =>
            frames[index].BoxedStateMachineMethod != MethodIndex.Invalid &&
            frames[index].Kind != AsyncProfilerStitchSyncFrameKind.V1StateMachineBoxExecutionContextCallback;

        /// <summary>The synchronous scheduler anchor retained beneath an optional resumption bridge.</summary>
        public virtual bool IsRuntimeResumptionAnchor(AsyncProfilerStitchedFrame frame) =>
            IsSyncFrameKind(frame, AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibThreadPoolDispatch);

        /// <summary>Optional individual-helper policy, applied only outside protected/rejected spans.</summary>
        public virtual bool IsOptionalInfrastructureFrame(AsyncProfilerStitchedFrame frame, AsyncProfilerCallStackStitchBoundaryKind boundary) =>
            boundary == AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher ||
            boundary == AsyncProfilerCallStackStitchBoundaryKind.V1DispatcherInfrastructure ||
            boundary == AsyncProfilerCallStackStitchBoundaryKind.V2ContinuationWrapper ||
            boundary == AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1MethodBuilderStart ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1MethodBuilderAwaitUnsafeOnCompleted ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1MethodBuilderInfrastructure ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1MethodBuilderCompletion ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1AwaiterRegistrationInfrastructure ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1DispatcherCreationInfrastructure ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult;

        /// <summary>
        /// Matches a layout inside [start, end). A rejection protects its candidate span; a match only establishes
        /// layout. The context validates captured module/profile identity, and transforms retain activation guards.
        /// </summary>
        public AsyncProfilerRuntimeSequenceMatch MatchSequence(
            AsyncProfilerRuntimeSequenceKind kind, AsyncProfilerCallStackTransformContext context, int start, int end)
        {
            switch (kind)
            {
                case AsyncProfilerRuntimeSequenceKind.TaskCompletionPair:
                    return MatchTaskCompletionPair(context, start, end);
                case AsyncProfilerRuntimeSequenceKind.TaskCompletionChain:
                    return MatchTaskCompletionChain(context, start, end);
                case AsyncProfilerRuntimeSequenceKind.V2CompletionHandoff:
                    return MatchV2CompletionHandoff(context, start, end);
                case AsyncProfilerRuntimeSequenceKind.V1Startup:
                    return MatchV1Startup(context, start, end);
                case AsyncProfilerRuntimeSequenceKind.V1AwaitRegistration:
                    return MatchV1AwaitRegistration(context, start, end);
                case AsyncProfilerRuntimeSequenceKind.ProfilerEntry:
                    return MatchProfilerEntry(context, start, end);
                case AsyncProfilerRuntimeSequenceKind.RuntimeResumptionBridge:
                    return MatchRuntimeResumptionBridge(context, start, end);
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        /// <summary>Recognizes implementation-dependent box/dispatcher helper types, not splice boundaries.</summary>
        protected virtual bool IsV1DispatcherInfrastructure(string frameName) =>
            IsCommonV1DispatcherInfrastructure(frameName);

        /// <summary>Matches a generated MoveNext and its root-side builder startup helpers, excluding kickoff.</summary>
        protected virtual AsyncProfilerRuntimeSequenceMatch MatchV1Startup(
            AsyncProfilerCallStackTransformContext context, int start, int end)
        {
            if (start == end || !IsSyncFrameKind(context.Frames[start], AsyncProfilerStitchSyncFrameKind.V1StateMachineMoveNext))
            {
                return default;
            }
            int next = start + 1;
            while (next < end && IsSyncFrameKind(context.Frames[next], AsyncProfilerStitchSyncFrameKind.V1MethodBuilderStart))
            {
                next++;
            }
            return AsyncProfilerRuntimeSequenceMatch.Match(next - start);
        }

        /// <summary>Matches the await-registration spine before a V1 method body.</summary>
        protected virtual AsyncProfilerRuntimeSequenceMatch MatchV1AwaitRegistration(
            AsyncProfilerCallStackTransformContext context, int start, int end)
        {
            int next = start;
            while (next < end &&
                   (IsSyncFrameKind(context.Frames[next], AsyncProfilerStitchSyncFrameKind.V1MethodBuilderAwaitUnsafeOnCompleted) ||
                    IsSyncFrameKind(context.Frames[next], AsyncProfilerStitchSyncFrameKind.V1AwaiterRegistrationInfrastructure)))
            {
                next++;
            }
            return next == start ? default : AsyncProfilerRuntimeSequenceMatch.Match(next - start);
        }

        /// <summary>Matches the entry spine immediately before the verified logical anchor at end.</summary>
        protected virtual AsyncProfilerRuntimeSequenceMatch MatchProfilerEntry(
            AsyncProfilerCallStackTransformContext context, int start, int end)
        {
            if (start == end || end == context.Frames.Count)
            {
                return default;
            }
            AsyncProfilerStitchedFrame target = context.Frames[end];
            if (IsSyncFrameKind(context.Frames[start], AsyncProfilerStitchSyncFrameKind.V1DispatcherCreationInfrastructure))
            {
                if (target.Origin != AsyncProfilerStitchedFrameOrigin.AsyncCurrent ||
                    target.Segment.Kind != AsyncCallstackKind.StateMachineAsync)
                {
                    return default;
                }
                AsyncProfilerRuntimeSequenceMatch registration = context.MatchRuntimeSequence(
                    AsyncProfilerRuntimeSequenceKind.V1AwaitRegistration, start + 1, end);
                return registration.IsMatch && registration.Length == end - start - 1
                    ? AsyncProfilerRuntimeSequenceMatch.Match(end - start) : default;
            }
            if (!IsSyncFrameKind(context.Frames[start], AsyncProfilerStitchSyncFrameKind.ProfilerContextResume))
            {
                return default;
            }
            bool dispatcherTarget = target.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget &&
                !target.IsStateMachineBoxCarrier;
            int length = dispatcherTarget ? 2 : 1;
            return end - start == length &&
                   (!dispatcherTarget ||
                    (target.Segment.Kind == AsyncCallstackKind.StateMachineAsync &&
                     context.ClassifyBoundary(context.Frames[start + 1].CodeAddress).Kind ==
                        AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher))
                ? AsyncProfilerRuntimeSequenceMatch.Match(length) : default;
        }

        /// <summary>
        /// Matches a whole optional bridge including its retained scheduler anchor. Explicit rejections protect
        /// incomplete or changed layouts from individual-helper cleanup.
        /// </summary>
        protected virtual AsyncProfilerRuntimeSequenceMatch MatchRuntimeResumptionBridge(
            AsyncProfilerCallStackTransformContext context, int start, int end)
        {
            bool anchor = start < end && IsRuntimeResumptionAnchor(context.Frames[end - 1]);
            int helpersEnd = anchor ? end - 1 : end;
            bool candidate = false;
            bool pairMatched = false;
            bool pairRejected = false;
            for (int i = start; i < helpersEnd; i++)
            {
                AsyncProfilerStitchedFrame frame = context.Frames[i];
                candidate |= frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure ||
                    frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations ||
                    frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult;
                AsyncProfilerRuntimeSequenceMatch pair = context.MatchRuntimeSequence(
                    AsyncProfilerRuntimeSequenceKind.TaskCompletionPair, i, end);
                candidate |= pair.IsMatch || pair.IsRejected;
                pairMatched |= pair.IsMatch;
                pairRejected |= pair.IsRejected;
            }
            return !candidate ? default :
                anchor && pairMatched && !pairRejected
                    ? AsyncProfilerRuntimeSequenceMatch.Match(end - start)
                    : AsyncProfilerRuntimeSequenceMatch.Reject(end - start);
        }

        /// <summary>
        /// The common layout is RunContinuations/TrySetResult. Version-specific overrides can include precisely
        /// identified helpers and return a longer span; chains and completed-dispatch tails reuse that span.
        /// </summary>
        protected virtual AsyncProfilerRuntimeSequenceMatch MatchTaskCompletionPair(
            AsyncProfilerCallStackTransformContext context, int start, int end) =>
            end - start >= 2 &&
            IsSyncFrameKind(context.Frames[start], AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations) &&
            IsSyncFrameKind(context.Frames[start + 1], AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult)
                ? AsyncProfilerRuntimeSequenceMatch.Match(2)
                : default;

        /// <summary>The common chain appends the public SetResult trigger to a profile-specific completion pair.</summary>
        protected virtual AsyncProfilerRuntimeSequenceMatch MatchTaskCompletionChain(
            AsyncProfilerCallStackTransformContext context, int start, int end)
        {
            AsyncProfilerRuntimeSequenceMatch pair = MatchTaskCompletionPair(context, start, end);
            if (!pair.IsMatch)
            {
                return pair.IsRejected
                    ? AsyncProfilerRuntimeSequenceMatch.Reject(Math.Min(end - start, pair.Length + 1))
                    : default;
            }
            int trigger = start + pair.Length;
            return trigger < end &&
                   IsSyncFrameKind(context.Frames[trigger], AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskCompletionSourceSetResult)
                ? AsyncProfilerRuntimeSequenceMatch.Match(pair.Length + 1)
                : default;
        }

        /// <summary>
        /// A completed V2 tail contains a bridge, the profile's completion pair, and two distinct contract
        /// dispatch-continuation methods. It does not establish ownership of an active V1 anchor or its caller.
        /// </summary>
        protected virtual AsyncProfilerRuntimeSequenceMatch MatchV2CompletionHandoff(
            AsyncProfilerCallStackTransformContext context, int start, int end)
        {
            if (start == end ||
                !IsSyncFrameKind(context.Frames[start], AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure))
            {
                return default;
            }
            AsyncProfilerRuntimeSequenceMatch pair = MatchTaskCompletionPair(context, start + 1, end);
            if (!pair.IsMatch)
            {
                return pair.IsRejected
                    ? AsyncProfilerRuntimeSequenceMatch.Reject(Math.Min(end - start, pair.Length + 3))
                    : default;
            }
            int dispatch = start + 1 + pair.Length;
            return end - dispatch >= 2 &&
                   context.Frames[dispatch].Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                   context.Frames[dispatch + 1].Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                   context.ClassifyBoundary(context.Frames[dispatch].CodeAddress).Kind ==
                       AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation &&
                   context.ClassifyBoundary(context.Frames[dispatch + 1].CodeAddress).Kind ==
                       AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation &&
                   context.Frames[dispatch].Method != context.Frames[dispatch + 1].Method
                ? AsyncProfilerRuntimeSequenceMatch.Match(pair.Length + 3)
                : default;
        }

        #region private

        // Carrier/helper defaults remain independent of overrides to boundary-plumbing recognition.
        private static bool IsCommonV1DispatcherInfrastructure(string frameName)
        {
            if (string.IsNullOrEmpty(frameName))
            {
                return false;
            }
            int qualifiedStart = frameName.IndexOf('!');
            qualifiedStart = qualifiedStart >= 0 ? qualifiedStart + 1 : 0;
            const string namespacePrefix = "System.Runtime.CompilerServices.";
            if (frameName.Length - qualifiedStart < namespacePrefix.Length ||
                string.CompareOrdinal(frameName, qualifiedStart, namespacePrefix, 0, namespacePrefix.Length) != 0)
            {
                return false;
            }

            string declaringType = GetDeclaringTypeName(frameName);
            if (declaringType is null)
            {
                return false;
            }

            int genericArity = declaringType.IndexOf('`');
            if (genericArity >= 0)
            {
                declaringType = declaringType.Substring(0, genericArity);
            }

            return declaringType.EndsWith(AsyncStateMachineBoxTypeSuffix, StringComparison.Ordinal) ||
                   declaringType == AsyncStateMachineDispatcherTypeName;
        }

        private static bool IsKnownV1MethodBuilderType(string builderType)
        {
            switch (builderType)
            {
                case "AsyncMethodBuilderCore":
                case "AsyncTaskMethodBuilder":
                case "AsyncValueTaskMethodBuilder":
                case "PoolingAsyncValueTaskMethodBuilder":
                case "AsyncVoidMethodBuilder":
                case "AsyncIteratorMethodBuilder":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsKnownV1AwaiterType(string awaiterType)
        {
            switch (awaiterType)
            {
                case "TaskAwaiter":
                case "ConfiguredTaskAwaiter":
                case "ValueTaskAwaiter":
                case "ConfiguredValueTaskAwaiter":
                case "YieldAwaiter":
                case "ForceYieldingAwaiter":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsAwaiterRegistrationMethod(string method) =>
            method == "OnCompleted" ||
            method == "UnsafeOnCompleted" ||
            method == "UnsafeOnCompletedInternal" ||
            method == "AwaitUnsafeOnCompleted";

        private static bool IsKnownStateMachineBoxAwareAwaiter(string frameName) =>
            frameName.IndexOf(
                "+YieldAwaiter.System.Runtime.CompilerServices.IStateMachineBoxAwareAwaiter.",
                StringComparison.Ordinal) >= 0 ||
            frameName.IndexOf(
                "+ForceYieldingAwaiter.System.Runtime.CompilerServices.IStateMachineBoxAwareAwaiter.",
                StringComparison.Ordinal) >= 0;

        private static AsyncProfilerStitchSyncFrameKind ClassifySystemPrivateCoreLibAsyncBridge(
            string frameName,
            string method)
        {
            if (method == "Dispatch" &&
                IsMethodOnType(frameName, "System.Threading.ThreadPoolWorkQueue", method))
            {
                return AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibThreadPoolDispatch;
            }

            if (IsMethodOnType(frameName, "System.Threading.Tasks.Task", method) ||
                IsMethodOnType(frameName, "System.Threading.Tasks.Task`1", method))
            {
                if (method == "RunContinuations")
                {
                    return AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations;
                }
                if (method == "TrySetResult")
                {
                    return AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult;
                }
            }

            if ((IsMethodOnType(frameName, "System.Threading.Tasks.TaskCompletionSource", method) ||
                 IsMethodOnType(frameName, "System.Threading.Tasks.TaskCompletionSource`1", method)) &&
                method == "SetResult")
            {
                return AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskCompletionSourceSetResult;
            }

            if (IsMethodOnType(frameName, "System.Threading.Tasks.AwaitTaskContinuation", method))
            {
                return method == "RunOrScheduleAction"
                    ? AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure
                    : AsyncProfilerStitchSyncFrameKind.None;
            }

            if (IsMethodOnType(frameName, "System.Threading.ExecutionContext", method))
            {
                return method == "RunInternal" ||
                       method == "RunFromThreadPoolDispatchLoop"
                    ? AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure
                    : AsyncProfilerStitchSyncFrameKind.None;
            }

            return AsyncProfilerStitchSyncFrameKind.None;
        }

        private static AsyncProfilerStitchSyncFrameKind ClassifyProfilerInfrastructure(string frameName, string method)
        {
            if (method == "CreateDispatcher" &&
                IsMethodOnType(frameName, "System.Runtime.CompilerServices.AsyncStateMachineDispatcherInfo", method))
            {
                return AsyncProfilerStitchSyncFrameKind.V1DispatcherCreationInfrastructure;
            }

            const string callstackType = "System.Runtime.CompilerServices.AsyncProfiler+AsyncCallstack";
            if (method == "CaptureStateMachineAsyncCallstack" &&
                IsMethodOnType(frameName, callstackType, method))
            {
                return AsyncProfilerStitchSyncFrameKind.ProfilerCallstackCapture;
            }
            if ((method == "EmitAsyncCallstack" || method == "EmitEvent") &&
                IsMethodOnType(frameName, callstackType, method))
            {
                return AsyncProfilerStitchSyncFrameKind.ProfilerCallstackEmission;
            }
            if (method == "Resume" &&
                IsMethodOnType(frameName, "System.Runtime.CompilerServices.AsyncProfiler+ResumeAsyncContext", method))
            {
                return AsyncProfilerStitchSyncFrameKind.ProfilerContextResume;
            }
            if ((method == "ResetContext" &&
                 IsMethodOnType(frameName, "System.Runtime.CompilerServices.AsyncProfiler+SyncPoint", method)) ||
                ((method == "get_MethodId" || method == "get_StateOffset" || method == "cctor") &&
                 IsMethodOnType(frameName, "System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`1",
                     method == "cctor" ? ".cctor" : method)) ||
                ((method == "CallClassConstructor" || method == "InitClassHelper" || method == "InitClassSlow") &&
                 IsMethodOnType(frameName, "System.Runtime.CompilerServices.InitHelpers", method)) ||
                (method == "GetNonGCStaticBaseSlow" &&
                 IsMethodOnType(frameName, "System.Runtime.CompilerServices.StaticsHelpers", method)) ||
                (method == "GetDiagnosticData" && IsCommonV1DispatcherInfrastructure(frameName)))
            {
                return AsyncProfilerStitchSyncFrameKind.ProfilerInfrastructure;
            }
            return AsyncProfilerStitchSyncFrameKind.None;
        }

        private static bool IsMethodOnType(string frameName, string fullTypeName, string method)
        {
            if (GetBareMethodName(frameName) != (method == ".cctor" ? "cctor" : method))
            {
                return false;
            }

            int typeStart = IsHostModuleQualifiedFrame(frameName)
                ? HostModuleName.Length + 1
                : 0;
            if (frameName.Length - typeStart <= fullTypeName.Length ||
                string.CompareOrdinal(frameName, typeStart, fullTypeName, 0, fullTypeName.Length) != 0)
            {
                return false;
            }

            int typeEnd = typeStart + fullTypeName.Length;
            if (fullTypeName.IndexOf('`') >= 0 && frameName[typeEnd] == '[')
            {
                int depth = 0;
                do
                {
                    char c = frameName[typeEnd++];
                    if (c == '[') depth++;
                    else if (c == ']') depth--;
                }
                while (typeEnd < frameName.Length && depth != 0);
                if (depth != 0)
                {
                    return false;
                }
            }

            return typeEnd < frameName.Length &&
                   frameName[typeEnd] == '.' &&
                   string.CompareOrdinal(frameName, typeEnd + 1, method, 0, method.Length) == 0 &&
                   (typeEnd + 1 + method.Length == frameName.Length ||
                    frameName[typeEnd + 1 + method.Length] == '(');
        }

        private static bool IsRuntimeCompilerServicesMethod(string frameName)
        {
            int qualifiedStart = IsHostModuleQualifiedFrame(frameName)
                ? HostModuleName.Length + 1
                : 0;
            const string namespacePrefix = "System.Runtime.CompilerServices.";
            return frameName.Length - qualifiedStart >= namespacePrefix.Length &&
                   string.CompareOrdinal(
                       frameName, qualifiedStart, namespacePrefix, 0, namespacePrefix.Length) == 0;
        }

        private static Version ParseVersion(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            // Product versions may append a prerelease identifier, source hash, or "Commit Hash: ...".
            text = text.Trim();
            int suffix = text.IndexOfAny(s_versionSuffixes);
            if (suffix >= 0)
            {
                text = text.Substring(0, suffix);
            }
            return Version.TryParse(text, out Version version) ? version : null;
        }

        /// <summary>The common box-family suffix, retained separately from profiler boundary contracts.</summary>
        private const string AsyncStateMachineBoxTypeSuffix = "AsyncStateMachineBox";

        private static readonly char[] s_versionSuffixes = { '-', '+', ' ' };
        private static readonly AsyncProfilerRuntimeRuleProfile s_common = new AsyncProfilerRuntimeRuleProfile("Common");
        private static readonly AsyncProfilerRuntimeRuleProfile s_coreClr11 = new AsyncProfilerRuntimeRuleProfile("CoreCLR 11.0");
        private static readonly AsyncProfilerCallStackTransform s_sampledV2Wrapper = new AsyncProfilerSampledV2WrapperTransform().Apply;
        private static readonly AsyncProfilerCallStackTransform s_reentrantHandoff = new AsyncProfilerReentrantHandoffTransform().Apply;
        private static readonly AsyncProfilerCallStackTransform s_v1Presentation = new AsyncProfilerV1PresentationTransform().Apply;
        private static readonly AsyncProfilerCallStackTransform s_infrastructureCleanup = new AsyncProfilerInfrastructureCleanupTransform().Apply;

        #endregion
    }
}
