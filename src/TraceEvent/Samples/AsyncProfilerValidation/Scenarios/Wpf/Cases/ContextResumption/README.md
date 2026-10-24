# Context resumption

Three named async methods await one another. The deepest suspends at a closed
gate, then performs CPU work. One path uses bare awaits and captures WPF UI
context; the other uses literal `ConfigureAwait(false)`. Status returns to UI
separately after work.

**Why:** callers may disappear from the physical stack after await. The profile
should retain the CPU work and only the awaiting callers supported by recorded
evidence, with the deepest method identified as running.

| Family/project | Stable source ID | Modes |
|---|---|---|
| [V1 classic](V1/src/WpfAsyncScenarios.csproj) | `wpf.context-resumption.v1` | `capture-ui`, `configure-false` |
| [V2 runtime-supported](V2/src/WpfAsyncScenarios.csproj) | `wpf.context-resumption.v2` | `capture-ui`, `configure-false` |

Use this small gated workload as a reference for adding a case.
Initial suspension and real thread/context behavior must be proved, not assumed
from the configured-await expression. UI CPU work deliberately freezes the
window briefly.

[Build/launch planning](../../README.md#prerequisites-and-recipes) ·
[observed results, including V1 UI recording limits](../../../../Review/Wpf/Findings/ContextResumption.md).
