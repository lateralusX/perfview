# Async profiling in TraceEvent: architecture and API guide

This guide starts with a plain-language explanation of how a trace containing
async-profiler data becomes a view with stitched async stacks. The numbered
architecture/API reference afterward describes the implementation in this
feature branch, not an arbitrary released TraceEvent package.
It is a companion to the
[validation and callstack-investigation README](README.md): that document
explains how to build the producer, capture traces, compare views, and
investigate samples; this document explains the library that makes those
operations possible.

## From a trace file to a stitched stack

The central idea is to combine two different accounts of the same execution:

- **The physical stack:** what was actually on a thread's stack when it was
  sampled.
- **The logical async chain:** which async operation was being resumed and
  which callers were awaiting it, even when those callers were no longer on
  the physical stack.

An ordinary sample might show a worker thread entering a resumed async method,
without showing the application callers that originally awaited that method.
Async-profiler data supplies the continuation context needed to reconnect
those callers. Stitching combines that context with the physical sample; it
does not replace physical execution with an arbitrary history of async calls.

The chain, in broad terms, is:

```text
Trace file containing physical samples and async-profiler records
  -> decode the records and their original timestamps
  -> build a searchable timeline of active async executions
  -> associate recorded addresses with methods
  -> save the processed information in a reusable trace cache
  -> find the async context for one physical sample
  -> align the physical and logical stacks and stitch them
  -> apply the selected presentation policy
  -> display or export the resulting stack
```

These are conceptual stages, not necessarily separate passes over the file.
For example, method association and timeline construction happen together
while the trace is converted.

### What must be present in the trace?

Three kinds of information work together:

| Information | What it tells us |
|---|---|
| Ordinary performance samples and their physical stacks | Where a thread was executing when work was sampled. These samples supply the time and weight to attribute. |
| Async-profiler records | When async executions resumed, suspended, or completed; their captured continuation chains; and available completion/progress information. |
| Method, module, and symbol information | Which application/runtime methods the recorded addresses belong to, so the two stacks can be matched and presented meaningfully. |

Async-profiler records alone are not CPU samples. Physical samples alone
usually cannot recover callers that disappeared across an await. Symbols alone
do not establish the async relationships.

### 1. Read the profiler's records, not just their delivery time

The trace contains batches of async-profiler records. TraceEvent unpacks those
batches using the format information supplied by the runtime. Each contained
record has its own original timestamp and thread identity.

That distinction matters because a batch can be delivered after the execution
it describes. The logical record's time, rather than the time its batch was
flushed, determines which physical samples it can explain.
The reader also establishes where trustworthy profiler state begins; it does
not assume that a capture started at the beginning of the application.

### 2. Turn the records into an execution timeline

For each process and thread, TraceEvent records windows in which particular
async executions were active. Resuming opens a window; suspending or completing
closes it. Nested executions retain their nesting rather than overwriting the
outer execution.

Each window refers to a captured logical call chain and its progress
information. Classic state-machine async can supply that chain incrementally;
those pieces are assembled before the window is finalized. Completion and
unwind records, when available, help distinguish frames that have finished
from frames that still belong in the continuation.

This produces a timeline that can answer:
**"At this exact instant on this thread, which async executions were active,
and what chains did they describe?"**

### 3. Give recorded frames a shared method identity

Both physical stacks and async chains contain addresses. TraceEvent associates
them with the method/module information in the capture, including information
that arrives later during rundown. Symbol information supplies readable names
and can support source lookup.

Matching is not merely comparing the text displayed in a chart. The system
needs compatible method identities to recognize when a physical frame and a
logical async frame represent the same method. Without enough identity
information, some reconstruction or cleanup must remain conservative.

### 4. Keep the processed evidence in a cache

The processed trace cache stores the async timeline, shared frame lists,
completion information, creation relationships, and address references.
Reopening it does not require decoding the original profiler records again.
The async information is loaded when needed and can be released and reloaded.

Importantly, the cache stores the **evidence needed to reconstruct stacks**,
not one permanently stitched version of every sample. The same evidence can
produce different views later without changing the original capture.

### 5. Find the async context of a particular physical sample

When stitched stacks are requested, TraceEvent takes a physical sample and
looks up the async timeline using the sample's process instance, thread, and
exact recorded time.

The result can contain one active async execution or several nested ones.
It is not the nearest async record, the last operation seen on any thread,
or the entire async history of the process.

If there is no active async context or no usable physical stack, the sample
continues through the ordinary stack-generation path. A gap in captured async
data is not filled by guessing.

### 6. Work out which part of the async chain the sample represents

A resumed chain can change while it runs: an inner method can finish and
resume its caller, while completed invocations are still physically returning.
The captured chain must therefore be interpreted at the sample's instant,
not treated as though its original leaf were always executing.

The two instrumentation families provide different evidence for this:
runtime-supported async (V2) has continuation-wrapper positions; classic
state-machine async (V1) can use ordered physical state-machine matches.
Recorded completion and exception-unwind events refine the decision where
available. The implementation reconciles those signals rather than blindly
adding counts that might describe the same progress twice.

The result distinguishes actual body execution, dispatch toward an indexed
target, suspended callers, and logically completed work still finishing its
physical invocation. Seeing an async method in the resulting chain does not,
by itself, mean that method's body was executing.

### 7. Join the stacks at verified async handoff points

TraceEvent walks the physical stack and the active async executions together,
starting with the innermost execution. Recognized runtime dispatch points and
matching methods tell it where the two accounts connect.

It preserves physical application work and its callees, avoids adding a
second copy of an already represented current method, and inserts the
remaining logical callers where runtime dispatch had broken the visible
application chain. With nested async executions, this happens in successive
sections; it is not a single append of every async frame below the stack.

For a simplified example, suppose the application chain is:

```text
HandleRequest
  FetchDocument
    ReadResponse
```

The first two methods are awaiting the operation beneath them. After
`ReadResponse` resumes on a worker thread, a physical sample might show:

```text
Thread / process
  Runtime dispatch
    ReadResponse
      ParseResponse       <- sampled work
```

The stitched application view can then show:

```text
Thread / process
  HandleRequest           <- logical awaiting caller
    FetchDocument         <- logical awaiting caller
      ReadResponse        <- matched physical async method
        ParseResponse     <- preserved physical callee
```

These diagrams run from caller to callee and omit retained runtime helpers
for clarity. The important point is that `ParseResponse` remains the sampled
work, `ReadResponse` is not duplicated, and the two awaiting callers provide
logical context rather than a claim of physical execution.

If matching is ambiguous, the system retains uncertain physical work instead
of forcing this tidy layout. An independently verified truncated root can be
shown as `BROKEN`; that reports missing ancestry rather than inventing a
thread-pool or application caller.

### 8. Choose how much runtime machinery to show

Structural stitching decides how the physical and logical evidence connect.
Presentation is a separate decision afterward.

The default view performs strong-evidence normalization, such as simplifying
recognized startup or handoff scaffolding. An additional, optional cleanup view
can attribute verified profiler/runtime overhead to the relevant async
context. Neither is a general instruction to delete all framework frames.
A structural view leaves those optional presentation changes disabled.

This separation also matters for completion: verified fully completed V1
return-unwind work can be attributed to the logical root as completed work,
without claiming that the root method's body is currently running.

Historical creation-parent context is another optional addition. It explains
where an execution originated, not who was physically calling it at the sample
time, and is labeled separately.

### 9. Attach the result to the original sample

The generated sample receives the reconstructed stack while the stack host
continues to account for its original time and weight. Stitching does not
turn suspended callers into additional CPU samples or create another copy of
the sample for each inserted frame. An inclusive view can show that sample's
weight at several ancestor frames; that is shared attribution, not additional
execution.

The stack can now be displayed or exported. Frame-role metadata distinguishes
physical execution, awaiting ancestry, dispatch targets, completion, and
historical parents even when displayed names look similar. A viewer such as
Speedscope may not retain all that metadata, which is why the validation
workflow also preserves investigation sidecars.

**The final view answers two questions together: "Where was this sample's work
executing?" and "Which async application chain gives that work its context?"**
It cannot recover profiler buffers or physical frames that were never
captured, and a clean-looking chart is not proof that every reconstruction is
unambiguous.

## Worked examples from captured V2 and V1 samples

These examples follow real samples through the validation helper's four
views, not invented stacks or different samples selected for each stage:

