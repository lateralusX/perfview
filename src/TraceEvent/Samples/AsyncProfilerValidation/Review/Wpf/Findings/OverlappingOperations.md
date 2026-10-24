# Independent overlapping operations

Finding ID: `wpf.overlapping-operations`.

Two separately started operations have independent gates and alternate A0, B0,
A1, B1 on the same UI dispatcher. The question is ownership: a shared thread
must not cause one operation to acquire the other's callers.

## V2 runtime-supported methods

Original records and public output retain each operation's full tested
three-level caller chain across both rounds. Stable distinct ownership and
phase-wide checks find no sibling method in the physical, indexed or rendered
chains. The deepest method is running; its own parents remain awaiting.

## V1 classic methods

The same gate/control sequence keeps A/B contexts separate without sibling
contamination. Their awaiting UI parents are incompletely recorded, as in the
classic UI baseline. Correct ownership is not a full V1 ancestry pass.

Both variants preserve sampled work and accounting. Lifetimes overlap, but
UI CPU work is **serial**, not simultaneous. One controlled release order does
not certify every interleaving or general concurrency. No confirmed TraceEvent
defect resulted.

[Scenario/source](../../../Scenarios/Wpf/Cases/OverlappingOperations/README.md) ·
[evidence index](../EvidenceIndex.json) ·
[shared scope](../README.md#shared-api-path-and-limits).
