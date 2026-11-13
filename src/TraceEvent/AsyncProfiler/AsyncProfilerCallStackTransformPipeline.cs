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
    /// Per-sample context supplied to an async stack presentation transform. Structural stitching has already
    /// completed, and <see cref="Frames"/> contains the complete stitched stack leaf-to-root.
    /// </summary>
    internal readonly struct AsyncProfilerCallStackTransformState
    {
        internal AsyncProfilerCallStackTransformState(
            TraceLog traceLog,
            IReadOnlyList<AsyncProfilerCallStack> segments,
            long timestampQpc,
            Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classifyBoundary,
            AsyncProfilerCallStackDiagnostics diagnostics,
            List<AsyncProfilerStitchedFrame> frames,
            Func<CodeAddressIndex, AsyncProfilerRuntimeRuleProfile> runtimeProfileResolver = null,
            AsyncProfilerRuntimeRuleProfile runtimeProfile = null,
            AsyncProfilerRuntimeRuleProfileCache runtimeProfiles = null,
            IReadOnlyList<AsyncProfilerSegmentPlacement> segmentPlacements = null,
            Func<CodeAddressIndex, MethodIndex> methodOf = null)
        {
            TraceLog = traceLog;
            Segments = segments;
            TimestampQpc = timestampQpc;
            m_classifyBoundary = classifyBoundary;
            Diagnostics = diagnostics;
            Frames = frames;
            m_segmentPlacements = segmentPlacements;
            m_methodOf = methodOf;
            m_runtimeProfiles = runtimeProfiles ??
                (traceLog == null ? null : new AsyncProfilerRuntimeRuleProfileCache(traceLog.ModuleFiles));
            m_runtimeProfileResolver = runtimeProfileResolver ?? CreateRuntimeProfileResolver(traceLog, m_runtimeProfiles);
            RuntimeProfile = runtimeProfile ?? ResolveRuntimeProfile(
                traceLog, segments, frames, m_runtimeProfileResolver, m_runtimeProfiles, runtimeProfileResolver == null);
        }

        /// <summary>
        /// The trace whose code addresses are referenced by <see cref="Frames"/>, or null if a standalone
        /// <see cref="AsyncProfilerCallStackTransformPipeline.Transform"/> call supplies no trace context. Built-in transforms
        /// requiring module or symbol metadata fail open when this is null.
        /// </summary>
        public TraceLog TraceLog { get; }

        /// <summary>The active async segments used by structural stitching, ordered leaf-to-root.
        /// During computer callbacks, the list and its activation objects are reused and must not be retained.</summary>
        public IReadOnlyList<AsyncProfilerCallStack> Segments { get; }

        /// <summary>The sample timestamp in the trace QPC domain.</summary>
        public long TimestampQpc { get; }

        /// <summary>Diagnostics for this sample. Built-in and custom transforms may add presentation counters.</summary>
        public AsyncProfilerCallStackDiagnostics Diagnostics { get; }

        /// <summary>The complete mutable stitched stack, leaf-to-root.</summary>
        public List<AsyncProfilerStitchedFrame> Frames { get; }

        /// <summary>Classifies a frame using the runtime boundary-name contract for this trace.</summary>
        public AsyncProfilerCallStackStitchBoundaryInfo ClassifyBoundary(CodeAddressIndex codeAddress) =>
            m_classifyBoundary(codeAddress);

        /// <summary>
        /// Unambiguous captured runtime for whole-stage replacement and application-led layouts. Fixed before
        /// any pass removes runtime evidence; mixed profiles use Common, never another sample's selection.
        /// </summary>
        internal AsyncProfilerRuntimeRuleProfile RuntimeProfile { get; }

        internal AsyncProfilerRuntimeRuleProfile GetRuntimeProfile(CodeAddressIndex codeAddress) =>
            m_runtimeProfileResolver(codeAddress) ??
            throw new InvalidOperationException("The runtime profile resolver returned null.");

        internal ModuleFileIndex GetHostModuleOwner(CodeAddressIndex codeAddress) =>
            TraceLog == null || codeAddress == CodeAddressIndex.Invalid
                ? ModuleFileIndex.Invalid
                : m_runtimeProfiles.GetHostModuleOwner(TraceLog.CodeAddresses.ModuleFileIndex(codeAddress));

        /// <summary>Uses the stitcher's canonical identity across captured and sampled code versions.</summary>
        internal MethodIndex GetMethod(CodeAddressIndex codeAddress)
        {
            if (codeAddress == CodeAddressIndex.Invalid)
            {
                return MethodIndex.Invalid;
            }
            if (m_methodOf != null)
            {
                return m_methodOf(codeAddress);
            }
            return TraceLog == null ? MethodIndex.Invalid : TraceLog.CodeAddresses.MethodIndex(codeAddress);
        }

        internal bool IsV2DispatchPair(CodeAddressIndex instrumented, CodeAddressIndex dispatch) =>
            TraceLog != null &&
            AsyncProfilerCallStackStitcher.IsV2DispatchPair(TraceLog, m_runtimeProfiles, instrumented, dispatch);

        internal bool HasVerifiedV2DispatchPair(AsyncProfilerCallStack activation, CodeAddressIndex bridge)
        {
            if (TraceLog == null || m_segmentPlacements == null)
            {
                return false;
            }
            ModuleFileIndex owner = GetHostModuleOwner(bridge);
            foreach (AsyncProfilerSegmentPlacement placement in m_segmentPlacements)
            {
                if (ReferenceEquals(placement.Activation, activation) &&
                    owner != ModuleFileIndex.Invalid &&
                    owner == GetHostModuleOwner(placement.InstrumentedDispatch) &&
                    ReferenceEquals(GetRuntimeProfile(bridge), GetRuntimeProfile(placement.InstrumentedDispatch)) &&
                    AsyncProfilerCallStackStitcher.IsV2DispatchPair(
                        TraceLog, m_runtimeProfiles, placement.InstrumentedDispatch, placement.Dispatch))
                {
                    return true;
                }
            }
            return false;
        }

        internal bool HasVerifiedDispatchEntry(
            AsyncProfilerCallStack activation, CodeAddressIndex bridge, bool requireAsyncAnchor = true)
        {
            if (activation.Frames.Kind == AsyncCallstackKind.RuntimeAsync)
            {
                return HasVerifiedV2DispatchPair(activation, bridge);
            }
            if (TraceLog == null || m_segmentPlacements == null)
            {
                return false;
            }
            ModuleFileIndex owner = GetHostModuleOwner(bridge);
            foreach (AsyncProfilerSegmentPlacement placement in m_segmentPlacements)
            {
                if (!ReferenceEquals(placement.Activation, activation) ||
                    owner == ModuleFileIndex.Invalid || owner != GetHostModuleOwner(placement.Dispatch) ||
                    !ReferenceEquals(GetRuntimeProfile(bridge), GetRuntimeProfile(placement.Dispatch)) ||
                    ClassifyBoundary(placement.Dispatch).Kind != AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher ||
                    TraceLog.CodeAddresses.MethodIndex(placement.Dispatch) == MethodIndex.Invalid)
                {
                    continue;
                }
                // Grouping dispatch machinery does not require the pending application target to have started.
                if (!requireAsyncAnchor)
                {
                    return true;
                }
                for (int i = Frames.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(Frames[i].Context, activation))
                    {
                        return HasVerifiedAsyncAnchor(this, i, activation);
                    }
                }
            }
            return false;
        }

        /// <summary>Verifies a V1 completion owner for machinery cleanup, not an executing target.
        /// Bounded resumption bridges can also accept a normalized, fully completed return unwind.</summary>
        internal bool HasVerifiedV1CompletionAnchor(
            AsyncProfilerCallStack activation, CodeAddressIndex bridge, bool allowCompletionUnwind = false)
        {
            if (activation.Frames.Kind != AsyncCallstackKind.StateMachineAsync ||
                Diagnostics.SegmentsProcessed != Segments.Count ||
                Diagnostics.BoundariesNotFound != 0 || Diagnostics.AdjacencyMismatches != 0 ||
                Diagnostics.V1CompletionAlignmentAmbiguous != 0 ||
                !HasVerifiedDispatchEntry(activation, bridge, requireAsyncAnchor: false))
            {
                return false;
            }

            int completed = activation.GetCompletedFrameCount(TimestampQpc);
            if (completed <= 0 || completed > activation.Frames.FrameCount)
            {
                return false;
            }
            CodeAddressIndex captured = activation.Frames.CodeAddressAt(completed - 1);
            if (captured == CodeAddressIndex.Invalid)
            {
                return false;
            }
            foreach (AsyncProfilerStitchedFrame frame in Frames)
            {
                if (allowCompletionUnwind &&
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompletionUnwind &&
                    !frame.IsDispatcherTailUnwind &&
                    frame.ContextKind == AsyncProfilerStitchedFrameContextKind.Active &&
                    ReferenceEquals(frame.Context, activation) &&
                    completed == activation.Frames.FrameCount &&
                    frame.SegmentFrameIndex == completed - 1 &&
                    frame.CodeAddress == captured && frame.Method == MethodIndex.Invalid &&
                    GetMethod(captured) != MethodIndex.Invalid &&
                    TraceLog.AsyncProfilerCallStacks?.MethodCompletionObserved(activation) == true &&
                    activation.GetMethodCompletedFrameCount(TimestampQpc) > 0)
                {
                    return true;
                }
                if (frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompleting &&
                    frame.ContextKind == AsyncProfilerStitchedFrameContextKind.Active &&
                    ReferenceEquals(frame.Context, activation) &&
                    frame.SegmentFrameIndex == completed - 1 &&
                    frame.CodeAddress != CodeAddressIndex.Invalid && frame.Method != MethodIndex.Invalid &&
                    GetMethod(frame.CodeAddress) == frame.Method &&
                    GetMethod(captured) == frame.Method)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Retains machinery ownership when a fully completed outer V1 activation emitted no frames.</summary>
        internal bool HasVerifiedCompletedV1OuterDispatch(AsyncProfilerCallStack activation, CodeAddressIndex bridge)
        {
            if (TraceLog == null || m_segmentPlacements == null || Segments.Count < 2 ||
                !ReferenceEquals(activation, Segments[Segments.Count - 1]) ||
                activation.Frames.Kind != AsyncCallstackKind.StateMachineAsync || activation.Frames.FrameCount == 0 ||
                Diagnostics.SegmentsProcessed != Segments.Count ||
                Diagnostics.BoundariesNotFound != 0 || Diagnostics.AdjacencyMismatches != 0 ||
                Diagnostics.V1CompletionAlignmentAmbiguous != 0 ||
                TraceLog.AsyncProfilerCallStacks?.MethodCompletionObserved(activation) != true ||
                activation.GetMethodCompletedFrameCount(TimestampQpc) != activation.Frames.FrameCount ||
                activation.GetExceptionCompletedFrameCount(TimestampQpc) != 0 ||
                Segments[Segments.Count - 2].Depth != activation.Depth + 1 ||
                !HasVerifiedDispatchEntry(activation, bridge, requireAsyncAnchor: false) ||
                !HasVerifiedDispatchEntry(Segments[Segments.Count - 2], bridge, requireAsyncAnchor: false))
            {
                return false;
            }
            foreach (AsyncProfilerStitchedFrame frame in Frames)
            {
                if (ReferenceEquals(frame.Context, activation))
                {
                    return false;
                }
            }
            foreach (AsyncProfilerSegmentPlacement outer in m_segmentPlacements)
            {
                if (!ReferenceEquals(outer.Activation, activation) ||
                    outer.LeafIndex != outer.RootIndexExclusive)
                {
                    continue;
                }
                foreach (AsyncProfilerSegmentPlacement inner in m_segmentPlacements)
                {
                    if (ReferenceEquals(inner.Activation, Segments[Segments.Count - 2]) &&
                        inner.LeafIndex < inner.RootIndexExclusive &&
                        inner.RootIndexExclusive == outer.LeafIndex)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Matches using captured candidate ownership. Application-led startup layouts use the owning sample's
        /// runtime rather than treating the application's module version as a CoreLib version.
        /// </summary>
        internal AsyncProfilerRuntimeSequenceMatch MatchRuntimeSequence(
            AsyncProfilerRuntimeSequenceKind kind, int start, int end, bool allowRuntimeInteriors = false)
        {
            if (start < 0 || end < start || end > Frames.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(start));
            }
            if (start == end)
            {
                return default;
            }
            bool startup = kind == AsyncProfilerRuntimeSequenceKind.V1Startup;
            AsyncProfilerRuntimeRuleProfile profile = startup ? RuntimeProfile : GetRuntimeProfile(Frames[start].CodeAddress);
            AsyncProfilerRuntimeSequenceMatch match = profile.MatchSequence(kind, this, start, end, allowRuntimeInteriors);
            if (match.Length > end - start)
            {
                throw new InvalidOperationException("A runtime sequence matcher returned a span outside its candidate range.");
            }
            if (match.IsMatch &&
                (startup
                    ? !IsSyncFrameKind(Frames[start], AsyncProfilerStitchSyncFrameKind.V1StateMachineMoveNext) ||
                      !IsSameRuntimeSpan(profile, start + 1, match.Length - 1)
                    : !IsSameRuntimeSpan(profile, start, match.Length)))
            {
                return AsyncProfilerRuntimeSequenceMatch.Reject(match.Length);
            }
            return match;
        }

        #region private

        private static AsyncProfilerRuntimeRuleProfile ResolveRuntimeProfile(
            TraceLog traceLog, IReadOnlyList<AsyncProfilerCallStack> segments, List<AsyncProfilerStitchedFrame> frames,
            Func<CodeAddressIndex, AsyncProfilerRuntimeRuleProfile> runtimeProfileResolver,
            AsyncProfilerRuntimeRuleProfileCache profiles, bool useProcessProfile)
        {
            if (useProcessProfile && profiles != null && segments.Count != 0 &&
                segments[0].Thread.ProcessIndex != ProcessIndex.Invalid)
            {
                ProcessIndex process = segments[0].Thread.ProcessIndex;
                for (int i = 1; i < segments.Count; i++)
                {
                    if (segments[i].Thread.ProcessIndex != process)
                    {
                        return AsyncProfilerRuntimeRuleProfile.Common;
                    }
                }
                return profiles.GetProcessProfile(process);
            }
            AsyncProfilerRuntimeRuleProfile result = null;
            foreach (AsyncProfilerStitchedFrame frame in frames)
            {
                if (traceLog != null &&
                    (frame.CodeAddress == CodeAddressIndex.Invalid ||
                     !profiles.IsHostModule(traceLog.CodeAddresses.ModuleFileIndex(frame.CodeAddress))))
                {
                    continue;
                }
                AsyncProfilerRuntimeRuleProfile profile = runtimeProfileResolver(frame.CodeAddress) ??
                    throw new InvalidOperationException("The runtime profile resolver returned null.");
                if (result != null && !ReferenceEquals(result, profile))
                {
                    return AsyncProfilerRuntimeRuleProfile.Common;
                }
                result = profile;
            }
            return result ?? AsyncProfilerRuntimeRuleProfile.Common;
        }

        private bool IsSameRuntimeSpan(AsyncProfilerRuntimeRuleProfile profile, int start, int length)
        {
            ModuleFileIndex moduleIndex = ModuleFileIndex.Invalid;
            for (int i = start; i < start + length; i++)
            {
                AsyncProfilerStitchedFrame frame = Frames[i];
                if (frame.Origin != AsyncProfilerStitchedFrameOrigin.Sync ||
                    !ReferenceEquals(profile, GetRuntimeProfile(frame.CodeAddress)))
                {
                    return false;
                }
                if (TraceLog != null)
                {
                    if (frame.CodeAddress == CodeAddressIndex.Invalid || frame.Method == MethodIndex.Invalid)
                    {
                        return false;
                    }
                    ModuleFileIndex current = GetHostModuleOwner(frame.CodeAddress);
                    if (current == ModuleFileIndex.Invalid)
                    {
                        return false;
                    }
                    if (i != start && current != moduleIndex)
                    {
                        return false;
                    }
                    moduleIndex = current;
                }
            }
            return true;
        }

        private static Func<CodeAddressIndex, AsyncProfilerRuntimeRuleProfile> CreateRuntimeProfileResolver(
            TraceLog traceLog, AsyncProfilerRuntimeRuleProfileCache profiles)
        {
            if (traceLog == null)
            {
                return _ => AsyncProfilerRuntimeRuleProfile.Common;
            }
            return codeAddress => codeAddress == CodeAddressIndex.Invalid
                ? AsyncProfilerRuntimeRuleProfile.Common
                : profiles.GetProfile(traceLog.CodeAddresses.ModuleFileIndex(codeAddress));
        }

        private readonly Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> m_classifyBoundary;
        private readonly Func<CodeAddressIndex, AsyncProfilerRuntimeRuleProfile> m_runtimeProfileResolver;
        private readonly AsyncProfilerRuntimeRuleProfileCache m_runtimeProfiles;
        private readonly IReadOnlyList<AsyncProfilerSegmentPlacement> m_segmentPlacements;
        private readonly Func<CodeAddressIndex, MethodIndex> m_methodOf;

        #endregion
    }

    /// <summary>A presentation transform applied to a structurally stitched async call stack.</summary>
    /// <remarks>
    /// The context, its segment list, and the mutable frame list are owned and reused by the computer and must not
    /// be retained after the callback returns. Copy any identity values needed later; copying a frame alone does
    /// not detach its <see cref="AsyncProfilerStitchedFrame.Context"/> from the computer's reusable activation object.
    /// </remarks>
    internal delegate void AsyncProfilerCallStackTransform(AsyncProfilerCallStackTransformState context);

    /// <summary>
    /// Ordered presentation pipeline for structurally stitched async call stacks. The mandatory structural
    /// stitcher always runs before this pipeline. Conservative normalization is enabled by default;
    /// runtime-specific async-infrastructure cleanup is opt-in; custom transforms run last in registration order.
    /// </summary>
    /// <remarks>
    /// Configure before stack generation. Runtime profiles select built-in implementations without changing
    /// enablement or order. Parent contexts and frame annotations are applied by the host builder and emitter,
    /// not by standalone <see cref="Transform"/> calls.
    /// </remarks>
    internal sealed class AsyncProfilerCallStackTransformPipeline
    {
        /// <summary>Creates the default pipeline with conservative presentation normalization enabled.</summary>
        public AsyncProfilerCallStackTransformPipeline()
        {
            m_customTransforms = new List<AsyncProfilerCallStackTransform>();
        }

        /// <summary>
        /// Enables conservative startup, reentrant, completion-return, handoff, and sampled-wrapper normalization.
        /// Unverified work is retained. Disabling this does not disable structural boundary replacement or
        /// verified incomplete-root normalization to <c>BROKEN</c>.
        /// </summary>
        public bool EnableNormalization
        {
            get => m_enableNormalization;
            set
            {
                if (m_enableNormalization != value)
                {
                    m_enableNormalization = value;
                    m_transforms = null;
                }
            }
        }

        /// <summary>
        /// Enables the host computer's insertion of missing historical creation-parent contexts before presentation.
        /// Requires captured creation relationships and parent resume intervals; unavailable parents are not inferred.
        /// Replays recorded resume/append frames, trimming completed frames at child creation only when both normal
        /// and exceptional completion records were observed for the parent's process, kind, and configuration epoch.
        /// The first surviving V1 frame then displays inferred running state -1; captured states and IPs are retained.
        /// Disabled by default. This option is not applied by standalone <see cref="Transform"/> calls.
        /// </summary>
        public bool EnableParentContexts { get; set; }

        /// <summary>
        /// Prefixes active async-profiler frames with <c>[Async V1]</c> or <c>[Async V2]</c> when they are
        /// interned. Retained synchronous frames keep their ordinary names, and this option does not add sync/async
        /// transition frames. Dispatch targets use the same names and annotations as ordinary active async frames;
        /// their dispatch role is retained in frame metadata rather than the name. Disabled by default.
        /// Matched current V1 frames display inferred running state <c>-1</c>, and record-verified physical
        /// completing frames display <c>-2</c>; other V1 frames display
        /// their captured compiler state. Raw captured states remain available in frame metadata.
        /// The host emitter applies this option; standalone
        /// <see cref="Transform"/> calls do not produce frame names.
        /// </summary>
        public bool EnableFrameAnnotations { get; set; }

        /// <summary>The maximum number of missing parent contexts inserted for one active segment when
        /// <see cref="EnableParentContexts"/> is enabled. Defaults to one historical parent context.</summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
        public int MaximumParentContextDepth
        {
            get => m_maximumParentContextDepth;
            set
            {
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(MaximumParentContextDepth));
                }
                m_maximumParentContextDepth = value;
            }
        }

        /// <summary>
        /// Enables profile-based grouping and overhead attribution for verified V1/V2 CoreLib machinery.
        /// Disabled by default because helper layouts are implementation details, not profiler contracts.
        /// Rejected spans and unverified callees remain protected; this is not general framework filtering.
        /// Independent of structural stitching and <see cref="EnableNormalization"/>.
        /// </summary>
        public bool EnableInfrastructureCleanup
        {
            get => m_enableInfrastructureCleanup;
            set
            {
                if (m_enableInfrastructureCleanup != value)
                {
                    m_enableInfrastructureCleanup = value;
                    m_transforms = null;
                }
            }
        }

        /// <summary>Adds a custom transform that runs after all enabled built-in transforms.</summary>
        public void Add(AsyncProfilerCallStackTransform transform, bool requiresCompletionObservations = false)
        {
            if (transform is null)
            {
                throw new ArgumentNullException(nameof(transform));
            }

            if (requiresCompletionObservations && !m_hasCompletionObservationTransform)
            {
                m_customTransforms.Add(PrepareCompletionObservations);
                m_hasCompletionObservationTransform = true;
            }
            m_customTransforms.Add(transform);
            m_transforms = null;
        }

        /// <summary>Removes all custom transforms. Built-in enablement is unchanged.</summary>
        public void ClearCustomTransforms()
        {
            m_customTransforms.Clear();
            m_hasCompletionObservationTransform = false;
            m_transforms = null;
        }

        /// <summary>
        /// Applies this pipeline to a structural stitch result without requiring a
        /// <see cref="SampleProfilerThreadTimeComputer"/>. The input frame list is not modified; its frames are
        /// copied into a mutable output list. Presentation diagnostics are added to the result's existing
        /// <see cref="AsyncProfilerStitchResult.Diagnostics"/> instance, which is shared by both results. This method does not
        /// insert historical parent contexts or intern/annotate output frames.
        /// </summary>
        /// <param name="structuralResult">The result produced by <see cref="AsyncProfilerCallStackStitcher"/>.</param>
        /// <param name="segments">The active async segments used to produce the structural result.</param>
        /// <param name="timestampQpc">The sample timestamp in the trace QPC domain.</param>
        /// <param name="classifyBoundary">Classifies runtime boundary methods for the trace.</param>
        /// <param name="traceLog">
        /// Optional trace context exposed to custom transforms and used by built-in rules that require module or
        /// symbol metadata. Those built-in rules fail open when it is null.
        /// </param>
        public AsyncProfilerStitchResult Transform(
            AsyncProfilerStitchResult structuralResult,
            IReadOnlyList<AsyncProfilerCallStack> segments,
            long timestampQpc,
            Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classifyBoundary,
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

            var frames = new List<AsyncProfilerStitchedFrame>(structuralResult.Frames);
            var context = new AsyncProfilerCallStackTransformState(
                traceLog,
                segments,
                timestampQpc,
                classifyBoundary,
                structuralResult.Diagnostics,
                frames,
                segmentPlacements: structuralResult.SegmentPlacements);
            ApplyInPlace(context);
            return new AsyncProfilerStitchResult(frames, structuralResult.Diagnostics);
        }

        internal void ApplyInPlace(AsyncProfilerCallStackTransformState context)
        {
            AsyncProfilerCallStackTransform[] transforms = m_transforms ?? (m_transforms = CreateTransforms());
            for (int i = 0; i < transforms.Length; i++)
            {
                transforms[i](context);
            }
        }

        #region private

        private AsyncProfilerCallStackTransform[] CreateTransforms()
        {
            var transforms = new List<AsyncProfilerCallStackTransform>(4 + m_customTransforms.Count);
            if (EnableNormalization)
            {
                transforms.Add(context => ApplyRuntimeTransform(context, AsyncProfilerRuntimeTransformKind.SampledV2Wrapper));
                transforms.Add(context => ApplyRuntimeTransform(context, AsyncProfilerRuntimeTransformKind.ReentrantHandoff));
                transforms.Add(context => ApplyRuntimeTransform(context, AsyncProfilerRuntimeTransformKind.V1Presentation));
            }
            if (EnableInfrastructureCleanup)
            {
                transforms.Add(context => ApplyRuntimeTransform(context, AsyncProfilerRuntimeTransformKind.InfrastructureCleanup));
            }
            transforms.AddRange(m_customTransforms);
            return transforms.ToArray();
        }

        private static void PrepareCompletionObservations(AsyncProfilerCallStackTransformState context)
        {
            AsyncProfilerCallStacksIndex index = context.TraceLog?.AsyncProfilerCallStacks;
            if (index != null)
            {
                for (int i = 0; i < context.Segments.Count; i++)
                {
                    index.PopulateCompletionObservations(context.Segments[i]);
                }
            }
        }

        private static void ApplyRuntimeTransform(AsyncProfilerCallStackTransformState context, AsyncProfilerRuntimeTransformKind kind)
        {
            AsyncProfilerCallStackTransform transform = context.RuntimeProfile.GetTransform(kind) ??
                throw new InvalidOperationException("A runtime profile returned a null presentation transform.");
            transform(context);
        }

        private int m_maximumParentContextDepth = AsyncProfilerParentContextAugmenter.DefaultMaximumDepth;
        private bool m_enableNormalization = true;
        private bool m_enableInfrastructureCleanup;
        private bool m_hasCompletionObservationTransform;
        private readonly List<AsyncProfilerCallStackTransform> m_customTransforms;
        // Null until first use or after pass configuration changes; otherwise reused across samples.
        private AsyncProfilerCallStackTransform[] m_transforms;

        #endregion
    }
}
