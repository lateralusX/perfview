# Cancellation followed by fresh work

Cancel a closed gate with its intended token. The outer caller handles
cancellation, performs cleanup and rethrows, so the root really ends cancelled.
A separate UI callback then starts fresh state, token, gate and named work.

**Why:** cleanup must stay with its caller, while the later operation on the
same UI thread must not acquire cancelled callers.

| Project | Stable source ID | Mode |
|---|---|---|
| [V1 classic](V1/src/WpfAsyncScenarios.csproj) | `wpf.cancellation.v1` | `cancel-fresh-ui` |
| [V2 runtime-supported](V2/src/WpfAsyncScenarios.csproj) | `wpf.cancellation.v2` | `cancel-fresh-ui` |

A cancellation request alone is insufficient: check terminal Task/token state,
handler/finally counts, fresh success and actual contexts. This is one controlled
sequence, not a cancellation timing matrix.

[Build/launch planning](../../README.md#prerequisites-and-recipes) ·
[observed results](../../../../Review/Wpf/Findings/Cancellation.md) ·
[same V2 app with detailed events](../../../../Review/Wpf/Findings/DetailedV2Events.md).
