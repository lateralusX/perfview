using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

namespace TraceEventBenchmarks
{
    [MemoryDiagnoser]
    public class AsyncProfilerParentContextBenchmarks
    {
        private const long Qpc = 1_000;
        private const int ParentCount = 4;

        private static readonly ProcessIndex s_processIndex = (ProcessIndex)1;
        private static readonly AsyncProfilerThreadKey s_childThread =
            new AsyncProfilerThreadKey(s_processIndex, 42);

        private IReadOnlyList<AsyncProfilerStitchSyncFrame> _sync;
        private IReadOnlyList<AsyncProfilerCallStack> _segmentsWithoutParents;
        private IReadOnlyList<AsyncProfilerCallStack> _segmentsWithUnrelatedDispatcherInfo;
        private IReadOnlyList<AsyncProfilerCallStack> _segmentsWithOneParent;
        private IReadOnlyList<AsyncProfilerCallStack> _segmentsWithTwoParents;
        private IReadOnlyList<AsyncProfilerCallStack> _segmentsWithParents;
        private IReadOnlyList<AsyncProfilerCallStack> _segmentsWithEightParents;
        private AsyncProfilerCallStacksIndex _indexWithoutParents;
        private AsyncProfilerCallStacksIndex _indexWithUnrelatedDispatcherInfo;
        private AsyncProfilerCallStacksIndex _indexWithOneParent;
        private AsyncProfilerCallStacksIndex _indexWithTwoParents;
        private AsyncProfilerCallStacksIndex _indexWithParents;
        private AsyncProfilerCallStacksIndex _indexWithEightParents;
        private Dictionary<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> _boundaries;
        private Dictionary<CodeAddressIndex, MethodIndex> _methods;
        private List<AsyncProfilerStitchedFrame> _output;
        private List<AsyncProfilerSegmentPlacement> _placements;
        private AsyncProfilerCallStackDiagnostics _diagnostics;
        private AsyncProfilerParentContextAugmenter _augmenter;
        private Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> _classify;
        private Func<ProcessIndex, AsyncCallstackKind, long, bool> _methodCompletionObserved;
        private Func<CodeAddressIndex, MethodIndex> _methodOf;

        [GlobalSetup]
        public void Setup()
        {
            _boundaries = new Dictionary<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo>();
            _methods = new Dictionary<CodeAddressIndex, MethodIndex>();
            _output = new List<AsyncProfilerStitchedFrame>(64);
            _placements = new List<AsyncProfilerSegmentPlacement>(1);
            _diagnostics = new AsyncProfilerCallStackDiagnostics();
            _augmenter = new AsyncProfilerParentContextAugmenter();
            _classify = Classify;
            _methodCompletionObserved = MethodCompletionObserved;
            _methodOf = MethodOf;

            _sync = RuntimeSync(
                firstCodeAddress: 100,
                firstAsyncMethod: 200,
                wrapperIndex: 3);

            _indexWithoutParents = CreateIndex(parentCount: 0);
            _segmentsWithoutParents =
                _indexWithoutParents.GetCallStacks(s_childThread, Qpc);

            _indexWithUnrelatedDispatcherInfo = CreateIndex(parentCount: 0);
            _indexWithUnrelatedDispatcherInfo.AddDispatcherInfo(
                s_childThread,
                dispatcherId: 999,
                parentDispatcherId: 1_000,
                creationQpc: 800);
            _segmentsWithUnrelatedDispatcherInfo =
                _indexWithUnrelatedDispatcherInfo.GetCallStacks(s_childThread, Qpc);

            _indexWithOneParent = CreateIndex(parentCount: 1);
            _segmentsWithOneParent =
                _indexWithOneParent.GetCallStacks(s_childThread, Qpc);

            _indexWithTwoParents = CreateIndex(parentCount: 2);
            _segmentsWithTwoParents =
                _indexWithTwoParents.GetCallStacks(s_childThread, Qpc);

            _indexWithParents = CreateIndex(parentCount: ParentCount);
            _segmentsWithParents =
                _indexWithParents.GetCallStacks(s_childThread, Qpc);

            _indexWithEightParents = CreateIndex(parentCount: 8);
            _segmentsWithEightParents =
                _indexWithEightParents.GetCallStacks(s_childThread, Qpc);

            int baselineFrameCount = Disabled();
            if (EnabledWithoutDispatcherInfo() != baselineFrameCount)
            {
                throw new InvalidOperationException(
                    "Enabling parent contexts without dispatcher metadata changed the stitched stack.");
            }
            if (EnabledWithUnrelatedDispatcherInfo() != baselineFrameCount)
            {
                throw new InvalidOperationException(
                    "Unrelated dispatcher metadata changed the stitched stack.");
            }
            if (EnabledWithOneParent() != baselineFrameCount + 6)
            {
                throw new InvalidOperationException(
                    "The one-parent benchmark did not insert the expected historical frames.");
            }
            if (EnabledWithTwoParents() != baselineFrameCount + 12)
            {
                throw new InvalidOperationException(
                    "The two-parent benchmark did not insert the expected historical frames.");
            }
            if (EnabledWithFourParents() != baselineFrameCount + (ParentCount * 6))
            {
                throw new InvalidOperationException(
                    "The four-parent benchmark did not insert the expected historical frames.");
            }
            if (EnabledWithEightParents() != baselineFrameCount + 48)
            {
                throw new InvalidOperationException(
                    "The eight-parent benchmark did not insert the expected historical frames.");
            }
        }

