using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;
using Microsoft.Diagnostics.Tracing.Stacks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>
    /// Projects async-profiler ancestry and a sampled physical stack into a <see cref="MutableTraceEventStackSource"/>.
    /// The caller supplies the synthetic root policy so the same stitching implementation can be reused by different
    /// CPU and thread-time computers.
    /// </summary>
    internal sealed class AsyncCpuStackSourceBuilder
    {
        /// <summary>
        /// Creates a builder when the trace contains an async-profiler index, otherwise returns null.
        /// </summary>
        internal static AsyncCpuStackSourceBuilder TryCreate(
            TraceLog eventLog,
            MutableTraceEventStackSource outputStackSource,
            AsyncStackTransformPipeline transforms,
            Func<TraceEvent, TraceThread, StackSourceCallStackIndex> rootFactory)
        {
            AsyncCallStacksIndex index = eventLog.AsyncCallStacks;
            return index == null
                ? null
                : new AsyncCpuStackSourceBuilder(
                    eventLog, outputStackSource, index, transforms, rootFactory);
        }

        /// <summary>Aggregate soft diagnostics from every stitched sample.</summary>
        internal StitchDiagnostics Diagnostics => m_diagnostics;

        /// <summary>Emit per-segment happy-path diagnostics in addition to anomaly notes.</summary>
        internal bool TraceSteps { get; set; }

        /// <summary>
        /// Attempts to stitch the active async-profiler ancestry onto the event's physical call stack.
        /// Returns false without modifying the stack when the event has no physical stack or no active async segment.
        /// </summary>
        internal bool TryGetStitchedCallStack(
            TraceEvent data,
            TraceThread thread,
            out StackSourceCallStackIndex stitchedStack)
        {
            stitchedStack = StackSourceCallStackIndex.Invalid;

            CallStackIndex callStackIndex = data.CallStackIndex();
            if (callStackIndex == CallStackIndex.Invalid)
            {
                return false;
            }

#pragma warning disable CS0618 // Exact QPC alignment is required and remains internal to TraceEvent.
            long qpc = data.TimeStampQPC;
#pragma warning restore CS0618

            ProcessIndex processIndex = thread.Process.ProcessIndex;
            m_index.GetAsyncCallStacks(
                new AsyncThreadKey(processIndex, (ulong)thread.ThreadID), qpc, m_segments);
            if (m_segments.Count == 0)
            {
                return false;
            }

            MaterializeSyncLeafToRoot(callStackIndex);
            AsyncRuntimeRuleProfile runtimeProfile = m_runtimeProfiles.GetProcessProfile(processIndex);

            m_sampleDiagnostics.MessageLimit = m_diagnostics.RemainingMessageCapacity;
            bool contextAncestryAvailable = false;
            if (m_transforms.EnableContextAncestry && m_index.HasCreationRecords)
            {
                if (m_contextAncestryAugmenter == null)
                {
                    m_contextAncestryAugmenter = new AsyncContextAncestryAugmenter();
                }
                contextAncestryAvailable =
                    m_contextAncestryAugmenter.Prepare(m_index, m_segments);
                if (contextAncestryAvailable && m_segmentPlacements == null)
                {
                    m_segmentPlacements = new List<AsyncSegmentPlacement>();
                }
            }
            AsyncCpuStackStitcher.StitchInto(
                m_syncFrames,
                m_segments,
                qpc,
                m_classify,
                m_methodCompletionObserved,
                processIndex,
                m_methodOf,
                TraceSteps,
                m_stitchedFrames,
                m_sampleDiagnostics,
                contextAncestryAvailable ? m_segmentPlacements : null,
                m_runtimeProfileResolver, runtimeProfile, m_eventLog);
            if (contextAncestryAvailable)
            {
                m_contextAncestryAugmenter.Augment(
                    m_index,
                    m_segments,
                    m_segmentPlacements,
                    m_stitchedFrames,
                    m_transforms.MaximumContextAncestryDepth);
            }
            var transformContext = new AsyncStackTransformContext(
                m_eventLog,
                m_segments,
                qpc,
                m_classify,
                m_sampleDiagnostics,
                m_stitchedFrames,
                m_runtimeProfileResolver, runtimeProfile);
            m_transforms.ApplyInPlace(transformContext);
            m_sampleDiagnostics.AddTo(m_diagnostics);

            stitchedStack = InternStitchedStack(m_rootFactory(data, thread), processIndex);
            return true;
        }

        /// <summary>
        /// Drops references that can retain the async index while preserving diagnostics for callers.
        /// </summary>
        internal void ClearResources()
        {
            m_index = null;
            m_boundaries = null;
            m_runtimeProfiles = null;
            m_runtimeProfileResolver = null;
            m_classify = null;
            m_methodCompletionObserved = null;
            m_methodOf = null;
            m_canonicalMethodByMethodIndex = null;
            m_canonicalMethodByIdentity = null;
            m_segments = null;
            m_syncFrames = null;
            m_stitchedFrames = null;
            m_segmentPlacements = null;
            m_contextAncestryAugmenter?.Clear();
            m_contextAncestryAugmenter = null;
            m_syncFrameMetadata = null;
            m_boxedStateMachineMethods = null;
            m_stateMachineMethodByTypeName = null;
            m_sampleDiagnostics = null;
            m_logicalSyncFrameByCodeAddress = null;
            m_frameByIdentity = null;
            m_rootFactory = null;
        }

        #region private

        private AsyncCpuStackSourceBuilder(
            TraceLog eventLog,
            MutableTraceEventStackSource outputStackSource,
            AsyncCallStacksIndex index,
            AsyncStackTransformPipeline transforms,
            Func<TraceEvent, TraceThread, StackSourceCallStackIndex> rootFactory)
        {
            m_eventLog = eventLog;
            m_outputStackSource = outputStackSource;
            m_index = index;
            m_transforms = transforms;
            m_rootFactory = rootFactory;
            m_runtimeProfiles = new AsyncRuntimeRuleProfileCache(eventLog.ModuleFiles);
            m_runtimeProfileResolver = GetRuntimeProfile;
            m_boundaries = new AsyncStitchBoundaryCache(eventLog.CodeAddresses, m_runtimeProfiles);
            m_classify = m_boundaries.Classify;
            m_methodCompletionObserved = index.MethodCompletionObserved;
            m_canonicalMethodByMethodIndex = new Dictionary<MethodIndex, MethodIndex>();
            m_canonicalMethodByIdentity = new Dictionary<AsyncManagedMethodIdentity, MethodIndex>();
            m_methodOf = ca => NormalizeMethod(eventLog.CodeAddresses.MethodIndex(ca));
            m_diagnostics = new StitchDiagnostics();
            m_sampleDiagnostics = new StitchDiagnostics();
            m_segments = new List<AsyncCallStack>();
            m_syncFrames = new List<StitchSyncFrame>();
            m_stitchedFrames = new List<StitchedFrame>();
            m_syncFrameMetadata =
                new Dictionary<CodeAddressIndex, (StitchSyncFrameKind Kind, AsyncRuntimeRuleProfile Profile)>();
            m_boxedStateMachineMethods = new Dictionary<CodeAddressIndex, MethodIndex>();
            m_logicalSyncFrameByCodeAddress =
                new Dictionary<CodeAddressIndex, StackSourceFrameIndex>();
            m_frameByIdentity =
                new Dictionary<AsyncStackSourceFrameIdentity, StackSourceFrameIndex>();
        }

        private void MaterializeSyncLeafToRoot(CallStackIndex callStackIndex)
        {
            m_syncFrames.Clear();
            TraceCallStacks callStacks = m_eventLog.CallStacks;
            TraceCodeAddresses codeAddresses = m_eventLog.CodeAddresses;

            for (CallStackIndex csi = callStackIndex; csi != CallStackIndex.Invalid; csi = callStacks.Caller(csi))
            {
                CodeAddressIndex ca = callStacks.CodeAddressIndex(csi);
                MethodIndex method = ca != CodeAddressIndex.Invalid
                    ? NormalizeMethod(codeAddresses.MethodIndex(ca))
                    : MethodIndex.Invalid;
                if (!m_syncFrameMetadata.TryGetValue(ca, out var metadata))
                {
                    TraceCodeAddress codeAddress = ca == CodeAddressIndex.Invalid ? null : codeAddresses[ca];
                    string frameName = codeAddress?.FullMethodName;
                    TraceModuleFile module = ca == CodeAddressIndex.Invalid ? null : codeAddresses.ModuleFile(ca);
                    bool isHostModule = AsyncRuntimeRuleProfileCache.IsHostModule(module);
                    AsyncRuntimeRuleProfile profile = module == null
                        ? AsyncRuntimeRuleProfile.Common
                        : m_runtimeProfiles.GetProfile(module.ModuleFileIndex);
                    metadata = (profile.ClassifySynchronousFrame(frameName, isHostModule), profile);
                    m_syncFrameMetadata.Add(ca, metadata);
                }
                MethodIndex boxedMethod = method != MethodIndex.Invalid &&
                    m_classify(ca).Kind == AsyncStitchBoundaryKind.V1DispatcherInfrastructure
                    ? GetBoxedStateMachineMethod(ca)
                    : MethodIndex.Invalid;
                m_syncFrames.Add(new StitchSyncFrame(ca, method, metadata.Kind, boxedMethod));
            }
        }

        private MethodIndex GetBoxedStateMachineMethod(CodeAddressIndex codeAddressIndex)
        {
            if (m_boxedStateMachineMethods.TryGetValue(codeAddressIndex, out MethodIndex cached))
            {
                return cached;
            }

            TraceCodeAddress codeAddress = m_eventLog.CodeAddresses[codeAddressIndex];
            TraceModuleFile module = m_eventLog.CodeAddresses.ModuleFile(codeAddressIndex);
            bool isHostModule = AsyncRuntimeRuleProfileCache.IsHostModule(module);
            AsyncRuntimeRuleProfile profile = module == null
                ? AsyncRuntimeRuleProfile.Common
                : m_runtimeProfiles.GetProfile(module.ModuleFileIndex);
            MethodIndex method = MethodIndex.Invalid;
            if (profile.TryGetBoxedStateMachineTypeName(
                codeAddress.FullMethodName, isHostModule, out string stateMachineType))
            {
                if (m_stateMachineMethodByTypeName == null)
                {
                    m_stateMachineMethodByTypeName = new Dictionary<string, MethodIndex>(StringComparer.Ordinal);
                    TraceMethods methods = m_eventLog.CodeAddresses.Methods;
                    for (int i = 0; i < methods.Count; i++)
                    {
                        string typeName = AsyncStitchBoundary.GetStateMachineTypeName(methods.FullMethodName((MethodIndex)i));
                        if (typeName == null)
                        {
                            continue;
                        }

                        MethodIndex canonical = NormalizeMethod((MethodIndex)i);
                        if (m_stateMachineMethodByTypeName.TryGetValue(typeName, out MethodIndex existing) &&
                            existing != canonical)
                        {
                            // Type names alone cannot disambiguate identically named types in different modules.
                            canonical = MethodIndex.Invalid;
                        }
                        m_stateMachineMethodByTypeName[typeName] = canonical;
                    }
                }
                if (!m_stateMachineMethodByTypeName.TryGetValue(stateMachineType, out method))
                {
                    method = MethodIndex.Invalid;
                }
            }

            m_boxedStateMachineMethods.Add(codeAddressIndex, method);
            return method;
        }

        private AsyncRuntimeRuleProfile GetRuntimeProfile(CodeAddressIndex codeAddressIndex) =>
            codeAddressIndex == CodeAddressIndex.Invalid
                ? AsyncRuntimeRuleProfile.Common
                : m_syncFrameMetadata.TryGetValue(codeAddressIndex, out var metadata)
                    ? metadata.Profile
                    : m_runtimeProfiles.GetProfile(m_eventLog.CodeAddresses.ModuleFileIndex(codeAddressIndex));

        private MethodIndex NormalizeMethod(MethodIndex method)
        {
            if (method == MethodIndex.Invalid)
            {
                return MethodIndex.Invalid;
            }
            if (m_canonicalMethodByMethodIndex.TryGetValue(method, out MethodIndex canonical))
            {
                return canonical;
            }

            TraceMethod traceMethod = m_eventLog.CodeAddresses.Methods[method];
            if (traceMethod.MethodToken == 0)
            {
                m_canonicalMethodByMethodIndex.Add(method, method);
                return method;
            }

            var identity = new AsyncManagedMethodIdentity(
                traceMethod.MethodModuleFileIndex, traceMethod.MethodToken, traceMethod.FullMethodName);
            if (!m_canonicalMethodByIdentity.TryGetValue(identity, out canonical))
            {
                canonical = method;
                m_canonicalMethodByIdentity.Add(identity, canonical);
            }
            m_canonicalMethodByMethodIndex.Add(method, canonical);
            return canonical;
        }

        private StackSourceCallStackIndex InternStitchedStack(
            StackSourceCallStackIndex root,
            ProcessIndex processIndex)
        {
            StackSourceCallStackIndex caller = root;
            StitchedFrame rootwardFrame = default;
            bool hasRootwardFrame = false;

            for (int i = m_stitchedFrames.Count - 1; i >= 0; i--)
            {
                StitchedFrame currentFrame = m_stitchedFrames[i];
                if (hasRootwardFrame && ShouldEmitContextTransition(rootwardFrame, currentFrame))
                {
                    caller = m_outputStackSource.Interner.CallStackIntern(
                        InternAsyncContextTransitionFrame(), caller);
                }

                StackSourceFrameIndex frame = InternStitchedFrame(currentFrame, processIndex);
                caller = m_outputStackSource.Interner.CallStackIntern(frame, caller);
                rootwardFrame = currentFrame;
                hasRootwardFrame = true;
            }

            return caller;
        }

        private StackSourceFrameIndex InternStitchedFrame(StitchedFrame frame, ProcessIndex processIndex)
        {
            if (frame.Origin == StitchedFrameOrigin.IncompleteRoot)
            {
                // Match synchronous roots; exporters omit the reserved Broken frame index.
                return m_outputStackSource.Interner.FrameIntern(
                    "BROKEN", m_outputStackSource.Interner.ModuleIntern(string.Empty));
            }

            if (frame.Presentation == StitchedFramePresentation.AsyncDispatcherFrames)
            {
                if (m_asyncDispatcherFrame == StackSourceFrameIndex.Invalid)
                {
                    m_asyncDispatcherFrame =
                        m_outputStackSource.Interner.FrameIntern("[Async dispatcher frames]");
                }
                return m_asyncDispatcherFrame;
            }

            if (frame.Origin != StitchedFrameOrigin.Sync && frame.Segment != null)
            {
                return InternAsyncFrame(frame, processIndex);
            }

            if (frame.Presentation == StitchedFramePresentation.LogicalStateMachineMethod &&
                frame.CodeAddress != CodeAddressIndex.Invalid)
            {
                if (m_logicalSyncFrameByCodeAddress.TryGetValue(
                    frame.CodeAddress, out StackSourceFrameIndex cachedFrame))
                {
                    return cachedFrame != StackSourceFrameIndex.Invalid
                        ? cachedFrame
                        : m_outputStackSource.GetFrameIndex(frame.CodeAddress);
                }

                TraceCodeAddress codeAddress = m_eventLog.CodeAddresses[frame.CodeAddress];
                if (AsyncStitchBoundary.TryGetLogicalStateMachineMethodName(
                    codeAddress.FullMethodName,
                    out string logicalName,
                    out string stateMachineSuffix))
                {
                    StackSourceModuleIndex module =
                        m_outputStackSource.Interner.ModuleIntern(codeAddress.ModuleName);
                    StackSourceFrameIndex logicalFrame =
                        m_outputStackSource.Interner.FrameIntern(
                            FormatUnresolvedV1Method(logicalName, stateMachineSuffix),
                            module);
                    m_logicalSyncFrameByCodeAddress.Add(frame.CodeAddress, logicalFrame);
                    return logicalFrame;
                }

                m_logicalSyncFrameByCodeAddress.Add(frame.CodeAddress, StackSourceFrameIndex.Invalid);
            }

            return frame.CodeAddress != CodeAddressIndex.Invalid
                ? m_outputStackSource.GetFrameIndex(frame.CodeAddress)
                : m_outputStackSource.Interner.FrameIntern("?!?");
        }

        private StackSourceFrameIndex InternAsyncFrame(StitchedFrame frame, ProcessIndex processIndex)
        {
            AsyncCallStackFrames segment = frame.Segment;
            int segmentFrameIndex = frame.SegmentFrameIndex;
            AsyncCallstackKind kind = segment.Kind;
            ulong methodId = segment.MethodIdAt(segmentFrameIndex);
            int state = kind == AsyncCallstackKind.StateMachineAsync
                ? segment.FrameStateAt(segmentFrameIndex)
                : 0;
            bool historicalParent =
                frame.ContextKind == StitchedFrameContextKind.HistoricalParent;
            bool dispatchTarget = frame.Origin == StitchedFrameOrigin.AsyncDispatchTarget;
            bool current = frame.Origin == StitchedFrameOrigin.AsyncCurrent;
            bool completing = frame.Origin == StitchedFrameOrigin.AsyncCompleting;
            int presentationState = completing ? -2 : (current ? -1 : state);
            bool annotateActive =
                m_transforms.EnableActiveAsyncFrameAnnotations &&
                frame.ContextKind == StitchedFrameContextKind.Active;
            var identity = new AsyncStackSourceFrameIdentity(
                kind,
                processIndex,
                frame.CodeAddress,
                methodId,
                state,
                frame.ContextKind,
                annotateActive,
                frame.Origin,
                frame.IsStateMachineBoxCarrier);
            if (m_frameByIdentity.TryGetValue(identity, out StackSourceFrameIndex existingFrame))
            {
                return existingFrame;
            }

            int identityTag = m_outputStackSource.NextAsyncFrameIdentityTag();
            string prefix = historicalParent
                ? GetAnnotationPrefix(kind, historicalParent: true)
                : (annotateActive
                    ? GetAnnotationPrefix(kind, historicalParent: false)
                    : string.Empty);
            StackSourceFrameIndex asyncFrame;
            if (frame.Presentation == StitchedFramePresentation.LogicalStateMachineMethod &&
                frame.CodeAddress != CodeAddressIndex.Invalid)
            {
                TraceCodeAddress codeAddress = m_eventLog.CodeAddresses[
                    frame.IsStateMachineBoxCarrier ? segment.CodeAddressAt(segmentFrameIndex) : frame.CodeAddress];
                if (AsyncStitchBoundary.TryGetLogicalStateMachineMethodName(
                    codeAddress.FullMethodName,
                    out string logicalName,
                    out string stateMachineSuffix))
                {
                    StackSourceModuleIndex module =
                        m_outputStackSource.Interner.ModuleIntern(codeAddress.ModuleName);
                    asyncFrame =
                        m_outputStackSource.Interner.FrameIntern(
                            FormatUnresolvedV1Method(
                                logicalName,
                                stateMachineSuffix,
                                historicalParent || annotateActive ? presentationState : (int?)null),
                            module,
                            identityTag,
                            prefix);
                }
                else if (prefix.Length != 0)
                {
                    StackSourceModuleIndex module =
                        m_outputStackSource.Interner.ModuleIntern(codeAddress.ModuleName);
                    asyncFrame = m_outputStackSource.Interner.FrameIntern(
                        codeAddress.FullMethodName, module, identityTag, prefix);
                }
                else
                {
                    asyncFrame = m_outputStackSource.Interner.FrameIntern(
                        m_outputStackSource.GetFrameIndex(frame.CodeAddress), string.Empty, identityTag);
                }
            }
            else if (frame.CodeAddress != CodeAddressIndex.Invalid)
            {
                if (prefix.Length != 0)
                {
                    TraceCodeAddress codeAddress = m_eventLog.CodeAddresses[frame.CodeAddress];
                    StackSourceModuleIndex module =
                        m_outputStackSource.Interner.ModuleIntern(codeAddress.ModuleName);
                    asyncFrame = m_outputStackSource.Interner.FrameIntern(
                        codeAddress.FullMethodName, module, identityTag, prefix);
                }
                else
                {
                    asyncFrame = m_outputStackSource.Interner.FrameIntern(
                        m_outputStackSource.GetFrameIndex(frame.CodeAddress), string.Empty, identityTag);
                }
            }
            else
            {
                string name =
                    prefix +
                    "AsyncFrame(0x" + methodId.ToString("x") + ")";
                asyncFrame = m_outputStackSource.Interner.FrameIntern(
                    name, StackSourceModuleIndex.Invalid, identityTag);
            }

            m_frameByIdentity.Add(identity, asyncFrame);
            m_outputStackSource.SetAsyncFrameInfo(
                asyncFrame,
                new AsyncStackSourceFrameInfo(
                    kind, processIndex, frame.CodeAddress, methodId, state, historicalParent, dispatchTarget,
                    current, frame.IsStateMachineBoxCarrier, completing));
            return asyncFrame;
        }

        private static string FormatUnresolvedV1Method(
            string logicalName,
            string stateMachineSuffix,
            int? state = null)
        {
            string ordinal = stateMachineSuffix.StartsWith("d__", StringComparison.Ordinal)
                ? stateMachineSuffix.Substring(3)
                : (stateMachineSuffix.StartsWith("d_", StringComparison.Ordinal)
                    ? stateMachineSuffix.Substring(2)
                    : stateMachineSuffix);
            return state.HasValue
                ? logicalName + " [ordinal " + ordinal + ", state " +
                    state.Value.ToString(CultureInfo.InvariantCulture) + "]"
                : logicalName + " [ordinal " + ordinal + "]";
        }

        private static string GetAnnotationPrefix(
            AsyncCallstackKind kind,
            bool historicalParent)
        {
            string version = kind == AsyncCallstackKind.StateMachineAsync
                ? "V1"
                : "V2";
            return historicalParent
                ? "[Async parent " + version + "] "
                : "[Async " + version + "] ";
        }

        private StackSourceFrameIndex InternAsyncContextTransitionFrame()
        {
            if (m_asyncContextTransitionFrame == StackSourceFrameIndex.Invalid)
            {
                m_asyncContextTransitionFrame =
                    m_outputStackSource.Interner.FrameIntern(
                        "[Async parent context transition]");
            }
            return m_asyncContextTransitionFrame;
        }

        private static bool ShouldEmitContextTransition(
            StitchedFrame rootwardFrame,
            StitchedFrame leafwardFrame)
        {
            if (SameContext(rootwardFrame, leafwardFrame))
            {
                return false;
            }

            return rootwardFrame.ContextKind == StitchedFrameContextKind.HistoricalParent ||
                   leafwardFrame.ContextKind == StitchedFrameContextKind.HistoricalParent;
        }

        private static bool SameContext(StitchedFrame left, StitchedFrame right)
        {
            if (left.ContextKind != right.ContextKind)
            {
                return false;
            }
            if (ReferenceEquals(left.Context, right.Context))
            {
                return true;
            }
            if (left.Context == null || right.Context == null)
            {
                return false;
            }

            return left.Context.Thread.Equals(right.Context.Thread) &&
                   left.Context.DispatcherId == right.Context.DispatcherId &&
                   left.Context.StartQpc == right.Context.StartQpc;
        }

        private readonly TraceLog m_eventLog;
        private readonly MutableTraceEventStackSource m_outputStackSource;
        private readonly AsyncStackTransformPipeline m_transforms;
        private AsyncCallStacksIndex m_index;
        private AsyncStitchBoundaryCache m_boundaries;
        private AsyncRuntimeRuleProfileCache m_runtimeProfiles;
        private Func<CodeAddressIndex, AsyncRuntimeRuleProfile> m_runtimeProfileResolver;
        private Func<CodeAddressIndex, AsyncStitchBoundaryInfo> m_classify;
        private Func<ProcessIndex, AsyncCallstackKind, long, bool> m_methodCompletionObserved;
        private Func<CodeAddressIndex, MethodIndex> m_methodOf;
        private Dictionary<MethodIndex, MethodIndex> m_canonicalMethodByMethodIndex;
        private Dictionary<AsyncManagedMethodIdentity, MethodIndex> m_canonicalMethodByIdentity;
        private readonly StitchDiagnostics m_diagnostics;
        private StitchDiagnostics m_sampleDiagnostics;
        private List<AsyncCallStack> m_segments;
        private List<StitchSyncFrame> m_syncFrames;
        private List<StitchedFrame> m_stitchedFrames;
        private List<AsyncSegmentPlacement> m_segmentPlacements;
        private AsyncContextAncestryAugmenter m_contextAncestryAugmenter;
        private Dictionary<CodeAddressIndex, (StitchSyncFrameKind Kind, AsyncRuntimeRuleProfile Profile)> m_syncFrameMetadata;
        private Dictionary<CodeAddressIndex, MethodIndex> m_boxedStateMachineMethods;
        private Dictionary<string, MethodIndex> m_stateMachineMethodByTypeName;
        private Dictionary<CodeAddressIndex, StackSourceFrameIndex> m_logicalSyncFrameByCodeAddress;
        private Dictionary<AsyncStackSourceFrameIdentity, StackSourceFrameIndex> m_frameByIdentity;
        private StackSourceFrameIndex m_asyncDispatcherFrame = StackSourceFrameIndex.Invalid;
        private StackSourceFrameIndex m_asyncContextTransitionFrame = StackSourceFrameIndex.Invalid;
        private Func<TraceEvent, TraceThread, StackSourceCallStackIndex> m_rootFactory;

        private readonly struct AsyncStackSourceFrameIdentity : IEquatable<AsyncStackSourceFrameIdentity>
        {
            public AsyncStackSourceFrameIdentity(
                AsyncCallstackKind kind,
                ProcessIndex processIndex,
                CodeAddressIndex codeAddress,
                ulong methodId,
                int state,
                StitchedFrameContextKind contextKind,
                bool annotateActive,
                StitchedFrameOrigin origin,
                bool stateMachineBoxCarrier)
            {
                Kind = kind;
                ProcessIndex = processIndex;
                CodeAddress = codeAddress;
                MethodId = methodId;
                State = state;
                ContextKind = contextKind;
                AnnotateActive = annotateActive;
                Origin = origin;
                StateMachineBoxCarrier = stateMachineBoxCarrier;
            }

            public bool Equals(AsyncStackSourceFrameIdentity other) =>
                Kind == other.Kind &&
                ProcessIndex == other.ProcessIndex &&
                CodeAddress == other.CodeAddress &&
                MethodId == other.MethodId &&
                State == other.State &&
                ContextKind == other.ContextKind &&
                AnnotateActive == other.AnnotateActive &&
                Origin == other.Origin &&
                StateMachineBoxCarrier == other.StateMachineBoxCarrier;

            public override bool Equals(object obj) =>
                obj is AsyncStackSourceFrameIdentity other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = (int)Kind;
                    hash = (hash * 397) ^ (int)ProcessIndex;
                    hash = (hash * 397) ^ (int)CodeAddress;
                    hash = (hash * 397) ^ MethodId.GetHashCode();
                    hash = (hash * 397) ^ State;
                    hash = (hash * 397) ^ (int)ContextKind;
                    hash = (hash * 397) ^ AnnotateActive.GetHashCode();
                    hash = (hash * 397) ^ (int)Origin;
                    return (hash * 397) ^ StateMachineBoxCarrier.GetHashCode();
                }
            }

            private readonly AsyncCallstackKind Kind;
            private readonly ProcessIndex ProcessIndex;
            private readonly CodeAddressIndex CodeAddress;
            private readonly ulong MethodId;
            private readonly int State;
            private readonly StitchedFrameContextKind ContextKind;
            private readonly bool AnnotateActive;
            private readonly StitchedFrameOrigin Origin;
            private readonly bool StateMachineBoxCarrier;
        }

        private readonly struct AsyncManagedMethodIdentity : IEquatable<AsyncManagedMethodIdentity>
        {
            public AsyncManagedMethodIdentity(ModuleFileIndex module, int token, string name)
            {
                Module = module;
                Token = token;
                Name = name;
            }

            public bool Equals(AsyncManagedMethodIdentity other) =>
                Module == other.Module && Token == other.Token &&
                string.Equals(Name, other.Name, StringComparison.Ordinal);

            public override bool Equals(object obj) =>
                obj is AsyncManagedMethodIdentity other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = ((int)Module * 397) ^ Token;
                    return (hash * 397) ^ (Name?.GetHashCode() ?? 0);
                }
            }

            private readonly ModuleFileIndex Module;
            private readonly int Token;
            private readonly string Name;
        }

        #endregion
    }
}
