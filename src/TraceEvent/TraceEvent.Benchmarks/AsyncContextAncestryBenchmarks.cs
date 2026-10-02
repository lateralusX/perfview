using System;
using System.Collections.Generic;

using BenchmarkDotNet.Attributes;

using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

namespace TraceEventBenchmarks
{
    [MemoryDiagnoser]
    public class AsyncContextAncestryBenchmarks
    {
        private const long Qpc = 1_000;
        private const int ParentCount = 4;

        private static readonly ProcessIndex s_processIndex = (ProcessIndex)1;
        private static readonly AsyncThreadKey s_childThread =
            new AsyncThreadKey(s_processIndex, 42);

        private IReadOnlyList<StitchSyncFrame> _sync;
        private IReadOnlyList<AsyncCallStack> _segmentsWithoutParents;
        private IReadOnlyList<AsyncCallStack> _segmentsWithUnrelatedCreation;
        private IReadOnlyList<AsyncCallStack> _segmentsWithOneParent;
        private IReadOnlyList<AsyncCallStack> _segmentsWithTwoParents;
        private IReadOnlyList<AsyncCallStack> _segmentsWithParents;
        private IReadOnlyList<AsyncCallStack> _segmentsWithEightParents;
        private AsyncCallStacksIndex _indexWithoutParents;
        private AsyncCallStacksIndex _indexWithUnrelatedCreation;
        private AsyncCallStacksIndex _indexWithOneParent;
        private AsyncCallStacksIndex _indexWithTwoParents;
        private AsyncCallStacksIndex _indexWithParents;
        private AsyncCallStacksIndex _indexWithEightParents;
        private Dictionary<CodeAddressIndex, AsyncStitchBoundaryInfo> _boundaries;
        private Dictionary<CodeAddressIndex, MethodIndex> _methods;
        private List<StitchedFrame> _output;
        private List<AsyncSegmentPlacement> _placements;
        private StitchDiagnostics _diagnostics;
        private AsyncContextAncestryAugmenter _augmenter;
        private Func<CodeAddressIndex, AsyncStitchBoundaryInfo> _classify;
        private Func<ProcessIndex, AsyncCallstackKind, long, bool> _methodCompletionObserved;
        private Func<CodeAddressIndex, MethodIndex> _methodOf;

