// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Stacks;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>
    /// Reconstructs physical/async call stacks in an existing <see cref="MutableTraceEventStackSource"/>.
    /// The physical input is independent of its event provider or sampling mechanism; logical reconstruction
    /// requires async-profiler data already indexed in the output's TraceLog.
    /// </summary>
    /// <remarks>
    /// Reuse this builder across inputs from the output's TraceLog. Returned indices belong to that output
    /// and can be inspected immediately without adding samples or calling DoneAddingSamples.
    /// Stop producing stacks before finalizing the output. Access must be serialized, including custom
    /// callbacks; this builder is not thread-safe or reentrant. It does not own or dispose the TraceLog.
    /// Finish constructing the TraceLog's physical stack and code-address tables before creating the output.
    /// Keep that TraceLog open while producing stacks or resolving frames. Previously returned indices remain
    /// valid as more stacks are added. Unique interned frames/stacks and any caller-added samples accumulate
    /// while the output is retained; no finalization call is needed for stack-only use.
    /// </remarks>
    public sealed class AsyncProfilerCallStackSourceBuilder
    {
        /// <summary>Creates a builder that interns reconstructed stacks into <paramref name="output"/>.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="output"/> is null.</exception>
        public AsyncProfilerCallStackSourceBuilder(MutableTraceEventStackSource output)
        {
            m_output = output ?? throw new ArgumentNullException(nameof(output));
            m_log = output.TraceLog;
            var pipeline = new AsyncProfilerCallStackTransformPipeline();
            Transforms = new AsyncCallStackTransforms(pipeline);
            m_core = AsyncProfilerCallStackSourceBuilderCore.TryCreate(m_log, output, pipeline, null);
        }

        /// <summary>Presentation settings and custom transforms for this builder. Configure before producing
        /// stacks. Normalization defaults to enabled; infrastructure cleanup, annotations, and parent contexts
        /// default to disabled.</summary>
        public AsyncCallStackTransforms Transforms { get; }

        /// <summary>
        /// Reconstructs a physical stack at the exact trace QPC timestamp and returns its index in the output.
        /// The physical stack must belong to the output's TraceLog; its root identifies the process and thread.
        /// The default Invalid root selects normal thread/process grouping. An explicit root must belong to the output.
        /// Missing async data uses the output's normal physical-stack conversion without custom transforms.
        /// An Invalid physical stack returns Invalid, provided the other arguments and builder state are valid.
        /// No samples are added, timestamps or weights assigned, or output finalization performed.
        /// </summary>
        /// <param name="physicalStack">A physical CallStackIndex from the output's TraceLog.</param>
        /// <param name="timestampQpc">The reconstruction time in that trace's QPC domain, not relative milliseconds.</param>
        /// <param name="root">A grouping stack in the output, or Invalid for the normal thread/process root.</param>
        /// <remarks>
        /// Bare stack handles carry no owner identity. Range checks cannot detect a same-valued handle from a
        /// different TraceLog or output; the caller must ensure ownership. The event overload checks its log instance.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">A non-Invalid physical stack or root is outside its table's range.</exception>
        /// <exception cref="InvalidOperationException">The output was finalized, or the builder is called reentrantly.</exception>
        public StackSourceCallStackIndex GetCallStack(
            CallStackIndex physicalStack,
            long timestampQpc,
            StackSourceCallStackIndex root = StackSourceCallStackIndex.Invalid)
        {
            if (m_output.IsFinalized)
            {
                throw new InvalidOperationException("Cannot produce call stacks after the output has been finalized.");
            }
            if (physicalStack != CallStackIndex.Invalid &&
                ((int)physicalStack < 0 || (int)physicalStack >= m_log.CallStacks.Count))
            {
                throw new ArgumentOutOfRangeException(nameof(physicalStack));
            }
            if (root != StackSourceCallStackIndex.Invalid &&
                ((int)root < 0 || (int)root >= m_output.CallStackIndexLimit))
            {
                throw new ArgumentOutOfRangeException(nameof(root));
            }
            if (m_busy)
            {
                throw new InvalidOperationException("Async call-stack production is not reentrant.");
            }
            if (physicalStack == CallStackIndex.Invalid)
            {
                return StackSourceCallStackIndex.Invalid;
            }
            m_busy = true;
            try
            {
                TraceThread thread = m_log.CallStacks.Thread(physicalStack);
                bool defaultRoot = root == StackSourceCallStackIndex.Invalid;
                if (defaultRoot)
                {
                    root = m_output.GetCallStackForThread(thread);
                }
                if (m_core != null &&
                    m_core.TryGetCallStack(physicalStack, timestampQpc, thread, root, out var stitched))
                {
                    return stitched;
                }
                return defaultRoot
                    ? m_output.GetCallStackThread(physicalStack, thread)
                    : m_output.GetCallStack(physicalStack, root);
            }
            finally
            {
                m_busy = false;
            }
        }

        /// <summary>
        /// Reconstructs an event's physical stack using its exact QPC timestamp. The event must belong to the
        /// output's TraceLog instance; opening the same file twice does not make the instances interchangeable.
        /// Events without a physical stack return Invalid when the root and builder state are valid, regardless
        /// of provider. The event is not retained. Root, fallback, and output semantics are the same as the
        /// explicit physical-stack/QPC overload.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="data"/> is null.</exception>
        /// <exception cref="ArgumentException">The event does not belong to the output's TraceLog instance.</exception>
        /// <exception cref="ArgumentOutOfRangeException">A non-Invalid root is outside the output's stack range.</exception>
        /// <exception cref="InvalidOperationException">The output was finalized, or the builder is called reentrantly.</exception>
        public StackSourceCallStackIndex GetCallStack(
            TraceEvent data, StackSourceCallStackIndex root = StackSourceCallStackIndex.Invalid)
        {
            AsyncProfilerCallStackComputer.ValidateEvent(data, m_log, nameof(data));
#pragma warning disable CS0618 // Keep exact event QPC access inside TraceEvent.
            return GetCallStack(data.CallStackIndex(), data.TimeStampQPC, root);
#pragma warning restore CS0618
        }

        #region private

        private readonly MutableTraceEventStackSource m_output;
        private readonly TraceLog m_log;
        private readonly AsyncProfilerCallStackSourceBuilderCore m_core;
        private bool m_busy;

        #endregion
    }
}
