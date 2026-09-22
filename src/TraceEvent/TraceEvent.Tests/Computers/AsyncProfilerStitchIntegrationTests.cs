// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;
using Microsoft.Diagnostics.Tracing.Stacks;

using Xunit;

namespace TraceEventTests
{
    /// <summary>
    /// Production-path async stitching tests. Each scenario declares only its physical sync stack, profiler async
    /// stack, and expected stitched stack (all leaf to root). The shared fixture turns those declarations into a
    /// complete synthetic EventPipe trace with sample-profiler events, StackBlocks, async-profiler buffers, symbols,
    /// and process/thread records, then runs <see cref="SampleProfilerThreadTimeComputer.GenerateThreadTimeStacks"/>.
    /// </summary>
    public class AsyncProfilerStitchIntegrationTests
    {
        [Fact]
        public void RuntimeAsync_ThreadPoolDispatch_StitchesSuspendedAncestry()
        {
            var scenario = new StitchScenario
            {
                Sync = new[]
                {
                    Frame.App("Scenario.DoWork"),
                    Frame.App("Scenario.Level3Async"),
                    Frame.V2Wrapper(0),
                    Frame.V2InstrumentedDispatch,
                    Frame.V2Dispatch,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                Async = new[]
                {
                    Frame.App("Scenario.Level3Async"),
                    Frame.App("Scenario.Level2Async"),
                    Frame.App("Scenario.Level1Async"),
                    Frame.App("Program.Main"),
                },
                ExpectedStitched = new[]
                {
                    Frame.App("Scenario.DoWork"),
                    Frame.App("Scenario.Level3Async"),
                    Frame.App("Scenario.Level2Async"),
                    Frame.App("Scenario.Level1Async"),
                    Frame.App("Program.Main"),
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                ExpectedDiagnostics = new StitchDiagnosticsExpectation
                {
                    // Both incoming CPU samples are stitched; the second sample causes the first sample's
                    // thread-time interval to be emitted.
                    SegmentsProcessed = 2,
                    V2PlumbingFramesCollapsed = 4,
                },
                ReleaseAsyncCallStacksAfterGeneration = true,
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void RuntimeAsync_DeepResume_StitchesRealCapturedAncestry()
        {
            Frame readConsoleInput = Frame.Library(
                "System.Console", "Interop+Kernel32.ReadConsoleInput", "Interop.Kernel32.ReadConsoleInput");
            Frame consolePalReadKey = Frame.Library(
                "System.Console", "System.ConsolePal.ReadKey", "ConsolePal.ReadKey");
            Frame consoleReadKey = Frame.Library(
                "System.Console", "System.Console.ReadKey", "Console.ReadKey");
            Frame level4 = Frame.App("TestCase8.Level4Async");
            Frame level3 = Frame.App("TestCase8.Level3Async");
            Frame level2 = Frame.App("TestCase8.Level2Async");
            Frame level1 = Frame.App("TestCase8.Level1Async");
            Frame run = Frame.App("TestCase8.Run");
            Frame main = Frame.App("Program.Main");
            Frame workerDoWork = Frame.CoreLib(
                "System.Threading.PortableThreadPool+WorkerThread.WorkerDoWork", "WorkerThread.WorkerDoWork");
            Frame threadStartCallback = Frame.CoreLib(
                "System.Threading.Thread.StartCallback", "Thread.StartCallback");

            var scenario = new StitchScenario
            {
                Sync = new[]
                {
                    readConsoleInput,
                    consolePalReadKey,
                    consoleReadKey,
                    level4,
                    Frame.V2Wrapper(0),
                    Frame.V2InstrumentedDispatch,
                    Frame.V2Dispatch,
                    Frame.ThreadPoolDispatch,
                    workerDoWork,
                    Frame.WorkerThreadStart,
                    threadStartCallback,
                },
                Async = new[]
                {
                    level4,
                    level3,
                    level2,
                    level1,
                    run,
                    main,
                },
                ContinuationIndexBase = 0,
                ExpectedStitched = new[]
                {
                    readConsoleInput,
                    consolePalReadKey,
                    consoleReadKey,
                    level4,
                    level3,
                    level2,
                    level1,
                    run,
                    main,
                    Frame.ThreadPoolDispatch,
                    workerDoWork,
                    Frame.WorkerThreadStart,
                    threadStartCallback,
                },
                ExpectedDiagnostics = new StitchDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V2PlumbingFramesCollapsed = 4,
                },
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void RuntimeAsync_DeepCompletion_UsesWrapperSlotToExcludeCompletedFrames()
        {
            Frame readConsoleInput = Frame.Library(
                "System.Console", "Interop+Kernel32.ReadConsoleInput", "Interop.Kernel32.ReadConsoleInput");
            Frame consolePalReadKey = Frame.Library(
                "System.Console", "System.ConsolePal.ReadKey", "ConsolePal.ReadKey");
            Frame consoleReadKey = Frame.Library(
                "System.Console", "System.Console.ReadKey", "Console.ReadKey");
            Frame level4 = Frame.App("TestCase8.Level4Async");
            Frame level3 = Frame.App("TestCase8.Level3Async");
            Frame level2 = Frame.App("TestCase8.Level2Async");
            Frame level1 = Frame.App("TestCase8.Level1Async");
            Frame run = Frame.App("TestCase8.Run");
            Frame main = Frame.App("Program.Main");
            Frame workerDoWork = Frame.CoreLib(
                "System.Threading.PortableThreadPool+WorkerThread.WorkerDoWork", "WorkerThread.WorkerDoWork");
            Frame threadStartCallback = Frame.CoreLib(
                "System.Threading.Thread.StartCallback", "Thread.StartCallback");

            var scenario = new StitchScenario
            {
                Sync = new[]
                {
                    readConsoleInput,
                    consolePalReadKey,
                    consoleReadKey,
                    level1,
                    Frame.V2Wrapper(3),
                    Frame.V2InstrumentedDispatch,
                    Frame.V2Dispatch,
                    Frame.ThreadPoolDispatch,
                    workerDoWork,
                    Frame.WorkerThreadStart,
                    threadStartCallback,
                },
                Async = new[]
                {
                    level4,
                    level3,
                    level2,
                    level1,
                    run,
                    main,
                },
                ContinuationIndexBase = 0,
                ExpectedStitched = new[]
                {
                    readConsoleInput,
                    consolePalReadKey,
                    consoleReadKey,
                    level1,
                    run,
                    main,
                    Frame.ThreadPoolDispatch,
                    workerDoWork,
                    Frame.WorkerThreadStart,
                    threadStartCallback,
                },
                ExpectedDiagnostics = new StitchDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V2PlumbingFramesCollapsed = 4,
                },
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void RuntimeAsync_NoActiveSegment_PreservesPhysicalStackVerbatim()
        {
            var scenario = new StitchScenario
            {
                Sync = new[]
                {
                    Frame.App("Scenario.DoWork"),
                    Frame.App("Scenario.Level3Async"),
                    Frame.V2Wrapper(0),
                    Frame.V2InstrumentedDispatch,
                    Frame.V2Dispatch,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                Async = new[]
                {
                    Frame.App("Scenario.Level3Async"),
                    Frame.App("Scenario.Level2Async"),
                    Frame.App("Scenario.Level1Async"),
                    Frame.App("Program.Main"),
                },
                ExpectedStitched = new[]
                {
                    Frame.App("Scenario.DoWork"),
                    Frame.App("Scenario.Level3Async"),
                    Frame.V2Wrapper(0),
                    Frame.V2InstrumentedDispatch,
                    Frame.V2Dispatch,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                AsyncStartQpc = 100_030,
                AsyncEndQpc = 100_040,
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void RuntimeAsync_MissingNativeStack_FallsBackWithoutStitching()
        {
            var scenario = new StitchScenario
            {
                Sync = Array.Empty<Frame>(),
                Async = new[]
                {
                    Frame.App("Scenario.Level3Async"),
                    Frame.App("Scenario.Level2Async"),
                    Frame.App("Scenario.Level1Async"),
                    Frame.App("Program.Main"),
                },
                ExpectedStitched = Array.Empty<Frame>(),
                SampleHasStack = false,
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void RuntimeAsync_WrapperIsSampledLeaf_DropsOnlyWrapper()
        {
            var scenario = new StitchScenario
            {
                Sync = new[]
                {
                    Frame.V2Wrapper(0),
                    Frame.V2InstrumentedDispatch,
                    Frame.V2Dispatch,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                Async = new[]
                {
                    Frame.App("Scenario.Level3Async"),
                    Frame.App("Scenario.Level2Async"),
                    Frame.App("Program.Main"),
                },
                ExpectedStitched = new[]
                {
                    Frame.V2InstrumentedDispatch,
                    Frame.V2Dispatch,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                ExpectedDiagnostics = new StitchDiagnosticsExpectation
                {
                    V2SyncLayoutUsed = 2,
                    V2LeafWrapperDropped = 2,
                },
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void RuntimeAsync_DispatchPlumbingWithoutWrapper_PreservesPhysicalStack()
        {
            var scenario = new StitchScenario
            {
                Sync = new[]
                {
                    Frame.V2InstrumentedDispatch,
                    Frame.V2Dispatch,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                Async = new[]
                {
                    Frame.App("Scenario.Level3Async"),
                    Frame.App("Scenario.Level2Async"),
                    Frame.App("Program.Main"),
                },
                ExpectedStitched = new[]
                {
                    Frame.V2InstrumentedDispatch,
                    Frame.V2Dispatch,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                ExpectedDiagnostics = new StitchDiagnosticsExpectation
                {
                    V2SyncLayoutUsed = 2,
                },
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void RuntimeAsync_OtherProcessHasActiveSegment_DoesNotStitchSample()
        {
            var scenario = new StitchScenario
            {
                Sync = new[]
                {
                    Frame.App("Scenario.DoWork"),
                    Frame.App("Scenario.Level3Async"),
                    Frame.V2Wrapper(0),
                    Frame.V2InstrumentedDispatch,
                    Frame.V2Dispatch,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                Async = new[]
                {
                    Frame.App("Scenario.Level3Async"),
                    Frame.App("Scenario.Level2Async"),
                    Frame.App("Program.Main"),
                },
                ExpectedStitched = new[]
                {
                    Frame.App("Scenario.DoWork"),
                    Frame.App("Scenario.Level3Async"),
                    Frame.V2Wrapper(0),
                    Frame.V2InstrumentedDispatch,
                    Frame.V2Dispatch,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                AsyncStartQpc = 100_030,
                AsyncEndQpc = 100_040,
                AddForeignProcessActiveSegment = true,
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void RuntimeAsync_Scenario3Resume_RemovesSchedulingFrames()
        {
            Frame readConsoleInput = Frame.Library("System.Console", "Interop+Kernel32.ReadConsoleInput", "Interop.Kernel32.ReadConsoleInput");
            Frame consolePalReadKey = Frame.Library("System.Console", "System.ConsolePal.ReadKey", "ConsolePal.ReadKey");
            Frame consoleReadKey = Frame.Library("System.Console", "System.Console.ReadKey", "Console.ReadKey");
            Frame asyncFoo = Frame.App("TestCase3.AsyncFoo");
            Frame runContinuations = Frame.CoreLib("System.Threading.Tasks.Task.RunContinuations", "Task.RunContinuations");
            Frame trySetResult = Frame.CoreLib("System.Threading.Tasks.Task.TrySetResult", "Task.TrySetResult");
            Frame completeTimedOut = Frame.CoreLib(
                "System.Threading.Tasks.Task+DelayPromise.CompleteTimedOut", "DelayPromise.CompleteTimedOut");
            Frame timerFire = Frame.CoreLib("System.Threading.TimerQueueTimer.Fire", "TimerQueueTimer.Fire");
            Frame workerDoWork = Frame.CoreLib(
                "System.Threading.PortableThreadPool+WorkerThread.WorkerDoWork", "WorkerThread.WorkerDoWork");
            Frame threadStartCallback = Frame.CoreLib("System.Threading.Thread.StartCallback", "Thread.StartCallback");

            var scenario = new StitchScenario
            {
                Sync = new[]
                {
                    readConsoleInput,
                    consolePalReadKey,
                    consoleReadKey,
                    asyncFoo,
                    Frame.V2Wrapper(1),
                    Frame.V2InstrumentedDispatch,
                    Frame.V2Dispatch,
                    runContinuations,
                    trySetResult,
                    completeTimedOut,
                    timerFire,
                    Frame.ThreadPoolDispatch,
                    workerDoWork,
                    Frame.WorkerThreadStart,
                    threadStartCallback,
                },
                Async = new[]
                {
                    Frame.Zero,
                    asyncFoo,
                },
                ExpectedStitched = new[]
                {
                    readConsoleInput,
                    consolePalReadKey,
                    consoleReadKey,
                    asyncFoo,
                    runContinuations,
                    trySetResult,
                    completeTimedOut,
                    timerFire,
                    Frame.ThreadPoolDispatch,
                    workerDoWork,
                    Frame.WorkerThreadStart,
                    threadStartCallback,
                },
                FilteredOut = new[] { runContinuations, trySetResult, completeTimedOut, timerFire },
                ContinuationIndexBase = 0,
                ExpectedDiagnostics = new StitchDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V2PlumbingFramesCollapsed = 4,
                },
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void StateMachineAsync_Scenario3Resume_RemovesSchedulingFrames()
        {
            // Captured from AsyncProfilerScenarios scenario 3 after AsyncFoo resumes from Task.Delay and blocks in
            // Console.ReadKey. The profiler segment contains only AsyncFoo, so there is no suspended ancestry to add.
            Frame asyncFoo = Frame.App("TestCase3+<AsyncFoo>d__3.MoveNext");
            Frame logicalAsyncFoo = asyncFoo.Logical("TestCase3.AsyncFoo");
            Frame[] sync =
            {
                Frame.Library("System.Console", "Interop+Kernel32.ReadConsoleInput", "Interop.Kernel32.ReadConsoleInput"),
                Frame.Library("System.Console", "System.ConsolePal.ReadKey", "ConsolePal.ReadKey"),
                Frame.Library("System.Console", "System.Console.ReadKey", "Console.ReadKey"),
                asyncFoo,
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox.ExecutionContextCallback",
                    "AsyncStateMachineBox.ExecutionContextCallback"),
                Frame.CoreLib("System.Threading.ExecutionContext.RunInternal", "ExecutionContext.RunInternal"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox.MoveNext(Thread,Flags)",
                    "AsyncStateMachineBox.MoveNext(Thread,Flags)"),
                Frame.V1MoveNextAsDispatcher,
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncProfilerAsyncStateMachineBox.InstrumentedMoveNext",
                    "AsyncProfilerAsyncStateMachineBox.InstrumentedMoveNext"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox.MoveNext(Thread)",
                    "AsyncStateMachineBox.MoveNext(Thread)"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox.MoveNext()",
                    "AsyncStateMachineBox.MoveNext()"),
                Frame.CoreLib("System.Threading.Tasks.AwaitTaskContinuation.RunOrScheduleAction",
                    "AwaitTaskContinuation.RunOrScheduleAction"),
                Frame.CoreLib("System.Threading.Tasks.Task.RunContinuations", "Task.RunContinuations"),
                Frame.CoreLib("System.Threading.Tasks.Task.TrySetResult", "Task.TrySetResult"),
                Frame.CoreLib("System.Threading.Tasks.Task+DelayPromise.CompleteTimedOut", "DelayPromise.CompleteTimedOut"),
                Frame.CoreLib("System.Threading.TimerQueueTimer.Fire", "TimerQueueTimer.Fire"),
                Frame.ThreadPoolDispatch,
                Frame.CoreLib("System.Threading.PortableThreadPool+WorkerThread.WorkerDoWork", "WorkerThread.WorkerDoWork"),
                Frame.WorkerThreadStart,
                Frame.CoreLib("System.Threading.Thread.StartCallback", "Thread.StartCallback"),
            };

            var scenario = new StitchScenario
            {
                Kind = AsyncCallstackKind.StateMachineAsync,
                Sync = sync,
                Async = new[]
                {
                    logicalAsyncFoo,
                },
                ExpectedStitched = new[]
                {
                    sync[0],
                    sync[1],
                    sync[2],
                    logicalAsyncFoo,
                    sync[11],
                    sync[12],
                    sync[13],
                    sync[14],
                    sync[15],
                    sync[16],
                    sync[17],
                    sync[18],
                    sync[19],
                },
                FilteredOut = sync.Skip(11).Take(5).ToArray(),
                ExpectedDiagnostics = new StitchDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V1InlineFallbackUsed = 2,
                    V1InfrastructureFramesCollapsed = 6,
                },
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void StateMachineAsync_DeepResume_StitchesSuspendedAncestryAndRemovesSchedulingFrames()
        {
            Frame readConsoleInput = Frame.Library("System.Console", "Interop+Kernel32.ReadConsoleInput", "Interop.Kernel32.ReadConsoleInput");
            Frame consolePalReadKey = Frame.Library("System.Console", "System.ConsolePal.ReadKey", "ConsolePal.ReadKey");
            Frame consoleReadKey = Frame.Library("System.Console", "System.Console.ReadKey", "Console.ReadKey");
            Frame level4 = Frame.App("TestCase8+<Level4Async>d__6.MoveNext");
            Frame level3 = Frame.App("TestCase8+<Level3Async>d__5.MoveNext");
            Frame level2 = Frame.App("TestCase8+<Level2Async>d__4.MoveNext");
            Frame level1 = Frame.App("TestCase8+<Level1Async>d__3.MoveNext");
            Frame run = Frame.App("TestCase8+<Run>d__2.MoveNext");
            Frame main = Frame.App("Program+<Main>d__0.MoveNext");
            Frame logicalLevel4 = level4.Logical("TestCase8.Level4Async");
            Frame logicalLevel3 = level3.Logical("TestCase8.Level3Async");
            Frame logicalLevel2 = level2.Logical("TestCase8.Level2Async");
            Frame logicalLevel1 = level1.Logical("TestCase8.Level1Async");
            Frame logicalRun = run.Logical("TestCase8.Run");
            Frame logicalMain = main.Logical("Program.Main");
            Frame executionContextCallback = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox.ExecutionContextCallback",
                "AsyncStateMachineBox.ExecutionContextCallback");
            Frame runFromThreadPool = Frame.CoreLib(
                "System.Threading.ExecutionContext.RunFromThreadPoolDispatchLoop",
                "ExecutionContext.RunFromThreadPoolDispatchLoop");
            Frame boxMoveNextThreadFlags = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox.MoveNext(Thread,Flags)",
                "AsyncStateMachineBox.MoveNext(Thread,Flags)");
            Frame instrumentedMoveNext = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncProfilerAsyncStateMachineBox.InstrumentedMoveNext",
                "AsyncProfilerAsyncStateMachineBox.InstrumentedMoveNext");
            Frame boxMoveNextThread = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox.MoveNext(Thread)",
                "AsyncStateMachineBox.MoveNext(Thread)");
            Frame executeDirectly = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox.ExecuteDirectly",
                "AsyncStateMachineBox.ExecuteDirectly");
            Frame workerDoWork = Frame.CoreLib(
                "System.Threading.PortableThreadPool+WorkerThread.WorkerDoWork", "WorkerThread.WorkerDoWork");
            Frame threadStartCallback = Frame.CoreLib("System.Threading.Thread.StartCallback", "Thread.StartCallback");
            var scenario = new StitchScenario
            {
                Kind = AsyncCallstackKind.StateMachineAsync,
                Sync = new[]
                {
                    readConsoleInput,
                    consolePalReadKey,
                    consoleReadKey,
                    level4,
                    executionContextCallback,
                    runFromThreadPool,
                    boxMoveNextThreadFlags,
                    Frame.V1MoveNextAsDispatcher,
                    instrumentedMoveNext,
                    boxMoveNextThread,
                    executeDirectly,
                    Frame.ThreadPoolDispatch,
                    workerDoWork,
                    Frame.WorkerThreadStart,
                    threadStartCallback,
                },
                Async = new[]
                {
                    logicalLevel4,
                    logicalLevel3,
                    logicalLevel2,
                    logicalLevel1,
                    logicalRun,
                    logicalMain,
                },
                AsyncStates = new[] { 0, 0, 0, 0, 0, 10 },
                ExpectedStitched = new[]
                {
                    readConsoleInput,
                    consolePalReadKey,
                    consoleReadKey,
                    logicalLevel4,
                    logicalLevel3,
                    logicalLevel2,
                    logicalLevel1,
                    logicalRun,
                    logicalMain,
                    Frame.ThreadPoolDispatch,
                    workerDoWork,
                    Frame.WorkerThreadStart,
                    threadStartCallback,
                },
                ExpectedDiagnostics = new StitchDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V1InlineFallbackUsed = 2,
                    V1InfrastructureFramesCollapsed = 6,
                },
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void StateMachineAsync_InlineCompletion_ExcludesCompletedFrames()
        {
            Frame readConsoleInput = Frame.Library("System.Console", "Interop+Kernel32.ReadConsoleInput", "Interop.Kernel32.ReadConsoleInput");
            Frame consolePalReadKey = Frame.Library("System.Console", "System.ConsolePal.ReadKey", "ConsolePal.ReadKey");
            Frame consoleReadKey = Frame.Library("System.Console", "System.Console.ReadKey", "Console.ReadKey");
            Frame level4 = Frame.App("TestCase8+<Level4Async>d__6.MoveNext");
            Frame level3 = Frame.App("TestCase8+<Level3Async>d__5.MoveNext");
            Frame level2 = Frame.App("TestCase8+<Level2Async>d__4.MoveNext");
            Frame level1 = Frame.App("TestCase8+<Level1Async>d__3.MoveNext");
            Frame run = Frame.App("TestCase8+<Run>d__2.MoveNext");
            Frame main = Frame.App("Program+<Main>d__0.MoveNext");
            Frame logicalLevel4 = level4.Logical("TestCase8.Level4Async");
            Frame logicalLevel3 = level3.Logical("TestCase8.Level3Async");
            Frame logicalLevel2 = level2.Logical("TestCase8.Level2Async");
            Frame logicalLevel1 = level1.Logical("TestCase8.Level1Async");
            Frame logicalRun = run.Logical("TestCase8.Run");
            Frame logicalMain = main.Logical("Program.Main");
            Frame workerDoWork = Frame.CoreLib(
                "System.Threading.PortableThreadPool+WorkerThread.WorkerDoWork", "WorkerThread.WorkerDoWork");
            Frame threadStartCallback = Frame.CoreLib("System.Threading.Thread.StartCallback", "Thread.StartCallback");
            Frame profilerInstrumentedMoveNext = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncProfilerAsyncStateMachineBox.ProfilerInstrumentedMoveNext",
                "AsyncProfilerAsyncStateMachineBox.ProfilerInstrumentedMoveNext");
            Frame boxMoveNextThread = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox.MoveNext(Thread)",
                "AsyncStateMachineBox.MoveNext(Thread)");
            Frame executeDirectly = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox.ExecuteDirectly",
                "AsyncStateMachineBox.ExecuteDirectly");

            var scenario = new StitchScenario
            {
                Kind = AsyncCallstackKind.StateMachineAsync,
                Sync = new[]
                {
                    readConsoleInput,
                    consolePalReadKey,
                    consoleReadKey,
                    level1,
                    Frame.V1InlineInfrastructure("Level1", "ExecutionContextCallback"),
                    Frame.V1InlineInfrastructure("Level1", "ExecutionContext.RunInternal"),
                    Frame.V1InlineInfrastructure("Level1", "MoveNext(Thread,Flags)"),
                    Frame.V1InlineInfrastructure("Level1", "InstrumentedMoveNext"),
                    Frame.V1InlineInfrastructure("Level1", "ProfilerInstrumentedMoveNext"),
                    Frame.V1InlineInfrastructure("Level1", "MoveNext(Thread)"),
                    Frame.V1InlineInfrastructure("Level1", "MoveNext()"),
                    Frame.V1InlineInfrastructure("Level1", "AwaitTaskContinuation.RunOrScheduleAction"),
                    Frame.V1InlineInfrastructure("Level1", "Task.RunContinuations"),
                    Frame.V1InlineInfrastructure("Level1", "Task.TrySetResult"),
                    Frame.V1InlineInfrastructure("Level1", "SetExistingTaskResult"),
                    level2,
                    Frame.V1InlineInfrastructure("Level2", "ExecutionContextCallback"),
                    Frame.V1InlineInfrastructure("Level2", "ExecutionContext.RunInternal"),
                    Frame.V1InlineInfrastructure("Level2", "MoveNext(Thread,Flags)"),
                    Frame.V1InlineInfrastructure("Level2", "InstrumentedMoveNext"),
                    Frame.V1InlineInfrastructure("Level2", "ProfilerInstrumentedMoveNext"),
                    Frame.V1InlineInfrastructure("Level2", "MoveNext(Thread)"),
                    Frame.V1InlineInfrastructure("Level2", "MoveNext()"),
                    Frame.V1InlineInfrastructure("Level2", "AwaitTaskContinuation.RunOrScheduleAction"),
                    Frame.V1InlineInfrastructure("Level2", "Task.RunContinuations"),
                    Frame.V1InlineInfrastructure("Level2", "Task.TrySetResult"),
                    Frame.V1InlineInfrastructure("Level2", "SetExistingTaskResult"),
                    level3,
                    Frame.V1InlineInfrastructure("Level3", "ExecutionContextCallback"),
                    Frame.V1InlineInfrastructure("Level3", "ExecutionContext.RunInternal"),
                    Frame.V1InlineInfrastructure("Level3", "MoveNext(Thread,Flags)"),
                    Frame.V1InlineInfrastructure("Level3", "InstrumentedMoveNext"),
                    Frame.V1InlineInfrastructure("Level3", "ProfilerInstrumentedMoveNext"),
                    Frame.V1InlineInfrastructure("Level3", "MoveNext(Thread)"),
                    Frame.V1InlineInfrastructure("Level3", "MoveNext()"),
                    Frame.V1InlineInfrastructure("Level3", "AwaitTaskContinuation.RunOrScheduleAction"),
                    Frame.V1InlineInfrastructure("Level3", "Task.RunContinuations"),
                    Frame.V1InlineInfrastructure("Level3", "Task.TrySetResult"),
                    Frame.V1InlineInfrastructure("Level3", "SetExistingTaskResult"),
                    level4,
                    Frame.V1InlineInfrastructure("Level4", "ExecutionContextCallback"),
                    Frame.V1InlineInfrastructure("Level4", "ExecutionContext.RunFromThreadPoolDispatchLoop"),
                    Frame.V1InlineInfrastructure("Level4", "MoveNext(Thread,Flags)"),
                    Frame.V1MoveNextAsDispatcher,
                    profilerInstrumentedMoveNext,
                    boxMoveNextThread,
                    executeDirectly,
                    Frame.ThreadPoolDispatch,
                    workerDoWork,
                    Frame.WorkerThreadStart,
                    threadStartCallback,
                },
                Async = new[]
                {
                    logicalLevel4,
                    logicalLevel3,
                    logicalLevel2,
                    logicalLevel1,
                    logicalRun,
                    logicalMain,
                },
                AsyncStates = new[] { 0, 0, 0, 0, 0, 10 },
                ExpectedStitched = new[]
                {
                    readConsoleInput,
                    consolePalReadKey,
                    consoleReadKey,
                    logicalLevel1,
                    logicalRun,
                    logicalMain,
                    Frame.ThreadPoolDispatch,
                    workerDoWork,
                    Frame.WorkerThreadStart,
                    threadStartCallback,
                },
                ExpectedDiagnostics = new StitchDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V1InlineFallbackUsed = 2,
                    V1InfrastructureFramesCollapsed = 6,
                },
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void RuntimeAsync_ActivityGroupingDisabled_UsesConsistentThreadRoot()
        {
            var scenario = new StitchScenario
            {
                Sync = new[]
                {
                    Frame.App("Scenario.DoWork"),
                    Frame.App("Scenario.Level3Async"),
                    Frame.V2Wrapper(0),
                    Frame.V2InstrumentedDispatch,
                    Frame.V2Dispatch,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                Async = new[]
                {
                    Frame.App("Scenario.Level3Async"),
                    Frame.App("Scenario.Level2Async"),
                    Frame.App("Program.Main"),
                },
                ExpectedStitched = new[]
                {
                    Frame.App("Scenario.DoWork"),
                    Frame.App("Scenario.Level3Async"),
                    Frame.App("Scenario.Level2Async"),
                    Frame.App("Program.Main"),
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                GroupByStartStopActivity = false,
                ExpectedDiagnostics = new StitchDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V2PlumbingFramesCollapsed = 4,
                },
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void MixedRuntimeAndStateMachineAsync_PreservesSynchronousAndNativeBridge()
        {
            Frame innerWork = Frame.App("Scenario.InnerSynchronousWork");
            Frame innerCurrent = Frame.App("Scenario+<InnerV1Async>d__4.MoveNext");
            Frame innerParent = Frame.App("Scenario+<InnerV1ParentAsync>d__3.MoveNext");
            Frame logicalInnerCurrent = innerCurrent.Logical("Scenario.InnerV1Async");
            Frame logicalInnerParent = innerParent.Logical("Scenario.InnerV1ParentAsync");
            Frame executionContextCallback = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox.ExecutionContextCallback",
                "AsyncStateMachineBox.ExecutionContextCallback");
            Frame runInternal = Frame.CoreLib(
                "System.Threading.ExecutionContext.RunInternal", "ExecutionContext.RunInternal");
            Frame boxMoveNext = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox.MoveNext(Thread,Flags)",
                "AsyncStateMachineBox.MoveNext(Thread,Flags)");
            Frame knownV1Infrastructure1 = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncProfilerAsyncStateMachineBox.InstrumentedMoveNext",
                "KnownV1BoxInfrastructure1");
            Frame knownV1Infrastructure2 = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox.MoveNext(Thread)",
                "KnownV1BoxInfrastructure2");
            Frame unclassifiedSchedulingFrame = Frame.CoreLib(
                "TestOnly.UnclassifiedSchedulingFrame", "UnclassifiedSchedulingFrame");
            Frame nativeBridgeFrame = Frame.Library(
                "SyntheticNativeBridge", "SyntheticNativeBridge.NativeBridgeFrame", "NativeBridgeFrame");
            Frame synchronousBridgeFrame = Frame.App("Scenario.SynchronousBridgeFrame");
            Frame outerCurrent = Frame.App("Scenario.OuterV2Async");
            Frame outerParent = Frame.App("Scenario.OuterV2ParentAsync");

            var scenario = new StitchScenario
            {
                Sync = new[]
                {
                    innerWork,
                    innerCurrent,
                    executionContextCallback,
                    runInternal,
                    boxMoveNext,
                    Frame.V1MoveNextAsDispatcher,
                    knownV1Infrastructure1,
                    knownV1Infrastructure2,
                    unclassifiedSchedulingFrame,
                    nativeBridgeFrame,
                    synchronousBridgeFrame,
                    outerCurrent,
                    Frame.V2Wrapper(0),
                    Frame.V2InstrumentedDispatch,
                    Frame.V2Dispatch,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                AsyncSegments = new[]
                {
                    AsyncSegment.Runtime(outerCurrent, outerParent),
                    AsyncSegment.StateMachine(logicalInnerCurrent, logicalInnerParent),
                },
                ExpectedStitched = new[]
                {
                    innerWork,
                    logicalInnerCurrent,
                    logicalInnerParent,
                    unclassifiedSchedulingFrame,
                    nativeBridgeFrame,
                    synchronousBridgeFrame,
                    outerCurrent,
                    outerParent,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                FilteredOut = new[]
                {
                    unclassifiedSchedulingFrame,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                ExpectedDiagnostics = new StitchDiagnosticsExpectation
                {
                    SegmentsProcessed = 4,
                    V1InlineFallbackUsed = 2,
                    V1InfrastructureFramesCollapsed = 4,
                    V2PlumbingFramesCollapsed = 4,
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(AsyncCallstackKind.RuntimeAsync, AsyncCallstackKind.StateMachineAsync)]
        [InlineData(AsyncCallstackKind.StateMachineAsync, AsyncCallstackKind.StateMachineAsync)]
        [InlineData(AsyncCallstackKind.RuntimeAsync, AsyncCallstackKind.RuntimeAsync)]
        public void NestedSegments_OtherKindOrderings_PreserveUnclassifiedBridge(
            AsyncCallstackKind innerKind, AsyncCallstackKind outerKind)
        {
            Frame innerWork = Frame.App("Scenario.InnerSynchronousWork");
            Frame innerIdentity = innerKind == AsyncCallstackKind.StateMachineAsync
                ? Frame.App("Scenario+<InnerV1Async>d__4.MoveNext")
                : Frame.App("Scenario.InnerV2Async");
            Frame innerCurrent = innerKind == AsyncCallstackKind.StateMachineAsync
                ? innerIdentity.Logical("Scenario.InnerV1Async")
                : innerIdentity;
            Frame innerParentIdentity = innerKind == AsyncCallstackKind.StateMachineAsync
                ? Frame.App("Scenario+<InnerV1ParentAsync>d__3.MoveNext")
                : Frame.App("Scenario.InnerV2ParentAsync");
            Frame innerParent = innerKind == AsyncCallstackKind.StateMachineAsync
                ? innerParentIdentity.Logical("Scenario.InnerV1ParentAsync")
                : innerParentIdentity;

            Frame outerIdentity = outerKind == AsyncCallstackKind.StateMachineAsync
                ? Frame.App("Scenario+<OuterV1Async>d__2.MoveNext")
                : Frame.App("Scenario.OuterV2Async");
            Frame outerCurrent = outerKind == AsyncCallstackKind.StateMachineAsync
                ? outerIdentity.Logical("Scenario.OuterV1Async")
                : outerIdentity;
            Frame outerParentIdentity = outerKind == AsyncCallstackKind.StateMachineAsync
                ? Frame.App("Scenario+<OuterV1ParentAsync>d__1.MoveNext")
                : Frame.App("Scenario.OuterV2ParentAsync");
            Frame outerParent = outerKind == AsyncCallstackKind.StateMachineAsync
                ? outerParentIdentity.Logical("Scenario.OuterV1ParentAsync")
                : outerParentIdentity;

            Frame v1LeafTransition = Frame.CoreLib(
                "System.Threading.ExecutionContext.RunInternal", "KnownV1LeafTransition");
            Frame v1RootInfrastructure = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncProfilerAsyncStateMachineBox.InstrumentedMoveNext",
                "KnownV1RootInfrastructure");
            Frame unclassifiedSchedulingFrame = Frame.CoreLib(
                "TestOnly.UnclassifiedSchedulingFrame", "UnclassifiedSchedulingFrame");
            Frame nativeBridgeFrame = Frame.Library(
                "SyntheticNativeBridge", "SyntheticNativeBridge.NativeBridgeFrame", "NativeBridgeFrame");
            Frame synchronousBridgeFrame = Frame.App("Scenario.SynchronousBridgeFrame");

            var sync = new List<Frame> { innerWork, innerIdentity };
            AddSyntheticDispatcher(sync, innerKind, v1LeafTransition, v1RootInfrastructure);
            sync.Add(unclassifiedSchedulingFrame);
            sync.Add(nativeBridgeFrame);
            sync.Add(synchronousBridgeFrame);
            sync.Add(outerIdentity);
            AddSyntheticDispatcher(sync, outerKind, v1LeafTransition, v1RootInfrastructure);
            sync.Add(Frame.ThreadPoolDispatch);
            sync.Add(Frame.WorkerThreadStart);

            AsyncSegment innerSegment = innerKind == AsyncCallstackKind.StateMachineAsync
                ? AsyncSegment.StateMachine(innerCurrent, innerParent)
                : AsyncSegment.Runtime(innerCurrent, innerParent);
            AsyncSegment outerSegment = outerKind == AsyncCallstackKind.StateMachineAsync
                ? AsyncSegment.StateMachine(outerCurrent, outerParent)
                : AsyncSegment.Runtime(outerCurrent, outerParent);

            var scenario = new StitchScenario
            {
                Sync = sync.ToArray(),
                AsyncSegments = new[] { outerSegment, innerSegment },
                ExpectedStitched = new[]
                {
                    innerWork,
                    innerCurrent,
                    innerParent,
                    unclassifiedSchedulingFrame,
                    nativeBridgeFrame,
                    synchronousBridgeFrame,
                    outerCurrent,
                    outerParent,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                FilteredOut = new[] { unclassifiedSchedulingFrame },
                ExpectedDiagnostics = new StitchDiagnosticsExpectation
                {
                    SegmentsProcessed = 4,
                    V1InlineFallbackUsed =
                        (innerKind == AsyncCallstackKind.StateMachineAsync ? 2 : 0) +
                        (outerKind == AsyncCallstackKind.StateMachineAsync ? 2 : 0),
                    V1InfrastructureFramesCollapsed =
                        (innerKind == AsyncCallstackKind.StateMachineAsync ? 2 : 0) +
                        (outerKind == AsyncCallstackKind.StateMachineAsync ? 2 : 0),
                    V2PlumbingFramesCollapsed =
                        (innerKind == AsyncCallstackKind.RuntimeAsync ? 4 : 0) +
                        (outerKind == AsyncCallstackKind.RuntimeAsync ? 4 : 0),
                },
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void MixedRuntimeAndStateMachineAsync_CompletionHistoryIsIndependentPerSegment()
        {
            Frame innerWork = Frame.App("Scenario.InnerSynchronousWork");
            Frame innerCompleted = Frame.App("Scenario+<InnerCompletedAsync>d__5.MoveNext");
            Frame innerCurrent = Frame.App("Scenario+<InnerCurrentAsync>d__4.MoveNext");
            Frame innerParent = Frame.App("Scenario+<InnerV1ParentAsync>d__3.MoveNext");
            Frame logicalInnerCompleted = innerCompleted.Logical("Scenario.InnerCompletedAsync");
            Frame logicalInnerCurrent = innerCurrent.Logical("Scenario.InnerCurrentAsync");
            Frame logicalInnerParent = innerParent.Logical("Scenario.InnerV1ParentAsync");
            Frame executionContextCallback = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox.ExecutionContextCallback",
                "AsyncStateMachineBox.ExecutionContextCallback");
            Frame runInternal = Frame.CoreLib(
                "System.Threading.ExecutionContext.RunInternal", "ExecutionContext.RunInternal");
            Frame boxMoveNext = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox.MoveNext(Thread,Flags)",
                "AsyncStateMachineBox.MoveNext(Thread,Flags)");
            Frame completedTransition = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox.SetExistingTaskResult",
                "AsyncStateMachineBox.SetExistingTaskResult");
            Frame knownV1Infrastructure1 = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncProfilerAsyncStateMachineBox.InstrumentedMoveNext",
                "KnownV1BoxInfrastructure1");
            Frame knownV1Infrastructure2 = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox.MoveNext(Thread)",
                "KnownV1BoxInfrastructure2");
            Frame unclassifiedSchedulingFrame = Frame.CoreLib(
                "TestOnly.UnclassifiedSchedulingFrame", "UnclassifiedSchedulingFrame");
            Frame nativeBridgeFrame = Frame.Library(
                "SyntheticNativeBridge", "SyntheticNativeBridge.NativeBridgeFrame", "NativeBridgeFrame");
            Frame synchronousBridgeFrame = Frame.App("Scenario.SynchronousBridgeFrame");
            Frame outerCompleted = Frame.App("Scenario.OuterCompletedAsync");
            Frame outerCurrent = Frame.App("Scenario.OuterCurrentAsync");
            Frame outerParent = Frame.App("Scenario.OuterV2ParentAsync");

            var scenario = new StitchScenario
            {
                Sync = new[]
                {
                    innerWork,
                    innerCurrent,
                    executionContextCallback,
                    runInternal,
                    boxMoveNext,
                    innerCompleted,
                    completedTransition,
                    Frame.V1MoveNextAsDispatcher,
                    knownV1Infrastructure1,
                    knownV1Infrastructure2,
                    unclassifiedSchedulingFrame,
                    nativeBridgeFrame,
                    synchronousBridgeFrame,
                    outerCurrent,
                    Frame.V2Wrapper(1),
                    Frame.V2InstrumentedDispatch,
                    Frame.V2Dispatch,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                AsyncSegments = new[]
                {
                    AsyncSegment.Runtime(outerCompleted, outerCurrent, outerParent),
                    AsyncSegment.StateMachine(logicalInnerCompleted, logicalInnerCurrent, logicalInnerParent),
                },
                ExpectedStitched = new[]
                {
                    innerWork,
                    logicalInnerCurrent,
                    logicalInnerParent,
                    unclassifiedSchedulingFrame,
                    nativeBridgeFrame,
                    synchronousBridgeFrame,
                    outerCurrent,
                    outerParent,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                FilteredOut = new[] { unclassifiedSchedulingFrame },
                ExpectedDiagnostics = new StitchDiagnosticsExpectation
                {
                    SegmentsProcessed = 4,
                    V1InlineFallbackUsed = 2,
                    V1InfrastructureFramesCollapsed = 4,
                    V2PlumbingFramesCollapsed = 4,
                },
            };

            scenario.AssertProductionStitch();
        }

        #region private

        private static void AddSyntheticDispatcher(
            List<Frame> sync, AsyncCallstackKind kind, Frame v1LeafTransition, Frame v1RootInfrastructure)
        {
            if (kind == AsyncCallstackKind.StateMachineAsync)
            {
                sync.Add(v1LeafTransition);
                sync.Add(Frame.V1MoveNextAsDispatcher);
                sync.Add(v1RootInfrastructure);
            }
            else
            {
                sync.Add(Frame.V2Wrapper(0));
                sync.Add(Frame.V2InstrumentedDispatch);
                sync.Add(Frame.V2Dispatch);
            }
        }

        private sealed class StitchScenario
        {
            private const int ProcessId = 1234;
            private const int ForeignProcessId = 5678;
            private const int OsThreadId = 5000;
            private const long ThreadStreamIndex = 7;
            private const long ForeignThreadStreamIndex = 8;
            private const long StartQpc = 100_000;
            private const long SampleQpc = StartQpc + 25;

            public Frame[] Sync { get; set; }
            public Frame[] Async { get; set; }
            public AsyncSegment[] AsyncSegments { get; set; }
            public Frame[] ExpectedStitched { get; set; }
            public Frame[] FilteredOut { get; set; }
            public StitchDiagnosticsExpectation ExpectedDiagnostics { get; set; }
            public AsyncCallstackKind Kind { get; set; } = AsyncCallstackKind.RuntimeAsync;
            public long AsyncStartQpc { get; set; } = StartQpc + 10;
            public long AsyncEndQpc { get; set; } = StartQpc + 40;
            public bool SampleHasStack { get; set; } = true;
            public bool IncludeEventSourceEvents { get; set; } = true;
            public bool GroupByStartStopActivity { get; set; } = true;
            public bool AddForeignProcessActiveSegment { get; set; }
            public byte ContinuationIndexBase { get; set; }
            public int[] AsyncStates { get; set; }
            public bool ReleaseAsyncCallStacksAfterGeneration { get; set; }

            public void AssertProductionStitch()
            {
                Validate();

                string nettracePath = Path.Combine(Path.GetTempPath(), $"asyncprofiler_stitch_{Guid.NewGuid():N}.nettrace");
                string etlxPath = null;
                try
                {
                    File.WriteAllBytes(nettracePath, BuildNettrace());
                    etlxPath = TraceLog.CreateFromEventPipeDataFile(nettracePath);

                    using (var symbolReader = new SymbolReader(TextWriter.Null))
                    using (var traceLog = new TraceLog(etlxPath))
                    {
                        AsyncSegment[] expectedSegments = GetAsyncSegments();
                        long asyncProbeQpc = AsyncStartQpc + expectedSegments.Length;
                        IReadOnlyList<AsyncCallStack> segments = traceLog.GetAsyncCallStacks(ProcessId, OsThreadId, asyncProbeQpc);
                        Assert.Equal(expectedSegments.Length, segments.Count);
                        for (int i = 0; i < expectedSegments.Length; i++)
                        {
                            Assert.Equal(Labels(expectedSegments[i].Frames),
                                AsyncFrameLabels(traceLog, segments[i].Frames, expectedSegments[i]));
                        }
                        if (AddForeignProcessActiveSegment)
                        {
                            Assert.Empty(traceLog.GetAsyncCallStacks(ProcessId, OsThreadId, SampleQpc));
                            Assert.Single(traceLog.GetAsyncCallStacks(ForeignProcessId, OsThreadId, SampleQpc));
                        }

                        var syncStackSource = new MutableTraceEventStackSource(traceLog);
                        var syncComputer = new SampleProfilerThreadTimeComputer(traceLog, symbolReader)
                        {
                            IncludeEventSourceEvents = IncludeEventSourceEvents,
                            GroupByStartStopActivity = GroupByStartStopActivity,
                        };
                        syncComputer.GenerateThreadTimeStacks(syncStackSource);
                        EmittedStack syncOutput = ReadSingleStack(syncStackSource);
                        Assert.Equal(Labels(Sync), syncOutput.ScenarioFrames);

                        var stitchedStackSource = new MutableTraceEventStackSource(traceLog);
                        var stitchedComputer = new SampleProfilerThreadTimeComputer(traceLog, symbolReader, stitchAsyncCallStacks: true)
                        {
                            IncludeEventSourceEvents = IncludeEventSourceEvents,
                            GroupByStartStopActivity = GroupByStartStopActivity,
                            ReleaseAsyncCallStacksAfterGeneration = ReleaseAsyncCallStacksAfterGeneration,
                        };
                        stitchedComputer.GenerateThreadTimeStacks(stitchedStackSource);
                        if (ReleaseAsyncCallStacksAfterGeneration)
                        {
                            Assert.False(traceLog.IsAsyncCallStacksLoaded);
                        }
                        EmittedStack stitchedOutput = ReadSingleStack(stitchedStackSource);
                        Assert.Equal(Labels(ExpectedStitched), stitchedOutput.ScenarioFrames);

                        Assert.Equal(syncOutput.RootFrames, stitchedOutput.RootFrames);
                        Assert.True(stitchedComputer.AsyncStitchActive);
                        (ExpectedDiagnostics ?? new StitchDiagnosticsExpectation()).Assert(stitchedComputer.AsyncStitchDiagnostics);

                        if (ReleaseAsyncCallStacksAfterGeneration)
                        {
                            Assert.Equal(
                                expectedSegments.Length,
                                traceLog.GetAsyncCallStacks(ProcessId, OsThreadId, asyncProbeQpc).Count);
                            Assert.True(traceLog.IsAsyncCallStacksLoaded);
                        }

                        if (FilteredOut != null && FilteredOut.Length != 0)
                        {
                            var filteredStackSource = new MutableTraceEventStackSource(traceLog);
                            var filteredComputer = new SampleProfilerThreadTimeComputer(
                                traceLog, symbolReader, stitchAsyncCallStacks: true)
                            {
                                IncludeEventSourceEvents = IncludeEventSourceEvents,
                                GroupByStartStopActivity = GroupByStartStopActivity,
                                AsyncStitchFrameFilter = frame => IncludeFilteredFrame(traceLog, frame),
                            };
                            filteredComputer.GenerateThreadTimeStacks(filteredStackSource);
                            EmittedStack filteredOutput = ReadSingleStack(filteredStackSource);
                            Assert.Equal(Labels(ExpectedFilteredFrames()), filteredOutput.ScenarioFrames);
                            Assert.Equal(syncOutput.RootFrames, filteredOutput.RootFrames);
                        }
                    }
                }
                finally
                {
                    if (File.Exists(nettracePath))
                    {
                        File.Delete(nettracePath);
                    }
                    if (etlxPath != null && File.Exists(etlxPath))
                    {
                        File.Delete(etlxPath);
                    }
                }
            }

            private byte[] BuildNettrace()
            {
                Dictionary<string, ModuleMapping> mappings = CreateModuleMappings();
                Dictionary<string, ulong> addresses = AssignAddresses(mappings);
                var asyncBufferBuilder = new AsyncProfilerBufferBuilder(OsThreadId, 0x0BADF00D, StartQpc)
                    .Armed(StartQpc);
                AsyncSegment[] segments = GetAsyncSegments();
                for (int i = 0; i < segments.Length; i++)
                {
                    AsyncSegment segment = segments[i];
                    ulong[] frameAddresses = segment.Frames.Select(frame => addresses[frame.Key]).ToArray();
                    if (segment.Kind == AsyncCallstackKind.RuntimeAsync)
                    {
                        asyncBufferBuilder.ResumeRuntimeStack(
                            AsyncStartQpc + i, dispatcher: (ulong)i + 1, frameAddresses, segment.ContinuationIndexBase);
                    }
                    else
                    {
                        asyncBufferBuilder.ResumeStack(
                            AsyncStartQpc + i, dispatcher: (ulong)i + 1, frameAddresses,
                            segment.States ?? new int[frameAddresses.Length]);
                    }
                }

                for (int i = segments.Length - 1; i >= 0; i--)
                {
                    long closeQpc = AsyncEndQpc + (segments.Length - 1 - i);
                    if (segments[i].Kind == AsyncCallstackKind.RuntimeAsync)
                    {
                        asyncBufferBuilder.SuspendRuntime(closeQpc);
                    }
                    else
                    {
                        asyncBufferBuilder.Suspend(closeQpc);
                    }
                }
                byte[] asyncBuffer = asyncBufferBuilder.Build();
                byte[] foreignAsyncBuffer = AddForeignProcessActiveSegment
                    ? new AsyncProfilerBufferBuilder(OsThreadId, 0x0BADBEEF, StartQpc)
                        .Armed(StartQpc)
                        .ResumeRuntimeStack(StartQpc + 10, dispatcher: 2,
                            segments[0].Frames.Select(frame => addresses[frame.Key]).ToArray(),
                            continuationIndex: segments[0].ContinuationIndexBase)
                        .SuspendRuntime(StartQpc + 40)
                        .Build()
                    : null;

                var asyncMetadata = new EventMetadata(
                    1, AsyncProfilerTraceEventParser.ProviderName, "AsyncEvents", AsyncProfilerTraceEventParser.AsyncEventsEventId)
                {
                    ProviderId = AsyncProfilerTraceEventParser.ProviderGuid,
                };
                var sampleMetadata = new EventMetadata(
                    2, SampleProfilerTraceEventParser.ProviderName, "Sample", 0,
                    new MetadataParameter("Type", MetadataTypeCode.Int32))
                {
                    ProviderId = SampleProfilerTraceEventParser.ProviderGuid,
                };
                var mappingMetadata = new EventMetadata(3, "Universal.System", "ProcessMapping", 3)
                {
                    ProviderId = EventPipeFixtureWriter.UniversalSystemProviderGuid,
                };
                var symbolMetadata = new EventMetadata(4, "Universal.System", "ProcessSymbol", 4)
                {
                    ProviderId = EventPipeFixtureWriter.UniversalSystemProviderGuid,
                };

                var writer = new EventPipeFixtureWriter();
                writer.WriteHeadersWithNonZeroSyncTime();
                writer.WriteMetadataBlock(asyncMetadata, sampleMetadata, mappingMetadata, symbolMetadata);
                writer.WriteThreadBlock(w =>
                {
                    w.WriteThreadEntry(ThreadStreamIndex, OsThreadId, ProcessId);
                    if (AddForeignProcessActiveSegment)
                    {
                        w.WriteThreadEntry(ForeignThreadStreamIndex, OsThreadId + 1, ForeignProcessId);
                    }
                });
                if (SampleHasStack)
                {
                    writer.WriteBlock(5 /* BlockKind.StackBlock */, w =>
                    {
                        w.Write(1); // firstStackId
                        w.Write(1); // countStackIds
                        WriteStack(w, Sync.Select(frame => addresses[frame.Key]));
                    });
                }
                writer.WriteEventBlock(w =>
                {
                    int sequence = 1;
                    foreach (ModuleMapping mapping in mappings.Values)
                    {
                        w.WriteEventBlob(EventOptions(3, sequence++, StartQpc + 1), p =>
                            WriteProcessMappingPayload(p, mapping.Id, mapping.StartAddress, mapping.EndAddress, mapping.Name));
                    }

                    ulong symbolId = 1;
                    foreach (Frame frame in InputFrames())
                    {
                        if (frame == Frame.Zero)
                        {
                            continue;
                        }

                        ulong mappingId = mappings[frame.ModuleName].Id;
                        ulong address = addresses[frame.Key];
                        w.WriteEventBlob(EventOptions(4, sequence++, StartQpc + 2), p =>
                            WriteProcessSymbolPayload(p, symbolId++, mappingId, address, frame.SymbolName));
                    }

                    // Thread-time computation emits a CPU sample when the following sample arrives.
                    int stackId = SampleHasStack ? 1 : 0;
                    w.WriteEventBlob(EventOptions(2, sequence++, SampleQpc, stackId),
                        p => p.Write((int)ClrThreadSampleType.Managed));
                    w.WriteEventBlob(EventOptions(2, sequence++, SampleQpc + 1, stackId),
                        p => p.Write((int)ClrThreadSampleType.Managed));
                    w.WriteEventBlob(EventOptions(1, sequence, StartQpc + 50),
                        p => p.Write(AsyncEventsPayload(asyncBuffer)));
                    if (foreignAsyncBuffer != null)
                    {
                        w.WriteEventBlob(EventOptions(1, ++sequence, StartQpc + 51, threadIndex: ForeignThreadStreamIndex),
                            p => p.Write(AsyncEventsPayload(foreignAsyncBuffer)));
                    }
                });
                writer.WriteEndBlock();
                return writer.ToArray();
            }

            private Dictionary<string, ModuleMapping> CreateModuleMappings()
            {
                var result = new Dictionary<string, ModuleMapping>();
                ulong mappingId = 1;
                ulong startAddress = 0x1000;
                foreach (Frame frame in InputFrames())
                {
                    if (frame == Frame.Zero)
                    {
                        continue;
                    }

                    if (!result.ContainsKey(frame.ModuleName))
                    {
                        result.Add(frame.ModuleName,
                            new ModuleMapping(mappingId++, frame.ModuleName, startAddress, startAddress + 0x1000));
                        startAddress += 0x1000;
                    }
                }
                return result;
            }

            private Dictionary<string, ulong> AssignAddresses(Dictionary<string, ModuleMapping> mappings)
            {
                var nextAddress = mappings.ToDictionary(pair => pair.Key, pair => pair.Value.StartAddress + 0x100);
                var result = new Dictionary<string, ulong>();
                foreach (Frame frame in InputFrames())
                {
                    if (frame == Frame.Zero)
                    {
                        if (!result.ContainsKey(frame.Key))
                        {
                            result.Add(frame.Key, 0);
                        }
                        continue;
                    }

                    if (!result.ContainsKey(frame.Key))
                    {
                        result.Add(frame.Key, nextAddress[frame.ModuleName]);
                        nextAddress[frame.ModuleName] += 0x20;
                    }
                }
                return result;
            }

            private IEnumerable<Frame> InputFrames()
            {
                var seen = new HashSet<string>();
                foreach (Frame frame in Sync.Concat(GetAsyncSegments().SelectMany(segment => segment.Frames)))
                {
                    if (seen.Add(frame.Key))
                    {
                        yield return frame;
                    }
                }
            }

            private EmittedStack ReadSingleStack(MutableTraceEventStackSource stackSource)
            {
                EmittedStack result = null;
                int sampleCount = 0;
                stackSource.ForEach(sample =>
                {
                    sampleCount++;
                    result = ReadStack(stackSource, sample.StackIndex);
                });
                Assert.Equal(1, sampleCount);
                return result;
            }

            private EmittedStack ReadStack(MutableTraceEventStackSource stackSource, StackSourceCallStackIndex stackIndex)
            {
                var scenarioFrames = new List<string>();
                var rootFrames = new List<string>();
                bool inRoot = false;

                while (stackIndex != StackSourceCallStackIndex.Invalid)
                {
                    string name = stackSource.GetFrameName(stackSource.GetFrameIndex(stackIndex), false);
                    if (name.StartsWith("Thread (", StringComparison.Ordinal))
                    {
                        inRoot = true;
                    }

                    if (inRoot)
                    {
                        rootFrames.Add(name);
                    }
                    else
                    {
                        Frame frame = DeclaredFrames().FirstOrDefault(candidate =>
                            candidate != Frame.Zero &&
                            (name.Contains(candidate.SymbolName) || name.Contains(candidate.Label)));
                        if (frame != null)
                        {
                            scenarioFrames.Add(frame.Label);
                        }
                    }
                    stackIndex = stackSource.GetCallerIndex(stackIndex);
                }

                return new EmittedStack(scenarioFrames.ToArray(), rootFrames.ToArray());
            }

            private string[] AsyncFrameLabels(
                TraceLog traceLog, AsyncCallStackFrames frames, AsyncSegment expectedSegment)
            {
                var result = new string[frames.FrameCount];
                for (int i = 0; i < result.Length; i++)
                {
                    Frame expected = expectedSegment.Frames[i];
                    CodeAddressIndex codeAddress = frames.CodeAddressAt(i);
                    string name = codeAddress == CodeAddressIndex.Invalid
                        ? "0x" + frames.MethodIdAt(i).ToString("x")
                        : traceLog.CodeAddresses[codeAddress].FullMethodName;
                    if (expected == Frame.Zero)
                    {
                        Assert.Equal(0UL, frames.MethodIdAt(i));
                    }
                    else
                    {
                        Assert.Contains(expected.SymbolName, name);
                    }
                    result[i] = expected.Label;
                }
                return result;
            }

            private IEnumerable<Frame> DeclaredFrames() =>
                Sync.Concat(GetAsyncSegments().SelectMany(segment => segment.Frames))
                    .Concat(ExpectedStitched);

            private IEnumerable<Frame> ExpectedFilteredFrames()
            {
                var filteredKeys = new HashSet<string>(FilteredOut.Select(frame => frame.Key));
                return ExpectedStitched.Where(frame => !filteredKeys.Contains(frame.Key));
            }

            private bool IncludeFilteredFrame(TraceLog traceLog, StitchedFrame stitchedFrame)
            {
                if (stitchedFrame.CodeAddress == CodeAddressIndex.Invalid)
                {
                    return true;
                }

                string methodName = traceLog.CodeAddresses[stitchedFrame.CodeAddress].FullMethodName;
                return !(FilteredOut ?? Array.Empty<Frame>()).Any(frame => methodName.Contains(frame.SymbolName));
            }

            private void Validate()
            {
                Assert.NotNull(Sync);
                if (SampleHasStack)
                {
                    Assert.NotEmpty(Sync);
                }
                AsyncSegment[] segments = GetAsyncSegments();
                Assert.NotEmpty(segments);
                foreach (AsyncSegment segment in segments)
                {
                    Assert.NotNull(segment.Frames);
                    Assert.NotEmpty(segment.Frames);
                    if (segment.States != null)
                    {
                        Assert.Equal(segment.Frames.Length, segment.States.Length);
                    }
                }
                Assert.NotNull(ExpectedStitched);
                Assert.True(AsyncStartQpc < AsyncEndQpc);

                var inputKeys = new HashSet<string>(
                    Sync.Concat(segments.SelectMany(segment => segment.Frames)).Select(frame => frame.Key));
                foreach (Frame expected in ExpectedStitched)
                {
                    Assert.True(inputKeys.Contains(expected.Key),
                        $"Expected stitched frame '{expected.Label}' is not present in the sync or async input.");
                }
                foreach (Frame filtered in FilteredOut ?? Array.Empty<Frame>())
                {
                    Assert.Contains(ExpectedStitched, expected => expected.Key == filtered.Key);
                }
            }

            private AsyncSegment[] GetAsyncSegments()
            {
                if (AsyncSegments != null)
                {
                    return AsyncSegments;
                }

                return new[]
                {
                    new AsyncSegment(Kind, Async, AsyncStates, ContinuationIndexBase),
                };
            }

            private static string[] Labels(IEnumerable<Frame> frames) => frames.Select(frame => frame.Label).ToArray();

            private static WriteEventOptions EventOptions(
                int metadataId, int sequence, long timestamp, int stackId = 0, long threadIndex = ThreadStreamIndex)
            {
                return new WriteEventOptions
                {
                    MetadataId = metadataId,
                    ThreadIndexOrId = threadIndex,
                    CaptureThreadIndexOrId = threadIndex,
                    SequenceNumber = sequence,
                    StackId = stackId,
                    Timestamp = timestamp,
                    IsSorted = true,
                };
            }

            private static byte[] AsyncEventsPayload(byte[] buffer)
            {
                byte[] payload = new byte[4 + buffer.Length];
                Array.Copy(BitConverter.GetBytes(buffer.Length), 0, payload, 0, 4);
                Array.Copy(buffer, 0, payload, 4, buffer.Length);
                return payload;
            }

            private static void WriteStack(BinaryWriter writer, IEnumerable<ulong> addresses)
            {
                ulong[] addressArray = addresses.ToArray();
                writer.Write(addressArray.Length * sizeof(ulong));
                foreach (ulong address in addressArray)
                {
                    writer.Write(address);
                }
            }

            private static void WriteProcessMappingPayload(BinaryWriter writer, ulong id, ulong startAddress, ulong endAddress, string fileName)
            {
                writer.WriteVarUInt(id);
                writer.WriteVarUInt(startAddress);
                writer.WriteVarUInt(endAddress);
                writer.WriteVarUInt(0); // FileOffset
                WriteShortUTF8String(writer, fileName);
                writer.WriteVarUInt(0); // MetadataId
            }

            private static void WriteProcessSymbolPayload(BinaryWriter writer, ulong id, ulong mappingId, ulong address, string name)
            {
                writer.WriteVarUInt(id);
                writer.WriteVarUInt(mappingId);
                writer.WriteVarUInt(address);
                writer.WriteVarUInt(address + 0xF);
                WriteShortUTF8String(writer, name);
            }

            private static void WriteShortUTF8String(BinaryWriter writer, string value)
            {
                byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(value);
                writer.Write((ushort)utf8.Length);
                writer.Write(utf8);
            }
        }

        private sealed class Frame
        {
            public const string ApplicationModule = "TestApp";

            private Frame(string moduleName, string symbolName, string label, FrameKind kind = FrameKind.Ordinary, byte wrapperIndex = 0)
            {
                ModuleName = moduleName;
                SymbolName = symbolName;
                Label = label;
                Kind = kind;
                WrapperIndex = wrapperIndex;
            }

            public string ModuleName { get; }
            public string SymbolName { get; }
            public string Label { get; }
            public FrameKind Kind { get; }
            public byte WrapperIndex { get; }
            public string Key => ModuleName + "!" + SymbolName;

            public static readonly Frame Zero = new Frame(null, null, "?? (0x0)");

            public static Frame App(string name) => new Frame(ApplicationModule, name, name);

            public static Frame Library(string moduleName, string symbolName, string label) =>
                new Frame(moduleName, symbolName, label);

            public static Frame CoreLib(string symbolName, string label) =>
                new Frame(AsyncStitchBoundary.HostModuleName, symbolName, label);

            public Frame Logical(string label) => new Frame(ModuleName, SymbolName, label, Kind, WrapperIndex);

            public static Frame V1InlineInfrastructure(string level, string name) =>
                new Frame(AsyncStitchBoundary.HostModuleName, level + "." + name, level + "." + name);

            public static Frame V2Wrapper(int index) =>
                new Frame(AsyncStitchBoundary.HostModuleName,
                    "System.Runtime.CompilerServices.AsyncProfiler.ContinuationWrapper." +
                    AsyncStitchBoundary.ContinuationWrapperPrefix + index,
                    AsyncStitchBoundary.ContinuationWrapperPrefix + index,
                    FrameKind.V2Wrapper,
                    checked((byte)index));

            public static readonly Frame V2InstrumentedDispatch =
                new Frame(AsyncStitchBoundary.HostModuleName,
                    "System.Threading.Tasks.RuntimeAsyncTask." + AsyncStitchBoundary.InstrumentedDispatchContinuationsName,
                    AsyncStitchBoundary.InstrumentedDispatchContinuationsName);

            public static readonly Frame V2Dispatch =
                new Frame(AsyncStitchBoundary.HostModuleName,
                    "System.Threading.Tasks.RuntimeAsyncTask." + AsyncStitchBoundary.DispatchContinuationsName,
                    AsyncStitchBoundary.DispatchContinuationsName);

            public static readonly Frame V1MoveNextAsDispatcher =
                new Frame(AsyncStitchBoundary.HostModuleName,
                    "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncProfilerAsyncStateMachineBox." +
                    AsyncStitchBoundary.MoveNextAsDispatcherName + "(Thread,Flags)",
                    AsyncStitchBoundary.MoveNextAsDispatcherName);

            public static readonly Frame ThreadPoolDispatch =
                new Frame(AsyncStitchBoundary.HostModuleName,
                    "System.Threading.ThreadPoolWorkQueue.Dispatch",
                    "ThreadPoolWorkQueue.Dispatch");

            public static readonly Frame WorkerThreadStart =
                new Frame(AsyncStitchBoundary.HostModuleName,
                    "System.Threading.PortableThreadPool.WorkerThread.WorkerThreadStart",
                    "WorkerThreadStart");
        }

        private enum FrameKind
        {
            Ordinary,
            V2Wrapper,
        }

        private sealed class AsyncSegment
        {
            public AsyncSegment(
                AsyncCallstackKind kind, Frame[] frames, int[] states = null, byte continuationIndexBase = 0)
            {
                Kind = kind;
                Frames = frames;
                States = states;
                ContinuationIndexBase = continuationIndexBase;
            }

            public AsyncCallstackKind Kind { get; }
            public Frame[] Frames { get; }
            public int[] States { get; }
            public byte ContinuationIndexBase { get; }

            public static AsyncSegment Runtime(params Frame[] frames) =>
                new AsyncSegment(AsyncCallstackKind.RuntimeAsync, frames);

            public static AsyncSegment StateMachine(params Frame[] frames) =>
                new AsyncSegment(AsyncCallstackKind.StateMachineAsync, frames, new int[frames.Length]);
        }

        private sealed class ModuleMapping
        {
            public ModuleMapping(ulong id, string name, ulong startAddress, ulong endAddress)
            {
                Id = id;
                Name = name;
                StartAddress = startAddress;
                EndAddress = endAddress;
            }

            public ulong Id { get; }
            public string Name { get; }
            public ulong StartAddress { get; }
            public ulong EndAddress { get; }
        }

        private sealed class EmittedStack
        {
            public EmittedStack(string[] scenarioFrames, string[] rootFrames)
            {
                ScenarioFrames = scenarioFrames;
                RootFrames = rootFrames;
            }

            public string[] ScenarioFrames { get; }
            public string[] RootFrames { get; }
        }

        private sealed class StitchDiagnosticsExpectation
        {
            public int SegmentsProcessed { get; set; }
            public int BoundariesNotFound { get; set; }
            public int AdjacencyMismatches { get; set; }
            public int V2PlumbingFramesCollapsed { get; set; }
            public int V1InfrastructureFramesCollapsed { get; set; }
            public int V1InlineFallbackUsed { get; set; }
            public int V2SyncLayoutUsed { get; set; }
            public int V2LeafWrapperDropped { get; set; }

            public void Assert(StitchDiagnostics actual)
            {
                Xunit.Assert.Equal(SegmentsProcessed, actual.SegmentsProcessed);
                Xunit.Assert.Equal(BoundariesNotFound, actual.BoundariesNotFound);
                Xunit.Assert.Equal(AdjacencyMismatches, actual.AdjacencyMismatches);
                Xunit.Assert.Equal(V2PlumbingFramesCollapsed, actual.V2PlumbingFramesCollapsed);
                Xunit.Assert.Equal(V1InfrastructureFramesCollapsed, actual.V1InfrastructureFramesCollapsed);
                Xunit.Assert.Equal(V1InlineFallbackUsed, actual.V1InlineFallbackUsed);
                Xunit.Assert.Equal(V2SyncLayoutUsed, actual.V2SyncLayoutUsed);
                Xunit.Assert.Equal(V2LeafWrapperDropped, actual.V2LeafWrapperDropped);
            }
        }

        private sealed class EventPipeFixtureWriter : EventPipeWriterV6
        {
            public static readonly Guid UniversalSystemProviderGuid =
                new Guid("8c107b6c-79f8-5231-4de6-2a0e20a3f562");

            public void WriteHeadersWithNonZeroSyncTime()
            {
                _writer.WriteNetTraceHeaderV6OrGreater(6, 0);
                _writer.WriteBlockV6OrGreater(1 /* BlockKind.Trace */, w =>
                {
                    DateTime now = new DateTime(2025, 2, 3, 4, 5, 6);
                    w.Write((short)now.Year);
                    w.Write((short)now.Month);
                    w.Write((short)now.DayOfWeek);
                    w.Write((short)now.Day);
                    w.Write((short)now.Hour);
                    w.Write((short)now.Minute);
                    w.Write((short)now.Second);
                    w.Write((short)now.Millisecond);
                    w.Write((long)1);
                    w.Write((long)1000);
                    w.Write(8);
                    w.Write(0);
                });
            }
        }

        #endregion
    }
}
