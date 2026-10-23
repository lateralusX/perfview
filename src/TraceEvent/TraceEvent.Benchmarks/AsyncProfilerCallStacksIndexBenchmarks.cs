using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

namespace TraceEventBenchmarks
{
    [MemoryDiagnoser]
    public class AsyncProfilerCallStacksIndexBenchmarks
    {
        private static readonly ProcessIndex s_processIndex = (ProcessIndex)1;
        private static readonly AsyncProfilerThreadKey s_thread = new AsyncProfilerThreadKey(s_processIndex, 42);

        private AsyncProfilerCallStacksIndex _index;
        private List<AsyncProfilerCallStack> _result;
        private long _queryQpc;

        [Params(1_000, 100_000)]
        public int IntervalCount { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _index = new AsyncProfilerCallStacksIndex();
            _result = new List<AsyncProfilerCallStack>(4);

            ulong[] methodIds = { 0x1000, 0x2000, 0x3000 };
            for (int i = 0; i < IntervalCount; i++)
            {
                long startQpc = i * 10L;
                _index.Add(
                    s_thread,
                    AsyncCallstackKind.RuntimeAsync,
                    methodIds,
                    frameStates: null,
                    dispatcherId: 0,
                    depth: 0,
                    continuationIndexBase: 0,
                    wrapperCount: 32,
                    startQpc,
                    endQpc: startQpc + 5,
                    methodCompletions: Array.Empty<AsyncProfilerCallStack.CompletionDelta>(),
                    exceptionCompletions: Array.Empty<AsyncProfilerCallStack.CompletionDelta>(),
                    wrapperResets: Array.Empty<long>());
            }

            _queryQpc = (IntervalCount / 2) * 10L + 1;
            for (int depth = 1; depth < 8; depth++)
            {
                _index.Add(
                    s_thread,
                    AsyncCallstackKind.RuntimeAsync,
                    methodIds,
                    frameStates: null,
                    dispatcherId: 0,
                    depth,
                    continuationIndexBase: 0,
                    wrapperCount: 32,
                    startQpc: _queryQpc - depth,
                    endQpc: _queryQpc + depth + 1,
                    methodCompletions: Array.Empty<AsyncProfilerCallStack.CompletionDelta>(),
                    exceptionCompletions: Array.Empty<AsyncProfilerCallStack.CompletionDelta>(),
                    wrapperResets: Array.Empty<long>());
            }
            _index.GetCallStacks(s_thread, _queryQpc, _result);
        }

        [Benchmark(Baseline = true)]
        public int AllocateResultList() => _index.GetCallStacks(s_thread, _queryQpc).Count;

        [Benchmark]
        public int ReuseResultList()
        {
            _index.GetCallStacks(s_thread, _queryQpc, _result);
            return _result.Count;
        }
    }
}
