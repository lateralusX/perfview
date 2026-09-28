using System;
using System.IO;

using BenchmarkDotNet.Attributes;

using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Stacks;

namespace TraceEventBenchmarks
{
    [MemoryDiagnoser]
    public class AsyncStackTransformPipelineBenchmarks
    {
        private AsyncProfilerEndToEndFixture _fixture;
        private TraceLog _traceLog;
        private SymbolReader _symbolReader;

        [GlobalSetup]
        public void Setup()
        {
            _fixture = AsyncProfilerEndToEndFixture.Create(
                AsyncProfilerWorkloadProfile.High,
                AsyncProfilerKindMode.Mixed);
            _fixture.WriteNettrace(_fixture.NetTracePath, includeAsyncProfilerData: true);
            TraceLog.CreateFromEventPipeDataFile(_fixture.NetTracePath, _fixture.EtlxPath);
            _traceLog = new TraceLog(_fixture.EtlxPath);
            _symbolReader = new SymbolReader(TextWriter.Null);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _traceLog?.Dispose();
            _symbolReader?.Dispose();
            _fixture?.Dispose();
        }

        [Benchmark(Baseline = true, OperationsPerInvoke = 6)]
        public int StructuralOnly() =>
            GenerateCpuStacks(enableConservativeTransforms: false, enableSystemPrivateCoreLibCleanup: false);

        [Benchmark(OperationsPerInvoke = 6)]
        public int DefaultConservative() =>
            GenerateCpuStacks(enableConservativeTransforms: true, enableSystemPrivateCoreLibCleanup: false);

        [Benchmark(OperationsPerInvoke = 6)]
        public int ConservativeWithSystemPrivateCoreLibCleanup() =>
            GenerateCpuStacks(enableConservativeTransforms: true, enableSystemPrivateCoreLibCleanup: true);

        private int GenerateCpuStacks(
            bool enableConservativeTransforms,
            bool enableSystemPrivateCoreLibCleanup)
        {
            int sampleCount = 0;
            for (int i = 0; i < 6; i++)
            {
                var stackSource = new MutableTraceEventStackSource(_traceLog);
                var computer = new SampleProfilerThreadTimeComputer(
                    _traceLog, _symbolReader, stitchAsyncCallStacks: true)
                {
                    IncludeEventSourceEvents = false,
                    GroupByStartStopActivity = false,
                };
                computer.AsyncStackTransforms.EnableConservativeTransforms = enableConservativeTransforms;
                computer.AsyncStackTransforms.EnableSystemPrivateCoreLibCleanup =
                    enableSystemPrivateCoreLibCleanup;
                computer.GenerateThreadTimeStacks(stackSource);
                stackSource.ForEach(_ => sampleCount++);
            }

            return sampleCount;
        }
    }
}