| View | Structural stitching | Conservative/default presentation | Optional machinery cleanup |
|---|---|---|---|
| `raw` | Off | Off | Off |
| `structural` | On | Off | Off |
| `default` | On | On | Off |
| `machinery` | On | On | On |

Each view is generated independently from the same converted capture.
Active-frame annotations are enabled and historical creation-parent ancestry
is disabled. The displayed sample keeps its timestamp and weight in every
view. Structural stitching already replaces some dispatch scaffolding to
join the stacks; "structural" does not mean "no runtime frames removed."

The two main samples show the same scenario family: three nested async
ValueTask methods, with the innermost method resumed from a delay and now
doing CPU work through `BurnCpuAsync` and `BurnCpuBurst`.

### Capture and display conventions

| Example | Capture filename | PID / OS thread ID | Relative time (ms) | Exact sample QPC | Export ordinal | Raw event index |
|---|---|---|---|---|---|---|
| V2 ValueTask | `async-v2-numbered-completion-climb.nettrace` | 13060 / 13160 | 5472.0473 | 2471080254290 | 10190 | 10191 |
| V1 ValueTask | `async-v1-only.nettrace` | 42352 / 58592 | 2031.9271 | 4051662443652 | 7249 | 7251 |

Capture SHA-256 values:

```text
async-v2-numbered-completion-climb.nettrace
01D465DBC42FD7C2BF40647D4017BF56A6568CFD8C2023EC75CB25131CCA6DC3

async-v1-only.nettrace
EB4E3B19C001A114A9C71715A8B157D59236ABC48755D2784C73F99B24AB585B
```

These captures are external artifacts, not files supplied by this repository.
The coordinates identify the examples only; they are not regression baselines
or coordinates to reuse with a fresh capture. To replay them, obtain the
matching captures, use the README's `export` workflow, and run `probe-qpc`
against each generated `capture.etlx` with the PID, thread, and QPC above.
Match emitted records by the full-export ordinal as well as their time and
roots; an export ordinal and a raw event index are different identifiers.
Without those artifacts, capture the corresponding V1/V2 scenarios using the
README and investigate an equivalent sample at its own coordinates.

The diagrams run **root to leaf**, ending at the sample-kind marker. Logical
awaiting/dispatch-target edges express context and attribution, not necessarily
literal physical caller/callee relationships.
Module/namespace prefixes and most signatures are shortened. All stacks start
at `ThreadPoolWorkQueue.Dispatch`; the unchanged process, activity-grouping,
thread, and worker-start prefix above it is omitted, not removed by a pass.
`CPU_TIME` and `UNMANAGED_CODE_TIME` are host accounting markers, not methods.

The **async resume inputs** are shown separately in **leaf-to-root slot
order**, as recorded, before completion trimming or presentation. Both
protocols record native code addresses; the names below are resolved from
those addresses, not strings carried in the events. V1 additionally records
compiler states; V2 has no per-frame state field. The resume QPC identifies
the earlier event that opened the sample's active interval, not the sample
itself or the batch's delivery time.

### V2: reconnect the awaiting ValueTask callers

**Physical input (`raw`).** The sample reaches the third level through a
continuation wrapper. The first two awaiting methods are absent:

```text
ThreadPoolWorkQueue.Dispatch
  TimerQueueTimer.Fire
    Task.DelayPromise.CompleteTimedOut
      Task.TrySetResult
        Task.RunContinuations
          RuntimeAsyncTask<Int64>.DispatchContinuations
            RuntimeAsyncTask<Int64>.InstrumentedDispatchContinuations
              Continuation_Wrapper_1
                V2TraceScenarios.ValueTaskLevel3Async
                  V2TraceScenarios.BurnCpuAsync
                    TraceCaptureWorkload.BurnCpuBurst
                      CPU_TIME
```

**Async resume input (leaf to root).**

```text
ResumeRuntimeAsyncCallstack (QPC 2471080224563, dispatcher 0x6)
ContinuationIndex = 0
[0] 0x0 (sentinel; no method)
[1] V2TraceScenarios.ValueTaskLevel3Async
[2] V2TraceScenarios.ValueTaskLevel2Async
[3] V2TraceScenarios.ValueTaskLevel1Async
```

The recorded chain contains the third-level method followed by its two
awaiting callers. Its first slot is a zero-address sentinel; wrapper slot 1
aligns this sample with the real `ValueTaskLevel3Async` frame at slot 1.
That slot position does not mean an application method has already completed.

**1. Structural result.** Match the physically present third-level method,
keep its physical callees, and insert the two awaiting callers. Replace the
wrapper/dispatch splice machinery rather than duplicating the current method:

```text
ThreadPoolWorkQueue.Dispatch
  TimerQueueTimer.Fire
    Task.DelayPromise.CompleteTimedOut
      Task.TrySetResult
        Task.RunContinuations
          [Async V2] V2TraceScenarios.ValueTaskLevel1Async
            [Async V2] V2TraceScenarios.ValueTaskLevel2Async
              [Async V2] V2TraceScenarios.ValueTaskLevel3Async
                V2TraceScenarios.BurnCpuAsync
                  TraceCaptureWorkload.BurnCpuBurst
                    CPU_TIME
```

The third-level frame represents the physically matched current method.
The first two represent awaiting ancestry, not additional physical execution.
The timer and Task completion bridge remains visible.

**2. Conservative/default result: identical to the structural result above.**
There is no remaining sampled-leaf wrapper, verified inter-activation handoff,
or V1 startup pattern to normalize in this sample. The default pass is not a
requirement to make every stack shorter.

**3. Machinery-cleanup result.** The optional policy folds the verified
four-frame timer-to-resumption bridge as one span:

```text
ThreadPoolWorkQueue.Dispatch
  [Async V2] V2TraceScenarios.ValueTaskLevel1Async
    [Async V2] V2TraceScenarios.ValueTaskLevel2Async
      [Async V2] V2TraceScenarios.ValueTaskLevel3Async
        V2TraceScenarios.BurnCpuAsync
          TraceCaptureWorkload.BurnCpuBurst
            CPU_TIME
```

The application chain and CPU work are unchanged. This view attributes the
recognized resumption overhead to that async context; it does not assert
that the timer helpers were never executed.

### V1: reconnect awaiting callers, then normalize synchronous startup

For this diagram, `Box3` abbreviates the captured
`AsyncTaskMethodBuilder<Int64>.AsyncStateMachineBox` for
`V1TraceScenarios.<ValueTaskLevel3Async>d__16`; `ProfilerBox3` abbreviates its
`AsyncProfilerAsyncStateMachineBox` counterpart. They are physical runtime
helpers, not extra application methods.

**Physical input (`raw`).** The third-level state machine has resumed, then
called `BurnCpuAsync` synchronously. Both its dispatch helpers and the
new child's compiler-generated startup are still on the stack:

```text
ThreadPoolWorkQueue.Dispatch
  TimerQueueTimer.Fire
    Task.DelayPromise.CompleteTimedOut
      Task.TrySetResult
        Task.RunContinuations
          AwaitTaskContinuation.RunOrScheduleAction
            Box3.MoveNext()
              Box3.MoveNext(Thread)
                ProfilerBox3.InstrumentedMoveNext
                  ProfilerBox3.MoveNextAsDispatcher
                    Box3.MoveNext(Thread, Flags)
                      ExecutionContext.RunInternal
                        Box3.ExecutionContextCallback
                          <ValueTaskLevel3Async>d__16.MoveNext
                            V1TraceScenarios.BurnCpuAsync
                              AsyncTaskMethodBuilder<Int64>.Start
                                AsyncMethodBuilderCore.Start
                                  <BurnCpuAsync>d__115.MoveNext
                                    TraceCaptureWorkload.BurnCpuBurst
                                      CPU_TIME
```

**Async resume input (leaf to root).**

```text
ResumeStateMachineAsyncCallstack (QPC 4051662428882, dispatcher 0xF)
ContinuationIndex = 0
[0] V1TraceScenarios+<ValueTaskLevel3Async>d__16.MoveNext  captured state = 0
[1] V1TraceScenarios+<ValueTaskLevel2Async>d__15.MoveNext  captured state = 1
[2] V1TraceScenarios+<ValueTaskLevel1Async>d__14.MoveNext  captured state = 1
```

