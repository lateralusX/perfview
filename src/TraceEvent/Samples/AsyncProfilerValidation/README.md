# Async-profiler validation and callstack investigation

This is the working handoff for validating TraceEvent's async-profiler parser,
persisted index, CPU-stack stitching, and presentation. It is intended for a
developer and their AI assistant, without access to the original conversation.
All repository commands below start at the PerfView repository root.

Start with the [plain-language trace-to-stack walkthrough](ARCHITECTURE.md#from-a-trace-file-to-a-stitched-stack)
for the complete chain from captured data to a stitched async stack. The same
guide then covers the public computer controls, internal implementation APIs, ownership/lifetime
contracts, and the separation between structural stitching and presentation.
The [real V2 and V1 worked examples](ARCHITECTURE.md#worked-examples-from-captured-v2-and-v1-samples)
follow individual samples from physical input through structural, default,
and optional machinery-cleanup views, with capture identities for replay.

**The goal is evidence-based attribution, not the cleanest-looking stack.**
Keep the original capture, distinguish physical execution from reconstructed
async callers, and preserve uncertain work rather than deleting inconvenient frames.

## 1. Choose the validation entry point

| Task | Required inputs | Starting point |
|---|---|---|
| Run deterministic regressions | This branch, .NET SDK, test dependencies | Section 2 |
| Revalidate an existing capture | Original `.nettrace`, this branch, capture metadata and symbols | Sections 3, 5, and 6 |
| Capture new scenario executions | Matching instrumented runtime, external scenario project, collector | Section 4 |
| Explain a particular stack | Capture/export identity, process, thread, sample time, full stack | Section 7 |
| Change a rule | Reproducer, agreed interpretation, positive and negative cases | Section 9 |

The real-trace producer used during development is an **external**
`AsyncProfilerScenarios` project. It and its build dependencies are not supplied
by cloning PerfView. Likewise, earlier session-local exporters and audit scripts
are not repository commands. Obtain the producer/artifact bundle described below
or use the source-controlled validation helper in section 5; do not assume another developer has
the original machine's directories or helper binaries. The source-controlled
`src\TraceEvent\Samples\AsyncProfilerValidation` helper now supplies both the
four-view exporter and a sample-provenance probe.

### Cold-start checkout and prerequisites

The complete walkthrough below is for **Windows x64/CoreCLR**. Install Git,
a .NET SDK capable of building `net8.0`, and the .NET 8 runtime for the helper
and collector. Check `dotnet --list-sdks` and `dotnet --list-runtimes`.
The `net462` regression target also needs the appropriate .NET Framework
runtime/developer targeting support. Native runtime prerequisites are separate
and described in section 4; a managed SDK alone is insufficient.

Start with a short, new workspace path. These commands select the actual
PerfView feature branch, not upstream's released TraceEvent:

```powershell
$ErrorActionPreference = 'Stop'
$WorkspaceRoot = 'C:\async-validation'
$PerfViewRoot = Join-Path $WorkspaceRoot 'perfview'
$PerfViewBranch = 'lateralusX/async-profiler-trace-event-support'
if (Test-Path -LiteralPath $PerfViewRoot) { throw 'Choose a new PerfView checkout directory.' }
New-Item -ItemType Directory -Path $WorkspaceRoot -Force | Out-Null
git clone --branch $PerfViewBranch https://github.com/lateralusX/perfview.git $PerfViewRoot
if ($LASTEXITCODE -ne 0) { throw 'PerfView feature-branch clone failed.' }
Set-Location $PerfViewRoot
$PerfViewRevision = git rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the PerfView checkout.' }
git --no-pager status --short
Write-Host "Record this PerfView revision: $PerfViewRevision"
```

Record the actual PerfView revision and local changes with each validation run
(see section 3). For repeat runs, use that recorded source state rather than
fetching a moving branch tip.

For a first complete run, finish the checkouts/builds in section 4 and the helper
build in section 5 **before launching the waiting scenario**. Then follow
section 4's "First complete V1 capture" in order. Sections 6 and 7 cover opening
the output and asking an evidence-backed stack question.

### Source-of-truth order

1. The captured runtime revision's emission code:
   `AsyncProfiler.cs`, `AsyncProfilerEventSource.cs`, V1 dispatchers/boxes, and V2
   continuation dispatch. These live in `dotnet/runtime`, principally under
   `src\libraries\System.Private.CoreLib\src\System\Runtime\CompilerServices`.
2. This branch's parser, index, structural stitcher, presentation rules, and tests.
3. Exact events and physical samples in the capture, including unresolved frames.
4. Scenario source and generated code from the binary that actually ran.

Old discussions, frame labels, and historical expectations are explanations, not
substitutes for those sources. In particular, **both V1 and V2 currently emit
native code IPs**. Do not resurrect the earlier V1 MethodDesc/MethodLoad-ID model.

## 2. Build and run repository regressions

Use Debug so `Debug.Assert` checks remain active:

```powershell
dotnet test 'src\TraceEvent\TraceEvent.Tests\TraceEvent.Tests.csproj' -c Debug --filter 'FullyQualifiedName~Async'
```

On Windows this project targets both `net462` and `net8.0`. Check that tests
actually ran for the intended frameworks; a successful command with zero
matching tests is not validation. On a non-Windows machine, select the supported
framework explicitly with `-f net8.0` and report the narrower coverage.

For a localized change, first select the affected test methods/classes with the
same runner and `--filter`; then run the relevant async suite. Do not start by
running every repository test. If dependencies are missing, restore with the
repository feed configuration:

```powershell
dotnet restore 'src\TraceEvent\TraceEvent.Tests\TraceEvent.Tests.csproj' --configfile Nuget.config
```

The following paths are relative to `src\TraceEvent`:

| Layer | Implementation | Main tests |
|---|---|---|
| Buffer decoding and manifest framing | `Parsers\AsyncProfilerTraceEventParser.cs`, `Parsers\AsyncProfilerTypes.cs` | `TraceEvent.Tests\Parsing\AsyncProfilerParsing.cs` |
| Activation lifecycle and completion timelines | `Computers\AsyncProfilerIndexBuilder.cs` | `AsyncProfilerIndexBuilderTests.cs` |
| Public activation and frame views | `Computers\AsyncProfilerCallStack.cs`, `Computers\AsyncProfilerCallStackFrame.cs`, `Computers\AsyncProfilerCallStackKind.cs` | `AsyncProfilerIndexBuilderTests.cs`, `AsyncProfilerStitchIntegrationTests.cs` |
| Internal frame storage and thread identity | `Computers\AsyncProfilerCallStackFrames.cs`, `Computers\AsyncProfilerThreadKey.cs` | `AsyncProfilerIndexBuilderTests.cs`, `AsyncProfilerTraceLogTests.cs` |
| Persisted/lazy index and symbol binding | `Computers\AsyncProfilerCallStacksIndex.cs`, `TraceLog.cs` | `AsyncProfilerTraceLogTests.cs` |
| Public segment queries and standalone reconstruction | `Computers\AsyncProfilerCallStackComputer.cs`, `Computers\AsyncProfilerCallStackSourceBuilder.cs` | `AsyncProfilerTraceLogTests.cs`, `AsyncProfilerStitchIntegrationTests.cs` |
| Structural matching, boundaries, missing roots | `Computers\AsyncProfilerCallStackStitcher.cs`, `Computers\AsyncProfilerCallStackStitchBoundary.cs` | `AsyncProfilerCallStackStitcherTests.cs`, `AsyncProfilerCallStackStitchBoundaryTests.cs` |
| Runtime ownership and sequence recognition | `Computers\AsyncProfilerRuntimeRuleProfile*.cs`, `Computers\AsyncProfilerRuntimeSequenceMatch.cs` | `AsyncProfilerRuntimeRuleProfileTests.cs`, `AsyncProfilerRuntimeSequenceTests.cs`, `AsyncProfilerRuntimeProfileExtensibilityTests.cs` |
| Presentation and provenance emission | `Computers\AsyncCallStackTransforms.cs`, `Computers\AsyncProfilerCallStackTransformPipeline.cs`, `Computers\AsyncProfilerCallStackTransformPasses.cs`, `Computers\AsyncProfilerCallStackSourceBuilderCore.cs`, `TraceEventStacks.cs` | `AsyncProfilerCallStackTransformPipelineTests.cs`, `AsyncProfilerStitchIntegrationTests.cs` |
| End-to-end sample projection | `Computers\SampleProfilerThreadTimeComputer.cs`, `Computers\ThreadTimeComputer.cs` | `AsyncProfilerStitchIntegrationTests.cs` |
| Speedscope output | `Stacks\SpeedScopeStackSourceWriter.cs`, `Stacks\StackSourceWriterHelper.cs` | `TraceEvent.Tests\Speedscope\SpeedScopeExporterTests.cs` |

Unless qualified otherwise, test filenames in the table live under
`src\TraceEvent\TraceEvent.Tests\Computers`.
The integration suite builds synthetic EventPipe traces, converts and reopens
ETLX, and runs the real computers. Reuse its fixtures rather than building a
parallel test-only stitcher. Its default callstack kind is RuntimeAsync: set
StateMachineAsync explicitly when adding V1 cases.

## 3. Preserve a reproducible artifact bundle

For each run, keep a uniquely named output directory outside the source tree.
Do not overwrite the exports being visually reviewed. Record:

- PerfView commit, local diff, build configuration, and loaded TraceEvent DLL path.
- Producer runtime commit/diff, flavor (CoreCLR/Mono/NativeAOT), architecture,
  actual runtime version, and paths/hashes of the runtime binaries that ran.
- Scenario source revision/diff, complete build command and inherited build
  properties, compiler version/options, assembly/PDB, and actual launch command.
- Collector repository/branch/commit, local dependency overrides, version/build,
  exact provider keywords/level, profile and command, environment variables, UTC
  capture window, PID/process lifetime, and phase log.
- Original trace path and SHA-256; symbol files, perfmaps, and symbol policy.
- ETLX conversion command/options and TraceEvent revision; view flags, grouping,
  filters, sample metric settings, and output filenames.
- Regression command/results, per-view metrics, stack differences, unresolved
  cases, and whether human visual review is still outstanding.

Use `git --no-pager rev-parse HEAD`, `git --no-pager status --short`, and
`Get-FileHash -Algorithm SHA256 '<artifact>'` to collect identities. A directory
name such as `net11.0` is not proof of the runtime version that executed the app.
Verify the actual host/CoreLib and framework selection, especially after
rebuilding a runtime or switching runtime flavors.

### When to reuse, reconvert, or recapture

| Change | Required action |
|---|---|
| Presentation, stitching, or annotation only | Reuse the same capture and compatible ETLX; rebuild/reload the current TraceEvent library and regenerate views |
| Parser, lifecycle, index, persistence, or conversion-time symbol registration | Produce a fresh, separately named ETLX from the original capture |
| Producer event semantics, runtime/compiler behavior, scenario code, keywords, or missing collection data | Rebuild the affected producer and recapture; keep the old capture as a separate regression input |
| New symbol material or symbol policy | Record the change; reconvert if binding occurred during conversion, otherwise reopen and regenerate consistently |

During unreleased development, an unchanged cache version does not prove an old
ETLX contains the current index format or logic. Do not let automatic cache reuse
hide a conversion change. Reopening ETLX should use the persisted index; explicitly
test release/reload when changing its lazy-loading or lifetime behavior.

For raw event forensics, convert with
`new TraceLogOptions { KeepAsyncProfilerEvents = true }`. By default the index is
persisted but raw async batches are omitted from ETLX. Their absence from that
ETLX's Events view is not proof that the capture lacked async events. Inner
sub-events are delivered to a decoder sink, not expanded into individual
TraceEvents.

## 4. Capture the external scenarios

### Fetch the scenario repository

The producer is `AsyncProfilerScenarios` in
<https://github.com/lateralusX/dotnet-tools>. The reference scenario revision is
`3d86eb80a6ce4908947bb0413e442e95b0fcc4a4` (`Add trace capture scenarios.`).
Pin it for the initial comparison rather than assuming future `main` has the
same scenarios or build settings.

The following creates a separate checkout beside PerfView. Use a new destination;
do not reset an existing checkout that might contain someone else's work:

```powershell
$PerfViewRoot = (Get-Location).Path
$WorkspaceRoot = Split-Path $PerfViewRoot -Parent
$ToolsRoot = Join-Path $WorkspaceRoot 'dotnet-tools-async-validation'
$ScenarioRevision = '3d86eb80a6ce4908947bb0413e442e95b0fcc4a4'
if (Test-Path -LiteralPath $ToolsRoot) { throw 'Choose a new scenario checkout directory.' }
git clone https://github.com/lateralusX/dotnet-tools.git $ToolsRoot
if ($LASTEXITCODE -ne 0) { throw 'Scenario clone failed; resolve access/network errors first.' }
git -C $ToolsRoot checkout --detach $ScenarioRevision
if ($LASTEXITCODE -ne 0) { throw 'The requested scenario revision is unavailable.' }
git -C $ToolsRoot --no-pager log -1 --format='%H %s'
```

Clone the whole repository, not just `Program.cs`. The project imports the root
`Directory.Build.props` and references the sibling `AsyncProfilerParser`
project. Build the scenario project, not the whole tools repository: unrelated
analysis tools have additional local TraceEvent dependencies.

### Select the compiler SDK and instrumented runtime

These instructions describe the Windows CoreCLR workflow. **The application
runner used here is a local build of `dotnet/runtime` main**, including the
matching CoreCLR and libraries/CoreLib, launched through that build's testhost
`dotnet.exe`. It is not the system-installed `dotnet`, the compiler SDK's bundled
runtime, or a released runtime selected implicitly by `dotnet run`.

Because `main` moves, record the exact runtime commit, local diff, and hashes of
the binaries that actually ran. Checking out the right source commit is not
enough: build and refresh the matching testhost before capturing. A supplied
binary bundle must identify the same source/build provenance; another runtime
is an explicitly recorded compatibility experiment, not the default runner.

Obtain that local runtime build and a matching compiler SDK first. The compiler
SDK and the runtime host are **different prerequisites**:

- The compiler must implement `runtime-async=on` for genuine V2 code generation.
  The verified build used SDK `11.0.100-rc.1.26425.128`, available under the
  runtime checkout's `.dotnet` directory. Do not assume the SDK selected by a
  bare `dotnet` on PATH supports the feature.
- The execution host/CoreLib must implement the async-profiler emission contract
  and V2 execution support. The historical capture used runtime revision
  `ed49b6f4b4500423fbecd2c41df5430ec2bbe23e` and framework **12.0.0**.
  Obtain that build or explicitly record/validate a compatible replacement.
  Installing a compiler SDK does not install this instrumented runtime.

### Fetch, bootstrap, and build runtime main

Use the [runtime Windows prerequisites](https://github.com/dotnet/runtime/blob/ed49b6f4b4500423fbecd2c41df5430ec2bbe23e/docs/workflow/requirements/windows-requirements.md):
install the Visual Studio C++/.NET workloads and components from that revision's
`.vsconfig`, CMake, Ninja, and Python; enable Windows/Git long paths as documented.
Use a new short checkout path. The authoritative
[CoreCLR build](https://github.com/dotnet/runtime/blob/ed49b6f4b4500423fbecd2c41df5430ec2bbe23e/docs/workflow/building/coreclr/README.md)
and [libraries build](https://github.com/dotnet/runtime/blob/ed49b6f4b4500423fbecd2c41df5430ec2bbe23e/docs/workflow/building/libraries/README.md)
instructions belong to the pinned revision, not an unrelated current SDK.

This is a pinned snapshot of **official `dotnet/runtime` main**, not an extra
private runtime patch. Bootstrap/build can download substantial dependencies and
take tens of minutes. Do not replace the full first build with an incremental
CoreLib-only build.

```powershell
$RuntimeRoot = Join-Path $WorkspaceRoot 'runtime-async-validation'
$RuntimeRevision = 'ed49b6f4b4500423fbecd2c41df5430ec2bbe23e'
if (Test-Path -LiteralPath $RuntimeRoot) { throw 'Choose a new runtime checkout directory.' }
git clone https://github.com/dotnet/runtime.git $RuntimeRoot
if ($LASTEXITCODE -ne 0) { throw 'Runtime clone failed.' }
git -C $RuntimeRoot checkout --detach $RuntimeRevision
if ($LASTEXITCODE -ne 0) { throw 'The requested runtime-main snapshot is unavailable.' }
Push-Location $RuntimeRoot
try {
    & '.\eng\common\dotnet.cmd' --version
    if ($LASTEXITCODE -ne 0) { throw 'Runtime SDK bootstrap failed.' }
    & '.\build.cmd' -subset clr+libs -arch x64 -c Release -rc Release
    if ($LASTEXITCODE -ne 0) { throw 'CoreCLR/libraries build failed; inspect artifacts\log.' }
}
finally { Pop-Location }
```

`clr+libs` includes `libs.pretest`, which assembles the libraries testhost.
`-c Release` selects the libraries/host configuration as well as `-rc Release`
selecting CoreCLR. Passing only `-rc Release` can leave you using a Debug
testhost while expecting Release.

After a subsequent native/CoreLib rebuild, refresh the **same** Release
testhost, from the runtime root:

```powershell
Push-Location $RuntimeRoot
try {
    & '.\build.cmd' -subset clr.corelib+clr.nativecorelib+libs.pretest -arch x64 -c Release -rc Release
    if ($LASTEXITCODE -ne 0) { throw 'Release CoreLib/testhost refresh failed.' }
}
finally { Pop-Location }
```

That is an incremental refresh, not a replacement for rebuilding native CoreCLR
when native source changed, or rebuilding libraries when their source changed.
Keep the host, native runtime, and CoreLib from one coherent build; do not fix a
stale host by mixing DLLs from different configurations or revisions.

Set the paths for the local main build. Change the variables only if a reviewed
binary bundle with the same provenance was supplied instead; no such download
bundle is assumed by this guide.
`$SdkDotnet` builds the sample; `$RuntimeDotnet` runs the resulting application:

```powershell
$SdkDotnet = Join-Path $RuntimeRoot '.dotnet\dotnet.exe'
$RuntimeDotnet = Join-Path $RuntimeRoot 'artifacts\bin\testhost\net11.0-windows-Release-x64\dotnet.exe'
$RuntimeVersion = '12.0.0'
if (-not (Test-Path -LiteralPath $SdkDotnet)) { throw 'Obtain/bootstrap the matching compiler SDK first.' }
if (-not (Test-Path -LiteralPath $RuntimeDotnet)) { throw 'Obtain/build the instrumented runtime testhost first.' }
& $SdkDotnet --version
& $RuntimeDotnet --list-runtimes
```

Confirm that the selected SDK supports runtime-async and that the host lists
the intended framework. Follow the matched runtime revision's build/bootstrap
instructions if these artifacts are missing; do not substitute an arbitrary
installed host or stale shared-framework directory. Hash the actual runtime
binaries as part of the handoff. The bootstrap uses `global.json`; it need not
install the exact SDK patch used in the reference verification. Record the
actual SDK and verify its V2 code generation below.

```powershell
$RuntimeFrameworkDir = Join-Path (Split-Path $RuntimeDotnet -Parent) "shared\Microsoft.NETCore.App\$RuntimeVersion"
Get-FileHash -Algorithm SHA256 $RuntimeDotnet,
    (Join-Path $RuntimeFrameworkDir 'coreclr.dll'),
    (Join-Path $RuntimeFrameworkDir 'System.Private.CoreLib.dll') -ErrorAction Stop
git -C $RuntimeRoot --no-pager rev-parse HEAD
git -C $RuntimeRoot --no-pager status --short
```

### Build V1 and V2; use V2 for Mixed

At the pinned scenario revision, `Directory.Build.props` defaults to
`TargetFramework=net10.0`, `AsyncVersion=V2`, and separate variant output/obj
directories. **The net10.0 target does not select the compiler SDK or execution
runtime.** The commands below use that repository-default target with the
explicitly selected compiler and later run it on the instrumented host.

| Build property | Code generation | Capture modes |
|---|---|---|
| `AsyncVersion=V1` | Classic state machines globally; defines `ASYNC_VERSION_V1`; no runtime-async compiler feature | `V1`, `V1LiveEnd` |
| `AsyncVersion=V2` | Enables `runtime-async=on`; V1 scenario methods retain explicit `RuntimeAsyncMethodGeneration(false)` controls | `V2`, `Mixed`, `V2LiveEnd`; also supports the explicitly V1 scenario modes |

There is **no separate Mixed build**. Do not pass `AsyncVersion=Mixed`: select
the V2 binary and pass `--trace-capture Mixed` at execution. For a strictly
V1-compiled producer, use the global V1 build rather than the V2 binary's V1
scenario mode. The global V1 binary rejects V2/Mixed modes.

Build both variants in Release so their workload code and referenced parser are
optimized. Restore/build one variant at a time and stop on any failure:

```powershell
$ScenarioProject = Join-Path $ToolsRoot 'AsyncProfilerScenarios\AsyncProfilerScenarios.csproj'
foreach ($Variant in @('V1', 'V2')) {
    & $SdkDotnet restore $ScenarioProject "-p:AsyncVersion=$Variant"
    if ($LASTEXITCODE -ne 0) { throw "Scenario restore failed for $Variant." }
    & $SdkDotnet build $ScenarioProject -c Release --no-restore "-p:AsyncVersion=$Variant"
    if ($LASTEXITCODE -ne 0) { throw "Scenario build failed for $Variant." }
}
$AppFramework = 'net10.0'
$V1App = Join-Path $ToolsRoot "AsyncProfilerScenarios\bin\V1\Release\$AppFramework\AsyncProfilerScenarios.dll"
$V2App = Join-Path $ToolsRoot "AsyncProfilerScenarios\bin\V2\Release\$AppFramework\AsyncProfilerScenarios.dll"
Get-FileHash -Algorithm SHA256 $V1App, $V2App
```

The root properties configure `bin\<V1|V2>\<Configuration>\<TFM>` and
`obj\<V1|V2>`, allowing the two scenario variants to coexist. Do not manually
override only the output folder, remove the shared properties, or copy one
variant's DLL over the other. Keep each output directory's dependencies,
runtimeconfig, and PDB with its DLL.

Check the evaluated V2 configuration if the compiler or output is in doubt:

```powershell
& $SdkDotnet msbuild $ScenarioProject -p:AsyncVersion=V2 '-getProperty:TargetFramework,AsyncVersion,Features,BaseOutputPath,BaseIntermediateOutputPath'
```

Expect `runtime-async=on` in `Features`. A successful build/property check alone
does not prove V2 code generation: when diagnosing a compiler mismatch, inspect
the generated method metadata/IL. V2 scenario methods should have the runtime
async method implementation flag (`Async`, `0x2000`); the explicitly V1 methods
should still be classic state machines. A compiler that ignores the feature
must not be accepted as a V2 validation build.

These instructions build the repository-default `net10.0` outputs, not the
historical `net11.0` assembly byte-for-byte. Use the original binary/build bundle
for exact capture reproduction. If retargeting, record the override and restore
the complete project-reference graph consistently: overriding the app's target
can leave `AsyncProfilerParser` assets for a different target. Do not treat such
a restore mismatch as an async-parser regression.

### Launch the intended mode

Run **one** of the following in the scenario terminal. Explicit framework
selection prevents the testhost from choosing a stale installed framework:

```powershell
& $RuntimeDotnet exec --fx-version $RuntimeVersion $V1App --trace-capture V1
& $RuntimeDotnet exec --fx-version $RuntimeVersion $V2App --trace-capture V2
& $RuntimeDotnet exec --fx-version $RuntimeVersion $V2App --trace-capture Mixed
& $RuntimeDotnet exec --fx-version $RuntimeVersion $V1App --trace-capture V1LiveEnd
& $RuntimeDotnet exec --fx-version $RuntimeVersion $V2App --trace-capture V2LiveEnd
```

The app prints its PID and selected mode, then waits for input. Attach the
collector from a second terminal, wait for collection-start confirmation, and
only then press Enter in the scenario terminal. Mixed mode runs the V1 and V2
suites plus both cross-family parent/child directions. The `--trace-capture`
entry path bypasses the interactive menu's optional in-process EventListener;
use it to avoid accidentally enabling a second profiler session.

### Build the custom dotnet-trace against local TraceEvent

The collector/converter is a custom build from
<https://github.com/lateralusX/diagnostics>, branch
`lateralusX/dotnet-trace-async-support`, reference commit
`6d63a866a4baee0b68eccd0f3533d0243bcd9dc4`. This supplies `collect --async`,
`convert --async`, and combined collection/conversion. A globally installed
released `dotnet-trace` is not a substitute for this workflow.

**The branch alone is not sufficient at that commit.** The two dependency
overrides described below were local, uncommitted changes in the development
checkout. A fresh clone must apply them and the public-API caller update below,
or receive an equivalent reviewed patch.
Without them the tool references released TraceEvent packages instead of this
branch's parser/index/stitcher.

#### Fetch the diagnostics branch

Use another new checkout; do not modify an existing dirty working tree:

```powershell
$DiagnosticsRoot = Join-Path $WorkspaceRoot 'diagnostics-async-validation'
$DiagnosticsBranch = 'lateralusX/dotnet-trace-async-support'
$DiagnosticsRevision = '6d63a866a4baee0b68eccd0f3533d0243bcd9dc4'
if (Test-Path -LiteralPath $DiagnosticsRoot) { throw 'Choose a new diagnostics checkout directory.' }
git clone --branch $DiagnosticsBranch https://github.com/lateralusX/diagnostics.git $DiagnosticsRoot
if ($LASTEXITCODE -ne 0) { throw 'Diagnostics clone failed.' }
git -C $DiagnosticsRoot checkout --detach $DiagnosticsRevision
if ($LASTEXITCODE -ne 0) { throw 'The requested diagnostics revision is unavailable.' }
```

#### Build TraceEvent first and apply the dependency overrides

From PerfView, build the current library in Debug. Keep the configuration
consistent with the reference paths below:

```powershell
$TraceEventProject = Join-Path $PerfViewRoot 'src\TraceEvent\TraceEvent.csproj'
dotnet restore $TraceEventProject --configfile "$PerfViewRoot\Nuget.config"
if ($LASTEXITCODE -ne 0) { throw 'TraceEvent restore failed.' }
dotnet build $TraceEventProject -c Debug --no-restore
if ($LASTEXITCODE -ne 0) { throw 'TraceEvent build failed.' }
$TraceEventBin = Join-Path $PerfViewRoot 'src\TraceEvent\bin\Debug\netstandard2.0'
```

In the diagnostics checkout's `src\Tools\dotnet-trace\dotnet-trace.csproj`,
replace its `Microsoft.Diagnostics.Tracing.TraceEvent` **PackageReference**
with these items inside the existing ItemGroup. Keep its unrelated package and
project references. Do not leave both the package and local reference enabled:

```xml
<Reference Include="Microsoft.Diagnostics.Tracing.TraceEvent">
  <HintPath>$(PerfViewRoot)\src\TraceEvent\bin\Debug\netstandard2.0\Microsoft.Diagnostics.Tracing.TraceEvent.dll</HintPath>
  <Private>true</Private>
</Reference>
<Reference Include="Microsoft.Diagnostics.FastSerialization">
  <HintPath>$(PerfViewRoot)\src\TraceEvent\bin\Debug\netstandard2.0\Microsoft.Diagnostics.FastSerialization.dll</HintPath>
  <Private>true</Private>
</Reference>
<None Include="$(PerfViewRoot)\src\TraceEvent\bin\Debug\netstandard2.0\Dia2Lib.dll;$(PerfViewRoot)\src\TraceEvent\bin\Debug\netstandard2.0\TraceReloggerLib.dll"
      CopyToOutputDirectory="PreserveNewest" Visible="false" />
```

Also change the existing TraceEvent package reference in
`src\Microsoft.Diagnostics.Monitoring.EventPipe\Microsoft.Diagnostics.Monitoring.EventPipe.csproj`
to:

```xml
<PackageReference Include="Microsoft.Diagnostics.Tracing.TraceEvent"
                  Version="$(MicrosoftDiagnosticsTracingTraceEventVersion)"
                  PrivateAssets="All" />
```

That project can still compile against its existing package, but the package
must not flow transitively into `dotnet-trace` and override its local reference.
Adding HintPath alone did not prevent that conflict. FastSerialization must
come from the same PerfView build, and the two interop support DLLs must be
copied alongside the tool. Use `$(PerfViewRoot)`, not the original machine's
hard-coded `C:\git\perfview` paths.

#### Update the converter to the current public API

The pinned diagnostics revision predates this branch's smaller public API.
In `src\Tools\dotnet-trace\TraceFileFormatConverter.cs`, construct
`SampleProfilerThreadTimeComputer` with only `(eventLog, symbolReader)` and set
`EnableAsyncCallStacks` to the existing `stitchAsyncCallStacks` flag before
generation. Use `computer.AsyncCallStacksActive` for the existing no-data
warning after generation. Do not reintroduce the Boolean constructor overload
or grant the converter internal friend access.

#### Build the tool and verify the copied libraries

Build with the SDK selected by diagnostics' own `global.json`, using its
`dotnet.cmd` bootstrap wrapper. This is separate from both the scenario compiler
selection and the instrumented application runner. Supply `PerfViewRoot` on
restore and build:

```powershell
$DiagnosticsDotnet = Join-Path $DiagnosticsRoot 'dotnet.cmd'
$TraceProject = Join-Path $DiagnosticsRoot 'src\Tools\dotnet-trace\dotnet-trace.csproj'
Push-Location $DiagnosticsRoot
try {
    & $DiagnosticsDotnet restore $TraceProject --configfile "$DiagnosticsRoot\NuGet.config" "-p:PerfViewRoot=$PerfViewRoot"
    if ($LASTEXITCODE -ne 0) { throw 'dotnet-trace restore failed.' }
    & $DiagnosticsDotnet build $TraceProject -c Debug --no-restore "-p:PerfViewRoot=$PerfViewRoot"
    if ($LASTEXITCODE -ne 0) { throw 'dotnet-trace build failed.' }
}
finally { Pop-Location }
$TraceToolDir = Join-Path $DiagnosticsRoot 'artifacts\bin\dotnet-trace\Debug\net8.0'
$TraceTool = Join-Path $TraceToolDir 'dotnet-trace.exe'
if (-not (Test-Path -LiteralPath $TraceTool)) { throw 'Expected custom dotnet-trace output is missing.' }
foreach ($Dll in @('Microsoft.Diagnostics.Tracing.TraceEvent.dll',
                   'Microsoft.Diagnostics.FastSerialization.dll',
                   'Dia2Lib.dll', 'TraceReloggerLib.dll')) {
    $SourceHash = (Get-FileHash -Algorithm SHA256 (Join-Path $TraceEventBin $Dll) -ErrorAction Stop).Hash
    $ToolHash = (Get-FileHash -Algorithm SHA256 (Join-Path $TraceToolDir $Dll) -ErrorAction Stop).Hash
    if ($SourceHash -ne $ToolHash) { throw "dotnet-trace has the wrong or stale $Dll." }
}
& $TraceTool --version
& $TraceTool collect --help
& $TraceTool convert --help
```

At this revision the tool targets net8.0 and needs the corresponding runtime to
execute its apphost. It does not need to run on the instrumented runtime being
profiled. The diagnostics SDK can build the tool without the scenario compiler's
runtime-async feature. If the output layout changes, inspect the evaluated
`TargetPath`; do not accidentally select an older executable elsewhere.

Confirm both help commands expose `--async`, record the diagnostics commit and
local diff, and retain the copied-library hashes. A version banner alone does
not identify a source build or its TraceEvent dependency. For binding issues,
inspect `dotnet-trace.deps.json` and host-resolution diagnostics for an actual
conversion, not only help output.

The current demo can emit MSB3277 version-conflict warnings for
`System.Reflection.Metadata` and `System.Collections.Immutable`. Record them;
do not suppress warnings or assume every binding is correct because the build
succeeded. Verify the actual conversion path and investigate new warnings or
runtime binding errors.

**Rebuild dotnet-trace after every relevant TraceEvent rebuild**, then repeat the
hash check. It loads the copy in its own output, not the DLL sitting in the
PerfView source tree. Never repair this by copying just TraceEvent.dll into an
arbitrary installed tool: keep dependencies and the restored runtime graph
consistent.

#### Attach the custom collector

Always invoke the full `$TraceTool` path above, including when the scenario
prints an example command using bare `dotnet-trace`. An explicit provider is
preferable when controlling keywords. For V1 completion-enabled collection:

```powershell
& $TraceTool ps
# Set $ScenarioPid to the verified scenario process, not the PowerShell $PID.
& $TraceTool collect -p $ScenarioPid --profile dotnet-sampled-thread-time --providers 'System.Runtime.CompilerServices.AsyncProfilerEventSource:0x2F04C:5' -o $CapturePath
```

Here `$CapturePath` is a new `.nettrace` path. For the minimal keyword set, the
custom branch also supports:

```powershell
& $TraceTool collect -p $ScenarioPid --profile dotnet-sampled-thread-time --async -o $CapturePath
```

Do not combine the two commands for the same output/session. When the provider
is specified explicitly, `--async` does not upgrade its keyword mask; the branch
warns and keeps the explicit provider settings. Verify the result contains
physical sample stacks, method identities, metadata/resets, and the intended
V1/V2 events.

### Keyword and coverage matrix

The following masks describe the development emission contract. Recheck
`AsyncProfilerEventSource.Keywords` in the runtime revision being tested:

| Mask | Purpose |
|---|---|
| `0xF04C` | Minimal stitching: V2 suspend/complete context and resume callstack; V1 suspend/complete context, exception unwind, and resume callstack |
| `0x2F04C` | The above plus V1 normal method-completion events |
| `0x3FFFF` | All currently defined keyword bits, including method completions and creation relationships |

The local collector's `--async` default used `0xF04C`, **not** the V1
completion-enabled mask. A completion-unwind result cannot be demanded from a
capture that did not record the necessary completion evidence. Context creation
relationships also require the appropriate events; `0x2F04C` alone does not
provide every creation-parent relationship.

Run V1, V2, and mixed directions; compare completion-enabled and fallback
captures. Cover deep chains, wrapper rollover, parallel branches/WhenAll,
exception propagation, pooling/ValueTask dispatch, nested/reentrant contexts,
initial synchronous startup, inline completion, truncated physical roots, and
live activations at capture end. Use targeted synthetic tests for malformed
buffers, append-only future payloads, loss, resets/configuration changes,
process-ID reuse, and deliberately ambiguous/unresolved ownership.
Do not assume every negative case will occur in a normal real capture.

The ordinary producer waits for periodic buffer flushing after its phases.
Keep collection running through that wait and finalization. Disabling/stopping
collection is not a reliable substitute for a producer flush: unflushed async
buffers can leave physical samples without corresponding intervals. LiveEnd
modes intentionally require collection while the activation is still live;
follow that producer revision's instructions instead of waiting for normal
scenario exit. Preserve phase output and capture timing.

Avoid an accidental second profiler/EventListener session: enablement changes
can alter keywords, metadata epochs, resets, and buffer delivery. If intentional,
record both sessions and test that condition explicitly.

### First complete V1 capture: setup, two terminals, export

Finish the PerfView/runtime/scenario/diagnostics setup above first, including the
diagnostics dependency/API edits and copied-library checks. Do not run the
mode-reference commands early. This walkthrough uses the **global V1 build**
and all currently defined keywords so normal completions and creation evidence
are available. Subsequent minimal/fallback captures are separate experiments.

**Setup terminal, from the PerfView root:** the paths below match the new
checkout names used above. Change them together if you chose different paths.
This block also builds the repository helper; it does not require section 5's
existing-capture setup to have been run first.

```powershell
$ErrorActionPreference = 'Stop'
$PerfViewRoot = (Get-Location).Path
$WorkspaceRoot = Split-Path $PerfViewRoot -Parent
$ToolsRoot = Join-Path $WorkspaceRoot 'dotnet-tools-async-validation'
$RuntimeRoot = Join-Path $WorkspaceRoot 'runtime-async-validation'
$DiagnosticsRoot = Join-Path $WorkspaceRoot 'diagnostics-async-validation'
$ValidationDotnet = (Get-Command dotnet -ErrorAction Stop).Source
$SdkDotnet = Join-Path $RuntimeRoot '.dotnet\dotnet.exe'
$RuntimeDotnet = Join-Path $RuntimeRoot 'artifacts\bin\testhost\net11.0-windows-Release-x64\dotnet.exe'
$RuntimeVersion = '12.0.0'
$V1App = Join-Path $ToolsRoot 'AsyncProfilerScenarios\bin\V1\Release\net10.0\AsyncProfilerScenarios.dll'
$V2App = Join-Path $ToolsRoot 'AsyncProfilerScenarios\bin\V2\Release\net10.0\AsyncProfilerScenarios.dll'
$TraceToolDir = Join-Path $DiagnosticsRoot 'artifacts\bin\dotnet-trace\Debug\net8.0'
$TraceTool = Join-Path $TraceToolDir 'dotnet-trace.exe'
$HelperProject = Join-Path $PerfViewRoot 'src\TraceEvent\Samples\AsyncProfilerValidation\AsyncProfilerValidation.csproj'
foreach ($RequiredFile in @($SdkDotnet, $RuntimeDotnet, $V1App, $V2App, $TraceTool, $HelperProject)) {
    if (-not (Test-Path -LiteralPath $RequiredFile -PathType Leaf)) { throw "Missing prerequisite: $RequiredFile" }
}
& $ValidationDotnet restore $HelperProject --configfile "$PerfViewRoot\Nuget.config"
if ($LASTEXITCODE -ne 0) { throw 'Validation helper restore failed.' }
& $ValidationDotnet build $HelperProject -c Debug --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Validation helper build failed.' }
$HelperDll = Join-Path (Split-Path $HelperProject -Parent) 'bin\Debug\net8.0\AsyncProfilerValidation.dll'
$TraceEventBin = Join-Path $PerfViewRoot 'src\TraceEvent\bin\Debug\netstandard2.0'
foreach ($Dll in @('Microsoft.Diagnostics.Tracing.TraceEvent.dll',
                   'Microsoft.Diagnostics.FastSerialization.dll',
                   'Dia2Lib.dll', 'TraceReloggerLib.dll')) {
    $SourceHash = (Get-FileHash (Join-Path $TraceEventBin $Dll) -Algorithm SHA256 -ErrorAction Stop).Hash
    $ToolHash = (Get-FileHash (Join-Path $TraceToolDir $Dll) -Algorithm SHA256 -ErrorAction Stop).Hash
    if ($SourceHash -ne $ToolHash) { throw "Rebuild the custom collector; its $Dll is stale." }
}
$Mode = 'V1'
$App = $V1App
$RunId = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$RunRoot = Join-Path $WorkspaceRoot "async-validation-runs\$RunId"
New-Item -ItemType Directory -Path $RunRoot | Out-Null
$Run = @{
    PerfViewRoot = $PerfViewRoot; ToolsRoot = $ToolsRoot; RuntimeRoot = $RuntimeRoot
    DiagnosticsRoot = $DiagnosticsRoot; ValidationDotnet = $ValidationDotnet
    SdkDotnet = $SdkDotnet; RuntimeDotnet = $RuntimeDotnet; RuntimeVersion = $RuntimeVersion
    Mode = $Mode; App = $App; TraceTool = $TraceTool; HelperDll = $HelperDll
    RunRoot = $RunRoot; CapturePath = (Join-Path $RunRoot "$Mode.nettrace")
    OutputDir = (Join-Path $RunRoot 'exports'); CliOutputDir = (Join-Path $RunRoot 'cli')
    Provider = 'System.Runtime.CompilerServices.AsyncProfilerEventSource:0x3FFFF:5'
}
$SessionPath = Join-Path $RunRoot 'session.clixml'
$Run | Export-Clixml -LiteralPath $SessionPath
foreach ($Repository in @($PerfViewRoot, $ToolsRoot, $RuntimeRoot, $DiagnosticsRoot)) {
    $Name = Split-Path $Repository -Leaf
    git -C $Repository rev-parse HEAD | Set-Content -Encoding UTF8 (Join-Path $RunRoot "$Name.commit.txt")
    if ($LASTEXITCODE -ne 0) { throw "Cannot record revision: $Repository" }
    git -C $Repository status --short | Set-Content -Encoding UTF8 (Join-Path $RunRoot "$Name.status.txt")
    git -C $Repository diff --binary | Set-Content -Encoding UTF8 (Join-Path $RunRoot "$Name.diff.txt")
}
Get-FileHash $App, $HelperDll, $RuntimeDotnet -Algorithm SHA256 |
    Export-Clixml -LiteralPath (Join-Path $RunRoot 'binary-hashes.clixml')
Write-Host "Paste this initialization into BOTH new terminals:"
Write-Host "`$Run = Import-Clixml -LiteralPath '$SessionPath'"
```

The session file contains configuration, **not** shared live PowerShell
variables. Each terminal must import it independently. Keep untracked handoff
sources/patches too: `git diff` alone does not include untracked files. Retain
the framework/CoreLib hashes from the runtime setup as well.

**Terminal A, scenario:** paste the printed `$Run = Import-Clixml ...` line,
then run:

```powershell
Start-Transcript -LiteralPath (Join-Path $Run.RunRoot 'scenario.log')
try {
    & $Run.RuntimeDotnet exec --fx-version $Run.RuntimeVersion $Run.App --trace-capture $Run.Mode
    if ($LASTEXITCODE -ne 0) { throw 'Scenario execution failed.' }
}
finally { Stop-Transcript }
```

Leave it waiting at the printed PID/Enter prompt. Do not redirect stdin from a
file or enable the interactive menu's in-process listener.

**Terminal B, collector:** paste the same initialization line, then verify the
PID printed by Terminal A:

```powershell
$ErrorActionPreference = 'Stop'
& $Run.TraceTool ps
$ScenarioPid = [int](Read-Host 'Verified scenario PID from Terminal A')
Get-CimInstance Win32_Process -Filter "ProcessId = $ScenarioPid" |
    Select-Object ProcessId, ExecutablePath, CommandLine
if (Test-Path -LiteralPath $Run.CapturePath) { throw 'Capture already exists; choose a new run.' }
Start-Transcript -LiteralPath (Join-Path $Run.RunRoot 'collector.log')
try {
    & $Run.TraceTool collect -p $ScenarioPid --profile dotnet-sampled-thread-time --providers $Run.Provider -o $Run.CapturePath
    if ($LASTEXITCODE -ne 0) { throw 'Collection failed.' }
}
finally { Stop-Transcript }
```

Confirm the command line is the intended app on the local testhost, not another
`dotnet` process. Wait for collection-start confirmation in Terminal B, then
press Enter **once in Terminal A**. Let every phase run, including the final
two-second flush wait. The scenario exits and the collector finalizes; wait for
the collector to return. Do not press Enter in the collector to stop it before
the producer's final wait. If collection remains open after normal app exit,
stop it only then and preserve the logs. A truncated/failed run is not a pass.

**Terminal B, export after finalization:**

```powershell
Get-Item -LiteralPath $Run.CapturePath -ErrorAction Stop | Select-Object FullName, Length
Get-FileHash -LiteralPath $Run.CapturePath -Algorithm SHA256 |
    Export-Clixml -LiteralPath (Join-Path $Run.RunRoot 'capture-hash.clixml')
& $Run.ValidationDotnet $Run.HelperDll export $Run.CapturePath $Run.OutputDir
if ($LASTEXITCODE -ne 0) { throw 'Four-view export failed; inspect logs, not partial JSON alone.' }
Get-Content -LiteralPath (Join-Path $Run.OutputDir 'default.summary.json')
```

The first success checkpoint is four nonempty, balanced exports with identical
sample counts/timestamp-metric hashes and `AsyncCallStacksActive=true` in the three
stitched summaries. It is **not** proof every sample was stitched. Continue with
the local viewer in section 6 and the probe in section 7.

For a new **V2** run, change `$Mode = 'V2'` and `$App = $V2App` in setup.
For **Mixed**, use `$Mode = 'Mixed'` and `$App = $V2App`. Re-run setup for a new
directory/session; do not reuse the V1 trace or edit a shared session file after
one terminal has imported it. Keep the initial all-events provider for these
first captures; later use section 4's minimal/completion-mask matrix deliberately.

### Capture expectations and first troubleshooting checks

| Observation | Evidence to check before changing TraceEvent |
|---|---|
| V1 capture | Global V1 binary; captured StateMachineAsync activations and per-frame states during resumed work. Initial synchronous startup need not have an activation. |
| V2 capture | Verified runtime-async method codegen; RuntimeAsync activations. Do not interpret V2's absent V1 state as a live compiler state. |
| Mixed capture | Both families and both cross-family phase directions in the phase log/index. They need not be active in every individual sample. |
| No async index / ordinary-conversion fallback | Actual host/CoreLib, collector DLL hashes, explicit provider mask, metadata/resets, and whether the waiting app was released only after attach. |
| No normal completion evidence | Use an appropriate completion-enabled mask, then check the activation's own process/epoch. A single completed-method event does not prove an activation is fully complete. |
| Hex/unresolved names | Method rundown/identities, captured binaries/symbols and symbol policy. A valid CodeAddressIndex alone is not a resolved method. |
| Physical wrappers but no active interval | Inspect the raw sample and neighboring metadata/reset/flush boundaries; keep the physical stack. Do not "fix" gaps by deleting wrappers. |
| Missing final live stack | Inspect producer flush timing and the LiveEnd CPU window; stop collection while the activation is live, not after it has completed. |
| Retained helpers or `BROKEN` | Compare structural/default/machinery evidence and runtime ownership. Uncertain helpers and missing-root markers can be expected. |

The phase log identifies the actual workload window; do not transplant fixed
thread IDs, sample counts, or times from another capture. Deep/near-limit phases
exercise root preservation, parallel phases must not invent a single executing
branch, nested phases exercise activation ownership, and completion-climb phases
can legitimately retain physical frames from already-completed children.

## 5. Generate comparable views

Use the **same input, symbol policy, grouping, filters, sample metrics, and
managed/native stack policy** for all views. Create a new computer per
generation; `GenerateThreadTimeStacks` is single-use.

| View | Stitching | Normalization | Infrastructure cleanup |
|---|---|---|---|
| Raw baseline | Off | Irrelevant | Off |
| Structural diagnostic | On | Off | Off |
| Default | On | On | Off |
| Machinery-cleaned | On | On | On |

Set `computer.AsyncCallStackTransforms.EnableFrameAnnotations = true` for
diagnosis. It is otherwise off by default. The validation helper keeps the
unfinished internal parent-context feature disabled by default; append
`--parent-contexts` for a separately labelled historical-context experiment.

"Raw" here means the same stack-source host with async stitching disabled; it is
not a replacement for dumping the original event's physical stack. Likewise,
disabling conservative transforms does not disable structural boundary
replacement or verified incomplete-root normalization.

### Conversion through the custom dotnet-trace

After the dependency/hash checks in section 4, the custom tool can provide a
plain versus default-stitched conversion. Use a new output directory and output
**basenames**; this branch adds/replaces the extension with `.speedscope.json`:

```powershell
if (Test-Path -LiteralPath $CliOutputDir) { throw 'Choose a new CLI conversion output directory.' }
New-Item -ItemType Directory -Path $CliOutputDir | Out-Null
& $TraceTool convert $CapturePath --format speedscope -o (Join-Path $CliOutputDir 'raw')
& $TraceTool convert $CapturePath --format speedscope --async -o (Join-Path $CliOutputDir 'default')
```

`--async` enables the custom TraceEvent library's default conservative
presentation. At the pinned diagnostics revision there are no CLI switches for
structural-only mode, machinery cleanup, active-frame annotations, or historical
parent contexts. Use the development helper below for the full four-view
matrix; do not describe a CLI export as machinery-cleaned.

The CLI converter also sets `IncludeEventSourceEvents=false` and an explicit
symbol policy. Match these settings before comparing its sample totals or names
with a different host. It reconverts to an intermediate ETLX beside the input
and normally deletes that cache afterward: use a scratch copy of the capture
if a neighboring ETLX must be preserved, and do not run concurrent conversions
against the same input path.

Check that both output files exist, parse, and have balanced profiles. This
experimental converter can report an error and still print "Conversion
complete"; that line or its exit code alone is not validation. A no-async-data
warning means it fell back to an ordinary conversion, not a successful stitching
test. Continue with the sample/index/semantic checks in sections 6 and 7.

### Build and run the source-controlled development helper

`src\TraceEvent\Samples\AsyncProfilerValidation` replaces the session-local
exporter and supplies the probe used in section 7. It references this checkout's
TraceEvent **project**, not a released NuGet version. No extra analysis package
or original session script is required. It is a development-only diagnostic
helper, not a shipped tool, public extension API, or scenario-specific semantic
oracle. It is publicly signed with the repository's existing development key
and has explicit friend access to internal diagnostic APIs. That signing is
not production signing or a reason to expose those APIs publicly.

External library consumers use either stack computer's `EnableAsyncCallStacks`,
`AsyncCallStacksActive`, and getter-only `AsyncCallStackTransforms` settings
object. Both retain only the original `(TraceLog, SymbolReader)` constructor.
The settings are `EnableNormalization` (true by default),
`EnableInfrastructureCleanup` (false), `EnableFrameAnnotations` (false),
`EnableParentContexts` (false), and `MaximumParentContextDepth` (1).
See the [public usage example](ARCHITECTURE.md#10-stack-source-hosts-emitted-metadata-and-diagnostics).
The helper's deeper probes deliberately use internal friend access.

Build once from the PerfView root; rebuild after relevant TraceEvent changes:

```powershell
$PerfViewRoot = (Get-Location).Path
$ValidationDotnet = (Get-Command dotnet -ErrorAction Stop).Source
$HelperProject = Join-Path $PerfViewRoot 'src\TraceEvent\Samples\AsyncProfilerValidation\AsyncProfilerValidation.csproj'
& $ValidationDotnet restore $HelperProject --configfile "$PerfViewRoot\Nuget.config"
if ($LASTEXITCODE -ne 0) { throw 'Validation helper restore failed.' }
& $ValidationDotnet build $HelperProject -c Debug --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Validation helper build failed.' }
$HelperDll = Join-Path (Split-Path $HelperProject -Parent) 'bin\Debug\net8.0\AsyncProfilerValidation.dll'
```

For an existing capture, define both input and a **new** output path explicitly.
The complete-capture walkthrough already defines these in its session file:

```powershell
$CapturePath = Read-Host 'Full path of the original .nettrace'
$OutputDir = Read-Host 'Full path of a NEW export directory'
& $ValidationDotnet $HelperDll export $CapturePath $OutputDir
if ($LASTEXITCODE -ne 0) { throw 'Export failed; preserve its logs, but do not accept partial results.' }
```

The helper writes `capture.etlx` with raw async batches retained,
`raw/structural/default/machinery.speedscope.json`, corresponding
`.samples.jsonl` and `.summary.json` files, and `run.json` with input/helper/
loaded-TraceEvent paths and SHA-256 hashes. The JSONL contains enumeration
ordinal, time, metric, and complete leaf-to-root names, including synthetic roots.
Full JSONL can be large; keep it with the trace, not in source control or on an
unapproved service. Failed runs can leave partial artifacts; require all four
successful summaries and exports before accepting an export run.

The helper preserves the host's default task/activity settings, enables managed-only
stacks, and uses `SymbolReader`'s inherited/default symbol policy, recording the
effective path. Keep the same approved symbol cache/path on export and probe.
Changing these settings is a separately labelled experiment.

### Optional historical parent contexts

Append `--parent-contexts` to `export`, `probe`, or `probe-qpc` to enable
historical creation-parent chains in the stitched views. The raw view is
unchanged. The helper uses the public
`computer.AsyncCallStackTransforms.EnableParentContexts` setting. Parent contexts
remain opt-in; broader parent-selection hardening is still in progress.

```powershell
$ParentOutputDir = Read-Host 'Full path of a NEW parent-context export directory'
& $ValidationDotnet $HelperDll export $CapturePath $ParentOutputDir --parent-contexts
if ($LASTEXITCODE -ne 0) { throw 'Parent-context export failed; do not accept partial results.' }
```

Use the same trailing flag when probing these exports. `run.json` records
`EnableParentContexts` and the complete arguments; each view summary records
whether that view enabled parent contexts. Historical blocks use
`[Async resumed parent context]` markers and ordinary `[Async V1]` /
`[Async V2]` frame annotations.

Each parent block starts from the activation's recorded resume/append stack.
When both normal-completion and exception-unwind records were observed for that
parent's process, instrumentation kind, and configuration epoch, the block
removes frames completed at or before child creation. Later progress does not
affect the block. The first surviving V1 frame displays inferred running state
`-1`; V2 retains the surviving frame's captured continuation IP. If either record
type is unavailable, the original snapshot is replayed unchanged.

These observation flags do not prove complete, loss-free collection. Captured
states and IPs remain unchanged in frame metadata; progress adjustment does not
establish an exact creation-time execution IP or uncaptured synchronous callees.
Historical-parent provenance remains distinct even when names match active frames.
By default, only the immediate historical parent context is included per active
segment. The public `computer.AsyncCallStackTransforms.MaximumParentContextDepth`
setting can request deeper history; it must be positive. There is no validation
CLI depth option.

The capture must contain creation relationships and parent resume intervals;
unavailable relationships are not inferred. For the external Mixed scenarios,
provider mask `0x3FFFF` includes creation, normal-completion, and unwind events
for both versions. The ordinary `--async` collection default omits creation
records and is insufficient for this experiment.

The timestamp/metric hash is over enumeration-order binary `double` timestamps
followed by `float` metrics on the analysis host; compare like-for-like
hosts/settings. It is not a hash of the capture or stack contents. Metric/count
and balanced-profile checks are built in; semantic correctness still requires
the evidence and guardrails below.

## 6. Audit before visually interpreting the exports

The export recipe checks timestamp/metric/count preservation across views and
balanced evented profiles. Those checks are necessary, not a semantic verdict.

Compare each `.samples.jsonl` with the same view from the previous revision,
aligning by enumeration ordinal and verifying timestamp/metric equality. Keep
thread/process roots in the compared leaf-to-root sequences. Report the exact
changed sample count, affected threads/windows, changed weight, before/after
stacks, and reason for every change class. A targeted rule should not silently
rewrite unrelated samples. Numeric frame/stack intern IDs are not stable
cross-run identities.

Check missing-root placement, remaining dispatcher markers, current versus
target/completing roles, and preserved application/unknown prefixes. Diagnostic
counters show which rules ran, but a nonzero counter is not proof the attribution
was correct. `AsyncCallStacksActive` means an index initialized stitching, not that
every sample matched an activation.

For visual review, open separately named exports and compare the same
thread/time window in the same orientation, zoom, grouping, and filters. Keep
raw/default/machinery side by side; consult structural output when the
responsible stage is unclear. Use an approved local viewer for private traces.
Speedscope event boundaries can have tiny adjustments/slivers, and adjacent
identical stacks can merge visually. Locate the original sample before treating
a displayed width or neighboring box as a single uninterrupted execution.

### Open and navigate Speedscope locally

Use the self-contained ZIP from the official
[Speedscope releases](https://github.com/jlfwong/speedscope/releases).
Download the viewer **without any trace attached**, record its release/hash,
and extract it to a new local directory. No profile URL, upload, third-party
hosting, or running development server is required. The upstream
[usage guide](https://github.com/jlfwong/speedscope#usage) documents the offline
`index.html` distribution and navigation.

```powershell
$ViewerZip = Read-Host 'Full path of the downloaded official Speedscope ZIP'
$ViewerDir = Read-Host 'Full path of a NEW local viewer directory'
if (Test-Path -LiteralPath $ViewerDir) { throw 'Choose a new viewer extraction directory.' }
Get-FileHash -LiteralPath $ViewerZip -Algorithm SHA256 -ErrorAction Stop
Expand-Archive -LiteralPath $ViewerZip -DestinationPath $ViewerDir
$IndexFiles = @(Get-ChildItem -LiteralPath $ViewerDir -Filter index.html -File -Recurse)
if ($IndexFiles.Count -ne 1) { throw 'Locate the release viewer entry point; do not guess among multiple HTML files.' }
Start-Process -FilePath $IndexFiles[0].FullName
```

Open that local page in Chrome or Firefox. Use `Ctrl+O` or drag in
`exports\default.speedscope.json`. Open the same local viewer in separate tabs
for raw, structural, and machinery exports; give every tab the correct file.
Do not load a `.nettrace`, `.etlx`, or `.samples.jsonl` as a Speedscope profile.

1. Press `t` to select the process/thread profile; verify its OS thread identity
   against the capture, rather than assuming profile order is stable.
2. Press `1` for **Time Order**. Use the top minimap to drag-select the workload
   window; `+`/`-` zoom, arrows pan, and `0` returns to the full range.
3. Use `Ctrl+F` for a distinctive method and double-click a frame to fit it.
   Compare the same thread/window in the other tabs. Keep orientation and
   recursion-collapsing settings identical.
4. Record filename, profile/thread, axis units, visible window, full stack, and
   screenshot if useful. JSONL/probe stacks are **leaf-to-root**, unlike a
   root-at-top flame chart; do not reverse the inferred caller relationship.
5. Treat a frame's summary/**All Instances**, Left Heavy (`2`), and Sandwich
   (`3`) as aggregates. Return to Time Order and use the probe before attributing
   one occurrence to a particular activation.

The writer declares its profile units in the JSON (normally milliseconds).
Record the units, not just the number shown by a scaled UI. The chart's
evented ranges represent projected thread-time weights; their boundaries can be
merged or adjusted and are not exact raw sample QPCs. Use the displayed window
to find candidate JSONL/sample records, then anchor the explanation to the
probe's actual event index/QPC. Do not select the nearest sample silently when
the UI time fails to identify one.

## 7. Investigate a pasted or suspicious callstack

### Run the sample-provenance probe

Build the helper from section 5, then point it at the **same export run's**
`capture.etlx`. No original session-local script is needed. Do not use a
neighboring stale cache from a different TraceEvent revision.

The following is independently runnable from the PerfView root, including in a
fresh PowerShell terminal. Enter decimal PID/thread IDs from the actual capture
and a **small inclusive window in trace-relative milliseconds**, using `.` as
the decimal separator, not the viewer's possibly scaled units.

```powershell
$PerfViewRoot = (Get-Location).Path
$ValidationDotnet = (Get-Command dotnet -ErrorAction Stop).Source
$HelperDll = Join-Path $PerfViewRoot 'src\TraceEvent\Samples\AsyncProfilerValidation\bin\Debug\net8.0\AsyncProfilerValidation.dll'
$EtlxPath = Read-Host 'Full path of exports\capture.etlx from the selected run'
$TargetPid = Read-Host 'Captured process ID'
$TargetThread = Read-Host 'Captured OS thread ID'
$StartMSec = Read-Host 'Window start in trace-relative milliseconds'
$EndMSec = Read-Host 'Window end in trace-relative milliseconds'
$ProbeOutputDir = Read-Host 'Full path of a NEW probe output directory'
& $ValidationDotnet $HelperDll probe $EtlxPath $TargetPid $TargetThread $StartMSec $EndMSec $ProbeOutputDir
if ($LASTEXITCODE -ne 0) { throw 'Probe failed; no exact matching sample or another error must be resolved first.' }
Get-Content -LiteralPath (Join-Path $ProbeOutputDir 'probe.summary.json')
Get-Content -LiteralPath (Join-Path $ProbeOutputDir 'physical-and-index.jsonl') |
    ForEach-Object { $_ | ConvertFrom-Json } |
    Select-Object EventIndex, TimestampQpc, TimeRelativeMSec, ProcessID, OsThreadId, ProcessIndex, ThreadIndex
```

Review **all** returned candidates. A broad window, rounded time, or reused PID
is not a unique sample identity. Narrow to a selected record's exact QPC using
a second, new directory:

```powershell
$SampleQpc = Read-Host 'Exact TimestampQpc from the selected physical-and-index record'
$ExactOutputDir = Read-Host 'Full path of a NEW exact-QPC probe output directory'
& $ValidationDotnet $HelperDll probe-qpc $EtlxPath $TargetPid $TargetThread $SampleQpc $ExactOutputDir
if ($LASTEXITCODE -ne 0) { throw 'Exact-QPC probe failed; it did not substitute a nearest sample.' }
```

The helper writes:

| Artifact | Evidence |
|---|---|
| `run.json` | Input ETLX SHA-256, loaded helper/TraceEvent paths and hashes, arguments, settings, UTC run start |
| `physical-and-index.jsonl` | Exact SampleProfiler event/QPC, sample type, process/thread lifetime and indices, unfiltered physical frames, active intervals, captured IPs/states, normal/exception completion and wrapper-reset counts, dispatcher metadata and historical parent/child relationships |
| `<view>.transforms.jsonl` | Synchronously serialized structural/default/machinery frames, origins, presentation, boundary classifications, owning activation identities, method/box identities, and per-callback diagnostics |
| `<view>.emitted.jsonl` | Timestamp-candidate generated stacks, original export ordinals, actual metrics, full names/roots, code-address identities, and `TryGetAsyncProfilerCallStackFrameInfo` role flags/captured state |
| `<view>.summary.json`, `probe.summary.json` | Selected physical/projected counts, callback counts, stitching initialization, symbol policy, and aggregate diagnostics |

Array order is leaf-to-root for frames and outermost-to-innermost for active
intervals. `CapturedState` and emitted `Async.State` retain the captured value,
not a live state-field read. `RecordedCompleted` is the event-derived sum,
**not** the stitcher's inferred wrapper/physical fallback result. Use the
captured wrapper base/reset counts, classified wrapper slot, and structural
diagnostics when explaining the fallback.

The probe enumerates **SampleProfiler thread samples**, not arbitrary GC,
allocation, or custom EventSource anchors. The computer still processes the
whole trace so task/activity context and metrics match normal generation; it
does not reconstruct stacks from an isolated one-event trace. Emitted records
are **timestamp candidates across all threads**, not an asserted unique join:
the public stack-source sample does not carry its originating EventIndex.
Match their `Ordinal` and complete roots against the original export's JSONL.
Activity grouping can create a different thread-frame intern identity from
`GetCallStackForThread`, so neither a display-name-only comparison nor that
frame index alone proves ownership. Do not discard candidates to force a match.
Projected thread-time metrics can be absent or multiple for an event; they are
not raw event payloads. Compare the actual emitted records, not an assumed
one-event/one-sample mapping.

Raw output intentionally has no transform callback. A stitched callback is also
absent when there is no active interval or usable physical stack. Empty callback
files therefore do not prove a probe failed: check `AsyncCallStacksActive`, the raw
stack, and activation list. If several events share a thread/process/QPC, the
callback lists **candidate** event indices rather than claiming a unique match.
Missing dispatcher/parent metadata is unknown evidence, not proof that no parent existed.
Activation records expose `DispatcherInfo` and `Children`; their dispatcher
metadata uses `DispatcherId`, `ParentDispatcherId`, `CreationThread`, and `CreationQpc`.
Diagnostics in the summary aggregate the whole computer run; per-callback
diagnostics explain the selected transformation.

`HasCodeAddress=false` marks synthetic/no-address frames; it is separate from
`Unresolved=true`, which means a real registered address lacks a method name.
Use the captured state and origin/role flags rather than inferring execution
from the display name or treating a synthetic thread/root frame as a symbol error.

If the change under investigation affects conversion/indexing, first regenerate
the four-view export from the original `.nettrace` into a new directory.
Retaining raw async batches enables a deeper decoder follow-up, but this probe
does not decode the surrounding batches automatically or certify every
scenario's semantics. Use the procedure below when the evidence remains
ambiguous.

### Establish the sample, not just its aggregated name

Get the exact export/run, process instance, OS thread, full unfiltered stack,
sample-relative time/window, metric, and active view flags. "All Instances"
aggregates other occurrences and does not establish which activation this
instance represents. A copied rounded time may identify several samples.

Enumerate the corresponding raw sample events and record provider/event,
event index, exact QPC, relative time, PID/ProcessIndex, thread/lifetime, metric
mapping, and every physical frame leaf-to-root. Keep IP, CodeAddressIndex,
MethodIndex, module identity, full name, and unresolved status distinct.

### Query the index at that instant

Use the public `AsyncProfilerCallStackComputer(log)` and prefer
`computer.GetCallStacks(anchorEvent)` for an event from the same TraceLog.
For an already identified QPC, use
`computer.GetCallStacks(processId, osThreadId, timestampQpc)`.
This also reaches async-only OS threads without a `TraceThread` entry.
There are no millisecond or ThreadIndex facade overloads; explicitly convert
through `RelativeMSecToQPC` when necessary, accounting for tick rounding.
Returned activations and their captured-frame views can be retained across
later queries. Raw segment queries do not require a stack source.
`CapturedFrames` is the finalized resume/append list, not a fresh physical
capture at the query timestamp. Its public frame views expose `CodeAddress`,
`CapturedInstructionPointer`, and `CapturedState` (null for V2).
Completion-observed flags summarize the activation-start configuration epoch,
not loss-free coverage or only events before that query.

The detailed diagnostics below also use **internal development APIs**.
Use the signed validation helper or repository tests for internal index,
classifier, runtime-profile, and emitted-provenance access.

For each returned activation, dump:

- `Thread`, `DispatcherId`, `Depth`, `FramesIndex`, `StartQpc`, `EndQpc`,
  `Frames.Kind`, `ContinuationIndexBase`, and `WrapperCount`.
- Every captured frame's ordinal, `MethodIdAt(i)`, `FrameStateAt(i)`,
  `CodeAddressAt(i)`, resolved method/module, and unresolved status.
- `GetMethodCompletedFrameCount(qpc)`,
  `GetExceptionCompletedFrameCount(qpc)`, `GetCompletedFrameCount(qpc)`,
  `GetWrapperResetCount(qpc)`, and
  `activation.MethodCompletionObserved` and `activation.ExceptionCompletionObserved`.
- Dispatcher metadata and parent/child relationships, if captured, using
  the internal index's `GetDispatcherInfo`, `GetParentCallStack`, and
  `GetChildDispatcherInfos`.

The progress methods count recorded deltas/resets through the timestamp,
inclusive. They do not validate interval membership or cap counts at the
captured frame count. The wrapper overload takes a physical wrapper slot for
that activation, not an ordinal in the captured frame list.

Intervals are half-open `[StartQpc, EndQpc)`; open-at-EOF intervals end at
`long.MaxValue`. Activations are returned outermost-to-innermost; captured method
frames are leaf-first. A dispatcher may have multiple resume intervals:
dispatcher ID alone is not an activation identity.

### Inspect structural and presentation evidence

Add a read-only callback using `computer.AsyncProfilerCallStackTransformPipeline.Add(context => ...)`
before generation. Filter to the target QPC **and** owning process/thread.
Snapshot `context.Frames`, `Segments`, `TimestampQpc`, and diagnostic values.
For each `AsyncProfilerStitchedFrame`, include `Origin`, `ContextKind`, owning activation,
`SegmentFrameIndex`, `CodeAddress`, `Method`, `BoxedStateMachineMethod`,
`SyncFrameKind`, and `context.ClassifyBoundary(frame.CodeAddress)`.

Custom callbacks run **after** enabled built-in transforms. Use separate
structural/default/machinery generations to observe their respective results,
not one callback that is assumed to run before every stage. Context/frame lists,
activation objects, and diagnostics supplied to callbacks are reused: serialize
or copy the needed primitive values immediately. A shallow frame copy still
references a reusable context. The public `AsyncProfilerCallStackComputer.GetCallStacks` results,
in contrast, are stable and may be retained across later queries.

At the emitted stack-source layer,
`source.TryGetAsyncProfilerCallStackFrameInfo(frameIndex, out info)` exposes captured identity and
role flags. Speedscope names do not preserve all that provenance. Do not use
internal parser/index queries, frame-provenance hooks, or runtime-profile
members as if they were public APIs in an external helper. Public presentation
callbacks use `AsyncCallStackTransforms.Add` and the narrow
`AsyncProfilerCallStackTransformContext`, without internal diagnostics or
classifiers. The public `AsyncProfilerCallStackSourceBuilder` shares the
existing computers' reconstruction core; see ARCHITECTURE.md section 10.
Its event overload checks the owning TraceLog instance; bare physical/root
handles carry no owner identity and can only be range-checked.

Inspect neighboring samples and other relevant threads, and correlate resume,
append, completion, exception, reset, and metadata records at or before the
sample. Raw async batches can be delivered later than their inner QPCs; use
decoded inner timestamps for execution chronology. For an independent raw
decoder, retain one `Manifest` per process instance across buffers;
do not reset the manifest for every callback.

Finally inspect the scenario's awaits and generated methods. Distinguish entry
before body execution, body execution, suspension/registration, completion
invoking parents inline, and return through old invocations. If inlining is
material, inspect the actual generated code and captured runtime's JIT rules;
do not infer it solely from a missing body or a familiar generic box name.

### State and provenance interpretation

| Evidence/role | Interpretation |
|---|---|
| Retained synchronous frame | Physical captured evidence; may be a callee or still-returning invocation, not necessarily the logically current async method |
| `AsyncCurrent` / `info.IsCurrent` | Physically matched current application method; annotated V1 display infers `-1` |
| `AsyncDispatchTarget` / `info.IsDispatchTarget` | Identified resumption target without a separately matched body; a V1 box carrier retains captured state |
| `AsyncCompleting` / `info.IsCompleting` | Completion records prove a physically present V1 method logically completed; display infers `-2` |
| `AsyncCompletionUnwind` / `info.IsCompletionUnwind` | Verified return work attributed to the completed activation's logical root; display infers `-2` without claiming the root body is physically present |
| `AsyncRemaining` | Captured suspended async callers, not proof of current execution |
| Historical parent / `info.IsParentContext` | Creation causality, not necessarily an active physical caller |
| `AsyncContextParentCurrent` / `info.IsParentContextCurrent` | First surviving logical parent frame at child creation, inferred with both progress record types; V1 displays `-1`, while captured state/IP remain unchanged and `IsCurrent` stays false |
| `BROKEN` | Missing physical root evidence, independent of whether the logical activation is complete |

`AsyncProfilerCallStackSourceFrameInfo.State` always retains the captured V1 compiler state,
even if an annotated name displays inferred `-1` or `-2`. It is neither a sampled
live state-field read nor an IL/native offset. V2 does not carry meaningful V1
compiler states. Identical-looking names can have different execution roles.

## 8. Non-negotiable interpretation and implementation guardrails

- Instrumentation family (V1/V2), buffer version, and per-event manifest schema
  version are separate concepts. Honor advertised payload framing/version, read
  supported fields, and skip appended fields/unknown advertised events using
  declared lengths. Future schemas must append, not change/remove known layout.
- Malformed payloads must produce diagnostics, not success-shaped results.
  Framing/header failures can end a buffer; valid framing lets the decoder skip
  a malformed typed payload. Decoder recovery and activation-state recovery are
  different responsibilities. Respect process/configuration/reset/loss
  boundaries; do not fabricate continuity through missing evidence.
- Method completion is optional. V1 normal completion uses recorded evidence
  when available for the activation's own process/epoch, otherwise ordered
  physical matching. Exception evidence must be reconciled with still-present
  exceptional invocations. V2 fallback uses wrapper resets, pool size, and the
  captured base; it already includes exceptional unwinds. Do not add exception
  counts to it again or borrow another activation's nested wrapper.
- No completion records means a box-only V1 stack can be entering or returning.
  It does not prove a fully completed unwind. Conversely, an interval can still
  be active with no remaining logical methods while native calls return.
  Completion events after the sample do not retrospectively complete it.
- The completed-V1-unwind fold requires full recorded completion and a resolved,
  uniquely owned, ordered physical return spine. No/partial/excess counts,
  recursion, foreign/unresolved/interrupted spans, or an unresolved logical root
  must not be converted into an apparently proven completed root.
- A box target, annotation, or cleaner machinery view is not stronger evidence
  of application-body execution. Optional cleanup attributes owned overhead.
  Keep arbitrary/unknown prefixes unless a verified owning spine justifies
  folding the whole prefix; class initialization alone does not establish that.
- Do not deduplicate by display name, delete all CoreLib/static/unknown frames,
  erase application bridges, or use a helper count/frame cap as an ownership
  rule. Match method/module/context identity and runtime-approved structure.
- Select runtime rules from captured CoreLib/runtime metadata, never the
  analysis host's installed runtime. Unknown/conflicting ownership must fall
  back conservatively. Keep perfmap/name normalization local to stitching;
  do not globally rewrite stored symbols to make one comparison pass.
- No active segment, or no usable physical stack, keeps the normal physical
  path. Initial synchronous startup can legitimately precede any activation.
  Do not drop samples or remove wrappers just to hide coverage gaps.
- `BROKEN` must survive export. The writer filters the reserved
  `StackSourceFrameIndex.Broken`; an export-visible marker is an ordinary
  interned named frame, as in `FrameIntern("BROKEN", emptyModule)`. Preserve
  correct root ordering and avoid duplicate markers. A stack of 100 frames is
  not, by itself, truncation proof.

## 9. The iterative change loop

1. Reproduce on the original capture and current branch. Freeze the baseline
   exports, exact sample identity, metrics, flags, and source revisions.
2. Collect raw/index/structural/presentation evidence using section 7. State
   proven facts separately from runtime-specific deductions and open questions.
3. Agree the general rule before changing semantics. Specify which layer owns
   it, what establishes ownership, what remains unchanged, and when to fail open.
   Do not bake scenario names, PIDs, timestamps, or chain depths into production.
4. Add a few focused positive/negative tests in the existing layer. For
   presentation rules, exercise default and cleanup views, annotations, and
   interrupted/unresolved/foreign or nested ownership as relevant. Include an
   emitted-stack/integration case when metadata, symbols, or roots matter.
5. Implement the smallest change. Run the targeted Debug cases, then the
   affected async regressions. Report failures or narrower framework coverage
   honestly; do not relabel an investigation as a passing test.
6. Rebuild the actual helper/consumer, reconvert ETLX if necessary, and regenerate
   distinctly named outputs. Compare all samples/metrics and explain all
   changed stacks, not only the originally cited one. Recheck negative examples.
7. Give the reviewer exact filenames and a short thread/time-window checklist.
   Distinguish automated checks from pending visual inspection. Preserve raw,
   default, and machinery evidence even when two now render identically.
8. Commit a coherent checkpoint only when requested/agreed. Keep external
   captures, runtime artifacts, generated exports, and unrelated scratch helpers
   out of the source commit; the reusable validation sample is repository source.

Do not use performance claims to justify a more complex rule without measurement.
Functional trace validation is not a throughput/allocation benchmark.

## 10. Peer/AI handoff format

For each investigated stack, report:

```text
Input: capture hash, producer/runtime/TraceEvent revisions, ETLX provenance
View: flags, symbol policy, filters/grouping, export filename
Sample: process instance/PID, OS thread/lifetime, exact QPC, relative ms, metric
Physical evidence: full leaf-to-root stack, unresolved identities, truncation
Index evidence: activation intervals/depths, captured frames/states, completion/reset counts
Projection: structural origins, default changes, optional-cleanup changes, emitted role flags
Explanation: proven facts; runtime/scenario-specific deductions; uncertainty
Verdict: expected / bug / insufficient evidence, with source/test references
If changed: focused tests, affected regressions, full-trace sample/metric diff
Review: new output filenames, exact visual checkpoints, remaining limitations
```

An AI taking over should read this file and the affected implementation/tests
before proposing a fix. Ask for missing capture/producer artifacts rather than
inventing them. A README export recipe does not replace a scenario-specific
semantic audit, and a visually plausible stack does not prove the rule.

This workflow validates async-profiler reconstruction; it does not infer missing
events, provide general Task/TPL causality enrichment, or guarantee complete
runtime-specific cleanup for unknown runtimes. Historical parent-context inclusion is
an explicit separate option, not a default execution claim.
