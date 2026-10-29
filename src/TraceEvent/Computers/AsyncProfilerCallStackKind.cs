// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>The instrumentation family that emitted an async-profiler segment.</summary>
    /// <remarks>These values identify V1/V2 instrumentation, not the buffer version or per-event schema version.</remarks>
    public enum AsyncProfilerCallStackKind
    {
        /// <summary>Classic async state machines (V1).</summary>
        StateMachineAsync,
        /// <summary>Runtime-managed async continuations (V2).</summary>
        RuntimeAsync,
    }
}
