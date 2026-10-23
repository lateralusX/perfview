// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

using Xunit;

namespace TraceEventTests
{
    /// <summary>
    /// Unit tests for the pure stitch core <see cref="AsyncProfilerCallStackStitcher"/>. They drive the delegate-based
    /// <c>Stitch</c> overload with synthetic <see cref="AsyncProfilerStitchSyncFrame"/>s and synthetic
    /// <see cref="AsyncProfilerCallStack"/> segments (built via the internals-visible constructors), plus
    /// dictionary-backed <c>classify</c> / <c>completionObserved</c> / <c>methodOf</c> delegates, so no real
    /// symbol table or <c>TraceLog</c> is required.
    /// <para>
    /// Identity model: <see cref="CodeAddressIndex"/> and <see cref="MethodIndex"/> are treated as opaque
    /// integer handles. A sync frame carries both directly; an async frame carries a code address whose
    /// <c>MethodIndex</c> is resolved through the <c>methodOf</c> map (or <see cref="CodeAddressIndex.Invalid"/>
    /// for a 0-IP frame).
    /// </para>
    /// </summary>
    public class AsyncProfilerCallStackStitcherTests
    {
        private const long Qpc = 1000;

        // A tiny builder for a synthetic async segment and its per-frame identity/boundary wiring.
        private sealed class Scenario
        {
            private readonly Dictionary<CodeAddressIndex, MethodIndex> _methodOf = new Dictionary<CodeAddressIndex, MethodIndex>();
            private readonly Dictionary<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> _classify = new Dictionary<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo>();
            private readonly HashSet<AsyncCallstackKind> _methodCompletionObserved = new HashSet<AsyncCallstackKind>();

            public Func<CodeAddressIndex, MethodIndex> MethodOf => ca => _methodOf.TryGetValue(ca, out MethodIndex m) ? m : MethodIndex.Invalid;
            public Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> Classify => ca => _classify.TryGetValue(ca, out AsyncProfilerCallStackStitchBoundaryInfo b) ? b : AsyncProfilerCallStackStitchBoundaryInfo.None;
            public Func<AsyncCallstackKind, bool> MethodCompletionObserved => k => _methodCompletionObserved.Contains(k);

            public static CodeAddressIndex CA(int i) => (CodeAddressIndex)i;
            public static MethodIndex MI(int i) => (MethodIndex)i;

            public void MarkMethodCompletionObserved(AsyncCallstackKind kind) => _methodCompletionObserved.Add(kind);

            public void RegisterMethod(int codeAddr, int method) =>
                _methodOf[CA(codeAddr)] = MI(method);

            /// <summary>Declares a sync frame with a code address whose method is <paramref name="method"/>.</summary>
            public AsyncProfilerStitchSyncFrame Sync(int codeAddr, int method) => new AsyncProfilerStitchSyncFrame(CA(codeAddr), MI(method));

            public AsyncProfilerStitchSyncFrame Sync(int codeAddr, int method, AsyncProfilerStitchSyncFrameKind kind) =>
                new AsyncProfilerStitchSyncFrame(CA(codeAddr), MI(method), kind);

            public AsyncProfilerStitchSyncFrame BoxCarrier(int codeAddr, int boxedMethod) =>
                new AsyncProfilerStitchSyncFrame(CA(codeAddr), MI(codeAddr), AsyncProfilerStitchSyncFrameKind.None, MI(boxedMethod));

            /// <summary>Declares a sync boundary frame classified as <paramref name="info"/>.</summary>
            public AsyncProfilerStitchSyncFrame Boundary(int codeAddr, AsyncProfilerCallStackStitchBoundaryInfo info)
            {
                _classify[CA(codeAddr)] = info;
                return new AsyncProfilerStitchSyncFrame(CA(codeAddr), MI(codeAddr));
            }

            /// <summary>
            /// Builds a synthetic async segment (leaf-first frames). Each entry is a code address; a negative
            /// value means <see cref="CodeAddressIndex.Invalid"/> (a 0-IP frame). The <paramref name="frameMethods"/>
            /// (same length) wire each async code address to its <c>MethodIndex</c> via the <c>methodOf</c> map.
            /// </summary>
            public AsyncProfilerCallStack Segment(AsyncCallstackKind kind, int[] frameCodeAddrs, int[] frameMethods,
                AsyncProfilerCallStack.CompletionDelta[] completions = null, AsyncProfilerCallStack.CompletionDelta[] exceptionCompletions = null,
                byte continuationIndexBase = 0, byte wrapperCount = 32, ulong[] frameMethodIds = null,
                long[] wrapperResets = null)
            {
                int n = frameCodeAddrs.Length;
                var methodIds = frameMethodIds ?? new ulong[n];
                var frames = new AsyncProfilerCallStackFrames(kind, methodIds, null);
                for (int i = 0; i < n; i++)
                {
                    if (frameCodeAddrs[i] < 0)
                    {
                        frames.SetCodeAddressAt(i, CodeAddressIndex.Invalid);
                    }
                    else
                    {
                        CodeAddressIndex ca = CA(frameCodeAddrs[i]);
                        frames.SetCodeAddressAt(i, ca);
                        _methodOf[ca] = MI(frameMethods[i]);
                    }
                }

                return new AsyncProfilerCallStack(depth: 0, framesIndex: (AsyncProfilerCallStackFramesIndex)0, frames: frames,
                    continuationIndexBase: continuationIndexBase, wrapperCount: wrapperCount, startQpc: 0, endQpc: long.MaxValue,
                    methodCompletions: completions ?? Array.Empty<AsyncProfilerCallStack.CompletionDelta>(),
                    exceptionCompletions: exceptionCompletions ?? Array.Empty<AsyncProfilerCallStack.CompletionDelta>(),
                    wrapperResets: wrapperResets ?? Array.Empty<long>());
            }

            public AsyncProfilerStitchResult Run(IReadOnlyList<AsyncProfilerStitchSyncFrame> sync, IReadOnlyList<AsyncProfilerCallStack> segmentsRootToLeaf) =>
                AsyncProfilerCallStackStitcher.Stitch(sync, segmentsRootToLeaf, Qpc, Classify, MethodCompletionObserved, MethodOf);

            public AsyncProfilerStitchResult Run(IReadOnlyList<AsyncProfilerStitchSyncFrame> sync, IReadOnlyList<AsyncProfilerCallStack> segmentsRootToLeaf, bool trace) =>
                AsyncProfilerCallStackStitcher.Stitch(sync, segmentsRootToLeaf, Qpc, Classify, MethodCompletionObserved, MethodOf, trace);

