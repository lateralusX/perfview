// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

using Xunit;

namespace TraceEventTests
{
    /// <summary>
    /// Fluent convenience wrappers over <see cref="AsyncProfilerBufferBuilder"/> for the state-machine (V1)
    /// event ids used by the computer tests.
    /// </summary>
    internal static class AsyncProfilerComputerBuilderExtensions
    {
        public static AsyncProfilerBufferBuilder Reset(this AsyncProfilerBufferBuilder b, long ts) =>
            b.ContextNoPayload(AsyncEventID.ResetAsyncThreadContext, ts);

        public static AsyncProfilerBufferBuilder ResumeStack(this AsyncProfilerBufferBuilder b, long ts, ulong dispatcher, ulong[] frames, int[] states = null, byte continuationIndex = 0) =>
            b.Callstack(AsyncEventID.ResumeStateMachineAsyncCallstack, ts, continuationIndex, 0, dispatcher, frames, states ?? new int[frames.Length]);

        public static AsyncProfilerBufferBuilder AppendStack(this AsyncProfilerBufferBuilder b, long ts, ulong dispatcher, ulong[] frames, int[] states = null, byte continuationIndex = 0) =>
            b.Callstack(AsyncEventID.AppendStateMachineAsyncCallstack, ts, continuationIndex, 0, dispatcher, frames, states ?? new int[frames.Length]);

        public static AsyncProfilerBufferBuilder Suspend(this AsyncProfilerBufferBuilder b, long ts) =>
            b.ContextNoPayload(AsyncEventID.SuspendStateMachineAsyncContext, ts);

        public static AsyncProfilerBufferBuilder Complete(this AsyncProfilerBufferBuilder b, long ts) =>
            b.ContextNoPayload(AsyncEventID.CompleteStateMachineAsyncContext, ts);

        public static AsyncProfilerBufferBuilder CompleteMethod(this AsyncProfilerBufferBuilder b, long ts) =>
            b.Method(AsyncEventID.CompleteStateMachineAsyncMethod, ts);

        public static AsyncProfilerBufferBuilder UnwindException(this AsyncProfilerBufferBuilder b, long ts, uint frames) =>
            b.Unwind(AsyncEventID.UnwindStateMachineAsyncException, ts, frames);

        public static AsyncProfilerBufferBuilder WrapperReset(this AsyncProfilerBufferBuilder b, long ts) =>
            b.ContextNoPayload(AsyncEventID.ResetAsyncContinuationWrapperIndex, ts);
    }

    /// <summary>
    /// Unit tests for <see cref="AsyncProfilerComputer"/>. Each test synthesizes an <c>AsyncEvents</c> buffer
    /// (via <see cref="AsyncProfilerBufferBuilder"/>) and drives it through the computer's decoder, then
    /// asserts the per-thread active-async-callstack index.
    /// </summary>
    public class AsyncProfilerComputerTests
    {
        private const ulong ThreadA = 0x1000;
        private const ulong ThreadB = 0x2000;
        private const long Start = 1_000_000;

        private static AsyncThreadKey Key(ulong osThreadId) => new AsyncThreadKey(0, osThreadId);

        private static AsyncProfilerComputer Compute(AsyncProfilerBufferBuilder builder)
        {
            var computer = new AsyncProfilerComputer();
            computer.Process(builder.Build());
            return computer;
        }

        [Fact]
        public void EventsBeforeReset_AreIgnored()
        {
            // No ResetAsyncThreadContext first: the thread is unarmed, so nothing is indexed.
            var computer = Compute(new AsyncProfilerBufferBuilder(ThreadA)
                .ResumeStack(Start + 10, dispatcher: 1, new ulong[] { 0x100 })
                .Suspend(Start + 20));

            Assert.Empty(computer.GetAsyncCallStacks(Key(ThreadA), Start + 15));
            Assert.Equal(0, computer.DistinctFramesCount);
        }

