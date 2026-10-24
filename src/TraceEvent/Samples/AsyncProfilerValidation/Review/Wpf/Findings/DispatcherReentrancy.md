# Real nested dispatcher execution

Finding ID: `wpf.dispatcher-reentrancy`.

A remains physically inside an owned nested dispatcher loop while independent
B runs within it; then A continues. The question is whether public output
can distinguish B without deleting legitimate outer A/pump context.

## V2 runtime-supported methods

Actual physical stacks establish nesting, not just phase labels. B keeps its
own tested chain and supported outer A context remains beneath the real pump.
A afterward has no stale B. The retained outer context is not a second CPU
sample or evidence that A and B consumed CPU simultaneously.

## V1 classic methods

The outer A activation survives while a separate inner B activation runs.
Physical A/pump frames remain, and A continues without stale B. Awaiting UI
parents are incompletely recorded for each chain; those missing callers are
not invented to match the V2 result.

Both variants retain work, WPF/application context, roots and accounting.
Only the owned loop exits and the operations finish normally. No confirmed
TraceEvent defect was found. This is one controlled nesting pattern, not
every dispatcher reentrancy behavior.

[Scenario/source](../../../Scenarios/Wpf/Cases/DispatcherReentrancy/README.md) ·
[evidence index](../EvidenceIndex.json) ·
[shared scope](../README.md#shared-api-path-and-limits).
