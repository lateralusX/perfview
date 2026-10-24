# Actual V2 helper-slot reuse

A WPF-started chain uses literal configured-false awaits, suspends at a gate,
then returns continuously through named methods doing worker CPU work. Only
final status returns to UI. There are no intervening UI hops resetting progress.

**Why:** finite continuation-helper slots are reused. The profile must advance
to the new running method and remaining callers, not identify an earlier slot
occupant. Source depth alone is insufficient; require the recorded reset and
actual reused physical slots.

[V2 project](V2/src/WpfAsyncScenarios.csproj): `wpf.wrapper-rollover.v2`,
mode `rollover-off-ui`. There is no V1 port: this mechanism is V2-specific.

Before/after sample evidence must not be relabeled as a CPU sample exactly at
the reset.

[Build/launch planning](../../README.md#prerequisites-and-recipes) ·
[observed results](../../../../Review/Wpf/Findings/WrapperRollover.md).
