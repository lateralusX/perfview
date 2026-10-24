# Independent overlapping operations

A and B start through separate real WPF callbacks, each with its own three-level
chain and two closed gates. Releases select A first, B first, A second, B second.
Their lifetimes overlap; their UI CPU work is serial.

**Why:** sharing a UI thread must not cause one operation to inherit the other's
callers. Expect the right current method, supported own callers and no sibling
ancestry, without mistaking this for parallel CPU execution.

| Project | Stable source ID | Mode |
|---|---|---|
| [V1 classic](V1/src/WpfAsyncScenarios.csproj) | `wpf.overlapping-operations.v1` | `overlap-abab` |
| [V2 runtime-supported](V2/src/WpfAsyncScenarios.csproj) | `wpf.overlapping-operations.v2` | `overlap-abab` |

The controlled release order and independent gates are part of the test;
changing them is a new scenario variation, not a repetition of the old result.

[Build/launch planning](../../README.md#prerequisites-and-recipes) ·
[observed results](../../../../Review/Wpf/Findings/OverlappingOperations.md).
