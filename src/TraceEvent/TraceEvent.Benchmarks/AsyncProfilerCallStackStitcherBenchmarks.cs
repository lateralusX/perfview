using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

namespace TraceEventBenchmarks
{
    [MemoryDiagnoser]
    public class AsyncProfilerCallStackStitcherBenchmarks
    {
        private const long Qpc = 1_000;

        private IReadOnlyList<AsyncProfilerStitchSyncFrame> _sync;
        private IReadOnlyList<AsyncProfilerCallStack> _segments;
        private Dictionary<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> _boundaries;
        private Dictionary<CodeAddressIndex, MethodIndex> _methods;
        private List<AsyncProfilerStitchedFrame> _output;
        private AsyncProfilerCallStackDiagnostics _diagnostics;
        private Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> _classify;
        private Func<ProcessIndex, AsyncCallstackKind, long, bool> _methodCompletionObservedByProcess;
        private Func<CodeAddressIndex, MethodIndex> _methodOf;

        [Params(
            AsyncProfilerStitchBenchmarkScenario.NoAsync,
            AsyncProfilerStitchBenchmarkScenario.RuntimeAsync,
            AsyncProfilerStitchBenchmarkScenario.StateMachineAsync,
            AsyncProfilerStitchBenchmarkScenario.MixedNested,
            AsyncProfilerStitchBenchmarkScenario.BoundaryNotFound)]
        public AsyncProfilerStitchBenchmarkScenario Scenario { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _boundaries = new Dictionary<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo>();
            _methods = new Dictionary<CodeAddressIndex, MethodIndex>();
            _output = new List<AsyncProfilerStitchedFrame>(32);
            _diagnostics = new AsyncProfilerCallStackDiagnostics();
            _classify = Classify;
            _methodCompletionObservedByProcess = MethodCompletionObservedByProcess;
            _methodOf = MethodOf;

            switch (Scenario)
            {
                case AsyncProfilerStitchBenchmarkScenario.NoAsync:
                    _sync = SyncFrames(16, 100);
                    _segments = Array.Empty<AsyncProfilerCallStack>();
                    break;

                case AsyncProfilerStitchBenchmarkScenario.RuntimeAsync:
                    _sync = RuntimeSync(100, firstAsyncMethod: 200, wrapperIndex: 3);
                    _segments = new[] { Segment(AsyncCallstackKind.RuntimeAsync, 200, 6, depth: 0) };
                    break;

                case AsyncProfilerStitchBenchmarkScenario.StateMachineAsync:
                    _sync = StateMachineSync(100, 200);
                    _segments = new[] { Segment(AsyncCallstackKind.StateMachineAsync, 200, 6, depth: 0) };
                    break;

                case AsyncProfilerStitchBenchmarkScenario.MixedNested:
                    var sync = new List<AsyncProfilerStitchSyncFrame>();
                    sync.AddRange(StateMachineSync(100, 200));
                    sync.Add(Sync(130, 90));
                    sync.AddRange(RuntimeSync(140, firstAsyncMethod: 300, wrapperIndex: 0));
                    _sync = sync;
                    _segments = new[]
                    {
                        Segment(AsyncCallstackKind.RuntimeAsync, 300, 6, depth: 0),
                        Segment(AsyncCallstackKind.StateMachineAsync, 200, 6, depth: 1),
                    };
                    break;

                case AsyncProfilerStitchBenchmarkScenario.BoundaryNotFound:
                    _sync = SyncFrames(16, 100);
                    _segments = new[] { Segment(AsyncCallstackKind.RuntimeAsync, 200, 6, depth: 0) };
                    break;

                default:
                    throw new InvalidOperationException("Unknown benchmark scenario.");
            }
        }

        [Benchmark]
        public object Stitch() =>
            AsyncProfilerCallStackStitcher.Stitch(
                _sync,
                _segments,
                Qpc,
                Classify,
                MethodCompletionObserved,
                MethodOf);

        [Benchmark]
        public int StitchIntoReusableBuffers()
        {
            AsyncProfilerCallStackStitcher.StitchInto(
                _sync,
                _segments,
                Qpc,
                _classify,
                _methodCompletionObservedByProcess,
                s_processIndex,
                _methodOf,
                trace: false,
                _output,
                _diagnostics);
            return _output.Count;
        }

