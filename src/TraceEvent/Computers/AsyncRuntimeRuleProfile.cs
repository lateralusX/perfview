// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

using static Microsoft.Diagnostics.Tracing.Computers.AsyncStackTransformHelpers;
using static Microsoft.Diagnostics.Tracing.Computers.AsyncStitchBoundary;

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
    internal class AsyncRuntimeRuleProfile
    {
        protected AsyncRuntimeRuleProfile(string name)
        {
            Name = name;
        }

        /// <summary>Selects rules from captured CoreLib metadata, never from the analysis host.</summary>
        public static AsyncRuntimeRuleProfile Select(
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
        public static AsyncRuntimeRuleProfile Common => s_common;

        /// <summary>Rules verified against the CoreCLR 11.0 async-profiler implementation.</summary>
        public static AsyncRuntimeRuleProfile CoreClr11 => s_coreClr11;

        /// <summary>A diagnostic name for the selected rule set.</summary>
        public string Name { get; }

        /// <summary>Recognizes shared contract boundaries and profile-specific V1 dispatcher plumbing.</summary>
        public AsyncStitchBoundaryInfo ClassifyBoundary(string frameName)
        {
            AsyncStitchBoundaryKind kind =
                AsyncStitchBoundary.ClassifyContractBoundary(frameName, out int wrapperIndex);
            if (kind == AsyncStitchBoundaryKind.None && IsV1DispatcherInfrastructure(frameName))
            {
                kind = AsyncStitchBoundaryKind.V1DispatcherInfrastructure;
            }
            return new AsyncStitchBoundaryInfo(kind, wrapperIndex);
        }

        /// <summary>Classifies generated application methods and CoreLib helpers for stitching and presentation.</summary>
        public virtual StitchSyncFrameKind ClassifySynchronousFrame(string frameName, bool isHostModule)
        {
            if (string.IsNullOrEmpty(frameName))
            {
                return StitchSyncFrameKind.None;
            }
            string method = GetBareMethodName(frameName);
            if (IsStateMachineMoveNext(frameName, method))
            {
                return StitchSyncFrameKind.V1StateMachineMoveNext;
            }

            if (!isHostModule)
            {
                return StitchSyncFrameKind.None;
            }

            StitchSyncFrameKind profilerKind = ClassifyAsyncProfilerInfrastructure(frameName, method);
            if (profilerKind != StitchSyncFrameKind.None)
            {
                return profilerKind;
            }

            StitchSyncFrameKind bridgeKind = ClassifySystemPrivateCoreLibAsyncBridge(frameName, method);
            if (bridgeKind != StitchSyncFrameKind.None)
            {
                return bridgeKind;
            }

            if (!IsRuntimeCompilerServicesMethod(frameName))
            {
                return StitchSyncFrameKind.None;
            }

            string builderType = GetDeclaringTypeName(frameName);
            if (builderType is null)
            {
                return StitchSyncFrameKind.None;
            }

            int genericArity = builderType.IndexOf('`');
            if (genericArity >= 0)
            {
                builderType = builderType.Substring(0, genericArity);
            }

            if ((IsKnownV1AwaiterType(builderType) || IsKnownStateMachineBoxAwareAwaiter(frameName)) &&
                IsAwaiterRegistrationMethod(method))
            {
                return StitchSyncFrameKind.V1AwaiterRegistrationInfrastructure;
            }

            if (method == "SetExistingTaskResult" && builderType == "AsyncTaskMethodBuilder")
            {
                return StitchSyncFrameKind.V1MethodBuilderCompletion;
            }

            if (method == "AwaitUnsafeOnCompleted" && IsKnownV1MethodBuilderType(builderType))
            {
                return StitchSyncFrameKind.V1MethodBuilderAwaitUnsafeOnCompleted;
            }

            if (method != "Start")
            {
                return IsKnownV1MethodBuilderType(builderType)
                    ? StitchSyncFrameKind.V1MethodBuilderInfrastructure
                    : StitchSyncFrameKind.None;
            }

            return IsKnownV1MethodBuilderType(builderType)
                ? StitchSyncFrameKind.V1MethodBuilderStart
                : StitchSyncFrameKind.None;
        }

        /// <summary>Recognizes a constructed V1 carrier's state-machine type; identity is resolved separately.</summary>
        public virtual bool TryGetBoxedStateMachineTypeName(
            string frameName, bool isHostModule, out string stateMachineType)
        {
            stateMachineType = null;
            string method = GetBareMethodName(frameName);
            if (!isHostModule ||
                (method != "MoveNext" && method != "InstrumentedMoveNext") ||
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
        public virtual AsyncStackTransform GetTransform(AsyncRuntimeTransformKind kind)
        {
            switch (kind)
            {
                case AsyncRuntimeTransformKind.SampledV2Wrapper: return s_sampledV2Wrapper;
                case AsyncRuntimeTransformKind.ReentrantHandoff: return s_reentrantHandoff;
                case AsyncRuntimeTransformKind.V1Presentation: return s_v1Presentation;
                case AsyncRuntimeTransformKind.InfrastructureCleanup: return s_infrastructureCleanup;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        /// <summary>
        /// Recognizes root-side plumbing after a consumed contract boundary. Matching does not authorize
        /// crossing another splice boundary; the stitcher validates bounds and boundary ownership.
        /// </summary>
        public virtual AsyncRuntimeSequenceMatch MatchDispatchPlumbing(
            AsyncCallstackKind kind, IReadOnlyList<StitchSyncFrame> frames, int start, int end,
            Func<CodeAddressIndex, AsyncStitchBoundaryInfo> classify)
        {
            AsyncStitchBoundaryKind expected = kind == AsyncCallstackKind.StateMachineAsync
                ? AsyncStitchBoundaryKind.V1DispatcherInfrastructure
                : AsyncStitchBoundaryKind.V2DispatchContinuation;
            int next = start;
            while (next < end && classify(frames[next].CodeAddress).Kind == expected)
            {
                next++;
            }
            return next == start ? default : AsyncRuntimeSequenceMatch.Match(next - start);
        }

        /// <summary>
        /// Supplies physical completion evidence only after the stitcher has matched the complete V1 segment
        /// and reconciled exception counts. The common rule accepts a leaf-side builder-completion helper.
        /// </summary>
        public virtual bool HasV1CompletionEvidence(IReadOnlyList<StitchSyncFrame> frames, int start, int end)
        {
            for (int i = end - 1; i >= start; i--)
            {
                if (frames[i].Kind == StitchSyncFrameKind.V1MethodBuilderCompletion)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Filters candidate V1 carriers. Resolved identity and physical-application-body precedence are
        /// checked independently by the stitcher and cannot be relaxed by this predicate.
        /// </summary>
        public virtual bool IsV1BoxCarrierCandidate(
            IReadOnlyList<StitchSyncFrame> frames, int start, int end, int index) =>
            frames[index].BoxedStateMachineMethod != MethodIndex.Invalid;

        /// <summary>The synchronous scheduler anchor retained beneath an optional resumption bridge.</summary>
        public virtual bool IsRuntimeResumptionAnchor(StitchedFrame frame) =>
            IsSyncFrameKind(frame, StitchSyncFrameKind.SystemPrivateCoreLibThreadPoolDispatch);

        /// <summary>Optional individual-helper policy, applied only outside protected/rejected spans.</summary>
        public virtual bool IsOptionalInfrastructureFrame(StitchedFrame frame, AsyncStitchBoundaryKind boundary) =>
            boundary == AsyncStitchBoundaryKind.V1Dispatcher ||
            boundary == AsyncStitchBoundaryKind.V1DispatcherInfrastructure ||
            boundary == AsyncStitchBoundaryKind.V2ContinuationWrapper ||
            boundary == AsyncStitchBoundaryKind.V2DispatchContinuation ||
            frame.SyncFrameKind == StitchSyncFrameKind.V1MethodBuilderStart ||
            frame.SyncFrameKind == StitchSyncFrameKind.V1MethodBuilderAwaitUnsafeOnCompleted ||
            frame.SyncFrameKind == StitchSyncFrameKind.V1MethodBuilderInfrastructure ||
            frame.SyncFrameKind == StitchSyncFrameKind.V1MethodBuilderCompletion ||
            frame.SyncFrameKind == StitchSyncFrameKind.V1AwaiterRegistrationInfrastructure ||
            frame.SyncFrameKind == StitchSyncFrameKind.V1DispatcherCreationInfrastructure ||
            frame.SyncFrameKind == StitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure ||
            frame.SyncFrameKind == StitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations ||
            frame.SyncFrameKind == StitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult;

        /// <summary>
        /// Matches a layout inside [start, end). A rejection protects its candidate span; a match only establishes
        /// layout. The context validates captured module/profile identity, and transforms retain activation guards.
        /// </summary>
        public AsyncRuntimeSequenceMatch MatchSequence(
            AsyncRuntimeSequenceKind kind, AsyncStackTransformContext context, int start, int end)
        {
            switch (kind)
            {
                case AsyncRuntimeSequenceKind.TaskCompletionPair:
                    return MatchTaskCompletionPair(context, start, end);
                case AsyncRuntimeSequenceKind.TaskCompletionChain:
                    return MatchTaskCompletionChain(context, start, end);
                case AsyncRuntimeSequenceKind.V2CompletionHandoff:
                    return MatchV2CompletionHandoff(context, start, end);
                case AsyncRuntimeSequenceKind.V1Startup:
                    return MatchV1Startup(context, start, end);
                case AsyncRuntimeSequenceKind.V1AwaitRegistration:
                    return MatchV1AwaitRegistration(context, start, end);
                case AsyncRuntimeSequenceKind.AsyncProfilerEntry:
                    return MatchAsyncProfilerEntry(context, start, end);
                case AsyncRuntimeSequenceKind.RuntimeResumptionBridge:
                    return MatchRuntimeResumptionBridge(context, start, end);
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        /// <summary>Recognizes implementation-dependent box/dispatcher helper types, not splice boundaries.</summary>
        protected virtual bool IsV1DispatcherInfrastructure(string frameName) =>
            IsCommonV1DispatcherInfrastructure(frameName);

        /// <summary>Matches a generated MoveNext and its root-side builder startup helpers, excluding kickoff.</summary>
        protected virtual AsyncRuntimeSequenceMatch MatchV1Startup(
            AsyncStackTransformContext context, int start, int end)
        {
            if (start == end || !IsSyncFrameKind(context.Frames[start], StitchSyncFrameKind.V1StateMachineMoveNext))
            {
                return default;
            }
            int next = start + 1;
            while (next < end && IsSyncFrameKind(context.Frames[next], StitchSyncFrameKind.V1MethodBuilderStart))
            {
                next++;
            }
            return AsyncRuntimeSequenceMatch.Match(next - start);
        }

        /// <summary>Matches the await-registration spine before a duplicate startup body.</summary>
        protected virtual AsyncRuntimeSequenceMatch MatchV1AwaitRegistration(
            AsyncStackTransformContext context, int start, int end)
        {
            int next = start;
            while (next < end &&
                   (IsSyncFrameKind(context.Frames[next], StitchSyncFrameKind.V1MethodBuilderAwaitUnsafeOnCompleted) ||
                    IsSyncFrameKind(context.Frames[next], StitchSyncFrameKind.V1AwaiterRegistrationInfrastructure)))
            {
                next++;
            }
            return next == start ? default : AsyncRuntimeSequenceMatch.Match(next - start);
        }

        /// <summary>Matches the entry spine immediately before the verified logical anchor at end.</summary>
        protected virtual AsyncRuntimeSequenceMatch MatchAsyncProfilerEntry(
            AsyncStackTransformContext context, int start, int end)
        {
            if (start == end || end == context.Frames.Count ||
                !IsSyncFrameKind(context.Frames[start], StitchSyncFrameKind.AsyncProfilerContextResume))
            {
                return default;
            }
            StitchedFrame target = context.Frames[end];
            bool dispatcherTarget = target.Origin == StitchedFrameOrigin.AsyncDispatchTarget &&
                !target.IsStateMachineBoxCarrier;
            int length = dispatcherTarget ? 2 : 1;
            return end - start == length &&
                   (!dispatcherTarget ||
                    (target.Segment.Kind == AsyncCallstackKind.StateMachineAsync &&
                     context.ClassifyBoundary(context.Frames[start + 1].CodeAddress).Kind ==
                        AsyncStitchBoundaryKind.V1Dispatcher))
                ? AsyncRuntimeSequenceMatch.Match(length) : default;
        }

        /// <summary>
        /// Matches a whole optional bridge including its retained scheduler anchor. Explicit rejections protect
        /// incomplete or changed layouts from individual-helper cleanup.
        /// </summary>
        protected virtual AsyncRuntimeSequenceMatch MatchRuntimeResumptionBridge(
            AsyncStackTransformContext context, int start, int end)
        {
            bool anchor = start < end && IsRuntimeResumptionAnchor(context.Frames[end - 1]);
            int helpersEnd = anchor ? end - 1 : end;
            bool candidate = false;
            bool pairMatched = false;
            bool pairRejected = false;
            for (int i = start; i < helpersEnd; i++)
            {
                StitchedFrame frame = context.Frames[i];
                candidate |= frame.SyncFrameKind == StitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure ||
                    frame.SyncFrameKind == StitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations ||
                    frame.SyncFrameKind == StitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult;
                AsyncRuntimeSequenceMatch pair = context.MatchRuntimeSequence(
                    AsyncRuntimeSequenceKind.TaskCompletionPair, i, end);
                candidate |= pair.IsMatch || pair.IsRejected;
                pairMatched |= pair.IsMatch;
                pairRejected |= pair.IsRejected;
            }
            return !candidate ? default :
                anchor && pairMatched && !pairRejected
                    ? AsyncRuntimeSequenceMatch.Match(end - start)
                    : AsyncRuntimeSequenceMatch.Reject(end - start);
        }

        /// <summary>
        /// The common layout is RunContinuations/TrySetResult. Version-specific overrides can include precisely
        /// identified helpers and return a longer span; chains and completed-dispatch tails reuse that span.
        /// </summary>
        protected virtual AsyncRuntimeSequenceMatch MatchTaskCompletionPair(
            AsyncStackTransformContext context, int start, int end) =>
            end - start >= 2 &&
            IsSyncFrameKind(context.Frames[start], StitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations) &&
            IsSyncFrameKind(context.Frames[start + 1], StitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult)
                ? AsyncRuntimeSequenceMatch.Match(2)
                : default;

        /// <summary>The common chain appends the public SetResult trigger to a profile-specific completion pair.</summary>
        protected virtual AsyncRuntimeSequenceMatch MatchTaskCompletionChain(
            AsyncStackTransformContext context, int start, int end)
        {
            AsyncRuntimeSequenceMatch pair = MatchTaskCompletionPair(context, start, end);
            if (!pair.IsMatch)
            {
                return pair.IsRejected
                    ? AsyncRuntimeSequenceMatch.Reject(Math.Min(end - start, pair.Length + 1))
                    : default;
            }
            int trigger = start + pair.Length;
            return trigger < end &&
                   IsSyncFrameKind(context.Frames[trigger], StitchSyncFrameKind.SystemPrivateCoreLibTaskCompletionSourceSetResult)
                ? AsyncRuntimeSequenceMatch.Match(pair.Length + 1)
                : default;
        }

        /// <summary>
        /// A completed V2 tail contains a bridge, the profile's completion pair, and two distinct contract
        /// dispatch-continuation methods. It does not establish ownership of an active V1 anchor or its caller.
        /// </summary>
        protected virtual AsyncRuntimeSequenceMatch MatchV2CompletionHandoff(
            AsyncStackTransformContext context, int start, int end)
        {
            if (start == end ||
                !IsSyncFrameKind(context.Frames[start], StitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure))
            {
                return default;
            }
            AsyncRuntimeSequenceMatch pair = MatchTaskCompletionPair(context, start + 1, end);
            if (!pair.IsMatch)
            {
                return pair.IsRejected
                    ? AsyncRuntimeSequenceMatch.Reject(Math.Min(end - start, pair.Length + 3))
                    : default;
            }
            int dispatch = start + 1 + pair.Length;
            return end - dispatch >= 2 &&
                   context.Frames[dispatch].Origin == StitchedFrameOrigin.Sync &&
                   context.Frames[dispatch + 1].Origin == StitchedFrameOrigin.Sync &&
                   context.ClassifyBoundary(context.Frames[dispatch].CodeAddress).Kind ==
                       AsyncStitchBoundaryKind.V2DispatchContinuation &&
                   context.ClassifyBoundary(context.Frames[dispatch + 1].CodeAddress).Kind ==
                       AsyncStitchBoundaryKind.V2DispatchContinuation &&
                   context.Frames[dispatch].Method != context.Frames[dispatch + 1].Method
                ? AsyncRuntimeSequenceMatch.Match(pair.Length + 3)
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

        private static StitchSyncFrameKind ClassifySystemPrivateCoreLibAsyncBridge(
            string frameName,
            string method)
        {
            if (method == "Dispatch" &&
                IsMethodOnType(frameName, "System.Threading.ThreadPoolWorkQueue", method))
            {
                return StitchSyncFrameKind.SystemPrivateCoreLibThreadPoolDispatch;
            }

            if (IsMethodOnType(frameName, "System.Threading.Tasks.Task", method) ||
                IsMethodOnType(frameName, "System.Threading.Tasks.Task`1", method))
            {
                if (method == "RunContinuations")
                {
                    return StitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations;
                }
                if (method == "TrySetResult")
                {
                    return StitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult;
                }
            }

            if ((IsMethodOnType(frameName, "System.Threading.Tasks.TaskCompletionSource", method) ||
                 IsMethodOnType(frameName, "System.Threading.Tasks.TaskCompletionSource`1", method)) &&
                method == "SetResult")
            {
                return StitchSyncFrameKind.SystemPrivateCoreLibTaskCompletionSourceSetResult;
            }

            if (IsMethodOnType(frameName, "System.Threading.Tasks.AwaitTaskContinuation", method))
            {
                return method == "RunOrScheduleAction"
                    ? StitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure
                    : StitchSyncFrameKind.None;
            }

            if (IsMethodOnType(frameName, "System.Threading.ExecutionContext", method))
            {
                return method == "RunInternal" ||
                       method == "RunFromThreadPoolDispatchLoop"
                    ? StitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure
                    : StitchSyncFrameKind.None;
            }

            return StitchSyncFrameKind.None;
        }

        private static StitchSyncFrameKind ClassifyAsyncProfilerInfrastructure(string frameName, string method)
        {
            if (method == "CreateDispatcher" &&
                IsMethodOnType(frameName, "System.Runtime.CompilerServices.AsyncStateMachineDispatcherInfo", method))
            {
                return StitchSyncFrameKind.V1DispatcherCreationInfrastructure;
            }

            const string callstackType = "System.Runtime.CompilerServices.AsyncProfiler+AsyncCallstack";
            if (method == "CaptureStateMachineAsyncCallstack" &&
                IsMethodOnType(frameName, callstackType, method))
            {
                return StitchSyncFrameKind.AsyncProfilerCallstackCapture;
            }
            if ((method == "EmitAsyncCallstack" || method == "EmitEvent") &&
                IsMethodOnType(frameName, callstackType, method))
            {
                return StitchSyncFrameKind.AsyncProfilerCallstackEmission;
            }
            if (method == "Resume" &&
                IsMethodOnType(frameName, "System.Runtime.CompilerServices.AsyncProfiler+ResumeAsyncContext", method))
            {
                return StitchSyncFrameKind.AsyncProfilerContextResume;
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
                return StitchSyncFrameKind.AsyncProfilerInfrastructure;
            }
            return StitchSyncFrameKind.None;
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
        private static readonly AsyncRuntimeRuleProfile s_common = new AsyncRuntimeRuleProfile("Common");
        private static readonly AsyncRuntimeRuleProfile s_coreClr11 = new AsyncRuntimeRuleProfile("CoreCLR 11.0");
        private static readonly AsyncStackTransform s_sampledV2Wrapper = new AsyncSampledV2WrapperTransform().Apply;
        private static readonly AsyncStackTransform s_reentrantHandoff = new AsyncReentrantHandoffTransform().Apply;
        private static readonly AsyncStackTransform s_v1Presentation = new AsyncV1PresentationTransform().Apply;
        private static readonly AsyncStackTransform s_infrastructureCleanup = new AsyncInfrastructureCleanupTransform().Apply;

        #endregion
    }
}
