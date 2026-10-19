// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>Implementation-dependent layouts recognized by captured-runtime profiles.</summary>
    internal enum AsyncRuntimeSequenceKind
    {
        TaskCompletionPair,
        TaskCompletionChain,
        V2CompletionHandoff,
        V1Startup,
        V1AwaitRegistration,
        AsyncProfilerEntry,
        RuntimeResumptionBridge,
    }

    /// <summary>Replaceable presentation stages; flags and execution order remain pipeline policy.</summary>
    internal enum AsyncRuntimeTransformKind
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
    internal readonly struct AsyncRuntimeSequenceMatch
    {
        public static AsyncRuntimeSequenceMatch Match(int length) => new AsyncRuntimeSequenceMatch(length, true);

        public static AsyncRuntimeSequenceMatch Reject(int length) => new AsyncRuntimeSequenceMatch(length, false);

        public int Length { get; }
        public bool IsMatch { get; }
        public bool IsRejected => Length != 0 && !IsMatch;

        #region private

        private AsyncRuntimeSequenceMatch(int length, bool isMatch)
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
