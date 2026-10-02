// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

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
        /// The trace whose code addresses are referenced by <see cref="Frames"/>, or null when the pipeline is
        /// invoked directly without a <see cref="SampleProfilerThreadTimeComputer"/>. Built-in transforms that
        /// require module or symbol metadata fail open when this is null.
        /// </summary>
        public TraceLog TraceLog { get; }

        /// <summary>The active async segments used by structural stitching, ordered root-to-leaf.</summary>
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
    /// be retained after the callback returns.
    /// </remarks>
    public delegate void AsyncStackTransform(AsyncStackTransformContext context);

    /// <summary>
    /// Ordered presentation pipeline for structurally stitched async call stacks. The mandatory structural
    /// stitcher always runs before this pipeline. Conservative normalization is enabled by default; runtime-specific
    /// System.Private.CoreLib cleanup is opt-in; custom transforms run last in registration order.
    /// </summary>
    public sealed class AsyncStackTransformPipeline
    {
        /// <summary>Creates the default pipeline with conservative presentation normalization enabled.</summary>
        public AsyncStackTransformPipeline()
        {
            EnableConservativeTransforms = true;
            m_customTransforms = new List<AsyncStackTransform>();
        }

        /// <summary>
        /// Enables default strong-signal presentation transforms that fail open when their exact frame pattern is
        /// absent. This includes V1 synchronous-startup normalization, exact reentrant-activation deduplication, and
        /// V2 sampled-leaf wrapper cleanup.
        /// </summary>
        public bool EnableConservativeTransforms { get; set; }

        /// <summary>
        /// Enables structural async-context ancestry. When enabled, historical creation-parent frames retain their
        /// activation identity and role so renderers can distinguish contexts after presentation transforms run.
        /// </summary>
        public bool EnableContextAncestry { get; set; }

        /// <summary>
        /// Prefixes active async-profiler frames with <c>[Async]</c> when they are interned. Retained synchronous
        /// frames keep their ordinary names, and this option does not add sync/async transition frames.
        /// </summary>
        public bool EnableActiveAsyncFrameAnnotations { get; set; }

        /// <summary>The maximum number of missing parent contexts inserted for one active segment.</summary>
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
        /// Enables cleanup tied to the current System.Private.CoreLib implementation. This is disabled by default
        /// because these frames are implementation details rather than part of the async-profiler contract.
        /// </summary>
        public bool EnableSystemPrivateCoreLibCleanup { get; set; }

        /// <summary>Adds a custom transform that runs after all enabled built-in transforms.</summary>
        public void Add(AsyncStackTransform transform)
        {
            if (transform is null)
            {
                throw new ArgumentNullException(nameof(transform));
            }

            m_customTransforms.Add(transform);
        }

        /// <summary>Removes all custom transforms. Built-in enablement is unchanged.</summary>
        public void ClearCustomTransforms() => m_customTransforms.Clear();

        /// <summary>
        /// Applies this pipeline to a structural stitch result without requiring a
        /// <see cref="SampleProfilerThreadTimeComputer"/>. The structural result is not modified; its frames are
        /// copied into a mutable output list. Presentation diagnostics are added to the result's existing
        /// <see cref="StitchResult.Diagnostics"/> instance.
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
            if (EnableConservativeTransforms)
            {
                ApplyConservativeTransforms(context);
            }

            if (EnableSystemPrivateCoreLibCleanup)
            {
                ApplySystemPrivateCoreLibCleanup(context);
            }

            for (int i = 0; i < m_customTransforms.Count; i++)
            {
                m_customTransforms[i](context);
            }
        }

        private static void ApplyConservativeTransforms(AsyncStackTransformContext context)
        {
            List<StitchedFrame> frames = context.Frames;

            // The structural V2 gate preserves the physical sync layout when the wrapper itself is the sampled
            // leaf. Removing that one known transition frame is presentation-only and intentionally opt-out-able.
            if (context.Diagnostics.V2SyncLayoutUsed != 0 &&
                frames.Count != 0 &&
                frames[0].Origin == StitchedFrameOrigin.Sync &&
                context.ClassifyBoundary(frames[0].CodeAddress).Kind ==
                    AsyncStitchBoundaryKind.V2ContinuationWrapper)
            {
                frames.RemoveAt(0);
                context.Diagnostics.V2LeafWrapperDropped++;
            }

            for (int i = 0; i < frames.Count; i++)
            {
                StitchedFrame frame = frames[i];
                if (frame.Origin == StitchedFrameOrigin.AsyncCurrent)
                {
                    TryApplyReentrantV1Deduplication(context, i);
                    continue;
                }

                if (frame.Origin != StitchedFrameOrigin.Sync ||
                    frame.SyncFrameKind != StitchSyncFrameKind.V1StateMachineMoveNext)
                {
                    continue;
                }

                int end = i + 1;
                while (end < frames.Count &&
                       frames[end].Origin == StitchedFrameOrigin.Sync &&
                       frames[end].SyncFrameKind == StitchSyncFrameKind.V1MethodBuilderStart)
                {
                    end++;
                }

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

                frames[i] = frame.WithPresentation(StitchedFramePresentation.LogicalStateMachineMethod);
                context.Diagnostics.V1SynchronousMoveNextFramesNormalized++;

                if (end == i + 1)
                {
                    continue;
                }

                frames.RemoveRange(i + 1, builderCount);
                context.Diagnostics.V1SynchronousStartupFramesCollapsed += builderCount;
            }
        }

        private static bool TryApplyReentrantV1Deduplication(
            AsyncStackTransformContext context,
            int currentIndex)
        {
            if (context.TraceLog == null)
            {
                return false;
            }

            List<StitchedFrame> frames = context.Frames;
            StitchedFrame current = frames[currentIndex];
            if (current.Segment == null ||
                current.Segment.Kind != AsyncCallstackKind.StateMachineAsync ||
                current.Method == MethodIndex.Invalid)
            {
                return false;
            }

            int registrationStart = currentIndex + 1;
            int startupMoveNextIndex = registrationStart;
            while (startupMoveNextIndex < frames.Count &&
                   IsV1AwaitRegistrationFrame(frames[startupMoveNextIndex]))
            {
                startupMoveNextIndex++;
            }

            if (startupMoveNextIndex == registrationStart || startupMoveNextIndex >= frames.Count)
            {
                return false;
            }

            StitchedFrame startupMoveNext = frames[startupMoveNextIndex];
            if (startupMoveNext.Origin != StitchedFrameOrigin.Sync ||
                startupMoveNext.SyncFrameKind != StitchSyncFrameKind.V1StateMachineMoveNext ||
                startupMoveNext.Method != current.Method)
            {
                return false;
            }

            int builderStart = startupMoveNextIndex + 1;
            int kickoffIndex = builderStart;
            while (kickoffIndex < frames.Count &&
                   frames[kickoffIndex].Origin == StitchedFrameOrigin.Sync &&
                   frames[kickoffIndex].SyncFrameKind == StitchSyncFrameKind.V1MethodBuilderStart)
            {
                kickoffIndex++;
            }

            if (kickoffIndex == builderStart ||
                kickoffIndex >= frames.Count ||
                !IsMatchingV1Kickoff(context.TraceLog, startupMoveNext, frames[kickoffIndex]))
            {
                return false;
            }

            // Keep the resumed AsyncCurrent representation. The exact match proves that the intervening CoreLib
            // await-registration frames, duplicate startup MoveNext, builder Start frames, and kickoff all belong
            // to the same reentrant activation, so collapse that complete generated span.
            int duplicateFrameCount = kickoffIndex - registrationStart + 1;
            frames.RemoveRange(registrationStart, duplicateFrameCount);
            context.Diagnostics.V1ReentrantFramesCollapsed += duplicateFrameCount;
            return true;
        }

        private static bool IsV1AwaitRegistrationFrame(StitchedFrame frame) =>
            frame.Origin == StitchedFrameOrigin.Sync &&
            (frame.SyncFrameKind == StitchSyncFrameKind.V1MethodBuilderAwaitUnsafeOnCompleted ||
             frame.SyncFrameKind == StitchSyncFrameKind.V1AwaiterRegistrationInfrastructure);

        private static bool IsMatchingV1Kickoff(
            TraceLog traceLog,
            StitchedFrame stateMachineMoveNext,
            StitchedFrame kickoff)
        {
            if (kickoff.Origin != StitchedFrameOrigin.Sync ||
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
                !AsyncStitchBoundary.TryGetLogicalStateMachineMethodName(
                    moveNextCodeAddress.FullMethodName, out string logicalMethod))
            {
                return false;
            }

            string kickoffMethod = kickoffCodeAddress.FullMethodName;
            if (string.IsNullOrEmpty(kickoffMethod))
            {
                return false;
            }

            int parameters = kickoffMethod.IndexOf('(');
            if (parameters >= 0)
            {
                kickoffMethod = kickoffMethod.Substring(0, parameters);
            }

            return string.Equals(
                logicalMethod,
                kickoffMethod.Replace('+', '.'),
                StringComparison.Ordinal);
        }

        private static void ApplySystemPrivateCoreLibCleanup(AsyncStackTransformContext context)
        {
            List<StitchedFrame> frames = context.Frames;
            for (int i = frames.Count - 1; i >= 0; i--)
            {
                StitchedFrame frame = frames[i];
                if (frame.Origin != StitchedFrameOrigin.Sync)
                {
                    continue;
                }

                AsyncStitchBoundaryKind boundaryKind = context.ClassifyBoundary(frame.CodeAddress).Kind;
                if (boundaryKind == AsyncStitchBoundaryKind.V1Dispatcher ||
                    boundaryKind == AsyncStitchBoundaryKind.V1DispatcherInfrastructure ||
                    boundaryKind == AsyncStitchBoundaryKind.V2ContinuationWrapper ||
                    boundaryKind == AsyncStitchBoundaryKind.V2DispatchContinuation ||
                    frame.SyncFrameKind == StitchSyncFrameKind.V1MethodBuilderStart ||
                    frame.SyncFrameKind == StitchSyncFrameKind.V1MethodBuilderAwaitUnsafeOnCompleted ||
                    frame.SyncFrameKind == StitchSyncFrameKind.V1MethodBuilderInfrastructure ||
                    frame.SyncFrameKind == StitchSyncFrameKind.V1MethodBuilderCompletion ||
                    frame.SyncFrameKind == StitchSyncFrameKind.V1AwaiterRegistrationInfrastructure ||
                    frame.SyncFrameKind == StitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure)
                {
                    frames.RemoveAt(i);
                    context.Diagnostics.SystemPrivateCoreLibFramesCollapsed++;
                }
            }
        }

        #region private

        private int m_maximumContextAncestryDepth = AsyncContextAncestryAugmenter.DefaultMaximumDepth;
        private readonly List<AsyncStackTransform> m_customTransforms;

        #endregion
    }
}
