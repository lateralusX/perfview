# Diagnostic: getter-driven configured awaits

Finding ID: `wpf.dynamic-await-policy`.

The V2 diagnostic pair supplies true/false through a property getter in
`ConfigureAwait(state.CaptureUi)`. Both paths genuinely use runtime-supported
methods, but that does not prove a direct recorded caller chain.

The actual compiler/JIT did not recognize the child-call pattern with the
intervening getter. Task boundaries split the chain: original CPU records
contain the deepest method, not its two awaiting scenario parents. True runs
on UI and false off UI in this suspended-gate test; the true path is not a
bare-await experiment.

Johan's public output preserves the recorded current work and accounting
without inventing those parents. The separate
[explicit bare/literal-false pair](ContextResumption.md) recovers the complete
tested V2 chains. This difference is a fixture/compiler-shape limitation, not
evidence that WPF crossing or TraceEvent dropped encoded callers.

This case exposes a recording limitation; it does not establish full V2 ancestry.

[Scenario/source](../../../Scenarios/Wpf/Cases/DynamicAwaitPolicy/README.md) ·
[evidence index](../EvidenceIndex.json) ·
[shared scope](../README.md#shared-api-path-and-limits).