This is the actual resume payload's frame sequence; it already contains all
three indexed frames for this activation. Other V1 activations can be extended
by later append events. There is no V2-style sentinel in this resume, and no
recorded method or exception completions at the sample. `BurnCpuAsync` is not
in this recorded chain: its synchronous startup and body are supplied by the
physical stack instead.

**1. Structural result.** Join at the V1 dispatcher/current-method evidence,
insert the two awaiting callers, and retain the physical work above the
matched third-level method:

```text
ThreadPoolWorkQueue.Dispatch
  TimerQueueTimer.Fire
    Task.DelayPromise.CompleteTimedOut
      Task.TrySetResult
        Task.RunContinuations
          AwaitTaskContinuation.RunOrScheduleAction
            [Async V1] V1TraceScenarios.ValueTaskLevel1Async [ordinal 14, state 1]
              [Async V1] V1TraceScenarios.ValueTaskLevel2Async [ordinal 15, state 1]
                [Async V1] V1TraceScenarios.ValueTaskLevel3Async [ordinal 16, state -1]
                  V1TraceScenarios.BurnCpuAsync
                    AsyncTaskMethodBuilder<Int64>.Start
                      AsyncMethodBuilderCore.Start
                        <BurnCpuAsync>d__115.MoveNext
                          TraceCaptureWorkload.BurnCpuBurst
                            CPU_TIME
```

The first two async frames are awaiting context; the third is physically
matched. Its displayed `-1` means running, inferred from that physical match:
the captured state `0` in the index is not rewritten. The annotation ordinals
`14`, `15`, and `16` are compiler state-machine name ordinals, not sample
ordinals. Role-aware name rendering already applies in the structural view;
it is separate from the optional conservative pass.

**2. Conservative/default result.** Recognize the same-method
`BurnCpuAsync -> builder Start -> builder-core Start -> generated MoveNext`
startup span. Keep its physical kickoff and CPU callee; remove the three
redundant startup representations:

```text
ThreadPoolWorkQueue.Dispatch
  TimerQueueTimer.Fire
    Task.DelayPromise.CompleteTimedOut
      Task.TrySetResult
        Task.RunContinuations
          AwaitTaskContinuation.RunOrScheduleAction
            [Async V1] V1TraceScenarios.ValueTaskLevel1Async [ordinal 14, state 1]
              [Async V1] V1TraceScenarios.ValueTaskLevel2Async [ordinal 15, state 1]
                [Async V1] V1TraceScenarios.ValueTaskLevel3Async [ordinal 16, state -1]
                  V1TraceScenarios.BurnCpuAsync
                    TraceCaptureWorkload.BurnCpuBurst
                      CPU_TIME
```

This does not add `BurnCpuAsync` as a suspended async ancestor or claim it
resumed independently. It simplifies an already captured synchronous call
into a child state machine. The resumption bridge remains intact.

**3. Machinery-cleanup result.** Fold the verified five-frame
timer/Task/awaiter resumption bridge:

```text
ThreadPoolWorkQueue.Dispatch
  [Async V1] V1TraceScenarios.ValueTaskLevel1Async [ordinal 14, state 1]
    [Async V1] V1TraceScenarios.ValueTaskLevel2Async [ordinal 15, state 1]
      [Async V1] V1TraceScenarios.ValueTaskLevel3Async [ordinal 16, state -1]
        V1TraceScenarios.BurnCpuAsync
          TraceCaptureWorkload.BurnCpuBurst
            CPU_TIME
```

As with V2, the shorter chart changes overhead attribution, not the sample's
weight or the evidence that `BurnCpuBurst` was executing.

### A V2 sampled wrapper with an indexed dispatch target

The unchanged V2 default result above is intentional. A second sample from
the same V2 capture shows structural reconstruction without a physical
application-body match:
PID 13060, thread 52316, time 4751.6051 ms, QPC 2471073049868,
export ordinal 6947, raw event index 6948.

Here the physical managed stack ends at `Continuation_Wrapper_1`, not inside
an application method. The index identifies `ParallelBranchAsync` as the
dispatch target, but there is no physical match proving that its body was
executing. Below the same unchanged worker prefix, the physical input is:

```text
Physical input (raw):
ThreadPoolWorkQueue.Dispatch
  TimerQueueTimer.Fire
    Task.DelayPromise.CompleteTimedOut
      Task.TrySetResult
        Task.RunContinuations
          RuntimeAsyncTask<Int64>.DispatchContinuations
            RuntimeAsyncTask<Int64>.InstrumentedDispatchContinuations
              Continuation_Wrapper_1
                UNMANAGED_CODE_TIME
```

**Async resume input (leaf to root).**

```text
ResumeRuntimeAsyncCallstack (QPC 2471073049395, dispatcher 0x2)
ContinuationIndex = 0
[0] 0x0 (sentinel; no method)
[1] V2TraceScenarios.ParallelBranchAsync
```

Wrapper slot 1 identifies `ParallelBranchAsync` in this recorded chain.
Unlike the first V2 example, the physical stack supplies no matching
application body. The same async-resume evidence can therefore support a
dispatch target without proving physical execution.

The three results for this sample are:

```text
Structural:
ThreadPoolWorkQueue.Dispatch
  TimerQueueTimer.Fire
    Task.DelayPromise.CompleteTimedOut
      Task.TrySetResult
        Task.RunContinuations
          [Async V2] V2TraceScenarios.ParallelBranchAsync
            UNMANAGED_CODE_TIME

Default:
ThreadPoolWorkQueue.Dispatch
  TimerQueueTimer.Fire
    Task.DelayPromise.CompleteTimedOut
      Task.TrySetResult
        Task.RunContinuations
          [Async V2] V2TraceScenarios.ParallelBranchAsync
            UNMANAGED_CODE_TIME

Machinery:
ThreadPoolWorkQueue.Dispatch
  [Async V2] V2TraceScenarios.ParallelBranchAsync
    UNMANAGED_CODE_TIME
```

Structural reconstruction uses the sampled wrapper to select the logical
**dispatch target**, then consumes that wrapper and the two
dispatch-continuation helpers. `ParallelBranchAsync` was not on the physical
stack. The default pass has nothing further to change here.

Optional cleanup verifies the complete four-frame resumption bridge and folds
it to the retained scheduler anchor, just as in the physically current
ValueTask examples. The resolved, structurally verified dispatch target is
sufficient for attribution; it is not evidence that the method's body was
executing. If the complete bridge cannot be verified, cleanup keeps the
candidate intact rather than removing just its Task completion pair.

The `[Async V2]` label alone cannot distinguish a dispatch target from a
physically current method. The probe's role metadata makes that distinction.
In none of these views should the shorter stack be read as proof that
`ParallelBranchAsync`'s body was doing the sampled work.

The remainder of this guide is the implementation and API reference for the
chain above.

Source paths below are relative to `src\TraceEvent`. Public API names are
distinguished from internal implementation components. The runtime's emission
code remains the authority for the captured wire format.

## 1. End-to-end architecture

The design separates **recorded evidence**, **structural reconstruction**, and
**presentation policy**:

```text
Instrumented runtime
  AsyncEvents batches + ordinary samples/stacks + method/module information
        |
        v
AsyncProfilerTraceEventParser
  manifest-framed decoding -> typed sink callbacks, not synthetic TraceEvents
        |
        v
AsyncProfilerComputer                       TraceLog code-address tables
  per-process metadata/manifests              ^
  per-thread nested activation builders      | register observed native IPs
  completions / wrapper resets / creates ----+
        |
        v
AsyncCallStacksIndex
  finalized intervals + interned frames + timelines + creation relationships
        |
        v
ETLX deferred region
  lazy load / query / release / reload, without replaying AsyncEvents
        |
        v
SampleProfilerThreadTimeComputer or ThreadTimeStackComputer
  event's physical stack + active intervals at the event's exact QPC
        |
        v
AsyncCpuStackSourceBuilder (internal)
  materialize/classify physical frames and canonicalize method identities
  -> AsyncCpuStackStitcher: mandatory structural reconstruction
  -> optional historical creation-parent ancestry
  -> AsyncStackTransformPipeline: enabled built-ins, then custom transforms
  -> intern frames, attach role metadata, preserve the host's root policy
        |
        v
MutableTraceEventStackSource
  existing consumers/exporters, including Speedscope
```

