using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

namespace TraceEventBenchmarks
{
    /// <summary>Measures presentation of one preclassified stack, excluding structural stitching and stack-source generation.</summary>
    /// <remarks>Each configuration includes restoring the mutable input buffer and resetting per-sample diagnostics.</remarks>
    [MemoryDiagnoser]
    public class AsyncProfilerCallStackTransformPipelineBenchmarks
    {
        [GlobalSetup]
        public void Setup()
        {
            var captured = new AsyncProfilerCallStackFrames(
                AsyncCallstackKind.StateMachineAsync, new ulong[] { 200 }, new[] { 0 });
            captured.SetCodeAddressAt(0, (CodeAddressIndex)200);
            m_segment = new AsyncProfilerCallStack(
                0, (AsyncProfilerCallStackFramesIndex)0, captured, 0, 32, 0, long.MaxValue,
                Array.Empty<AsyncProfilerCallStack.CompletionDelta>(),
                Array.Empty<AsyncProfilerCallStack.CompletionDelta>(), Array.Empty<long>());
            m_input = new[]
            {
                Sync(10),
                Sync(11, AsyncProfilerStitchSyncFrameKind.V1StateMachineMoveNext),
                Sync(12, AsyncProfilerStitchSyncFrameKind.V1MethodBuilderStart),
                Sync(13, AsyncProfilerStitchSyncFrameKind.V1MethodBuilderStart),
                Sync(14),
                Sync(15, AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations),
                Sync(16, AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult),
                Sync(17, AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskCompletionSourceSetResult),
                Sync(18),
                AsyncProfilerStitchedFrame.CreateAsync(m_segment, 0),
            };
            m_frames = new List<AsyncProfilerStitchedFrame>(m_input.Length);
            m_diagnostics = new AsyncProfilerCallStackDiagnostics();
            m_context = new AsyncProfilerCallStackTransformState(
                traceLog: null, segments: new[] { m_segment }, timestampQpc: 1,
                classifyBoundary: _ => AsyncProfilerCallStackStitchBoundaryInfo.None,
                diagnostics: m_diagnostics, frames: m_frames);
            m_structural = new AsyncProfilerCallStackTransformPipeline { EnableNormalization = false };
            m_normalization = new AsyncProfilerCallStackTransformPipeline();
            m_cleanup = new AsyncProfilerCallStackTransformPipeline { EnableInfrastructureCleanup = true };

            Validate(m_structural, expectedFrames: 10, normalized: 0, startupCollapsed: 0, infrastructureCollapsed: 0);
            Validate(m_normalization, expectedFrames: 8, normalized: 1, startupCollapsed: 2, infrastructureCollapsed: 0);
            Validate(m_cleanup, expectedFrames: 6, normalized: 1, startupCollapsed: 2, infrastructureCollapsed: 3);
        }

        /// <summary>Measures buffer reset and pipeline dispatch with all built-in presentation transforms disabled.</summary>
        [Benchmark(Baseline = true)]
        public int StructuralOnly() => Apply(m_structural);

        /// <summary>Normalizes synchronous V1 startup frames in the same restored stack.</summary>
        [Benchmark]
        public int DefaultNormalization() => Apply(m_normalization);

        /// <summary>Normalizes startup frames and groups a runtime task-completion chain in the same restored stack.</summary>
        [Benchmark]
        public int NormalizationWithInfrastructureCleanup() => Apply(m_cleanup);

        #region private

        private int Apply(AsyncProfilerCallStackTransformPipeline pipeline)
        {
            // Restore the same preclassified sample for each invocation, including the no-transform baseline.
            m_frames.Clear();
            m_frames.AddRange(m_input);
            m_diagnostics.Clear();
            pipeline.ApplyInPlace(m_context);
            return m_frames.Count;
        }

        private void Validate(AsyncProfilerCallStackTransformPipeline pipeline,
            int expectedFrames, int normalized, int startupCollapsed, int infrastructureCollapsed)
        {
            if (Apply(pipeline) != expectedFrames ||
                m_diagnostics.V1SynchronousMoveNextFramesNormalized != normalized ||
                m_diagnostics.V1SynchronousStartupFramesCollapsed != startupCollapsed ||
                m_diagnostics.AsyncInfrastructureFramesCollapsed != infrastructureCollapsed)
            {
                throw new InvalidOperationException("The pipeline benchmark did not exercise the expected transforms.");
            }
        }

        private AsyncProfilerStitchedFrame Sync(int codeAddress,
            AsyncProfilerStitchSyncFrameKind kind = AsyncProfilerStitchSyncFrameKind.None) =>
            AsyncProfilerStitchedFrame.CreateSync(
                new AsyncProfilerStitchSyncFrame((CodeAddressIndex)codeAddress, MethodIndex.Invalid, kind), m_segment);

        private AsyncProfilerCallStack m_segment;
        private AsyncProfilerStitchedFrame[] m_input;
        private List<AsyncProfilerStitchedFrame> m_frames;
        private AsyncProfilerCallStackDiagnostics m_diagnostics;
        private AsyncProfilerCallStackTransformState m_context;
        private AsyncProfilerCallStackTransformPipeline m_structural;
        private AsyncProfilerCallStackTransformPipeline m_normalization;
        private AsyncProfilerCallStackTransformPipeline m_cleanup;

        #endregion
    }
}
