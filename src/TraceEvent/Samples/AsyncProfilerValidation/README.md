# WPF async-profiler validation

This suite checks whether TraceEvent recovers useful **recorded async callers**
around real WPF CPU work. After an await, callers can disappear from the physical
stack; a useful profile must retain the work without inventing execution or
attaching the wrong operation.

V1/classic and V2/runtime-supported are method implementation families, not
TraceEvent API versions. Read the findings before assuming every source-level
await relationship can be recovered from a trace.

## Start here

| Task | Entry point |
|---|---|
| Read observed V1, V2, mixed and graph results | [WPF findings](Review/Wpf/README.md) |
| Choose, build and run an included case | [WPF scenarios](Scenarios/Wpf/README.md) |
| Add a focused case | [Scenario authoring guide](Scenarios/README.md) |
| Check prerequisites or prepare a run | [Script capabilities and verification status](Scripts/README.md) |
| Understand stack reconstruction, APIs and ownership | [Architecture](ARCHITECTURE.md#from-a-trace-file-to-a-stitched-stack) |

The suite separates scenario intent/source, observed findings and execution
output. Compiler, runtime and helper are different tool roles; there is no
one-command capture-and-semantic-validation runner.

## Prerequisites

The ordinary exporter/probe needs a .NET SDK capable of building `net8.0` and
the .NET 8 runtime. It does **not** require the experimental WPF SDK/frameworks.

WPF producers require Windows, the compiler SDK pinned under `Scenarios\Wpf`,
matching instrumented Core/WindowsDesktop frameworks and a compatible collector.
Use explicit tool paths and the [WPF prerequisites](Scenarios/Wpf/README.md#prerequisites-and-recipes).
An arbitrary installed runtime, a matching version string, or copied runtime
DLLs is not a substitute for that compatible setup.

The scripts provide prerequisite inventory, unexecuted build/smoke recipes and
existing-trace export. Automated capture/control and case-specific semantic
checking are unsupported. See [verification status](Scripts/README.md#verification-status)
for the boundary between observed results and current tooling.

## Export and probe a trace

Commands below start at the repository root. Configure an ordinary SDK/host
with the .NET 8 runtime; do not use the producer host implicitly.

```powershell
$dotnet = '<ordinary-SDK-and-net8-runtime-dotnet>'
$project = 'src\TraceEvent\Samples\AsyncProfilerValidation\AsyncProfilerValidation.csproj'
& $dotnet restore $project --configfile Nuget.config
& $dotnet build $project -c Debug --no-restore
$helper = 'src\TraceEvent\Samples\AsyncProfilerValidation\bin\Debug\net8.0\AsyncProfilerValidation.dll'

& $dotnet $helper export '<capture.nettrace>' '<new-export-directory>'
& $dotnet $helper probe '<export-directory>\capture.etlx' '<pid>' '<os-thread-id>' '<start-ms>' '<end-ms>' '<new-probe-directory>'
& $dotnet $helper probe-qpc '<export-directory>\capture.etlx' '<pid>' '<os-thread-id>' '<qpc>' '<another-new-probe-directory>'
```

Replace placeholders with actual recorded identities. Millisecond windows are
inclusive and relative to the trace; `probe-qpc` selects an actual recorded QPC,
not a nearest-time substitute. Use the ETLX from the same export and a fresh
output directory each time. The signed helper uses this checkout's TraceEvent,
not a separately installed NuGet version.

Export retains the async events in a fresh ETLX and emits JSONL, Speedscope and
summary files. Each view has a fresh public stack computer/source:

| View | Meaning |
|---|---|
| `raw` | Same stack-computer output with async reconstruction disabled |
| `structural` | Recorded async reconstruction without normalization/cleanup |
| `default` | Conservative normalization of supported patterns |
| `machinery` | Additional opt-in cleanup/overhead attribution for verified infrastructure |

The raw view still uses the host's task/activity grouping and metric projection;
use the probe for exact physical event stacks.

The helper rejects empty output, inactive async reconstruction in enabled views,
changed sample counts/timestamps/metrics and unbalanced profiles. Passing those
checks does not establish correct callers for a particular workload.
Probe output relates physical samples, indexed contexts, emitted frames and
transforms. Public controls and internal diagnostic boundaries are explained in
the [architecture/API guide](ARCHITECTURE.md).

## Interpret evidence, not just shorter stacks

- Keep capture, source/binary, compiler/runtime, collector/provider and
  TraceEvent identities with a run. [Findings and provenance](Review/Wpf/README.md)
  distinguish known results from unproven relationships.
- Check physical CPU work and original records before blaming the consumer for
  a missing caller. Source intent alone is not recorded evidence.
- Distinguish running methods, dispatch targets, physically returning frames
  and awaiting callers. Optional overhead cleanup does not prove a target body
  executed.
- Do not guess ownership from shared threads, nearby times or display names,
  flatten siblings into a stack, or substitute creation for waiting.
- Preserve unresolved work. V1 display states can be inferred without replacing
  captured states; V2 does not use V1 compiler-state semantics.

See the [worked stack examples](ARCHITECTURE.md#worked-examples-from-captured-v2-and-v1-samples)
and [shared result limits](Review/Wpf/README.md#shared-api-path-and-limits) for
detail.

## Contributor checks

For code changes, use Debug so assertions remain active, select the smallest
relevant regressions first, then the async suite when appropriate:

```powershell
dotnet test 'src\TraceEvent\TraceEvent.Tests\TraceEvent.Tests.csproj' -c Debug --filter 'FullyQualifiedName~Async'
```

Verify that matching tests actually ran. Windows targets include `net462` and
`net8.0`; `net462` needs its .NET Framework support. Use `-f net8.0` where that
is the supported scope. Reuse the repository's synthetic EventPipe fixtures
rather than adding another reconstruction implementation. Protocol, lifetime
and transform contracts remain in [ARCHITECTURE.md](ARCHITECTURE.md).