Index construction happens during ETL/EventPipe-to-ETLX conversion, even when
no consumer requests stitched stacks. Stitching is a separate opt-in at stack
generation time. These library APIs do not enable the runtime provider or
collect a trace; collection belongs to the producer and diagnostic tool.

An async interval says what continuation chain was active, not which of its
methods was physically executing. The physical stack, completion evidence,
and frame role are needed to distinguish execution, dispatch, awaiting,
completion, and historical ancestry.

### Terminology and ownership

| Object | Meaning |
|---|---|
| `AsyncCallstackKind` | V1 `StateMachineAsync` or V2 `RuntimeAsync` instrumentation, not a serialization version. |
| `AsyncThreadKey` | A unique trace `ProcessIndex` plus the OS thread ID from the batch header. A reusable OS PID is not the key. |
| `AsyncCallStack` | One resume interval on a thread, with dispatcher ID, nesting depth, frame-list reference, and progress timelines. The same dispatcher can have multiple activations. |
| `AsyncCallStackFrames` | An interned, leaf-first frame list. Both kinds carry native IPs; V1 also carries raw signed compiler states. |
| `AsyncDispatcherCreation` | Recorded child/parent dispatcher relationship at a creation QPC; not a complete synchronous call path. |
| `StitchedFrame` | One reconstructed frame, with physical/logical identity, origin, activation context, and presentation request. |
| `AsyncStackSourceFrameInfo` | Async identity and role retained on a generated stack-source frame, independently of its displayed name. |

Frame lists are leaf-to-root. Active-interval queries return activations in the
opposite direction: outermost-to-innermost, in ascending `Depth`.

The examples use the following namespaces as needed:

```csharp
using System;
using System.Collections.Generic;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;
using Microsoft.Diagnostics.Tracing.Stacks;
```

## 2. Versions and identities that must not be conflated

| Dimension | Current contract |
|---|---|
| Instrumentation family | V1 classic state machines versus V2 runtime-async continuations; both can occur in one process and one nested execution stack. |
| Outer batch layout | `AsyncProfilerBufferHeader`, currently version 1 and 37 bytes. |
| Logical-event schema | Per-event version and payload-length-prefix width advertised in `AsyncProfilerManifest`. |
| ETLX serialization | `TraceLog` currently requires ETLX version 78 for both reader and writer compatibility. |
| Async-index serialization | Internal `AsyncCallStacksIndex` serialization version, currently 1; not a separately promised interchange format. |
| Captured runtime profile | Implementation-dependent layout rules selected from captured CoreLib/module information, not the analyzer's runtime version. |

`MethodIdAt(i)` and `AsyncCallstackEvent.MethodIds[i]` are native code IPs for
**both** families. V2 captures continuation IPs; V1 captures state-machine
`MoveNext` body IPs. They are not CLR MethodLoad IDs or MethodDesc handles.
V1 state is neither an IL offset nor a native offset and must not be added to
an IP to obtain an address.

`CodeAddressIndex`, `MethodIndex`, `ThreadIndex`, and `ProcessIndex` belong to
their originating trace. `AsyncCallStackFramesIndex` belongs to its async
index; `StackSourceFrameIndex` belongs to its stack source. Neither integer
equality across traces nor equal displayed names establish identity.

The async sub-event and sample QPCs are used in the same captured trace's
clock domain. Joining them does not require conversion to UTC. Metadata clock
synchronization is for external/UTC correlation, not a substitute for exact
in-trace QPC matching.

## 3. Raw decoding and forward-compatible framing

**Implementation:** `Parsers\AsyncProfilerTraceEventParser.cs` and
`Parsers\AsyncProfilerTypes.cs`.

`AsyncProfilerTraceEventParser` is in
`Microsoft.Diagnostics.Tracing.Parsers`. Payload types and decoding helpers
are in `Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler`.

| Public API | Purpose |
|---|---|
| `new AsyncProfilerTraceEventParser(TraceEventSource source)` | Register the parser against a raw source or an ETLX event source. |
| `AsyncEvents` event, `Action<AsyncEventsTraceData>` | Observe the original batch. `Length` is the outer payload length; `Buffer` returns its byte array. |
| `ProviderName`, `ProviderGuid`, `AsyncEventsEventId` | Identify `System.Runtime.CompilerServices.AsyncProfilerEventSource`, event 1. |
| `ParseBuffer(byte[] buffer, IAsyncProfilerSubEventSink sink)` | Decode one buffer using fresh built-in manifest defaults. |
| `ParseBuffer(byte[] buffer, AsyncProfilerManifest manifest, IAsyncProfilerSubEventSink sink)` | Decode while retaining the process instance's manifest across buffers. |
| `AsyncProfilerBufferHeader.TryRead(...)` | Read the fixed header; full buffer/event-boundary validation belongs to `ParseBuffer`. |
| `AsyncProfilerManifest.GetVersion(...)`, `GetPayloadLengthFieldSize(...)`, `Apply(...)` | Inspect/update the live event-framing table. Unlisted entries retain their previous values. |
| `AsyncEventInfo` and `AsyncProfilerReader` | Default event classifications and fixed-width/LEB128/zigzag readers for low-level consumers. |

Each outer EventSource payload contains a length followed by a batch. Its
header identifies the emitting OS thread and the batch's QPC range. Logical
events carry a running timestamp delta, a manifest-defined payload-length
prefix, and their payload. Use the decoded logical-event timestamp, not the
batch's delivery time, for activation state.

The decoder retains advertised schema versions but does not have a different
decoder for every future version. It reads the known layout prefix, then
advances to the **declared payload end**. Compatible new schemas must append
fields rather than change/remove existing fields. Advertised unknown events
can be skipped using their manifest framing. An unknown payload's width cannot
be invented if its framing was never advertised.

Metadata is emitted per configuration revision, not necessarily in every
buffer. Direct decoder callers must retain one manifest per **process
instance**. The overload creating fresh defaults is appropriate for isolated
buffers, not arbitrary multi-buffer streams with manifest changes.

### Typed sink contract

`IAsyncProfilerSubEventSink` receives synchronous `in`-parameter callbacks:

| Callbacks | Payload and meaning |
|---|---|
| `OnContextCreate`, `OnContextResume`, `OnContextSuspend`, `OnContextComplete` | `AsyncContextEvent`: dispatcher lifecycle and, where encoded, parent/dispatcher IDs. |
| `OnCallstack` | `AsyncCallstackEvent`: kind, dispatcher, continuation index, IPs, and optional V1 states. |
| `OnMethodResume`, `OnMethodComplete` | `AsyncMethodEvent`: method-level progress in the current context. |
| `OnException` | `AsyncUnwindEvent`: exceptional completed-frame delta. |
| `OnResetThreadContext`, `OnResetContinuationWrapperIndex` | `AsyncNeutralEvent`: thread re-synchronization or wrapper progress. |
| `OnMetadata`, `OnSyncClock` | `AsyncMetadataEvent` / `AsyncSyncClockEvent`: manifest, wrapper count, and clock correlation. |
| `OnUnknown` | `AsyncUnknownEvent`: unknown event identity and payload length. |
| `OnParseError` | `AsyncProfilerParseError`: message, byte offset, and any known header, QPC, and event ID. |

Logical IDs 1-10 are V2, 11-19 are V1, and 20-23 are neutral. They are not
expanded into individually enumerable `TraceEvent` records. The raw
`AsyncEvents` batch is the event exposed in an event stream.

A malformed known payload reports an error and decoding can continue at its
declared end; header/framing failures stop that buffer. The decoder itself
does not know how to recover activation state.

The decoder exposes zero-frame cached-callstack references through `IsCached`,
but the current computer does not implement out-of-band cached-frame
resolution. A zero-frame record must not be assumed to reconstruct ancestry.

## 4. Building activation state

**Public type:** `Microsoft.Diagnostics.Tracing.Computers.AsyncProfilerComputer`.

