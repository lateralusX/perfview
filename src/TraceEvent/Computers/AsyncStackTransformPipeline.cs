// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

using Microsoft.Diagnostics.Tracing.Etlx;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>
    /// Per-sample context supplied to an async stack presentation transform. Structural stitching has already
    /// completed, and <see cref="Frames"/> contains the complete stitched stack leaf-to-root.
    /// </summary>
    public readonly struct AsyncStackTransformContext
    {
        internal AsyncStackTransformContext(
            TraceLog traceLog,
            IReadOnlyList<AsyncCallStack> segments,
            long timestampQpc,
            Func<CodeAddressIndex, AsyncStitchBoundaryInfo> classifyBoundary,
            StitchDiagnostics diagnostics,
            List<StitchedFrame> frames)
        {
            TraceLog = traceLog;
            Segments = segments;
            TimestampQpc = timestampQpc;
            m_classifyBoundary = classifyBoundary;
            Diagnostics = diagnostics;
            Frames = frames;
        }

        /// <summary>
        /// The trace whose code addresses are referenced by <see cref="Frames"/>, or null if a standalone
        /// <see cref="AsyncStackTransformPipeline.Transform"/> call supplies no trace context. Built-in transforms
        /// requiring module or symbol metadata fail open when this is null.
        /// </summary>
        public TraceLog TraceLog { get; }

        /// <summary>The active async segments used by structural stitching, ordered root-to-leaf.
        /// During computer callbacks, the list and its activation objects are reused and must not be retained.</summary>
        public IReadOnlyList<AsyncCallStack> Segments { get; }

        /// <summary>The sample timestamp in the trace QPC domain.</summary>
        public long TimestampQpc { get; }

        /// <summary>Diagnostics for this sample. Built-in and custom transforms may add presentation counters.</summary>
        public StitchDiagnostics Diagnostics { get; }

        /// <summary>The complete mutable stitched stack, leaf-to-root.</summary>
        public List<StitchedFrame> Frames { get; }

        /// <summary>Classifies a frame using the runtime boundary-name contract for this trace.</summary>
        public AsyncStitchBoundaryInfo ClassifyBoundary(CodeAddressIndex codeAddress) =>
            m_classifyBoundary(codeAddress);

        #region private

        private readonly Func<CodeAddressIndex, AsyncStitchBoundaryInfo> m_classifyBoundary;

        #endregion
    }

    /// <summary>A presentation transform applied to a structurally stitched async call stack.</summary>
    /// <remarks>
    /// The context, its segment list, and the mutable frame list are owned and reused by the computer and must not
    /// be retained after the callback returns. Copy any identity values needed later; copying a frame alone does
    /// not detach its <see cref="StitchedFrame.Context"/> from the computer's reusable activation object.
    /// </remarks>
    public delegate void AsyncStackTransform(AsyncStackTransformContext context);

    /// <summary>
    /// Ordered presentation pipeline for structurally stitched async call stacks. The mandatory structural
    /// stitcher always runs before this pipeline. Conservative normalization is enabled by default;
    /// runtime-specific async-infrastructure cleanup is opt-in; custom transforms run last in registration order.
    /// </summary>
    /// <remarks>
    /// Configure the host computer's pipeline before stack generation. <see cref="Transform"/> applies only
    /// conservative normalization, async-infrastructure cleanup, and custom callbacks to an existing structural result.
    /// Context ancestry and frame annotations are separate host-builder/emitter options.
    /// </remarks>
    public sealed class AsyncStackTransformPipeline
    {
        /// <summary>Creates the default pipeline with conservative presentation normalization enabled.</summary>
        public AsyncStackTransformPipeline()
        {
            m_customTransforms = new List<AsyncStackTransform>();
        }

        /// <summary>
        /// Enables default strong-signal presentation transforms that fail open when their exact frame pattern is
        /// absent. This includes V1 synchronous-startup normalization, exact reentrant-activation deduplication, and
        /// V2 sampled-leaf wrapper cleanup. It also collapses an all-System.Private.CoreLib synchronous handoff span
        /// between verified adjacent V1/V2 activations with a physically matched outer method and a matched
        /// inner method or unambiguous dispatch target. V1 targets require a matched box carrier or recorded
        /// completion progress and a retained dispatcher. Ambiguous completion evidence and physically
        /// completing inner invocations preserve the bridge. It also collapses an exact classified completion
        /// bridge from a remaining V2 dispatcher to a matched V1 activation. Unresolved, non-CoreLib, or incomplete
        /// handoffs preserve the complete span. Disabling this does not disable structural boundary replacement
        /// or verified incomplete-root normalization to <c>BROKEN</c>.
        /// </summary>
        public bool EnableConservativeTransforms
        {
            get => m_enableConservativeTransforms;
            set
            {
                if (m_enableConservativeTransforms != value)
                {
                    m_enableConservativeTransforms = value;
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
        public bool EnableActiveAsyncFrameAnnotations { get; set; }

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
        /// a matched async activation to <c>ThreadPoolWorkQueue.Dispatch</c>, retaining that dispatch anchor.
        /// V1 requires a verified dispatcher boundary; V2 also requires a physically matched current async method.
        /// Bridges require adjacent <c>Task.RunContinuations -&gt; Task.TrySetResult</c> evidence. Interrupted,
        /// unresolved, or non-CoreLib candidates remain protected from individual-helper cleanup. An exact,
        /// resolved <c>Task.RunContinuations -&gt; Task.TrySetResult -&gt; TaskCompletionSource.SetResult</c>
        /// chain directly below a verified root activation can still be removed independently, preserving
        /// application callbacks and the rest of the rejected bridge. The same chain is also removed outside
        /// protected candidates.
        /// A resolved CoreLib profiler <c>Resume</c> immediately above a verified V1 dispatcher or physically
        /// matched current/completing async method or V1 box carrier establishes ownership of the synchronous
        /// leaf-side prefix.
        /// That entire prefix, including unresolved frames, initialization, GC, and callbacks invoked by the
        /// profiler, is atomically attributed to the nearest active logical async frame. Capture and emission
        /// helpers need not be visible because they may inline. All prefix frames must belong to the same active
        /// activation; cleanup never crosses another async boundary or a physically matched application anchor.
        /// A registered V1 target without a physical current-method match keeps its dispatch-target metadata,
        /// even when its retained physical dispatcher is removed by cleanup.
        /// This is overhead attribution, not evidence that a suspended caller was physically executing.
        /// Missing, unresolved, foreign, interrupted, or context-inconsistent ownership evidence leaves the
        /// candidate prefix protected from general helper cleanup; no partial profiler tail is removed.
        /// Outside rejected candidates, the exact CoreLib <c>AsyncStateMachineDispatcherInfo.CreateDispatcher</c>
        /// helper is removed individually, preserving the application kickoff frame.
        /// Application frames outside profiler-owned prefixes, thread roots, isolated
        /// <c>TaskCompletionSource.SetResult</c> frames, and other profiler work outside a verified prefix are retained.
        /// This option is independent of structural stitching and
        /// <see cref="EnableConservativeTransforms"/>.
        /// </summary>
        public bool EnableAsyncInfrastructureCleanup
        {
            get => m_enableAsyncInfrastructureCleanup;
            set
            {
                if (m_enableAsyncInfrastructureCleanup != value)
                {
                    m_enableAsyncInfrastructureCleanup = value;
                    m_transforms = null;
                }
            }
        }

        /// <summary>Adds a custom transform that runs after all enabled built-in transforms.</summary>
        public void Add(AsyncStackTransform transform)
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
        /// <see cref="StitchResult.Diagnostics"/> instance, which is shared by both results. This method does not
        /// insert historical ancestry or intern/annotate output frames.
        /// </summary>
        /// <param name="structuralResult">The result produced by <see cref="AsyncCpuStackStitcher"/>.</param>
        /// <param name="segments">The active async segments used to produce the structural result.</param>
        /// <param name="timestampQpc">The sample timestamp in the trace QPC domain.</param>
        /// <param name="classifyBoundary">Classifies runtime boundary methods for the trace.</param>
        /// <param name="traceLog">
        /// Optional trace context exposed to custom transforms and used by built-in rules that require module or
        /// symbol metadata. Those built-in rules fail open when it is null.
        /// </param>
        public StitchResult Transform(
            StitchResult structuralResult,
            IReadOnlyList<AsyncCallStack> segments,
            long timestampQpc,
            Func<CodeAddressIndex, AsyncStitchBoundaryInfo> classifyBoundary,
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

            var frames = new List<StitchedFrame>(structuralResult.Frames);
            var context = new AsyncStackTransformContext(
                traceLog,
                segments,
                timestampQpc,
                classifyBoundary,
                structuralResult.Diagnostics,
                frames);
            ApplyInPlace(context);
            return new StitchResult(frames, structuralResult.Diagnostics);
        }

        internal void ApplyInPlace(AsyncStackTransformContext context)
        {
            AsyncStackTransform[] transforms = m_transforms ?? (m_transforms = CreateTransforms());
            for (int i = 0; i < transforms.Length; i++)
            {
                transforms[i](context);
            }
        }

        #region private

        private AsyncStackTransform[] CreateTransforms()
        {
            var transforms = new List<AsyncStackTransform>(4 + m_customTransforms.Count);
            if (EnableConservativeTransforms)
            {
                transforms.Add(new AsyncSampledV2WrapperTransform().Apply);
                transforms.Add(new AsyncReentrantHandoffTransform().Apply);
                transforms.Add(new AsyncV1PresentationTransform().Apply);
            }
            if (EnableAsyncInfrastructureCleanup)
            {
                transforms.Add(new AsyncInfrastructureCleanupTransform().Apply);
            }
            transforms.AddRange(m_customTransforms);
            return transforms.ToArray();
        }

        private int m_maximumContextAncestryDepth = AsyncContextAncestryAugmenter.DefaultMaximumDepth;
        private bool m_enableConservativeTransforms = true;
        private bool m_enableAsyncInfrastructureCleanup;
        private readonly List<AsyncStackTransform> m_customTransforms;
        // Null until first use or after pass configuration changes; otherwise reused across samples.
        private AsyncStackTransform[] m_transforms;

        #endregion
    }
}