        [Benchmark(Baseline = true)]
        public int Disabled()
        {
            Stitch(_segmentsWithoutParents, placements: null);
            return _output.Count;
        }

        [Benchmark]
        public int EnabledWithoutDispatcherInfo() =>
            StitchWithOptionalParentContexts(
                _indexWithoutParents,
                _segmentsWithoutParents);

        [Benchmark]
        public int EnabledWithUnrelatedDispatcherInfo() =>
            StitchWithOptionalParentContexts(
                _indexWithUnrelatedDispatcherInfo,
                _segmentsWithUnrelatedDispatcherInfo);

        [Benchmark]
        public int EnabledWithOneParent() =>
            StitchWithOptionalParentContexts(
                _indexWithOneParent,
                _segmentsWithOneParent);

        [Benchmark]
        public int EnabledWithTwoParents() =>
            StitchWithOptionalParentContexts(
                _indexWithTwoParents,
                _segmentsWithTwoParents,
                maximumDepth: 2);

        [Benchmark]
        public int EnabledWithFourParents() =>
            StitchWithOptionalParentContexts(
                _indexWithParents,
                _segmentsWithParents,
                maximumDepth: ParentCount);

        [Benchmark]
        public int EnabledWithEightParents() =>
            StitchWithOptionalParentContexts(
                _indexWithEightParents,
                _segmentsWithEightParents,
                maximumDepth: 8);

        private int StitchWithOptionalParentContexts(
            AsyncProfilerCallStacksIndex index,
            IReadOnlyList<AsyncProfilerCallStack> segments,
            int maximumDepth = AsyncProfilerParentContextAugmenter.DefaultMaximumDepth)
        {
            bool parentContextsAvailable =
                index.HasDispatcherRecords &&
                _augmenter.Prepare(index, segments);
            Stitch(segments, parentContextsAvailable ? _placements : null);
            if (!parentContextsAvailable)
            {
                return _output.Count;
            }

            _augmenter.Augment(
                index,
                segments,
                _placements,
                _output,
                maximumDepth);
            return _output.Count;
        }

        private void Stitch(
            IReadOnlyList<AsyncProfilerCallStack> segments,
            List<AsyncProfilerSegmentPlacement> placements)
        {
            AsyncProfilerCallStackStitcher.StitchInto(
                _sync,
                segments,
                Qpc,
                _classify,
                _methodCompletionObserved,
                s_processIndex,
                _methodOf,
                trace: false,
                _output,
                _diagnostics,
                placements);
        }