| Public API | Contract |
|---|---|
| `new AsyncProfilerComputer()` | Standalone computer, driven by the caller. The parser-binding constructor used by TraceLog is internal. |
| `Process(byte[] buffer)` | Process one batch in the default process instance, index 0. Use only for a single logical process. |
| `Process(byte[] buffer, ProcessIndex processIndex)` | Keep manifests, metadata, recovery, and thread state isolated by process instance. |
| `Finish()` | Finalize still-open activations after the stream; call once after processing all buffers. |
| `Index`, `DistinctFramesCount`, `GetFrames(...)`, `GetAsyncCallStacks(AsyncThreadKey, long)` | Access/query finalized state. Open builders are not point-query results until committed. |
| `ParseError` event and `ParseErrorCount` | Observe decode failures and the affected process instance. |
| `OnFrameObserved` | `Action<ProcessIndex, ulong, AsyncCallstackKind>` invoked for nonzero IPs in accepted resume/append records, before final interning. |
| `ClockKnown`, `WrapperCount`, `QpcToDateTime(long)` | Clock/wrapper helpers for default process index 0. |
| `ClockKnownForProcess(...)`, `GetWrapperCount(...)`, `QpcToDateTime(ProcessIndex, long)` | Process-specific variants. UTC conversion returns null without usable clock metadata. |

For direct raw-source use, subscribe to `parser.AsyncEvents` and call
`computer.Process(data.Buffer, processIndexOf(data))`. The caller must supply
a stable process-instance mapping, not cast an OS PID to `ProcessIndex`.
Call `source.Process()` and then `computer.Finish()`. TraceLog performs this
integration automatically during conversion. Standalone buffer processing does
not automatically populate TraceLog code-address/symbol tables. Process
synchronously; do not share a building computer between concurrent processing
loops.

### State machine

| Input | Effect |
|---|---|
| Metadata | Update process clock/wrapper information and establish a completion-availability epoch. The parser has already applied its manifest. |
| Thread reset | Arm only when its QPC is at or after usable metadata's gate. Commit any open activations at the reset QPC, clear nesting, and re-arm that thread. |
| Resume callstack, V1 or V2 | Push a builder with dispatcher, continuation base, wrapper count, start QPC, kind, and depth. |
| V1 append callstack | Extend the top builder only when dispatcher and kind match. |
| Context suspend/complete | Pop, finalize, intern frames, and record `[StartQpc, EndQpc)`. |
| Method completion / exception unwind | Add separate normal/exception timelines to the active top builder. |
| Wrapper reset | Add progress to the active top builder, not all enclosing activations. |
| Context create | Record a creation relationship when the thread is armed; creation alone does not push an activation. |
| Context/method resume without a callstack | Does not create or replace frame data. |
| `Finish()` | Commit remaining open builders with `EndQpc = long.MaxValue`. |

Before a valid metadata/reset sequence, thread-scoped data cannot establish
trusted activation state. The QPC gate also excludes prior-session buffered
resets delivered after the current session's metadata. Delivery order alone
is not a sufficient gate.

Intervals overlap to preserve nesting; an outer interval is not split around
its children. V1 frames are assembled from resume plus append records and
finalized on close. Their captured states are not live per-sample state reads.
`Finish()` preserves observed live-at-end work; it cannot recover events the
runtime never flushed into the capture. Its open-ended interval is not proof
that the activation continued indefinitely beyond the capture.

### Recovery after decode failure

With a known thread and timestamp, the computer closes trustworthy open
prefixes at the error QPC and unarms that thread until a valid reset.
An unreadable header invalidates that process's open state without guessing an
end timestamp. Malformed metadata invalidates the process's threads, closing
known prefixes where possible. Unreadable headers and malformed metadata also
require fresh metadata before resets can re-arm the process. Finalized
intervals and other processes remain intact.

TraceLog writes bounded individual warnings and a failure-count summary to
`TraceLogOptions.ConversionLog`. Save that log: the public TraceLog query API
does not expose the building computer's `ParseErrorCount` afterward.

## 5. Interning, symbolization, persistence, and memory

**Implementation:** `AsyncCallStacksIndex`, its compact per-thread records,
and the async integration in `TraceLog.CopyRawEvents`.

Frame interning keys include process instance, instrumentation kind, IP
sequence, and V1 states. Identical method sequences with different captured
states are not interchangeable. Activations refer to the interned frame list
instead of duplicating it.

`OnFrameObserved` lets TraceLog register native IPs while processing the stream,
before later method loads, rundown, or process-symbol events resolve them.
TraceLog remembers `(ProcessIndex, IP) -> CodeAddressIndex`; finalization reuses
those indices even when a live activation is committed only after rundown.
Each frame persists its raw identity/state and a reference into the existing
TraceLog code-address table, not a separately copied method-name string.

`CodeAddressAt(i) != CodeAddressIndex.Invalid` means the address was registered,
**not** that a method/module was resolved. Method names and source information
remain TraceLog/symbol-reader responsibilities. A zero-IP sentinel can have no
registered address without representing a symbol-resolution failure.

The index persists frame lists, interval records, completion/reset timelines,
completion-availability epochs, and dispatcher creations. In-memory builders,
decoder scratch arrays, and point-query interval trees are not serialized.
Construction uses chunked compact records; loading allocates records from the
stored counts. Public activation objects are materialized when queried.
Each thread's start-sorted, max-end-augmented interval tree is built lazily.

Use `TraceLog.CreateFromEventPipeDataFile(...)` for EventPipe captures or
`TraceLog.CreateFromEventTraceLogFile(...)` for ETL conversion. Both accept
`TraceLogOptions` and return the ETLX path to open with `new TraceLog(path)`.
These conversion paths install the async parser/computer automatically;
ordinary TraceLog consumers do not instantiate a second computer to rebuild
the persisted index.

| Public TraceLog API/option | Meaning |
|---|---|
| `HasAsyncProfilerEvents` | Cheap event-presence check from persisted statistics; does not load the index. Metadata-only or malformed data can make it true. |
| `TraceLogOptions.KeepAsyncProfilerEvents` | Default false. Retain raw batches in ETLX for inspection or later re-decoding. Does not gate index construction. |
| `TraceLogOptions.KeepAllEvents` | Also preserves the raw batches. |
| Async query methods | Load the deferred index on demand. Missing usable intervals produce empty/null results. |
| `ReleaseAsyncCallStacks()` | Drop TraceLog's loaded index reference when reloadable ETLX backing exists; return true if released, otherwise false. A later query reloads it without replaying raw events. |

`TraceLog.AsyncCallStacks` and `IsAsyncCallStacksLoaded` are **internal**.
External callers use the public query methods, not access to that field.
Returned public activations remain valid across later queries and release;
retained results can keep backing data alive. Keep the originating TraceLog
open for code-address, symbol, or source lookup. Release does not unload its
code-address/symbol tables.

Persisted reconstruction is not rebuilt by changing presentation flags.
Regenerate ETLX when parser/index/symbol-binding behavior changes; regenerate
stack sources when only stitching/presentation policy changes. Do not assume
that an older development cache is compatible merely because its filename or
an unchanged internal version number looks familiar.

## 6. Public activation and relationship queries

The usual consumer entry point is
`Microsoft.Diagnostics.Tracing.Etlx.TraceLog`.

### Returned data objects

`AsyncCallStack` and `AsyncCallStackFrames` are produced by the computer/index,
not constructed directly by application callers.

| Public members | Meaning |
|---|---|
| `AsyncCallStack.Thread`, `DispatcherId`, `Depth` | Process/thread identity, dispatcher identity, and nesting depth for this activation. |
| `FramesIndex`, `Frames` | Index-scoped handle and the interned frame-list object. |
| `StartQpc`, `EndQpc` | Inclusive start and exclusive end in the trace QPC domain. |
| `ContinuationIndexBase`, `WrapperCount` | Wrapper position captured at resume and the activation's wrapper-pool size. |
| `AsyncCallStackFrames.Kind`, `FrameCount` | Instrumentation family and number of leaf-first frames. |
| `MethodIdAt(int)`, `FrameStateAt(int)`, `CodeAddressAt(int)` | Captured IP, captured V1 state, and registered TraceLog address reference. |

### Active intervals

The following are the current overloads (return types shown):

```csharp
IReadOnlyList<AsyncCallStack> GetAsyncCallStacks(TraceEvent anchorEvent);
IReadOnlyList<AsyncCallStack> GetAsyncCallStacks(ThreadIndex threadIndex, long timeQPC);
IReadOnlyList<AsyncCallStack> GetAsyncCallStacks(ThreadIndex threadIndex, double timeRelativeMSec);
IReadOnlyList<AsyncCallStack> GetAsyncCallStacks(int processId, ulong osThreadId, long timeQPC);
IReadOnlyList<AsyncCallStack> GetAsyncCallStacks(int processId, ulong osThreadId, double timeRelativeMSec);

AsyncCallStack GetAsyncCallStack(ThreadIndex threadIndex, ulong dispatcherId, long timeQPC);
AsyncCallStack GetAsyncCallStack(int processId, ulong osThreadId, ulong dispatcherId, long timeQPC);
```

