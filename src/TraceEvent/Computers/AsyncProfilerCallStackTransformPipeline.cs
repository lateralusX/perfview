// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

using Microsoft.Diagnostics.Tracing.Etlx;

using static Microsoft.Diagnostics.Tracing.Computers.AsyncProfilerCallStackTransformHelpers;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>
    /// Per-sample context supplied to an async stack presentation transform. Structural stitching has already
    /// completed, and <see cref="Frames"/> contains the complete stitched stack leaf-to-root.
    /// </summary>
    internal readonly struct AsyncProfilerCallStackTransformContext
    {
        internal AsyncProfilerCallStackTransformContext(
            TraceLog traceLog,
            IReadOnlyList<AsyncProfilerCallStack> segments,
            long timestampQpc,
            Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classifyBoundary,
            AsyncProfilerCallStackDiagnostics diagnostics,
            List<AsyncProfilerStitchedFrame> frames,
            Func<CodeAddressIndex, AsyncProfilerRuntimeRuleProfile> runtimeProfileResolver = null,
            AsyncProfilerRuntimeRuleProfile runtimeProfile = null,
            AsyncProfilerRuntimeRuleProfileCache runtimeProfiles = null)
        {
            TraceLog = traceLog;
            Segments = segments;
            TimestampQpc = timestampQpc;
            m_classifyBoundary = classifyBoundary;
            Diagnostics = diagnostics;
            Frames = frames;
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

        /// <summary>The active async segments used by structural stitching, ordered root-to-leaf.
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

        /// <summary>
        /// Matches using captured candidate ownership. Application-led startup layouts use the owning sample's
        /// runtime rather than treating the application's module version as a CoreLib version.
        /// </summary>
        internal AsyncProfilerRuntimeSequenceMatch MatchRuntimeSequence(AsyncProfilerRuntimeSequenceKind kind, int start, int end)
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
            AsyncProfilerRuntimeSequenceMatch match = profile.MatchSequence(kind, this, start, end);
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

        #endregion
    }

    /// <summary>A presentation transform applied to a structurally stitched async call stack.</summary>
    /// <remarks>
    /// The context, its segment list, and the mutable frame list are owned and reused by the computer and must not
    /// be retained after the callback returns. Copy any identity values needed later; copying a frame alone does
    /// not detach its <see cref="AsyncProfilerStitchedFrame.Context"/> from the computer's reusable activation object.
    /// </remarks>
    internal delegate void AsyncProfilerCallStackTransform(AsyncProfilerCallStackTransformContext context);

    /// <summary>
    /// Ordered presentation pipeline for structurally stitched async call stacks. The mandatory structural
    /// stitcher always runs before this pipeline. Conservative normalization is enabled by default;
    /// runtime-specific async-infrastructure cleanup is opt-in; custom transforms run last in registration order.
    /// </summary>
    /// <remarks>
    /// Configure the host computer's pipeline before stack generation. <see cref="Transform"/> applies only
    /// conservative normalization, async-infrastructure cleanup, and custom callbacks to an existing structural result.
    /// Context ancestry and frame annotations are separate host-builder/emitter options.
    /// Built-in stages are selected from captured runtime profiles without changing their enablement or order.
    /// A profile can replace one complete stage while inheriting the others; missing or ambiguous runtime
    /// ownership retains common implementations. Custom callbacks still run after all enabled built-in stages.
    /// </remarks>
    internal sealed class AsyncProfilerCallStackTransformPipeline
    {
        /// <summary>Creates the default pipeline with conservative presentation normalization enabled.</summary>
        public AsyncProfilerCallStackTransformPipeline()
        {
            m_customTransforms = new List<AsyncProfilerCallStackTransform>();
        }

        /// <summary>
        /// Enables default strong-signal presentation transforms that fail open when their exact frame pattern is
        /// absent. This includes V1 synchronous-startup normalization, exact reentrant-activation deduplication,
        /// V1 completed-return attribution to the logical root with inferred state -2, and V2 sampled-leaf wrapper
        /// cleanup. Completed-return attribution requires explicit full-completion records and a uniquely ordered,
        /// resolved physical completion span; uncertain work is retained. V1 reentrant dispatch-entry deduplication requires a retained
        /// dispatcher, resolved startup identity, and a physically matched enclosing context; dispatch targets
        /// retain their captured state and role. It also collapses an all-System.Private.CoreLib synchronous handoff span
        /// between verified adjacent V1/V2 activations with a physically matched outer method and a matched
        /// inner method or unambiguous dispatch target. V1 targets require a matched box carrier or recorded
        /// completion progress and a retained dispatcher. Ambiguous completion evidence and physically
        /// completing inner invocations preserve the bridge. It also collapses an exact classified completion
        /// bridge from a remaining V2 dispatcher to a matched V1 activation. Unresolved, non-CoreLib, or incomplete
        /// handoffs preserve the complete span. Disabling this does not disable structural boundary replacement
        /// or verified incomplete-root normalization to <c>BROKEN</c>.
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
        /// Requires captured creation relationships and parent resume intervals; unavailable ancestry is not inferred.
        /// Disabled by default. This option is not applied by standalone <see cref="Transform"/> calls.
        /// </summary>
        public bool EnableContextAncestry { get; set; }

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
        /// <see cref="EnableContextAncestry"/> is enabled.</summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
        public int MaximumContextAncestryDepth
        {
            get => m_maximumContextAncestryDepth;
            set
            {
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(MaximumContextAncestryDepth));
                }
                m_maximumContextAncestryDepth = value;
            }
        }

        /// <summary>
        /// Enables presentation cleanup of recognized V1/V2 async runtime infrastructure, including method builders,
        /// state-machine boxes, awaiter registration, async dispatchers, and completion bridges. This is not general
        /// framework filtering: it targets known helpers owned by System.Private.CoreLib. Disabled by default because
        /// these implementation details are not part of the async-profiler contract.
        /// Before individual-helper cleanup, it atomically folds fully resolved CoreLib resumption bridges from
        /// a verified async activation to <c>ThreadPoolWorkQueue.Dispatch</c>, retaining that dispatch anchor.
        /// V1 requires a verified dispatcher boundary; V2 requires a physically matched current async method
        /// or a resolved structurally verified dispatch target.
        /// Bridges require a runtime-profile completion pair (adjacent
        /// <c>Task.RunContinuations -&gt; Task.TrySetResult</c> under the common rules). Interrupted,
        /// unresolved, non-CoreLib, or unverified candidates remain intact and protected from all subsequent
        /// helper cleanup, including completion-chain removal. Exact completion chains can still be removed
        /// independently outside protected candidates.
        /// A resolved CoreLib profiler <c>Resume</c> immediately above a verified V1 dispatcher or physically
        /// matched current/completing async method or V1 box carrier establishes ownership of the synchronous
        /// leaf-side prefix.
        /// That entire prefix, including unresolved frames, initialization, GC, and callbacks invoked by the
        /// profiler, is atomically attributed to the nearest active logical async frame. Capture and emission
        /// helpers need not be visible because they may inline. All prefix frames must belong to the same active
        /// activation; cleanup never crosses another async boundary or a physically matched application anchor.
        /// A resolved CoreLib <c>CreateDispatcher</c> and await-registration spine directly above a physical
        /// current V1 method likewise establish ownership of the dispatcher's entire callee prefix, regardless
        /// of callee names. Rejected dispatcher-callee prefixes remain protected from individual-helper cleanup.
        /// A registered V1 target without a physical current-method match keeps its dispatch-target metadata.
        /// Residual recognized V1/V2 dispatcher and box frames outside protected spans are represented by
        /// <c>[Async dispatcher frames]</c>, rather than deleted, preserving the caller context of their callees.
        /// Each contiguous span within the same async context gets one marker, whether or not a body was matched.
        /// This is overhead attribution, not evidence that a suspended caller was physically executing.
        /// Missing, unresolved, foreign, interrupted, or context-inconsistent ownership evidence leaves the
        /// candidate prefix protected from general helper cleanup; no partial profiler tail is removed.
        /// Outside rejected candidates, the exact CoreLib <c>AsyncStateMachineDispatcherInfo.CreateDispatcher</c>
        /// helper is removed individually, preserving the application kickoff frame.
        /// Application frames outside profiler-owned prefixes, thread roots, isolated
        /// <c>TaskCompletionSource.SetResult</c> frames, and other profiler work outside a verified prefix are retained.
        /// This option is independent of structural stitching and
        /// <see cref="EnableNormalization"/>.
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
        public void Add(AsyncProfilerCallStackTransform transform)
        {
            if (transform is null)
            {
                throw new ArgumentNullException(nameof(transform));
            }

            m_customTransforms.Add(transform);
            m_transforms = null;
        }

        /// <summary>Removes all custom transforms. Built-in enablement is unchanged.</summary>
        public void ClearCustomTransforms()
        {
            m_customTransforms.Clear();
            m_transforms = null;
        }

        /// <summary>
        /// Applies this pipeline to a structural stitch result without requiring a
        /// <see cref="SampleProfilerThreadTimeComputer"/>. The input frame list is not modified; its frames are
        /// copied into a mutable output list. Presentation diagnostics are added to the result's existing
        /// <see cref="AsyncProfilerStitchResult.Diagnostics"/> instance, which is shared by both results. This method does not
        /// insert historical ancestry or intern/annotate output frames.
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
            var context = new AsyncProfilerCallStackTransformContext(
                traceLog,
                segments,
                timestampQpc,
                classifyBoundary,
                structuralResult.Diagnostics,
                frames);
            ApplyInPlace(context);
            return new AsyncProfilerStitchResult(frames, structuralResult.Diagnostics);
        }

        internal void ApplyInPlace(AsyncProfilerCallStackTransformContext context)
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

        private static void ApplyRuntimeTransform(AsyncProfilerCallStackTransformContext context, AsyncProfilerRuntimeTransformKind kind)
        {
            AsyncProfilerCallStackTransform transform = context.RuntimeProfile.GetTransform(kind) ??
                throw new InvalidOperationException("A runtime profile returned a null presentation transform.");
            transform(context);
        }

        private int m_maximumContextAncestryDepth = AsyncProfilerContextAncestryAugmenter.DefaultMaximumDepth;
        private bool m_enableNormalization = true;
        private bool m_enableInfrastructureCleanup;
        private readonly List<AsyncProfilerCallStackTransform> m_customTransforms;
        // Null until first use or after pass configuration changes; otherwise reused across samples.
        private AsyncProfilerCallStackTransform[] m_transforms;

        #endregion
    }
}
