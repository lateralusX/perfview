// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>Queries recorded async-profiler segments without a physical stack or output stack source.</summary>
    /// <remarks>
    /// Captured resume/append frames do not establish physical execution. Use
    /// <see cref="AsyncProfilerCallStackSourceBuilder"/> for physical/async reconstruction.
    /// This reads data indexed during trace conversion; it does not collect events, enable the runtime provider,
    /// or require an output stack source. It does not own or dispose the TraceLog, which must remain open while
    /// querying or resolving code addresses.
    /// </remarks>
    public sealed class AsyncProfilerCallStackComputer
    {
        /// <summary>Creates a query computer for <paramref name="traceLog"/>.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="traceLog"/> is null.</exception>
        public AsyncProfilerCallStackComputer(TraceLog traceLog)
        {
            m_log = traceLog ?? throw new ArgumentNullException(nameof(traceLog));
        }

        /// <summary>
        /// Returns recorded async segments at the exact trace QPC timestamp, ordered outermost-to-innermost.
        /// Frames are captured leaf-to-root, without completion trimming or presentation transforms.
        /// OS process reuse is resolved at the timestamp; threads without a TraceThread are supported.
        /// Results remain valid across later queries and index release. Missing data produces an empty list.
        /// </summary>
        /// <param name="processId">The reusable OS process ID, not a TraceLog ProcessIndex.</param>
        /// <param name="osThreadId">The OS thread ID, not a TraceLog ThreadIndex.</param>
        /// <param name="timestampQpc">An exact timestamp in the originating trace's QPC domain, not relative
        /// milliseconds or a UTC timestamp. Intervals include StartQpc and exclude EndQpc.</param>
        /// <returns>A non-null list of recorded activations covering the timestamp.</returns>
        public IReadOnlyList<AsyncProfilerCallStack> GetCallStacks(int processId, ulong osThreadId, long timestampQpc)
        {
            AsyncProfilerCallStacksIndex index = m_log.AsyncProfilerCallStacks;
            TraceProcess process = index == null ? null : m_log.Processes.GetProcess(processId, timestampQpc);
            return process == null
                ? Array.Empty<AsyncProfilerCallStack>()
                : index.GetCallStacks(new AsyncProfilerThreadKey(process.ProcessIndex, osThreadId), timestampQpc);
        }

        /// <summary>
        /// Queries recorded segments using an event's process, OS thread, and exact QPC timestamp.
        /// The event must belong to this computer's TraceLog instance, even if another instance opened the same
        /// file. A physical stack is not required, and the event is not retained. Ordering, captured-frame
        /// semantics, and result lifetime are the same as the explicit process/thread/QPC overload.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="anchorEvent"/> is null.</exception>
        /// <exception cref="ArgumentException">The event does not belong to this computer's TraceLog instance.</exception>
        public IReadOnlyList<AsyncProfilerCallStack> GetCallStacks(TraceEvent anchorEvent)
        {
            ValidateEvent(anchorEvent, m_log, nameof(anchorEvent));
#pragma warning disable CS0618 // Preserve exact event alignment inside TraceEvent.
            return GetCallStacks(anchorEvent.ProcessID, (ulong)anchorEvent.ThreadID, anchorEvent.TimeStampQPC);
#pragma warning restore CS0618
        }

        #region private

        internal static void ValidateEvent(TraceEvent data, TraceLog log, string parameterName)
        {
            if (data == null)
            {
                throw new ArgumentNullException(parameterName);
            }
            if (!ReferenceEquals(data.Log(), log))
            {
                throw new ArgumentException("The event must belong to the owning TraceLog.", parameterName);
            }
        }

        private readonly TraceLog m_log;

        #endregion
    }
}
