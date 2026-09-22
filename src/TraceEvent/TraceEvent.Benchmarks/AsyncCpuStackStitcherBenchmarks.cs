using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

namespace TraceEventBenchmarks
{
    [MemoryDiagnoser]
    public class AsyncCpuStackStitcherBenchmarks
    {
        private const long Qpc = 1_000;

        private IReadOnlyList<StitchSyncFrame> _sync;
        private IReadOnlyList<AsyncCallStack> _segments;
        private Dictionary<CodeAddressIndex, AsyncStitchBoundaryInfo> _boundaries;
        private Dictionary<CodeAddressIndex, MethodIndex> _methods;
        private List<StitchedFrame> _output;
        private StitchDiagnostics _diagnostics;
        private Func<CodeAddressIndex, AsyncStitchBoundaryInfo> _classify;
        private Func<ProcessIndex, AsyncCallstackKind, long, bool> _methodCompletionObservedByProcess;
        private Func<CodeAddressIndex, MethodIndex> _methodOf;

        [Params(
            StitchBenchmarkScenario.NoAsync,
            StitchBenchmarkScenario.RuntimeAsync,
            StitchBenchmarkScenario.StateMachineAsync,
            StitchBenchmarkScenario.MixedNested,
            StitchBenchmarkScenario.BoundaryNotFound)]
        public StitchBenchmarkScenario Scenario { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _boundaries = new Dictionary<CodeAddressIndex, AsyncStitchBoundaryInfo>();
            _methods = new Dictionary<CodeAddressIndex, MethodIndex>();
            _output = new List<StitchedFrame>(32);
            _diagnostics = new StitchDiagnostics();
            _classify = Classify;
            _methodCompletionObservedByProcess = MethodCompletionObservedByProcess;
            _methodOf = MethodOf;

            switch (Scenario)
            {
                case StitchBenchmarkScenario.NoAsync:
                    _sync = SyncFrames(16, 100);
                    _segments = Array.Empty<AsyncCallStack>();
                    break;

                case StitchBenchmarkScenario.RuntimeAsync:
                    _sync = RuntimeSync(100, firstAsyncMethod: 200, wrapperIndex: 3);
                    _segments = new[] { Segment(AsyncCallstackKind.RuntimeAsync, 200, 6, depth: 0) };
                    break;

                case StitchBenchmarkScenario.StateMachineAsync:
                    _sync = StateMachineSync(100, 200);
                    _segments = new[] { Segment(AsyncCallstackKind.StateMachineAsync, 200, 6, depth: 0) };
                    break;

                case StitchBenchmarkScenario.MixedNested:
                    var sync = new List<StitchSyncFrame>();
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

                case StitchBenchmarkScenario.BoundaryNotFound:
                    _sync = SyncFrames(16, 100);
                    _segments = new[] { Segment(AsyncCallstackKind.RuntimeAsync, 200, 6, depth: 0) };
                    break;

                default:
                    throw new InvalidOperationException("Unknown benchmark scenario.");
            }
        }

        [Benchmark]
        public StitchResult Stitch() =>
            AsyncCpuStackStitcher.Stitch(
                _sync,
                _segments,
                Qpc,
                Classify,
                MethodCompletionObserved,
                MethodOf);