        private AsyncProfilerCallStack Segment(AsyncCallstackKind kind, int firstCodeAddress, int frameCount, int depth)
        {
            var methodIds = new ulong[frameCount];
            var frames = new AsyncProfilerCallStackFrames(kind, methodIds, kind == AsyncCallstackKind.StateMachineAsync ? new int[frameCount] : null);
            for (int i = 0; i < frameCount; i++)
            {
                CodeAddressIndex codeAddress = CodeAddress(firstCodeAddress + i);
                methodIds[i] = (ulong)(firstCodeAddress + i);
                frames.SetCodeAddressAt(i, codeAddress);
                _methods[codeAddress] = Method(firstCodeAddress + i);
            }

            return new AsyncProfilerCallStack(
                depth,
                (AsyncProfilerCallStackFramesIndex)depth,
                frames,
                continuationIndexBase: 0,
                wrapperCount: 32,
                startQpc: 900,
                endQpc: 1_100,
                methodCompletions: Array.Empty<AsyncProfilerCallStack.CompletionDelta>(),
                exceptionCompletions: Array.Empty<AsyncProfilerCallStack.CompletionDelta>(),
                wrapperResets: Array.Empty<long>());
        }

        private AsyncProfilerStitchSyncFrame[] RuntimeSync(int firstCodeAddress, int firstAsyncMethod, int wrapperIndex)
        {
            var result = new[]
            {
                Sync(firstCodeAddress, firstAsyncMethod + 10),
                Sync(firstCodeAddress + 1, firstAsyncMethod + wrapperIndex),
                Boundary(firstCodeAddress + 2, AsyncProfilerCallStackStitchBoundaryKind.V2ContinuationWrapper, wrapperIndex),
                Boundary(firstCodeAddress + 3, AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation),
                Boundary(firstCodeAddress + 4, AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation),
                Sync(firstCodeAddress + 5, 95),
                Sync(firstCodeAddress + 6, 96),
            };
            return result;
        }

        private AsyncProfilerStitchSyncFrame[] StateMachineSync(int firstCodeAddress, int firstAsyncMethod)
        {
            return new[]
            {
                Sync(firstCodeAddress, 90),
                Sync(firstCodeAddress + 1, firstAsyncMethod + 1),
                Sync(firstCodeAddress + 2, firstAsyncMethod),
                Boundary(firstCodeAddress + 3, AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher),
                Boundary(firstCodeAddress + 4, AsyncProfilerCallStackStitchBoundaryKind.V1DispatcherInfrastructure),
                Boundary(firstCodeAddress + 5, AsyncProfilerCallStackStitchBoundaryKind.V1DispatcherInfrastructure),
            };
        }

        private AsyncProfilerStitchSyncFrame[] SyncFrames(int count, int firstCodeAddress)
        {
            var result = new AsyncProfilerStitchSyncFrame[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = Sync(firstCodeAddress + i, firstCodeAddress + i);
            }
            return result;
        }

        private AsyncProfilerStitchSyncFrame Sync(int codeAddress, int method)
        {
            CodeAddressIndex ca = CodeAddress(codeAddress);
            MethodIndex mi = Method(method);
            _methods[ca] = mi;
            return new AsyncProfilerStitchSyncFrame(ca, mi);
        }

        private AsyncProfilerStitchSyncFrame Boundary(
            int codeAddress, AsyncProfilerCallStackStitchBoundaryKind kind, int wrapperIndex = -1)
        {
            CodeAddressIndex ca = CodeAddress(codeAddress);
            _boundaries[ca] = new AsyncProfilerCallStackStitchBoundaryInfo(kind, wrapperIndex);
            return new AsyncProfilerStitchSyncFrame(ca, MethodIndex.Invalid);
        }

        private AsyncProfilerCallStackStitchBoundaryInfo Classify(CodeAddressIndex codeAddress) =>
            _boundaries.TryGetValue(codeAddress, out AsyncProfilerCallStackStitchBoundaryInfo boundary)
                ? boundary
                : AsyncProfilerCallStackStitchBoundaryInfo.None;

        private MethodIndex MethodOf(CodeAddressIndex codeAddress) =>
            _methods.TryGetValue(codeAddress, out MethodIndex method) ? method : MethodIndex.Invalid;

        private static bool MethodCompletionObserved(AsyncCallstackKind kind) => false;

        private static bool MethodCompletionObservedByProcess(
            ProcessIndex processIndex, AsyncCallstackKind kind, long activationStartQpc) => false;

        private static readonly ProcessIndex s_processIndex = (ProcessIndex)1;

        private static CodeAddressIndex CodeAddress(int value) => (CodeAddressIndex)value;

        private static MethodIndex Method(int value) => (MethodIndex)value;
    }

    public enum AsyncProfilerStitchBenchmarkScenario
    {
        NoAsync,
        RuntimeAsync,
        StateMachineAsync,
        MixedNested,
        BoundaryNotFound,
    }
}
