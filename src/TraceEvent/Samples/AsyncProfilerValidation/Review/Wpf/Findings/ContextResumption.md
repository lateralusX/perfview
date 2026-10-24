# Context resumption

Finding ID: `wpf.context-resumption`.

Three named methods suspend at a closed gate, then do deepest-method CPU work.
The question is whether callers survive UI versus worker resumption without
losing the physical work or inventing execution.

## V2 runtime-supported methods

Bare awaits resume on the real UI dispatcher; literal configured-false awaits
resume off UI in this guaranteed-suspension case. Original records contain
the complete three-level chain in both paths. Public output retains it once,
in order, with the deepest method running and its parents awaiting.

This removes the getter confounder in the separate
[dynamic-policy diagnostic](DynamicAwaitPolicy.md). Very short parent callbacks
after the CPU window were not independently established as parent CPU windows.
Configured-false is not a universal promise of a thread switch.

## V1 classic methods

The worker recording/output retains all three scenario callers. The UI CPU
record contains the running deepest method, not its two awaiting scenario
parents. That gap exists in original bytes; it is not shown to be TraceEvent
dropping encoded parents. The recorded work is handled correctly, but full
classic UI ancestry is not established.

Both families retain application CPU work, physical prefixes, roots, timing
and weight across views. No confirmed consumer defect resulted.

[Scenario and source projects](../../../Scenarios/Wpf/Cases/ContextResumption/README.md) ·
[evidence IDs/availability](../EvidenceIndex.json) ·
[shared scope](../README.md#shared-api-path-and-limits).
