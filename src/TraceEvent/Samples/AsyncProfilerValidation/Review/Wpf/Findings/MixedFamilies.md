# Mixed classic/runtime-supported callers

Finding ID: `wpf.mixed-families`.

The two real WPF chains are V1 -> V2 -> V1 and V2 -> V1 -> V2, with a gate and
CPU work in the deepest method. The question is which awaiting callers survive
the cross-family boundaries in the recording itself.

In both directions, the CPU resume records contain only the innermost scenario
method. The other source parents are absent already in original bytes, not
merely missing from an index query. Their later resumptions do not establish
their presence in the earlier CPU chain.

Public output faithfully retains recorded current work, physical prefixes,
roots, timing and weight without inventing awaiting parents.
**Full mixed-family ancestry is not established.** Both-family presence and
preserved accounting are not substitutes for that result.

No encoded-parent loss or confirmed API defect was demonstrated. This limited
recording behavior is not a global impossibility claim. Creation history is
not established as a substitute for missing await relationships.

[Scenario/source and both modes](../../../Scenarios/Wpf/Cases/MixedFamilies/README.md) ·
[evidence index](../EvidenceIndex.json) ·
[shared scope](../README.md#shared-api-path-and-limits).