Point queries use `StartQpc <= qpc && qpc < EndQpc`, with outermost activation
first. Singular queries return the innermost matching dispatcher or null.
No interval or no index is an empty/null result, not a fabricated stack.

Prefer the event-anchored overload when an exact sample event is available.
It uses the event's PID, OS thread ID, and QPC without requiring callers to
read the discouraged `TimeStampQPC` property. The event must belong to this
trace or its source capture; the query does not retain it.

PID-based overloads resolve the process instance **at the query QPC**. They
also reach async-only threads with no `TraceThread`/`ThreadIndex`.
Millisecond overloads convert through `RelativeMSecToQPC`; they require this
trace's relative time and can round at QPC-tick precision. There is no nearest
sample search. Use a `double` for milliseconds: an integer argument can select
the raw-QPC overload instead.

For example, inside enumeration of a sample event, with `log` and `sample`
from the same trace:

```csharp
IReadOnlyList<AsyncCallStack> active = log.GetAsyncCallStacks(sample);
foreach (AsyncCallStack activation in active)
{
    bool normalEventsObserved = log.AsyncMethodCompletionObserved(activation);
    for (int i = 0; i < activation.Frames.FrameCount; i++)
    {
        ulong ip = activation.Frames.MethodIdAt(i);
        CodeAddressIndex address = activation.Frames.CodeAddressAt(i);
        string name = address == CodeAddressIndex.Invalid
            ? "AsyncFrame(0x" + ip.ToString("x") + ")"
            : log.CodeAddresses[address].FullMethodName;
        Console.WriteLine(activation.Frames.Kind + ": " + name);
    }
    Console.WriteLine("Normal completion events observed: " + normalEventsObserved);
}
```

This prints captured frames, not the stitcher's inferred executing position.
Use the stitcher/host when execution-versus-ancestry attribution is required.

### Dispatcher relationships and completion availability

| Public TraceLog API | Result |
|---|---|
| `GetAsyncCallStackCreation(AsyncCallStack callStack)` | Latest recorded creation associated with the activation, or null. |
| `GetParentAsyncCallStack(AsyncCallStack child)` | Parent activation at the child's creation QPC, or null. |
| `TryGetParentAsyncCallStack(AsyncCallStack child, out AsyncCallStack parent, out long parentQpc)` | Resolve the parent and the historical creation timestamp used to query it. |
| `GetChildAsyncCallStackCreations(AsyncCallStack parent)` | Children created on the parent's thread during its resume interval. |
| `AsyncMethodCompletionObserved(AsyncCallStack callStack)` | Whether normal-completion events were observed for this activation's process, kind, and metadata/configuration epoch. Not proof every completion was captured. |

`AsyncDispatcherCreation` exposes `Thread`, `ChildDispatcherId`,
`ParentDispatcherId`, and `CreateQpc`. Missing create records or parent resume
intervals mean missing ancestry, not an inferred relationship.
Creation-parent ancestry is historical: it does not establish that the parent
was running at the child's sample time or recover intervening synchronous
frames.

### Direct index API

Callers owning `AsyncProfilerComputer.Index` can use
`AsyncCallStacksIndex.Threads`, `DistinctFramesCount`, `IsEmpty`,
`GetFrames(AsyncCallStackFramesIndex)`, point/singular queries keyed by
`AsyncThreadKey`, and `GetAsyncCallStacks(AsyncThreadKey)` for all finalized
runs in recording order. That last list is index-backed during construction,
not a frozen snapshot of its count.

Its relationship methods are `GetCreation` (process/dispatcher/QPC or
activation), `GetChildCreations` (process/parent ID or activation), and
`GetParentAsyncCallStack` / `TryGetParentAsyncCallStack`.
`MethodCompletionObserved(AsyncCallStack)` selects the activation's epoch.
Kind-only completion-observed overloads refer to default process index 0;
process/kind overloads summarize **any** epoch in that process. Those broad
summaries must not choose another activation's completion policy.
`ExceptionCompletionObserved` has kind-only and process/kind forms.

The index implements `IFastSerializable`. Public queries return stable
activation objects; the allocation-saving list/activation reuse overloads are
internal and have a different lifetime contract.

## 7. Completion counting is separate from recorded states

`AsyncCallStack` exposes:

| API | Meaning |
|---|---|
| `GetMethodCompletedFrameCount(long qpc)` | Sum of recorded normal-completion deltas at or before QPC. |
| `GetExceptionCompletedFrameCount(long qpc)` | Sum of recorded exceptional-unwind deltas. |
| `GetCompletedFrameCount(long qpc)` | Sum of those two recorded sources; no physical inference. |
| `GetWrapperResetCount(long qpc)` | Number of reset events in this activation through QPC. |
| `GetCompletedFrameCount(long qpc, int currentMethodIndex)` | Wrapper-derived progress: `max(0, resets * WrapperCount + currentMethodIndex - ContinuationIndexBase)`. The caller supplies the physical wrapper slot. |

The structural stitcher selects and bounds progress using evidence:

| Kind | Normal-completion events observed in the activation's epoch | Otherwise |
|---|---|---|
| V2 | Use recorded normal plus exceptional counts. | Use activation-relative wrapper progress; wrapper advancement already includes exceptions, so do not add unwind deltas again. |
| V1 | Use recorded normal plus exceptional counts and verify physical alignment. | Reconcile ordered physical `MoveNext`/approved box matches with exceptional deltas. Exceptionally completed methods can remain physically present while finishing; naive addition can double-count them. Ambiguous overlap retains the physical slice. |

Observation is scoped by process instance, instrumentation kind, and metadata
epoch, not a single trace-wide boolean. Observing one event is not evidence
that every event was collected. A sample can also land after logical method
completion but before the physical invocation returns.

`Frames.FrameStateAt(i)` always returns the captured signed V1 state (0 for
V2, where it is not meaningful). Displayed `-1` for a verified current V1
method or `-2` for verified completing/unwind work is an inference attached
during emission, not a mutation of captured data.

## 8. Structural stitching and its lower-level APIs

**Public types:** `AsyncCpuStackStitcher`, `StitchSyncFrame`, `StitchResult`,
`StitchedFrame`, `StitchDiagnostics`, `AsyncStitchBoundary`,
`AsyncStitchBoundaryCache`, and associated enums in
`Microsoft.Diagnostics.Tracing.Computers`.

The stitcher walks physical frames leaf-to-root and active segments
innermost-to-outermost in lockstep. It aligns method identities and verified
dispatch boundaries, preserves synchronous execution/callees, and replaces
dispatch plumbing with the appropriate suspended ancestry. It is not a
concatenation of the entire async stack beneath every physical sample.

V2 wrappers (`Continuation_Wrapper_0` through `_31`) supply the structural
splice boundary and wrapper position. Dispatch-continuation plumbing without
a wrapper is classified but is not itself permission to splice a V2 segment.
V1 uses `MoveNextAsDispatcher` or the type-qualified
`AsyncStateMachineDispatcher.MoveNext`, plus method/box evidence.
Classification and using a classification as a splice boundary are distinct.

A verified dispatcher/box/wrapper can establish an **indexed dispatch
target** without proving that the application's body is physically present.
For a sampled-leaf V2 wrapper with a known target, structural stitching
consumes the wrapper and emits `AsyncDispatchTarget`, not `AsyncCurrent`.
An unavailable target retains the conservative structural fallback; without
async data, the original physical wrapper remains untouched.
Unique ordered physical matches can recover truncated alignments; ambiguous
recursive or repeated matches are not guessed. Verified unavailable roots are
represented as `BROKEN`, not invented thread-pool/framework callers. Its
export-visible marker is an ordinary interned frame named `BROKEN`, because
exporters filter the reserved `StackSourceFrameIndex.Broken` value.

Missing boundaries do not always preclude other reconstruction: independent
enclosing activations can still match, while uncertain slices remain.
Empty/missing segment data preserves the supplied physical stack. Production
hosts also bypass the builder entirely without an active interval or physical
stack, so coverage gaps remain visible.