        [GlobalSetup]
        public void Setup()
        {
            _boundaries = new Dictionary<CodeAddressIndex, AsyncStitchBoundaryInfo>();
            _methods = new Dictionary<CodeAddressIndex, MethodIndex>();
            _output = new List<StitchedFrame>(64);
            _placements = new List<AsyncSegmentPlacement>(1);
            _diagnostics = new StitchDiagnostics();
            _augmenter = new AsyncContextAncestryAugmenter();
            _classify = Classify;
            _methodCompletionObserved = MethodCompletionObserved;
            _methodOf = MethodOf;

            _sync = RuntimeSync(
                firstCodeAddress: 100,
                firstAsyncMethod: 200,
                wrapperIndex: 3);

            _indexWithoutParents = CreateIndex(parentCount: 0);
            _segmentsWithoutParents =
                _indexWithoutParents.GetAsyncCallStacks(s_childThread, Qpc);

            _indexWithUnrelatedCreation = CreateIndex(parentCount: 0);
            _indexWithUnrelatedCreation.AddCreation(
                s_childThread,
                childDispatcherId: 999,
                parentDispatcherId: 1_000,
                createQpc: 800);
            _segmentsWithUnrelatedCreation =
                _indexWithUnrelatedCreation.GetAsyncCallStacks(s_childThread, Qpc);

            _indexWithOneParent = CreateIndex(parentCount: 1);
            _segmentsWithOneParent =
                _indexWithOneParent.GetAsyncCallStacks(s_childThread, Qpc);

            _indexWithTwoParents = CreateIndex(parentCount: 2);
            _segmentsWithTwoParents =
                _indexWithTwoParents.GetAsyncCallStacks(s_childThread, Qpc);

            _indexWithParents = CreateIndex(parentCount: ParentCount);
            _segmentsWithParents =
                _indexWithParents.GetAsyncCallStacks(s_childThread, Qpc);

            _indexWithEightParents = CreateIndex(parentCount: 8);
            _segmentsWithEightParents =
                _indexWithEightParents.GetAsyncCallStacks(s_childThread, Qpc);

            int baselineFrameCount = Disabled();
            if (EnabledWithoutCreationData() != baselineFrameCount)
            {
                throw new InvalidOperationException(
                    "Enabling ancestry without creation data changed the stitched stack.");
            }
            if (EnabledWithUnrelatedCreationData() != baselineFrameCount)
            {
                throw new InvalidOperationException(
                    "Unrelated creation data changed the stitched stack.");
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
        public int EnabledWithoutCreationData() =>
            StitchWithOptionalAncestry(
                _indexWithoutParents,
                _segmentsWithoutParents);

        [Benchmark]
        public int EnabledWithUnrelatedCreationData() =>
            StitchWithOptionalAncestry(
                _indexWithUnrelatedCreation,
                _segmentsWithUnrelatedCreation);

        [Benchmark]
        public int EnabledWithOneParent() =>
            StitchWithOptionalAncestry(
                _indexWithOneParent,
                _segmentsWithOneParent);

        [Benchmark]
        public int EnabledWithTwoParents() =>
            StitchWithOptionalAncestry(
                _indexWithTwoParents,
                _segmentsWithTwoParents);

        [Benchmark]
        public int EnabledWithFourParents() =>
            StitchWithOptionalAncestry(
                _indexWithParents,
                _segmentsWithParents);

        [Benchmark]
        public int EnabledWithEightParents() =>
            StitchWithOptionalAncestry(
                _indexWithEightParents,
                _segmentsWithEightParents);

        private int StitchWithOptionalAncestry(
            AsyncCallStacksIndex index,
            IReadOnlyList<AsyncCallStack> segments)
        {
            bool ancestryAvailable =
                index.HasCreationRecords &&
                _augmenter.Prepare(index, segments);
            Stitch(segments, ancestryAvailable ? _placements : null);
            if (!ancestryAvailable)
            {
                return _output.Count;
            }

            _augmenter.Augment(
                index,
                segments,
                _placements,
                _output,
                AsyncContextAncestryAugmenter.DefaultMaximumDepth);
            return _output.Count;
        }

        private void Stitch(
            IReadOnlyList<AsyncCallStack> segments,
            List<AsyncSegmentPlacement> placements)
        {
            AsyncCpuStackStitcher.StitchInto(
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

        private AsyncCallStacksIndex CreateIndex(int parentCount)
        {
            var index = new AsyncCallStacksIndex();
            AddActivation(
                index,
                s_childThread,
                dispatcherId: 1,
                depth: 0,
                startQpc: 900,
                endQpc: 1_100,
                firstMethodId: 200);
            SetCodeAddresses(
                index.GetAsyncCallStacks(s_childThread, Qpc)[0],
                firstCodeAddress: 200);

            if (parentCount == 0)
            {
                return index;
            }

            ulong childDispatcherId = 1;
            long childStartQpc = 900;
            for (int parentIndex = 0; parentIndex < parentCount; parentIndex++)
            {
                ulong parentDispatcherId = (ulong)(10 + parentIndex);
                long createQpc = childStartQpc - 100;
                var parentThread = new AsyncThreadKey(
                    s_processIndex,
                    (ulong)(100 + parentIndex));

                AddActivation(
                    index,
                    parentThread,
                    parentDispatcherId,
                    depth: 0,
                    startQpc: createQpc - 50,
                    endQpc: createQpc + 50,
                    firstMethodId: 300 + (parentIndex * 10));
                SetCodeAddresses(
                    index.GetAsyncCallStacks(parentThread, createQpc)[0],
                    firstCodeAddress: 300 + (parentIndex * 10));
                index.AddCreation(
                    parentThread,
                    childDispatcherId,
                    parentDispatcherId,
                    createQpc);

                childDispatcherId = parentDispatcherId;
                childStartQpc = createQpc - 50;
            }

            return index;
        }

        private static void AddActivation(
            AsyncCallStacksIndex index,
            AsyncThreadKey thread,
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
                methodCompletions: Array.Empty<AsyncCallStack.CompletionDelta>(),
                exceptionCompletions: Array.Empty<AsyncCallStack.CompletionDelta>(),
                wrapperResets: Array.Empty<long>());
        }

        private void SetCodeAddresses(AsyncCallStack activation, int firstCodeAddress)
        {
            for (int i = 0; i < activation.Frames.FrameCount; i++)
            {
                CodeAddressIndex codeAddress = CodeAddress(firstCodeAddress + i);
                activation.Frames.SetCodeAddressAt(i, codeAddress);
                _methods[codeAddress] = Method(firstCodeAddress + i);
            }
        }

        private StitchSyncFrame[] RuntimeSync(
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
                    AsyncStitchBoundaryKind.V2ContinuationWrapper,
                    wrapperIndex),
                Boundary(
                    firstCodeAddress + 3,
                    AsyncStitchBoundaryKind.V2DispatchContinuation),
                Boundary(
                    firstCodeAddress + 4,
                    AsyncStitchBoundaryKind.V2DispatchContinuation),
                Sync(firstCodeAddress + 5, 95),
                Sync(firstCodeAddress + 6, 96),
            };
        }

        private StitchSyncFrame Sync(int codeAddress, int method)
        {
            CodeAddressIndex ca = CodeAddress(codeAddress);
            MethodIndex mi = Method(method);
            _methods[ca] = mi;
            return new StitchSyncFrame(ca, mi);
        }

        private StitchSyncFrame Boundary(
            int codeAddress,
            AsyncStitchBoundaryKind kind,
            int wrapperIndex = -1)
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
