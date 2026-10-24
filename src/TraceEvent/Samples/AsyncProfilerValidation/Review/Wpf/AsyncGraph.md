# Can branch CPU work be connected to its waiting parent?

Finding ID: `wpf.async-graph`.

**Question:** can the trace alone connect sampled branch work to its Task,
membership in `WhenAll`, and the parent awaiting that join?
That would explain **which higher-level operation is waiting for the work**,
not imply that the waiting parent consumes its CPU.

The unchanged [V2 WhenAll app](../../Scenarios/Wpf/Cases/WhenAll/README.md)
starts two gated branches. A finishes first; the parent continues only after B.
This is a branching dependency graph, not one physical call stack.

## What the focused recording proved

Additional source-grounded Task/async-creation events were collected without
rewriting the app or enabling all events. Original identifiers establish:

| Connection | Result |
|---|---|
| Sampled branch work to its Task identity | Verified |
| A and B membership in the same WhenAll join | Verified |
| The V2 parent waiting on that join | Missing from the recorded events in this case |

The app's source is the intended-graph oracle, not recorded evidence.
Membership proof uses explicit typed completion-notification scopes and join
records, not nearby timestamps, names or shared threads.
There is a recorded wait from the classic UI handler to the parent; that is
not the parent's wait on `WhenAll`. Creation cannot stand in for a future await.

## Meaning for the APIs

Existing events prove part of the graph, but not the complete connection back
to the awaiting V2 parent. Johan's current public stack APIs recover recorded
execution chains; they do not expose a complete typed dependency graph.
Internal creation ancestry was diagnostic only and its rendering stayed off.
No confirmed TraceEvent loss or misattribution was demonstrated.

The design question is whether the runtime should record a reliable explicit
V2 waiter-to-awaited-Task relationship, and how a separately designed graph
feature would distinguish waiting, membership, creation and physical calling.
No graph engine, runtime patch or speculative public API was implemented.

[Execution-stack finding](Findings/WhenAll.md) ·
[portable evidence references](EvidenceIndex.json) ·
[shared setup/API limits](README.md#shared-api-path-and-limits).
This establishes the relationships above in one WPF/.NET 11 preview configuration,
not a complete graph feature or a result for every async program.
