#requires -Version 7.0
<#
.SYNOPSIS
Selects an exact WPF case, writes manual recipes, or exports an existing trace.
.DESCRIPTION
Plan never executes the recipes. Export invokes only the existing signed helper.
Integrated build, GUI launch, capture/control and semantic audits are unsupported.
#>
[CmdletBinding()]
param(
    [ValidateSet('Plan','Export','Build','Run','Capture')][string]$Action = 'Plan',
    [string]$Case,
    [ValidateSet('V1','V2','Mixed')][string]$Family,
    [string]$Mode,
    [string]$OutputDirectory,
    [string]$SdkDotnet,
    [string]$ProducerDotnet,
    [string]$HelperDotnet,
    [string]$Collector,
    [string]$HelperDll,
    [string]$InputTrace,
    [ValidateRange(1,3600)][int]$TimeoutSeconds = 180,
    [switch]$ListCases,
    [switch]$Help
)
$ErrorActionPreference = 'Stop'
$sample = Split-Path $PSScriptRoot
$wpf = Join-Path $sample 'Scenarios\Wpf'
$catalog = Get-Content -LiteralPath (Join-Path $wpf 'Cases.json') -Raw | ConvertFrom-Json
if ($Help) {
    Write-Output @'
Invoke-Validation.ps1 -ListCases
Invoke-Validation.ps1 -Action Plan -Case <name> -Family <V1|V2|Mixed> [-Mode <mode>]
  -OutputDirectory <new-root> -SdkDotnet <compiler-dotnet> -ProducerDotnet <instrumented-dotnet>
  -HelperDotnet <ordinary-dotnet> -Collector <collector-executable> -HelperDll <built-helper.dll>
Invoke-Validation.ps1 -Action Export -Case <name> -Family <family> [-Mode <mode>]
  -OutputDirectory <new-root> -InputTrace <existing.nettrace> -HelperDotnet <ordinary-dotnet>
  -HelperDll <built-helper.dll> [-TimeoutSeconds 180]
Plan writes unexecuted restore/build/smoke-launch recipes. Export calls the existing helper only.
Build, Run and Capture are unsupported; no integrated semantic auditor is implemented.
Output must be outside the checkout or below this sample's ignored artifacts directory.
'@
    return
}
if ($ListCases) { $catalog.cases | Select-Object case,family,modes,id; return }
if ($Action -notin @('Plan','Export')) {
    throw "$Action is unsupported in this source-preserving migration. Use Plan for explicit recipes; no integrated capture/control is implemented."
}
if ($Action -eq 'Export' -and ($SdkDotnet -or $ProducerDotnet -or $Collector)) {
    throw 'SdkDotnet, ProducerDotnet and Collector configure Plan only; Export uses the existing helper and input trace.'
}
if ($Action -eq 'Plan' -and $InputTrace) { throw 'InputTrace belongs to Export, not Plan.' }
$selected = @($catalog.cases | Where-Object { $_.case -eq $Case -and $_.family -eq $Family })
if ($selected.Count -ne 1) { throw 'Select an exact -Case and -Family from -ListCases.' }
$selected = $selected[0]
$Case = $selected.case
$Family = $selected.family
if ([string]::IsNullOrWhiteSpace($Mode)) {
    if ($selected.modes.Count -ne 1) { throw "Select -Mode explicitly: $($selected.modes -join ', ')." }
    $Mode = $selected.modes[0]
}
if ($Mode -notin $selected.modes) { throw "Unsupported mode for this source variant: $Mode." }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { throw 'Supply a unique -OutputDirectory.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$repo = [IO.Path]::GetFullPath((Join-Path $sample '..\..\..\..'))
$ignored = [IO.Path]::GetFullPath((Join-Path $sample 'artifacts'))
if ($output.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
    -not $output.StartsWith($ignored + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Keep output outside the checkout or under this sample''s ignored artifacts directory.'
}
if ($output -eq $repo -or (Test-Path -LiteralPath $output)) { throw 'Use a new output directory; do not replace existing evidence.' }
$project = [IO.Path]::GetFullPath((Join-Path $wpf $selected.project))
if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "Missing migrated project: $project" }
$origins = Get-Content -LiteralPath (Join-Path $wpf 'SourceOrigins.json') -Raw | ConvertFrom-Json
$origin = @($origins.projects | Where-Object id -eq $selected.sourceOriginId)
if ($origin.Count -ne 1) { throw 'Missing or ambiguous source-origin metadata.' }
$sourceInputs = @($origin[0].copiedFiles | ForEach-Object {
    $path = Join-Path $wpf $_.path
    [ordered]@{ path=$_.path; currentSha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash; archivedSha256=$_.sha256 }
})
if ($sourceInputs.Count -eq 0) { throw 'Source-origin metadata must list the selected project inputs.' }
$sourceMatches = @($sourceInputs | Where-Object { $_.currentSha256 -ne $_.archivedSha256 }).Count -eq 0
$tools = [ordered]@{ HelperDotnet=$HelperDotnet; HelperDll=$HelperDll }
if ($Action -eq 'Plan') {
    $tools.SdkDotnet=$SdkDotnet; $tools.ProducerDotnet=$ProducerDotnet; $tools.Collector=$Collector
}
foreach ($tool in $tools.GetEnumerator()) {
    if ([string]::IsNullOrWhiteSpace($tool.Value) -or -not (Test-Path -LiteralPath $tool.Value -PathType Leaf)) {
        throw "Supply an existing explicit -$($tool.Key) path."
    }
}
if ($Action -eq 'Export' -and ([string]::IsNullOrWhiteSpace($InputTrace) -or
    -not (Test-Path -LiteralPath $InputTrace -PathType Leaf) -or [IO.Path]::GetExtension($InputTrace) -ne '.nettrace')) {
    throw 'Export requires an existing original .nettrace; it does not capture or accept an ETLX substitute.'
}
New-Item -ItemType Directory -Path $output,(Join-Path $output 'logs') | Out-Null
$run = [ordered]@{
    schemaVersion=1; caseId=$selected.id; case=$Case; family=$Family; mode=$Mode
    action=$Action; startedUtc=[DateTime]::UtcNow.ToString('o'); status='not-run'
    sourceOriginId=$selected.sourceOriginId; project=$project
    sourceInputs=$sourceInputs; sourceInputsMatchArchive=$sourceMatches
    sdkSelectionSha256=(Get-FileHash -LiteralPath (Join-Path $wpf 'global.json') -Algorithm SHA256).Hash
    caseBinding='user-declared; not verified against an input trace'
    tools=@($tools.GetEnumerator() | ForEach-Object {
        [ordered]@{ role=$_.Key; path=[IO.Path]::GetFullPath($_.Value); sha256=(Get-FileHash -LiteralPath $_.Value -Algorithm SHA256).Hash }
    })
}
$checks = [ordered]@{
    environment='not-run'; build='not-run'; scenario='not-run'; codegen='not-run'
    capture='unsupported'; semanticAudit='unsupported'; helperExport='not-run'
}
function Write-RunState {
    $run | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'run.json') -Encoding utf8
    $checks | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'checks.json') -Encoding utf8
    @(
        "# $($selected.id)"
        ''
        "Action: $Action. Status: $($run.status)."
        ''
        'Case/family binding is user-declared. No scenario execution or semantic pass is implied.'
        'See run.json and checks.json for implemented versus unperformed checks.'
    ) | Set-Content -LiteralPath (Join-Path $output 'SUMMARY.md') -Encoding utf8
}
Write-RunState
try {
    if ($Action -eq 'Plan') {
        $build = Join-Path $output 'build'
        $app = Join-Path $build 'bin\WpfAsyncScenarios\release\WpfAsyncScenarios.dll'
        $recipes = @(
            [ordered]@{ stage='restore'; executable=[IO.Path]::GetFullPath($SdkDotnet); workingDirectory=(Split-Path $project);
                arguments=@('restore',$project,'--configfile',(Join-Path $repo 'Nuget.config'),'--artifacts-path',$build) }
            [ordered]@{ stage='build'; executable=[IO.Path]::GetFullPath($SdkDotnet); workingDirectory=(Split-Path $project);
                arguments=@('build',$project,'--no-restore','-c','Release','--artifacts-path',$build) }
            [ordered]@{ stage='smoke-launch-only'; executable=[IO.Path]::GetFullPath($ProducerDotnet); workingDirectory=(Split-Path $project);
                arguments=@($app,'--auto-mode',$Mode,'--smoke-start','--auto-close');
                note='Unrecorded app smoke only; not a capture, codegen certification or API validation.' }
        )
        $recipes | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'recipes.json') -Encoding utf8
        $run.status='planned-not-run'
        $run.collectorUse='configured path only; no attach/control/capture recipe is implemented'
    }
    else {
        $run.inputTrace=[IO.Path]::GetFullPath($InputTrace)
        $run.inputSha256=(Get-FileHash -LiteralPath $InputTrace -Algorithm SHA256).Hash
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName=[IO.Path]::GetFullPath($HelperDotnet)
        $start.WorkingDirectory=$sample
        $start.UseShellExecute=$false
        $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        foreach ($argument in @([IO.Path]::GetFullPath($HelperDll),'export',$run.inputTrace,(Join-Path $output 'exports'))) {
            $start.ArgumentList.Add($argument)
        }
        $process=[Diagnostics.Process]::new()
        $process.StartInfo=$start
        $started=$false
        $stdout=$null; $stderr=$null
        $checks.helperExport='running'
        try {
            $started=$process.Start()
            if (-not $started) { throw 'Could not start the existing helper.' }
            $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
                Stop-Process -Id $process.Id -ErrorAction Stop
                if (-not $process.WaitForExit(5000)) { throw 'Owned helper did not terminate after timeout.' }
                throw 'Helper export timed out; partial output is retained.'
            }
            if ($process.ExitCode -ne 0) { throw "Existing helper failed with exit code $($process.ExitCode); inspect logs." }
        }
        finally {
            try {
                if ($started) {
                    if (-not $process.HasExited) {
                        Stop-Process -Id $process.Id -ErrorAction Stop
                        if (-not $process.WaitForExit(5000)) { throw 'Owned helper failed bounded cleanup.' }
                    }
                    if ($null -ne $stdout) {
                        [IO.File]::WriteAllText((Join-Path $output 'logs\helper.stdout.log'),$stdout.GetAwaiter().GetResult())
                        [IO.File]::WriteAllText((Join-Path $output 'logs\helper.stderr.log'),$stderr.GetAwaiter().GetResult())
                    }
                }
            }
            finally { $process.Dispose() }
        }
        $checks.helperExport='passed-existing-helper-checks-only'
        $run.status='exported-semantic-review-required'
    }
}
catch {
    $run.status='failed'
    if ($Action -eq 'Export') { $checks.helperExport='failed' }
    $run.error=$_.Exception.Message
    throw
}
finally {
    $run.finishedUtc=[DateTime]::UtcNow.ToString('o')
    Write-RunState
}
Write-Output "Saved $Action output at $output; status: $($run.status)."
