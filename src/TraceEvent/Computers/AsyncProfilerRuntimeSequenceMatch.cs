// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>Implementation-dependent layouts recognized by captured-runtime profiles.</summary>
    internal enum AsyncProfilerRuntimeSequenceKind
    {
        TaskCompletionPair,
        TaskCompletionChain,
        V2CompletionHandoff,
        V1Startup,
        V1AwaitRegistration,
        ProfilerEntry,
        RuntimeResumptionBridge,
    }

    /// <summary>Replaceable presentation stages; flags and execution order remain pipeline policy.</summary>
    internal enum AsyncProfilerRuntimeTransformKind
    {
        SampledV2Wrapper,
        ReentrantHandoff,
        V1Presentation,
        InfrastructureCleanup,
    }

    /// <summary>
    /// A match consumes Length frames; a rejection protects Length frames from later individual cleanup.
    /// The default value means no candidate and leaves existing per-frame cleanup policy unchanged.
    /// </summary>
    internal readonly struct AsyncProfilerRuntimeSequenceMatch
    {
        public static AsyncProfilerRuntimeSequenceMatch Match(int length) => new AsyncProfilerRuntimeSequenceMatch(length, true);

        public static AsyncProfilerRuntimeSequenceMatch Reject(int length) => new AsyncProfilerRuntimeSequenceMatch(length, false);

        public int Length { get; }
        public bool IsMatch { get; }
        public bool IsRejected => Length != 0 && !IsMatch;

        #region private

        private AsyncProfilerRuntimeSequenceMatch(int length, bool isMatch)
        {
            if (length <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }
            Length = length;
            IsMatch = isMatch;
        }

        #endregion
    }
}
