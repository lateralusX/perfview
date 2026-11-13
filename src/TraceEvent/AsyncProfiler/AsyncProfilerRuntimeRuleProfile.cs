// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

using static Microsoft.Diagnostics.Tracing.Computers.AsyncProfilerCallStackTransformHelpers;
using static Microsoft.Diagnostics.Tracing.Computers.AsyncProfilerCallStackStitchBoundary;
using static Microsoft.Diagnostics.Tracing.Computers.AsyncProfilerMethodNames;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>
    /// Supplies implementation-dependent frame classifications and evidence before structural stitching,
    /// and layout recognition or complete presentation stages afterward. Profiler boundary
    /// contracts are shared by every profile; version selection never substitutes for identity, ownership,
    /// activation, or layout validation.
    /// </summary>
    /// <remarks>
    /// Missing, conflicting, or unsupported captured metadata selects common rules. Profiles may override
    /// classifications, layouts, or whole presentation stages without relaxing shared ownership and identity checks.
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

        /// <summary>The short CoreLib module name used in symbols and captured-module lookup.</summary>
        public const string HostModuleName = "System.Private.CoreLib";

        /// <summary>Rules shared with unrecognized runtimes; not an alias for the newest supported runtime.</summary>
        public static AsyncProfilerRuntimeRuleProfile Common => s_common;

        /// <summary>Rules verified against the CoreCLR 11.0 async-profiler implementation.</summary>
        public static AsyncProfilerRuntimeRuleProfile CoreClr11 => s_coreClr11;

        /// <summary>A diagnostic name for the selected rule set.</summary>
        public string Name { get; }

        /// <summary>Checks the CoreLib symbol prefix, not captured module ownership.</summary>
        internal static bool IsHostModuleQualifiedFrame(string frameName)
        {
            return AsyncProfilerMethodNames.IsModuleQualifiedFrame(frameName, HostModuleName);
        }

        /// <summary>Recognizes shared contract boundaries and profile-specific V1 dispatcher infrastructure.</summary>
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

        /// <summary>Recognizes captured native runtime images that can execute while a V2 wrapper prepares its target.</summary>
        public virtual bool IsV2NativeDispatchModule(TraceModuleFile module, TraceModuleFile hostModule)
        {
            if (module == null || hostModule == null || module.ImageBase == 0 || module.ManagedModule != null)
            {
                return false;
            }
            string name = module.Name;
            if (!string.Equals(name, "coreclr", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "clrjit", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "libcoreclr.so", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "libclrjit.so", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "libcoreclr.dylib", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, "libclrjit.dylib", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Use captured paths, including when a trace is opened on a different operating system.
            string path = module.FilePath;
            string hostPath = hostModule.FilePath;
            int directoryEnd = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
            int hostDirectoryEnd = Math.Max(hostPath.LastIndexOf('\\'), hostPath.LastIndexOf('/'));
            return directoryEnd > 0 && directoryEnd == hostDirectoryEnd &&
                string.Compare(path, 0, hostPath, 0, directoryEnd + 1, StringComparison.OrdinalIgnoreCase) == 0;
        }

        /// <summary>Recognizes generated async methods and runtime helper frames using this runtime's rules.</summary>
        /// <param name="frameName">The full method name, normalized for matching.</param>
        /// <param name="isHostModule">True when the method belongs to the captured CoreLib.</param>
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

            // Generated resume stubs can belong to the application's module rather than CoreLib.
            if (method == "IL_STUB_AsyncResume" && IsMethodOnType(frameName, "dynamicClass", method))
            {
                return AsyncProfilerStitchSyncFrameKind.V2ResumeStub;
            }

            if (!isHostModule)
            {
                return AsyncProfilerStitchSyncFrameKind.None;
            }

            if (method == "ExecutionContextCallback" && IsCommonV1DispatcherInfrastructure(frameName))
            {
                return AsyncProfilerStitchSyncFrameKind.V1StateMachineBoxExecutionContextCallback;
            }

            AsyncProfilerStitchSyncFrameKind profilerKind = ClassifyProfilerInfrastructure(frameName, method);
            if (profilerKind != AsyncProfilerStitchSyncFrameKind.None)
            {
                return profilerKind;
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

            builderType = GetTypeNameWithoutArity(builderType);

            if (((IsKnownV1AwaiterType(builderType) || IsKnownStateMachineBoxAwareAwaiter(frameName)) &&
                 IsAwaiterRegistrationMethod(method)) ||
                ((method == "QueueContinuation" || method == "OutputCorrelationEtwEvent") &&
                 IsMethodOnType(frameName, "System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter", method)))
            {
                return AsyncProfilerStitchSyncFrameKind.V1AwaiterRegistrationInfrastructure;
            }

            if (method == "get_IsCompleted" &&
                (IsMethodOnType(frameName, "System.Runtime.CompilerServices.TaskAwaiter", method) ||
                 IsMethodOnType(frameName, "System.Runtime.CompilerServices.TaskAwaiter`1", method) ||
                 IsMethodOnType(frameName, "System.Runtime.CompilerServices.ValueTaskAwaiter", method) ||
                 IsMethodOnType(frameName, "System.Runtime.CompilerServices.ValueTaskAwaiter`1", method) ||
                 IsMethodOnType(frameName, "System.Runtime.CompilerServices.ConfiguredTaskAwaitable+ConfiguredTaskAwaiter", method) ||
                 IsMethodOnType(frameName, "System.Runtime.CompilerServices.ConfiguredTaskAwaitable`1+ConfiguredTaskAwaiter", method) ||
                 IsMethodOnType(frameName, "System.Runtime.CompilerServices.ConfiguredTaskAwaitable`1", method, "ConfiguredTaskAwaiter") ||
                 IsMethodOnType(frameName, "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable+ConfiguredValueTaskAwaiter", method) ||
                 IsMethodOnType(frameName, "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable`1+ConfiguredValueTaskAwaiter", method) ||
                 IsMethodOnType(frameName, "System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable`1", method, "ConfiguredValueTaskAwaiter")))
            {
                return AsyncProfilerStitchSyncFrameKind.V1AwaiterCompletionCheck;
            }

            AsyncProfilerStitchSyncFrameKind awaiterKind = ClassifyAwaiterInfrastructure(frameName);
            if (awaiterKind != AsyncProfilerStitchSyncFrameKind.None)
            {
                return awaiterKind;
            }

            if ((method == "SetExistingTaskResult" && builderType == "AsyncTaskMethodBuilder") ||
                (method == "SetResult" &&
                 (IsMethodOnType(frameName, "System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder", method) ||
                  IsMethodOnType(frameName, "System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder`1", method))))
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
                    AsyncStateMachineBoxTypeSuffix + "`1", StringComparison.Ordinal))
            {
                return false;
            }

            // Nested boxes include their builder's arguments; TStateMachine is the final argument.
            return TryGetLastTypeArgument(declaringType, out stateMachineType);
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
        /// Recognizes root-side infrastructure after a consumed contract boundary. Matching does not authorize
        /// crossing another splice boundary; the stitcher validates bounds and boundary ownership.
        /// </summary>
        public virtual AsyncProfilerRuntimeSequenceMatch MatchDispatchInfrastructure(
            AsyncCallstackKind kind, IReadOnlyList<AsyncProfilerStitchSyncFrame> frames, int start, int end,
            Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classify)
        {
            AsyncProfilerCallStackStitchBoundaryKind expected = kind == AsyncCallstackKind.StateMachineAsync
                ? AsyncProfilerCallStackStitchBoundaryKind.V1DispatcherInfrastructure
                : AsyncProfilerCallStackStitchBoundaryKind.V2DispatcherInfrastructure;
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
        /// Recognizes return-unwind infrastructure after completion has independently been recorded. Captured
        /// method order, box identities, and runtime-module ownership are checked by the presentation transform.
        /// </summary>
        public virtual bool IsV1CompletionUnwindInfrastructure(
            AsyncProfilerStitchedFrame frame, AsyncProfilerCallStackStitchBoundaryKind boundary) =>
            (boundary == AsyncProfilerCallStackStitchBoundaryKind.V1DispatcherInfrastructure &&
             frame.BoxedStateMachineMethod != MethodIndex.Invalid) ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1MethodBuilderCompletion ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAwaiterInfrastructure;

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

        /// <summary>Recognizes helper types eligible to extend an already verified dispatcher span.
        /// Captured runtime ownership, physical contiguity, and context identity are checked by cleanup.</summary>
        public virtual bool IsProfilerDispatchInfrastructure(AsyncProfilerStitchSyncFrameKind kind) =>
            kind == AsyncProfilerStitchSyncFrameKind.ProfilerCallstackCapture ||
            kind == AsyncProfilerStitchSyncFrameKind.ProfilerCallstackEmission ||
            kind == AsyncProfilerStitchSyncFrameKind.ProfilerContextResume ||
            kind == AsyncProfilerStitchSyncFrameKind.ProfilerInfrastructure ||
            kind == AsyncProfilerStitchSyncFrameKind.V1StateMachineBoxExecutionContextCallback ||
            kind == AsyncProfilerStitchSyncFrameKind.V1AwaiterRegistrationInfrastructure ||
            kind == AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAwaiterInfrastructure ||
            kind == AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskExceptionInfrastructure;

        /// <summary>Optional individual-helper policy, applied only outside protected/rejected spans.</summary>
        public virtual bool IsOptionalInfrastructureFrame(AsyncProfilerStitchedFrame frame, AsyncProfilerCallStackStitchBoundaryKind boundary) =>
            boundary == AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher ||
            boundary == AsyncProfilerCallStackStitchBoundaryKind.V1DispatcherInfrastructure ||
            boundary == AsyncProfilerCallStackStitchBoundaryKind.V2Dispatcher ||
            boundary == AsyncProfilerCallStackStitchBoundaryKind.V2DispatcherInfrastructure ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1MethodBuilderStart ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1MethodBuilderAwaitUnsafeOnCompleted ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1MethodBuilderInfrastructure ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1MethodBuilderCompletion ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1AwaiterRegistrationInfrastructure ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1AwaiterCompletionCheck ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1DispatcherCreationInfrastructure ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations ||
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult;

        /// <summary>
        /// Matches a layout inside [start, end). A rejection protects its candidate span; a match only establishes
        /// layout. The context validates captured module/profile identity, and transforms retain activation guards.
        /// Wider runtime interiors are opt-in presentation evidence, never structural completion evidence.
        /// </summary>
        public AsyncProfilerRuntimeSequenceMatch MatchSequence(
            AsyncProfilerRuntimeSequenceKind kind, AsyncProfilerCallStackTransformState context, int start, int end,
            bool allowRuntimeInteriors = false)
        {
            AsyncProfilerRuntimeSequenceMatch match;
            switch (kind)
            {
                case AsyncProfilerRuntimeSequenceKind.TaskCompletion:
                    match = MatchTaskCompletion(context, start, end);
                    return allowRuntimeInteriors && match.Length == 0
                        ? MatchOptionalTaskCompletion(context, start, end) : match;
                case AsyncProfilerRuntimeSequenceKind.TaskExceptionCompletion:
                    return allowRuntimeInteriors
                        ? MatchOptionalTaskExceptionCompletion(context, start, end) : default;
                case AsyncProfilerRuntimeSequenceKind.TaskCompletionChain:
                    match = MatchTaskCompletionChain(context, start, end);
                    return allowRuntimeInteriors && match.Length == 0
                        ? MatchOptionalTaskCompletionChain(context, start, end) : match;
                case AsyncProfilerRuntimeSequenceKind.V2CompletionHandoff:
                    return MatchV2CompletionHandoff(context, start, end);
                case AsyncProfilerRuntimeSequenceKind.V1Startup:
                    return MatchV1Startup(context, start, end);
                case AsyncProfilerRuntimeSequenceKind.V1AwaitRegistration:
                    match = MatchV1AwaitRegistration(context, start, end);
                    return allowRuntimeInteriors && match.Length == 0
                        ? MatchOptionalV1AwaitRegistration(context, start, end) : match;
                case AsyncProfilerRuntimeSequenceKind.ProfilerEntry:
                    return MatchProfilerEntry(context, start, end);
                case AsyncProfilerRuntimeSequenceKind.RuntimeResumptionBridge:
                    return MatchRuntimeResumptionBridge(context, start, end);
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        /// <summary>Identifies optional sequence roots without authorizing removal of their callers or callees.</summary>
        public virtual bool TryGetOptionalSequenceKind(
            string rootName, AsyncProfilerStitchSyncFrameKind kind, out AsyncProfilerRuntimeSequenceKind sequence)
        {
            sequence = default;
            string method = GetBareMethodName(rootName);
            bool sourceCompletion = (method == "SetResult" || method == "SetException") &&
                (IsMethodOnType(rootName, ValueTaskSourceCoreTypeName, method) ||
                 IsPooledValueTaskBoxMethod(rootName, method));
            if (kind == AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult ||
                (method == "SetResult" && sourceCompletion))
            {
                sequence = AsyncProfilerRuntimeSequenceKind.TaskCompletion;
                return true;
            }
            if (method == "SetException" &&
                (kind == AsyncProfilerStitchSyncFrameKind.V1MethodBuilderInfrastructure || sourceCompletion))
            {
                sequence = AsyncProfilerRuntimeSequenceKind.TaskExceptionCompletion;
                return true;
            }
            if (kind == AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskCompletionSourceSetResult)
            {
                sequence = AsyncProfilerRuntimeSequenceKind.TaskCompletionChain;
                return true;
            }
            if (IsMethodOnType(rootName, "System.Runtime.CompilerServices.TaskAwaiter", "UnsafeOnCompletedInternal") ||
                IsMethodOnType(rootName, "System.Runtime.CompilerServices.TaskAwaiter", "OnCompletedInternal") ||
                (kind == AsyncProfilerStitchSyncFrameKind.V1AwaiterRegistrationInfrastructure &&
                 IsKnownStateMachineBoxAwareAwaiter(rootName)))
            {
                sequence = AsyncProfilerRuntimeSequenceKind.V1AwaitRegistration;
                return true;
            }
            return false;
        }

        /// <summary>Recognizes implementation-dependent box/dispatcher helper types, not splice boundaries.</summary>
        protected virtual bool IsV1DispatcherInfrastructure(string frameName) =>
            IsCommonV1DispatcherInfrastructure(frameName);

        /// <summary>Matches a generated MoveNext and its root-side builder startup helpers, excluding kickoff.</summary>
        protected virtual AsyncProfilerRuntimeSequenceMatch MatchV1Startup(
            AsyncProfilerCallStackTransformState context, int start, int end)
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
            AsyncProfilerCallStackTransformState context, int start, int end)
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

        /// <summary>Matches an entry spine before a physical caller or logical anchor; cleanup verifies its ownership.</summary>
        protected virtual AsyncProfilerRuntimeSequenceMatch MatchProfilerEntry(
            AsyncProfilerCallStackTransformState context, int start, int end)
        {
            if (start == end || end == context.Frames.Count)
            {
                return default;
            }
            AsyncProfilerStitchedFrame target = context.Frames[end];
            if (IsSyncFrameKind(context.Frames[start], AsyncProfilerStitchSyncFrameKind.V1DispatcherCreationInfrastructure))
            {
                if (target.Origin != AsyncProfilerStitchedFrameOrigin.Sync &&
                    (target.Origin != AsyncProfilerStitchedFrameOrigin.AsyncCurrent ||
                     target.Segment.Kind != AsyncCallstackKind.StateMachineAsync))
                {
                    return default;
                }
                AsyncProfilerRuntimeSequenceMatch registration = context.MatchRuntimeSequence(
                    AsyncProfilerRuntimeSequenceKind.V1AwaitRegistration, start + 1, end);
                return registration.IsMatch && registration.Length == end - start - 1
                    ? AsyncProfilerRuntimeSequenceMatch.Match(end - start)
                    : AsyncProfilerRuntimeSequenceMatch.Reject(end - start);
            }
            bool dispatcherTarget = target.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget &&
                !target.IsStateMachineBoxCarrier;
            if (!IsSyncFrameKind(context.Frames[start], AsyncProfilerStitchSyncFrameKind.ProfilerContextResume) &&
                !(dispatcherTarget &&
                  IsSyncFrameKind(context.Frames[start], AsyncProfilerStitchSyncFrameKind.ProfilerCallstackCapture)))
            {
                return default;
            }
            if (target.IsStateMachineBoxCarrier && end - start == 2)
            {
                AsyncProfilerStitchedFrame carrier = context.Frames[start + 1];
                return carrier.Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                       carrier.Presentation == AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames &&
                       carrier.CodeAddress == target.CodeAddress &&
                       carrier.BoxedStateMachineMethod == target.BoxedStateMachineMethod &&
                       ReferenceEquals(carrier.Context, target.Context)
                    ? AsyncProfilerRuntimeSequenceMatch.Match(2) : default;
            }
            int length = dispatcherTarget ? 2 : 1;
            return end - start == length &&
                   (!dispatcherTarget ||
                    (target.Segment.Kind == AsyncCallstackKind.StateMachineAsync &&
                     context.ClassifyBoundary(context.Frames[start + 1].CodeAddress).Kind ==
                        AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher))
                ? AsyncProfilerRuntimeSequenceMatch.Match(length) : default;
        }

        /// <summary>
        /// Matches a whole optional bridge including its retained scheduler anchor, or a root-adjacent
        /// completion prefix ending before a non-CoreLib caller. Shared guards verify that caller and anchor.
        /// Explicit rejections protect incomplete or changed layouts from individual-helper cleanup.
        /// </summary>
        protected virtual AsyncProfilerRuntimeSequenceMatch MatchRuntimeResumptionBridge(
            AsyncProfilerCallStackTransformState context, int start, int end)
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
                    AsyncProfilerRuntimeSequenceKind.TaskCompletion, i, end);
                candidate |= pair.IsMatch || pair.IsRejected;
                pairMatched |= pair.IsMatch;
                pairRejected |= pair.IsRejected;
            }
            if (anchor && pairMatched && !pairRejected)
            {
                int completionStart = start;
                while (completionStart < helpersEnd &&
                       (IsSyncFrameKind(context.Frames[completionStart],
                            AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAwaiterInfrastructure) ||
                        IsSyncFrameKind(context.Frames[completionStart],
                            AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure)))
                {
                    completionStart++;
                }
                AsyncProfilerRuntimeSequenceMatch chain = context.MatchRuntimeSequence(
                    AsyncProfilerRuntimeSequenceKind.TaskCompletionChain, completionStart, helpersEnd);
                int caller = completionStart + chain.Length;
                if (chain.IsMatch && caller < helpersEnd && context.TraceLog != null &&
                    context.GetHostModuleOwner(context.Frames[caller].CodeAddress) == ModuleFileIndex.Invalid)
                {
                    return AsyncProfilerRuntimeSequenceMatch.Match(caller - start);
                }
                return AsyncProfilerRuntimeSequenceMatch.Match(end - start);
            }
            if (context.TraceLog != null && context.Segments.Count != 0)
            {
                AsyncProfilerRuntimeSequenceMatch helpers =
                    MatchDispatchResumptionHelpers(context, start, helpersEnd);
                if (helpers.Length != 0)
                {
                    AsyncProfilerCallStack rootContext = context.Segments[context.Segments.Count - 1];
                    return anchor && !pairRejected && helpers.IsMatch && helpers.Length == helpersEnd - start &&
                        (context.HasVerifiedDispatchEntry(rootContext, context.Frames[start].CodeAddress) ||
                         context.HasVerifiedV1CompletionAnchor(
                             rootContext, context.Frames[start].CodeAddress, allowCompletionUnwind: true) ||
                         context.HasVerifiedCompletedV1OuterDispatch(rootContext, context.Frames[start].CodeAddress))
                            ? AsyncProfilerRuntimeSequenceMatch.Match(end - start)
                            : AsyncProfilerRuntimeSequenceMatch.Reject(end - start);
                }
            }
            return candidate ? AsyncProfilerRuntimeSequenceMatch.Reject(end - start) : default;
        }

        /// <summary>
        /// Supplies the removable helper span before the scheduler anchor. The caller verifies the root
        /// activation's consumed V1 dispatcher or V2 dispatch pair; the common rule recognizes contiguous YieldAwaitable-family methods.
        /// </summary>
        protected virtual AsyncProfilerRuntimeSequenceMatch MatchDispatchResumptionHelpers(
            AsyncProfilerCallStackTransformState context, int start, int end)
        {
            int matched = 0;
            for (int i = start; i < end; i++)
            {
                CodeAddressIndex address = context.Frames[i].CodeAddress;
                if (address != CodeAddressIndex.Invalid &&
                    IsYieldAwaitableMethod(context.TraceLog.CodeAddresses[address].FullMethodName))
                {
                    matched++;
                }
            }
            return matched == 0 ? default :
                matched == end - start
                    ? AsyncProfilerRuntimeSequenceMatch.Match(matched)
                    : AsyncProfilerRuntimeSequenceMatch.Reject(end - start);
        }

        /// <summary>
        /// The common layout is RunContinuations/TrySetResult. Version-specific overrides can include precisely
        /// identified helpers and return a longer span; chains and completed-dispatch tails reuse that span.
        /// </summary>
        protected virtual AsyncProfilerRuntimeSequenceMatch MatchTaskCompletion(
            AsyncProfilerCallStackTransformState context, int start, int end) =>
            end - start >= 2 &&
            IsSyncFrameKind(context.Frames[start], AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations) &&
            IsSyncFrameKind(context.Frames[start + 1], AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult)
                ? AsyncProfilerRuntimeSequenceMatch.Match(2)
                : default;

        /// <summary>The common chain appends the public SetResult trigger to a profile-specific completion span.</summary>
        protected virtual AsyncProfilerRuntimeSequenceMatch MatchTaskCompletionChain(
            AsyncProfilerCallStackTransformState context, int start, int end)
        {
            AsyncProfilerRuntimeSequenceMatch pair = MatchTaskCompletion(context, start, end);
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
        /// A completed V2 tail contains a bridge, the profile's completion span, and two distinct contract
        /// dispatch-continuation methods. It does not establish ownership of an active V1 anchor or its caller.
        /// </summary>
        protected virtual AsyncProfilerRuntimeSequenceMatch MatchV2CompletionHandoff(
            AsyncProfilerCallStackTransformState context, int start, int end)
        {
            if (start == end ||
                !IsSyncFrameKind(context.Frames[start], AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure))
            {
                return default;
            }
            AsyncProfilerRuntimeSequenceMatch pair = MatchTaskCompletion(context, start + 1, end);
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
                       AsyncProfilerCallStackStitchBoundaryKind.V2DispatcherInfrastructure &&
                   context.ClassifyBoundary(context.Frames[dispatch + 1].CodeAddress).Kind ==
                       AsyncProfilerCallStackStitchBoundaryKind.V2DispatcherInfrastructure &&
                   context.Frames[dispatch].Method != context.Frames[dispatch + 1].Method
                ? AsyncProfilerRuntimeSequenceMatch.Match(pair.Length + 3)
                : default;
        }

        /// <summary>Matches normal Task or value-task-source completion anchors with a wider runtime-owned interior.</summary>
        protected virtual AsyncProfilerRuntimeSequenceMatch MatchOptionalTaskCompletion(
            AsyncProfilerCallStackTransformState context, int start, int end)
        {
            if (context.TraceLog == null || end - start < 2)
            {
                return default;
            }
            if (IsSyncFrameKind(context.Frames[end - 1], AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult))
            {
                return IsSyncFrameKind(context.Frames[start], AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations)
                    ? AsyncProfilerRuntimeSequenceMatch.Match(end - start) : default;
            }
            return MatchOptionalValueTaskSourceCompletion(context, start, end, "SetResult");
        }

        /// <summary>Matches fault/cancellation completion anchors without supplying structural completion evidence.</summary>
        protected virtual AsyncProfilerRuntimeSequenceMatch MatchOptionalTaskExceptionCompletion(
            AsyncProfilerCallStackTransformState context, int start, int end)
        {
            if (context.TraceLog == null || end - start < 2)
            {
                return default;
            }
            AsyncProfilerRuntimeSequenceMatch source = MatchOptionalValueTaskSourceCompletion(
                context, start, end, "SetException");
            if (source.IsMatch)
            {
                return source;
            }
            if (end - start < 3 ||
                !IsSyncFrameKind(context.Frames[end - 1], AsyncProfilerStitchSyncFrameKind.V1MethodBuilderInfrastructure) ||
                GetBareMethodName(GetFrameName(context, end - 1)) != "SetException")
            {
                return default;
            }
            string leaf = GetFrameName(context, start);
            bool continuation = IsSyncFrameKind(context.Frames[start],
                AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations);
            bool canceled = IsMethodOnType(leaf, "System.Threading.Tasks.TaskExceptionHolder", "SetCancellationException");
            if (!continuation && !canceled &&
                !IsMethodOnType(leaf, "System.Threading.Tasks.TaskExceptionHolder", "AddFaultException"))
            {
                return default;
            }
            for (int i = start + 1; i < end - 1; i++)
            {
                string name = GetFrameName(context, i);
                if (IsTaskMethod(name, canceled ? "TrySetCanceled" : "TrySetException") ||
                    (continuation && IsTaskMethod(name, "TrySetCanceled")))
                {
                    return AsyncProfilerRuntimeSequenceMatch.Match(end - start);
                }
            }
            return default;
        }

        /// <summary>Keeps the completion-source trigger as the root of an optionally widened Task completion span.</summary>
        protected virtual AsyncProfilerRuntimeSequenceMatch MatchOptionalTaskCompletionChain(
            AsyncProfilerCallStackTransformState context, int start, int end)
        {
            if (end - start < 4 ||
                !IsSyncFrameKind(context.Frames[end - 1],
                    AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskCompletionSourceSetResult) ||
                !IsSyncFrameKind(context.Frames[start],
                    AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations) ||
                !IsSyncFrameKind(context.Frames[end - 2],
                    AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult))
            {
                return default;
            }
            AsyncProfilerRuntimeSequenceMatch pair = context.MatchRuntimeSequence(
                AsyncProfilerRuntimeSequenceKind.TaskCompletion, start, end - 1, allowRuntimeInteriors: true);
            return pair.IsRejected ? AsyncProfilerRuntimeSequenceMatch.Reject(end - start) :
                pair.IsMatch && pair.Length == end - start - 1
                    ? AsyncProfilerRuntimeSequenceMatch.Match(end - start) : default;
        }

        /// <summary>Matches Task-backed or CoreLib pooled-ValueTask registration, never an arbitrary value-task source.</summary>
        protected virtual AsyncProfilerRuntimeSequenceMatch MatchOptionalV1AwaitRegistration(
            AsyncProfilerCallStackTransformState context, int start, int end)
        {
            if (context.TraceLog == null || end - start < 3)
            {
                return default;
            }
            if (MatchV1AwaitRegistration(context, end - 1, end).IsRejected)
            {
                return AsyncProfilerRuntimeSequenceMatch.Reject(end - start);
            }
            string leaf = GetFrameName(context, start);
            string root = GetFrameName(context, end - 1);
            bool task = IsTaskMethod(leaf, "AddTaskContinuation") &&
                (IsMethodOnType(root, "System.Runtime.CompilerServices.TaskAwaiter", "UnsafeOnCompletedInternal") ||
                 IsMethodOnType(root, "System.Runtime.CompilerServices.TaskAwaiter", "OnCompletedInternal"));
            bool pooled = IsMethodOnType(leaf, ValueTaskSourceCoreTypeName, "OnCompleted") &&
                IsKnownStateMachineBoxAwareAwaiter(root);
            for (int i = start + 1; i < end - 1 && (task || pooled); i++)
            {
                string name = GetFrameName(context, i);
                if ((task && (IsTaskMethod(name, "SetContinuationForAwait") || IsTaskMethod(name, "UnsafeSetContinuationForAwait"))) ||
                    (pooled && IsPooledValueTaskBoxMethod(name, "OnCompleted")))
                {
                    return AsyncProfilerRuntimeSequenceMatch.Match(end - start);
                }
            }
            return default;
        }

        #region private

        private static AsyncProfilerRuntimeSequenceMatch MatchOptionalValueTaskSourceCompletion(
            AsyncProfilerCallStackTransformState context, int start, int end, string method)
        {
            string root = GetFrameName(context, end - 1);
            bool source = IsMethodOnType(root, ValueTaskSourceCoreTypeName, method);
            if (!source && !IsPooledValueTaskBoxMethod(root, method))
            {
                return default;
            }
            string leaf = GetFrameName(context, start);
            if (!IsMethodOnType(leaf, ValueTaskSourceCoreTypeName, "SignalCompletion") &&
                !IsMethodOnType(leaf, "System.Threading.Tasks.Sources.ManualResetValueTaskSourceCoreShared",
                    "InvokeContinuationWithContext"))
            {
                return default;
            }
            if (source)
            {
                return AsyncProfilerRuntimeSequenceMatch.Match(end - start);
            }
            for (int i = start + 1; i < end - 1; i++)
            {
                if (IsMethodOnType(GetFrameName(context, i), ValueTaskSourceCoreTypeName, method))
                {
                    return AsyncProfilerRuntimeSequenceMatch.Match(end - start);
                }
            }
            return default;
        }

        private static string GetFrameName(AsyncProfilerCallStackTransformState context, int index) =>
            AsyncProfilerMethodNames.NormalizeMethodName(
                context.TraceLog.CodeAddresses[context.Frames[index].CodeAddress].FullMethodName);

        private static bool IsTaskMethod(string frameName, string method) =>
            IsMethodOnType(frameName, "System.Threading.Tasks.Task", method) ||
            IsMethodOnType(frameName, "System.Threading.Tasks.Task`1", method);

        private static bool IsPooledValueTaskBoxMethod(string frameName, string method) =>
            IsMethodOnType(frameName, "System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder`1+StateMachineBox", method) ||
            IsMethodOnType(frameName, "System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder`1", method, "StateMachineBox");

        private static bool IsYieldAwaitableMethod(string methodName) =>
            IsMethodOnTypeOrNestedType(NormalizeMethodName(methodName), "System.Runtime.CompilerServices.YieldAwaitable");

        // Carrier/helper defaults remain independent of overrides to boundary-infrastructure recognition.
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

            return IsV1DispatcherTypeName(declaringType);
        }

        private static bool IsV1DispatcherImplementingType(string frameName)
        {
            if (!IsRuntimeCompilerServicesMethod(frameName))
            {
                return false;
            }
            int typeStart = (IsHostModuleQualifiedFrame(frameName) ? HostModuleName.Length + 1 : 0) +
                "System.Runtime.CompilerServices.".Length;
            string implementingType = GetImplementingTypeName(frameName, typeStart);
            return implementingType != null && IsV1DispatcherTypeName(implementingType);
        }

        private static bool IsV1DispatcherTypeName(string declaringType)
        {
            declaringType = GetTypeNameWithoutArity(declaringType);
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

        private static AsyncProfilerStitchSyncFrameKind ClassifyAwaiterInfrastructure(string frameName)
        {
            const string typeName = "System.Runtime.CompilerServices.TaskAwaiter";
            string method = GetBareMethodName(frameName);
            string member = method == "ctor" || method == "cctor" ? "." + method : method;
            int typeStart = IsHostModuleQualifiedFrame(frameName) ? HostModuleName.Length + 1 : 0;
            int typeEnd = typeStart + typeName.Length;
            return IsMethodOnType(frameName, typeName, member) ||
                IsMethodOnType(frameName, typeName + "`1", member) ||
                (frameName.Length > typeEnd && frameName[typeEnd] == '+' &&
                 string.CompareOrdinal(frameName, typeStart, typeName, 0, typeName.Length) == 0)
                    ? AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAwaiterInfrastructure
                    : AsyncProfilerStitchSyncFrameKind.None;
        }

        private static bool IsAwaiterRegistrationMethod(string method) =>
            method == "OnCompleted" ||
            method == "UnsafeOnCompleted" ||
            method == "UnsafeOnCompletedInternal" ||
            method == "AwaitUnsafeOnCompleted";

        private static bool IsKnownStateMachineBoxAwareAwaiter(string frameName)
        {
            const string qualification = ".System.Runtime.CompilerServices.IStateMachineBoxAwareAwaiter.";
            int interfaceStart = frameName.IndexOf(qualification, StringComparison.Ordinal);
            if (interfaceStart < 0)
            {
                return false;
            }
            // Explicit-interface qualification is part of the member name, not the declaring awaiter type.
            string awaiterType = GetDeclaringTypeName(frameName.Remove(interfaceStart, qualification.Length - 1));
            return IsKnownV1AwaiterType(GetTypeNameWithoutArity(awaiterType));
        }

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
                if (method == "TrySetException" || method == "AddException")
                {
                    return AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskExceptionInfrastructure;
                }
            }

            if (method == "AddFaultException" &&
                IsMethodOnType(frameName, "System.Threading.Tasks.TaskExceptionHolder", method))
            {
                return AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskExceptionInfrastructure;
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

            if (method == "ResumeTaskContinuation" &&
                IsMethodOnType(frameName,
                    "System.Runtime.CompilerServices.RuntimeAsyncTaskContinuation+TaskContinuationResume", method))
            {
                return AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure;
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
            if (((method == "CallClassConstructor" || method == "InitClassHelper" || method == "InitClassSlow") &&
                 IsMethodOnType(frameName, "System.Runtime.CompilerServices.InitHelpers", method)) ||
                (method == "GetNonGCStaticBaseSlow" &&
                 IsMethodOnType(frameName, "System.Runtime.CompilerServices.StaticsHelpers", method)))
            {
                return AsyncProfilerStitchSyncFrameKind.RuntimeInitializationInfrastructure;
            }
            const string profilerType = "System.Runtime.CompilerServices.AsyncProfiler";
            string member = method == "ctor" || method == "cctor" ? "." + method : method;
            int typeStart = IsHostModuleQualifiedFrame(frameName) ? HostModuleName.Length + 1 : 0;
            int typeEnd = typeStart + profilerType.Length;
            if (IsCommonV1DispatcherInfrastructure(frameName) ||
                IsV1DispatcherImplementingType(frameName) ||
                IsMethodOnType(frameName, "System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`1", member) ||
                IsMethodOnType(frameName, "System.Runtime.CompilerServices.AsyncProfilerEventSource", member) ||
                IsMethodOnType(frameName, profilerType, member) ||
                (frameName.Length > typeEnd && frameName[typeEnd] == '+' &&
                 string.CompareOrdinal(frameName, typeStart, profilerType, 0, profilerType.Length) == 0))
            {
                return AsyncProfilerStitchSyncFrameKind.ProfilerInfrastructure;
            }
            return AsyncProfilerStitchSyncFrameKind.None;
        }

        private static bool IsMethodOnType(string frameName, string fullTypeName, string method, string nestedTypeName = null) =>
            AsyncProfilerMethodNames.IsMethodOnType(frameName, fullTypeName, method, HostModuleName, nestedTypeName);

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

        private const string ValueTaskSourceCoreTypeName = "System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore`1";
        // Box-family recognition is separate from profiler boundary contracts.
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
