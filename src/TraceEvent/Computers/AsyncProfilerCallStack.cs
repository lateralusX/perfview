// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;

using FastSerialization;

using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>
    /// One recorded run of an async call stack on a thread: the time window <c>[StartQpc, EndQpc)</c> from a
    /// resume callstack to its matching suspend/complete, with nesting <see cref="Depth"/> and raw
    /// <see cref="CapturedFrames"/>. Several activations can cover the same instant.
    /// <see cref="AsyncProfilerCallStackComputer.GetCallStacks(TraceEvent)"/> returns them outermost-to-innermost.
    /// A reset or decode failure can close an interval early; an activation still open at stream end has
    /// <see cref="EndQpc"/> equal to <see cref="long.MaxValue"/>.
    /// </summary>
    /// <remarks>
    /// Obtain activations from <see cref="AsyncProfilerCallStackComputer"/>; callers cannot construct them directly.
    /// Query results and their captured-frame views can be retained across later queries and index release.
    /// Activations and frames supplied to custom transform callbacks instead have callback-only lifetimes.
    /// Keep the originating TraceLog open while resolving code addresses. An activation records a continuation
    /// context, not proof that each captured method was physically executing.
    /// </remarks>
    public sealed class AsyncProfilerCallStack
    {
        /// <summary>The unique process instance in the originating TraceLog.</summary>
        public ProcessIndex ProcessIndex => Thread.ProcessIndex;

        /// <summary>The OS thread id recorded in the async-profiler buffer.</summary>
        public ulong OsThreadId => Thread.OsThreadId;

        /// <summary>The instrumentation family that emitted this activation.</summary>
        public AsyncProfilerCallStackKind Kind => Frames.Kind == AsyncCallstackKind.StateMachineAsync
            ? AsyncProfilerCallStackKind.StateMachineAsync : AsyncProfilerCallStackKind.RuntimeAsync;

        /// <summary>The finalized raw resume/append frames, leaf-to-root, without completion trimming or
        /// presentation transforms. V1 appends captured later in the activation are included. These frames
        /// do not identify the physically current method.</summary>
        public IReadOnlyList<AsyncProfilerCallStackFrame> CapturedFrames =>
            m_capturedFrames ?? (m_capturedFrames = new CapturedFrameList(this));

        /// <summary>Whether normal-completion records were observed for this process and instrumentation kind
        /// in the configuration epoch containing StartQpc. This summarizes the recorded epoch, not just events
        /// before a query timestamp, and does not guarantee every completion was captured.</summary>
        public bool MethodCompletionObserved { get; internal set; }

        /// <summary>Whether exception-unwind records were observed for this process and instrumentation kind
        /// in the configuration epoch containing StartQpc. This summarizes the recorded epoch, not just events
        /// before a query timestamp, and does not guarantee every unwind was captured.</summary>
        public bool ExceptionCompletionObserved { get; internal set; }

        /// <summary>The recorded dispatcher identifier. A dispatcher can have multiple resume activations;
        /// this identifier alone does not uniquely identify an activation.</summary>
        public ulong DispatcherId { get; private set; }

        /// <summary>The nesting depth (0 = outermost) of this async call stack on its thread.</summary>
        public int Depth { get; private set; }

        /// <summary>The continuation-wrapper index captured when this async call stack was emitted.</summary>
        public byte ContinuationIndexBase { get; private set; }

        /// <summary>The continuation-wrapper pool size recorded for this activation, or 0 when unavailable.
        /// Used with wrapper resets and the current physical wrapper slot to derive V2 completion progress.</summary>
        public byte WrapperCount { get; private set; }

        /// <summary>The inclusive resume timestamp in the trace's QPC domain.</summary>
        public long StartQpc { get; private set; }
        /// <summary>The exclusive end timestamp in the trace's QPC domain; <see cref="long.MaxValue"/> if open at stream end.</summary>
        public long EndQpc { get; private set; }

        /// <summary>
        /// The recorded number of completed leaf frames at or before <paramref name="timestampQpc"/>, summed from
        /// <c>CompleteMethod</c> and <c>Unwind</c> events in this activation. Missing events are not inferred.
        /// This raw count is not clamped to CapturedFrames.Count and does not validate that the timestamp falls
        /// within this activation. Use <see cref="AsyncProfilerCallStackSourceBuilder"/> for reconstruction
        /// that reconciles recorded progress with the physical stack.
        /// </summary>
        public int GetCompletedFrameCount(long timestampQpc) =>
            GetMethodCompletedFrameCount(timestampQpc) + GetExceptionCompletedFrameCount(timestampQpc);

        /// <summary>
        /// The number of completed leaf frames derived from continuation-wrapper-index resets and the current
        /// wrapper slot, relative to the wrapper slot captured at resume (<see cref="ContinuationIndexBase"/>):
        /// <c>GetWrapperResetCount(timestampQpc) * WrapperCount + currentMethodIndex - ContinuationIndexBase</c>
        /// (clamped to &gt;= 0). Wrapper resets only advance every <see cref="WrapperCount"/> completions, so the
        /// caller supplies <paramref name="currentMethodIndex"/> - the current wrapper slot read from the physical
        /// stack at <paramref name="timestampQpc"/>, not an index into CapturedFrames - to refine within the window.
        /// <para>
        /// <see cref="ContinuationIndexBase"/> is the wrapper slot at resume: normally 0, but on late attach it is
        /// the current slot, because the resets that advanced it before attach could not be observed. Subtracting
        /// it makes the count start from that value, so only completions observed since resume are counted.
        /// </para>
        /// Use this overload for V2 when <c>CompleteMethod</c> events were not observed in this activation's
        /// metadata/configuration epoch; otherwise prefer <see cref="GetCompletedFrameCount(long)"/>.
        /// Wrapper advancement includes exceptional unwinds, so do not add the recorded exception count again.
        /// V1 completion counting instead uses normal-completion events or physical <c>MoveNext</c> matches,
        /// plus recorded exception deltas.
        /// </summary>
        /// <remarks>
        /// The timestamp and wrapper slot must describe this activation in the originating trace.
        /// Neither is range-validated, and the result is not capped at the captured frame count.
        /// </remarks>
        public int GetCompletedFrameCount(long timestampQpc, int currentMethodIndex)
        {
            int completed = (GetWrapperResetCount(timestampQpc) * WrapperCount) + currentMethodIndex - ContinuationIndexBase;
            return completed > 0 ? completed : 0;
        }

        /// <summary>
        /// The number of leaf frames completed by <paramref name="timestampQpc"/> via normal <c>CompleteMethod</c> events
        /// (each contributes 1). Meaningful when <c>CompleteMethod</c> events were observed in this activation's
        /// metadata/configuration epoch; otherwise this is 0 and the normal completed count must be derived another
        /// way (the continuation-wrapper slot for V2, or the inline-resumed frames on the sync stack for V1).
        /// </summary>
        public int GetMethodCompletedFrameCount(long timestampQpc) => SumDeltasUpTo(_methodCompletions, timestampQpc);

        /// <summary>
        /// The number of leaf frames completed by <paramref name="timestampQpc"/> via <c>Unwind</c> (exception) events (each
        /// contributes its unwound frame count). For V1, add this to recorded normal completions, or reconcile it
        /// with physical inline matches that may include still-finishing exceptional methods.
        /// The V2 wrapper-index fallback already counts exceptional unwinds, so
        /// adding these deltas to that fallback would double-count them.
        /// </summary>
        public int GetExceptionCompletedFrameCount(long timestampQpc) => SumDeltasUpTo(_exceptionCompletions, timestampQpc);

        /// <summary>
        /// The number of continuation-wrapper-index resets observed during this async call stack up to
        /// <paramref name="timestampQpc"/> (this stack's wrapper "generation").
        /// </summary>
        public int GetWrapperResetCount(long timestampQpc)
        {
            int lo = 0, hi = _wrapperResets.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (_wrapperResets[mid] <= timestampQpc)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }
            return lo;
        }

        #region private

        internal AsyncProfilerCallStack(int depth, AsyncProfilerCallStackFramesIndex framesIndex, AsyncProfilerCallStackFrames frames,
            byte continuationIndexBase, byte wrapperCount, long startQpc, long endQpc,
            CompletionDelta[] methodCompletions, CompletionDelta[] exceptionCompletions, long[] wrapperResets)
            : this(default, 0, depth, framesIndex, frames,
                  continuationIndexBase, wrapperCount, startQpc, endQpc,
                  methodCompletions, exceptionCompletions, wrapperResets)
        {
        }

        internal AsyncProfilerCallStack(AsyncProfilerThreadKey thread, ulong dispatcherId,
            int depth, AsyncProfilerCallStackFramesIndex framesIndex, AsyncProfilerCallStackFrames frames,
            byte continuationIndexBase, byte wrapperCount, long startQpc, long endQpc,
            CompletionDelta[] methodCompletions, CompletionDelta[] exceptionCompletions, long[] wrapperResets)
        {
            Reset(thread, dispatcherId, depth, framesIndex, frames,
                continuationIndexBase, wrapperCount, startQpc, endQpc,
                methodCompletions, exceptionCompletions, wrapperResets);
        }

        /// <summary>The process/thread on which this activation resumed.</summary>
        internal AsyncProfilerThreadKey Thread { get; private set; }

        /// <summary>A compact handle to the interned frames, scoped to the originating async index.</summary>
        internal AsyncProfilerCallStackFramesIndex FramesIndex { get; private set; }

        /// <summary>The interned frames of this async call stack.</summary>
        internal AsyncProfilerCallStackFrames Frames { get; private set; }

        internal void Reset(AsyncProfilerThreadKey thread, ulong dispatcherId,
            int depth, AsyncProfilerCallStackFramesIndex framesIndex, AsyncProfilerCallStackFrames frames,
            byte continuationIndexBase, byte wrapperCount, long startQpc, long endQpc,
            CompletionDelta[] methodCompletions, CompletionDelta[] exceptionCompletions, long[] wrapperResets)
        {
            Thread = thread;
            DispatcherId = dispatcherId;
            Depth = depth;
            FramesIndex = framesIndex;
            Frames = frames;
            ContinuationIndexBase = continuationIndexBase;
            WrapperCount = wrapperCount;
            StartQpc = startQpc;
            EndQpc = endQpc;
            _methodCompletions = methodCompletions;
            _exceptionCompletions = exceptionCompletions;
            _wrapperResets = wrapperResets;
            MethodCompletionObserved = false;
            ExceptionCompletionObserved = false;
        }

        internal void Write(Serializer serializer)
        {
            serializer.Write((int)Thread.ProcessIndex);
            serializer.Write((long)Thread.OsThreadId);
            serializer.Write((long)DispatcherId);
            serializer.Write(Depth);
            serializer.Write((int)FramesIndex);
            serializer.Write((byte)ContinuationIndexBase);
            serializer.Write((byte)WrapperCount);
            serializer.Write(StartQpc);
            serializer.Write(EndQpc);

            serializer.Write(_methodCompletions.Length);
            for (int i = 0; i < _methodCompletions.Length; i++)
            {
                serializer.Write(_methodCompletions[i].Qpc);
                serializer.Write(_methodCompletions[i].Delta);
            }

            serializer.Write(_exceptionCompletions.Length);
            for (int i = 0; i < _exceptionCompletions.Length; i++)
            {
                serializer.Write(_exceptionCompletions[i].Qpc);
                serializer.Write(_exceptionCompletions[i].Delta);
            }

            serializer.Write(_wrapperResets.Length);
            for (int i = 0; i < _wrapperResets.Length; i++)
            {
                serializer.Write(_wrapperResets[i]);
            }
        }

        internal static AsyncProfilerCallStack Read(Deserializer deserializer, Func<AsyncProfilerCallStackFramesIndex, AsyncProfilerCallStackFrames> resolveFrames)
        {
            ProcessIndex processIndex = (ProcessIndex)deserializer.ReadInt();
            ulong osThreadId = (ulong)deserializer.ReadInt64();
            ulong dispatcherId = (ulong)deserializer.ReadInt64();
            int depth = deserializer.ReadInt();
            var framesIndex = (AsyncProfilerCallStackFramesIndex)deserializer.ReadInt();
            byte continuationIndexBase = deserializer.ReadByte();
            byte wrapperCount = deserializer.ReadByte();
            long startQpc = deserializer.ReadInt64();
            long endQpc = deserializer.ReadInt64();

            int methodCompletionCount = deserializer.ReadInt();
            CompletionDelta[] methodCompletions = methodCompletionCount == 0
                ? Array.Empty<CompletionDelta>()
                : new CompletionDelta[methodCompletionCount];
            for (int i = 0; i < methodCompletionCount; i++)
            {
                long timestampQpc = deserializer.ReadInt64();
                int delta = deserializer.ReadInt();
                methodCompletions[i] = new CompletionDelta(timestampQpc, delta);
            }

            int exceptionCompletionCount = deserializer.ReadInt();
            CompletionDelta[] exceptionCompletions = exceptionCompletionCount == 0
                ? Array.Empty<CompletionDelta>()
                : new CompletionDelta[exceptionCompletionCount];
            for (int i = 0; i < exceptionCompletionCount; i++)
            {
                long timestampQpc = deserializer.ReadInt64();
                int delta = deserializer.ReadInt();
                exceptionCompletions[i] = new CompletionDelta(timestampQpc, delta);
            }

            int wrapperResetCount = deserializer.ReadInt();
            long[] wrapperResets = wrapperResetCount == 0
                ? Array.Empty<long>()
                : new long[wrapperResetCount];
            for (int i = 0; i < wrapperResetCount; i++)
            {
                wrapperResets[i] = deserializer.ReadInt64();
            }

            return new AsyncProfilerCallStack(
                new AsyncProfilerThreadKey(processIndex, osThreadId), dispatcherId,
                depth, framesIndex, resolveFrames(framesIndex),
                continuationIndexBase, wrapperCount, startQpc, endQpc,
                methodCompletions, exceptionCompletions, wrapperResets);
        }

        private static int SumDeltasUpTo(CompletionDelta[] deltas, long timestampQpc)
        {
            int total = 0;
            for (int i = 0; i < deltas.Length; i++)
            {
                if (deltas[i].Qpc <= timestampQpc)
                {
                    total += deltas[i].Delta;
                }
                else
                {
                    break; // ascending
                }
            }
            return total;
        }

        internal readonly struct CompletionDelta
        {
            public readonly long Qpc;
            public readonly int Delta;
            public CompletionDelta(long timestampQpc, int delta) { Qpc = timestampQpc; Delta = delta; }
        }

        private sealed class CapturedFrameList : IReadOnlyList<AsyncProfilerCallStackFrame>
        {
            internal CapturedFrameList(AsyncProfilerCallStack activation) { m_activation = activation; }
            public int Count => m_activation.Frames.FrameCount;
            public AsyncProfilerCallStackFrame this[int index]
            {
                get
                {
                    if ((uint)index >= (uint)Count)
                    {
                        throw new ArgumentOutOfRangeException(nameof(index));
                    }
                    return new AsyncProfilerCallStackFrame(AsyncProfilerStitchedFrame.CreateAsync(m_activation, index));
                }
            }
            public IEnumerator<AsyncProfilerCallStackFrame> GetEnumerator()
            {
                for (int i = 0; i < Count; i++)
                {
                    yield return this[i];
                }
            }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            private readonly AsyncProfilerCallStack m_activation;
        }

        private CompletionDelta[] _methodCompletions;    // ascending by Qpc; CompleteMethod events (delta 1)
        private CompletionDelta[] _exceptionCompletions; // ascending by Qpc; Unwind events (delta = unwound frame count)
        private long[] _wrapperResets;                   // ascending
        private IReadOnlyList<AsyncProfilerCallStackFrame> m_capturedFrames;

        #endregion
    }
}
