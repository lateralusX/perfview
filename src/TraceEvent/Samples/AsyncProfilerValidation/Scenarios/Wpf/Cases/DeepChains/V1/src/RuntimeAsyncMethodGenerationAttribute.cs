using System;

namespace System.Runtime.CompilerServices;

// Roslyn's experimental per-method opt-out is intentionally not defined by the BCL.
[AttributeUsage(AttributeTargets.Method)]
internal sealed class RuntimeAsyncMethodGenerationAttribute(bool runtimeAsync) : Attribute
{
    public bool RuntimeAsync { get; } = runtimeAsync;
}
