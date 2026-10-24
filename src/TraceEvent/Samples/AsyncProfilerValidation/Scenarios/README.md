# Adding validation scenarios

Scenarios make async stack questions reproducible: **what does the app do,
why could a profile be misleading, and what observations would distinguish
correct output from missing recording or a consumer error?**

Start with [WPF cases](Wpf/README.md). The small
[ContextResumption case](Wpf/Cases/ContextResumption/README.md) is the reference
for a three-level, gated workload. Its V1 and V2 projects remain distinct.
Use the [observed findings](../Review/Wpf/README.md) to distinguish supported
caller recovery from relationships missing in the recording.

## Adding a case

1. Describe the question and expected observations before adding machinery.
   Name the running CPU work, expected awaiting callers, legitimate physical
   callers, and evidence that would be insufficient.
2. Give the case a stable ID and explicit method family. V1/classic and
   V2/runtime-supported are implementation families, not TraceEvent API versions.
   Register the exact project, modes, instruction page and finding ID in
   [Cases.json](Wpf/Cases.json); record source origin/changes separately.
3. Reuse a small project's structure. Keep async bodies and compiler policy
   understandable; do not extract them into a shared host merely for reuse.
   Changing an await expression or storing a child Task can change recording.
4. Use recognizable CPU methods, named phases and initially closed gates.
   Prove actual suspension/root incompleteness before release, log process,
   OS thread/context and phase times, and separate work from status/idle waits.
   Do not substitute Task.Run, UI-blocking waits or guessed timing for the
   behavior being tested.
5. Establish the family from the actual binary and records, not labels.
   V1 needs classic state-machine/MoveNext evidence; V2 needs runtime-async
   implementation/await-shape evidence. Preserve compiler, framework and
   source/binary identities with any later run.
6. State the result honestly: not run, insufficient evidence, recording
   limitation, or demonstrated consumer behavior/error. Source intent is an
   oracle, not proof that the trace contains every relationship.

## Output and review contract

Use a fresh output root containing `run.json`, `SUMMARY.md`, `checks.json`,
`logs/`, and, when exported, the unchanged helper's `exports/`.
Optional captures, ETLX and probes belong with that output, not tracked source.
Record each run's identities separately from previously reported results.

The [thin scripts](../Scripts/README.md) implement planning and existing-trace
export only. Capture/control and scenario semantic checks are explicitly
unsupported there. Share concise findings and selected provenance, not private
traces or diagnostic inventories. See the
[tool verification status](../Scripts/README.md#verification-status).
