# WPF async-caller findings

**What:** check whether Johan's public TraceEvent stack APIs recover recorded
async callers around real sampled WPF work. **Why:** callers can disappear from
the physical stack after await; useful context must not lose CPU work, invent
execution, or attach another operation.

V1/classic and V2/runtime-supported describe method implementation families,
not API versions. The results below describe the covered workloads and recorded
configurations. [Scenario instructions](../../Scenarios/Wpf/README.md) describe
how to choose a case; provenance and the
[tool verification status](../../Scripts/README.md#verification-status) are separate.

## V2 methods

| Scenario | Why | Observed result |
|---|---|---|
| [UI/worker](Findings/ContextResumption.md) | Retain callers after changing context | Both explicit paths recover the tested full chain. |
| [Depth/returns](Findings/DeepChains.md) | Advance current/remaining callers | Correct ordered chains through the tested depths; not rollover. |
| [Overlap](Findings/OverlappingOperations.md) | Separate unrelated operations on one thread | Own A/B callers retained; no sibling contamination. |
| [Exception](Findings/ExceptionHandling.md), [cancellation/fresh](Findings/Cancellation.md) | Keep cleanup and avoid stale callers | Correct cleanup/fresh-operation callers; original recordings omitted detailed V2 events. |
| [Detailed events](Findings/DetailedV2Events.md) | Check optional record processing separately | Same apps, extra records emitted and applied at the right times; brief live states remain limited. |
| [Nested dispatcher](Findings/DispatcherReentrancy.md) | Distinguish B while retaining real outer A/pump | Correct nesting and return to A, not duplicate CPU. |
| [Wrapper reuse](Findings/WrapperRollover.md) | Handle finite tracking-slot reuse | Actual reset/reuse observed; correct before/after output, no exactly-at-reset CPU sample. |
| [WhenAll](Findings/WhenAll.md) | Separate branches and parent-after-join work | Execution chains correct; not a complete dependency graph. |

The preserved [dynamic-policy diagnostic](Findings/DynamicAwaitPolicy.md)
recorded incomplete chains because a getter changed the recognized call shape.
The separate explicit-policy pair removed that confounder; this was not a
WPF or TraceEvent defect.

## V1 methods alone

The same applicable designs ran as classic-only sample bodies. Worker baseline
and WhenAll branch chains were retained. **Classic UI recordings often omit
awaiting parents already in the original records.** Deep returning work,
overlap, cleanup/fresh isolation and nested execution identify the right work,
but are not full V1 UI-ancestry passes. Each case's finding keeps the V1/V2
distinction. V2 wrapper rollover/detail events are not V1 coverage.

## Mixed families and graph relationships

[Both mixed directions](Findings/MixedFamilies.md) retain recorded current work
but omit other source parents in the original CPU records. No complete mixed
ancestry or confirmed API loss was demonstrated.
[Graph feasibility](AsyncGraph.md) separately proves branch Task identities and
join membership, but lacks the V2 parent's recorded wait on that join.
Creation is not waiting, and branches must not be flattened into a guessed stack.

## Shared API path and limits

The tested configuration uses the identity in [EvidenceIndex.json](EvidenceIndex.json),
the unchanged signed helper, public `SampleProfilerThreadTimeComputer` constructor,
`EnableAsyncCallStacks`, normalization/cleanup/annotation controls,
`GenerateThreadTimeStacks`, and post-generation `AsyncCallStacksActive`.
Fresh computers/sources generated raw, structural, default and optional
machinery-cleanup views. Internal probes supported those results, not a new
reconstructor or public graph API.

The tested setup is coherent .NET 11 preview Core/WindowsDesktop. Common rules
are selected because captured version fields are empty; symbol-path/binding
warning limits remain. No confirmed TraceEvent defect was found in these cases. Results are
not all-API, ETW, general concurrency, human visual or performance certification.
Raw evidence is retained separately, **not checked in or yet shared**.
