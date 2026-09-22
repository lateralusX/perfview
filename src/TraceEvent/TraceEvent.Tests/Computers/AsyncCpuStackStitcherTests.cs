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
    /// Unit tests for the pure stitch core <see cref="AsyncCpuStackStitcher"/>. They drive the delegate-based
    /// <c>Stitch</c> overload with synthetic <see cref="StitchSyncFrame"/>s and synthetic
    /// <see cref="AsyncCallStack"/> segments (built via the internals-visible constructors), plus
    /// dictionary-backed <c>classify</c> / <c>completionObserved</c> / <c>methodOf</c> delegates, so no real
    /// symbol table or <c>TraceLog</c> is required.
    /// <para>
    /// Identity model: <see cref="CodeAddressIndex"/> and <see cref="MethodIndex"/> are treated as opaque
    /// integer handles. A sync frame carries both directly; an async frame carries a code address whose
    /// <c>MethodIndex</c> is resolved through the <c>methodOf</c> map (or <see cref="CodeAddressIndex.Invalid"/>
    /// for a 0-IP frame).
    /// </para>
    /// </summary>
    public class AsyncCpuStackStitcherTests
    {
        private const long Qpc = 1000;

        // A tiny builder for a synthetic async segment and its per-frame identity/boundary wiring.
        private sealed class Scenario
        {
            private readonly Dictionary<CodeAddressIndex, MethodIndex> _methodOf = new Dictionary<CodeAddressIndex, MethodIndex>();
            private readonly Dictionary<CodeAddressIndex, AsyncStitchBoundaryInfo> _classify = new Dictionary<CodeAddressIndex, AsyncStitchBoundaryInfo>();
            private readonly HashSet<AsyncCallstackKind> _methodCompletionObserved = new HashSet<AsyncCallstackKind>();

            public Func<CodeAddressIndex, MethodIndex> MethodOf => ca => _methodOf.TryGetValue(ca, out MethodIndex m) ? m : MethodIndex.Invalid;
            public Func<CodeAddressIndex, AsyncStitchBoundaryInfo> Classify => ca => _classify.TryGetValue(ca, out AsyncStitchBoundaryInfo b) ? b : AsyncStitchBoundaryInfo.None;
            public Func<AsyncCallstackKind, bool> MethodCompletionObserved => k => _methodCompletionObserved.Contains(k);

            public static CodeAddressIndex CA(int i) => (CodeAddressIndex)i;
            public static MethodIndex MI(int i) => (MethodIndex)i;

            public void MarkMethodCompletionObserved(AsyncCallstackKind kind) => _methodCompletionObserved.Add(kind);

            /// <summary>Declares a sync frame with a code address whose method is <paramref name="method"/>.</summary>
            public StitchSyncFrame Sync(int codeAddr, int method) => new StitchSyncFrame(CA(codeAddr), MI(method));

            /// <summary>Declares a sync boundary frame classified as <paramref name="info"/>.</summary>
            public StitchSyncFrame Boundary(int codeAddr, AsyncStitchBoundaryInfo info)
            {
                _classify[CA(codeAddr)] = info;
                return new StitchSyncFrame(CA(codeAddr), MI(codeAddr));
            }

            /// <summary>
            /// Builds a synthetic async segment (leaf-first frames). Each entry is a code address; a negative
            /// value means <see cref="CodeAddressIndex.Invalid"/> (a 0-IP frame). The <paramref name="frameMethods"/>
            /// (same length) wire each async code address to its <c>MethodIndex</c> via the <c>methodOf</c> map.
            /// </summary>
            public AsyncCallStack Segment(AsyncCallstackKind kind, int[] frameCodeAddrs, int[] frameMethods,
                AsyncCallStack.CompletionDelta[] completions = null, AsyncCallStack.CompletionDelta[] exceptionCompletions = null,
                byte continuationIndexBase = 0, byte wrapperCount = 32)
            {
                int n = frameCodeAddrs.Length;
                var methodIds = new ulong[n];
                var frames = new AsyncCallStackFrames(kind, methodIds, null);
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

                return new AsyncCallStack(depth: 0, framesIndex: (AsyncCallStackFramesIndex)0, frames: frames,
                    continuationIndexBase: continuationIndexBase, wrapperCount: wrapperCount, startQpc: 0, endQpc: long.MaxValue,
                    methodCompletions: completions ?? Array.Empty<AsyncCallStack.CompletionDelta>(),
                    exceptionCompletions: exceptionCompletions ?? Array.Empty<AsyncCallStack.CompletionDelta>(),
                    wrapperResets: Array.Empty<long>());
            }

            public StitchResult Run(IReadOnlyList<StitchSyncFrame> sync, IReadOnlyList<AsyncCallStack> segmentsRootToLeaf) =>
                AsyncCpuStackStitcher.Stitch(sync, segmentsRootToLeaf, Qpc, Classify, MethodCompletionObserved, MethodOf);

            public StitchResult Run(IReadOnlyList<StitchSyncFrame> sync, IReadOnlyList<AsyncCallStack> segmentsRootToLeaf, bool trace) =>
                AsyncCpuStackStitcher.Stitch(sync, segmentsRootToLeaf, Qpc, Classify, MethodCompletionObserved, MethodOf, trace);
        }

        private static AsyncStitchBoundaryInfo Wrapper(int index) => new AsyncStitchBoundaryInfo(AsyncStitchBoundaryKind.V2ContinuationWrapper, index);
        private static readonly AsyncStitchBoundaryInfo DispatchContinuation = new AsyncStitchBoundaryInfo(AsyncStitchBoundaryKind.V2DispatchContinuation, -1);
        private static readonly AsyncStitchBoundaryInfo V1Dispatcher = new AsyncStitchBoundaryInfo(AsyncStitchBoundaryKind.V1Dispatcher, -1);
        private static readonly AsyncStitchBoundaryInfo V1Infrastructure =
            new AsyncStitchBoundaryInfo(AsyncStitchBoundaryKind.V1DispatcherInfrastructure, -1);

        private static AsyncCallStack.CompletionDelta[] Completed(int count) =>
            new[] { new AsyncCallStack.CompletionDelta(Qpc, count) };

        private static AsyncCallStack.CompletionDelta[] Unwound(int frames) =>
            new[] { new AsyncCallStack.CompletionDelta(Qpc, frames) };

        [Fact]
        public void NoSegments_IsVerbatimPassthrough()
        {
            var s = new Scenario();
            var sync = new[] { s.Sync(10, 1), s.Sync(11, 2), s.Sync(12, 3) };

            StitchResult result = s.Run(sync, Array.Empty<AsyncCallStack>());

            Assert.Equal(3, result.Frames.Count);
            Assert.All(result.Frames, f => Assert.Equal(StitchedFrameOrigin.Sync, f.Origin));
            Assert.Equal(new[] { Scenario.CA(10), Scenario.CA(11), Scenario.CA(12) }, result.Frames.Select(f => f.CodeAddress));
            Assert.Equal(0, result.Diagnostics.SegmentsProcessed);
            Assert.Equal(0, result.Diagnostics.BoundariesNotFound);
        }

        [Fact]
        public void StitchInto_ReusesAndClearsCallerOwnedBuffers()
        {
            var s = new Scenario();
            AsyncCallStack segment = s.Segment(
                AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 200, 201 },
                frameMethods: new[] { 50, 51 });
            var stitchedSync = new[]
            {
                s.Sync(10, 50),
                s.Boundary(11, Wrapper(0)),
                s.Sync(12, 99),
            };
            var output = new List<StitchedFrame>();
            var diagnostics = new StitchDiagnostics();
            long completionPolicyQpc = long.MinValue;

            AsyncCpuStackStitcher.StitchInto(
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
            AsyncCpuStackStitcher.StitchInto(
                passthroughSync,
                Array.Empty<AsyncCallStack>(),
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

            StitchResult result = s.Run(sync, Array.Empty<AsyncCallStack>());

            Assert.Equal(4, result.Frames.Count);
            Assert.All(result.Frames, f => Assert.Equal(StitchedFrameOrigin.Sync, f.Origin));
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

            StitchResult result = s.Run(sync, Array.Empty<AsyncCallStack>());

            Assert.Equal(4, result.Frames.Count);
            Assert.All(result.Frames, f => Assert.Equal(StitchedFrameOrigin.Sync, f.Origin));
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

            StitchResult result = s.Run(sync, Array.Empty<AsyncCallStack>());

            Assert.Equal(3, result.Frames.Count);
            Assert.Equal(new[] { Scenario.CA(84), Scenario.CA(85), Scenario.CA(86) }, result.Frames.Select(f => f.CodeAddress));
            Assert.Equal(0, result.Diagnostics.SegmentsProcessed);
        }

        [Fact]
        public void EmptySegment_PreservesPhysicalSyncBoundaries()
        {
            var s = new Scenario();
            AsyncCallStack empty = s.Segment(
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

            StitchResult result = s.Run(sync, new[] { empty });

            Assert.Equal(4, result.Frames.Count);
            Assert.All(result.Frames, frame => Assert.Equal(StitchedFrameOrigin.Sync, frame.Origin));
            Assert.Equal(new[] { Scenario.CA(70), Scenario.CA(71), Scenario.CA(72), Scenario.CA(73) },
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(0, result.Diagnostics.SegmentsProcessed);
        }

        [Fact]
        public void V2_CompletionEvents_SplicesRemainingAncestryAndKeepsCurrentFromSync()
        {
            // Segment (leaf-first): [C0, C1, Mcur, A1, A2]; completed=2 -> current=Mcur (frames[2]); ancestry=A1,A2.
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            AsyncCallStack seg = s.Segment(AsyncCallstackKind.RuntimeAsync,
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

            StitchResult result = s.Run(sync, new[] { seg });

            // Expected: [Mcur(Sync), A1(Async), A2(Async), root(Sync)].
            Assert.Equal(4, result.Frames.Count);
            Assert.Equal(StitchedFrameOrigin.Sync, result.Frames[0].Origin);
            Assert.Equal(Scenario.CA(90), result.Frames[0].CodeAddress);           // current came from sync
            Assert.Equal(StitchedFrameOrigin.AsyncRemaining, result.Frames[1].Origin);
            Assert.Equal(Scenario.CA(203), result.Frames[1].CodeAddress);          // A1
            Assert.Equal(StitchedFrameOrigin.AsyncRemaining, result.Frames[2].Origin);
            Assert.Equal(Scenario.CA(204), result.Frames[2].CodeAddress);          // A2
            Assert.Equal(StitchedFrameOrigin.Sync, result.Frames[3].Origin);
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
            AsyncCallStack seg = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 300, 301, 302 },
                frameMethods:   new[] {  60,  61,  62 });

            var sync = new[]
            {
                s.Sync(80, 61),                 // current (frames[1]) adjacent above the wrapper
                s.Boundary(81, Wrapper(1)),     // wrapper slot 1 -> completed = 1
                s.Sync(82, 99),                 // root
            };

            StitchResult result = s.Run(sync, new[] { seg });

            // completed=1 -> current=frames[1]; splice frames[2]=302.
            Assert.Equal(3, result.Frames.Count);
            Assert.Equal(Scenario.CA(80), result.Frames[0].CodeAddress);
            Assert.Equal(StitchedFrameOrigin.AsyncRemaining, result.Frames[1].Origin);
            Assert.Equal(Scenario.CA(302), result.Frames[1].CodeAddress);
            Assert.Equal(Scenario.CA(82), result.Frames[2].CodeAddress);
            Assert.Equal(1, result.Diagnostics.V2WrapperFallbackUsed);
            Assert.Equal(0, result.Diagnostics.AdjacencyMismatches);
        }

        [Fact]
        public void V2_AdjacencyMismatch_IsRecordedAsSoftDiagnostic()
        {
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            AsyncCallStack seg = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 400, 401 },
                frameMethods:   new[] {  70,  71 },
                completions: Completed(0)); // current = frames[0] (method 70)

            var sync = new[]
            {
                s.Sync(70, 999),                 // adjacent-above method 999 != frames[0] method 70 -> mismatch
                s.Boundary(71, Wrapper(0)),
                s.Sync(72, 99),
            };

            StitchResult result = s.Run(sync, new[] { seg });

            Assert.Equal(1, result.Diagnostics.AdjacencyMismatches);
        }

        [Fact]
        public void V1_OrderedMatch_CountsCompletedInlineFrames()
        {
            // Inline-resume cascade. Sync (leaf-first): A(current), B, C, dispatcher, root.
            // Async (leaf-first): [C, B, A, P1]; C,B completed -> completed=2; current=A(frames[2]); ancestry=P1.
            var s = new Scenario();
            AsyncCallStack seg = s.Segment(AsyncCallstackKind.StateMachineAsync,
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

            StitchResult result = s.Run(sync, new[] { seg });

            // Expected: [A(AsyncCurrent), P1(Async), root(Sync)]. Completed B/C and their physical
            // transition chain are replaced by the logical ancestry.
            Assert.Equal(3, result.Frames.Count);
            Assert.Equal(StitchedFrameOrigin.AsyncCurrent, result.Frames[0].Origin);
            Assert.Equal(Scenario.CA(40), result.Frames[0].CodeAddress);
            Assert.Equal(StitchedFrameOrigin.AsyncRemaining, result.Frames[1].Origin);
            Assert.Equal(Scenario.CA(503), result.Frames[1].CodeAddress); // P1 ancestry
            Assert.Equal(Scenario.CA(44), result.Frames[2].CodeAddress);  // root
            Assert.Equal(1, result.Diagnostics.V1InlineFallbackUsed);
            Assert.Equal(0, result.Diagnostics.BoundariesNotFound);
        }

        [Fact]
        public void V1_CollapsesContiguousBoxInfrastructureRootwardOfDispatcher()
        {
            var s = new Scenario();
            AsyncCallStack seg = s.Segment(AsyncCallstackKind.StateMachineAsync,
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

            StitchResult result = s.Run(sync, new[] { seg });

            Assert.Equal(new[] { Scenario.CA(40), Scenario.CA(501), Scenario.CA(44), Scenario.CA(45) },
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(StitchedFrameOrigin.AsyncCurrent, result.Frames[0].Origin);
            Assert.Equal(StitchedFrameOrigin.AsyncRemaining, result.Frames[1].Origin);
            Assert.Equal(2, result.Diagnostics.V1InfrastructureFramesCollapsed);
        }

        [Fact]
        public void V1_ZeroIpCurrentFrame_ResolvesFromSyncLeaf_NotSpliced()
        {
            // Regression: the current (running) V1 frame has a 0-IP on the async side. Its identity must come
            // from the physical sync leaf, and it must NOT be spliced back in as an Invalid async frame.
            var s = new Scenario();
            AsyncCallStack seg = s.Segment(AsyncCallstackKind.StateMachineAsync,
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

            StitchResult result = s.Run(sync, new[] { seg });

            // completed must still be 2 (C,B); current A resolved from sync, ancestry P1 spliced.
            Assert.Equal(5, result.Frames.Count);
            Assert.Equal(StitchedFrameOrigin.Sync, result.Frames[0].Origin);
            Assert.Equal(Scenario.CA(40), result.Frames[0].CodeAddress);        // real sync leaf, not Invalid
            Assert.Equal(StitchedFrameOrigin.AsyncRemaining, result.Frames[3].Origin);
            Assert.Equal(Scenario.CA(503), result.Frames[3].CodeAddress);       // P1
            // No spliced async frame is the Invalid 0-IP current frame.
            Assert.DoesNotContain(result.Frames.Where(f => f.Origin == StitchedFrameOrigin.AsyncRemaining),
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
            AsyncCallStack seg = s.Segment(AsyncCallstackKind.StateMachineAsync,
                frameCodeAddrs: new[] { 500, 501, 502 },   // Mcur, A1, A2
                frameMethods:   new[] {  30,  31,  32 },
                completions: Completed(1));                 // one normal completion via events

            var sync = new[]
            {
                s.Sync(40, 31),                 // current -> frames[1] (method 31)
                s.Boundary(43, V1Dispatcher),
                s.Sync(44, 99),
            };

            StitchResult result = s.Run(sync, new[] { seg });

            // Expected: [current(Sync), A2(Async), root(Sync)]. The stack scan is not used (no V1InlineFallbackUsed).
            Assert.Equal(3, result.Frames.Count);
            Assert.Equal(Scenario.CA(40), result.Frames[0].CodeAddress);
            Assert.Equal(StitchedFrameOrigin.AsyncRemaining, result.Frames[1].Origin);
            Assert.Equal(Scenario.CA(502), result.Frames[1].CodeAddress); // A2 ancestry
            Assert.Equal(Scenario.CA(44), result.Frames[2].CodeAddress);
            Assert.Equal(0, result.Diagnostics.V1InlineFallbackUsed);
        }

        [Fact]
        public void V1_ExceptionUnwind_IsAddedToInlineStackScan()
        {
            // No CompleteMethod events, but an Unwind (exception) event: the exceptional frame left the sync stack,
            // so it is counted from the event and ADDED to the inline stack scan. Segment (leaf-first): [C, B, A, P1];
            // C completed inline (on the sync stack) -> scan=1; B unwound exceptionally -> +1; completed=2 ->
            // current=frames[2]=A; ancestry=P1.
            var s = new Scenario();
            AsyncCallStack seg = s.Segment(AsyncCallstackKind.StateMachineAsync,
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

            StitchResult result = s.Run(sync, new[] { seg });

            // Expected: [A(AsyncCurrent), P1(Async), root(Sync)]. C completed inline and B unwound, so both
            // physical transition frames are replaced by the logical ancestry.
            Assert.Equal(3, result.Frames.Count);
            Assert.Equal(StitchedFrameOrigin.AsyncCurrent, result.Frames[0].Origin);
            Assert.Equal(Scenario.CA(40), result.Frames[0].CodeAddress);
            Assert.Equal(StitchedFrameOrigin.AsyncRemaining, result.Frames[1].Origin);
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
            AsyncCallStack seg = s.Segment(AsyncCallstackKind.StateMachineAsync,
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

            StitchResult result = s.Run(sync, new[] { seg });

            Assert.Equal(new[] { Scenario.CA(40), Scenario.CA(503), Scenario.CA(43) },
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(StitchedFrameOrigin.AsyncCurrent, result.Frames[0].Origin);
            Assert.Equal(StitchedFrameOrigin.AsyncRemaining, result.Frames[1].Origin);
            Assert.Equal(1, result.Diagnostics.V1InlineFallbackUsed);
        }

        [Fact]
        public void V1_UnresolvedExceptionBeforeInlineCompletion_AlignsPastUnwoundFrame()
        {
            // The exceptionally unwound frame has no code-address identity. Its Unwind event still proves that one
            // leading async frame may be skipped before matching the normally completed B and current C frames.
            var s = new Scenario();
            AsyncCallStack seg = s.Segment(AsyncCallstackKind.StateMachineAsync,
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

            StitchResult result = s.Run(sync, new[] { seg });

            Assert.Equal(new[] { Scenario.CA(40), Scenario.CA(503), Scenario.CA(43) },
                result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(StitchedFrameOrigin.AsyncCurrent, result.Frames[0].Origin);
            Assert.Equal(StitchedFrameOrigin.AsyncRemaining, result.Frames[1].Origin);
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
            AsyncCallStack seg = s.Segment(AsyncCallstackKind.StateMachineAsync,
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

            StitchResult result = s.Run(sync, new[] { seg });

            Assert.Equal(3, result.Frames.Count);
            Assert.Equal(Scenario.CA(40), result.Frames[0].CodeAddress);
            Assert.Equal(StitchedFrameOrigin.AsyncRemaining, result.Frames[1].Origin);
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
            AsyncCallStack seg = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 300, 301, 302 },
                frameMethods:   new[] {  60,  61,  62 },
                exceptionCompletions: Unwound(1));   // must be ignored on the wrapper path

            var sync = new[]
            {
                s.Sync(80, 61),                 // current (frames[1]) adjacent above the wrapper
                s.Boundary(81, Wrapper(1)),     // wrapper slot 1 -> completed = 1
                s.Sync(82, 99),
            };

            StitchResult result = s.Run(sync, new[] { seg });

            // completed=1 (wrapper slot only) -> current=frames[1]; splice frames[2]=302.
            Assert.Equal(3, result.Frames.Count);
            Assert.Equal(Scenario.CA(80), result.Frames[0].CodeAddress);
            Assert.Equal(StitchedFrameOrigin.AsyncRemaining, result.Frames[1].Origin);
            Assert.Equal(Scenario.CA(302), result.Frames[1].CodeAddress);
            Assert.Equal(Scenario.CA(82), result.Frames[2].CodeAddress);
            Assert.Equal(1, result.Diagnostics.V2WrapperFallbackUsed);
        }

        [Fact]
        public void MissingBoundary_DegradesToAppendOnly_WithDiagnostic()
        {
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            AsyncCallStack seg = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 600, 601 },
                frameMethods:   new[] {  80,  81 },
                completions: Completed(0)); // current=frames[0]; ancestry=frames[1]

            var sync = new[] { s.Sync(10, 1), s.Sync(11, 2) }; // no boundary frame present

            StitchResult result = s.Run(sync, new[] { seg });

            // All sync frames verbatim, then the remaining ancestry appended.
            Assert.Equal(3, result.Frames.Count);
            Assert.Equal(StitchedFrameOrigin.Sync, result.Frames[0].Origin);
            Assert.Equal(StitchedFrameOrigin.Sync, result.Frames[1].Origin);
            Assert.Equal(StitchedFrameOrigin.AsyncRemaining, result.Frames[2].Origin);
            Assert.Equal(Scenario.CA(601), result.Frames[2].CodeAddress);
            Assert.Equal(1, result.Diagnostics.BoundariesNotFound);
        }

        [Fact]
        public void MultipleSegments_ProcessedLeafToRoot_InLockstep()
        {
            // Two stacked V2 segments (each freshly resumed: completed=0, splice one ancestry frame).
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            AsyncCallStack leaf = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 700, 701 }, frameMethods: new[] { 10, 11 }, completions: Completed(0));
            AsyncCallStack root = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 800, 801 }, frameMethods: new[] { 20, 21 }, completions: Completed(0));

            var sync = new[]
            {
                s.Sync(50, 10),                  // L current (frames[0] of leaf seg)
                s.Boundary(51, Wrapper(0)),      // leaf boundary
                s.Sync(52, 20),                  // R current (frames[0] of root seg)
                s.Boundary(53, Wrapper(0)),      // root boundary
                s.Sync(54, 99),                  // root frame
            };

            // GetAsyncCallStacks returns depth-ascending (root -> leaf); Stitch reverses internally.
            StitchResult result = s.Run(sync, new[] { root, leaf });

            // Expected: [L0(Sync), L1(Async), R0(Sync), R1(Async), root(Sync)].
            Assert.Equal(5, result.Frames.Count);
            Assert.Equal(Scenario.CA(50), result.Frames[0].CodeAddress);
            Assert.Equal(StitchedFrameOrigin.AsyncRemaining, result.Frames[1].Origin);
            Assert.Equal(Scenario.CA(701), result.Frames[1].CodeAddress);
            Assert.Equal(Scenario.CA(52), result.Frames[2].CodeAddress);
            Assert.Equal(StitchedFrameOrigin.AsyncRemaining, result.Frames[3].Origin);
            Assert.Equal(Scenario.CA(801), result.Frames[3].CodeAddress);
            Assert.Equal(Scenario.CA(54), result.Frames[4].CodeAddress);
            Assert.Equal(2, result.Diagnostics.SegmentsProcessed);
            Assert.Equal(0, result.Diagnostics.BoundariesNotFound);
        }

        [Fact]
        public void MixedV1AndV2Segments_PreserveSynchronousAndNativeBridge()
        {
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            AsyncCallStack outer = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 800, 801 }, frameMethods: new[] { 20, 21 }, completions: Completed(0));
            AsyncCallStack inner = s.Segment(AsyncCallstackKind.StateMachineAsync,
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
            StitchResult result = s.Run(sync, new[] { outer, inner });

            Assert.Equal(10, result.Frames.Count);
            Assert.Equal(new[]
            {
                Scenario.CA(50), Scenario.CA(51), Scenario.CA(501),
                Scenario.CA(58), Scenario.CA(59), Scenario.CA(60), Scenario.CA(61), Scenario.CA(801),
                Scenario.CA(65), Scenario.CA(66),
            }, result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(StitchedFrameOrigin.AsyncCurrent, result.Frames[1].Origin);
            Assert.Equal(StitchedFrameOrigin.AsyncRemaining, result.Frames[2].Origin);
            Assert.Equal(StitchedFrameOrigin.AsyncRemaining, result.Frames[7].Origin);
            Assert.Equal(2, result.Diagnostics.SegmentsProcessed);
            Assert.Equal(2, result.Diagnostics.V2PlumbingFramesCollapsed);
            Assert.Equal(2, result.Diagnostics.V1InfrastructureFramesCollapsed);
            Assert.Equal(1, result.Diagnostics.V1InlineFallbackUsed);
            Assert.Equal(0, result.Diagnostics.BoundariesNotFound);
        }

        [Fact]
        public void MixedV2InnerAndV1Outer_PreservesUnclassifiedBridge()
        {
            var s = new Scenario();
            AsyncCallStack outer = s.Segment(AsyncCallstackKind.StateMachineAsync,
                frameCodeAddrs: new[] { 800, 801 }, frameMethods: new[] { 20, 21 });
            AsyncCallStack inner = s.Segment(AsyncCallstackKind.RuntimeAsync,
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

            StitchResult result = s.Run(sync, new[] { outer, inner });

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
        public void NestedV1Segments_PreserveUnclassifiedBridge()
        {
            var s = new Scenario();
            AsyncCallStack outer = s.Segment(AsyncCallstackKind.StateMachineAsync,
                frameCodeAddrs: new[] { 800, 801 }, frameMethods: new[] { 20, 21 });
            AsyncCallStack inner = s.Segment(AsyncCallstackKind.StateMachineAsync,
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

            StitchResult result = s.Run(sync, new[] { outer, inner });

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
            AsyncCallStack outer = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 800, 801, 802 }, frameMethods: new[] { 20, 21, 22 });
            AsyncCallStack inner = s.Segment(AsyncCallstackKind.StateMachineAsync,
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

            StitchResult result = s.Run(sync, new[] { outer, inner });

            Assert.Equal(new[]
            {
                Scenario.CA(50), Scenario.CA(51), Scenario.CA(502),
                Scenario.CA(58), Scenario.CA(59), Scenario.CA(60), Scenario.CA(61), Scenario.CA(802),
                Scenario.CA(65), Scenario.CA(66),
            }, result.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(StitchedFrameOrigin.AsyncCurrent, result.Frames[1].Origin);
            Assert.Equal(StitchedFrameOrigin.AsyncRemaining, result.Frames[2].Origin);
            Assert.Equal(StitchedFrameOrigin.AsyncRemaining, result.Frames[7].Origin);
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
            AsyncCallStack seg = s.Segment(AsyncCallstackKind.RuntimeAsync,
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

            StitchResult result = s.Run(sync, new[] { seg });

            // Expected: [current(Sync,90), ancestry(Async,201), Dispatch(Sync,94), root(Sync,95)].
            Assert.Equal(4, result.Frames.Count);
            Assert.Equal(Scenario.CA(90), result.Frames[0].CodeAddress);
            Assert.Equal(StitchedFrameOrigin.Sync, result.Frames[0].Origin);
            Assert.Equal(StitchedFrameOrigin.AsyncRemaining, result.Frames[1].Origin);
            Assert.Equal(Scenario.CA(201), result.Frames[1].CodeAddress);
            Assert.Equal(Scenario.CA(94), result.Frames[2].CodeAddress);
            Assert.Equal(Scenario.CA(95), result.Frames[3].CodeAddress);
            Assert.DoesNotContain(result.Frames, f => f.CodeAddress == Scenario.CA(92) || f.CodeAddress == Scenario.CA(93));
            Assert.Equal(2, result.Diagnostics.V2PlumbingFramesCollapsed);
            Assert.Equal(0, result.Diagnostics.V2SyncLayoutUsed);
        }

        [Fact]
        public void V2_WrapperIsLeaf_DropsWrapperAndUsesSyncLayout()
        {
            // The CPU is in the continuation-wrapper's own transition (the wrapper is the sampled leaf), not yet
            // inside the resumed async body: drop the wrapper frame and emit the sync layout; splice no ancestry.
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            AsyncCallStack seg = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 300, 301 }, frameMethods: new[] { 60, 61 }, completions: Completed(0));

            var sync = new[]
            {
                s.Boundary(80, Wrapper(0)),           // wrapper IS the leaf -> dropped
                s.Boundary(81, DispatchContinuation), // InstrumentedDispatchContinuations (becomes the leaf)
                s.Sync(82, 99),                       // Dispatch anchor
                s.Sync(83, 98),                       // root
            };

            StitchResult result = s.Run(sync, new[] { seg });

            // Expected: sync layout minus the leaf wrapper: [81, 82, 83]; no async frames spliced.
            Assert.Equal(3, result.Frames.Count);
            Assert.All(result.Frames, f => Assert.Equal(StitchedFrameOrigin.Sync, f.Origin));
            Assert.Equal(new[] { Scenario.CA(81), Scenario.CA(82), Scenario.CA(83) }, result.Frames.Select(f => f.CodeAddress));
            Assert.Equal(1, result.Diagnostics.V2SyncLayoutUsed);
            Assert.Equal(1, result.Diagnostics.V2LeafWrapperDropped);
            Assert.Equal(0, result.Diagnostics.SegmentsProcessed);
        }

        [Fact]
        public void V2_NoWrapperInDispatchPlumbing_UsesSyncLayout()
        {
            // An active V2 segment, but the CPU is in dispatch-continuation plumbing with no wrapper above the leaf:
            // emit the sync layout unchanged (dispatch machinery kept); splice no ancestry.
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            AsyncCallStack seg = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 400, 401 }, frameMethods: new[] { 70, 71 }, completions: Completed(0));

            var sync = new[]
            {
                s.Boundary(84, DispatchContinuation), // InstrumentedDispatchContinuations (leaf)
                s.Boundary(85, DispatchContinuation), // DispatchContinuations
                s.Sync(86, 99),                       // Dispatch anchor
                s.Sync(87, 98),                       // root
            };

            StitchResult result = s.Run(sync, new[] { seg });

            Assert.Equal(4, result.Frames.Count);
            Assert.All(result.Frames, f => Assert.Equal(StitchedFrameOrigin.Sync, f.Origin));
            Assert.Equal(new[] { Scenario.CA(84), Scenario.CA(85), Scenario.CA(86), Scenario.CA(87) },
                result.Frames.Select(f => f.CodeAddress));
            Assert.Equal(1, result.Diagnostics.V2SyncLayoutUsed);
            Assert.Equal(0, result.Diagnostics.V2LeafWrapperDropped);
            Assert.Equal(0, result.Diagnostics.SegmentsProcessed);
        }

        [Fact]
        public void Trace_Disabled_ByDefault_EmitsNoHappyPathMessages()
        {
            // A clean V2 segment (boundary found, no anomalies) must not add any Messages when trace is off.
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            AsyncCallStack seg = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 200, 201, 202, 203, 204 },
                frameMethods:   new[] {  50,  51,  52,  53,  54 },
                completions: Completed(2));
            var sync = new[] { s.Sync(90, 52), s.Boundary(91, Wrapper(3)), s.Sync(92, 99) };

            StitchResult result = s.Run(sync, new[] { seg }); // trace defaults to off

            Assert.Empty(result.Diagnostics.Messages);
        }

        [Fact]
        public void Trace_Enabled_EmitsPerSegmentHappyPathNote()
        {
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            AsyncCallStack seg = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 200, 201, 202, 203, 204 },
                frameMethods:   new[] {  50,  51,  52,  53,  54 },
                completions: Completed(2));
            var sync = new[] { s.Sync(90, 52), s.Boundary(91, Wrapper(3)), s.Sync(92, 99) };

            StitchResult result = s.Run(sync, new[] { seg }, trace: true);

            string note = Assert.Single(result.Diagnostics.Messages);
            Assert.Contains("segment[0]", note);
            Assert.Contains($"kind={AsyncCallstackKind.RuntimeAsync}", note);
            Assert.Contains($"{AsyncStitchBoundaryKind.V2ContinuationWrapper}[3]", note); // boundary + wrapper index
            Assert.Contains("completed=2", note);
            Assert.Contains("spliced=[3..5)", note);                                       // ancestry A1,A2
        }

        [Fact]
        public void Trace_Enabled_NotesMissingBoundary()
        {
            // When no boundary is found the happy-path note still fires (alongside the anomaly note) and reflects it.
            var s = new Scenario();
            s.MarkMethodCompletionObserved(AsyncCallstackKind.RuntimeAsync);
            AsyncCallStack seg = s.Segment(AsyncCallstackKind.RuntimeAsync,
                frameCodeAddrs: new[] { 600, 601 }, frameMethods: new[] { 70, 71 }, completions: Completed(0));
            var sync = new[] { s.Sync(10, 1), s.Sync(11, 2) }; // no boundary frame at all

            StitchResult result = s.Run(sync, new[] { seg }, trace: true);

            Assert.Equal(1, result.Diagnostics.BoundariesNotFound);
            Assert.Contains(result.Diagnostics.Messages, m => m.Contains("segment[0]") && m.Contains("boundary=no-boundary"));
        }

        [Fact]
        public void Diagnostics_BoundsRetainedMessages()
        {
            var diagnostics = new StitchDiagnostics();

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
            System.Reflection.FieldInfo[] counters = typeof(StitchDiagnostics).GetFields()
                .Where(field => field.IsPublic && !field.IsStatic && field.FieldType == typeof(int))
                .ToArray();
            var source = new StitchDiagnostics();
            foreach (System.Reflection.FieldInfo counter in counters)
            {
                counter.SetValue(source, 1);
            }

            var target = new StitchDiagnostics();
            source.AddTo(target);
            Assert.All(counters, counter => Assert.Equal(1, counter.GetValue(target)));

            source.Clear();
            Assert.All(counters, counter => Assert.Equal(0, counter.GetValue(source)));
        }
    }
}
