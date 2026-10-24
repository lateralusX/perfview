# Diagnostic: getter-driven await policy

This V2 diagnostic app uses `ConfigureAwait(state.CaptureUi)` at its child awaits.
Three methods await one another; the deepest suspends, then does CPU work on UI
for true or off UI for false in the tested executions.

**Why preserve it:** a method can genuinely be V2 while its compiled call shape
still splits the recorded caller chain. A property getter is not equivalent to
the recognized immediate bare/literal-configured await pattern. The true button
is not a bare-await test.

[V2 diagnostic project](V2/src/WpfAsyncScenarios.csproj):
`wpf.dynamic-await-policy.v2`, modes `capture-ui` and `configure-false`.
It is deliberately not rewritten to make the old result look successful.

[Build/launch planning](../../README.md#prerequisites-and-recipes) ·
[observed recording limitation](../../../../Review/Wpf/Findings/DynamicAwaitPolicy.md) ·
[separate explicit-policy case](../ContextResumption/README.md).
