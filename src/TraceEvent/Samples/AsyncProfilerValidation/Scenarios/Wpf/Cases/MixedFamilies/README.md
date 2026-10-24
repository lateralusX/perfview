# Classic and runtime-supported methods together

The two UI-started chains are V1 -> V2 -> V1 and V2 -> V1 -> V2.
Every arrow is an awaited child call; the deepest method suspends on a gate
before doing recognizable work.

**Why:** both method families can coexist. Determine which callers are actually
recorded at the handoffs, and keep the running work without manufacturing
missing relationships. V1/V2 are method implementations, not API versions.

[Mixed project](Mixed/src/WpfAsyncScenarios.csproj): `wpf.mixed-families.mixed`,
modes `mixed-121` and `mixed-212`. Its per-method classic opt-outs and V2
compiler policy are preserved, not replaced by one family.

Source intent is not proof of an encoded parent. Compare original records,
indexed identities and public output before calling a missing edge an API bug.

[Build/launch planning](../../README.md#prerequisites-and-recipes) ·
[observed results](../../../../Review/Wpf/Findings/MixedFamilies.md).
