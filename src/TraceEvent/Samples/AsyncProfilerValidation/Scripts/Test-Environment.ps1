#requires -Version 7.0
<#
.SYNOPSIS
Checks explicit tool paths and installed SDK/framework prerequisites, not instrumentation.
.DESCRIPTION
Only queries tool inventories. It does not build, restore, launch a scenario,
attach a collector, or prove runtime instrumentation/collector compatibility.
#>
[CmdletBinding()]
param(
    [string]$SdkDotnet,
    [string]$ProducerDotnet,
    [string]$HelperDotnet,
    [string]$Collector,
    [string]$HelperDll,
    [switch]$Help
)
$ErrorActionPreference = 'Stop'
if ($Help) {
    Write-Output @'
Test-Environment.ps1 -SdkDotnet <compiler-dotnet> -ProducerDotnet <instrumented-dotnet>
  -HelperDotnet <ordinary-dotnet> -Collector <collector-executable> -HelperDll <built-helper.dll>
Requires Windows and PowerShell 7. Queries SDK/framework inventories only.
Matching versions do NOT prove async instrumentation, codegen, or capture compatibility.
'@
    return
}
if (-not $IsWindows) { throw 'The WPF scenarios require Windows.' }
$tools = [ordered]@{ SdkDotnet=$SdkDotnet; ProducerDotnet=$ProducerDotnet; HelperDotnet=$HelperDotnet; Collector=$Collector; HelperDll=$HelperDll }
foreach ($entry in $tools.GetEnumerator()) {
    if ([string]::IsNullOrWhiteSpace($entry.Value) -or -not (Test-Path -LiteralPath $entry.Value -PathType Leaf)) {
        throw "Supply an existing explicit -$($entry.Key) path."
    }
}
$wpf = Join-Path (Split-Path $PSScriptRoot) 'Scenarios\Wpf'
$pin = Get-Content -LiteralPath (Join-Path $wpf 'global.json') -Raw | ConvertFrom-Json
function Read-ToolInventory([string]$Executable, [string]$Argument) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = [IO.Path]::GetFullPath($Executable)
    $start.WorkingDirectory = $wpf
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add($Argument)
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw "Could not query $Executable." }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) {
            Stop-Process -Id $process.Id -ErrorAction Stop
            throw "Tool inventory timed out: $Executable $Argument"
        }
        $text = $stdout.GetAwaiter().GetResult()
        $errorText = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Tool inventory failed: $Executable $Argument`n$errorText" }
        return $text.Trim()
    }
    finally { $process.Dispose() }
}
$sdkVersion = Read-ToolInventory $SdkDotnet '--version'
if ($sdkVersion -ne $pin.sdk.version) { throw "Expected SDK $($pin.sdk.version); selected $sdkVersion." }
$producerRuntimes = Read-ToolInventory $ProducerDotnet '--list-runtimes'
foreach ($framework in @('Microsoft.NETCore.App','Microsoft.WindowsDesktop.App')) {
    if ($producerRuntimes -notmatch "(?m)^$([regex]::Escape($framework)) 11\.0\.0-rc\.1\.26420\.103\s") {
        throw "Producer host lacks the pinned $framework 11.0.0-rc.1.26420.103 framework."
    }
}
$helperRuntimes = Read-ToolInventory $HelperDotnet '--list-runtimes'
if ($helperRuntimes -notmatch '(?m)^Microsoft\.NETCore\.App 8\.') {
    throw 'The ordinary helper host must have the net8.0 runtime available.'
}
[ordered]@{
    prerequisiteStatus='available'
    sdkVersion=$sdkVersion
    producerFrameworkInventory=$producerRuntimes
    helperFrameworkInventory=$helperRuntimes
    tools=@($tools.GetEnumerator() | ForEach-Object {
        [ordered]@{ role=$_.Key; path=[IO.Path]::GetFullPath($_.Value); sha256=(Get-FileHash -LiteralPath $_.Value -Algorithm SHA256).Hash }
    })
    runtimeInstrumentation='not-verified'
    collectorCompatibility='not-verified'
    scenarioCodegen='not-run'
    capture='not-run'
    note='Installed versions are prerequisites, not proof of the experimental emitter or a passing scenario.'
} | ConvertTo-Json -Depth 6
