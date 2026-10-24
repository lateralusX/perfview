# Branch isolation and parent-after-join work

Finding ID: `wpf.whenall`.

The parent starts two independently gated branches and awaits their real
`Task.WhenAll` join. A finishes while B stays gated; parent CPU work begins only
after B also completes. Materializing branch Tasks is legitimate fan-out.

**Why:** sibling work must not become sibling ancestry, and a waiting parent
must not be invented as physically running.

## V1 and V2 execution results

Both classic and runtime-supported variants keep each off-UI branch's tested
three-level recorded execution chain separate. A alone cannot resume the
parent. Both results/completions are checked, and later UI CPU belongs to the
parent without stale branch callers. Work and accounting remain intact.

The original branch CPU records do not contain the full branch-to-join-parent
relationship. Correct branch chains and parent-after-join stacks do not establish
a complete Task dependency graph. This is overlapping lifetime/join behavior,
not a simultaneous-CPU claim or a third async family.

The [separate V2 graph investigation](../AsyncGraph.md) collected additional
relationship records from the unchanged app. It proves part of that graph but
still lacks the parent's recorded wait on the join. No confirmed TraceEvent
execution-stack defect was found.

[Scenario/source](../../../Scenarios/Wpf/Cases/WhenAll/README.md) ·
[evidence index](../EvidenceIndex.json) ·
[shared scope](../README.md#shared-api-path-and-limits).
