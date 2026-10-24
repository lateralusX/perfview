# Cancellation followed by fresh work

Finding ID: `wpf.cancellation`.

The first root genuinely cancels with the intended token after handler/finally
work. Only then does a new callback start independent fresh work on the same
UI thread. The profile must keep cleanup under its caller without carrying
cancelled context into the later operation.

## V2 runtime-supported methods

Handler and finally belong to the cancelled outer caller; failed children are
not left awaiting or current. The fresh operation has its own complete tested
three-level chain and succeeds without cancelled ancestry.

Original recordings omitted detailed V2 completion/unwind events.
The [same-app repeat](DetailedV2Events.md) checks their processing separately,
not retroactively as part of the original CPU-caller check.

## V1 classic methods

The intended cancelled Task/token and once-only cleanup are proved, and fresh
work succeeds in a separate context without cancelled callers. Fresh awaiting
UI parents are incompletely recorded, so full classic UI ancestry is not
established. The baseline already contained classic completion/exception
records; exact-time counter checks agree with them.

Recorded work and accounting are retained in both variants. Not every brief
exception-return state or cancellation timing is covered. No confirmed
TraceEvent defect was found.

[Scenario/source](../../../Scenarios/Wpf/Cases/Cancellation/README.md) ·
[evidence index](../EvidenceIndex.json) ·
[shared scope](../README.md#shared-api-path-and-limits).
