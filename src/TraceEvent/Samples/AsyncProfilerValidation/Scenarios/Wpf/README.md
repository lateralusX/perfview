# WPF cases

Choose a workload that isolates the caller-context question: resumption,
return progress, overlapping operations, cleanup, nested dispatcher execution
or a join. Read the [observed results](../../Review/Wpf/README.md) separately
from each case's intent and expected observations.

## Case layout and selection

Each case/family has its own project. Await expressions and compiler policy
are part of the experiment: combining bodies into a shared host or casually
extracting helpers can change the recorded chain.

| Case | Families | Instructions |
|---|---|---|
| ContextResumption | V1, V2 | [UI versus worker](Cases/ContextResumption/README.md) |
| DeepChains | V1, V2 | [Depth and returning work](Cases/DeepChains/README.md) |
| OverlappingOperations | V1, V2 | [Independent A/B](Cases/OverlappingOperations/README.md) |
| ExceptionHandling | V1, V2 | [Typed catch/finally](Cases/ExceptionHandling/README.md) |
| Cancellation | V1, V2 | [Cancellation then fresh work](Cases/Cancellation/README.md) |
| DispatcherReentrancy | V1, V2 | [Nested dispatcher loop](Cases/DispatcherReentrancy/README.md) |
| WrapperRollover | V2 | [Actual helper-slot reuse](Cases/WrapperRollover/README.md) |
| WhenAll | V1, V2 | [Branch isolation and join](Cases/WhenAll/README.md) |
| MixedFamilies | Mixed | [Cross-family handoffs](Cases/MixedFamilies/README.md) |
| DynamicAwaitPolicy | V2 diagnostic | [Getter-driven recording limitation](Cases/DynamicAwaitPolicy/README.md) |

[Cases.json](Cases.json) maps stable IDs to projects and covered modes.
[SourceOrigins.json](SourceOrigins.json) supplies source hashes and build
provenance; it is not another result summary. See
[verification status](../../Scripts/README.md#verification-status) before
equating a newly built binary with a recorded result.

## Prerequisites and recipes

Use Windows, PowerShell 7, the SDK selected by this directory's `global.json`,
and matching instrumented Core/WindowsDesktop frameworks. The project pin is
SDK `11.0.100-rc.1.26420.103` with framework `11.0.0-rc.1.26420.103` and
roll-forward disabled. The compiler SDK, producer host, ordinary net8 helper
host and compatible collector are separate roles. Matching version strings
alone do not prove instrumentation; do not mix runtime/framework DLLs.

[Test-Environment and Invoke-Validation](../../Scripts/README.md) accept explicit
tool paths. `Plan` writes restore/build/smoke-launch recipes without executing
them. Run compiler commands from the chosen project directory so its SDK pin
applies, restore with repository `Nuget.config`, and keep output separate.
These projects do not make ordinary helper/library/test builds require the
experimental SDK.

## Build and open a case manually

From the repository root, configure paths and choose the project:

```powershell
$repo = (Get-Location).Path
$project = Join-Path $repo 'src\TraceEvent\Samples\AsyncProfilerValidation\Scenarios\Wpf\Cases\ContextResumption\V2\src\WpfAsyncScenarios.csproj'
$sdkDotnet = '<compiler-SDK-dotnet>'
$producerDotnet = '<matching-instrumented-dotnet>'
$buildOutput = '<new-absolute-build-output-directory>'

Push-Location (Split-Path $project)
try {
    & $sdkDotnet restore $project --configfile (Join-Path $repo 'Nuget.config') --artifacts-path $buildOutput
    if ($LASTEXITCODE -ne 0) { throw 'Scenario restore failed.' }
    & $sdkDotnet build $project --no-restore -c Release --artifacts-path $buildOutput
    if ($LASTEXITCODE -ne 0) { throw 'Scenario build failed.' }
    & $producerDotnet (Join-Path $buildOutput 'bin\WpfAsyncScenarios\release\WpfAsyncScenarios.dll')
} finally {
    Pop-Location
}
```

Select the corresponding control in the visible window. CPU windows can
deliberately freeze UI briefly; gate/idle waits must not block the dispatcher.
Opening the app is not a collection or API-validation pass.

For a recording, the existing startup arguments
`--auto-mode <mode> --start-file <new-marker> --auto-close` keep work gated until
that marker is created. Use a fresh run directory and do not release the marker
until the collector's actual session, provider settings and output stream are
confirmed. Standard cases use `dotnet-sampled-thread-time` with explicit
`System.Runtime.CompilerServices.AsyncProfilerEventSource:0x2F04C:5`; specialized
recording differences belong to their case evidence, not an all-events preset.
Preserve complete flush/finalization and owned-process
closure evidence. Automated capture/control and deep semantic audits are not
implemented by the scripts. Use [helper export/probe](../../README.md#export-and-probe-a-trace)
on an actual recording, then inspect the case's expected and observed behavior.