        [Fact]
        public void ResumeSuspend_ProducesInterval()
        {
            var computer = Compute(new AsyncProfilerBufferBuilder(ThreadA)
                .Reset(Start)
                .ResumeStack(Start + 10, dispatcher: 1, new ulong[] { 0x100, 0x200 })
                .Suspend(Start + 20));

            Assert.Empty(computer.GetAsyncCallStacks(Key(ThreadA), Start + 5));   // before start
            Assert.Empty(computer.GetAsyncCallStacks(Key(ThreadA), Start + 20));  // end is exclusive
            Assert.Empty(computer.GetAsyncCallStacks(Key(ThreadA), Start + 25));  // after end

            IReadOnlyList<AsyncCallStack> active = computer.GetAsyncCallStacks(Key(ThreadA), Start + 15);
            AsyncCallStack cs = Assert.Single(active);
            Assert.Equal(0, cs.Depth);
            Assert.Equal(Start + 10, cs.StartQpc);
            Assert.Equal(Start + 20, cs.EndQpc);
            Assert.Equal(2, cs.Frames.FrameCount);
            Assert.Equal(0x100UL, cs.Frames.MethodIdAt(0));
            Assert.Equal(0x200UL, cs.Frames.MethodIdAt(1));
            Assert.Equal(AsyncCallstackKind.StateMachineAsync, cs.Frames.Kind);
        }

        [Fact]
        public void ResumeComplete_ProducesInterval()
        {
            var computer = Compute(new AsyncProfilerBufferBuilder(ThreadA)
                .Reset(Start)
                .ResumeStack(Start + 10, dispatcher: 1, new ulong[] { 0x100 })
                .Complete(Start + 20));

            Assert.Single(computer.GetAsyncCallStacks(Key(ThreadA), Start + 15));
        }

        [Fact]
        public void NestedActivations_QueryReturnsOrderedStack()
        {
            // D1 [10,40) with D2 [20,30) nested inside it.
            var computer = Compute(new AsyncProfilerBufferBuilder(ThreadA)
                .Reset(Start)
                .ResumeStack(Start + 10, dispatcher: 1, new ulong[] { 0xA })
                .ResumeStack(Start + 20, dispatcher: 2, new ulong[] { 0xB })
                .Suspend(Start + 30)   // pops D2
                .Suspend(Start + 40)); // pops D1

            // Inside the nested window: both are active, ordered bottom-to-top.
            IReadOnlyList<AsyncCallStack> nested = computer.GetAsyncCallStacks(Key(ThreadA), Start + 25);
            Assert.Equal(2, nested.Count);
            Assert.Equal(0, nested[0].Depth);
            Assert.Equal(0xAUL, nested[0].Frames.MethodIdAt(0));
            Assert.Equal(1, nested[1].Depth);
            Assert.Equal(0xBUL, nested[1].Frames.MethodIdAt(0));

            // Before and after the nested child: only D1 is active.
            Assert.Equal(0xAUL, Assert.Single(computer.GetAsyncCallStacks(Key(ThreadA), Start + 15)).Frames.MethodIdAt(0));
            Assert.Equal(0xAUL, Assert.Single(computer.GetAsyncCallStacks(Key(ThreadA), Start + 35)).Frames.MethodIdAt(0));
        }

        [Fact]
        public void Append_ExtendsCallstack_FinalizedAtClose()
        {
            var computer = Compute(new AsyncProfilerBufferBuilder(ThreadA)
                .Reset(Start)
                .ResumeStack(Start + 10, dispatcher: 1, new ulong[] { 0xA }, new[] { 0 })
                .AppendStack(Start + 15, dispatcher: 1, new ulong[] { 0xB, 0xC }, new[] { 1, 2 })
                .Suspend(Start + 20));

            AsyncCallStack cs = Assert.Single(computer.GetAsyncCallStacks(Key(ThreadA), Start + 18));
            Assert.Equal(3, cs.Frames.FrameCount);
            Assert.Equal(new ulong[] { 0xA, 0xB, 0xC }, new[] { cs.Frames.MethodIdAt(0), cs.Frames.MethodIdAt(1), cs.Frames.MethodIdAt(2) });
            Assert.Equal(new[] { 0, 1, 2 }, new[] { cs.Frames.FrameStateAt(0), cs.Frames.FrameStateAt(1), cs.Frames.FrameStateAt(2) });
            Assert.Equal(1, computer.DistinctFramesCount);
        }

