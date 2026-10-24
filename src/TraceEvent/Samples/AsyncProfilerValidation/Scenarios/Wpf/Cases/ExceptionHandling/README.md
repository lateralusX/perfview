# Expected exception, typed catch and finally

Three methods await one another. The deepest suspends at a gate, performs CPU
work and throws the dedicated expected exception. It passes through the middle
method to the outer typed catch and finally, each with named CPU work.

**Why:** retain handler/cleanup CPU under its caller without marking failed
children as still awaiting or assigning them that work.

| Project | Stable source ID | Mode |
|---|---|---|
| [V1 classic](V1/src/WpfAsyncScenarios.csproj) | `wpf.exception-handling.v1` | `exception-ui` |
| [V2 runtime-supported](V2/src/WpfAsyncScenarios.csproj) | `wpf.exception-handling.v2` | `exception-ui` |

Unexpected faults are failures, not acceptable substitutes for the intended
exception. The V2 comparison with detailed events uses the same V2 source,
not another project or a different exception workload.

[Build/launch planning](../../README.md#prerequisites-and-recipes) ·
[observed caller results](../../../../Review/Wpf/Findings/ExceptionHandling.md) ·
[separate detailed-event finding](../../../../Review/Wpf/Findings/DetailedV2Events.md).