        private AsyncProfilerCallStacksIndex CreateIndex(int parentCount)
        {
            var index = new AsyncProfilerCallStacksIndex();
            AddActivation(
                index,
                s_childThread,
                dispatcherId: 1,
                depth: 0,
                startQpc: 900,
                endQpc: 1_100,
                firstMethodId: 200);
            SetCodeAddresses(
                index.GetCallStacks(s_childThread, Qpc)[0],
                firstCodeAddress: 200);

            if (parentCount == 0)
            {
                return index;
            }

            ulong dispatcherId = 1;
            long childStartQpc = 900;
            for (int parentIndex = 0; parentIndex < parentCount; parentIndex++)
            {
                ulong parentDispatcherId = (ulong)(10 + parentIndex);
                long creationQpc = childStartQpc - 100;
                var parentThread = new AsyncProfilerThreadKey(
                    s_processIndex,
                    (ulong)(100 + parentIndex));

                AddActivation(
                    index,
                    parentThread,
                    parentDispatcherId,
                    depth: 0,
                    startQpc: creationQpc - 50,
                    endQpc: creationQpc + 50,
                    firstMethodId: 300 + (parentIndex * 10));
                SetCodeAddresses(
                    index.GetCallStacks(parentThread, creationQpc)[0],
                    firstCodeAddress: 300 + (parentIndex * 10));
                index.AddDispatcherInfo(
                    parentThread,
                    dispatcherId,
                    parentDispatcherId,
                    creationQpc);

                dispatcherId = parentDispatcherId;
                childStartQpc = creationQpc - 50;
            }

            return index;
        }

        private static void AddActivation(
            AsyncProfilerCallStacksIndex index,
            AsyncProfilerThreadKey thread,
            ulong dispatcherId,
            int depth,
            long startQpc,
            long endQpc,
            int firstMethodId)
        {
            const int FrameCount = 6;
            var methodIds = new ulong[FrameCount];
            for (int i = 0; i < methodIds.Length; i++)
            {
                methodIds[i] = (ulong)(firstMethodId + i);
            }

            index.Add(
                thread,
                AsyncCallstackKind.RuntimeAsync,
                methodIds,
                frameStates: null,
                dispatcherId,
                depth,
                continuationIndexBase: 0,
                wrapperCount: 32,
                startQpc,
                endQpc,
                methodCompletions: Array.Empty<AsyncProfilerCallStack.CompletionDelta>(),
                exceptionCompletions: Array.Empty<AsyncProfilerCallStack.CompletionDelta>(),
                wrapperResets: Array.Empty<long>());
        }

        private void SetCodeAddresses(AsyncProfilerCallStack activation, int firstCodeAddress)
        {
            for (int i = 0; i < activation.Frames.FrameCount; i++)
            {
                CodeAddressIndex codeAddress = CodeAddress(firstCodeAddress + i);
                activation.Frames.SetCodeAddressAt(i, codeAddress);
                _methods[codeAddress] = Method(firstCodeAddress + i);
            }
        }

        private AsyncProfilerStitchSyncFrame[] RuntimeSync(
            int firstCodeAddress,
            int firstAsyncMethod,
            int wrapperIndex)
        {
            return new[]
            {
                Sync(firstCodeAddress, firstAsyncMethod + 10),
                Sync(firstCodeAddress + 1, firstAsyncMethod + wrapperIndex),
                Boundary(
                    firstCodeAddress + 2,
                    AsyncProfilerCallStackStitchBoundaryKind.V2ContinuationWrapper,
                    wrapperIndex),
                Boundary(
                    firstCodeAddress + 3,
                    AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation),
                Boundary(
                    firstCodeAddress + 4,
                    AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation),
                Sync(firstCodeAddress + 5, 95),
                Sync(firstCodeAddress + 6, 96),
            };
        }

        private AsyncProfilerStitchSyncFrame Sync(int codeAddress, int method)
        {
            CodeAddressIndex ca = CodeAddress(codeAddress);
            MethodIndex mi = Method(method);
            _methods[ca] = mi;
            return new AsyncProfilerStitchSyncFrame(ca, mi);
        }

        private AsyncProfilerStitchSyncFrame Boundary(
            int codeAddress,
            AsyncProfilerCallStackStitchBoundaryKind kind,
            int wrapperIndex = -1)
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
            _methods.TryGetValue(codeAddress, out MethodIndex method)
                ? method
                : MethodIndex.Invalid;

        private static bool MethodCompletionObserved(
            ProcessIndex processIndex,
            AsyncCallstackKind kind,
            long activationStartQpc) => false;

        private static CodeAddressIndex CodeAddress(int value) =>
            (CodeAddressIndex)value;

        private static MethodIndex Method(int value) =>
            (MethodIndex)value;
    }
}
