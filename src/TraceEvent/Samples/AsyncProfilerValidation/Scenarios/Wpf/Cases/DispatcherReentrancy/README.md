# Controlled nested dispatcher execution

A does work, then calls a synchronous helper owning a real nested dispatcher
frame. B starts and suspends inside that loop, resumes at its own gate, and does
work while A/pump frames remain physically beneath it. B finishes, only the
owned loop exits, then A does more work.

**Why:** B needs its own callers without erasing legitimate physical A context.
After nesting ends, A must not retain stale B or duplicate CPU accounting.

| Project | Stable source ID | Mode |
|---|---|---|
| [V1 classic](V1/src/WpfAsyncScenarios.csproj) | `wpf.dispatcher-reentrancy.v1` | `nested-ui` |
| [V2 runtime-supported](V2/src/WpfAsyncScenarios.csproj) | `wpf.dispatcher-reentrancy.v2` | `nested-ui` |

This is actual `Dispatcher.PushFrame`, not ordinary alternating callbacks or a
blocking Task wait. An owned watchdog bounds failure; unexpected B faults
propagate rather than silently becoming success.

[Build/launch planning](../../README.md#prerequisites-and-recipes) ·
[observed results](../../../../Review/Wpf/Findings/DispatcherReentrancy.md).