            public AsyncProfilerStitchResult Run(IReadOnlyList<AsyncProfilerStitchSyncFrame> sync, IReadOnlyList<AsyncProfilerCallStack> segments,
                AsyncProfilerRuntimeRuleProfile profile, Func<CodeAddressIndex, AsyncProfilerRuntimeRuleProfile> runtimeProfileResolver = null)
            {
                var output = new List<AsyncProfilerStitchedFrame>();
                var diagnostics = new AsyncProfilerCallStackDiagnostics();
                AsyncProfilerCallStackStitcher.StitchInto(sync, segments, Qpc, Classify,
                    (process, kind, qpc) => MethodCompletionObserved(kind), ProcessIndex.Invalid, MethodOf,
                    false, output, diagnostics, null, runtimeProfileResolver ?? (_ => profile), profile);
                return new AsyncProfilerStitchResult(output, diagnostics);
            }
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, false, false)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, false, false)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, true, false)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, true, false)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, false, true)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, false, true)]
        public void ProfiledStructuralPlumbing_RecognizesInsertedHelpersButPreservesForeignSpansAndBoundaries(
            int kindValue, bool foreignProfile, bool crossesBoundary)
        {
            var kind = (AsyncCallstackKind)kindValue;
            var s = new Scenario();
            var profile = new StructuralRulesProfile(2);
            AsyncProfilerCallStack segment = s.Segment(kind, new[] { 800, 801 }, new[] { 10, 11 });
            var sync = new[]
            {
                s.Sync(50, 10),
                s.Boundary(51, kind == AsyncCallstackKind.StateMachineAsync ? V1Dispatcher : Wrapper(0)),
                s.Sync(52, 90, AsyncProfilerStitchSyncFrameKind.V1MethodBuilderInfrastructure),
                s.Boundary(53, crossesBoundary ? V1Dispatcher :
                    kind == AsyncCallstackKind.StateMachineAsync ? V1Infrastructure : DispatchContinuation),
                s.Sync(54, 99),
            };
            AsyncProfilerStitchResult common = s.Run(sync, new[] { segment });
            Assert.Contains(common.Frames, frame => frame.CodeAddress == Scenario.CA(52));
            AsyncProfilerStitchResult changed = s.Run(sync, new[] { segment }, profile,
                ca => foreignProfile && ca == Scenario.CA(52) ? AsyncProfilerRuntimeRuleProfile.Common : profile);
            Assert.Equal(foreignProfile || crossesBoundary,
                changed.Frames.Any(frame => frame.CodeAddress == Scenario.CA(52)));
            Assert.Contains(changed.Frames, frame => frame.CodeAddress == Scenario.CA(54));
            Assert.Contains(changed.Frames, frame => frame.CodeAddress == Scenario.CA(801));
        }

        [Fact]
        public void ProfiledStructuralPlumbing_InvalidBoundsThrow()
        {
            var s = new Scenario();
            AsyncProfilerCallStack segment = s.Segment(AsyncCallstackKind.StateMachineAsync, new[] { 800 }, new[] { 10 });
            var sync = new[]
            {
                s.Sync(50, 10), s.Boundary(51, V1Dispatcher),
                s.Sync(52, 90, AsyncProfilerStitchSyncFrameKind.V1MethodBuilderInfrastructure),
            };
            Assert.Throws<InvalidOperationException>(() =>
                s.Run(sync, new[] { segment }, new StructuralRulesProfile(10)));
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void ProfiledCarrierEvidence_CannotOverridePhysicalBodyPrecedence(bool allowCarrier, bool physicalBody)
        {
            var s = new Scenario();
            AsyncProfilerCallStack segment = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 800, 801 }, new[] { 10, 11 });
            var sync = new[]
            {
                s.Sync(50, physicalBody ? 10 : 90), s.BoxCarrier(51, 10),
                s.Boundary(52, V1Dispatcher), s.Sync(53, 99),
            };
            AsyncProfilerStitchResult result = s.Run(sync, new[] { segment },
                new StructuralRulesProfile(0, allowCarrier: allowCarrier));
            Assert.Equal(allowCarrier && !physicalBody, result.Frames.Any(frame => frame.IsStateMachineBoxCarrier));
            Assert.Contains(result.Frames, frame => frame.CodeAddress == Scenario.CA(50));
        }

        private sealed class StructuralRulesProfile : AsyncProfilerRuntimeRuleProfile
        {
            public StructuralRulesProfile(int plumbingLength, bool allowCarrier = true, bool allowCompletion = true)
                : base("Test-only structural rules")
            {
                m_plumbingLength = plumbingLength;
                m_allowCarrier = allowCarrier;
                m_allowCompletion = allowCompletion;
            }

            public override AsyncProfilerRuntimeSequenceMatch MatchDispatchPlumbing(
                AsyncCallstackKind kind, IReadOnlyList<AsyncProfilerStitchSyncFrame> frames, int start, int end,
                Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classify) =>
                m_plumbingLength != 0 && start < end &&
                frames[start].Kind == AsyncProfilerStitchSyncFrameKind.V1MethodBuilderInfrastructure
                    ? AsyncProfilerRuntimeSequenceMatch.Match(m_plumbingLength)
                    : base.MatchDispatchPlumbing(kind, frames, start, end, classify);

            public override bool IsV1BoxCarrierCandidate(
                IReadOnlyList<AsyncProfilerStitchSyncFrame> frames, int start, int end, int index) =>
                m_allowCarrier && base.IsV1BoxCarrierCandidate(frames, start, end, index);

            public override bool HasV1CompletionEvidence(IReadOnlyList<AsyncProfilerStitchSyncFrame> frames, int start, int end) =>
                m_allowCompletion && base.HasV1CompletionEvidence(frames, start, end);

            private readonly int m_plumbingLength;
            private readonly bool m_allowCarrier;
            private readonly bool m_allowCompletion;
        }

        private static AsyncProfilerCallStackStitchBoundaryInfo Wrapper(int index) => new AsyncProfilerCallStackStitchBoundaryInfo(AsyncProfilerCallStackStitchBoundaryKind.V2ContinuationWrapper, index);
        private static readonly AsyncProfilerCallStackStitchBoundaryInfo DispatchContinuation = new AsyncProfilerCallStackStitchBoundaryInfo(AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation, -1);
        private static readonly AsyncProfilerCallStackStitchBoundaryInfo V1Dispatcher = new AsyncProfilerCallStackStitchBoundaryInfo(AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher, -1);
        private static readonly AsyncProfilerCallStackStitchBoundaryInfo V1Infrastructure =
            new AsyncProfilerCallStackStitchBoundaryInfo(AsyncProfilerCallStackStitchBoundaryKind.V1DispatcherInfrastructure, -1);

        private static AsyncProfilerCallStack.CompletionDelta[] Completed(int count) =>
            new[] { new AsyncProfilerCallStack.CompletionDelta(Qpc, count) };

        private static AsyncProfilerCallStack.CompletionDelta[] Unwound(int frames) =>
            new[] { new AsyncProfilerCallStack.CompletionDelta(Qpc, frames) };

        [Fact]
        public void StitchedFrame_BoxCarrierMetadata_PreservesCompactStorage()
        {
            Assert.Equal(6 * sizeof(int) + 2 * IntPtr.Size,
                System.Runtime.CompilerServices.Unsafe.SizeOf<AsyncProfilerStitchedFrame>());
        }

        [Fact]
        public void StitchedFrame_CurrentFactory_BoxIdentityCreatesAResumptionTarget()
        {
            var s = new Scenario();
            AsyncProfilerCallStack segment = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 500 }, new[] { 30 });
            AsyncProfilerStitchedFrame frame = AsyncProfilerStitchedFrame.CreateAsyncCurrent(
                Scenario.CA(40), Scenario.MI(40), segment, 0, Scenario.MI(30));

            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget, frame.Origin);
            Assert.True(frame.IsStateMachineBoxCarrier);
            Assert.Equal(Scenario.CA(40), frame.CodeAddress);
            Assert.Equal(Scenario.MI(40), frame.Method);
            Assert.Equal(Scenario.MI(30), frame.BoxedStateMachineMethod);
        }

        [Fact]
        public void NoSegments_IsVerbatimPassthrough()
        {
            var s = new Scenario();
            var sync = new[] { s.Sync(10, 1), s.Sync(11, 2), s.Sync(12, 3) };

            AsyncProfilerStitchResult result = s.Run(sync, Array.Empty<AsyncProfilerCallStack>());

            Assert.Equal(3, result.Frames.Count);
            Assert.All(result.Frames, f => Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, f.Origin));
            Assert.Equal(new[] { Scenario.CA(10), Scenario.CA(11), Scenario.CA(12) }, result.Frames.Select(f => f.CodeAddress));
            Assert.Equal(0, result.Diagnostics.SegmentsProcessed);
            Assert.Equal(0, result.Diagnostics.BoundariesNotFound);
        }

        [Fact]
        public void StitchInto_ReusesAndClearsCallerOwnedBuffers()
        {
            var s = new Scenario();
            AsyncProfilerCallStack segment = s.Segment(
                AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 200, 201 },
                frameMethods: new[] { 50, 51 });
            var stitchedSync = new[]
            {
                s.Sync(10, 50),
                s.Boundary(11, Wrapper(0)),
                s.Sync(12, 99),
            };
            var output = new List<AsyncProfilerStitchedFrame>();
            var diagnostics = new AsyncProfilerCallStackDiagnostics();
            long completionPolicyQpc = long.MinValue;

            AsyncProfilerCallStackStitcher.StitchInto(
                stitchedSync,
                new[] { segment },
                Qpc,
                s.Classify,
                (processIndex, kind, activationStartQpc) =>
                {
                    completionPolicyQpc = activationStartQpc;
                    return s.MethodCompletionObserved(kind);
                },
                (ProcessIndex)1,
                s.MethodOf,
                trace: false,
                output,
                diagnostics);

            Assert.Equal(
                new[] { Scenario.CA(10), Scenario.CA(201), Scenario.CA(12) },
                output.Select(frame => frame.CodeAddress));
            Assert.Equal(1, diagnostics.SegmentsProcessed);
            Assert.Equal(segment.StartQpc, completionPolicyQpc);

            var passthroughSync = new[] { s.Sync(20, 60), s.Sync(21, 61) };
            AsyncProfilerCallStackStitcher.StitchInto(
                passthroughSync,
                Array.Empty<AsyncProfilerCallStack>(),
                Qpc,
                s.Classify,
                (processIndex, kind, activationStartQpc) => s.MethodCompletionObserved(kind),
                (ProcessIndex)1,
                s.MethodOf,
                trace: false,
                output,
                diagnostics);

            Assert.Equal(
                new[] { Scenario.CA(20), Scenario.CA(21) },
                output.Select(frame => frame.CodeAddress));
            Assert.Equal(0, diagnostics.SegmentsProcessed);
            Assert.Equal(0, diagnostics.V2WrapperFallbackUsed);
        }

        [Fact]
        public void NoSegments_WrapperIsLeaf_IsVerbatim()
        {
            // No async segment covers this sample and the sampled leaf is a continuation-wrapper. With no ancestry to
            // splice, the sync stack is emitted verbatim - the wrapper is kept, not dropped - so the coverage gap is
            // plainly visible rather than hidden behind a synthetic edit.
            var s = new Scenario();
            var sync = new[]
            {
                s.Boundary(80, Wrapper(0)),           // wrapper IS the leaf -> kept as-is
                s.Boundary(81, DispatchContinuation),
                s.Sync(82, 99),
                s.Sync(83, 98),
            };

            AsyncProfilerStitchResult result = s.Run(sync, Array.Empty<AsyncProfilerCallStack>());

            Assert.Equal(4, result.Frames.Count);
            Assert.All(result.Frames, f => Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, f.Origin));
            Assert.Equal(new[] { Scenario.CA(80), Scenario.CA(81), Scenario.CA(82), Scenario.CA(83) }, result.Frames.Select(f => f.CodeAddress));
            Assert.Equal(0, result.Diagnostics.V2LeafWrapperDropped);
            Assert.Equal(0, result.Diagnostics.SegmentsProcessed);
        }

        [Fact]
        public void NoSegments_WrapperAboveLeaf_IsVerbatim()
        {
            // No async segment, with a continuation-wrapper above the sampled leaf. The whole sync stack is emitted
            // verbatim (wrapper and plumbing kept); nothing is spliced or dropped.
            var s = new Scenario();
            var sync = new[]
            {
                s.Sync(70, 50),                       // real leaf
                s.Boundary(71, Wrapper(0)),           // wrapper above the leaf -> kept
                s.Boundary(72, DispatchContinuation), // plumbing -> kept
                s.Sync(73, 98),
            };

            AsyncProfilerStitchResult result = s.Run(sync, Array.Empty<AsyncProfilerCallStack>());

            Assert.Equal(4, result.Frames.Count);
            Assert.All(result.Frames, f => Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, f.Origin));
            Assert.Equal(new[] { Scenario.CA(70), Scenario.CA(71), Scenario.CA(72), Scenario.CA(73) }, result.Frames.Select(f => f.CodeAddress));
            Assert.Equal(0, result.Diagnostics.SegmentsProcessed);
        }

        [Fact]
        public void NoSegments_NoWrapper_IsVerbatimPassthrough()
        {
            // No async segment and no wrapper (e.g. bare dispatch-continuation plumbing): emitted verbatim.
            var s = new Scenario();
            var sync = new[]
            {
                s.Boundary(84, DispatchContinuation),
                s.Sync(85, 99),
                s.Sync(86, 98),
            };

            AsyncProfilerStitchResult result = s.Run(sync, Array.Empty<AsyncProfilerCallStack>());

            Assert.Equal(3, result.Frames.Count);
            Assert.Equal(new[] { Scenario.CA(84), Scenario.CA(85), Scenario.CA(86) }, result.Frames.Select(f => f.CodeAddress));
            Assert.Equal(0, result.Diagnostics.SegmentsProcessed);
        }

        [Fact]
        public void EmptySegment_PreservesPhysicalSyncBoundaries()
        {
            var s = new Scenario();
            AsyncProfilerCallStack empty = s.Segment(
                AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: Array.Empty<int>(),
                frameMethods: Array.Empty<int>());
            var sync = new[]
            {
                s.Sync(70, 50),
                s.Boundary(71, Wrapper(0)),
                s.Boundary(72, DispatchContinuation),
                s.Sync(73, 98),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { empty });

            Assert.Equal(4, result.Frames.Count);
            Assert.All(result.Frames, frame => Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, frame.Origin));
            Assert.Equal(new[] { Scenario.CA(70), Scenario.CA(71), Scenario.CA(72), Scenario.CA(73) },
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(0, result.Diagnostics.SegmentsProcessed);
        }

        [Fact]
        public void V2_CompletionEvents_SplicesRemainingAncestryAndMarksCurrentAsync()
        {
            // Segment (leaf-first): [C0, C1, Mcur, A1, A2]; completed=2 -> current=Mcur (frames[2]); ancestry=A1,A2.
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            AsyncProfilerCallStack seg = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 200, 201, 202, 203, 204 },
                frameMethods:   new[] {  50,  51,  52,  53,  54 },
                completions: Completed(2));

            // Sync (leaf-first): Mcur is adjacent above the wrapper boundary; completed frames already returned.
            var sync = new[]
            {
                s.Sync(90, 52),                     // Mcur (current, running) -> method 52 matches frames[2]
                s.Boundary(91, Wrapper(3)),         // V2 continuation wrapper boundary
                s.Sync(92, 99),                     // root
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg });

            // Expected: [Mcur(AsyncCurrent), A1(Async), A2(Async), root(Sync)].
            Assert.Equal(4, result.Frames.Count);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCurrent, result.Frames[0].Origin);
            Assert.Equal(Scenario.CA(90), result.Frames[0].CodeAddress);           // retain sampled code address
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncRemaining, result.Frames[1].Origin);
            Assert.Equal(Scenario.CA(203), result.Frames[1].CodeAddress);          // A1
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncRemaining, result.Frames[2].Origin);
            Assert.Equal(Scenario.CA(204), result.Frames[2].CodeAddress);          // A2
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, result.Frames[3].Origin);
            Assert.Equal(Scenario.CA(92), result.Frames[3].CodeAddress);           // root

            Assert.Equal(1, result.Diagnostics.SegmentsProcessed);
            Assert.Equal(0, result.Diagnostics.BoundariesNotFound);
            Assert.Equal(0, result.Diagnostics.AdjacencyMismatches);
            Assert.Equal(0, result.Diagnostics.V2WrapperFallbackUsed); // completion events used, not the fallback
        }

        [Fact]
        public void V2_NoCompletionEvents_UsesWrapperSlotFallback()
        {
            // No completion events -> completed derived from the wrapper slot (index 1 -> completed 1).
            var s = new Scenario();
            AsyncProfilerCallStack seg = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 300, 301, 302 },
                frameMethods:   new[] {  60,  61,  62 });

            var sync = new[]
            {
                s.Sync(80, 61),                 // current (frames[1]) adjacent above the wrapper
                s.Boundary(81, Wrapper(1)),     // wrapper slot 1 -> completed = 1
                s.Sync(82, 99),                 // root
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg });

            // completed=1 -> current=frames[1]; splice frames[2]=302.
            Assert.Equal(3, result.Frames.Count);
            Assert.Equal(Scenario.CA(80), result.Frames[0].CodeAddress);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncRemaining, result.Frames[1].Origin);
            Assert.Equal(Scenario.CA(302), result.Frames[1].CodeAddress);
            Assert.Equal(Scenario.CA(82), result.Frames[2].CodeAddress);
            Assert.Equal(1, result.Diagnostics.V2WrapperFallbackUsed);
            Assert.Equal(0, result.Diagnostics.AdjacencyMismatches);
        }

        [Theory]
        [InlineData(30, true, 0)]
        [InlineData(31, true, 1)]
        [InlineData(-1, true, 0)]
        [InlineData(30, false, 0)]
        public void V2_CurrentMatchingAndAdjacency_ResolveTheIndexedMethodOnce(
            int physicalMethod, bool resolved, int mismatches)
        {
            var s = new Scenario();
            AsyncProfilerCallStack segment = s.Segment(
                AsyncCallstackKind.RuntimeAsync, new[] { 500, 501 }, new[] { 30, 32 });
            var sync = new[]
            {
                s.Sync(40, physicalMethod),
                s.Boundary(41, Wrapper(0)),
                s.Sync(42, 99),
            };
            int resolutionCount = 0;
            Func<CodeAddressIndex, MethodIndex> methodOf = s.MethodOf;
            AsyncProfilerStitchResult result = AsyncProfilerCallStackStitcher.Stitch(
                sync, new[] { segment }, Qpc, s.Classify, s.MethodCompletionObserved,
                codeAddress =>
                {
                    resolutionCount++;
                    return resolved ? methodOf(codeAddress) : MethodIndex.Invalid;
                });

            Assert.Equal(1, resolutionCount);
            Assert.Equal(mismatches, result.Diagnostics.AdjacencyMismatches);
            Assert.Equal(resolved && physicalMethod == 30,
                result.Frames[0].Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
        }

        [Fact]
        public void V2_AdjacencyMismatch_IsRecordedAsSoftDiagnostic()
        {
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            AsyncProfilerCallStack seg = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 400, 401 },
                frameMethods:   new[] {  70,  71 },
                completions: Completed(0)); // current = frames[0] (method 70)

            var sync = new[]
            {
                s.Sync(70, 999),                 // adjacent-above method 999 != frames[0] method 70 -> mismatch
                s.Boundary(71, Wrapper(0)),
                s.Sync(72, 99),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg });

            Assert.Equal(1, result.Diagnostics.AdjacencyMismatches);
        }

        [Fact]
        public void V1_InlineMatching_ReusesTheMethodAcrossPhysicalTransitionFrames()
        {
            var s = new Scenario();
            AsyncProfilerCallStack segment = s.Segment(
                AsyncCallstackKind.StateMachineAsync, new[] { 500, 501, 502 }, new[] { 30, 31, 32 });
            var sync = new[]
            {
                s.Sync(40, 99),
                s.Sync(41, 31),
                s.Sync(42, 98),
                s.Sync(43, 30),
                s.Sync(44, 97),
                s.Sync(45, 96),
                s.Boundary(46, V1Dispatcher),
                s.Sync(47, 95),
            };
            int firstMethodResolutions = 0;
            Func<CodeAddressIndex, MethodIndex> methodOf = s.MethodOf;
            AsyncProfilerStitchResult result = AsyncProfilerCallStackStitcher.Stitch(
                sync, new[] { segment }, Qpc, s.Classify, s.MethodCompletionObserved,
                codeAddress =>
                {
                    if (codeAddress == Scenario.CA(500))
                    {
                        firstMethodResolutions++;
                    }
                    return methodOf(codeAddress);
                });

            Assert.Equal(1, firstMethodResolutions);
            Assert.Equal(new[] { Scenario.CA(40), Scenario.CA(41), Scenario.CA(502), Scenario.CA(47) },
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCurrent, result.Frames[1].Origin);
            Assert.Equal(1, result.Frames[1].SegmentFrameIndex);
        }

        [Fact]
        public void V1_OrderedMatch_CountsCompletedInlineFrames()
        {
            // Inline-resume cascade. Sync (leaf-first): A(current), B, C, dispatcher, root.
            // Async (leaf-first): [C, B, A, P1]; C,B completed -> completed=2; current=A(frames[2]); ancestry=P1.
            var s = new Scenario();
            AsyncProfilerCallStack seg = s.Segment(AsyncCallstackKind.StateMachineAsync,
                frameCodeAddrs: new[] { 500, 501, 502, 503 },  // C, B, A, P1
                frameMethods:   new[] {  30,  31,  32,  33 });

            var sync = new[]
            {
                s.Sync(40, 32),                  // A (current, leaf) -> method 32
                s.Sync(41, 31),                  // B (completed)     -> method 31
                s.Sync(42, 30),                  // C (completed)     -> method 30
                s.Boundary(43, V1Dispatcher),    // MoveNextAsDispatcher
                s.Sync(44, 99),                  // root
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg });

            // Expected: [A(AsyncCurrent), P1(Async), root(Sync)]. Completed B/C and their physical
            // transition chain are replaced by the logical ancestry.
            Assert.Equal(3, result.Frames.Count);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCurrent, result.Frames[0].Origin);
            Assert.Equal(Scenario.CA(40), result.Frames[0].CodeAddress);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncRemaining, result.Frames[1].Origin);
            Assert.Equal(Scenario.CA(503), result.Frames[1].CodeAddress); // P1 ancestry
            Assert.Equal(Scenario.CA(44), result.Frames[2].CodeAddress);  // root
            Assert.Equal(1, result.Diagnostics.V1InlineFallbackUsed);
            Assert.Equal(0, result.Diagnostics.BoundariesNotFound);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void V1_LeafBoxCarrier_ExtendsOrderedAlignmentWithoutCountingOtherBoxFrames(int completed)
        {
            var s = new Scenario();
            AsyncProfilerCallStack segment = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 500, 501, 502, 503 }, new[] { 30, 31, 32, 33 });
            var sync = new List<AsyncProfilerStitchSyncFrame>
            {
                s.BoxCarrier(40, 30 + completed),
                s.BoxCarrier(41, 30 + completed),
            };
            for (int i = completed - 1; i >= 0; i--)
            {
                sync.Add(s.Sync(42 + i, 30 + i));
            }
            sync.Add(s.Boundary(46, V1Dispatcher));
            sync.Add(s.Sync(47, 99));

            AsyncProfilerStitchResult result = s.Run(sync, new[] { segment });
            AsyncProfilerStitchedFrame current = result.Frames[0];
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget, current.Origin);
            Assert.True(current.IsStateMachineBoxCarrier);
            Assert.Equal(Scenario.CA(40), current.CodeAddress);
            Assert.Equal(Scenario.MI(40), current.Method);
            Assert.Equal(Scenario.MI(30 + completed), current.BoxedStateMachineMethod);
            Assert.Equal(completed, current.SegmentFrameIndex);
            Assert.Same(segment, current.Context);
            Assert.True(current.WithPresentation(AsyncProfilerStitchedFramePresentation.Native).IsStateMachineBoxCarrier);
            Assert.Equal(5 - completed, result.Frames.Count);
            Assert.Equal(Scenario.CA(47), result.Frames[result.Frames.Count - 1].CodeAddress);
            Assert.Single(result.Frames.Where(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void V1_LeafBoxCarrier_RespectsMethodAndExceptionCompletionSources(bool methodEvents)
        {
            var s = new Scenario();
            if (methodEvents)
            {
                s.MarkMethodCompletionObserved(AsyncCallstackKind.StateMachineAsync);
            }
            AsyncProfilerCallStack segment = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 500, 501, 502, 503 }, new[] { 30, 31, 32, 33 },
                completions: methodEvents ? Completed(1) : null,
                exceptionCompletions: Unwound(1));
            AsyncProfilerStitchResult result = s.Run(
                new[] { s.BoxCarrier(40, 32), s.Sync(41, 31), s.Boundary(42, V1Dispatcher), s.Sync(43, 99) },
                new[] { segment });

            Assert.Equal(2, result.Frames[0].SegmentFrameIndex);
            Assert.True(result.Frames[0].IsStateMachineBoxCarrier);
            Assert.Equal(3, result.Frames.Count);
            Assert.Equal(Scenario.CA(503), result.Frames[1].CodeAddress);
            Assert.Equal(Scenario.CA(43), result.Frames[2].CodeAddress);
        }

        [Theory]
        [InlineData("no-boundary")]
        [InlineData("no-segment")]
        [InlineData("wrong-next")]
        [InlineData("unresolved-carrier")]
        [InlineData("unresolved-address")]
        [InlineData("unresolved-target")]
        public void V1_LeafBoxCarrier_GuardsPreserveExistingAlignment(string guard)
        {
            var s = new Scenario();
            AsyncProfilerCallStack segment = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 500, guard == "unresolved-target" ? -1 : 501, 502 }, new[] { 30, 31, 32 });
            var sync = new List<AsyncProfilerStitchSyncFrame>();
            if (guard == "non-leaf")
            {
                sync.Add(s.Sync(39, 99));
            }
            sync.Add(guard == "unresolved-address"
                ? new AsyncProfilerStitchSyncFrame(CodeAddressIndex.Invalid, Scenario.MI(40), AsyncProfilerStitchSyncFrameKind.None, Scenario.MI(31))
                : guard == "unresolved-carrier"
                ? new AsyncProfilerStitchSyncFrame(Scenario.CA(40), MethodIndex.Invalid, AsyncProfilerStitchSyncFrameKind.None, Scenario.MI(31))
                : s.BoxCarrier(40, guard == "wrong-next" ? 32 : 31));
            sync.Add(s.Sync(41, 30));
            if (guard != "no-boundary")
            {
                sync.Add(s.Boundary(42, V1Dispatcher));
            }
            sync.Add(s.Sync(43, 99));
            AsyncProfilerCallStack[] segments = guard == "no-segment" ? Array.Empty<AsyncProfilerCallStack>() : new[] { segment };

            AsyncProfilerStitchResult result = s.Run(sync, segments);
            AsyncProfilerStitchResult baseline = s.Run(sync.Select(frame =>
                new AsyncProfilerStitchSyncFrame(frame.CodeAddress, frame.Method, frame.Kind)).ToArray(), segments);
            Assert.Equal(
                baseline.Frames.Select(frame => (frame.Origin, frame.CodeAddress, frame.SegmentFrameIndex)),
                result.Frames.Select(frame => (frame.Origin, frame.CodeAddress, frame.SegmentFrameIndex)));
            Assert.DoesNotContain(result.Frames, frame => frame.IsStateMachineBoxCarrier);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void V1_BoxCarrierWithCallees_AdvancesResumptionWithoutClaimingApplicationExecution(
            bool truncated, bool methodEvents)
        {
            var s = new Scenario();
            if (methodEvents)
            {
                s.MarkMethodCompletionObserved(AsyncCallstackKind.StateMachineAsync);
            }
            AsyncProfilerCallStack segment = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 500, 501, 502, 503 }, new[] { 30, 31, 32, 33 },
                completions: methodEvents ? Completed(2) : null);
            var sync = new List<AsyncProfilerStitchSyncFrame>
            {
                s.Sync(40, 90),
                new AsyncProfilerStitchSyncFrame(Scenario.CA(41), MethodIndex.Invalid),
                s.BoxCarrier(42, 32),
                s.BoxCarrier(43, 32),
                s.Sync(44, 31),
                s.Sync(45, 30),
            };
            if (!truncated)
            {
                sync.Add(s.Boundary(46, V1Dispatcher));
            }
            sync.Add(s.Sync(47, 99));

            AsyncProfilerStitchResult result = s.Run(sync, new[] { segment });
            Assert.Equal(Scenario.CA(40), result.Frames[0].CodeAddress);
            Assert.Equal(Scenario.CA(41), result.Frames[1].CodeAddress);
            AsyncProfilerStitchedFrame target = result.Frames[2];
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget, target.Origin);
            Assert.True(target.IsStateMachineBoxCarrier);
            Assert.Equal(2, target.SegmentFrameIndex);
            Assert.Equal(Scenario.CA(42), target.CodeAddress);
            Assert.Equal(Scenario.MI(32), target.BoxedStateMachineMethod);
            Assert.Equal(Scenario.CA(503), result.Frames[3].CodeAddress);
            Assert.Equal(truncated ? AsyncProfilerStitchedFrameOrigin.IncompleteRoot : AsyncProfilerStitchedFrameOrigin.Sync,
                result.Frames[4].Origin);
            Assert.Equal(truncated ? 1 : 0, result.Diagnostics.IncompleteAsyncRootsNormalized);
            Assert.DoesNotContain(result.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
        }

        [Fact]
        public void V1_ApplicationBodyAboveBoxCarrier_TakesPrecedence()
        {
            var s = new Scenario();
            AsyncProfilerCallStack segment = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 500, 501, 502 }, new[] { 30, 31, 32 });
            AsyncProfilerStitchResult result = s.Run(new[]
            {
                s.Sync(40, 31),
                s.BoxCarrier(41, 31),
                s.BoxCarrier(42, 32),
                s.Sync(43, 30),
                s.Boundary(44, V1Dispatcher),
                s.Sync(45, 99),
            }, new[] { segment });

            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCurrent, result.Frames[0].Origin);
            Assert.Equal(Scenario.CA(40), result.Frames[0].CodeAddress);
            Assert.Equal(1, result.Frames[0].SegmentFrameIndex);
            Assert.False(result.Frames[0].IsStateMachineBoxCarrier);
        }

        [Fact]
        public void V1_CollapsesContiguousBoxInfrastructureRootwardOfDispatcher()
        {
            var s = new Scenario();
            AsyncProfilerCallStack seg = s.Segment(AsyncCallstackKind.StateMachineAsync,
                frameCodeAddrs: new[] { 500, 501 }, frameMethods: new[] { 30, 31 });

            var sync = new[]
            {
                s.Sync(40, 30),
                s.Boundary(41, V1Dispatcher),
                s.Boundary(42, V1Infrastructure),
                s.Boundary(43, V1Infrastructure),
                s.Sync(44, 90), // first non-box frame: preserve and stop collapsing
                s.Sync(45, 91),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg });

            Assert.Equal(new[] { Scenario.CA(40), Scenario.CA(501), Scenario.CA(44), Scenario.CA(45) },
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCurrent, result.Frames[0].Origin);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncRemaining, result.Frames[1].Origin);
            Assert.Equal(2, result.Diagnostics.V1InfrastructureFramesCollapsed);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void V1_UnmatchedCurrent_RetainsDispatcherAndIndexedDispatchTarget(bool prefix, bool unresolved)
        {
            var s = new Scenario();
            AsyncProfilerCallStack segment = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { unresolved ? -1 : 500, 501 }, new[] { 30, 31 }, frameMethodIds: new ulong[] { 123, 124 });
            var sync = new List<AsyncProfilerStitchSyncFrame>();
            if (prefix)
            {
                sync.Add(s.Sync(39, 99));
            }
            sync.Add(s.Boundary(40, V1Dispatcher));
            sync.Add(s.Boundary(41, V1Infrastructure));
            sync.Add(s.Sync(42, 90));

            AsyncProfilerStitchResult result = s.Run(sync, new[] { segment });
            int dispatcher = prefix ? 1 : 0;
            Assert.Equal(sync[dispatcher].CodeAddress, result.Frames[dispatcher].CodeAddress);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, result.Frames[dispatcher].Origin);
            AsyncProfilerStitchedFrame target = result.Frames[dispatcher + 1];
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget, target.Origin);
            Assert.Equal(AsyncProfilerStitchedFrameContextKind.Active, target.ContextKind);
            Assert.Equal(0, target.SegmentFrameIndex);
            Assert.Equal(segment.Frames.CodeAddressAt(0), target.CodeAddress);
            Assert.Equal(MethodIndex.Invalid, target.Method);
            Assert.Same(segment, target.Context);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncRemaining, result.Frames[dispatcher + 2].Origin);
            Assert.Equal(Scenario.CA(501), result.Frames[dispatcher + 2].CodeAddress);
            Assert.Equal(Scenario.CA(42), result.Frames[dispatcher + 3].CodeAddress);
            Assert.Equal(dispatcher + 4, result.Frames.Count);
            Assert.Equal(1, result.Diagnostics.V1InfrastructureFramesCollapsed);
            Assert.DoesNotContain(result.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
        }

        [Fact]
        public void V1_DispatcherLeaf_SingleFrameSegmentIsNotLost()
        {
            var s = new Scenario();
            AsyncProfilerCallStack segment = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 500 }, new[] { 30 });
            AsyncProfilerStitchResult result = s.Run(
                new[] { s.Boundary(40, V1Dispatcher), s.Sync(41, 90) }, new[] { segment });

            Assert.Equal(new[] { Scenario.CA(40), Scenario.CA(500), Scenario.CA(41) },
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget, result.Frames[1].Origin);
        }

        [Fact]
        public void V1_DispatchTargetDoesNotReintroduceCompletedFrames()
        {
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.StateMachineAsync);
            AsyncProfilerCallStack segment = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 500, 501, 502 }, new[] { 30, 31, 32 }, completions: Completed(1));
            AsyncProfilerStitchResult result = s.Run(
                new[] { s.Boundary(40, V1Dispatcher), s.Sync(41, 90) }, new[] { segment });

            Assert.Equal(new[] { Scenario.CA(40), Scenario.CA(501), Scenario.CA(502), Scenario.CA(41) },
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget, result.Frames[1].Origin);
            Assert.Equal(1, result.Frames[1].SegmentFrameIndex);
        }

        [Fact]
        public void V1_UnmatchedCurrentWithoutBoundary_DoesNotInventADispatchTarget()
        {
            var s = new Scenario();
            AsyncProfilerCallStack segment = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 500, 501 }, new[] { 30, 31 });
            AsyncProfilerStitchResult result = s.Run(new[] { s.Sync(40, 90) }, new[] { segment });

            Assert.Equal(new[] { Scenario.CA(40), Scenario.CA(501) },
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.DoesNotContain(result.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget);
            Assert.Equal(1, result.Diagnostics.BoundariesNotFound);
        }

        [Fact]
        public void V1_MatchedMoveNext_DoesNotInsertADispatchTarget()
        {
            var s = new Scenario();
            AsyncProfilerCallStack segment = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 500, 501 }, new[] { 30, 31 });
            AsyncProfilerStitchResult result = s.Run(
                new[] { s.Sync(39, 30), s.Boundary(40, V1Dispatcher), s.Sync(41, 90) }, new[] { segment });

            Assert.Equal(new[] { Scenario.CA(39), Scenario.CA(501), Scenario.CA(41) },
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCurrent, result.Frames[0].Origin);
            Assert.DoesNotContain(result.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget);
        }

        [Fact]
        public void V1_StructuralStitchPreservesSynchronousStartupPattern()
        {
            // Leaf-first physical suffix:
            //   CPU work <- generated child MoveNext <- CoreLib builder Start <- logical child kickoff <- boundary.
            // Presentation normalization is a later pipeline stage, so the structural stitcher preserves this
            // synchronous sequence while still stitching the active V1 segment around it.
            var s = new Scenario();
            AsyncProfilerCallStack seg = s.Segment(
                AsyncCallstackKind.StateMachineAsync,
                frameCodeAddrs: new[] { 500, 501 },
                frameMethods: new[] { 30, 31 });

            var sync = new[]
            {
                s.Sync(40, 70), // CPU work
                s.Sync(41, 71, AsyncProfilerStitchSyncFrameKind.V1StateMachineMoveNext),
                s.Sync(42, 72, AsyncProfilerStitchSyncFrameKind.V1MethodBuilderStart),
                s.Sync(43, 73, AsyncProfilerStitchSyncFrameKind.V1MethodBuilderStart),
                s.Sync(44, 30), // child kickoff/current async method
                s.Boundary(45, V1Dispatcher),
                s.Sync(46, 99),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg });

            Assert.Equal(
                new[]
                {
                    Scenario.CA(40), Scenario.CA(41), Scenario.CA(42), Scenario.CA(43),
                    Scenario.CA(44), Scenario.CA(501), Scenario.CA(46),
                },
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(0, result.Diagnostics.V1SynchronousStartupFramesCollapsed);
        }

        [Fact]
        public void V1_IncompleteSynchronousStartupPattern_IsPreserved()
        {
            var s = new Scenario();
            AsyncProfilerCallStack seg = s.Segment(
                AsyncCallstackKind.StateMachineAsync,
                frameCodeAddrs: new[] { 500, 501 },
                frameMethods: new[] { 30, 31 });

            var sync = new[]
            {
                s.Sync(40, 70),
                s.Sync(41, 71, AsyncProfilerStitchSyncFrameKind.V1StateMachineMoveNext),
                s.Sync(42, 72), // unknown frame interrupts the required contiguous pattern
                s.Sync(43, 30),
                s.Boundary(44, V1Dispatcher),
                s.Sync(45, 99),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg });

            Assert.Contains(result.Frames, frame => frame.CodeAddress == Scenario.CA(41));
            Assert.Equal(0, result.Diagnostics.V1SynchronousStartupFramesCollapsed);
        }

        [Fact]
        public void V1_ZeroIpCurrentFrame_ResolvesFromSyncLeaf_NotSpliced()
        {
            // Regression: the current (running) V1 frame has a 0-IP on the async side. Its identity must come
            // from the physical sync leaf, and it must NOT be spliced back in as an Invalid async frame.
            var s = new Scenario();
            AsyncProfilerCallStack seg = s.Segment(AsyncCallstackKind.StateMachineAsync,
                frameCodeAddrs: new[] { 500, 501, -1, 503 },   // C, B, A(0-IP), P1
                frameMethods:   new[] {  30,  31,  0,  33 });   // A's method irrelevant (Invalid ca)

            var sync = new[]
            {
                s.Sync(40, 32),                  // A (current, leaf) -> REAL sync IP + method
                s.Sync(41, 31),                  // B (completed)
                s.Sync(42, 30),                  // C (completed)
                s.Boundary(43, V1Dispatcher),
                s.Sync(44, 99),                  // root
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg });

            // completed must still be 2 (C,B); current A resolved from sync, ancestry P1 spliced.
            Assert.Equal(5, result.Frames.Count);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, result.Frames[0].Origin);
            Assert.Equal(Scenario.CA(40), result.Frames[0].CodeAddress);        // real sync leaf, not Invalid
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncRemaining, result.Frames[3].Origin);
            Assert.Equal(Scenario.CA(503), result.Frames[3].CodeAddress);       // P1
            // No spliced async frame is the Invalid 0-IP current frame.
            Assert.DoesNotContain(result.Frames.Where(f => f.Origin == AsyncProfilerStitchedFrameOrigin.AsyncRemaining),
                f => f.CodeAddress == CodeAddressIndex.Invalid);
        }

        [Fact]
        public void V1_MethodObserved_UsesEventCount_NotStackScan()
        {
            // CompleteMethod events observed for V1: the normal completed count comes from the method deltas
            // (GetMethodCompletedFrameCount), NOT the inline sync-stack scan. Segment (leaf-first): [Mcur, A1, A2];
            // one method completion -> completed=1 -> current=frames[1]; ancestry=frames[2].
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.StateMachineAsync);
            AsyncProfilerCallStack seg = s.Segment(AsyncCallstackKind.StateMachineAsync,
                frameCodeAddrs: new[] { 500, 501, 502 },   // Mcur, A1, A2
                frameMethods:   new[] {  30,  31,  32 },
                completions: Completed(1));                 // one normal completion via events

            var sync = new[]
            {
                s.Sync(40, 31),                 // current -> frames[1] (method 31)
                s.Boundary(43, V1Dispatcher),
                s.Sync(44, 99),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg });

            // Expected: [current(Sync), A2(Async), root(Sync)]. The stack scan is not used (no V1InlineFallbackUsed).
            Assert.Equal(3, result.Frames.Count);
            Assert.Equal(Scenario.CA(40), result.Frames[0].CodeAddress);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncRemaining, result.Frames[1].Origin);
            Assert.Equal(Scenario.CA(502), result.Frames[1].CodeAddress); // A2 ancestry
            Assert.Equal(Scenario.CA(44), result.Frames[2].CodeAddress);
            Assert.Equal(0, result.Diagnostics.V1InlineFallbackUsed);
        }

        [Theory]
        [InlineData(false, 0, 2)]
        [InlineData(true, 0, 2)]
        [InlineData(true, 2, 0)]
        [InlineData(true, 1, 1)]
        public void V1_CompletionTail_PreservesFinishingMethodAndSampledPrefix(
            bool methodEvents, int normal, int exceptional)
        {
            var s = new Scenario();
            if (methodEvents)
            {
                s.MarkMethodCompletionObserved(AsyncCallstackKind.StateMachineAsync);
            }
            AsyncProfilerCallStack segment = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 500, 501, 502, 503, 504 }, new[] { 30, 31, 32, 33, 34 },
                completions: Completed(normal), exceptionCompletions: Unwound(exceptional));
            var sync = new[]
            {
                s.Sync(40, 90), s.Sync(41, 31), s.Sync(42, 91), s.Sync(43, 30),
                s.Boundary(44, V1Dispatcher), s.Sync(45, 99),
            };
            AsyncProfilerStitchResult result = s.Run(sync, new[] { segment });

            Assert.Equal(new[] { Scenario.CA(40), Scenario.CA(41), Scenario.CA(502), Scenario.CA(503),
                Scenario.CA(504), Scenario.CA(45) }, result.Frames.Select(frame => frame.CodeAddress));
            AsyncProfilerStitchedFrame finishing = result.Frames[1];
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCompleting, finishing.Origin);
            Assert.Equal(1, finishing.SegmentFrameIndex);
            Assert.Equal(Scenario.MI(31), finishing.Method);
            Assert.Same(segment, finishing.Context);
            Assert.Equal(AsyncProfilerStitchedFramePresentation.LogicalStateMachineMethod, finishing.Presentation);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget, result.Frames[2].Origin);
            Assert.Equal(1, result.Diagnostics.V1CompletionTailUsed);
            Assert.Equal(0, result.Diagnostics.V1CompletionAlignmentAmbiguous);
            Assert.Equal(methodEvents ? 0 : 1, result.Diagnostics.V1InlineFallbackUsed);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void V1_CompletionTail_LastMethodCanStillBePhysicallyFinishing(bool methodEvents)
        {
            var s = new Scenario();
            if (methodEvents)
            {
                s.MarkMethodCompletionObserved(AsyncCallstackKind.StateMachineAsync);
            }
            AsyncProfilerCallStack segment = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 500 }, new[] { 30 },
                completions: methodEvents ? Completed(1) : null,
                exceptionCompletions: methodEvents ? null : Unwound(1));
            AsyncProfilerStitchResult result = s.Run(
                new[] { s.Sync(40, 90), s.Sync(41, 30), s.Boundary(42, V1Dispatcher), s.Sync(43, 99) },
                new[] { segment });

            Assert.Equal(new[] { Scenario.CA(40), Scenario.CA(41), Scenario.CA(43) },
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCompleting, result.Frames[1].Origin);
            Assert.DoesNotContain(result.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void V1_CompletionTail_NextPhysicalMethodIsRunning(bool methodEvents)
        {
            var s = new Scenario();
            if (methodEvents)
            {
                s.MarkMethodCompletionObserved(AsyncCallstackKind.StateMachineAsync);
            }
            AsyncProfilerCallStack segment = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 500, 501, 502, 503 }, new[] { 30, 31, 32, 33 },
                completions: methodEvents ? Completed(2) : null,
                exceptionCompletions: methodEvents ? null : Unwound(2));
            AsyncProfilerStitchResult result = s.Run(
                new[] { s.Sync(40, 90), s.Sync(41, 32), s.Boundary(42, V1Dispatcher), s.Sync(43, 99) },
                new[] { segment });

            Assert.Equal(new[] { Scenario.CA(40), Scenario.CA(41), Scenario.CA(503), Scenario.CA(43) },
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCurrent, result.Frames[1].Origin);
            Assert.Equal(0, result.Diagnostics.V1CompletionTailUsed);
        }

        [Theory]
        [InlineData("no-boundary")]
        [InlineData("repeated")]
        [InlineData("out-of-order")]
        [InlineData("unresolved")]
        [InlineData("no-records")]
        [InlineData("future-records")]
        public void V1_CompletionTail_UnverifiedLayoutsDoNotAcquireCompletingStatus(string guard)
        {
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.StateMachineAsync);
            AsyncProfilerCallStack segment = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 500, guard == "unresolved" ? -1 : 501, 502, 503 },
                new[] { guard == "repeated" ? 31 : 30, 31, 32, 33 },
                completions: guard == "no-records" ? null :
                    new[] { new AsyncProfilerCallStack.CompletionDelta(guard == "future-records" ? Qpc + 1 : Qpc, 2) });
            var sync = new List<AsyncProfilerStitchSyncFrame>
            {
                s.Sync(40, 90), s.Sync(41, guard == "out-of-order" ? 30 : 31),
                s.Sync(42, guard == "out-of-order" ? 31 : 30),
            };
            if (guard != "no-boundary")
            {
                sync.Add(s.Boundary(43, V1Dispatcher));
            }
            sync.Add(s.Sync(44, 99));
            AsyncProfilerStitchResult result = s.Run(sync, new[] { segment });

            Assert.DoesNotContain(result.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompleting);
            Assert.Equal(0, result.Diagnostics.V1CompletionTailUsed);
        }

        [Fact]
        public void V1_CompletionTail_PartialRecordsRetainPhysicalCurrentWithoutDoubleCounting()
        {
            var s = new Scenario();
            AsyncProfilerCallStack segment = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 500, 501, 502, 503, 504 }, new[] { 30, 31, 32, 33, 34 },
                exceptionCompletions: Unwound(1));
            var sync = new[]
            {
                s.Sync(40, 90), s.Sync(41, 32), s.Sync(42, 31), s.Sync(43, 30),
                s.Boundary(44, V1Dispatcher), s.Sync(45, 99),
            };
            AsyncProfilerStitchResult result = s.Run(sync, new[] { segment }, trace: true);

            Assert.Equal(new[] { Scenario.CA(40), Scenario.CA(41), Scenario.CA(503), Scenario.CA(504), Scenario.CA(45) },
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCurrent, result.Frames[1].Origin);
            Assert.Equal(2, result.Frames[1].SegmentFrameIndex);
            Assert.DoesNotContain(result.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompleting);
            Assert.Equal(0, result.Diagnostics.V1CompletionAlignmentAmbiguous);
        }

        [Fact]
        public void V1_CompletionTail_RecordsBeyondPhysicalMatchesDoNotInventFinishingAnchor()
        {
            var s = new Scenario();
            AsyncProfilerCallStack segment = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 500, 501, 502, 503, 504 }, new[] { 30, 31, 32, 33, 34 },
                exceptionCompletions: Unwound(3));
            var sync = new[]
            {
                s.Sync(40, 90), s.Sync(41, 31), s.Sync(42, 30),
                s.Boundary(43, V1Dispatcher), s.Sync(44, 99),
            };
            AsyncProfilerStitchResult result = s.Run(sync, new[] { segment });

            Assert.Equal(sync.Take(4).Select(frame => frame.CodeAddress),
                result.Frames.Take(4).Select(frame => frame.CodeAddress));
            Assert.DoesNotContain(result.Frames, frame =>
                frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompleting || frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
            Assert.Equal(1, result.Diagnostics.V1CompletionAlignmentAmbiguous);
            Assert.Equal(3, result.Frames[4].SegmentFrameIndex);
        }

        [Fact]
        public void V1_CompletionTail_UnwindAfterSampleDoesNotRetrospectivelyMarkFrameCompleted()
        {
            var s = new Scenario();
            AsyncProfilerCallStack segment = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 500, 501, 502 }, new[] { 30, 31, 32 },
                exceptionCompletions: new[]
                {
                    new AsyncProfilerCallStack.CompletionDelta(Qpc - 1, 1),
                    new AsyncProfilerCallStack.CompletionDelta(Qpc + 1, 1),
                });
            var sync = new[]
            {
                s.Sync(40, 90), s.Sync(41, 31), s.Sync(42, 91), s.Sync(43, 30),
                s.Boundary(44, V1Dispatcher), s.Sync(45, 99),
            };
            AsyncProfilerStitchResult atSample = s.Run(sync, new[] { segment });
            AsyncProfilerStitchResult afterUnwind = AsyncProfilerCallStackStitcher.Stitch(
                sync, new[] { segment }, Qpc + 1, s.Classify, s.MethodCompletionObserved, s.MethodOf);

            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCurrent, atSample.Frames[1].Origin);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCompleting, afterUnwind.Frames[1].Origin);
            Assert.Equal(Scenario.CA(41), atSample.Frames[1].CodeAddress);
            Assert.Equal(Scenario.CA(41), afterUnwind.Frames[1].CodeAddress);
            Assert.Equal(1, atSample.Frames[1].SegmentFrameIndex);
            Assert.Equal(1, afterUnwind.Frames[1].SegmentFrameIndex);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncRemaining, atSample.Frames[2].Origin);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget, afterUnwind.Frames[2].Origin);
            Assert.Equal(2, afterUnwind.Frames[2].SegmentFrameIndex);
        }

        [Fact]
        public void V1_CompletionTail_RecursiveCompletedCopyIsNotMistakenForRunningParent()
        {
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.StateMachineAsync);
            AsyncProfilerCallStack segment = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 500, 501, 502 }, new[] { 30, 30, 31 }, completions: Completed(1));
            AsyncProfilerStitchResult result = s.Run(
                new[] { s.Sync(40, 90), s.Sync(41, 30), s.Boundary(42, V1Dispatcher), s.Sync(43, 99) },
                new[] { segment });

            Assert.DoesNotContain(result.Frames, frame =>
                frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent || frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompleting);
            Assert.Equal(Scenario.CA(41), result.Frames[1].CodeAddress);
            Assert.Equal(1, result.Diagnostics.V1CompletionAlignmentAmbiguous);
        }

        [Fact]
        public void V1_CompletionTail_MissingExceptionalChildStillAnchorsFinishingParent()
        {
            var s = new Scenario();
            AsyncProfilerCallStack segment = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 500, 501, 502, 503 }, new[] { 30, 31, 32, 33 },
                exceptionCompletions: Unwound(2));
            AsyncProfilerStitchResult result = s.Run(
                new[] { s.Sync(40, 90), s.Sync(41, 31), s.Boundary(42, V1Dispatcher), s.Sync(43, 99) },
                new[] { segment });

            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCompleting, result.Frames[1].Origin);
            Assert.Equal(1, result.Frames[1].SegmentFrameIndex);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget, result.Frames[2].Origin);
            Assert.Equal(2, result.Frames[2].SegmentFrameIndex);
        }

        [Fact]
        public void V1_MissingBoundary_UniqueTruncatedSuffix_RecoversCompletedCount()
        {
            // The root side of the CPU sample was truncated before the dispatcher and the first two async frames.
            // The retained physical sequence C,D,E uniquely aligns with segment[2..4], so E is current and four
            // leaf frames have completed.
            var s = new Scenario();
            AsyncProfilerCallStack seg = s.Segment(AsyncCallstackKind.StateMachineAsync,
                frameCodeAddrs: new[] { 500, 501, 502, 503, 504 },
                frameMethods: new[] { 30, 31, 32, 33, 34 });

            var sync = new[]
            {
                s.Sync(40, 70), // CPU work
                s.Sync(41, 34), // E current
                s.Sync(42, 90), // transition plumbing
                s.Sync(43, 33), // D completed
                s.Sync(44, 91), // transition plumbing
                s.Sync(45, 32), // C completed; A/B and dispatcher were truncated
                s.Sync(46, 99), // retained root-side frame
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg }, trace: true);

            Assert.Equal(
                new[] { Scenario.CA(40), Scenario.CA(41), CodeAddressIndex.Invalid },
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCurrent, result.Frames[1].Origin);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.IncompleteRoot, result.Frames[2].Origin);
            Assert.Null(result.Frames[2].Context);
            Assert.Equal(1, result.Diagnostics.BoundariesNotFound);
            Assert.Equal(1, result.Diagnostics.V1InlineFallbackUsed);
            Assert.Equal(1, result.Diagnostics.V1TruncatedSuffixFallbackUsed);
            Assert.Equal(1, result.Diagnostics.IncompleteAsyncRootsNormalized);
            Assert.Contains(result.Diagnostics.Messages, message => message.Contains("completed=4"));
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, 0)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, 1)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, 3)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, 0)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, 1)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, 3)]
        public void IncompleteRoot_DifferentCutDepthsHaveTheSameStructuralRoot(
            int kindValue, int fragmentLength)
        {
            var kind = (AsyncCallstackKind)kindValue;
            var s = new Scenario();
            AsyncProfilerCallStack seg = s.Segment(kind,
                frameCodeAddrs: new[] { 500, 501, 502, 503, 504, 505, 506 },
                frameMethods: new[] { 30, 31, 32, 33, 34, 35, 36 });
            var sync = new List<AsyncProfilerStitchSyncFrame>
            {
                s.Sync(40, 70),
                s.Sync(41, 34),
                s.Sync(42, 90),
                s.Sync(43, 33),
                s.Sync(44, 91),
                s.Sync(45, 32),
            };
            for (int i = 0; i < fragmentLength; i++)
            {
                // Opaque resolved frames: normalization must not depend on helper names or classifications.
                sync.Add(s.Sync(90 + i, 100 + i));
            }

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg });

            Assert.Equal(
                new[] { Scenario.CA(40), Scenario.CA(41), Scenario.CA(505), Scenario.CA(506), CodeAddressIndex.Invalid },
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCurrent, result.Frames[1].Origin);
            Assert.Equal(4, result.Frames[1].SegmentFrameIndex);
            Assert.Equal(Scenario.MI(34), result.Frames[1].Method);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.IncompleteRoot, result.Frames[4].Origin);
            Assert.Equal(AsyncProfilerStitchedFrameContextKind.None, result.Frames[4].ContextKind);
            Assert.Equal(1, result.Diagnostics.IncompleteAsyncRootsNormalized);
            Assert.Equal(kind == AsyncCallstackKind.RuntimeAsync ? 1 : 0,
                result.Diagnostics.V2TruncatedSuffixFallbackUsed);
            Assert.Equal(0, result.Diagnostics.V2WrapperFallbackUsed);
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, 4, true)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, 2, false)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, 7, false)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, 4, true)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, 2, false)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, 7, false)]
        public void IncompleteRoot_CompletionRecordsMustAgreeWithPhysicalAlignment(
            int kindValue, int completed, bool normalize)
        {
            var kind = (AsyncCallstackKind)kindValue;
            var s = new Scenario();
            s.MarkMethodCompletionObserved(kind);
            AsyncProfilerCallStack seg = s.Segment(kind,
                frameCodeAddrs: new[] { 500, 501, 502, 503, 504, 505, 506 },
                frameMethods: new[] { 30, 31, 32, 33, 34, 35, 36 },
                completions: Completed(completed));
            var sync = new[]
            {
                s.Sync(40, 70),
                s.Sync(41, 34),
                s.Sync(42, 33),
                s.Sync(43, 32),
                s.Sync(44, 99),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg });

            Assert.Equal(normalize ? 1 : 0, result.Diagnostics.IncompleteAsyncRootsNormalized);
            if (normalize)
            {
                Assert.Equal(AsyncProfilerStitchedFrameOrigin.IncompleteRoot, result.Frames[result.Frames.Count - 1].Origin);
            }
            else
            {
                Assert.DoesNotContain(result.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.IncompleteRoot);
                Assert.Contains(result.Frames,
                    frame => frame.CodeAddress == Scenario.CA(44) && frame.Origin == AsyncProfilerStitchedFrameOrigin.Sync);
            }
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync)]
        public void IncompleteRoot_UnresolvedCallerCouldConcealABoundary(int kindValue)
        {
            var kind = (AsyncCallstackKind)kindValue;
            var s = new Scenario();
            AsyncProfilerCallStack seg = s.Segment(kind,
                frameCodeAddrs: new[] { 500, 501, 502, 503, 504 },
                frameMethods: new[] { 30, 31, 32, 33, 34 });
            var sync = new[]
            {
                s.Sync(40, 70),
                s.Sync(41, 34),
                s.Sync(42, 33),
                s.Sync(43, 32),
                new AsyncProfilerStitchSyncFrame(Scenario.CA(44), MethodIndex.Invalid),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg });

            Assert.Equal(kind == AsyncCallstackKind.StateMachineAsync ? 1 : 0,
                result.Diagnostics.V1TruncatedSuffixFallbackUsed);
            Assert.Equal(0, result.Diagnostics.IncompleteAsyncRootsNormalized);
            Assert.Contains(result.Frames, frame => frame.CodeAddress == Scenario.CA(44));
            Assert.Equal(sync.Select(frame => frame.CodeAddress),
                result.Frames.Take(sync.Length).Select(frame => frame.CodeAddress));
            Assert.DoesNotContain(result.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.IncompleteRoot);
        }

        [Theory]
        [InlineData((int)AsyncProfilerCallStackStitchBoundaryKind.V2ContinuationWrapper)]
        [InlineData((int)AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation)]
        public void V1_IncompleteRoot_IntactForeignBoundaryAndHostArePreserved(int kindValue)
        {
            var kind = (AsyncProfilerCallStackStitchBoundaryKind)kindValue;
            var s = new Scenario();
            AsyncProfilerCallStack seg = s.Segment(AsyncCallstackKind.StateMachineAsync,
                frameCodeAddrs: new[] { 500, 501, 502, 503, 504 },
                frameMethods: new[] { 30, 31, 32, 33, 34 });
            var sync = new[]
            {
                s.Sync(40, 70),
                s.Sync(41, 34),
                s.Sync(42, 33),
                s.Sync(43, 32),
                s.Boundary(44, new AsyncProfilerCallStackStitchBoundaryInfo(kind, 0)),
                s.Sync(45, 99),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg });

            Assert.Equal(0, result.Diagnostics.IncompleteAsyncRootsNormalized);
            Assert.Equal(
                new[] { Scenario.CA(44), Scenario.CA(45) },
                result.Frames.Skip(result.Frames.Count - 2).Select(frame => frame.CodeAddress));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(3)]
        public void V1_SurvivingDispatcher_MarksCallerTailCutInsideRuntimePlumbing(int plumbingLength)
        {
            var s = new Scenario();
            AsyncProfilerCallStack segment = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 500, 501 }, new[] { 30, 31 });
            var sync = new List<AsyncProfilerStitchSyncFrame>
            {
                s.Sync(40, 70),
                s.Sync(41, 30),
                s.Boundary(42, V1Dispatcher),
            };
            sync.AddRange(Enumerable.Range(0, plumbingLength).Select(i => s.Boundary(43 + i, V1Infrastructure)));

            AsyncProfilerStitchResult result = s.Run(sync, new[] { segment });

            Assert.Equal(new[] { Scenario.CA(40), Scenario.CA(41), Scenario.CA(501), CodeAddressIndex.Invalid },
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.IncompleteRoot, result.Frames.Last().Origin);
            Assert.Null(result.Frames.Last().Context);
            Assert.Equal(0, result.Diagnostics.BoundariesNotFound);
            Assert.Equal(plumbingLength, result.Diagnostics.V1InfrastructureFramesCollapsed);
            Assert.Equal(1, result.Diagnostics.IncompleteAsyncRootsNormalized);
        }

        [Theory]
        [InlineData("unknown")]
        [InlineData("foreign-profile")]
        [InlineData("foreign-boundary")]
        public void V1_SurvivingDispatcher_PreservesUnverifiedCallerTail(string obstruction)
        {
            var s = new Scenario();
            var profile = new StructuralRulesProfile(0);
            AsyncProfilerCallStack segment = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 500, 501 }, new[] { 30, 31 });
            AsyncProfilerStitchSyncFrame tail = obstruction == "unknown" ? s.Sync(43, 90) :
                s.Boundary(43, obstruction == "foreign-boundary" ? Wrapper(0) : V1Infrastructure);
            var sync = new[] { s.Sync(40, 30), s.Boundary(42, V1Dispatcher), tail };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { segment }, profile,
                ca => obstruction == "foreign-profile" && ca == Scenario.CA(43)
                    ? AsyncProfilerRuntimeRuleProfile.Common : profile);

            Assert.Equal(Scenario.CA(43), result.Frames.Last().CodeAddress);
            Assert.DoesNotContain(result.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.IncompleteRoot);
        }

        [Fact]
        public void V1_SurvivingDispatcher_UnresolvedBoundaryDoesNotProveAnIncompleteRoot()
        {
            var s = new Scenario();
            AsyncProfilerCallStack segment = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 500, 501 }, new[] { 30, 31 });
            s.Boundary(42, V1Dispatcher);
            var sync = new[]
            {
                s.Sync(40, 30),
                new AsyncProfilerStitchSyncFrame(Scenario.CA(42), MethodIndex.Invalid),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { segment });

            Assert.DoesNotContain(result.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.IncompleteRoot);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        public void V1_SurvivingDispatcher_NestedContextsHaveOneOutermostIncompleteRoot(int plumbingLength)
        {
            var s = new Scenario();
            AsyncProfilerCallStack outer = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 500, 501 }, new[] { 30, 31 });
            AsyncProfilerCallStack inner = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 600, 601 }, new[] { 40, 41 });
            var sync = new List<AsyncProfilerStitchSyncFrame>
            {
                s.Sync(40, 40),
                s.Boundary(41, V1Dispatcher),
                s.Boundary(42, V1Infrastructure),
                s.Sync(43, 70),
                s.Sync(44, 30),
                s.Boundary(45, V1Dispatcher),
            };
            sync.AddRange(Enumerable.Range(0, plumbingLength).Select(i => s.Boundary(46 + i, V1Infrastructure)));

            AsyncProfilerStitchResult result = s.Run(sync, new[] { outer, inner });

            Assert.Equal(new[]
                {
                    Scenario.CA(40), Scenario.CA(601), Scenario.CA(43),
                    Scenario.CA(44), Scenario.CA(501), CodeAddressIndex.Invalid,
                }, result.Frames.Select(frame => frame.CodeAddress));
            Assert.Single(result.Frames.Where(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.IncompleteRoot));
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.IncompleteRoot, result.Frames.Last().Origin);
            Assert.Equal(2, result.Diagnostics.SegmentsProcessed);
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, false)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, true)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, false)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, true)]
        public void CompleteBoundary_PreservesArbitraryManagedAndNativeHosts(
            int kindValue, bool unresolvedHost)
        {
            var kind = (AsyncCallstackKind)kindValue;
            var s = new Scenario();
            AsyncProfilerCallStack seg = s.Segment(kind,
                frameCodeAddrs: new[] { 500, 501 },
                frameMethods: new[] { 30, 31 });
            var sync = new[]
            {
                s.Sync(40, 70),
                s.Sync(41, 30),
                s.Boundary(42, kind == AsyncCallstackKind.StateMachineAsync ? V1Dispatcher : Wrapper(0)),
                s.Sync(43, 99),
                new AsyncProfilerStitchSyncFrame(Scenario.CA(44), unresolvedHost ? MethodIndex.Invalid : Scenario.MI(100)),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg });

            Assert.Equal(
                new[] { Scenario.CA(40), Scenario.CA(41), Scenario.CA(501), Scenario.CA(43), Scenario.CA(44) },
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(0, result.Diagnostics.IncompleteAsyncRootsNormalized);
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, (int)AsyncCallstackKind.RuntimeAsync)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, (int)AsyncCallstackKind.StateMachineAsync)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, (int)AsyncCallstackKind.RuntimeAsync)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, (int)AsyncCallstackKind.StateMachineAsync)]
        public void NestedIncompleteRoot_IsOnlyAttachedToTheOutermostActivation(
            int outerKindValue, int innerKindValue)
        {
            var outerKind = (AsyncCallstackKind)outerKindValue;
            var innerKind = (AsyncCallstackKind)innerKindValue;
            var s = new Scenario();
            AsyncProfilerCallStack outer = s.Segment(outerKind,
                frameCodeAddrs: new[] { 500, 501, 502, 503, 504, 505, 506 },
                frameMethods: new[] { 30, 31, 32, 33, 34, 35, 36 });
            AsyncProfilerCallStack inner = s.Segment(innerKind,
                frameCodeAddrs: new[] { 200, 201 },
                frameMethods: new[] { 50, 51 });
            var sync = new[]
            {
                s.Sync(40, 70),
                s.Sync(41, 50),
                s.Boundary(42, innerKind == AsyncCallstackKind.RuntimeAsync ? Wrapper(0) : V1Dispatcher),
                s.Sync(43, 90),
                s.Sync(44, 34),
                s.Sync(45, 33),
                s.Sync(46, 32),
                s.Sync(47, 99),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { outer, inner });

            Assert.Equal(
                new[]
                {
                    Scenario.CA(40), Scenario.CA(41), Scenario.CA(201), Scenario.CA(43),
                    Scenario.CA(44), Scenario.CA(505), Scenario.CA(506), CodeAddressIndex.Invalid,
                },
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(2, result.Diagnostics.SegmentsProcessed);
            Assert.Equal(1, result.Diagnostics.IncompleteAsyncRootsNormalized);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.IncompleteRoot, result.Frames[result.Frames.Count - 1].Origin);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void V2_IncompleteRoot_CannotRecoverBeforeRecordedCompletionLowerBounds(bool exception)
        {
            var s = new Scenario();
            AsyncProfilerCallStack seg = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: Enumerable.Range(500, 41).ToArray(),
                frameMethods: Enumerable.Range(30, 41).ToArray(),
                exceptionCompletions: exception ? Unwound(5) : null,
                wrapperResets: exception ? null : new[] { Qpc });
            var sync = new[]
            {
                s.Sync(40, 90),
                s.Sync(41, 34),
                s.Sync(42, 33),
                s.Sync(43, 32),
                s.Sync(44, 99),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg });

            Assert.Equal(0, result.Diagnostics.IncompleteAsyncRootsNormalized);
            Assert.Equal(0, result.Diagnostics.V2TruncatedSuffixFallbackUsed);
            Assert.Equal(1, result.Diagnostics.V2WrapperFallbackUsed);
            Assert.Contains(result.Frames, frame => frame.CodeAddress == Scenario.CA(44));
        }

        [Fact]
        public void V1_MissingBoundary_AmbiguousRepeatedSuffix_IsNotRecovered()
        {
            // A recursive state machine produces the same method identity at every logical depth. The retained
            // physical sequence can align at several positions, so truncated-suffix recovery must not guess.
            var s = new Scenario();
            AsyncProfilerCallStack seg = s.Segment(AsyncCallstackKind.StateMachineAsync,
                frameCodeAddrs: new[] { 500, 501, 502, 503, 504 },
                frameMethods: new[] { 30, 30, 30, 30, 30 });

            var sync = new[]
            {
                s.Sync(40, 70),
                s.Sync(41, 30),
                s.Sync(42, 90),
                s.Sync(43, 30),
                s.Sync(44, 91),
                s.Sync(45, 30),
                s.Sync(46, 99),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg });

            Assert.Equal(0, result.Diagnostics.V1TruncatedSuffixFallbackUsed);
            Assert.Equal(0, result.Diagnostics.IncompleteAsyncRootsNormalized);
            Assert.DoesNotContain(result.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.IncompleteRoot);
            Assert.Equal(1, result.Diagnostics.V1InlineFallbackUsed);
            Assert.Contains(result.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncRemaining);
        }

        [Fact]
        public void V1_ExceptionUnwind_IsAddedToInlineStackScan()
        {
            // No CompleteMethod events, but an Unwind (exception) event: the exceptional frame left the sync stack,
            // so it is counted from the event and ADDED to the inline stack scan. Segment (leaf-first): [C, B, A, P1];
            // C completed inline (on the sync stack) -> scan=1; B unwound exceptionally -> +1; completed=2 ->
            // current=frames[2]=A; ancestry=P1.
            var s = new Scenario();
            AsyncProfilerCallStack seg = s.Segment(AsyncCallstackKind.StateMachineAsync,
                frameCodeAddrs: new[] { 500, 501, 502, 503 },  // C, B, A, P1
                frameMethods:   new[] {  30,  31,  32,  33 },
                exceptionCompletions: Unwound(1));             // B unwound off the stack

            var sync = new[]
            {
                s.Sync(40, 32),                  // A (current, leaf) -> method 32
                s.Sync(42, 30),                  // C (completed inline) -> method 30 (B is gone: unwound)
                s.Boundary(43, V1Dispatcher),
                s.Sync(44, 99),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg });

            // Expected: [A(AsyncCurrent), P1(Async), root(Sync)]. C completed inline and B unwound, so both
            // physical transition frames are replaced by the logical ancestry.
            Assert.Equal(3, result.Frames.Count);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCurrent, result.Frames[0].Origin);
            Assert.Equal(Scenario.CA(40), result.Frames[0].CodeAddress);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncRemaining, result.Frames[1].Origin);
            Assert.Equal(Scenario.CA(503), result.Frames[1].CodeAddress); // P1 ancestry
            Assert.Equal(Scenario.CA(44), result.Frames[2].CodeAddress);
            Assert.Equal(1, result.Diagnostics.V1InlineFallbackUsed);
        }

        [Fact]
        public void V1_ExceptionBeforeInlineCompletion_AlignsPastUnwoundFrame()
        {
            // A unwound first, then B completed inline while C became current. The physical stack contains B/C
            // but not A, so the matcher must use the one exception as a bounded missing async frame before it can
            // match B and C in order.
            var s = new Scenario();
            AsyncProfilerCallStack seg = s.Segment(AsyncCallstackKind.StateMachineAsync,
                frameCodeAddrs: new[] { 500, 501, 502, 503 }, // A(unwound), B(completed), C(current), P
                frameMethods: new[] { 30, 31, 32, 33 },
                exceptionCompletions: Unwound(1));

            var sync = new[]
            {
                s.Sync(40, 32),                  // C current
                s.Sync(41, 31),                  // B completed inline
                s.Boundary(42, V1Dispatcher),
                s.Sync(43, 99),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg });

            Assert.Equal(new[] { Scenario.CA(40), Scenario.CA(503), Scenario.CA(43) },
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCurrent, result.Frames[0].Origin);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncRemaining, result.Frames[1].Origin);
            Assert.Equal(1, result.Diagnostics.V1InlineFallbackUsed);
        }

        [Fact]
        public void V1_UnresolvedExceptionBeforeInlineCompletion_AlignsPastUnwoundFrame()
        {
            // The exceptionally unwound frame has no code-address identity. Its Unwind event still proves that one
            // leading async frame may be skipped before matching the normally completed B and current C frames.
            var s = new Scenario();
            AsyncProfilerCallStack seg = s.Segment(AsyncCallstackKind.StateMachineAsync,
                frameCodeAddrs: new[] { -1, 501, 502, 503 }, // A(unresolved/unwound), B(completed), C(current), P
                frameMethods: new[] { 0, 31, 32, 33 },
                exceptionCompletions: Unwound(1));

            var sync = new[]
            {
                s.Sync(40, 32),                  // C current
                s.Sync(41, 31),                  // B completed inline
                s.Boundary(42, V1Dispatcher),
                s.Sync(43, 99),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg });

            Assert.Equal(new[] { Scenario.CA(40), Scenario.CA(503), Scenario.CA(43) },
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCurrent, result.Frames[0].Origin);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncRemaining, result.Frames[1].Origin);
            Assert.Equal(1, result.Diagnostics.V1InlineFallbackUsed);
        }

        [Fact]
        public void V1_MethodObservedPlusException_CombinesBothMechanisms()
        {
            // CompleteMethod observed AND an Unwind event: completed = method deltas + exception deltas (the total
            // GetCompletedFrameCount). Segment (leaf-first): [C0, C1, Mcur, A1]; 1 method + 1 unwind -> completed=2
            // -> current=frames[2]=Mcur; ancestry=A1. The inline stack scan is not used.
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.StateMachineAsync);
            AsyncProfilerCallStack seg = s.Segment(AsyncCallstackKind.StateMachineAsync,
                frameCodeAddrs: new[] { 500, 501, 502, 503 },  // C0, C1, Mcur, A1
                frameMethods:   new[] {  30,  31,  32,  33 },
                completions: Completed(1),                      // 1 normal completion via events
                exceptionCompletions: Unwound(1));             // + 1 exceptional unwind

            var sync = new[]
            {
                s.Sync(40, 32),                  // current -> frames[2] (method 32)
                s.Boundary(43, V1Dispatcher),
                s.Sync(44, 99),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg });

            Assert.Equal(3, result.Frames.Count);
            Assert.Equal(Scenario.CA(40), result.Frames[0].CodeAddress);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncRemaining, result.Frames[1].Origin);
            Assert.Equal(Scenario.CA(503), result.Frames[1].CodeAddress); // A1 ancestry
            Assert.Equal(Scenario.CA(44), result.Frames[2].CodeAddress);
            Assert.Equal(0, result.Diagnostics.V1InlineFallbackUsed); // events used, not the scan
        }

        [Fact]
        public void V2_WrapperSlot_IgnoresExceptionDeltas()
        {
            // V2 without CompleteMethod events derives completed from the wrapper slot, which already reflects
            // exceptional unwinds. Any Unwind deltas on the segment must NOT be added again. Wrapper slot 1 ->
            // completed=1 even though the segment also carries an exception delta.
            var s = new Scenario();
            AsyncProfilerCallStack seg = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 300, 301, 302 },
                frameMethods:   new[] {  60,  61,  62 },
                exceptionCompletions: Unwound(1));   // must be ignored on the wrapper path

            var sync = new[]
            {
                s.Sync(80, 61),                 // current (frames[1]) adjacent above the wrapper
                s.Boundary(81, Wrapper(1)),     // wrapper slot 1 -> completed = 1
                s.Sync(82, 99),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg });

            // completed=1 (wrapper slot only) -> current=frames[1]; splice frames[2]=302.
            Assert.Equal(3, result.Frames.Count);
            Assert.Equal(Scenario.CA(80), result.Frames[0].CodeAddress);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncRemaining, result.Frames[1].Origin);
            Assert.Equal(Scenario.CA(302), result.Frames[1].CodeAddress);
            Assert.Equal(Scenario.CA(82), result.Frames[2].CodeAddress);
            Assert.Equal(1, result.Diagnostics.V2WrapperFallbackUsed);
        }

        [Fact]
        public void MissingBoundary_DegradesToAppendOnly_WithDiagnostic()
        {
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            AsyncProfilerCallStack seg = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 600, 601 },
                frameMethods:   new[] {  80,  81 },
                completions: Completed(0)); // current=frames[0]; ancestry=frames[1]

            var sync = new[] { s.Sync(10, 1), s.Sync(11, 2) }; // no boundary frame present

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg });

            // All sync frames verbatim, then the remaining ancestry appended.
            Assert.Equal(3, result.Frames.Count);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, result.Frames[0].Origin);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, result.Frames[1].Origin);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncRemaining, result.Frames[2].Origin);
            Assert.Equal(Scenario.CA(601), result.Frames[2].CodeAddress);
            Assert.Equal(1, result.Diagnostics.BoundariesNotFound);
        }

        [Fact]
        public void NestedV2Segments_PromoteBothCurrentFramesAndPreserveSynchronousBridge()
        {
            // Two stacked V2 segments (each freshly resumed: completed=0, splice one ancestry frame).
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            AsyncProfilerCallStack leaf = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 700, 701 }, frameMethods: new[] { 10, 11 }, completions: Completed(0));
            AsyncProfilerCallStack root = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 800, 801 }, frameMethods: new[] { 20, 21 }, completions: Completed(0));

            var sync = new[]
            {
                s.Sync(50, 10),                  // L current (frames[0] of leaf seg)
                s.Boundary(51, Wrapper(0)),      // leaf boundary
                s.Sync(55, 70),                  // synchronous bridge between the nested activations
                s.Sync(52, 20),                  // R current (frames[0] of root seg)
                s.Boundary(53, Wrapper(0)),      // root boundary
                s.Sync(54, 99),                  // root frame
            };

            // GetCallStacks returns depth-ascending (root -> leaf); Stitch reverses internally.
            AsyncProfilerStitchResult result = s.Run(sync, new[] { root, leaf });

            // Expected: [L0(AsyncCurrent), L1(Async), bridge(Sync),
            // R0(AsyncCurrent), R1(Async), root(Sync)].
            Assert.Equal(6, result.Frames.Count);
            Assert.Equal(Scenario.CA(50), result.Frames[0].CodeAddress);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCurrent, result.Frames[0].Origin);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncRemaining, result.Frames[1].Origin);
            Assert.Equal(Scenario.CA(701), result.Frames[1].CodeAddress);
            Assert.Equal(Scenario.CA(55), result.Frames[2].CodeAddress);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, result.Frames[2].Origin);
            Assert.Equal(Scenario.CA(52), result.Frames[3].CodeAddress);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCurrent, result.Frames[3].Origin);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncRemaining, result.Frames[4].Origin);
            Assert.Equal(Scenario.CA(801), result.Frames[4].CodeAddress);
            Assert.Equal(Scenario.CA(54), result.Frames[5].CodeAddress);
            Assert.Equal(2, result.Diagnostics.SegmentsProcessed);
            Assert.Equal(0, result.Diagnostics.BoundariesNotFound);

            Assert.Equal(2, result.SegmentPlacements.Count);
            Assert.Same(leaf, result.SegmentPlacements[0].Activation);
            Assert.Equal(0, result.SegmentPlacements[0].LeafIndex);
            Assert.Equal(2, result.SegmentPlacements[0].RootIndexExclusive);
            Assert.Same(root, result.SegmentPlacements[1].Activation);
            Assert.Equal(2, result.SegmentPlacements[1].LeafIndex);
            Assert.Equal(5, result.SegmentPlacements[1].RootIndexExclusive);
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, false, true)]
        [InlineData(false, true, false)]
        [InlineData(false, true, true)]
        [InlineData(true, false, false)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        [InlineData(true, true, true)]
        public void MissingInnerV1Boundary_PreservesVerifiedOuterV2Activation(
            bool carrier, bool methodEvents, bool sharedMethod)
        {
            var s = new Scenario();
            if (methodEvents)
            {
                s.MarkMethodCompletionObserved(AsyncCallstackKind.StateMachineAsync);
                s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            }
            int outerMethod = sharedMethod ? 30 : 40;
            AsyncProfilerCallStack outer = s.Segment(AsyncCallstackKind.RuntimeAsync,
                new[] { 500, 501, 502, 503 }, new[] { 70, 71, outerMethod, 41 },
                completions: methodEvents ? Completed(2) : null);
            AsyncProfilerCallStack inner = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 600, 601, 602, 603 }, new[] { 30, 31, 32, 33 },
                completions: methodEvents ? Completed(2) : null);
            var sync = new[]
            {
                s.Sync(10, 90),
                carrier ? s.BoxCarrier(11, 32) : s.Sync(11, 32),
                s.Sync(12, 31),
                s.Sync(13, 30),
                new AsyncProfilerStitchSyncFrame(Scenario.CA(18), MethodIndex.Invalid),
                s.Sync(19, 89),
                s.Sync(14, outerMethod),
                s.Boundary(15, Wrapper(methodEvents ? 0 : 2)),
                s.Boundary(16, DispatchContinuation),
                s.Sync(17, 99),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { outer, inner });

            Assert.Equal(
                new[] { 10, 11, 603, 18, 19, 14, 503, 17 }.Select(Scenario.CA),
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(carrier ? AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget : AsyncProfilerStitchedFrameOrigin.AsyncCurrent,
                result.Frames[1].Origin);
            Assert.Equal(carrier, result.Frames[1].IsStateMachineBoxCarrier);
            Assert.Same(inner, result.Frames[1].Context);
            Assert.Equal(2, result.Frames[1].SegmentFrameIndex);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCurrent, result.Frames[5].Origin);
            Assert.Same(outer, result.Frames[5].Context);
            Assert.Equal(2, result.Frames[5].SegmentFrameIndex);
            Assert.Equal(2, result.Diagnostics.SegmentsProcessed);
            Assert.Equal(1, result.Diagnostics.BoundariesNotFound);
            Assert.Equal(methodEvents ? 0 : 1, result.Diagnostics.V1TruncatedSuffixFallbackUsed);
            Assert.Equal(1, result.Diagnostics.V2PlumbingFramesCollapsed);
            Assert.Equal(0, result.Diagnostics.IncompleteAsyncRootsNormalized);
            Assert.DoesNotContain(result.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.IncompleteRoot);
            Assert.Same(inner, result.SegmentPlacements[0].Activation);
            Assert.Equal(3, result.SegmentPlacements[0].RootIndexExclusive);
            Assert.Same(outer, result.SegmentPlacements[1].Activation);
            Assert.Equal(3, result.SegmentPlacements[1].LeafIndex);
            Assert.Equal(7, result.SegmentPlacements[1].RootIndexExclusive);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void MissingInnerV1Boundary_UnverifiedOuterAnchorDoesNotTrimThePhysicalSpan(bool unresolved)
        {
            var s = new Scenario();
            AsyncProfilerCallStack outer = s.Segment(AsyncCallstackKind.RuntimeAsync,
                new[] { 500, 501 }, new[] { 40, 41 });
            AsyncProfilerCallStack inner = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 600, 601, 602, 603 }, new[] { 30, 31, 32, 33 });
            var sync = new[]
            {
                s.Sync(10, 90),
                s.Sync(11, 32),
                s.Sync(12, 31),
                s.Sync(13, 30),
                new AsyncProfilerStitchSyncFrame(Scenario.CA(14), unresolved ? MethodIndex.Invalid : Scenario.MI(89)),
                s.Boundary(15, Wrapper(0)),
                s.Sync(17, 99),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { outer, inner });

            Assert.Equal(sync.Select(frame => frame.CodeAddress),
                result.Frames.Take(sync.Length).Select(frame => frame.CodeAddress));
            Assert.DoesNotContain(result.Frames, frame =>
                frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent && ReferenceEquals(frame.Context, outer));
            Assert.Equal(0, result.Diagnostics.IncompleteAsyncRootsNormalized);
        }

        [Fact]
        public void MissingInnerV1Boundaries_PreserveTheEnclosingV2AcrossMultipleActivations()
        {
            var s = new Scenario();
            AsyncProfilerCallStack outer = s.Segment(AsyncCallstackKind.RuntimeAsync,
                new[] { 500, 501 }, new[] { 40, 41 });
            AsyncProfilerCallStack middle = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 600, 601, 602 }, new[] { 20, 21, 22 });
            AsyncProfilerCallStack inner = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 700, 701, 702 }, new[] { 10, 11, 12 });
            var sync = new[]
            {
                s.Sync(50, 90),
                s.Sync(51, 11),
                s.Sync(52, 10),
                new AsyncProfilerStitchSyncFrame(Scenario.CA(53), MethodIndex.Invalid),
                s.Sync(54, 91),
                s.Sync(55, 21),
                s.Sync(56, 20),
                new AsyncProfilerStitchSyncFrame(Scenario.CA(57), MethodIndex.Invalid),
                s.Sync(58, 92),
                s.Sync(59, 40),
                s.Boundary(60, Wrapper(0)),
                s.Boundary(61, DispatchContinuation),
                s.Sync(62, 99),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { outer, middle, inner });

            Assert.Equal(
                new[] { 50, 51, 702, 53, 54, 55, 602, 57, 58, 59, 501, 62 }.Select(Scenario.CA),
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(3, result.Diagnostics.SegmentsProcessed);
            Assert.Equal(2, result.Diagnostics.BoundariesNotFound);
            Assert.Equal(2, result.Diagnostics.V1TruncatedSuffixFallbackUsed);
            Assert.Equal(3, result.Frames.Count(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent));
            Assert.Equal(0, result.Diagnostics.IncompleteAsyncRootsNormalized);
        }

        [Fact]
        public void MissingInnerV1Boundary_CarrierStillRequiresUniquePhysicalAlignment()
        {
            var s = new Scenario();
            AsyncProfilerCallStack outer = s.Segment(AsyncCallstackKind.RuntimeAsync,
                new[] { 500, 501 }, new[] { 40, 41 });
            AsyncProfilerCallStack inner = s.Segment(AsyncCallstackKind.StateMachineAsync,
                new[] { 600, 601, 602 }, new[] { 30, 31, 32 });
            var sync = new[]
            {
                s.Sync(10, 90),
                s.BoxCarrier(11, 31),
                s.Sync(12, 30),
                new AsyncProfilerStitchSyncFrame(Scenario.CA(18), MethodIndex.Invalid),
                s.Sync(14, 40),
                s.Boundary(15, Wrapper(0)),
                s.Sync(17, 99),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { outer, inner });

            Assert.Equal(sync.Take(4).Select(frame => frame.CodeAddress),
                result.Frames.Take(4).Select(frame => frame.CodeAddress));
            Assert.DoesNotContain(result.Frames, frame => frame.IsStateMachineBoxCarrier);
            Assert.Contains(result.Frames, frame =>
                frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent && ReferenceEquals(frame.Context, outer));
            Assert.Equal(0, result.Diagnostics.V1TruncatedSuffixFallbackUsed);
            Assert.Equal(1, result.Diagnostics.BoundariesNotFound);
        }

        [Fact]
        public void ContextAncestry_TracksEveryHistoricalParentSegment()
        {
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            var index = new AsyncProfilerCallStacksIndex();
            ProcessIndex process = (ProcessIndex)1;
            var sampledThread = new AsyncProfilerThreadKey(process, 100);
            var p3Thread = new AsyncProfilerThreadKey(process, 200);
            var p2Thread = new AsyncProfilerThreadKey(process, 300);
            var p1Thread = new AsyncProfilerThreadKey(process, 400);

            AsyncProfilerCallStack outer = AddIndexedSegment(
                s, index, sampledThread, dispatcherId: 40, depth: 0,
                startQpc: 0, endQpc: 2000, firstCodeAddress: 400);
            AsyncProfilerCallStack p3 = AddIndexedSegment(
                s, index, p3Thread, dispatcherId: 30, depth: 0,
                startQpc: 200, endQpc: 800, firstCodeAddress: 300);
            AsyncProfilerCallStack p2 = AddIndexedSegment(
                s, index, p2Thread, dispatcherId: 20, depth: 0,
                startQpc: 400, endQpc: 900, firstCodeAddress: 200);
            AsyncProfilerCallStack p1 = AddIndexedSegment(
                s, index, p1Thread, dispatcherId: 10, depth: 0,
                startQpc: 600, endQpc: 950, firstCodeAddress: 100);
            AsyncProfilerCallStack inner = AddIndexedSegment(
                s, index, sampledThread, dispatcherId: 1, depth: 1,
                startQpc: 1000, endQpc: 2000, firstCodeAddress: 10);

            index.AddDispatcherInfo(sampledThread, dispatcherId: 30, parentDispatcherId: 40, creationQpc: 100);
            index.AddDispatcherInfo(p3Thread, dispatcherId: 20, parentDispatcherId: 30, creationQpc: 300);
            index.AddDispatcherInfo(p2Thread, dispatcherId: 10, parentDispatcherId: 20, creationQpc: 500);
            index.AddDispatcherInfo(p1Thread, dispatcherId: 1, parentDispatcherId: 10, creationQpc: 800);

            var sync = new[]
            {
                s.Sync(50, 10),
                s.Boundary(51, Wrapper(0)),
                s.Sync(52, 400),
                s.Boundary(53, Wrapper(0)),
                s.Sync(54, 999),
            };

            AsyncProfilerStitchResult structural = s.Run(sync, new[] { outer, inner });
            var frames = structural.Frames.ToList();
            var augmenter = new AsyncProfilerContextAncestryAugmenter();

            augmenter.Augment(
                index,
                new[] { outer, inner },
                structural.SegmentPlacements,
                frames,
                AsyncProfilerContextAncestryAugmenter.DefaultMaximumDepth);

            Assert.Equal(
                new[]
                {
                    AsyncProfilerStitchedFrameOrigin.AsyncCurrent,
                    AsyncProfilerStitchedFrameOrigin.AsyncRemaining,
                    AsyncProfilerStitchedFrameOrigin.AsyncContextParent,
                    AsyncProfilerStitchedFrameOrigin.AsyncContextParent,
                    AsyncProfilerStitchedFrameOrigin.AsyncContextParent,
                    AsyncProfilerStitchedFrameOrigin.AsyncContextParent,
                    AsyncProfilerStitchedFrameOrigin.AsyncContextParent,
                    AsyncProfilerStitchedFrameOrigin.AsyncContextParent,
                    AsyncProfilerStitchedFrameOrigin.AsyncCurrent,
                    AsyncProfilerStitchedFrameOrigin.AsyncRemaining,
                    AsyncProfilerStitchedFrameOrigin.Sync,
                },
                frames.Select(frame => frame.Origin));

            Assert.Equal(
                new[]
                {
                    AsyncProfilerStitchedFrameContextKind.Active,
                    AsyncProfilerStitchedFrameContextKind.Active,
                    AsyncProfilerStitchedFrameContextKind.HistoricalParent,
                    AsyncProfilerStitchedFrameContextKind.HistoricalParent,
                    AsyncProfilerStitchedFrameContextKind.HistoricalParent,
                    AsyncProfilerStitchedFrameContextKind.HistoricalParent,
                    AsyncProfilerStitchedFrameContextKind.HistoricalParent,
                    AsyncProfilerStitchedFrameContextKind.HistoricalParent,
                    AsyncProfilerStitchedFrameContextKind.Active,
                    AsyncProfilerStitchedFrameContextKind.Active,
                    AsyncProfilerStitchedFrameContextKind.None,
                },
                frames.Select(frame => frame.ContextKind));
            Assert.All(frames.Take(2), frame => Assert.Same(inner, frame.Context));
            Assert.All(frames.Skip(2).Take(2), frame => Assert.Same(p1, frame.Context));
            Assert.All(frames.Skip(4).Take(2), frame => Assert.Same(p2, frame.Context));
            Assert.All(frames.Skip(6).Take(2), frame => Assert.Same(p3, frame.Context));
            Assert.All(frames.Skip(8).Take(2), frame => Assert.Same(outer, frame.Context));
            Assert.Null(frames[10].Context);
            Assert.Equal(
                new[]
                {
                    Scenario.CA(100), Scenario.CA(101),
                    Scenario.CA(200), Scenario.CA(201),
                    Scenario.CA(300), Scenario.CA(301),
                },
                frames
                    .Where(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncContextParent)
                    .Select(frame => frame.CodeAddress));

            var limitedFrames = structural.Frames.ToList();
            augmenter.Augment(
                index,
                new[] { outer, inner },
                structural.SegmentPlacements,
                limitedFrames,
                maximumDepth: 2);
            Assert.Equal(
                new[]
                {
                    Scenario.CA(100), Scenario.CA(101),
                    Scenario.CA(200), Scenario.CA(201),
                },
                limitedFrames
                    .Where(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncContextParent)
                    .Select(frame => frame.CodeAddress));
            Assert.DoesNotContain(
                limitedFrames,
                frame => frame.ContextKind == AsyncProfilerStitchedFrameContextKind.HistoricalParent &&
                         ReferenceEquals(frame.Context, p3));
        }

        [Fact]
        public void ContextAncestry_PreservesOverlappingDistinctParentActivations()
        {
            var s = new Scenario();
            var index = new AsyncProfilerCallStacksIndex();
            ProcessIndex process = (ProcessIndex)1;
            var childThread = new AsyncProfilerThreadKey(process, 100);
            var parentThread = new AsyncProfilerThreadKey(process, 200);
            var grandparentThread = new AsyncProfilerThreadKey(process, 300);
            AsyncProfilerCallStack child = AddIndexedSegment(
                s, index, childThread, dispatcherId: 1, depth: 0,
                startQpc: 100, endQpc: 200, firstCodeAddress: 10, frameCount: 3);
            AsyncProfilerCallStack parent = AddIndexedSegment(
                s, index, parentThread, dispatcherId: 2, depth: 0,
                startQpc: 50, endQpc: 99, firstCodeAddress: 11, frameCount: 3);
            AsyncProfilerCallStack grandparent = AddIndexedSegment(
                s, index, grandparentThread, dispatcherId: 3, depth: 0,
                startQpc: 0, endQpc: 99, firstCodeAddress: 12, frameCount: 3);
            index.AddDispatcherInfo(
                parentThread, dispatcherId: 1, parentDispatcherId: 2, creationQpc: 90);
            index.AddDispatcherInfo(
                grandparentThread, dispatcherId: 2, parentDispatcherId: 3, creationQpc: 40);

            var frames = new List<AsyncProfilerStitchedFrame>
            {
                AsyncProfilerStitchedFrame.CreateAsync(child, 0),
                AsyncProfilerStitchedFrame.CreateAsync(child, 1),
                AsyncProfilerStitchedFrame.CreateAsync(child, 2),
            };
            var placements = new[]
            {
                new AsyncProfilerSegmentPlacement(child, leafIndex: 0, rootIndexExclusive: 3),
            };

            new AsyncProfilerContextAncestryAugmenter().Augment(
                index,
                new[] { child },
                placements,
                frames,
                AsyncProfilerContextAncestryAugmenter.DefaultMaximumDepth);

            AsyncProfilerStitchedFrame[] historical = frames
                .Where(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncContextParent)
                .ToArray();
            Assert.Equal(
                new[]
                {
                    Scenario.CA(11), Scenario.CA(12), Scenario.CA(13),
                    Scenario.CA(12), Scenario.CA(13), Scenario.CA(14),
                },
                historical.Select(frame => frame.CodeAddress));
            Assert.All(historical.Take(3), frame => Assert.Same(parent, frame.Context));
            Assert.All(historical.Skip(3), frame => Assert.Same(grandparent, frame.Context));
        }

        [Fact]
        public void ContextAncestry_PreservesRecursiveV1ParentWithIdenticalFramesAndStates()
        {
            var s = new Scenario();
            var index = new AsyncProfilerCallStacksIndex();
            ProcessIndex process = (ProcessIndex)1;
            var childThread = new AsyncProfilerThreadKey(process, 100);
            var parentThread = new AsyncProfilerThreadKey(process, 200);
            AsyncProfilerCallStack child = AddIndexedSegment(
                s, index, childThread, dispatcherId: 1, depth: 0,
                startQpc: 100, endQpc: 200, firstCodeAddress: 10, frameCount: 2,
                kind: AsyncCallstackKind.StateMachineAsync,
                frameStates: new[] { 1, 2 });
            AsyncProfilerCallStack parent = AddIndexedSegment(
                s, index, parentThread, dispatcherId: 2, depth: 0,
                startQpc: 0, endQpc: 90, firstCodeAddress: 10, frameCount: 2,
                kind: AsyncCallstackKind.StateMachineAsync,
                frameStates: new[] { 1, 2 });
            index.AddDispatcherInfo(
                parentThread, dispatcherId: 1, parentDispatcherId: 2, creationQpc: 50);

            var frames = new List<AsyncProfilerStitchedFrame>
            {
                AsyncProfilerStitchedFrame.CreateAsync(child, 0),
                AsyncProfilerStitchedFrame.CreateAsync(child, 1),
            };
            var placements = new[]
            {
                new AsyncProfilerSegmentPlacement(child, leafIndex: 0, rootIndexExclusive: 2),
            };

            new AsyncProfilerContextAncestryAugmenter().Augment(
                index,
                new[] { child },
                placements,
                frames,
                AsyncProfilerContextAncestryAugmenter.DefaultMaximumDepth);

            AsyncProfilerStitchedFrame[] historical = frames
                .Where(frame =>
                    frame.ContextKind == AsyncProfilerStitchedFrameContextKind.HistoricalParent)
                .ToArray();
            Assert.Equal(2, historical.Length);
            Assert.All(historical, frame => Assert.Same(parent, frame.Context));
        }

        [Fact]
        public void ContextAncestry_DoesNotTrimAcrossAsyncKinds()
        {
            var s = new Scenario();
            var index = new AsyncProfilerCallStacksIndex();
            ProcessIndex process = (ProcessIndex)1;
            var childThread = new AsyncProfilerThreadKey(process, 100);
            var parentThread = new AsyncProfilerThreadKey(process, 200);
            AsyncProfilerCallStack child = AddIndexedSegment(
                s, index, childThread, dispatcherId: 1, depth: 0,
                startQpc: 100, endQpc: 200, firstCodeAddress: 10,
                kind: AsyncCallstackKind.RuntimeAsync);
            AddIndexedSegment(
                s, index, parentThread, dispatcherId: 2, depth: 0,
                startQpc: 0, endQpc: 90, firstCodeAddress: 10,
                kind: AsyncCallstackKind.StateMachineAsync);
            index.AddDispatcherInfo(
                parentThread, dispatcherId: 1, parentDispatcherId: 2, creationQpc: 50);

            var frames = new List<AsyncProfilerStitchedFrame>
            {
                AsyncProfilerStitchedFrame.CreateAsync(child, 0),
                AsyncProfilerStitchedFrame.CreateAsync(child, 1),
            };
            var placements = new[]
            {
                new AsyncProfilerSegmentPlacement(child, leafIndex: 0, rootIndexExclusive: 2),
            };

            new AsyncProfilerContextAncestryAugmenter().Augment(
                index,
                new[] { child },
                placements,
                frames,
                AsyncProfilerContextAncestryAugmenter.DefaultMaximumDepth);

            Assert.Equal(
                2,
                frames.Count(frame =>
                    frame.ContextKind == AsyncProfilerStitchedFrameContextKind.HistoricalParent));
        }

        [Fact]
        public void ContextAncestry_DoesNotTrimDifferentV1States()
        {
            var s = new Scenario();
            var index = new AsyncProfilerCallStacksIndex();
            ProcessIndex process = (ProcessIndex)1;
            var childThread = new AsyncProfilerThreadKey(process, 100);
            var parentThread = new AsyncProfilerThreadKey(process, 200);
            AsyncProfilerCallStack child = AddIndexedSegment(
                s, index, childThread, dispatcherId: 1, depth: 0,
                startQpc: 100, endQpc: 200, firstCodeAddress: 10,
                kind: AsyncCallstackKind.StateMachineAsync,
                frameStates: new[] { 1, 2 });
            AddIndexedSegment(
                s, index, parentThread, dispatcherId: 2, depth: 0,
                startQpc: 0, endQpc: 90, firstCodeAddress: 10,
                kind: AsyncCallstackKind.StateMachineAsync,
                frameStates: new[] { 1, 3 });
            index.AddDispatcherInfo(
                parentThread, dispatcherId: 1, parentDispatcherId: 2, creationQpc: 50);

            var frames = new List<AsyncProfilerStitchedFrame>
            {
                AsyncProfilerStitchedFrame.CreateAsync(child, 0),
                AsyncProfilerStitchedFrame.CreateAsync(child, 1),
            };
            var placements = new[]
            {
                new AsyncProfilerSegmentPlacement(child, leafIndex: 0, rootIndexExclusive: 2),
            };

            new AsyncProfilerContextAncestryAugmenter().Augment(
                index,
                new[] { child },
                placements,
                frames,
                AsyncProfilerContextAncestryAugmenter.DefaultMaximumDepth);

            Assert.Equal(
                2,
                frames.Count(frame =>
                    frame.ContextKind == AsyncProfilerStitchedFrameContextKind.HistoricalParent));
        }

        [Fact]
        public void ContextAncestry_CycleStopsBeforeRepeatingAnActivation()
        {
            var s = new Scenario();
            var index = new AsyncProfilerCallStacksIndex();
            ProcessIndex process = (ProcessIndex)1;
            var aThread = new AsyncProfilerThreadKey(process, 100);
            var bThread = new AsyncProfilerThreadKey(process, 200);
            AsyncProfilerCallStack a = AddIndexedSegment(
                s, index, aThread, dispatcherId: 1, depth: 0,
                startQpc: 0, endQpc: 100, firstCodeAddress: 10);
            AsyncProfilerCallStack b = AddIndexedSegment(
                s, index, bThread, dispatcherId: 2, depth: 0,
                startQpc: 0, endQpc: 100, firstCodeAddress: 20);

            index.AddDispatcherInfo(
                bThread, dispatcherId: 1, parentDispatcherId: 2, creationQpc: 0);
            index.AddDispatcherInfo(
                aThread, dispatcherId: 2, parentDispatcherId: 1, creationQpc: 0);

            var frames = new List<AsyncProfilerStitchedFrame>
            {
                AsyncProfilerStitchedFrame.CreateAsync(a, 0),
            };
            var placements = new[]
            {
                new AsyncProfilerSegmentPlacement(a, leafIndex: 0, rootIndexExclusive: 1),
            };
            var augmenter = new AsyncProfilerContextAncestryAugmenter();

            Assert.True(augmenter.Prepare(index, new[] { a }));
            augmenter.Augment(
                index,
                new[] { a },
                placements,
                frames,
                AsyncProfilerContextAncestryAugmenter.DefaultMaximumDepth);

            AsyncProfilerStitchedFrame[] parents = frames
                .Where(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncContextParent)
                .ToArray();
            Assert.Equal(b.Frames.FrameCount, parents.Length);
            Assert.All(parents, frame => Assert.Same(b, frame.Context));
            Assert.DoesNotContain(parents, frame => ReferenceEquals(a, frame.Context));
        }

        [Fact]
        public void ContextAncestry_SharedParentIsInsertedForEachIndependentSample()
        {
            var s = new Scenario();
            var index = new AsyncProfilerCallStacksIndex();
            ProcessIndex process = (ProcessIndex)1;
            var parentThread = new AsyncProfilerThreadKey(process, 100);
            var firstChildThread = new AsyncProfilerThreadKey(process, 200);
            var secondChildThread = new AsyncProfilerThreadKey(process, 300);
            AsyncProfilerCallStack parent = AddIndexedSegment(
                s, index, parentThread, dispatcherId: 10, depth: 0,
                startQpc: 0, endQpc: 500, firstCodeAddress: 100);
            AsyncProfilerCallStack firstChild = AddIndexedSegment(
                s, index, firstChildThread, dispatcherId: 1, depth: 0,
                startQpc: 100, endQpc: 200, firstCodeAddress: 10);
            AsyncProfilerCallStack secondChild = AddIndexedSegment(
                s, index, secondChildThread, dispatcherId: 2, depth: 0,
                startQpc: 200, endQpc: 300, firstCodeAddress: 20);

            index.AddDispatcherInfo(
                parentThread, dispatcherId: 1, parentDispatcherId: 10, creationQpc: 50);
            index.AddDispatcherInfo(
                parentThread, dispatcherId: 2, parentDispatcherId: 10, creationQpc: 150);

            var augmenter = new AsyncProfilerContextAncestryAugmenter();
            AssertSharedParent(firstChild);
            AssertSharedParent(secondChild);

            void AssertSharedParent(AsyncProfilerCallStack child)
            {
                var frames = new List<AsyncProfilerStitchedFrame>
                {
                    AsyncProfilerStitchedFrame.CreateAsync(child, 0),
                };
                var placements = new[]
                {
                    new AsyncProfilerSegmentPlacement(child, leafIndex: 0, rootIndexExclusive: 1),
                };

                Assert.True(augmenter.Prepare(index, new[] { child }));
                augmenter.Augment(
                    index,
                    new[] { child },
                    placements,
                    frames,
                    AsyncProfilerContextAncestryAugmenter.DefaultMaximumDepth);

                AsyncProfilerStitchedFrame[] parents = frames
                    .Where(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncContextParent)
                    .ToArray();
                Assert.Equal(parent.Frames.FrameCount, parents.Length);
                Assert.All(parents, frame => Assert.Same(parent, frame.Context));
            }
        }

        [Fact]
        public void ContextParent_UsesExactCompletionAndUnwindHistoryAtDispatcherCreationQpc()
        {
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            var index = new AsyncProfilerCallStacksIndex();
            ProcessIndex process = (ProcessIndex)1;
            var parentThread = new AsyncProfilerThreadKey(process, 100);
            var childThread = new AsyncProfilerThreadKey(process, 200);
            index.StartCompletionAvailabilityEpoch(process, startQpc: 0);
            index.MarkMethodCompletionObserved(
                process, AsyncCallstackKind.RuntimeAsync, qpc: 20);

            AsyncProfilerCallStack parent = AddIndexedSegment(
                s, index, parentThread, dispatcherId: 10, depth: 0,
                startQpc: 0, endQpc: 90, firstCodeAddress: 100,
                frameCount: 3,
                methodCompletions: new[] { new AsyncProfilerCallStack.CompletionDelta(20, 1) },
                exceptionCompletions: new[] { new AsyncProfilerCallStack.CompletionDelta(30, 1) });
            AsyncProfilerCallStack child = AddIndexedSegment(
                s, index, childThread, dispatcherId: 1, depth: 0,
                startQpc: 100, endQpc: 2000, firstCodeAddress: 10);
            index.AddDispatcherInfo(
                parentThread, dispatcherId: 1, parentDispatcherId: 10, creationQpc: 50);

            var sync = new[]
            {
                s.Sync(50, 10),
                s.Boundary(51, Wrapper(0)),
                s.Sync(52, 999),
            };
            AsyncProfilerStitchResult structural = s.Run(sync, new[] { child });
            var frames = structural.Frames.ToList();

            new AsyncProfilerContextAncestryAugmenter().Augment(
                index,
                new[] { child },
                structural.SegmentPlacements,
                frames,
                AsyncProfilerContextAncestryAugmenter.DefaultMaximumDepth);

            Assert.Equal(
                new[] { Scenario.CA(102) },
                frames
                    .Where(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncContextParent)
                    .Select(frame => frame.CodeAddress));
            Assert.All(
                frames.Where(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncContextParent),
                frame =>
                {
                    Assert.Same(parent, frame.Context);
                    Assert.Equal(AsyncProfilerStitchedFrameContextKind.HistoricalParent, frame.ContextKind);
                });
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, (int)AsyncCallstackKind.RuntimeAsync)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, (int)AsyncCallstackKind.StateMachineAsync)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, (int)AsyncCallstackKind.RuntimeAsync)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, (int)AsyncCallstackKind.StateMachineAsync)]
        public void ContextAncestry_PreservesParentKindAcrossMixedKinds(
            int childKindValue,
            int parentKindValue)
        {
            var childKind = (AsyncCallstackKind)childKindValue;
            var parentKind = (AsyncCallstackKind)parentKindValue;
            var s = new Scenario();
            var index = new AsyncProfilerCallStacksIndex();
            ProcessIndex process = (ProcessIndex)1;
            var parentThread = new AsyncProfilerThreadKey(process, 100);
            var childThread = new AsyncProfilerThreadKey(process, 200);
            AsyncProfilerCallStack parent = AddIndexedSegment(
                s, index, parentThread, dispatcherId: 10, depth: 0,
                startQpc: 0, endQpc: 90, firstCodeAddress: 100,
                kind: parentKind);
            AsyncProfilerCallStack child = AddIndexedSegment(
                s, index, childThread, dispatcherId: 1, depth: 0,
                startQpc: 100, endQpc: 2000, firstCodeAddress: 10,
                kind: childKind);
            index.AddDispatcherInfo(
                parentThread, dispatcherId: 1, parentDispatcherId: 10, creationQpc: 50);

            var frames = new List<AsyncProfilerStitchedFrame>
            {
                AsyncProfilerStitchedFrame.CreateAsync(child, 0),
            };
            var placements = new[]
            {
                new AsyncProfilerSegmentPlacement(child, leafIndex: 0, rootIndexExclusive: 1),
            };

            new AsyncProfilerContextAncestryAugmenter().Augment(
                index,
                new[] { child },
                placements,
                frames,
                AsyncProfilerContextAncestryAugmenter.DefaultMaximumDepth);

            AsyncProfilerStitchedFrame[] parentContextFrames =
                frames.Where(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncContextParent).ToArray();
            Assert.Equal(parent.Frames.FrameCount, parentContextFrames.Length);
            Assert.All(parentContextFrames, frame =>
            {
                Assert.Same(parent.Frames, frame.Segment);
                Assert.Equal(
                    parentKind == AsyncCallstackKind.StateMachineAsync
                        ? AsyncProfilerStitchedFramePresentation.LogicalStateMachineMethod
                        : AsyncProfilerStitchedFramePresentation.Native,
                    frame.Presentation);
            });
        }

        private static AsyncProfilerCallStack AddIndexedSegment(
            Scenario scenario,
            AsyncProfilerCallStacksIndex index,
            AsyncProfilerThreadKey thread,
            ulong dispatcherId,
            int depth,
            long startQpc,
            long endQpc,
            int firstCodeAddress,
            int frameCount = 2,
            AsyncProfilerCallStack.CompletionDelta[] methodCompletions = null,
            AsyncProfilerCallStack.CompletionDelta[] exceptionCompletions = null,
            AsyncCallstackKind kind = AsyncCallstackKind.RuntimeAsync,
            int[] frameStates = null)
        {
            var methodIds = new ulong[frameCount];
            for (int i = 0; i < methodIds.Length; i++)
            {
                methodIds[i] = (ulong)(firstCodeAddress + i);
            }
            index.Add(
                thread,
                kind,
                methodIds,
                frameStates: kind == AsyncCallstackKind.StateMachineAsync
                    ? (frameStates ?? new int[frameCount])
                    : null,
                dispatcherId,
                depth,
                continuationIndexBase: 0,
                wrapperCount: 32,
                startQpc,
                endQpc,
                methodCompletions: methodCompletions ?? Array.Empty<AsyncProfilerCallStack.CompletionDelta>(),
                exceptionCompletions: exceptionCompletions ?? Array.Empty<AsyncProfilerCallStack.CompletionDelta>(),
                wrapperResets: Array.Empty<long>());

            AsyncProfilerCallStack segment =
                index.GetCallStack(thread, dispatcherId, startQpc);
            for (int i = 0; i < frameCount; i++)
            {
                segment.Frames.SetCodeAddressAt(i, Scenario.CA(firstCodeAddress + i));
                scenario.RegisterMethod(firstCodeAddress + i, firstCodeAddress + i);
            }
            return segment;
        }

        [Fact]
        public void MixedV1AndV2Segments_PreserveSynchronousAndNativeBridge()
        {
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            AsyncProfilerCallStack outer = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 800, 801 }, frameMethods: new[] { 20, 21 }, completions: Completed(0));
            AsyncProfilerCallStack inner = s.Segment(AsyncCallstackKind.StateMachineAsync,
                frameCodeAddrs: new[] { 500, 501 }, frameMethods: new[] { 10, 11 });

            var sync = new[]
            {
                s.Sync(50, 90),                       // synchronous work inside the inner V1 method
                s.Sync(51, 10),                       // inner V1 current
                s.Sync(52, 91),                       // AsyncStateMachineBox.ExecutionContextCallback
                s.Sync(53, 92),                       // ExecutionContext.RunInternal
                s.Sync(54, 93),                       // AsyncStateMachineBox.MoveNext(Thread,Flags)
                s.Boundary(55, V1Dispatcher),         // MoveNextAsDispatcher
                s.Boundary(56, V1Infrastructure),     // known V1 box infrastructure
                s.Boundary(57, V1Infrastructure),     // known V1 box infrastructure
                s.Sync(58, 94),                       // unclassified scheduling frame: collapse stops
                s.Sync(59, 95),                       // native bridge frame
                s.Sync(60, 96),                       // synchronous bridge frame
                s.Sync(61, 20),                       // outer V2 current
                s.Boundary(62, Wrapper(0)),
                s.Boundary(63, DispatchContinuation),
                s.Boundary(64, DispatchContinuation),
                s.Sync(65, 97),                       // ThreadPool dispatch anchor
                s.Sync(66, 98),                       // thread root
            };

            // Index order is outer/root -> inner/leaf.
            AsyncProfilerStitchResult result = s.Run(sync, new[] { outer, inner });

            Assert.Equal(10, result.Frames.Count);
            Assert.Equal(new[]
            {
                Scenario.CA(50), Scenario.CA(51), Scenario.CA(501),
                Scenario.CA(58), Scenario.CA(59), Scenario.CA(60), Scenario.CA(61), Scenario.CA(801),
                Scenario.CA(65), Scenario.CA(66),
            }, result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCurrent, result.Frames[1].Origin);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncRemaining, result.Frames[2].Origin);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncRemaining, result.Frames[7].Origin);
            Assert.Equal(2, result.Diagnostics.SegmentsProcessed);
            Assert.Equal(2, result.Diagnostics.V2PlumbingFramesCollapsed);
            Assert.Equal(2, result.Diagnostics.V1InfrastructureFramesCollapsed);
            Assert.Equal(1, result.Diagnostics.V1InlineFallbackUsed);
            Assert.Equal(0, result.Diagnostics.BoundariesNotFound);

            Assert.Equal(2, result.SegmentPlacements.Count);
            Assert.Same(inner, result.SegmentPlacements[0].Activation);
            Assert.Equal(0, result.SegmentPlacements[0].LeafIndex);
            Assert.Equal(3, result.SegmentPlacements[0].RootIndexExclusive);
            Assert.Same(outer, result.SegmentPlacements[1].Activation);
            Assert.Equal(3, result.SegmentPlacements[1].LeafIndex);
            Assert.Equal(8, result.SegmentPlacements[1].RootIndexExclusive);
        }

        [Fact]
        public void MixedV2InnerAndV1Outer_PreservesUnclassifiedBridge()
        {
            var s = new Scenario();
            AsyncProfilerCallStack outer = s.Segment(AsyncCallstackKind.StateMachineAsync,
                frameCodeAddrs: new[] { 800, 801 }, frameMethods: new[] { 20, 21 });
            AsyncProfilerCallStack inner = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 700, 701 }, frameMethods: new[] { 10, 11 });

            var sync = new[]
            {
                s.Sync(50, 90),
                s.Sync(51, 10),
                s.Boundary(52, Wrapper(0)),
                s.Boundary(53, DispatchContinuation),
                s.Boundary(54, DispatchContinuation),
                s.Sync(55, 91),                       // unclassified scheduling frame
                s.Sync(56, 92),                       // native bridge
                s.Sync(57, 93),                       // synchronous bridge
                s.Sync(58, 20),
                s.Sync(59, 94),                       // V1 leaf transition
                s.Boundary(60, V1Dispatcher),
                s.Boundary(61, V1Infrastructure),
                s.Sync(62, 95),
                s.Sync(63, 96),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { outer, inner });

            Assert.Equal(new[]
            {
                Scenario.CA(50), Scenario.CA(51), Scenario.CA(701),
                Scenario.CA(55), Scenario.CA(56), Scenario.CA(57),
                Scenario.CA(58), Scenario.CA(801), Scenario.CA(62), Scenario.CA(63),
            }, result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(2, result.Diagnostics.V2PlumbingFramesCollapsed);
            Assert.Equal(1, result.Diagnostics.V1InfrastructureFramesCollapsed);
            Assert.Equal(1, result.Diagnostics.V1InlineFallbackUsed);
            Assert.Equal(0, result.Diagnostics.BoundariesNotFound);
        }

        [Fact]
        public void MixedV2InnerAndFullyCompletedV1Outer_RemovesCompletionChain()
        {
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.StateMachineAsync);
            AsyncProfilerCallStack outer = s.Segment(AsyncCallstackKind.StateMachineAsync,
                frameCodeAddrs: new[] { 800, 801 }, frameMethods: new[] { 20, 21 },
                completions: Completed(2));
            AsyncProfilerCallStack inner = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { -1, 700 }, frameMethods: new[] { 0, 10 });

            var sync = new[]
            {
                s.Sync(50, 90),                       // synchronous work in the resumed V2 parent
                s.Sync(51, 10),                       // V2 current = segment[1]
                s.Boundary(52, Wrapper(1)),
                s.Boundary(53, DispatchContinuation),
                s.Boundary(54, DispatchContinuation),
                s.Sync(55, 91),                       // Task.RunContinuations
                s.Sync(56, 21),                       // completed V1 MixedChildAsync
                s.Sync(57, 92),                       // V1 completion/dispatcher machinery
                s.Sync(58, 20),                       // completed V1 BurnCpuAsync
                s.Boundary(59, V1Dispatcher),
                s.Sync(60, 99),                       // ThreadPool dispatch anchor
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { outer, inner });

            Assert.Equal(new[] { Scenario.CA(50), Scenario.CA(51), Scenario.CA(60) },
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(2, result.Diagnostics.SegmentsProcessed);
            Assert.Equal(2, result.Diagnostics.V2PlumbingFramesCollapsed);
            Assert.Equal(0, result.Diagnostics.BoundariesNotFound);
        }

        [Fact]
        public void MixedV2InnerAndV1Outer_CompletionMarkerRemovesFinalV1Frame()
        {
            var s = new Scenario();
            AsyncProfilerCallStack outer = s.Segment(AsyncCallstackKind.StateMachineAsync,
                frameCodeAddrs: new[] { 800, 801 }, frameMethods: new[] { 20, 21 });
            AsyncProfilerCallStack inner = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { -1, 700 }, frameMethods: new[] { 0, 10 });

            var sync = new[]
            {
                s.Sync(50, 90),
                s.Sync(51, 10),
                s.Boundary(52, Wrapper(1)),
                s.Boundary(53, DispatchContinuation),
                s.Boundary(54, DispatchContinuation),
                s.Sync(55, 91, AsyncProfilerStitchSyncFrameKind.V1MethodBuilderCompletion),
                s.Sync(56, 21),
                s.Sync(57, 92),
                s.Sync(58, 20),
                s.Boundary(59, V1Dispatcher),
                s.Sync(60, 99),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { outer, inner });

            Assert.Equal(new[] { Scenario.CA(50), Scenario.CA(51), Scenario.CA(60) },
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(1, result.Diagnostics.V1InlineFallbackUsed);
            Assert.Equal(0, result.Diagnostics.BoundariesNotFound);

            AsyncProfilerStitchResult withoutCompletionProof = s.Run(sync, new[] { outer, inner },
                new StructuralRulesProfile(0, allowCompletion: false));
            Assert.Contains(withoutCompletionProof.Frames, frame =>
                frame.CodeAddress == Scenario.CA(56) && frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
        }

        [Fact]
        public void V1ResumedInlineAfterV2ContextCompleted_PreservesPhysicalV2CompletionChain()
        {
            var s = new Scenario();
            AsyncProfilerCallStack activeV1 = s.Segment(AsyncCallstackKind.StateMachineAsync,
                frameCodeAddrs: new[] { 500, 501 }, frameMethods: new[] { 10, 11 });

            var sync = new[]
            {
                s.Sync(50, 90),                       // synchronous work in the resumed V1 parent
                s.Sync(51, 10),                       // V1 current
                s.Boundary(52, V1Dispatcher),
                s.Boundary(53, V1Infrastructure),
                s.Sync(54, 91),                       // Task.RunContinuations / TrySetResult
                s.Boundary(55, DispatchContinuation), // V2 InstrumentedDispatchContinuations
                s.Boundary(56, DispatchContinuation), // V2 DispatchContinuations
                s.Sync(57, 99),                       // ThreadPool dispatch anchor
            };

            // The V2 context emits CompleteRuntimeAsyncContext before TrySetResult resumes the
            // V1 continuation inline. Its physical dispatcher is still on the stack, but its
            // interval is already closed and therefore must not be supplied as an active segment.
            AsyncProfilerStitchResult result = s.Run(sync, new[] { activeV1 });

            Assert.Equal(new[]
            {
                Scenario.CA(50), Scenario.CA(51), Scenario.CA(501),
                Scenario.CA(54), Scenario.CA(55), Scenario.CA(56), Scenario.CA(57),
            },
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(1, result.Diagnostics.SegmentsProcessed);
            Assert.Equal(1, result.Diagnostics.V1InlineFallbackUsed);
            Assert.Equal(1, result.Diagnostics.V1InfrastructureFramesCollapsed);
            Assert.Equal(0, result.Diagnostics.V2WrapperFallbackUsed);
            Assert.Equal(0, result.Diagnostics.V2PlumbingFramesCollapsed);
            Assert.Equal(0, result.Diagnostics.BoundariesNotFound);
        }

        [Fact]
        public void NestedV1Segments_PreserveUnclassifiedBridge()
        {
            var s = new Scenario();
            AsyncProfilerCallStack outer = s.Segment(AsyncCallstackKind.StateMachineAsync,
                frameCodeAddrs: new[] { 800, 801 }, frameMethods: new[] { 20, 21 });
            AsyncProfilerCallStack inner = s.Segment(AsyncCallstackKind.StateMachineAsync,
                frameCodeAddrs: new[] { 500, 501 }, frameMethods: new[] { 10, 11 });

            var sync = new[]
            {
                s.Sync(50, 90),
                s.Sync(51, 10),
                s.Sync(52, 91),
                s.Boundary(53, V1Dispatcher),
                s.Boundary(54, V1Infrastructure),
                s.Sync(55, 92),                       // unclassified scheduling frame
                s.Sync(56, 93),                       // native/synchronous bridge
                s.Sync(57, 20),
                s.Sync(58, 94),
                s.Boundary(59, V1Dispatcher),
                s.Boundary(60, V1Infrastructure),
                s.Sync(61, 95),
                s.Sync(62, 96),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { outer, inner });

            Assert.Equal(new[]
            {
                Scenario.CA(50), Scenario.CA(51), Scenario.CA(501),
                Scenario.CA(55), Scenario.CA(56), Scenario.CA(57), Scenario.CA(801),
                Scenario.CA(61), Scenario.CA(62),
            }, result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(2, result.Diagnostics.V1InfrastructureFramesCollapsed);
            Assert.Equal(2, result.Diagnostics.V1InlineFallbackUsed);
            Assert.Equal(0, result.Diagnostics.BoundariesNotFound);
        }

        [Fact]
        public void MixedV1AndV2Segments_ApplyCompletionHistoryIndependently()
        {
            var s = new Scenario();
            AsyncProfilerCallStack outer = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 800, 801, 802 }, frameMethods: new[] { 20, 21, 22 });
            AsyncProfilerCallStack inner = s.Segment(AsyncCallstackKind.StateMachineAsync,
                frameCodeAddrs: new[] { 500, 501, 502 }, frameMethods: new[] { 10, 11, 12 });

            var sync = new[]
            {
                s.Sync(50, 90),                       // inner synchronous leaf work
                s.Sync(51, 11),                       // inner current = segment[1]
                s.Sync(52, 91),                       // current transition machinery
                s.Sync(53, 10),                       // inner completed = segment[0]
                s.Sync(54, 92),                       // completed transition machinery
                s.Boundary(55, V1Dispatcher),
                s.Boundary(56, V1Infrastructure),     // known V1 box infrastructure
                s.Boundary(57, V1Infrastructure),     // known V1 box infrastructure
                s.Sync(58, 93),                       // unclassified scheduling frame: collapse stops
                s.Sync(59, 94),                       // native bridge frame
                s.Sync(60, 95),                       // synchronous bridge frame
                s.Sync(61, 21),                       // outer current = segment[1]
                s.Boundary(62, Wrapper(1)),           // outer completed count = 1
                s.Boundary(63, DispatchContinuation),
                s.Boundary(64, DispatchContinuation),
                s.Sync(65, 96),                       // ThreadPool dispatch anchor
                s.Sync(66, 97),                       // root
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { outer, inner });

            Assert.Equal(new[]
            {
                Scenario.CA(50), Scenario.CA(51), Scenario.CA(502),
                Scenario.CA(58), Scenario.CA(59), Scenario.CA(60), Scenario.CA(61), Scenario.CA(802),
                Scenario.CA(65), Scenario.CA(66),
            }, result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCurrent, result.Frames[1].Origin);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncRemaining, result.Frames[2].Origin);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncRemaining, result.Frames[7].Origin);
            Assert.Equal(2, result.Diagnostics.SegmentsProcessed);
            Assert.Equal(1, result.Diagnostics.V1InlineFallbackUsed);
            Assert.Equal(2, result.Diagnostics.V2PlumbingFramesCollapsed);
            Assert.Equal(2, result.Diagnostics.V1InfrastructureFramesCollapsed);
            Assert.Equal(0, result.Diagnostics.BoundariesNotFound);
        }

        [Fact]
        public void V2_StitchCollapsesDispatchContinuationPlumbing()
        {
            // Inside a resumed continuation body: the wrapper sits above the sampled leaf, so we stitch. The
            // dispatch-continuation plumbing below the wrapper (InstrumentedDispatchContinuations +
            // DispatchContinuations) is machinery the spliced ancestry already represents, so it is collapsed,
            // leaving the physical anchor (ThreadPoolWorkQueue.Dispatch) directly below the logical stack.
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            AsyncProfilerCallStack seg = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 200, 201 },   // current (leaf), suspended ancestry
                frameMethods:   new[] {  50,  51 },
                completions: Completed(0));           // current = frames[0]; splice frames[1]=201

            var sync = new[]
            {
                s.Sync(90, 50),                       // current (frames[0]), adjacent above the wrapper
                s.Boundary(91, Wrapper(0)),           // continuation wrapper (stitched -> dropped)
                s.Boundary(92, DispatchContinuation), // InstrumentedDispatchContinuations (collapsed)
                s.Boundary(93, DispatchContinuation), // DispatchContinuations (collapsed)
                s.Sync(94, 99),                       // Dispatch anchor
                s.Sync(95, 98),                       // root
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg });

            // Expected: [current(AsyncCurrent,90), ancestry(Async,201), Dispatch(Sync,94), root(Sync,95)].
            Assert.Equal(4, result.Frames.Count);
            Assert.Equal(Scenario.CA(90), result.Frames[0].CodeAddress);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCurrent, result.Frames[0].Origin);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncRemaining, result.Frames[1].Origin);
            Assert.Equal(Scenario.CA(201), result.Frames[1].CodeAddress);
            Assert.Equal(Scenario.CA(94), result.Frames[2].CodeAddress);
            Assert.Equal(Scenario.CA(95), result.Frames[3].CodeAddress);
            Assert.DoesNotContain(result.Frames, f => f.CodeAddress == Scenario.CA(92) || f.CodeAddress == Scenario.CA(93));
            Assert.Equal(2, result.Diagnostics.V2PlumbingFramesCollapsed);
            Assert.Equal(0, result.Diagnostics.V2SyncLayoutUsed);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void V2_WrapperIsLeaf_ConsumesWrapperAndProjectsDispatchTarget(bool methodEvents)
        {
            var s = new Scenario();
            if (methodEvents)
            {
                s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            }
            AsyncProfilerCallStack seg = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 300, 301 }, frameMethods: new[] { 60, 61 },
                completions: methodEvents ? Completed(0) : null);

            var sync = new[]
            {
                s.Boundary(80, Wrapper(0)),
                s.Boundary(81, DispatchContinuation),
                s.Sync(82, 99),                       // Dispatch anchor
                s.Sync(83, 98),                       // root
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg });

            Assert.Equal(
                new[] { Scenario.CA(300), Scenario.CA(301), Scenario.CA(82), Scenario.CA(83) },
                result.Frames.Select(f => f.CodeAddress));
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget, result.Frames[0].Origin);
            Assert.Same(seg, result.Frames[0].Context);
            Assert.Equal(AsyncProfilerStitchedFramePresentation.Native, result.Frames[0].Presentation);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncRemaining, result.Frames[1].Origin);
            Assert.DoesNotContain(result.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
            Assert.Equal(0, result.Diagnostics.V2SyncLayoutUsed);
            Assert.Equal(1, result.Diagnostics.V2LeafWrapperDropped);
            Assert.Equal(1, result.Diagnostics.SegmentsProcessed);
            Assert.Equal(1, result.Diagnostics.V2PlumbingFramesCollapsed);
            AsyncProfilerSegmentPlacement placement = Assert.Single(result.SegmentPlacements);
            Assert.Same(seg, placement.Activation);
            Assert.Equal(0, placement.LeafIndex);
            Assert.Equal(2, placement.RootIndexExclusive);

            AsyncProfilerStitchResult presented = new AsyncProfilerCallStackTransformPipeline().Transform(result, new[] { seg }, Qpc, s.Classify);
            Assert.Equal(result.Frames, presented.Frames);
            Assert.Equal(1, presented.Diagnostics.V2LeafWrapperDropped);
        }

        [Theory]
        [InlineData("slot", 1)]
        [InlineData("base", 1)]
        [InlineData("reset", 33)]
        [InlineData("method", 1)]
        [InlineData("method-and-unwind", 3)]
        [InlineData("wrapper-and-unwind", 3)]
        [InlineData("raw-ip", 1)]
        public void V2_WrapperIsLeaf_UsesVerifiedActivationPosition(string evidence, int expectedIndex)
        {
            var s = new Scenario();
            bool methodEvents = evidence == "method" || evidence == "method-and-unwind";
            if (methodEvents)
            {
                s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            }
            int slot = methodEvents ? 0 : evidence == "base" || evidence == "wrapper-and-unwind" ? 3 : 1;
            int[] addresses = Enumerable.Range(300, 36).ToArray();
            if (evidence == "raw-ip")
            {
                addresses[expectedIndex] = -1;
            }
            AsyncProfilerCallStack segment = s.Segment(AsyncCallstackKind.RuntimeAsync,
                addresses, Enumerable.Range(60, addresses.Length).ToArray(),
                completions: methodEvents ? Completed(1) : null,
                exceptionCompletions: evidence.EndsWith("-and-unwind", StringComparison.Ordinal) ? Unwound(2) : null,
                continuationIndexBase: evidence == "base" ? (byte)2 : (byte)0,
                wrapperResets: evidence == "reset" ? new[] { Qpc } : null,
                frameMethodIds: Enumerable.Range(1000, addresses.Length).Select(id => (ulong)id).ToArray());
            var sync = new[]
            {
                s.Boundary(80, Wrapper(slot)),
                s.Boundary(81, DispatchContinuation),
                s.Boundary(82, DispatchContinuation),
                s.Sync(999, 99),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { segment });

            AsyncProfilerStitchedFrame target = Assert.Single(result.Frames.Where(frame =>
                frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget));
            Assert.Equal(expectedIndex, target.SegmentFrameIndex);
            Assert.Equal(AsyncProfilerStitchedFramePresentation.Native, target.Presentation);
            Assert.Equal(MethodIndex.Invalid, target.Method);
            Assert.False(target.IsStateMachineBoxCarrier);
            Assert.Same(segment, target.Context);
            Assert.Equal(
                addresses.Skip(expectedIndex).Select(address =>
                        address < 0 ? CodeAddressIndex.Invalid : Scenario.CA(address))
                    .Concat(new[] { Scenario.CA(999) }),
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.All(result.Frames.Skip(1).Take(addresses.Length - expectedIndex - 1),
                frame => Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncRemaining, frame.Origin));
            Assert.DoesNotContain(result.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
            Assert.Equal(1, result.Diagnostics.SegmentsProcessed);
            Assert.Equal(methodEvents ? 0 : 1, result.Diagnostics.V2WrapperFallbackUsed);
            Assert.Equal(0, result.Diagnostics.AdjacencyMismatches);
            Assert.Equal(0, result.Diagnostics.V2SyncLayoutUsed);
            Assert.Equal(2, result.Diagnostics.V2PlumbingFramesCollapsed);
        }

        [Theory]
        [InlineData("unknown-target")]
        [InlineData("past-end")]
        [InlineData("before-base")]
        [InlineData("unknown-pool")]
        [InlineData("invalid-slot")]
        [InlineData("completed")]
        [InlineData("over-completed")]
        public void V2_WrapperIsLeaf_UnavailableTargetPreservesSyncLayout(string obstruction)
        {
            var s = new Scenario();
            bool methodEvents = obstruction == "completed" || obstruction == "over-completed";
            if (methodEvents)
            {
                s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            }
            int[] addresses = obstruction == "invalid-slot"
                ? Enumerable.Range(300, 36).ToArray() : new[] { 300, 301 };
            if (obstruction == "unknown-target")
            {
                addresses[0] = -1;
            }
            AsyncProfilerCallStack segment = s.Segment(AsyncCallstackKind.RuntimeAsync,
                addresses, Enumerable.Range(60, addresses.Length).ToArray(),
                completions: methodEvents ? Completed(obstruction == "completed" ? 2 : 3) : null,
                continuationIndexBase: obstruction == "before-base" ? (byte)1 : (byte)0,
                wrapperCount: obstruction == "unknown-pool" ? (byte)0 : (byte)32);
            var sync = new[]
            {
                s.Boundary(80, Wrapper(obstruction == "past-end" ? 2 : obstruction == "invalid-slot" ? 32 : 0)),
                s.Boundary(81, DispatchContinuation),
                s.Sync(999, 99),
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { segment });

            Assert.Equal(sync.Select(frame => frame.CodeAddress), result.Frames.Select(frame => frame.CodeAddress));
            Assert.All(result.Frames, frame =>
            {
                Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, frame.Origin);
                Assert.Null(frame.Context);
            });
            Assert.Empty(result.SegmentPlacements);
            Assert.Equal(1, result.Diagnostics.V2SyncLayoutUsed);
            Assert.Equal(0, result.Diagnostics.SegmentsProcessed);
        }

        [Fact]
        public void V2_NoWrapperInDispatchPlumbing_UsesSyncLayout()
        {
            // An active V2 segment, but the CPU is in dispatch-continuation plumbing with no wrapper above the leaf:
            // emit the sync layout unchanged (dispatch machinery kept); splice no ancestry.
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            AsyncProfilerCallStack seg = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 400, 401 }, frameMethods: new[] { 70, 71 }, completions: Completed(0));

            var sync = new[]
            {
                s.Boundary(84, DispatchContinuation), // InstrumentedDispatchContinuations (leaf)
                s.Boundary(85, DispatchContinuation), // DispatchContinuations
                s.Sync(86, 99),                       // Dispatch anchor
                s.Sync(87, 98),                       // root
            };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg });

            Assert.Equal(4, result.Frames.Count);
            Assert.All(result.Frames, f => Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, f.Origin));
            Assert.Equal(new[] { Scenario.CA(84), Scenario.CA(85), Scenario.CA(86), Scenario.CA(87) },
                result.Frames.Select(f => f.CodeAddress));
            Assert.Equal(1, result.Diagnostics.V2SyncLayoutUsed);
            Assert.Equal(0, result.Diagnostics.V2LeafWrapperDropped);
            Assert.Equal(0, result.Diagnostics.SegmentsProcessed);
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, false)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, true)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, false)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, true)]
        public void V2_InnerWrapperIsLeaf_PreservesEntryAndBridgeWhileStitchingOuterActivation(
            int outerKindValue, bool methodEvents)
        {
            var outerKind = (AsyncCallstackKind)outerKindValue;
            var s = new Scenario();
            if (methodEvents)
            {
                s.MarkMethodCompletionObserved(outerKind);
            }
            AsyncProfilerCallStack outer = s.Segment(outerKind,
                new[] { 500, 501, 502 }, new[] { 20, 21, 22 },
                completions: methodEvents ? Completed(1) : null);
            AsyncProfilerCallStack inner = s.Segment(AsyncCallstackKind.RuntimeAsync,
                new[] { -1, -1, 602 }, new[] { -1, -1, 32 });
            var sync = new List<AsyncProfilerStitchSyncFrame>
            {
                s.Boundary(80, Wrapper(1)),
                s.Boundary(81, DispatchContinuation),
                s.Boundary(82, DispatchContinuation),
                s.Sync(83, 90),
                s.Sync(84, 91),
                s.Sync(85, 92),
                s.Sync(86, 21),
            };
            if (outerKind == AsyncCallstackKind.StateMachineAsync)
            {
                sync.Add(s.Sync(87, 20));
            }
            sync.Add(s.Boundary(88, outerKind == AsyncCallstackKind.RuntimeAsync
                ? Wrapper(methodEvents ? 0 : 1) : V1Dispatcher));
            if (outerKind == AsyncCallstackKind.RuntimeAsync)
            {
                sync.Add(s.Boundary(89, DispatchContinuation));
                sync.Add(s.Boundary(90, DispatchContinuation));
            }
            sync.Add(s.Sync(91, 99));

            AsyncProfilerStitchResult result = s.Run(sync, new[] { outer, inner }, trace: true);

            Assert.Equal(
                new[] { 80, 81, 82, 83, 84, 85, 86, 502, 91 }.Select(Scenario.CA),
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.All(result.Frames.Take(3), frame => Assert.Null(frame.Context));
            Assert.All(result.Frames.Skip(3).Take(3), frame =>
            {
                Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, frame.Origin);
                Assert.Same(outer, frame.Context);
            });
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCurrent, result.Frames[6].Origin);
            Assert.Equal(1, result.Frames[6].SegmentFrameIndex);
            Assert.Same(outer, result.Frames[6].Context);
            Assert.DoesNotContain(result.Frames, frame => ReferenceEquals(frame.Context, inner));
            Assert.Equal(2, result.Diagnostics.SegmentsProcessed);
            Assert.Equal(1, result.Diagnostics.V2SyncLayoutUsed);
            Assert.Equal(outerKind == AsyncCallstackKind.RuntimeAsync ? 2 : 0,
                result.Diagnostics.V2PlumbingFramesCollapsed);
            Assert.Equal(0, result.Diagnostics.BoundariesNotFound);
            AsyncProfilerSegmentPlacement placement = Assert.Single(result.SegmentPlacements);
            Assert.Same(outer, placement.Activation);
            Assert.Equal(3, placement.LeafIndex);
            Assert.Equal(8, placement.RootIndexExclusive);

            var pipeline = new AsyncProfilerCallStackTransformPipeline();
            AsyncProfilerStitchResult presented = pipeline.Transform(result, new[] { outer, inner }, Qpc, s.Classify);
            Assert.Equal(result.Frames.Skip(1), presented.Frames);
            Assert.Equal(1, presented.Diagnostics.V2LeafWrapperDropped);
        }

        [Theory]
        [InlineData(0, false)]
        [InlineData(0, true)]
        [InlineData(1, false)]
        [InlineData(1, true)]
        [InlineData(2, false)]
        [InlineData(2, true)]
        [InlineData(3, false)]
        [InlineData(3, true)]
        [InlineData(4, false)]
        [InlineData(4, true)]
        [InlineData(5, false)]
        [InlineData(5, true)]
        [InlineData(6, false)]
        [InlineData(6, true)]
        [InlineData(7, false)]
        [InlineData(7, true)]
        public void V2_InnerWrapperIsLeaf_StitchesMultipleMixedOuterActivations(int protocols, bool methodEvents)
        {
            var s = new Scenario();
            var segments = new AsyncProfilerCallStack[4];
            if (methodEvents)
            {
                s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            }
            int v1Count = 0;
            for (int i = 0; i < 3; i++)
            {
                AsyncCallstackKind kind = (protocols & (1 << i)) == 0
                    ? AsyncCallstackKind.RuntimeAsync : AsyncCallstackKind.StateMachineAsync;
                if (methodEvents)
                {
                    s.MarkMethodCompletionObserved(kind);
                }
                if (kind == AsyncCallstackKind.StateMachineAsync)
                {
                    v1Count++;
                }
                int address = 500 + i * 10;
                int method = 20 + i * 10;
                segments[i] = s.Segment(kind,
                    new[] { address, address + 1, address + 2 }, new[] { method, method + 1, method + 2 },
                    completions: methodEvents ? Completed(1) : null);
            }
            segments[3] = s.Segment(AsyncCallstackKind.RuntimeAsync,
                new[] { -1, 901, 902 }, new[] { -1, 200, 201 },
                completions: methodEvents ? Completed(1) : null);
            var sync = new List<AsyncProfilerStitchSyncFrame>
            {
                s.Boundary(80, Wrapper(methodEvents ? 0 : 1)),
                s.Boundary(81, DispatchContinuation),
                s.Boundary(82, DispatchContinuation),
            };
            var expected = new List<CodeAddressIndex> { Scenario.CA(901), Scenario.CA(902) };
            for (int i = 2; i >= 0; i--)
            {
                int address = 100 + i * 10;
                int method = 20 + i * 10;
                sync.Add(s.Sync(address, 90 + i));
                sync.Add(s.Sync(address + 1, method + 1));
                bool v1 = segments[i].Frames.Kind == AsyncCallstackKind.StateMachineAsync;
                if (v1)
                {
                    sync.Add(s.Sync(address + 2, method));
                }
                sync.Add(s.Boundary(address + 3, v1 ? V1Dispatcher : Wrapper(methodEvents ? 0 : 1)));
                if (!v1)
                {
                    sync.Add(s.Boundary(address + 4, DispatchContinuation));
                    sync.Add(s.Boundary(address + 5, DispatchContinuation));
                }
                expected.Add(Scenario.CA(address));
                expected.Add(Scenario.CA(address + 1));
                expected.Add(Scenario.CA(502 + i * 10));
            }
            sync.Add(s.Sync(999, 400));
            expected.Add(Scenario.CA(999));

            AsyncProfilerStitchResult result = s.Run(sync, segments);

            Assert.Equal(expected, result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(4, result.Diagnostics.SegmentsProcessed);
            Assert.Equal(0, result.Diagnostics.V2SyncLayoutUsed);
            Assert.Equal((4 - v1Count) * 2, result.Diagnostics.V2PlumbingFramesCollapsed);
            Assert.Equal(methodEvents ? 0 : v1Count, result.Diagnostics.V1InlineFallbackUsed);
            Assert.Equal(0, result.Diagnostics.BoundariesNotFound);
            Assert.Equal(0, result.Diagnostics.AdjacencyMismatches);
            Assert.Equal(0, result.Diagnostics.IncompleteAsyncRootsNormalized);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget, result.Frames[0].Origin);
            Assert.Equal(1, result.Frames[0].SegmentFrameIndex);
            Assert.Same(segments[3], result.Frames[0].Context);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncRemaining, result.Frames[1].Origin);
            Assert.Equal(4, result.SegmentPlacements.Count);
            Assert.Same(segments[3], result.SegmentPlacements[0].Activation);
            Assert.Equal(0, result.SegmentPlacements[0].LeafIndex);
            Assert.Equal(2, result.SegmentPlacements[0].RootIndexExclusive);
            for (int i = 0; i < 3; i++)
            {
                AsyncProfilerCallStack activation = segments[2 - i];
                AsyncProfilerStitchedFrame current = result.Frames[3 + i * 3];
                Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCurrent, current.Origin);
                Assert.Equal(1, current.SegmentFrameIndex);
                Assert.Same(activation, current.Context);
                AsyncProfilerSegmentPlacement placement = result.SegmentPlacements[i + 1];
                Assert.Same(activation, placement.Activation);
                Assert.Equal(2 + i * 3, placement.LeafIndex);
                Assert.Equal(5 + i * 3, placement.RootIndexExclusive);
            }
        }

        [Theory]
        [InlineData("missing-wrapper")]
        [InlineData("unresolved-current")]
        [InlineData("mismatched-current")]
        [InlineData("completed-outer")]
        public void V2_InnerWrapperIsLeaf_UnverifiedOuterActivationPreservesSyncLayout(string obstruction)
        {
            var s = new Scenario();
            bool completed = obstruction == "completed-outer";
            if (completed)
            {
                s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            }
            AsyncProfilerCallStack outer = s.Segment(AsyncCallstackKind.RuntimeAsync,
                new[] { 500, 501 }, new[] { 20, 21 },
                completions: completed ? Completed(2) : null);
            AsyncProfilerCallStack inner = s.Segment(AsyncCallstackKind.RuntimeAsync,
                new[] { -1, -1 }, new[] { -1, -1 });
            var sync = new List<AsyncProfilerStitchSyncFrame>
            {
                s.Boundary(80, Wrapper(1)),
                s.Boundary(81, DispatchContinuation),
                s.Sync(82, 90),
                new AsyncProfilerStitchSyncFrame(Scenario.CA(83), obstruction == "unresolved-current"
                    ? MethodIndex.Invalid : Scenario.MI(obstruction == "mismatched-current" ? 99 : 20)),
            };
            if (obstruction != "missing-wrapper")
            {
                sync.Add(s.Boundary(84, Wrapper(0)));
            }
            sync.Add(s.Sync(85, 98));

            AsyncProfilerStitchResult result = s.Run(sync, new[] { outer, inner });

            Assert.Equal(sync.Select(frame => frame.CodeAddress), result.Frames.Select(frame => frame.CodeAddress));
            Assert.All(result.Frames, frame => Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, frame.Origin));
            Assert.Equal(1, result.Diagnostics.V2SyncLayoutUsed);
            Assert.Equal(0, result.Diagnostics.SegmentsProcessed);
        }

        [Fact]
        public void Trace_Disabled_ByDefault_EmitsNoHappyPathMessages()
        {
            // A clean V2 segment (boundary found, no anomalies) must not add any Messages when trace is off.
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            AsyncProfilerCallStack seg = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 200, 201, 202, 203, 204 },
                frameMethods:   new[] {  50,  51,  52,  53,  54 },
                completions: Completed(2));
            var sync = new[] { s.Sync(90, 52), s.Boundary(91, Wrapper(3)), s.Sync(92, 99) };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg }); // trace defaults to off

            Assert.Empty(result.Diagnostics.Messages);
        }

        [Fact]
        public void Trace_Enabled_EmitsPerSegmentHappyPathNote()
        {
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            AsyncProfilerCallStack seg = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 200, 201, 202, 203, 204 },
                frameMethods:   new[] {  50,  51,  52,  53,  54 },
                completions: Completed(2));
            var sync = new[] { s.Sync(90, 52), s.Boundary(91, Wrapper(3)), s.Sync(92, 99) };

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg }, trace: true);

            string note = Assert.Single(result.Diagnostics.Messages);
            Assert.Contains("segment[0]", note);
            Assert.Contains($"kind={AsyncCallstackKind.RuntimeAsync}", note);
            Assert.Contains($"{AsyncProfilerCallStackStitchBoundaryKind.V2ContinuationWrapper}[3]", note); // boundary + wrapper index
            Assert.Contains("completed=2", note);
            Assert.Contains("spliced=[3..5)", note);                                       // ancestry A1,A2
        }

        [Fact]
        public void Trace_Enabled_NotesMissingBoundary()
        {
            // When no boundary is found the happy-path note still fires (alongside the anomaly note) and reflects it.
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            AsyncProfilerCallStack seg = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 600, 601 }, frameMethods: new[] { 70, 71 }, completions: Completed(0));
            var sync = new[] { s.Sync(10, 1), s.Sync(11, 2) }; // no boundary frame at all

            AsyncProfilerStitchResult result = s.Run(sync, new[] { seg }, trace: true);

            Assert.Equal(1, result.Diagnostics.BoundariesNotFound);
            Assert.Contains(result.Diagnostics.Messages, m => m.Contains("segment[0]") && m.Contains("boundary=no-boundary"));
        }

        [Fact]
        public void Diagnostics_BoundsRetainedMessages()
        {
            var diagnostics = new AsyncProfilerCallStackDiagnostics();

            for (int i = 0; i < 1_100; i++)
            {
                diagnostics.Note("message " + i);
            }

            Assert.Equal(1_024, diagnostics.Messages.Count);
            Assert.Equal(76, diagnostics.MessagesDropped);
        }

        [Fact]
        public void Diagnostics_ClearAndAddToCoverEveryCounter()
        {
            System.Reflection.FieldInfo[] counters = typeof(AsyncProfilerCallStackDiagnostics).GetFields()
                .Where(field => field.IsPublic && !field.IsStatic && field.FieldType == typeof(int))
                .ToArray();
            var source = new AsyncProfilerCallStackDiagnostics();
            foreach (System.Reflection.FieldInfo counter in counters)
            {
                counter.SetValue(source, 1);
            }

            var target = new AsyncProfilerCallStackDiagnostics();
            source.AddTo(target);
            Assert.All(counters, counter => Assert.Equal(1, counter.GetValue(target)));

            source.Clear();
            Assert.All(counters, counter => Assert.Equal(0, counter.GetValue(source)));
        }
    }
}