        [Fact]
        public void IdenticalCallstacks_AreDeduplicated()
        {
            var computer = Compute(new AsyncProfilerBufferBuilder(ThreadA)
                .Reset(Start)
                .ResumeStack(Start + 10, dispatcher: 1, new ulong[] { 0xA, 0xB })
                .Suspend(Start + 20)
                .ResumeStack(Start + 30, dispatcher: 2, new ulong[] { 0xA, 0xB })
                .Suspend(Start + 40));

            Assert.Equal(1, computer.DistinctFramesCount);

            AsyncCallStack first = Assert.Single(computer.GetAsyncCallStacks(Key(ThreadA), Start + 15));
            AsyncCallStack second = Assert.Single(computer.GetAsyncCallStacks(Key(ThreadA), Start + 35));
            Assert.Equal(first.FramesIndex, second.FramesIndex);
            Assert.Same(first.Frames, second.Frames);
        }

        [Fact]
        public void MultipleThreads_AreIsolated()
        {
            var computer = new AsyncProfilerComputer();
            computer.Process(new AsyncProfilerBufferBuilder(ThreadA)
                .Reset(Start)
                .ResumeStack(Start + 10, dispatcher: 1, new ulong[] { 0xAAA })
                .Suspend(Start + 50)
                .Build());
            computer.Process(new AsyncProfilerBufferBuilder(ThreadB)
                .Reset(Start)
                .ResumeStack(Start + 20, dispatcher: 2, new ulong[] { 0xBBB })
                .Suspend(Start + 30)
                .Build());

            Assert.Equal(0xAAAUL, Assert.Single(computer.GetAsyncCallStacks(Key(ThreadA), Start + 25)).Frames.MethodIdAt(0));
            Assert.Equal(0xBBBUL, Assert.Single(computer.GetAsyncCallStacks(Key(ThreadB), Start + 25)).Frames.MethodIdAt(0));

            // Thread B is idle at t40; thread A is still active.
            Assert.Empty(computer.GetAsyncCallStacks(Key(ThreadB), Start + 40));
            Assert.Single(computer.GetAsyncCallStacks(Key(ThreadA), Start + 40));
        }

        [Fact]
        public void ResetMidEpisode_DiscardsInProgressActivation()
        {
            var computer = Compute(new AsyncProfilerBufferBuilder(ThreadA)
                .Reset(Start)
                .ResumeStack(Start + 10, dispatcher: 1, new ulong[] { 0xA })
                .Reset(Start + 15)      // clears the in-progress activation
                .Suspend(Start + 20));  // nothing to pop

            Assert.Empty(computer.GetAsyncCallStacks(Key(ThreadA), Start + 12));
            Assert.Empty(computer.GetAsyncCallStacks(Key(ThreadA), Start + 18));
        }

        [Fact]
        public void CompletedFrameCount_FromCompleteAndUnwind()
        {
            var computer = Compute(new AsyncProfilerBufferBuilder(ThreadA)
                .Reset(Start)
                .ResumeStack(Start + 10, dispatcher: 1, new ulong[] { 0xA, 0xB, 0xC, 0xD })
                .CompleteMethod(Start + 15)             // +1
                .UnwindException(Start + 18, frames: 2) // +2
                .Suspend(Start + 30));

            AsyncCallStack cs = Assert.Single(computer.GetAsyncCallStacks(Key(ThreadA), Start + 20));
            Assert.Equal(0, cs.GetCompletedFrameCount(Start + 12));
            Assert.Equal(1, cs.GetCompletedFrameCount(Start + 16));
            Assert.Equal(3, cs.GetCompletedFrameCount(Start + 20));
        }

