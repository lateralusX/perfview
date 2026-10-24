# Actual V2 helper-slot reuse

Finding ID: `wpf.wrapper-rollover`.

One WPF-started V2 return chain remains off UI long enough to reuse the runtime's
finite continuation-helper slots. The profile must advance to the new method
rather than interpreting a reused address as its earlier occupant.

**Actual reset and reuse were observed** in original records and physical
stacks within one uninterrupted worker activation. The new running method and
ordered remaining callers are correct before and after reuse. Sampled
application work, prefixes, roots, timing and weight are retained.

The CPU samples bracket the reset timestamp; none is relabeled as an
exactly-at-reset sample. Event-time index queries are a separate check.
The UI-depth case did not produce this reuse.

No confirmed TraceEvent defect resulted. This mechanism is V2-specific, with
no V1 port or repeated-wrap/exception/late-attach matrix claimed.

[Scenario/source](../../../Scenarios/Wpf/Cases/WrapperRollover/README.md) ·
[evidence index](../EvidenceIndex.json) ·
[shared scope](../README.md#shared-api-path-and-limits).