### Public `Stitch` overloads

```csharp
StitchResult AsyncCpuStackStitcher.Stitch(
    IReadOnlyList<StitchSyncFrame> syncLeafToRoot,
    IReadOnlyList<AsyncCallStack> segmentsRootToLeaf,
    long qpc,
    AsyncStitchBoundaryCache boundaries,
    AsyncCallStacksIndex index,
    ProcessIndex processIndex,
    Func<CodeAddressIndex, MethodIndex> methodOf,
    bool trace = false);

StitchResult AsyncCpuStackStitcher.Stitch(
    IReadOnlyList<StitchSyncFrame> syncLeafToRoot,
    IReadOnlyList<AsyncCallStack> segmentsRootToLeaf,
    long qpc,
    Func<CodeAddressIndex, AsyncStitchBoundaryInfo> classify,
    Func<AsyncCallstackKind, bool> methodCompletionObserved,
    Func<CodeAddressIndex, MethodIndex> methodOf,
    bool trace = false);
```

These allocate output and do not mutate the input lists. The index/cache
overload selects completion availability per activation epoch; the delegate
overload is useful for controlled integrations/tests, but its kind-only
predicate cannot itself distinguish mixed configuration epochs.
Callers must provide compatible trace-owned addresses and method identities.
The public core does not perform symbol lookup, intern stack-source frames,
or automatically run presentation transforms. The normal host computers
prepare richer physical metadata, canonical method identities, and runtime
ownership, so they are the recommended application-facing integration.

`StitchSyncFrame` contains `CodeAddress`, `Method`, optional
`StitchSyncFrameKind`, and optional `BoxedStateMachineMethod`. Approved box
identity associates a physical runtime helper with its application state
machine; it does not turn that helper IP into evidence of body execution.
`StitchResult` contains leaf-first `Frames` and `Diagnostics`.

`AsyncStitchBoundary.Classify(...)`, `TryGetWrapperIndex(...)`, and
`ClassifySynchronousFrame(...)` expose name/classification helpers;
`GetBareMethodName`, `GetDeclaringTypeName`, and
`TryGetLogicalStateMachineMethodName` support generated-name interpretation.
Metadata-free classifiers use common rules. A per-trace
`new AsyncStitchBoundaryCache(log.CodeAddresses)` classifies code-address or
method indices using captured CoreLib ownership, caching the result;
`BoundaryMethodCount` is a diagnostic count, not a sample-match count.

## 9. Frame provenance and presentation policy

`StitchedFrame` carries `Origin`, `CodeAddress`, canonical physical `Method`,
optional `BoxedStateMachineMethod`, `Segment` and `SegmentFrameIndex`,
`SyncFrameKind`, `Presentation`, and `Context`. `ContextKind` distinguishes
active and historical contexts. Factory methods (`CreateSync`,
`CreateAsyncCurrent`, `CreateAsync`, `CreateAsyncDispatchTarget`,
`CreateAsyncCompleting`, `CreateAsyncCompletionUnwind`,
`CreateAsyncContextParent`, `CreateIncompleteRoot`) preserve those
distinctions; `WithPresentation(...)` changes presentation without changing
structural identity.

| `StitchedFrameOrigin` | Interpretation |
|---|---|
| `Sync` | Retained physical frame; it can still belong to an active context. |
| `AsyncCurrent` | Physically matched current async method, retaining the sampled address. |
| `AsyncRemaining` | Captured suspended continuation ancestry, not physical execution. |
| `AsyncDispatchTarget` | Indexed target, possibly represented by a physical V1 box carrier; no body-execution claim. |
| `AsyncCompleting` | Physically present V1 method established as logically completed, still finishing its invocation. |
| `AsyncCompletionUnwind` | Verified fully completed V1 return work attributed to the logical root; no claim the root body is physically executing. |
| `AsyncContextParent` | Historical creation-parent ancestry. |
| `IncompleteRoot` | Unavailable physical root, without an async-method or execution-host identity. |

### Pipeline configuration and ordering

`AsyncStackTransformPipeline` is public. Configure the host's
`AsyncStackTransforms` **before generation**.

| Property | Default | Effect |
|---|---|---|
| `EnableConservativeTransforms` | true | Strong-signal default presentation normalization; not a switch for structural reconstruction. |
| `EnableAsyncInfrastructureCleanup` | false | Additional ownership-checked attribution/removal of runtime implementation machinery. |
| `EnableContextAncestry` | false | Host inserts missing recorded historical parents before presentation. |
| `MaximumContextAncestryDepth` | 64 | Positive maximum inserted-parent depth per active segment. |
| `EnableActiveAsyncFrameAnnotations` | false | Host emitter labels active frames `[Async V1]` / `[Async V2]` and shows V1 presentation states where names can be decoded. |

Enabled stages execute in this order:

1. Mandatory structural stitching, including known-target V2 leaf-wrapper
   consumption and incomplete-root handling.
2. Optional historical creation-parent insertion, with cycle/depth checks.
3. Conservative sampled-leaf V2 wrapper presentation for retained fallback
   or caller-assembled layouts.
4. Conservative verified inter-activation handoff folding.
5. Conservative V1 startup/reentrant normalization, completed-return
   attribution, and verified completion-handoff normalization.
6. Optional async-infrastructure cleanup.
7. Custom transforms, in registration order.
8. Host frame interning, annotations, role metadata, and historical-context
   transition markers.

Thus a "structural" view disables both conservative transforms and cleanup,
not structural boundary replacement. The validation helper also disables
historical ancestry to keep its four comparison views focused.

Default normalization does not assert that every helper with a familiar name
can be removed. For example, full V1 completion-unwind attribution requires
recorded full completion plus a verified, uniquely ordered physical return
span. It emits a logical-root unwind role with displayed state `-2`; merely
seeing a completed count is not sufficient to fold an arbitrary stack.

Infrastructure cleanup is an additional attribution policy, not general
framework filtering. Verified same-runtime resumption/completion bridges can
be folded atomically, retaining their scheduler anchor. V2 allows either a
physically matched current method or a resolved, structurally verified
dispatch target. An interrupted, unresolved, foreign, or otherwise unverified
candidate stays intact, including its Task completion chain; independent
cleanup can still run outside that protected span. For example, an application
callback between `ThreadPoolWorkQueue.Dispatch` and `TaskCompletionSource.SetResult`
prevents the complete bridge fold, so its `SetResult`, `TrySetResult`, and
`RunContinuations` frames remain together.

A verified profiler `Resume` or dispatcher-registration
spine can establish ownership of its entire callee prefix, including unresolved
callees, GC, initialization, or callbacks. That work can then be charged to a
logical async frame without claiming the suspended method executed it.
Residual dispatcher spans can become `[Async dispatcher frames]`.

Historical parents remain explicitly labeled `[Async parent V1]` /
`[Async parent V2]`; crossing those contexts can emit
`[Async parent context transition]`. They do not become current execution
because their names appear in the chart.

### Custom transform API and lifetime

`Add(AsyncStackTransform)` registers a callback;
`ClearCustomTransforms()` removes custom callbacks without changing built-in
flags. The delegate receives `AsyncStackTransformContext` with:

| Member | Use |
|---|---|
| `TraceLog` | Owning trace; can be null in standalone transforms. |
| `TimestampQpc` | Exact anchor-event QPC. |
| `Segments` | Active intervals, outermost first. |
| `Frames` | Mutable leaf-first `List<StitchedFrame>` after enabled built-ins. |
| `ClassifyBoundary(CodeAddressIndex)` | Classification consistent with this trace. |
| `Diagnostics` | Per-sample counters accumulated into the host's aggregate. |

During host callbacks the context, segment list, activation slots, frame list,
and diagnostics are reused. Snapshot primitive identities, roles, timestamps,
and frame data synchronously. Retaining `StitchedFrame` alone is insufficient:
its `Context` can reference a reused activation. These lifetimes differ from
the stable objects returned by **public** TraceLog/index queries.

Standalone
`pipeline.Transform(structuralResult, segments, qpc, classifyBoundary, traceLog)`
copies the result's frame list, applies presentation, and returns a new result.
The optional trace defaults to null; trace-dependent rules retain work when
required evidence is absent. Diagnostics are shared with the original result,
not cloned. This API does not insert historical ancestry or emit annotations.

### Runtime-specific rules are internal, not a public plug-in API

