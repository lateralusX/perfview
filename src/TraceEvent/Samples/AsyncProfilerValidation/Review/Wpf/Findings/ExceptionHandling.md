# Expected exception and cleanup

Finding ID: `wpf.exception-handling`.

The deepest awaited method does work and throws the intended exception.
The outer typed catch and finally do their own work. A correct profile keeps
those handler costs without reviving failed children or assigning them the
outer caller's CPU work.

## V2 runtime-supported methods

Before the throw, the tested full chain is recovered. Catch and finally belong
to the outer caller without unwound children marked running or awaiting.
The expected catch/finally occur once and the operation completes normally.

These original recordings did **not** include detailed V2 completion/unwind
events. They check callers around CPU work; the
[unchanged-app detailed-event repeat](DetailedV2Events.md) checks optional
record processing separately.

## V1 classic methods

Catch/finally likewise belong to the correct outer caller, without stale
children. The pre-throw UI record omits awaiting scenario parents already in
the original recording. Classic normal-completion/exception records were
included in this baseline and were checked at their actual event times.

The app's handler work and accounting remain intact. Exact event-time checks
are not proof that every brief application state while returning from an
exception was physically sampled. No confirmed TraceEvent defect was found;
this is one expected-exception path, not a failure matrix.

[Scenario/source](../../../Scenarios/Wpf/Cases/ExceptionHandling/README.md) ·
[evidence index](../EvidenceIndex.json) ·
[shared scope](../README.md#shared-api-path-and-limits).
