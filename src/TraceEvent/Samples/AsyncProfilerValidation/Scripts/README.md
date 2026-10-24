# Validation tooling

Select a WPF case, inspect prerequisites, prepare explicit commands, or export
an existing trace. The scripts do not automate collection or decide whether a
scenario's reconstructed callers are correct.

| Operation | Implemented behavior | Not established |
|---|---|---|
| `Test-Environment.ps1` | Check explicit file paths; query selected SDK and installed producer/helper frameworks | Instrumentation, collector compatibility, actual loaded framework or codegen |
| `Invoke-Validation.ps1 -ListCases` | List catalogued case/family/modes | Execution or coverage |
| `-Action Plan` | Write unexecuted restore/build/app-smoke recipes and not-run metadata | Builds, launches, capture or validation passes |
| `-Action Export` | Call the existing signed helper on an existing original nettrace; retain logs/exports | Case/family identity or semantic caller correctness |
| `Build`, `Run`, `Capture` | Fail explicitly as unsupported | No hidden fallback or automatic experiment |

## Configure separate roles

Use explicit existing paths; no PATH/default-runtime assumptions:

```powershell
$scripts = 'src\TraceEvent\Samples\AsyncProfilerValidation\Scripts'
$sdk = '<compiler-SDK-dotnet>'
$producer = '<instrumented-producer-dotnet>'
$ordinary = '<ordinary-net8-dotnet>'
$collector = '<matched-collector-executable>'
$helper = '<built-signed-AsyncProfilerValidation.dll>'
$out = '<new-output-directory>'

& "$scripts\Test-Environment.ps1" -SdkDotnet $sdk -ProducerDotnet $producer `
    -HelperDotnet $ordinary -Collector $collector -HelperDll $helper
& "$scripts\Invoke-Validation.ps1" -ListCases
```

The compiler SDK is not the producer runtime. Matching framework version strings
are necessary prerequisites, not proof of experimental event emission.
Missing prerequisites fail clearly; scripts do not install, restore or build them.

## Plan without executing

```powershell
& "$scripts\Invoke-Validation.ps1" -Action Plan -Case ContextResumption -Family V1 `
    -Mode capture-ui -OutputDirectory $out -SdkDotnet $sdk -ProducerDotnet $producer `
    -HelperDotnet $ordinary -Collector $collector -HelperDll $helper
```

`recipes.json` supplies executable, argument array and working directory for
each manual command. Its launch is deliberately an **unrecorded app smoke**,
using the existing `--smoke-start` protocol; it is not a capture recipe.
Mode is forwarded to that smoke command. The collector path is recorded only.
Do not use a smoke's successful close as an API-validation claim.

## Export an already collected trace

```powershell
& "$scripts\Invoke-Validation.ps1" -Action Export -Case ContextResumption -Family V2 `
    -Mode configure-false -InputTrace '<original.nettrace>' -OutputDirectory '<another-new-output>' `
    -HelperDotnet $ordinary -HelperDll $helper
```

The root contains `run.json`, `checks.json`, `SUMMARY.md`, `logs/` and the helper's
own `exports/` (including its fresh ETLX and four views). The wrapper does not
duplicate the helper's checks. Successful export means only that those existing
checks succeeded. Case/family binding is user-declared; semantic auditing remains
unsupported, not silently marked passed. Failures retain partial output.
Run metadata records current source-input hashes separately from archived
hashes; matching source inputs are not proof of a matching new binary or run.

Keep output outside the checkout or below the sample's ignored `artifacts/`.
Existing output roots are rejected. Use `-Help` or `Get-Help <script> -Full`.
For analysis see [helper export/probe usage](../README.md#export-and-probe-a-trace);
for interpretation see the [WPF findings](../Review/Wpf/README.md).

## Verification status

Observed findings are tied to the tested source, binary, framework and TraceEvent
identities in [EvidenceIndex.json](../Review/Wpf/EvidenceIndex.json).
The suite organization preserves the tested source/project bytes. Its current
project paths and entry scripts have been checked statically, not validated by
new builds, executions, replays or captures. Matching source inputs do not
certify a new binary or run. This status is distinct from the recorded WPF results.