`AsyncRuntimeRuleProfile`, `AsyncRuntimeRuleProfileCache`, and sequence
matchers centralize implementation-specific helper recognition, ownership
evidence, layout matching, and complete presentation-stage replacements.
They are selected from **captured** CoreLib versions and loaded engine modules,
never the analysis machine's runtime or an application's target framework.
Selection happens before structural removal can erase runtime evidence.

The current explicit profile is CoreCLR 11.0; it presently shares common
implementations. Missing, conflicting, or unsupported metadata selects the
evidence-checked `Common` rules, not an arbitrary "latest runtime." Sequence
matching still checks frame identity, boundaries, same-module ownership, and
context. A version match alone never authorizes removal.
Compiler-generated application-name decoding is a separate convention; the
captured runtime version does not identify the application's compiler.
External presentation extensions use `Add(...)`; adding a runtime profile is
a library implementation change with regression coverage.

## 10. Stack-source hosts, emitted metadata, and diagnostics

Both host types expose constructors `(TraceLog, SymbolReader)` and
`(TraceLog, SymbolReader, bool stitchAsyncCallStacks)`. The two-argument
constructor leaves async stitching disabled.

| Public host | Intended source |
|---|---|
| `SampleProfilerThreadTimeComputer` | SampleProfiler/EventPipe thread-time projection. |
| `ThreadTimeStackComputer` | ETW/universal CPU and wait-stack projection, preserving configured activity/request roots. |

Both expose `AsyncStackTransforms`, `TraceAsyncStitchSteps`,
`AsyncStitchActive`, `AsyncStitchDiagnostics`, and
`GenerateThreadTimeStacks(MutableTraceEventStackSource, TraceEvents = null)`.
`AsyncStitchActive` becomes meaningful after initialization during generation:
it means stitching was requested and a usable index exists, not that a
particular sample matched. `HasAsyncProfilerEvents`, `AsyncStitchActive`, and
per-sample successful attribution are three different facts.

For example, with an open `TraceLog log` and `SymbolReader symbols`:

```csharp
var output = new MutableTraceEventStackSource(log)
{
    OnlyManagedCodeStacks = true
};
var computer = new SampleProfilerThreadTimeComputer(
    log, symbols, stitchAsyncCallStacks: true);
computer.AsyncStackTransforms.EnableActiveAsyncFrameAnnotations = true;
computer.AsyncStackTransforms.EnableAsyncInfrastructureCleanup = false;
computer.TraceAsyncStitchSteps = true;
computer.GenerateThreadTimeStacks(output);

if (!computer.AsyncStitchActive)
{
    throw new InvalidOperationException("No usable async index initialized stitching.");
}
StitchDiagnostics diagnostics = computer.AsyncStitchDiagnostics;
```

The explicit failure is a caller policy for a run that requires async data;
the library otherwise keeps the existing non-async generation path.
`SampleProfilerThreadTimeComputer` permits only one generation per instance;
use new computers/output sources for comparison views. Its defaults include
`UseTasks = true`, `GroupByStartStopActivity = true`, and
`IncludeEventSourceEvents = true`. Set and record host options explicitly when
comparing with another tool.

Without an active async interval or usable physical stack, the existing host
path is retained, including physical wrappers/dispatchers. Eligible samples
bypass the activity/TPL callstack reconstruction but keep the host's synthetic
thread/process/activity/request roots. Stitching does not replace the host's
metric accounting. Generated samples may represent CPU, blocked, await, or
other event-derived work depending on host configuration; they are not
necessarily one-to-one with raw profiler events.

Root consistency matters to exporters and investigations: activity grouping
name-interns thread roots, whereas `GetCallStackForThread` encodes TraceLog
thread frames. Equal displayed thread names need not share frame indices.
Do not correlate raw events and emitted records by one root-frame index or
timestamp alone. The validation probe preserves timestamp candidates and
original export ordinals rather than claiming a unique match.

### Emitted frame identity

`TraceEventStackSource.TryGetAsyncFrameInfo(StackSourceFrameIndex, out
AsyncStackSourceFrameInfo)` is virtual; `MutableTraceEventStackSource`
provides metadata for generated async frames. Ordinary sync and pseudo
frames return false.

`AsyncStackSourceFrameInfo` exposes `Kind`, `ProcessIndex`, `CodeAddress`,
`MethodId`, captured `State`, and the flags `IsHistoricalParent`,
`IsDispatchTarget`, `IsCurrent`, `IsStateMachineBoxCarrier`, `IsCompleting`,
and `IsCompletionUnwind`. Display strings are not a substitute for these
roles. Distinct frame identities can deliberately have identical names.

`GetFrameCodeAddress(StackSourceFrameIndex)` also returns generated async
addresses. Current/completing frames retain sampled addresses; a box carrier's
sampled address belongs to its runtime helper while `MethodId` identifies the
logical target. A completion-unwind frame uses logical-root attribution,
not a sampled root-body address. Preserve both identities when investigating.
Speedscope does not carry this full API metadata; keep the helper's sidecar
evidence with an export.

`StitchDiagnostics` includes public counter fields plus bounded `Messages`
and `MessagesDropped`. Examples include `SegmentsProcessed`,
`BoundariesNotFound`, `V2WrapperFallbackUsed`, `V1InlineFallbackUsed`,
`V1CompletionAlignmentAmbiguous`, `IncompleteAsyncRootsNormalized`, and
presentation-collapse counters. Nonzero values can describe expected fallback
or normal processing, not just failures. `TraceAsyncStitchSteps` adds detailed
per-segment notes; message storage is bounded, not a complete event log.

## 11. Source and regression map

| Responsibility | Implementation | Principal regression coverage |
|---|---|---|
| Framing/typed decoding | `Parsers\AsyncProfilerTraceEventParser.cs`, `Parsers\AsyncProfilerTypes.cs` | `TraceEvent.Tests\Parsing\AsyncProfilerParsing.cs` |
| Stateful construction and recovery | `Computers\AsyncProfilerComputer.cs` | `AsyncProfilerComputerTests.cs` |
| Compact storage, epochs, interval/creation queries | `Computers\AsyncCallStacksIndex.cs` | `AsyncProfilerComputerTests.cs`, `AsyncProfilerTraceLogTests.cs` |
| Conversion, address registration, deferred region, public facade | `TraceLog.cs` | `AsyncProfilerTraceLogTests.cs` |
| Structural reconstruction and boundary contracts | `Computers\AsyncCpuStackStitcher.cs`, `Computers\AsyncStitchBoundary.cs` | `AsyncCpuStackStitcherTests.cs`, `AsyncStitchBoundaryTests.cs` |
| Runtime ownership and implementation layouts | `Computers\AsyncRuntimeRuleProfile.cs`, `Computers\AsyncRuntimeRuleProfileCache.cs`, `Computers\AsyncRuntimeSequenceMatch.cs` | `AsyncRuntimeRuleProfileTests.cs`, `AsyncRuntimeSequenceTests.cs`, `AsyncRuntimeProfileExtensibilityTests.cs` |
| Historical creation ancestry | `Computers\AsyncContextAncestryAugmenter.cs` | `AsyncProfilerStitchIntegrationTests.cs` |
| Presentation and callbacks | `Computers\AsyncStackTransformPipeline.cs`, `Computers\AsyncStackTransforms.cs` | `AsyncStackTransformPipelineTests.cs` |
| Projection and emitted roles | `Computers\AsyncCpuStackSourceBuilder.cs`, `TraceEventStacks.cs`, `Computers\SampleProfilerThreadTimeComputer.cs`, `Computers\ThreadTimeComputer.cs` | `AsyncProfilerStitchIntegrationTests.cs` |
| Exported frame visibility | `Stacks\StackSourceWriterHelper.cs`, `Stacks\SpeedScopeStackSourceWriter.cs` | `TraceEvent.Tests\Speedscope\SpeedScopeExporterTests.cs` |

Unless fully qualified, test filenames in this table are under
`TraceEvent.Tests\Computers`. The integration tests exercise real conversion,
ETLX reopen, and the production computers using synthetic EventPipe captures;
core tests isolate boundary/progress/presentation decisions.

For the reproducible external scenario loop, exact run commands, four-view
settings, and artifact/probe interpretation, return to the
[validation README](README.md). When changing a layer, update its focused
regressions and this contract description rather than relying only on a
visually plausible chart.