        [Fact]
        public void WrapperResets_AreScopedPerActivation_AndYieldCompletedFrames()
        {
            var computer = Compute(new AsyncProfilerBufferBuilder(ThreadA)
                .Metadata(Start + 1, qpcFrequency: 10_000_000, qpcSync: 1, utcSync: 1, eventBufferSize: 0, wrapperCount: 32, new AsyncManifestEntry[0])
                .Reset(Start + 2)
                .ResumeStack(Start + 10, dispatcher: 1, new ulong[] { 0xA }, continuationIndex: 5)
                .WrapperReset(Start + 15)
                .WrapperReset(Start + 18)
                .Suspend(Start + 30));

            AsyncCallStack cs = Assert.Single(computer.GetAsyncCallStacks(Key(ThreadA), Start + 20));
            Assert.Equal((byte)5, cs.ContinuationIndexBase);
            Assert.Equal((byte)32, cs.WrapperCount);

            Assert.Equal(1, cs.GetWrapperResetCount(Start + 16));
            Assert.Equal(2, cs.GetWrapperResetCount(Start + 20));

            // Completed-frame count via wrapper resets: resets*WrapperCount + currentMethodIndex.
            Assert.Equal(2 * 32 + 7, cs.GetCompletedFrameCount(Start + 20, currentMethodIndex: 7));
        }

        [Fact]
        public void Metadata_EstablishesClock()
        {
            long utcFileTime = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
            var computer = Compute(new AsyncProfilerBufferBuilder(ThreadA)
                .Metadata(Start + 1, qpcFrequency: 10_000_000, qpcSync: (ulong)(Start + 1), utcSync: (ulong)utcFileTime, eventBufferSize: 0, wrapperCount: 32, new AsyncManifestEntry[0]));

            Assert.True(computer.ClockKnown);
            Assert.Equal((byte)32, computer.WrapperCount);

            // One second of QPC (10,000,000 ticks) after the sync point => one second later in UTC.
            DateTime? at = computer.QpcToDateTime(Start + 1 + 10_000_000);
            Assert.NotNull(at);
            Assert.Equal(new DateTime(2026, 7, 1, 12, 0, 1, DateTimeKind.Utc), at.Value);
        }

        [Fact]
        public void DefaultConstructor_ProcessesWithoutThrowing()
        {
            var computer = new AsyncProfilerComputer();
            computer.Process(new AsyncProfilerBufferBuilder(ThreadA).Reset(Start).Build());
            Assert.Empty(computer.GetAsyncCallStacks(Key(ThreadA), Start + 5));
        }

        [Fact]
        public void SameTimestamp_NestedResumes_PreserveEmissionOrder()
        {
            // D1 and D2 are resumed at the SAME timestamp; emission order (D1 then D2) must be preserved so
            // D1 is the outer (depth 0) and D2 the inner (depth 1) activation.
            var computer = Compute(new AsyncProfilerBufferBuilder(ThreadA)
                .Reset(Start)
                .ResumeStack(Start + 10, dispatcher: 1, new ulong[] { 0xA })
                .ResumeStack(Start + 10, dispatcher: 2, new ulong[] { 0xB }) // same timestamp
                .Suspend(Start + 20)   // pops D2 -> [10,20)
                .Suspend(Start + 30)); // pops D1 -> [10,30)

            IReadOnlyList<AsyncCallStack> nested = computer.GetAsyncCallStacks(Key(ThreadA), Start + 15);
            Assert.Equal(2, nested.Count);
            Assert.Equal(0, nested[0].Depth);
            Assert.Equal(0xAUL, nested[0].Frames.MethodIdAt(0)); // outer, emitted first
            Assert.Equal(1, nested[1].Depth);
            Assert.Equal(0xBUL, nested[1].Frames.MethodIdAt(0)); // inner, emitted second
        }

