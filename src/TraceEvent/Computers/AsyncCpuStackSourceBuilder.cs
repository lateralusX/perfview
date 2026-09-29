using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;
using Microsoft.Diagnostics.Tracing.Stacks;
using System;
using System.Collections.Generic;
using System.Diagnostics;

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

            m_sampleDiagnostics.MessageLimit = m_diagnostics.RemainingMessageCapacity;
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
                m_sampleDiagnostics);
            var transformContext = new AsyncStackTransformContext(
                m_eventLog,
                m_segments,
                qpc,
                m_classify,
                m_sampleDiagnostics,
                m_stitchedFrames);
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
            m_classify = null;
            m_methodCompletionObserved = null;
            m_methodOf = null;
            m_canonicalMethodByMethodIndex = null;
            m_canonicalMethodByIdentity = null;
            m_segments = null;
            m_syncFrames = null;
            m_stitchedFrames = null;
            m_syncFrameKinds = null;
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
            m_boundaries = new AsyncStitchBoundaryCache(eventLog.CodeAddresses);
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
            m_syncFrameKinds = new Dictionary<CodeAddressIndex, StitchSyncFrameKind>();
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
                if (!m_syncFrameKinds.TryGetValue(ca, out StitchSyncFrameKind kind))
                {
                    TraceCodeAddress codeAddress = ca == CodeAddressIndex.Invalid ? null : codeAddresses[ca];
                    string frameName = codeAddress?.FullMethodName;
                    bool isHostModule = codeAddress != null &&
                        (string.Equals(
                             codeAddress.ModuleName,
                             AsyncStitchBoundary.HostModuleName,
                             StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(
                             codeAddress.ModuleName,
                             AsyncStitchBoundary.HostModuleName + ".dll",
                             StringComparison.OrdinalIgnoreCase));
                    kind = AsyncStitchBoundary.ClassifyV1SynchronousFrame(frameName, isHostModule);
                    m_syncFrameKinds.Add(ca, kind);
                }
                m_syncFrames.Add(new StitchSyncFrame(ca, method, kind));
            }
        }

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

            for (int i = m_stitchedFrames.Count - 1; i >= 0; i--)
            {
                StackSourceFrameIndex frame = InternStitchedFrame(m_stitchedFrames[i], processIndex);
                caller = m_outputStackSource.Interner.CallStackIntern(frame, caller);
            }

            return caller;
        }

        private StackSourceFrameIndex InternStitchedFrame(StitchedFrame frame, ProcessIndex processIndex)
        {
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
                    codeAddress.FullMethodName, out string logicalName))
                {
                    StackSourceModuleIndex module =
                        m_outputStackSource.Interner.ModuleIntern(codeAddress.ModuleName);
                    StackSourceFrameIndex logicalFrame =
                        m_outputStackSource.Interner.FrameIntern(logicalName, module);
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
            var identity = new AsyncStackSourceFrameIdentity(
                kind, processIndex, frame.CodeAddress, methodId, state);
            if (m_frameByIdentity.TryGetValue(identity, out StackSourceFrameIndex existingFrame))
            {
                return existingFrame;
            }

            int identityTag = m_outputStackSource.NextAsyncFrameIdentityTag();
            StackSourceFrameIndex asyncFrame;
            if (frame.Presentation == StitchedFramePresentation.LogicalStateMachineMethod &&
                frame.CodeAddress != CodeAddressIndex.Invalid)
            {
                TraceCodeAddress codeAddress = m_eventLog.CodeAddresses[frame.CodeAddress];
                if (AsyncStitchBoundary.TryGetLogicalStateMachineMethodName(
                    codeAddress.FullMethodName, out string logicalName))
                {
                    StackSourceModuleIndex module =
                        m_outputStackSource.Interner.ModuleIntern(codeAddress.ModuleName);
                    asyncFrame =
                        m_outputStackSource.Interner.FrameIntern(logicalName, module, identityTag);
                }
                else
                {
                    asyncFrame = m_outputStackSource.Interner.FrameIntern(
                        m_outputStackSource.GetFrameIndex(frame.CodeAddress), string.Empty, identityTag);
                }
            }
            else if (frame.CodeAddress != CodeAddressIndex.Invalid)
            {
                asyncFrame = m_outputStackSource.Interner.FrameIntern(
                    m_outputStackSource.GetFrameIndex(frame.CodeAddress), string.Empty, identityTag);
            }
            else
            {
                string name = "AsyncFrame(0x" + methodId.ToString("x") + ")";
                asyncFrame = m_outputStackSource.Interner.FrameIntern(
                    name, StackSourceModuleIndex.Invalid, identityTag);
            }

            m_frameByIdentity.Add(identity, asyncFrame);
            m_outputStackSource.SetAsyncFrameInfo(
                asyncFrame,
                new AsyncStackSourceFrameInfo(kind, processIndex, frame.CodeAddress, methodId, state));
            return asyncFrame;
        }

        private readonly TraceLog m_eventLog;
        private readonly MutableTraceEventStackSource m_outputStackSource;
        private readonly AsyncStackTransformPipeline m_transforms;
        private AsyncCallStacksIndex m_index;
        private AsyncStitchBoundaryCache m_boundaries;
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
        private Dictionary<CodeAddressIndex, StitchSyncFrameKind> m_syncFrameKinds;
        private Dictionary<CodeAddressIndex, StackSourceFrameIndex> m_logicalSyncFrameByCodeAddress;
        private Dictionary<AsyncStackSourceFrameIdentity, StackSourceFrameIndex> m_frameByIdentity;
        private Func<TraceEvent, TraceThread, StackSourceCallStackIndex> m_rootFactory;

        private readonly struct AsyncStackSourceFrameIdentity : IEquatable<AsyncStackSourceFrameIdentity>
        {
            public AsyncStackSourceFrameIdentity(
                AsyncCallstackKind kind,
                ProcessIndex processIndex,
                CodeAddressIndex codeAddress,
                ulong methodId,
                int state)
            {
                Kind = kind;
                ProcessIndex = processIndex;
                CodeAddress = codeAddress;
                MethodId = methodId;
                State = state;
            }

            public bool Equals(AsyncStackSourceFrameIdentity other) =>
                Kind == other.Kind &&
                ProcessIndex == other.ProcessIndex &&
                CodeAddress == other.CodeAddress &&
                MethodId == other.MethodId &&
                State == other.State;

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
                    return (hash * 397) ^ State;
                }
            }

            private readonly AsyncCallstackKind Kind;
            private readonly ProcessIndex ProcessIndex;
            private readonly CodeAddressIndex CodeAddress;
            private readonly ulong MethodId;
            private readonly int State;
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
