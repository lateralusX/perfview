# Two branches and a real Task.WhenAll join

The UI-started parent materializes A/B branch Tasks, each with an independent
gate and identifiable three-level worker chain, then genuinely awaits their
`Task.WhenAll` join. A is released first while B stays gated; parent CPU work
begins on UI only after B also finishes.

**Why:** keep siblings separate and identify parent work when it resumes,
without flattening branches into one call stack or inventing execution by a
waiting parent. Materialized branch Tasks are legitimate fan-out, not a defect.

| Project | Stable source ID | Mode |
|---|---|---|
| [V1 classic](V1/src/WpfAsyncScenarios.csproj) | `wpf.whenall.v1` | `whenall-ui` |
| [V2 runtime-supported](V2/src/WpfAsyncScenarios.csproj) | `wpf.whenall.v2` | `whenall-ui` |

Check both initial suspensions, A-only parent incompleteness, expected results,
exactly-once completions and actual branch/parent contexts. Correct execution
chains do not themselves prove a full dependency graph.

[Build/launch planning](../../README.md#prerequisites-and-recipes) ·
[observed results](../../../../Review/Wpf/Findings/WhenAll.md) ·
[separate graph feasibility investigation](../../../../Review/Wpf/AsyncGraph.md).
