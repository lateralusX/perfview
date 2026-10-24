# Depth and return progress

Finding ID: `wpf.deep-chains`.

The deepest method waits twice and does work after both releases; each parent
then does work while the chain returns. The profile must advance to the right
running level and keep only supported remaining callers.

## V2 runtime-supported methods

The tested UI chains at depths 8, 32 and 64 retain their complete ordered
recorded suffixes and correct current/awaiting roles. Finished inner methods
are not revived. Application CPU work, prefixes, roots, timing and weight
remain intact.

**No rollover happened in these runs.** UI resumption did not advance one
uninterrupted wrapper sequence through reuse. The separate
[off-UI wrapper case](WrapperRollover.md) observed that mechanism;
depth64 alone is not evidence of it.

## V1 classic methods

The depth64 counterpart covers two leaf suspensions and work in every returning
parent. Each sampled phase names the correct method. Most remaining awaiting
UI parents are absent already in original records, so this is not a full deep
classic-ancestry pass. Depth8/32 source was present in its original project but
was not exercised as V1 coverage.

No confirmed TraceEvent defect was found. These are progress observations,
not capacity, recursive-call or performance certification.

[Scenario/source](../../../Scenarios/Wpf/Cases/DeepChains/README.md) ·
[evidence index](../EvidenceIndex.json) ·
[shared scope](../README.md#shared-api-path-and-limits).
