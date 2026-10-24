# Detailed V2 completion/exception records

Finding ID: `wpf.detailed-v2-events`.

The original V2 exception/cancellation recordings checked callers around CPU
work without detailed completion/unwind events. A separate repeat kept the
same app binaries and enabled only the source-verified additional event kinds.
There is no extra source project to migrate.

**Why:** correct handler stacks alone do not prove processing of optional
runtime progress records. Those records must advance the right context at
the right time, without applying future events early or counting a return twice.

Both event kinds were emitted, decoded and indexed correctly. Exact event-time
queries agree with original counts, while cleanup and fresh-operation stacks
remain correct. These counter queries are not physical CPU samples.

The sampled transition with exception progress showed runtime initialization,
not an executing application body. That synchronous work was retained without
inventing application execution. Not every brief application-return state or
other combination of enabled events was established.

Original baseline settings were preserved; the focused repeat's provider
difference is recorded in the [evidence index](../EvidenceIndex.json).
This is V2 event processing, not V1 or universal exception coverage, and no
confirmed TraceEvent defect was found.

[Exception source](../../../Scenarios/Wpf/Cases/ExceptionHandling/README.md) ·
[cancellation source](../../../Scenarios/Wpf/Cases/Cancellation/README.md) ·
[shared scope](../README.md#shared-api-path-and-limits).