        [Fact]
        public void SameTimestamp_Completions_AreAllCounted()
        {
            // Two CompleteMethod events share a timestamp; both must be counted at that instant, and none
            // before it.
            var computer = Compute(new AsyncProfilerBufferBuilder(ThreadA)
                .Reset(Start)
                .ResumeStack(Start + 10, dispatcher: 1, new ulong[] { 0xA, 0xB, 0xC, 0xD })
                .CompleteMethod(Start + 15)
                .CompleteMethod(Start + 15) // same timestamp
                .Suspend(Start + 30));

            AsyncCallStack cs = Assert.Single(computer.GetAsyncCallStacks(Key(ThreadA), Start + 20));
            Assert.Equal(0, cs.GetCompletedFrameCount(Start + 14));
            Assert.Equal(2, cs.GetCompletedFrameCount(Start + 15));
            Assert.Equal(2, cs.GetCompletedFrameCount(Start + 16));
        }

        [Fact]
        public void CoarseTimestamps_ZeroWidthActivation_ExcludedButOuterStaysCoherent()
        {
            // WASM-like coarse resolution: D2 resumes AND suspends within the same tick as D1's resume, so
            // D2 is a zero-width [10,10) activation. A point query never returns it (half-open end), and it
            // must not disturb the outer D1 activation. D2 is still recorded/interned.
            var computer = Compute(new AsyncProfilerBufferBuilder(ThreadA)
                .Reset(Start)
                .ResumeStack(Start + 10, dispatcher: 1, new ulong[] { 0xA })
                .ResumeStack(Start + 10, dispatcher: 2, new ulong[] { 0xB })
                .Suspend(Start + 10)   // pops D2 -> zero-width [10,10)
                .Suspend(Start + 20)); // pops D1 -> [10,20)

            AsyncCallStack atTen = Assert.Single(computer.GetAsyncCallStacks(Key(ThreadA), Start + 10));
            Assert.Equal(0xAUL, atTen.Frames.MethodIdAt(0)); // only the outer D1
            Assert.Equal(0xAUL, Assert.Single(computer.GetAsyncCallStacks(Key(ThreadA), Start + 15)).Frames.MethodIdAt(0));

            // Both activations were recorded/interned even though the inner one is zero-width.
            Assert.Equal(2, computer.DistinctFramesCount);
        }

        [Fact]
        public void CoarseTimestamps_AllTransitionsSameTick_ReturnCoherentStack()
        {
            // Everything happens in one coarse tick except D1's suspend. D2 and D3 are sequential zero-width
            // activations that both occupied depth 1; neither is returned by a point query, so there is no
            // depth-tie ambiguity, and the outer D1 is returned coherently.
            var computer = Compute(new AsyncProfilerBufferBuilder(ThreadA)
                .Reset(Start)
                .ResumeStack(Start + 10, dispatcher: 1, new ulong[] { 0xA })
                .ResumeStack(Start + 10, dispatcher: 2, new ulong[] { 0xB })
                .Suspend(Start + 10)   // pop D2 (zero-width, depth 1)
                .ResumeStack(Start + 10, dispatcher: 3, new ulong[] { 0xC })
                .Suspend(Start + 10)   // pop D3 (zero-width, depth 1)
                .Suspend(Start + 20)); // pop D1 -> [10,20)

            AsyncCallStack only = Assert.Single(computer.GetAsyncCallStacks(Key(ThreadA), Start + 10));
            Assert.Equal(0, only.Depth);
            Assert.Equal(0xAUL, only.Frames.MethodIdAt(0));
            Assert.Equal(3, computer.DistinctFramesCount); // A, B, C all recorded
        }
    }
}
