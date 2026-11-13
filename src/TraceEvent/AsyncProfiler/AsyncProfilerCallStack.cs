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
    /// One recorded activation of an async call stack on a thread. Nested activations can overlap in time.
    /// </summary>
    /// <remarks>
    /// Obtain instances from <see cref="AsyncProfilerCallStackComputer"/>.
    /// Query results and captured-frame views remain valid across later queries and index release.
    /// Instances and frames supplied to transform callbacks are valid only during the callback.
    /// Keep the originating TraceLog open while resolving code addresses.
    /// </remarks>
    public sealed class AsyncProfilerCallStack
    {
        /// <summary>The unique process instance in the originating TraceLog.</summary>
        public ProcessIndex ProcessIndex => Thread.ProcessIndex;

        /// <summary>The OS thread identifier for this activation.</summary>
        public ulong OsThreadId => Thread.OsThreadId;

        /// <summary>The instrumentation family that emitted this activation.</summary>
        public AsyncProfilerCallStackKind Kind => Frames.Kind == AsyncCallstackKind.StateMachineAsync
            ? AsyncProfilerCallStackKind.StateMachineAsync
            : AsyncProfilerCallStackKind.RuntimeAsync;

        /// <summary>The captured frames, leaf-to-root, including later V1 appends.
        /// Completion progress and presentation transforms are not applied; these frames do not identify
        /// the currently executing method.</summary>
        public IReadOnlyList<AsyncProfilerCallStackFrame> CapturedFrames => m_capturedFrames ?? (m_capturedFrames = new CapturedFrameList(this));

        /// <summary>The nesting depth (0 = outermost) of this async call stack on its thread.</summary>
        public int Depth { get; private set; }

        /// <summary>The inclusive resume timestamp in the trace's QPC domain.</summary>
        public long StartQpc { get; private set; }

        /// <summary>The exclusive end timestamp in the trace's QPC domain; <see cref="long.MaxValue"/> if open at stream end.</summary>
        public long EndQpc { get; private set; }

        #region private

        internal AsyncProfilerCallStack(
            int depth,
            AsyncProfilerCallStackFramesIndex framesIndex,
            AsyncProfilerCallStackFrames frames,
            byte continuationIndexBase,
            byte wrapperCount,
            long startQpc,
            long endQpc,
            CompletionDelta[] methodCompletions,
            CompletionDelta[] exceptionCompletions,
            long[] wrapperResets)
            : this(default, 0, depth, framesIndex, frames, continuationIndexBase, wrapperCount, startQpc, endQpc, methodCompletions, exceptionCompletions, wrapperResets)
        {
        }

        internal AsyncProfilerCallStack(
            AsyncProfilerThreadKey thread,
            ulong dispatcherId,
            int depth,
            AsyncProfilerCallStackFramesIndex framesIndex,
            AsyncProfilerCallStackFrames frames,
            byte continuationIndexBase,
            byte wrapperCount,
            long startQpc,
            long endQpc,
            CompletionDelta[] methodCompletions,
            CompletionDelta[] exceptionCompletions,
            long[] wrapperResets)
        {
            Reset(thread, dispatcherId, depth, framesIndex, frames, continuationIndexBase, wrapperCount, startQpc, endQpc, methodCompletions, exceptionCompletions, wrapperResets);
        }

        // Both flags cover this process and instrumentation kind for the configuration interval containing StartQpc.
        // Observed events do not guarantee complete capture.

        /// <summary>Whether normal method-completion events were observed.</summary>
        internal bool MethodCompletionObserved { get; set; }

        /// <summary>Whether exception-unwind events were observed.</summary>
        internal bool ExceptionCompletionObserved { get; set; }

        /// <summary>The dispatcher identifier. Multiple activations can share the same identifier.</summary>
        internal ulong DispatcherId { get; private set; }

        /// <summary>The continuation-wrapper slot captured at resume.</summary>
        internal byte ContinuationIndexBase { get; private set; }

        /// <summary>The continuation-wrapper pool size for this activation, or 0 when unavailable.</summary>
        internal byte WrapperCount { get; private set; }

        internal AsyncProfilerThreadKey Thread { get; private set; }

        // Scoped to the originating async index.
        internal AsyncProfilerCallStackFramesIndex FramesIndex { get; private set; }

        internal AsyncProfilerCallStackFrames Frames { get; private set; }

        internal IReadOnlyList<CompletionDelta> MethodCompletions => _methodCompletions;

        internal IReadOnlyList<CompletionDelta> ExceptionCompletions => _exceptionCompletions;

        /// <summary>
        /// The number of leaf frames completed at or before <paramref name="timestampQpc"/>, from recorded
        /// normal-completion and exception-unwind events. Missing events are not inferred.
        /// The timestamp is not range-checked, and the count is not capped at <c>CapturedFrames.Count</c>.
        /// </summary>
        internal int GetCompletedFrameCount(long timestampQpc) => GetMethodCompletedFrameCount(timestampQpc) + GetExceptionCompletedFrameCount(timestampQpc);

        /// <summary>
        /// The V2 completion count derived from recorded wrapper resets and <paramref name="currentMethodIndex"/>,
        /// the physical wrapper slot at <paramref name="timestampQpc"/>.
        /// Progress is relative to <see cref="ContinuationIndexBase"/> and is clamped to zero or greater.
        /// This count already includes exceptional unwinds; do not add them again.
        /// </summary>
        /// <remarks>
        /// The timestamp and slot must refer to this activation. Neither is range-checked,
        /// and the result is not capped at <c>CapturedFrames.Count</c>.
        /// </remarks>
        internal int GetCompletedFrameCount(long timestampQpc, int currentMethodIndex)
        {
            int completed = (GetWrapperResetCount(timestampQpc) * WrapperCount) + currentMethodIndex - ContinuationIndexBase;
            return completed > 0 ? completed : 0;
        }

        /// <summary>
        /// The number of leaf frames completed at or before <paramref name="timestampQpc"/>, from recorded
        /// normal-completion events. Missing events are not inferred.
        /// </summary>
        internal int GetMethodCompletedFrameCount(long timestampQpc) => SumDeltasUpTo(_methodCompletions, timestampQpc);

        /// <summary>
        /// The number of leaf frames unwound at or before <paramref name="timestampQpc"/>, from recorded
        /// exception-unwind events. Missing events are not inferred.
        /// </summary>
        internal int GetExceptionCompletedFrameCount(long timestampQpc) => SumDeltasUpTo(_exceptionCompletions, timestampQpc);

        /// <summary>
        /// The number of continuation-wrapper resets recorded at or before <paramref name="timestampQpc"/>
        /// during this activation.
        /// </summary>
        internal int GetWrapperResetCount(long timestampQpc)
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

        internal void Reset(
            AsyncProfilerThreadKey thread,
            ulong dispatcherId,
            int depth,
            AsyncProfilerCallStackFramesIndex framesIndex,
            AsyncProfilerCallStackFrames frames,
            byte continuationIndexBase,
            byte wrapperCount,
            long startQpc,
            long endQpc,
            CompletionDelta[] methodCompletions,
            CompletionDelta[] exceptionCompletions,
            long[] wrapperResets)
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
            CompletionDelta[] methodCompletions = methodCompletionCount == 0 ? Array.Empty<CompletionDelta>() : new CompletionDelta[methodCompletionCount];
            for (int i = 0; i < methodCompletionCount; i++)
            {
                long timestampQpc = deserializer.ReadInt64();
                int delta = deserializer.ReadInt();
                methodCompletions[i] = new CompletionDelta(timestampQpc, delta);
            }

            int exceptionCompletionCount = deserializer.ReadInt();
            CompletionDelta[] exceptionCompletions = exceptionCompletionCount == 0 ? Array.Empty<CompletionDelta>() : new CompletionDelta[exceptionCompletionCount];
            for (int i = 0; i < exceptionCompletionCount; i++)
            {
                long timestampQpc = deserializer.ReadInt64();
                int delta = deserializer.ReadInt();
                exceptionCompletions[i] = new CompletionDelta(timestampQpc, delta);
            }

            int wrapperResetCount = deserializer.ReadInt();
            long[] wrapperResets = wrapperResetCount == 0 ? Array.Empty<long>() : new long[wrapperResetCount];
            for (int i = 0; i < wrapperResetCount; i++)
            {
                wrapperResets[i] = deserializer.ReadInt64();
            }

            return new AsyncProfilerCallStack(
                new AsyncProfilerThreadKey(processIndex, osThreadId),
                dispatcherId,
                depth,
                framesIndex,
                resolveFrames(framesIndex),
                continuationIndexBase,
                wrapperCount,
                startQpc,
                endQpc,
                methodCompletions,
                exceptionCompletions,
                wrapperResets);
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
                    break;
                }
            }
            return total;
        }

        internal readonly struct CompletionDelta
        {
            public readonly long Qpc;
            public readonly int Delta;
            public CompletionDelta(long timestampQpc, int delta)
            {
                Qpc = timestampQpc;
                Delta = delta;
            }
        }

        private sealed class CapturedFrameList : IReadOnlyList<AsyncProfilerCallStackFrame>
        {
            internal CapturedFrameList(AsyncProfilerCallStack activation)
            {
                m_activation = activation;
            }

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

        // All three timelines are ordered by QPC.
        private CompletionDelta[] _methodCompletions;    // Each delta counts one completed method.
        private CompletionDelta[] _exceptionCompletions; // Each delta counts unwound frames.
        private long[] _wrapperResets;
        private IReadOnlyList<AsyncProfilerCallStackFrame> m_capturedFrames;

        #endregion
    }
}
