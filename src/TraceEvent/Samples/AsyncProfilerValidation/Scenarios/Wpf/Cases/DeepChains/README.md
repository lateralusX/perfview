# Deep chains and returning-parent work

The deepest method waits at two independent gates and does recognizable CPU work
after each release. Every parent then does work as the chain returns toward its
root. Bare awaits keep these phases on the real UI dispatcher.

**Why:** identify the right running level, retain supported awaiting callers in
order, and do not revive finished children. Depth is not proof of wrapper reuse.

| Project | Stable source ID | Covered modes |
|---|---|---|
| [V1 classic](V1/src/WpfAsyncScenarios.csproj) | `wpf.deep-chains.v1` | `depth-64` |
| [V2 runtime-supported](V2/src/WpfAsyncScenarios.csproj) | `wpf.deep-chains.v2` | `depth-08`, `depth-32`, `depth-64` |

Each generated C# level has a distinct name. The V1 project also contains
other generated depths, but they are not claimed as
executed V1 coverage or advertised runner modes.

[Build/launch planning](../../README.md#prerequisites-and-recipes) ·
[observed results](../../../../Review/Wpf/Findings/DeepChains.md) ·
[separate V2 rollover case](../WrapperRollover/README.md).