        [Benchmark]
        public int StitchIntoReusableBuffers()
        {
            AsyncCpuStackStitcher.StitchInto(
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

        private AsyncCallStack Segment(AsyncCallstackKind kind, int firstCodeAddress, int frameCount, int depth)
        {
            var methodIds = new ulong[frameCount];
            var frames = new AsyncCallStackFrames(kind, methodIds, kind == AsyncCallstackKind.StateMachineAsync ? new int[frameCount] : null);
            for (int i = 0; i < frameCount; i++)
            {
                CodeAddressIndex codeAddress = CodeAddress(firstCodeAddress + i);
                methodIds[i] = (ulong)(firstCodeAddress + i);
                frames.SetCodeAddressAt(i, codeAddress);
                _methods[codeAddress] = Method(firstCodeAddress + i);
            }

            return new AsyncCallStack(
                depth,
                (AsyncCallStackFramesIndex)depth,
                frames,
                continuationIndexBase: 0,
                wrapperCount: 32,
                startQpc: 900,
                endQpc: 1_100,
                methodCompletions: Array.Empty<AsyncCallStack.CompletionDelta>(),
                exceptionCompletions: Array.Empty<AsyncCallStack.CompletionDelta>(),
                wrapperResets: Array.Empty<long>());
        }

        private StitchSyncFrame[] RuntimeSync(int firstCodeAddress, int firstAsyncMethod, int wrapperIndex)
        {
            var result = new[]
            {
                Sync(firstCodeAddress, firstAsyncMethod + 10),
                Sync(firstCodeAddress + 1, firstAsyncMethod + wrapperIndex),
                Boundary(firstCodeAddress + 2, AsyncStitchBoundaryKind.V2ContinuationWrapper, wrapperIndex),
                Boundary(firstCodeAddress + 3, AsyncStitchBoundaryKind.V2DispatchContinuation),
                Boundary(firstCodeAddress + 4, AsyncStitchBoundaryKind.V2DispatchContinuation),
                Sync(firstCodeAddress + 5, 95),
                Sync(firstCodeAddress + 6, 96),
            };
            return result;
        }

        private StitchSyncFrame[] StateMachineSync(int firstCodeAddress, int firstAsyncMethod)
        {
            return new[]
            {
                Sync(firstCodeAddress, 90),
                Sync(firstCodeAddress + 1, firstAsyncMethod + 1),
                Sync(firstCodeAddress + 2, firstAsyncMethod),
                Boundary(firstCodeAddress + 3, AsyncStitchBoundaryKind.V1Dispatcher),
                Boundary(firstCodeAddress + 4, AsyncStitchBoundaryKind.V1DispatcherInfrastructure),
                Boundary(firstCodeAddress + 5, AsyncStitchBoundaryKind.V1DispatcherInfrastructure),
            };
        }

        private StitchSyncFrame[] SyncFrames(int count, int firstCodeAddress)
        {
            var result = new StitchSyncFrame[count];
            for (int i = 0; i < count; i++)
            {
                result[i] = Sync(firstCodeAddress + i, firstCodeAddress + i);
            }
            return result;
        }

        private StitchSyncFrame Sync(int codeAddress, int method)
        {
            CodeAddressIndex ca = CodeAddress(codeAddress);
            MethodIndex mi = Method(method);
            _methods[ca] = mi;
            return new StitchSyncFrame(ca, mi);
        }

        private StitchSyncFrame Boundary(
            int codeAddress, AsyncStitchBoundaryKind kind, int wrapperIndex = -1)
        {
            CodeAddressIndex ca = CodeAddress(codeAddress);
            _boundaries[ca] = new AsyncStitchBoundaryInfo(kind, wrapperIndex);
            return new StitchSyncFrame(ca, MethodIndex.Invalid);
        }

        private AsyncStitchBoundaryInfo Classify(CodeAddressIndex codeAddress) =>
            _boundaries.TryGetValue(codeAddress, out AsyncStitchBoundaryInfo boundary)
                ? boundary
                : AsyncStitchBoundaryInfo.None;

        private MethodIndex MethodOf(CodeAddressIndex codeAddress) =>
            _methods.TryGetValue(codeAddress, out MethodIndex method) ? method : MethodIndex.Invalid;

        private static bool MethodCompletionObserved(AsyncCallstackKind kind) => false;

        private static bool MethodCompletionObservedByProcess(
            ProcessIndex processIndex, AsyncCallstackKind kind, long activationStartQpc) => false;

        private static readonly ProcessIndex s_processIndex = (ProcessIndex)1;

        private static CodeAddressIndex CodeAddress(int value) => (CodeAddressIndex)value;

        private static MethodIndex Method(int value) => (MethodIndex)value;
    }

    public enum StitchBenchmarkScenario
    {
        NoAsync,
        RuntimeAsync,
        StateMachineAsync,
        MixedNested,
        BoundaryNotFound,
    }
}
