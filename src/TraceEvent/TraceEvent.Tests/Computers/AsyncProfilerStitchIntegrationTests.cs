// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
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
        [Theory]
        [InlineData(100_015, 100_010, 100_020)]
        [InlineData(100_035, 100_030, 100_040)]
        public void BorrowedUserStack_UsesSourceTimeForReconstruction(long userQpc, long asyncStart, long asyncEnd)
        {
            Frame work = Frame.App("Scenario.Work");
            Frame current = Frame.App("Scenario.CurrentAsync");
            Frame parent = Frame.App("Scenario.ParentAsync");
            var scenario = new StitchScenario
            {
                Sync = new[] { work, current, Frame.V2Wrapper(0), Frame.V2InstrumentedDispatch,
                    Frame.V2Dispatch, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart },
                Async = new[] { current, parent },
                AsyncStartQpc = asyncStart,
                AsyncEndQpc = asyncEnd,
                ExpectedStitched = new[] { work, current, parent, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart },
                VerifyThreadTimeComputer = true,
                IncludeEventSourceEvents = false,
                AllowSupplementalSamples = true,
                VerifySampleWeights = true,
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V2PlumbingFramesCollapsed = 4,
                },
                ConfigureTraceLog = log =>
                {
                    ConfigureCapturedRuntime(log, "11.0.0");
                    foreach (TraceEvent data in log.Events.Where(e => e.CallStackIndex() != CallStackIndex.Invalid))
                    {
                        log.AddStackUserModeTimeStampQPC(data.EventIndex, userQpc);
                    }
                },
                AssertTransformContext = context => Assert.Equal(userQpc, context.TimestampQpc),
                AssertPublicApi = (log, path) =>
                {
                    var query = new AsyncProfilerCallStackComputer(log);
                    var output = new MutableTraceEventStackSource(log);
                    var builder = new AsyncProfilerCallStackSourceBuilder(output);
                    foreach (TraceEvent data in log.Events.Where(e => e.CallStackIndex() != CallStackIndex.Invalid))
                    {
                        Assert.True(data.TryGetCallStackIndex(out var physical, out long timestamp));
                        Assert.Equal(userQpc, timestamp);
                        Assert.Empty(query.GetCallStacks(data));
                        Assert.Single(query.GetCallStacks(data.ProcessID, (ulong)data.ThreadID, timestamp));
#pragma warning disable CS0618
                        Assert.Equal(output.GetCallStackThread(physical, data.Thread()),
                            builder.GetCallStack(physical, data.TimeStampQPC));
#pragma warning restore CS0618
                        Assert.Equal(builder.GetCallStack(physical, timestamp), builder.GetCallStack(data));
                    }
                },
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void RuntimeAsync_ResumeStub_IsConsumedStructurallyWithHonestBodyProvenance(
            bool sampledStub, bool enableNormalization)
        {
            Frame work = Frame.App("Scenario.Work");
            Frame current = Frame.App("Scenario.CurrentAsync");
            Frame parent = Frame.App("Scenario.ParentAsync");
            Frame stub = Frame.App("dynamicClass.IL_STUB_AsyncResume(class System.Object,unsigned int8&)");
            Frame[] entry = { stub, Frame.V2Wrapper(0), Frame.V2InstrumentedDispatch,
                Frame.V2Dispatch, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart };
            var scenario = new StitchScenario
            {
                Sync = sampledStub ? entry : new[] { work, current }.Concat(entry).ToArray(),
                Async = new[] { current, parent },
                ExpectedStitched = (sampledStub ? Array.Empty<Frame>() : new[] { work })
                    .Concat(new[] { current, parent, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart }).ToArray(),
                EnableNormalization = enableNormalization,
                EnableFrameAnnotations = true,
                IncludeEventSourceEvents = false,
                VerifyThreadTimeComputer = true,
                AllowSupplementalSamples = true,
                VerifySampleWeights = true,
                ConfigureTraceLog = log => ConfigureCapturedRuntime(log, "12.0.0"),
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V2PlumbingFramesCollapsed = 6,
                },
                AssertTransformContext = context =>
                {
                    AsyncProfilerStitchedFrame anchor = Assert.Single(context.Frames.Where(frame =>
                        frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent ||
                        frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget));
                    Assert.Equal(sampledStub ? AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget :
                        AsyncProfilerStitchedFrameOrigin.AsyncCurrent, anchor.Origin);
                    Assert.Equal(0, anchor.SegmentFrameIndex);
                    Assert.DoesNotContain(context.Frames, frame => frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V2ResumeStub);
                },
            };
            scenario.AssertProductionStitch();
        }

        [Fact]
        public void PublicBuilder_TransformsRootsAndIncrementalLookup()
        {
            Frame work = Frame.App("Scenario.Work");
            Frame current = Frame.App("Scenario.CurrentAsync");
            Frame parent = Frame.App("Scenario.ParentAsync");
            var scenario = new StitchScenario
            {
                Sync = new[] { work, current, Frame.V2Wrapper(0), Frame.V2InstrumentedDispatch,
                    Frame.V2Dispatch, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart },
                Async = new[] { current, parent },
                ExpectedStitched = new[] { work, current, parent, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V2PlumbingFramesCollapsed = 4,
                },
                AssertPublicApi = (log, path) =>
                {
                    var query = new AsyncProfilerCallStackComputer(log);
                    var output = new MutableTraceEventStackSource(log);
                    var builder = new AsyncProfilerCallStackSourceBuilder(output);
                    TraceEvent data = log.Events.First(e => e.CallStackIndex() != CallStackIndex.Invalid).Clone();
#pragma warning disable CS0618
                    long timestampQpc = data.TimeStampQPC;
#pragma warning restore CS0618
                    Assert.Throws<ArgumentNullException>(() => new AsyncProfilerCallStackComputer(null));
                    Assert.Throws<ArgumentNullException>(() => new AsyncProfilerCallStackSourceBuilder(null));
                    Assert.Throws<ArgumentNullException>(() => query.GetCallStacks(null));
                    Assert.Throws<ArgumentNullException>(() => builder.GetCallStack((TraceEvent)null));
                    using (var otherLog = new TraceLog(path))
                    {
                        Assert.Throws<ArgumentException>(() => new AsyncProfilerCallStackComputer(otherLog).GetCallStacks(data));
                        Assert.Throws<ArgumentException>(() =>
                            new AsyncProfilerCallStackSourceBuilder(new MutableTraceEventStackSource(otherLog)).GetCallStack(data));
                    }

                    IReadOnlyList<AsyncProfilerCallStack> segments = query.GetCallStacks(data);
                    AsyncProfilerCallStack retained = Assert.Single(segments);
                    AsyncProfilerCallStackFrame captured = retained.CapturedFrames[0];
                    Assert.Equal(retained.Frames.MethodIdAt(0), captured.CapturedInstructionPointer);
                    Assert.Equal(retained.Frames.CodeAddressAt(0), captured.CodeAddress);
                    Assert.Null(captured.CapturedState);
                    Assert.False(captured.IsPhysical);
                    Assert.False(captured.IsParentContext);
                    Assert.Empty(query.GetCallStacks(data.ProcessID, (ulong)data.ThreadID, retained.EndQpc));
                    Assert.Equal(captured.CapturedInstructionPointer, retained.CapturedFrames[0].CapturedInstructionPointer);

                    var order = new List<int>();
                    builder.Transforms.Add(context =>
                    {
                        order.Add(1);
                        Assert.Equal(timestampQpc, context.TimestampQpc);
                        Assert.Same(log, context.TraceLog);
                        Assert.Single(context.Segments);
                        Assert.DoesNotContain(context.Frames, frame =>
                            frame.CodeAddress != CodeAddressIndex.Invalid &&
                            log.CodeAddresses[frame.CodeAddress].FullMethodName.Contains("Continuation_Wrapper_"));
                        Assert.Throws<InvalidOperationException>(() => builder.GetCallStack(data));
                        context.Frames.Insert(0, new AsyncProfilerCallStackFrame("Custom label"));
                    });
                    builder.Transforms.Add(context =>
                    {
                        order.Add(2);
                        Assert.Equal("Custom label", context.Frames[0].DisplayName);
                        Assert.False(context.Frames[0].IsPhysical);
                        Assert.Equal(CodeAddressIndex.Invalid, context.Frames[0].CodeAddress);
                    });
                    StackSourceCallStackIndex first = builder.GetCallStack(data);
                    Assert.Equal(new[] { 1, 2 }, order);
                    Assert.Equal("Custom label", output.GetFrameName(output.GetFrameIndex(first), false));
                    Assert.Equal(0, output.SampleIndexLimit);
                    builder.Transforms.ClearCustomTransforms();
                    var root = output.Interner.CallStackIntern(output.Interner.FrameIntern("Custom root"),
                        StackSourceCallStackIndex.Invalid);
                    var next = builder.GetCallStack(data.CallStackIndex(), timestampQpc, root);
                    Assert.Equal(new[] { 1, 2 }, order);
                    Assert.NotEqual("Custom label", output.GetFrameName(output.GetFrameIndex(next), false));
                    while (output.GetCallerIndex(next) != StackSourceCallStackIndex.Invalid)
                    {
                        next = output.GetCallerIndex(next);
                    }
                    Assert.Equal("Custom root", output.GetFrameName(output.GetFrameIndex(next), false));
                    Assert.Equal("Custom label", output.GetFrameName(output.GetFrameIndex(first), false));
                    var fallback = builder.GetCallStack(data.CallStackIndex(), retained.EndQpc);
                    Assert.Equal(output.GetCallStackThread(data.CallStackIndex(), data.Thread()), fallback);
                    Assert.Equal(StackSourceCallStackIndex.Invalid,
                        builder.GetCallStack(CallStackIndex.Invalid, timestampQpc));
                    Assert.Throws<ArgumentOutOfRangeException>(() => builder.GetCallStack((CallStackIndex)int.MaxValue, timestampQpc));
                    Assert.Throws<ArgumentOutOfRangeException>(() =>
                        builder.GetCallStack(data.CallStackIndex(), timestampQpc, (StackSourceCallStackIndex)int.MaxValue));
                    output.AddSample(new StackSourceSample(output) { StackIndex = first, Metric = 3, TimeRelativeMSec = 2 });
                    output.AddSample(new StackSourceSample(output) { StackIndex = fallback, Metric = 7, TimeRelativeMSec = 1 });
                    output.DoneAddingSamples();
                    Assert.Equal("Custom label", output.GetFrameName(output.GetFrameIndex(first), false));
                    Assert.Throws<InvalidOperationException>(() => builder.GetCallStack(data));
                    Assert.Single(query.GetCallStacks(data));
                },
            };
            scenario.AssertProductionStitch();
        }

        [Fact]
        public void PublicBuilder_CompletionObservationsSurviveReusedCallbackSegments()
        {
            Frame current = Frame.App("Scenario.CurrentAsync");
            Frame parent = Frame.App("Scenario.ParentAsync");
            var scenario = new StitchScenario
            {
                Sync = new[] { current, Frame.V2Wrapper(0), Frame.V2InstrumentedDispatch,
                    Frame.V2Dispatch, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart },
                Async = new[] { current, parent },
                MethodCompletions = 1,
                MethodCompletionQpc = 100_035,
                ExpectedStitched = new[] { current, parent, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V2PlumbingFramesCollapsed = 4,
                },
                AssertPublicApi = (log, path) =>
                {
                    var query = new AsyncProfilerCallStackComputer(log);
                    var builder = new AsyncProfilerCallStackSourceBuilder(new MutableTraceEventStackSource(log));
                    int callbacks = 0;
                    builder.Transforms.Add(context =>
                    {
                        AsyncProfilerCallStack activation = Assert.Single(context.Segments);
                        Assert.True(activation.MethodCompletionObserved);
                        Assert.False(activation.ExceptionCompletionObserved);
                        Assert.Equal(0, activation.GetMethodCompletedFrameCount(context.TimestampQpc));
                        Assert.Contains(context.Frames, frame => ReferenceEquals(frame.Context, activation));
                        callbacks++;
                    });
                    foreach (TraceEvent sample in log.Events.Where(e => e.CallStackIndex() != CallStackIndex.Invalid))
                    {
                        AsyncProfilerCallStack activation = Assert.Single(query.GetCallStacks(sample));
                        Assert.True(activation.MethodCompletionObserved);
                        Assert.False(activation.ExceptionCompletionObserved);
                        Assert.NotEqual(StackSourceCallStackIndex.Invalid, builder.GetCallStack(sample));
                    }
                    Assert.Equal(2, callbacks);
                },
            };
            scenario.AssertProductionStitch();
        }

        [Fact]
        public void PublicCallbacks_PrepareCompletionObservationsOncePerSample()
        {
            Frame current = Frame.App("Scenario.CurrentAsync");
            Frame parent = Frame.App("Scenario.ParentAsync");
            var scenario = new StitchScenario
            {
                Sync = new[] { current, Frame.V2Wrapper(0), Frame.V2InstrumentedDispatch,
                    Frame.V2Dispatch, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart },
                Async = new[] { current, parent },
                ExpectedStitched = new[] { current, parent, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V2PlumbingFramesCollapsed = 4,
                },
                AssertPublicApi = (log, path) =>
                {
                    TraceEvent sample = log.Events.First(e => e.CallStackIndex() != CallStackIndex.Invalid);
                    var segments = new CountingCallStacks(new AsyncProfilerCallStackComputer(log).GetCallStacks(sample));
                    var pipeline = new AsyncProfilerCallStackTransformPipeline { EnableNormalization = false };
                    var transforms = new AsyncCallStackTransforms(pipeline);
                    var state = new AsyncProfilerCallStackTransformState(
                        log, segments, 0, _ => AsyncProfilerCallStackStitchBoundaryInfo.None,
                        new AsyncProfilerCallStackDiagnostics(), new List<AsyncProfilerStitchedFrame>(),
                        runtimeProfile: AsyncProfilerRuntimeRuleProfile.Common);
                    segments.ReadCount = 0;
                    pipeline.ApplyInPlace(state);
                    Assert.Equal(0, segments.ReadCount);

                    int callbacks = 0;
                    transforms.Add(_ => callbacks++);
                    transforms.Add(_ => callbacks++);
                    pipeline.ApplyInPlace(state);
                    Assert.Equal(segments.Count, segments.ReadCount);
                    Assert.Equal(2, callbacks);

                    segments.ReadCount = 0;
                    transforms.Add(_ => callbacks++);
                    pipeline.ApplyInPlace(state);
                    Assert.Equal(segments.Count, segments.ReadCount);
                    Assert.Equal(5, callbacks);

                    segments.ReadCount = 0;
                    transforms.ClearCustomTransforms();
                    pipeline.ApplyInPlace(state);
                    Assert.Equal(0, segments.ReadCount);
                    Assert.Equal(5, callbacks);

                    transforms.Add(_ => callbacks++);
                    pipeline.ApplyInPlace(state);
                    Assert.Equal(segments.Count, segments.ReadCount);
                    Assert.Equal(6, callbacks);
                },
            };
            scenario.AssertProductionStitch();
        }

        [Fact]
        public void PublicBuilder_NoAsyncIndex_PreservesPhysicalInput()
        {
            var scenario = new StitchScenario
            {
                Sync = new[] { Frame.App("Scenario.Work"), Frame.ThreadPoolDispatch, Frame.WorkerThreadStart },
                Async = Array.Empty<Frame>(),
                ExpectedStitched = Array.Empty<Frame>(),
            };
            scenario.AssertWithoutAsyncIndex();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, false)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, true)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, false)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, true)]
        public void PerfMapSymbols_StitchBothAsyncFamiliesWithoutRewritingStoredSymbols(
            int kindValue, bool enableInfrastructureCleanup)
        {
            var kind = (AsyncCallstackKind)kindValue;
            bool stateMachine = kind == AsyncCallstackKind.StateMachineAsync;
            Frame current = Frame.PerfMap(Frame.ApplicationModule,
                stateMachine ? "Scenario+<ChildAsync>d__1::MoveNext()" : "AsyncDemo.ClockScenario::ChildAAsync(int32)",
                "Physical current");
            Frame parent = Frame.PerfMap(Frame.ApplicationModule,
                stateMachine ? "Scenario+<ParentAsync>d__2::MoveNext()" : "AsyncDemo.ClockScenario::ParentAAsync(int32)",
                "Captured parent");
            Frame native = Frame.Library("NativeHelpers", "NativeHelper::Run()", "NativeHelper::Run");
            Frame dispatcher = Frame.PerfMap(AsyncProfilerCallStackStitchBoundary.HostModuleName,
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncProfilerAsyncStateMachineBox`1[" +
                "System.Int64,Scenario+<ChildAsync>d__1]::MoveNextAsDispatcher(class System.Threading.Thread," +
                "valuetype System.Runtime.CompilerServices.AsyncInstrumentation/Flags)", "V1 dispatcher");
            Frame wrapper = Frame.PerfMap(AsyncProfilerCallStackStitchBoundary.HostModuleName,
                "System.Runtime.CompilerServices.AsyncProfiler+ContinuationWrapper::Continuation_Wrapper_1(" +
                "class System.Runtime.CompilerServices.Continuation,uint8&)", "V2 wrapper");
            const string runtimeTask = "System.Runtime.CompilerServices.AsyncHelpers+RuntimeAsyncTask`1[" +
                "System.Threading.Tasks.VoidTaskResult]";
            Frame instrumented = Frame.PerfMap(AsyncProfilerCallStackStitchBoundary.HostModuleName,
                runtimeTask + "::InstrumentedDispatchContinuations(valuetype System.Runtime.CompilerServices.AsyncInstrumentation/Flags)",
                "V2 instrumented dispatch");
            Frame dispatch = Frame.PerfMap(AsyncProfilerCallStackStitchBoundary.HostModuleName,
                runtimeTask + "::DispatchContinuations()", "V2 dispatch");
            Frame threadPool = Frame.PerfMap(AsyncProfilerCallStackStitchBoundary.HostModuleName,
                "System.Threading.ThreadPoolWorkQueue::Dispatch()", "ThreadPoolWorkQueue.Dispatch");
            Frame worker = Frame.PerfMap(AsyncProfilerCallStackStitchBoundary.HostModuleName,
                "System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()", "WorkerThreadStart");
            Frame logicalCurrent = stateMachine ? current.Logical("Scenario.ChildAsync") : current;
            Frame logicalParent = stateMachine ? parent.Logical("Scenario.ParentAsync") : parent;
            var scenario = new StitchScenario
            {
                Kind = kind,
                Sync = stateMachine
                    ? new[] { native, current, dispatcher, threadPool, worker }
                    : new[] { native, current, wrapper, instrumented, dispatch, threadPool, worker },
                Async = new[] { current, parent },
                AsyncStates = stateMachine ? new[] { 0, 1 } : null,
                ContinuationIndexBase = 1,
                ExpectedStitched = new[] { native, logicalCurrent, logicalParent, threadPool, worker },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V1InlineFallbackUsed = stateMachine ? 2 : 0,
                    V2PlumbingFramesCollapsed = stateMachine ? 0 : 4,
                },
                EnableInfrastructureCleanup = enableInfrastructureCleanup,
                VerifyThreadTimeComputer = true,
                IncludeEventSourceEvents = false,
                AllowSupplementalSamples = true,
                VerifySampleWeights = true,
                AssertStitchedStackSource = stackSource =>
                {
                    var names = new List<string>();
                    stackSource.ForEach(sample =>
                    {
                        for (var stack = sample.StackIndex; stack != StackSourceCallStackIndex.Invalid;
                            stack = stackSource.GetCallerIndex(stack))
                        {
                            names.Add(stackSource.GetFrameName(stackSource.GetFrameIndex(stack), false));
                        }
                    });
                    Assert.Contains(names, name => name.Contains(worker.SymbolName));
                    Assert.Contains(names, name => name.Contains(native.SymbolName));
                    Assert.DoesNotContain(names, name => name.Contains(AsyncProfilerCallStackStitchBoundary.NormalizeMethodName(worker.SymbolName)));
                },
            };
            scenario.AssertProductionStitch();
        }

        [Fact]
        public void PerfMapSymbols_V1ReentrantRegistrationMatchesItsKickoff()
        {
            Frame work = Frame.PerfMap(Frame.ApplicationModule, "Scenario::ReentrantWork()", "ReentrantWork");
            Frame inner = Frame.PerfMap(Frame.ApplicationModule, "Scenario+<InnerAsync>d__1::MoveNext()", "Inner.MoveNext");
            Frame outer = Frame.PerfMap(Frame.ApplicationModule, "Scenario+<OuterAsync>d__2::MoveNext()", "Outer.MoveNext");
            Frame logicalInner = inner.Logical("Scenario.InnerAsync");
            Frame logicalOuter = outer.Logical("Scenario.OuterAsync");
            Frame kickoff = Frame.PerfMap(Frame.ApplicationModule, "Scenario::InnerAsync(int32)", "Kickoff");
            Frame dispatcher = Frame.PerfMap(AsyncProfilerCallStackStitchBoundary.HostModuleName,
                "System.Runtime.CompilerServices.AsyncStateMachineDispatcher::MoveNext()", "AsyncStateMachineDispatcher.MoveNext");
            Frame box = Frame.PerfMap(AsyncProfilerCallStackStitchBoundary.HostModuleName,
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncProfilerAsyncStateMachineBox::InstrumentedMoveNext()",
                "Box.InstrumentedMoveNext");
            Frame await = Frame.PerfMap(AsyncProfilerCallStackStitchBoundary.HostModuleName,
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64]::AwaitUnsafeOnCompleted(!!0&,!!1&)",
                "Builder.AwaitUnsafeOnCompleted");
            Frame start = Frame.PerfMap(AsyncProfilerCallStackStitchBoundary.HostModuleName,
                "System.Runtime.CompilerServices.AsyncMethodBuilderCore::Start(!!0&)", "AsyncMethodBuilderCore.Start");
            var scenario = new StitchScenario
            {
                Kind = AsyncCallstackKind.StateMachineAsync,
                Sync = new[]
                {
                    work, inner, dispatcher, box, await, inner, start, kickoff,
                    outer, dispatcher, box, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart,
                },
                AsyncSegments = new[] { AsyncSegment.StateMachine(logicalOuter), AsyncSegment.StateMachine(logicalInner) },
                ExpectedStitched = new[] { work, logicalInner, logicalOuter, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart },
                ExpectedWithoutNormalization = new[]
                {
                    work, logicalInner, await, inner, start, kickoff, logicalOuter,
                    Frame.ThreadPoolDispatch, Frame.WorkerThreadStart,
                },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 4,
                    V1InlineFallbackUsed = 4,
                    V1InfrastructureFramesCollapsed = 4,
                    V1ReentrantFramesCollapsed = 8,
                },
                VerifyThreadTimeComputer = true,
                IncludeEventSourceEvents = false,
                AllowSupplementalSamples = true,
                VerifySampleWeights = true,
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync)]
        public void PerfMapSymbols_ZeroIpWithoutBoundary_PreservesPhysicalStackAndAppendsAsyncCallers(int kindValue)
        {
            var kind = (AsyncCallstackKind)kindValue;
            bool stateMachine = kind == AsyncCallstackKind.StateMachineAsync;
            Frame work = Frame.PerfMap(Frame.ApplicationModule, "Scenario::SampledWork()", "SampledWork");
            Frame current = Frame.PerfMap(Frame.ApplicationModule,
                stateMachine ? "Scenario+<CurrentAsync>d__1::MoveNext()" : "Scenario::CurrentAsync()", "Captured current");
            Frame parent = Frame.PerfMap(Frame.ApplicationModule,
                stateMachine ? "Scenario+<ParentAsync>d__2::MoveNext()" : "Scenario::ParentAsync()", "Captured parent");
            var scenario = new StitchScenario
            {
                Kind = kind,
                Sync = new[] { work, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart },
                Async = new[] { Frame.Zero, current, parent },
                ExpectedStitched = new[]
                {
                    work, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart,
                    stateMachine ? current.Logical("Scenario.CurrentAsync") : current,
                    stateMachine ? parent.Logical("Scenario.ParentAsync") : parent,
                },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    BoundariesNotFound = 2,
                    V1InlineFallbackUsed = stateMachine ? 2 : 0,
                },
                VerifyThreadTimeComputer = true,
                IncludeEventSourceEvents = false,
                AllowSupplementalSamples = true,
                VerifySampleWeights = true,
                AssertTransformContext = context =>
                    Assert.DoesNotContain(context.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.IncompleteRoot),
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync)]
        public void VersionedWholeBridge_RejectsAnOtherwiseAcceptedLayoutAtomically(int kindValue)
        {
            var kind = (AsyncCallstackKind)kindValue;
            StitchScenario scenario = CreateRuntimeResumptionBridgeScenario(kind);
            var profile = new AsyncProfilerRejectRuntimeBridgeProfile();
            scenario.RuntimeProfileForModule = module => AsyncProfilerRuntimeRuleProfileCache.IsHostModule(module)
                ? profile : AsyncProfilerRuntimeRuleProfile.Common;
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
            scenario.ExpectedWithoutInfrastructureCleanup = null;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;
            scenario.VerifyThreadTimeComputer = true;
            scenario.IncludeEventSourceEvents = false;
            scenario.AllowSupplementalSamples = true;
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void VersionedEntrySpineAndSchedulerAnchor_UseSharedOwnershipGuards(bool physicalCurrent, bool enableInfrastructureCleanup)
        {
            StitchScenario scenario = CreateProfilerEnvelopeScenario(physicalCurrent);
            UseExtendedRuntimeLayouts(scenario);
            Frame helper = Frame.CoreLib(AsyncProfilerExtendedRuntimeLayoutProfile.HelperName, "LayoutHelper");
            scenario.Sync = InsertEntryHelper(scenario.Sync, helper);
            scenario.ExpectedWithoutInfrastructureCleanup =
                InsertEntryHelper(scenario.ExpectedWithoutInfrastructureCleanup, helper);
            scenario.EnableInfrastructureCleanup = enableInfrastructureCleanup;
            if (enableInfrastructureCleanup)
            {
                scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed += 2;
                scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed += 2;
            }
            else
            {
                scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
                scenario.ExpectedWithoutInfrastructureCleanup = null;
                scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
                scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = 0;
                scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;
            }
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RuntimeProfiles_WholeStageSelectionUsesCapturedProcessEvenWithoutRetainedRuntimeFrames(bool conflicting)
        {
            var scenario = new StitchScenario
            {
                Sync = new[] { Frame.App("Scenario.Current"), Frame.V2Wrapper(0), Frame.V2Dispatch, Frame.App("Scenario.Caller") },
                Async = new[] { Frame.App("Scenario.Current"), Frame.App("Scenario.Parent") },
                ExpectedStitched = new[] { Frame.App("Scenario.Current"), Frame.App("Scenario.Parent"), Frame.App("Scenario.Caller") },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation { SegmentsProcessed = 2, V2PlumbingFramesCollapsed = 2 },
                ExpectedRuntimeProfile = conflicting ? AsyncProfilerRuntimeRuleProfile.Common : AsyncProfilerRuntimeRuleProfile.CoreClr11,
                VerifyThreadTimeComputer = true,
                IncludeEventSourceEvents = false,
                AllowSupplementalSamples = true,
                ConfigureTraceLog = log =>
                {
                    TraceModuleFile host = ConfigureCapturedRuntime(log, "11.0.0");
                    TraceProcess process = log.Processes.First(p => p.ProcessID == 1234);
                    if (conflicting)
                    {
                        TraceModuleFile other = process.LoadedModules.UniversalMapping(
                            @"C:\other\System.Private.CoreLib.dll", 0x220000, 0x221000, 100_001, null);
                        other.productVersion = "12.0.0";
                    }
                    var profiles = new AsyncProfilerRuntimeRuleProfileCache(log.ModuleFiles);
                    Assert.Same(AsyncProfilerRuntimeRuleProfile.CoreClr11, profiles.GetProfile(host.ModuleFileIndex));
                    AsyncProfilerRuntimeRuleProfile expected = conflicting
                        ? AsyncProfilerRuntimeRuleProfile.Common : AsyncProfilerRuntimeRuleProfile.CoreClr11;
                    Assert.Same(expected, profiles.GetProcessProfile(process.ProcessIndex));
                    Assert.Same(expected, profiles.GetProcessProfile(process.ProcessIndex));
                    Assert.Same(AsyncProfilerRuntimeRuleProfile.Common, profiles.GetProcessProfile(ProcessIndex.Invalid));
                },
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RuntimeResumptionBridge_CompletedV1Unwind_GroupsOnlyTheCompletionPrefix(bool cleanup)
        {
            StitchScenario callback = CreateCallbackCompletionBridgeScenario(
                AsyncCallstackKind.StateMachineAsync, includeAwaiterHelpers: true);
            Frame[] helpers = callback.Sync.Skip(callback.Sync.Length - 9).Take(7).ToArray();
            StitchScenario scenario = CreateV1CompletedUnwindYieldBridgeScenario(helpers);
            scenario.EnableInfrastructureCleanup = cleanup;
            scenario.ExpectedStitched = cleanup
                ? scenario.ExpectedStitched.Take(3).Concat(new[] { helpers[0].DispatcherFrames() })
                    .Concat(helpers.Skip(5)).Concat(scenario.ExpectedStitched.Skip(3)).ToArray()
                : scenario.ExpectedWithoutInfrastructureCleanup;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = cleanup ? 10 : 0;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = cleanup ? 10 : 0;
            scenario.AssertTransformContext = context =>
            {
                AsyncProfilerStitchedFrame unwind = Assert.Single(context.Frames.Where(frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompletionUnwind));
                CodeAddressIndex bridge = context.Frames.Single(frame =>
                    frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibThreadPoolDispatch).CodeAddress;
                Assert.False(context.HasVerifiedDispatchEntry(unwind.Context, bridge));
                Assert.False(context.HasVerifiedV1CompletionAnchor(unwind.Context, bridge));
                Assert.Equal(cleanup,
                    context.HasVerifiedV1CompletionAnchor(unwind.Context, bridge, allowCompletionUnwind: true));
                Assert.False(AsyncProfilerCallStackTransformHelpers.HasVerifiedAsyncAnchor(
                    context, context.Frames.IndexOf(unwind), unwind.Context));
                Assert.DoesNotContain(context.Frames, frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent ||
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompleting ||
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget);
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("missing-placement")]
        [InlineData("missing-boundary")]
        [InlineData("ambiguous")]
        [InlineData("profile-veto")]
        public void RuntimeResumptionBridge_CompletedV1Unwind_RequiresVerifiedOwnership(string obstruction)
        {
            StitchScenario callback = CreateCallbackCompletionBridgeScenario(
                AsyncCallstackKind.StateMachineAsync, includeAwaiterHelpers: true);
            Frame[] helpers = callback.Sync.Skip(callback.Sync.Length - 9).Take(7).ToArray();
            StitchScenario scenario = CreateV1CompletedUnwindYieldBridgeScenario(helpers);
            scenario.EnableInfrastructureCleanup = false;
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;
            scenario.AssertTransformContext = context =>
            {
                var frames = context.Frames.ToList();
                AsyncProfilerCallStack activation = Assert.Single(context.Segments);
                CodeAddressIndex dispatch = Enumerable.Range(0, context.TraceLog.CodeAddresses.Count)
                    .Select(i => (CodeAddressIndex)i).Single(ca =>
                        context.ClassifyBoundary(ca).Kind == AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher);
                var placements = new[] { new AsyncProfilerSegmentPlacement(activation, 0, frames.Count, dispatch: dispatch) };
                var diagnostics = new AsyncProfilerCallStackDiagnostics
                {
                    SegmentsProcessed = 1,
                    BoundariesNotFound = obstruction == "missing-boundary" ? 1 : 0,
                    V1CompletionAlignmentAmbiguous = obstruction == "ambiguous" ? 1 : 0,
                };
                var profile = new AsyncProfilerRejectRuntimeBridgeProfile();
                var state = new AsyncProfilerCallStackTransformState(
                    context.TraceLog, context.Segments, context.TimestampQpc, context.ClassifyBoundary, diagnostics, frames,
                    runtimeProfileResolver: obstruction == "profile-veto"
                        ? (Func<CodeAddressIndex, AsyncProfilerRuntimeRuleProfile>)(ca => profile) : null,
                    segmentPlacements: obstruction == "missing-placement" ? null : placements);
                CodeAddressIndex bridge = frames.First(frame => frame.Context == null).CodeAddress;
                Assert.Equal(obstruction == "profile-veto",
                    state.HasVerifiedV1CompletionAnchor(activation, bridge, allowCompletionUnwind: true));
                new AsyncProfilerInfrastructureCleanupTransform().Apply(state);
                Assert.Equal(context.Frames, frames);
                Assert.Equal(0, diagnostics.RuntimeResumptionBridgeFramesCollapsed);
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, false, false)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, false, true)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, true, false)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, true, true)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, false, false)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, false, true)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, true, false)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, true, true)]
        public void VersionedCompletionPair_ResumptionBridgeUsesTheChangedLayout(
            int kindValue, bool enableNormalization, bool enableInfrastructureCleanup)
        {
            var kind = (AsyncCallstackKind)kindValue;
            StitchScenario scenario = CreateRuntimeResumptionBridgeScenario(kind);
            UseExtendedCompletionLayout(scenario);
            scenario.EnableNormalization = enableNormalization;
            scenario.EnableInfrastructureCleanup = enableInfrastructureCleanup;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = enableInfrastructureCleanup ? 12 : 0;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = enableInfrastructureCleanup ? 12 : 0;
            if (!enableInfrastructureCleanup)
            {
                scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
                scenario.ExpectedWithoutInfrastructureCleanup = null;
            }
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void VersionedCompletionPair_CompletedV2TailUsesTheChangedSpanLength(bool enableNormalization, bool enableInfrastructureCleanup)
        {
            StitchScenario scenario = CreateV1AfterV2CompletionScenario();
            UseExtendedCompletionLayout(scenario);
            scenario.EnableNormalization = enableNormalization;
            scenario.EnableInfrastructureCleanup = enableInfrastructureCleanup;
            scenario.ExpectedDiagnostics.V2CompletionHandoffFramesCollapsed = enableNormalization ? 12 : 0;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = !enableNormalization && enableInfrastructureCleanup ? 12 : 0;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = !enableNormalization && enableInfrastructureCleanup ? 12 : 0;
            if (!enableNormalization && !enableInfrastructureCleanup)
            {
                scenario.ExpectedStitched = scenario.ExpectedWithoutNormalization;
            }
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync)]
        public void VersionedCompletionChain_RejectedBridgePreservesTheCompleteChain(int kindValue)
        {
            var kind = (AsyncCallstackKind)kindValue;
            StitchScenario scenario = CreateCallbackCompletionBridgeScenario(kind);
            UseExtendedCompletionLayout(scenario);
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, "unknown")]
        [InlineData(true, "unknown")]
        [InlineData(false, "unresolved")]
        [InlineData(true, "unresolved")]
        [InlineData(false, "foreign")]
        [InlineData(true, "foreign")]
        [InlineData(false, "application")]
        [InlineData(true, "application")]
        [InlineData(false, "other-corelib")]
        [InlineData(true, "other-corelib")]
        public void VersionedCompletionPair_RejectedHelperProtectsTheCompleteV2Tail(bool enableNormalization, string obstruction)
        {
            StitchScenario scenario = CreateV1AfterV2CompletionScenario();
            UseExtendedCompletionLayout(scenario);
            Frame helper = scenario.Sync.Single(frame => frame.SymbolName == AsyncProfilerExtendedCompletionRuntimeProfile.InjectedHelperName);
            if (obstruction == "unresolved")
            {
                scenario.OmitSymbolFor = helper;
            }
            else
            {
                Frame replacement = obstruction == "foreign"
                    ? Frame.Library("OtherLibrary", helper.SymbolName, "ForeignCompletionHelper")
                    : obstruction == "other-corelib"
                        ? Frame.Library(@"C:\other\System.Private.CoreLib.dll", helper.SymbolName, "OtherRuntimeCompletionHelper")
                    : obstruction == "application"
                        ? Frame.App("Scenario.ApplicationCallback")
                        : Frame.CoreLib("System.Threading.Tasks.Task.UnknownHelper", "UnknownCompletionHelper");
                scenario.Sync = scenario.Sync.Select(frame => frame == helper ? replacement : frame).ToArray();
                scenario.ExpectedWithoutNormalization = scenario.ExpectedWithoutNormalization
                    .Select(frame => frame.Key == helper.Key ? replacement : frame).ToArray();
            }
            scenario.EnableNormalization = enableNormalization;
            scenario.EnableInfrastructureCleanup = true;
            scenario.ExpectedStitched = scenario.ExpectedWithoutNormalization;
            scenario.ExpectedDiagnostics.V2CompletionHandoffFramesCollapsed = 0;
            scenario.AssertProductionStitch();
        }

        [Fact]
        public void VersionedCompletionPair_NativeAndIlAliasesShareOneRuntimeSequence()
        {
            StitchScenario scenario = CreateV1AfterV2CompletionScenario();
            UseExtendedCompletionLayout(scenario);
            Frame alias = Frame.Library(
                @"C:\runtime\System.Private.CoreLib.ni.dll",
                AsyncProfilerExtendedCompletionRuntimeProfile.InjectedHelperName, "NativeCompletionHelper");
            scenario.Sync = scenario.Sync.Select(frame =>
                frame.SymbolName == alias.SymbolName ? alias : frame).ToArray();
            scenario.ExpectedWithoutNormalization = scenario.ExpectedWithoutNormalization
                .Select(frame => frame.SymbolName == alias.SymbolName ? alias : frame).ToArray();
            scenario.ConfigureTraceLog = log =>
            {
                TraceModuleFile host = ConfigureCapturedRuntime(log, "11.0.0");
                log.ModuleFiles.Single(module => string.Equals(
                    module.Name, "System.Private.CoreLib.ni", StringComparison.OrdinalIgnoreCase)).managedModule = host;
            };
            scenario.ExpectedDiagnostics.V2CompletionHandoffFramesCollapsed = 12;
            scenario.AssertProductionStitch();
        }

        [Fact]
        public void CoreLibUnloadRundownAliases_CollapseStructuralPlumbingAndResumptionBridge()
        {
            StitchScenario scenario = CreateRuntimeResumptionBridgeScenario(AsyncCallstackKind.RuntimeAsync);
            scenario.IncludeCoreLibUnloadRundown = true;
            scenario.EnableNormalization = false;
            scenario.VerifyThreadTimeComputer = true;
            scenario.AllowSupplementalSamples = true;
            scenario.ExpectedWithoutNormalization = scenario.ExpectedWithoutInfrastructureCleanup;
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation
            {
                SegmentsProcessed = 5,
                V2PlumbingFramesCollapsed = 10,
                AsyncInfrastructureFramesCollapsed = 25,
                RuntimeResumptionBridgeFramesCollapsed = 25,
            };
            scenario.ConfigureTraceLog = log =>
            {
                TraceModuleFile[] modules = log.ModuleFiles.Where(module =>
                    module.Name == AsyncProfilerCallStackStitchBoundary.HostModuleName).ToArray();
                Assert.Equal(2, modules.Length);
                Assert.NotEqual(modules[0].ModuleFileIndex, modules[1].ModuleFileIndex);
                Assert.Equal(modules[0].FilePath, modules[1].FilePath);
                Assert.NotEqual(Guid.Empty, modules[0].PdbSignature);
                Assert.Equal(modules[0].PdbSignature, modules[1].PdbSignature);
                Assert.Null(modules[0].ManagedModule);
                Assert.Null(modules[1].ManagedModule);
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("path")]
        [InlineData("signature")]
        [InlineData("age")]
        [InlineData("missing-signature")]
        [InlineData("profile")]
        public void CoreLibUnloadRundownAliases_ConflictingOrMissingIdentityPreservesTheBridge(string obstruction)
        {
            StitchScenario scenario = CreateRuntimeResumptionBridgeScenario(AsyncCallstackKind.RuntimeAsync);
            scenario.IncludeCoreLibUnloadRundown = true;
            scenario.EnableNormalization = false;
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup.Take(3)
                .Concat(new[] { Frame.V2InstrumentedDispatch, Frame.V2Dispatch })
                .Concat(scenario.ExpectedWithoutInfrastructureCleanup.Skip(3)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = scenario.ExpectedStitched;
            scenario.ExpectedWithoutNormalization = scenario.ExpectedStitched;
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation { SegmentsProcessed = 2 };
            scenario.ConfigureTraceLog = log =>
            {
                TraceModuleFile alias = log.ModuleFiles.Last(module =>
                    module.Name == AsyncProfilerCallStackStitchBoundary.HostModuleName);
                if (obstruction == "path")
                {
                    alias.fileName = @"C:\other\System.Private.CoreLib.dll";
                }
                else if (obstruction == "signature")
                {
                    alias.PEInfo.PdbSignature = new Guid("fd85f1ed-6152-4ee3-bf58-917b38d65eaf");
                }
                else if (obstruction == "age")
                {
                    alias.PEInfo.PdbAge++;
                }
                else if (obstruction == "missing-signature")
                {
                    alias.PEInfo.PdbSignature = Guid.Empty;
                }
                else
                {
                    ConfigureCapturedRuntime(log, "11.0.0");
                    alias.productVersion = "12.0.0";
                }
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CoreLibUnloadRundownAliases_CompletionProjectionRequiresCompatibleProfiles(bool conflictingProfile)
        {
            Frame target = Frame.App("Scenario.TargetAsync");
            Frame[] sync = { Frame.V2InstrumentedDispatch, Frame.V2Dispatch, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart };
            var scenario = new StitchScenario
            {
                Sync = sync,
                Async = new[] { target },
                IncludeCoreLibUnloadRundown = true,
                CoreLibMethodBeforeUnload = Frame.V2InstrumentedDispatch,
                MethodCompletions = 1,
                MethodCompletionQpc = 100_030,
                EnableNormalization = false,
                IncludeEventSourceEvents = false,
                VerifySampleWeights = true,
                ExpectedStitched = conflictingProfile ? sync : new[]
                {
                    Frame.V2InstrumentedDispatch.DispatcherFrames(), target, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart,
                },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = conflictingProfile ? 0 : 2,
                    V2SyncLayoutUsed = conflictingProfile ? 2 : 0,
                    V2PlumbingFramesCollapsed = conflictingProfile ? 0 : 4,
                },
                ConfigureTraceLog = log =>
                {
                    ConfigureCapturedRuntime(log, "11.0.0");
                    TraceModuleFile alias = log.ModuleFiles.Last(module =>
                        module.Name == AsyncProfilerCallStackStitchBoundary.HostModuleName);
                    alias.productVersion = conflictingProfile ? "12.0.0" : "11.0.0";
                },
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("11.0.0", "coreclr.dll", true)]
        [InlineData("11.0.0-preview.1+abcdef", "libcoreclr.so", true)]
        [InlineData("11.0.0", "libcoreclr.dylib", true)]
        [InlineData("12.0.0", "coreclr.dll", false)]
        [InlineData(null, "coreclr.dll", false)]
        [InlineData("11.0.0", "libmonosgen-2.0.so", false)]
        [InlineData("11.0.0", null, false)]
        public void RuntimeProfiles_AreCachedPerCapturedModuleAcrossMixedRuntimes(
            string productVersion, string engineModule, bool supported)
        {
            var scenario = new StitchScenario
            {
                Sync = new[]
                {
                    Frame.App("Scenario.Current"), Frame.V2Wrapper(0), Frame.V2Dispatch,
                    Frame.ThreadPoolDispatch, Frame.WorkerThreadStart,
                },
                Async = new[] { Frame.App("Scenario.Current"), Frame.App("Scenario.Parent") },
                ExpectedStitched = new[]
                {
                    Frame.App("Scenario.Current"), Frame.App("Scenario.Parent"),
                    Frame.ThreadPoolDispatch, Frame.WorkerThreadStart,
                },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V2PlumbingFramesCollapsed = 2,
                },
                ConfigureTraceLog = log =>
                {
                    TraceModuleFile host = ConfigureCapturedRuntime(log, productVersion, engineModule);
                    TraceProcess otherProcess = log.Processes.GetOrCreateProcess(9876, 100_000);
                    TraceModuleFile other = otherProcess.LoadedModules.UniversalMapping(
                        @"C:\other\System.Private.CoreLib.dll", 0x200000, 0x201000, 100_001, null);
                    other.productVersion = "12.0.0";
                    otherProcess.LoadedModules.UniversalMapping(
                        @"C:\other\coreclr.dll", 0x210000, 0x211000, 100_001, null);

                    var profiles = new AsyncProfilerRuntimeRuleProfileCache(log.ModuleFiles);
                    AsyncProfilerRuntimeRuleProfile expected = supported
                        ? AsyncProfilerRuntimeRuleProfile.CoreClr11
                        : AsyncProfilerRuntimeRuleProfile.Common;
                    Assert.Same(expected, profiles.GetProfile(host.ModuleFileIndex));
                    Assert.Same(AsyncProfilerRuntimeRuleProfile.Common, profiles.GetProfile(other.ModuleFileIndex));
                    Assert.Same(expected, profiles.GetProcessProfile(
                        log.Processes.First(p => p.ProcessID == 1234).ProcessIndex));
                    Assert.Same(AsyncProfilerRuntimeRuleProfile.Common, profiles.GetProcessProfile(otherProcess.ProcessIndex));
                    Assert.Same(expected, profiles.GetProfile(host.ModuleFileIndex));
                    Assert.Same(AsyncProfilerRuntimeRuleProfile.Common, profiles.GetProfile(ModuleFileIndex.Invalid));

                    TraceModuleFile native = log.ModuleFiles.GetOrCreateModuleFile(
                        @"C:\runtime\System.Private.CoreLib.ni.dll", 0x220000);
                    native.managedModule = host;
                    Assert.True(profiles.IsHostModule(native.ModuleFileIndex));
                    Assert.Same(expected, profiles.GetProfile(native.ModuleFileIndex));
                    Assert.Same(expected, profiles.GetProfile(native.ModuleFileIndex));
                    var nativeFirstProfiles = new AsyncProfilerRuntimeRuleProfileCache(log.ModuleFiles);
                    Assert.Same(expected, nativeFirstProfiles.GetProfile(native.ModuleFileIndex));
                    Assert.Same(expected, nativeFirstProfiles.GetProfile(host.ModuleFileIndex));
                    Assert.Same(expected, nativeFirstProfiles.GetProfile(native.ModuleFileIndex));

                    ModuleFileIndex applicationModule = log.ModuleFiles.GetOrCreateModuleFile(
                        @"C:\app\NotCoreLib.dll", 0).ModuleFileIndex;
                    Assert.Same(AsyncProfilerRuntimeRuleProfile.Common, profiles.GetProfile(applicationModule));
                    Assert.Same(AsyncProfilerRuntimeRuleProfile.Common, profiles.GetProfile(applicationModule));
                    Assert.Same(AsyncProfilerRuntimeRuleProfile.Common, profiles.GetProfile(ModuleFileIndex.Invalid));
                },
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void RuntimeProfiles_ConflictingOrUnknownModuleOwnersUseCommonRules(
            bool sharedWithAnotherProcess, bool unknownFlavor)
        {
            var scenario = new StitchScenario
            {
                Sync = new[] { Frame.App("Scenario.Current"), Frame.V2Wrapper(0), Frame.ThreadPoolDispatch },
                Async = new[] { Frame.App("Scenario.Current"), Frame.App("Scenario.Parent") },
                ExpectedStitched = new[] { Frame.App("Scenario.Current"), Frame.App("Scenario.Parent"), Frame.ThreadPoolDispatch },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation { SegmentsProcessed = 2 },
                ConfigureTraceLog = log =>
                {
                    TraceModuleFile host = ConfigureCapturedRuntime(log, "11.0.0");
                    TraceProcess process = sharedWithAnotherProcess
                        ? log.Processes.GetOrCreateProcess(9876, 100_000)
                        : log.Processes.First(p => p.ProcessID == 1234);
                    if (sharedWithAnotherProcess)
                    {
                        process.LoadedModules.UniversalMapping(
                            host.FilePath, host.ImageBase, host.ImageEnd, 100_001, null);
                    }
                    if (!unknownFlavor || !sharedWithAnotherProcess)
                    {
                        process.LoadedModules.UniversalMapping(
                            unknownFlavor ? @"C:\runtime\clr.dll" : @"C:\runtime\libmonosgen-2.0.so",
                            0x210000, 0x211000, 100_001, null);
                    }
                    Assert.Same(AsyncProfilerRuntimeRuleProfile.Common,
                        new AsyncProfilerRuntimeRuleProfileCache(log.ModuleFiles).GetProfile(host.ModuleFileIndex));
                },
            };
            scenario.AssertProductionStitch();
        }

        [Fact]
        public void RuntimeAsync_ThreadPoolDispatch_StitchesSuspendedCallers()
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
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    // Both incoming CPU samples are stitched; the second sample causes the first sample's
                    // thread-time interval to be emitted.
                    SegmentsProcessed = 2,
                    V2PlumbingFramesCollapsed = 4,
                },
                VerifyComputerAndIndexLifecycle = true,
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RuntimeAsync_ParentContexts_RendersParentContextMetadata(bool enableFrameAnnotations)
        {
            Frame current = Frame.App("Scenario.ChildAsync");
            Frame childParent = Frame.App("Scenario.ChildParentAsync");
            Frame ownerCurrent = Frame.App("Scenario.OwnerCurrentAsync");
            Frame ownerRoot = Frame.App("Scenario.OwnerRootAsync");
            var scenario = new StitchScenario
            {
                Sync = new[]
                {
                    Frame.App("Scenario.DoWork"),
                    current,
                    Frame.V2Wrapper(0),
                    Frame.V2InstrumentedDispatch,
                    Frame.V2Dispatch,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                Async = new[]
                {
                    current,
                    childParent,
                },
                ContextParent = AsyncSegment.Runtime(ownerCurrent, ownerRoot),
                EnableParentContexts = true,
                EnableFrameAnnotations = enableFrameAnnotations,
                VerifyRemovingParentContextRemovesPresentation = true,
                ExpectedStitched = new[]
                {
                    Frame.App("Scenario.DoWork"),
                    current,
                    childParent,
                    ownerCurrent,
                    ownerRoot,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V2PlumbingFramesCollapsed = 4,
                },
                AssertOutputs = (normal, stitched) =>
                {
                    Assert.Equal(
                        new[]
                        {
                            "[Async resumed parent context]",
                            "[Async resumed parent context]",
                        },
                        stitched.GroupingFrames.Where(
                            frame => string.Equals(
                                frame,
                                "[Async resumed parent context]",
                                StringComparison.Ordinal)));
                },
                AssertStitchedStackSource = stackSource =>
                {
                    bool foundParentContext = false;
                    bool foundActiveAsync = false;
                    stackSource.ForEach(sample =>
                    {
                        for (StackSourceCallStackIndex stack = sample.StackIndex;
                             stack != StackSourceCallStackIndex.Invalid;
                             stack = stackSource.GetCallerIndex(stack))
                        {
                            StackSourceFrameIndex frameIndex = stackSource.GetFrameIndex(stack);
                            string name = stackSource.GetFrameName(frameIndex, false);
                            if (!stackSource.TryGetAsyncProfilerCallStackFrameInfo(
                                frameIndex, out AsyncProfilerCallStackSourceFrameInfo info))
                            {
                                continue;
                            }

                            if (info.IsParentContext || enableFrameAnnotations)
                            {
                                Assert.StartsWith("[Async V2] ", name);
                                Assert.DoesNotContain("![Async V2] ", name);
                            }
                            else
                            {
                                Assert.DoesNotContain("[Async V2] ", name);
                            }
                            foundParentContext |= info.IsParentContext;
                            foundActiveAsync |= !info.IsParentContext;
                        }
                    });
                    Assert.True(foundParentContext);
                    Assert.True(foundActiveAsync);
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, false, true)]
        [InlineData(false, true, false)]
        [InlineData(false, true, true)]
        [InlineData(true, false, false)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        [InlineData(true, true, true)]
        public void ContextParent_RecordedProgressPreservesCapturedMetadataInBothHosts(
            bool stateMachineParent, bool enableFrameAnnotations, bool enableInfrastructureCleanup)
        {
            Frame current = Frame.App("Scenario.ChildAsync");
            Frame ownerCurrent = Frame.App(stateMachineParent
                ? "Scenario+<OwnerCurrentAsync>d__3.MoveNext" : "Scenario.OwnerCurrentAsync");
            Frame ownerRoot = Frame.App(stateMachineParent
                ? "Scenario+<OwnerRootAsync>d__2.MoveNext" : "Scenario.OwnerRootAsync");
            Frame[] parentFrames =
            {
                Frame.App("Scenario.CompletedAsync"),
                Frame.App("Scenario.UnwoundAsync"),
                ownerCurrent,
                ownerRoot,
            };
            var parentKind = stateMachineParent
                ? AsyncCallstackKind.StateMachineAsync : AsyncCallstackKind.RuntimeAsync;
            var scenario = new StitchScenario
            {
                Sync = new[]
                {
                    current, Frame.V2Wrapper(0), Frame.V2InstrumentedDispatch, Frame.V2Dispatch,
                    Frame.ThreadPoolDispatch, Frame.WorkerThreadStart,
                },
                Async = new[] { current },
                ContextParent = new AsyncSegment(parentKind, parentFrames, new[] { 0, 1, 4, 7 }),
                ParentMethodCompletions = 1,
                ParentExceptionCompletions = 1,
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V2PlumbingFramesCollapsed = 4,
                },
                EnableParentContexts = true,
                EnableFrameAnnotations = enableFrameAnnotations,
                EnableInfrastructureCleanup = enableInfrastructureCleanup,
                IncludeEventSourceEvents = false,
                VerifyThreadTimeComputer = true,
                VerifySampleWeights = true,
                ExpectedStitched = new[]
                {
                    current,
                    stateMachineParent ? ownerCurrent.Logical("Scenario.OwnerCurrentAsync", -1) : ownerCurrent,
                    stateMachineParent ? ownerRoot.Logical("Scenario.OwnerRootAsync", 7) : ownerRoot,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                AssertTransformContext = context =>
                {
                    AsyncProfilerStitchedFrame[] parents = context.Frames.Where(frame =>
                        frame.ContextKind == AsyncProfilerStitchedFrameContextKind.ParentContext).ToArray();
                    Assert.Equal(new[] { 2, 3 }, parents.Select(frame => frame.SegmentFrameIndex));
                    Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncContextParentCurrent, parents[0].Origin);
                    Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncContextParent, parents[1].Origin);
                },
                AssertStitchedStackSource = stackSource =>
                {
                    int parents = 0;
                    int runningParents = 0;
                    for (int i = 0; i < stackSource.CallFrameIndexLimit; i++)
                    {
                        var frameIndex = (StackSourceFrameIndex)i;
                        if (!stackSource.TryGetAsyncProfilerCallStackFrameInfo(
                            frameIndex, out AsyncProfilerCallStackSourceFrameInfo info) || !info.IsParentContext)
                        {
                            continue;
                        }
                        parents++;
                        Assert.Equal(parentKind, info.Kind);
                        Assert.False(info.IsCurrent);
                        Assert.False(info.IsCompleting);
                        Assert.False(info.IsCompletionUnwind);
                        Assert.False(info.IsDispatchTarget);
                        Assert.Equal(info.CodeAddress, stackSource.GetFrameCodeAddress(frameIndex));
                        Assert.Equal(stackSource.TraceLog.CodeAddresses[info.CodeAddress].Address, info.MethodId);
                        string name = stackSource.GetFrameName(frameIndex, false);
                        if (info.IsParentContextCurrent)
                        {
                            runningParents++;
                            Assert.Contains(ownerCurrent.SymbolName,
                                stackSource.TraceLog.CodeAddresses[info.CodeAddress].FullMethodName);
                            Assert.Equal(stateMachineParent ? 4 : 0, info.State);
                            if (stateMachineParent)
                            {
                                Assert.StartsWith("[Async V1] ", name);
                                Assert.EndsWith("[ordinal 3, state -1]", name);
                            }
                            else
                            {
                                Assert.StartsWith("[Async V2] ", name);
                                Assert.DoesNotContain("state ", name);
                            }
                        }
                        else
                        {
                            Assert.Equal(stateMachineParent ? 7 : 0, info.State);
                        }
                    }
                    Assert.Equal(2, parents);
                    Assert.Equal(1, runningParents);
                },
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void RuntimeAsync_ActiveAnnotations_DoNotAddParentTransitions()
        {
            Frame current = Frame.App("Scenario.CurrentAsync");
            Frame parent = Frame.App("Scenario.ParentAsync");
            var scenario = new StitchScenario
            {
                Sync = new[]
                {
                    Frame.App("Scenario.DoWork"),
                    current,
                    Frame.V2Wrapper(0),
                    Frame.V2InstrumentedDispatch,
                    Frame.V2Dispatch,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                Async = new[] { current, parent },
                EnableFrameAnnotations = true,
                ExpectedStitched = new[]
                {
                    Frame.App("Scenario.DoWork"),
                    current,
                    parent,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V2PlumbingFramesCollapsed = 4,
                },
                AssertOutputs = (normal, stitched) =>
                    Assert.DoesNotContain(
                        "[Async resumed parent context]",
                        stitched.GroupingFrames),
                AssertStitchedStackSource = stackSource =>
                {
                    bool foundAnnotatedCurrent = false;
                    bool foundUnannotatedSyncCallee = false;
                    stackSource.ForEach(sample =>
                    {
                        for (StackSourceCallStackIndex stack = sample.StackIndex;
                             stack != StackSourceCallStackIndex.Invalid;
                             stack = stackSource.GetCallerIndex(stack))
                        {
                            StackSourceFrameIndex frameIndex = stackSource.GetFrameIndex(stack);
                            string name = stackSource.GetFrameName(frameIndex, false);
                            if (name.IndexOf("Scenario.DoWork", StringComparison.Ordinal) >= 0)
                            {
                                Assert.DoesNotContain("[Async V2] ", name);
                                foundUnannotatedSyncCallee = true;
                            }

                            if (name.IndexOf("[Async V2] ", StringComparison.Ordinal) < 0)
                            {
                                continue;
                            }

                            Assert.StartsWith("[Async V2] ", name);
                            Assert.DoesNotContain("![Async V2] ", name);
                            if (name.IndexOf("Scenario.CurrentAsync", StringComparison.Ordinal) >= 0)
                            {
                                foundAnnotatedCurrent = true;
                            }
                            Assert.True(stackSource.TryGetAsyncProfilerCallStackFrameInfo(
                                frameIndex, out AsyncProfilerCallStackSourceFrameInfo info));
                            Assert.False(info.IsParentContext);
                        }
                    });
                    Assert.True(foundAnnotatedCurrent);
                    Assert.True(foundUnannotatedSyncCallee);
                },
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void RuntimeAsync_IdenticalRecursiveContextParent_IsRendered()
        {
            Frame current = Frame.App("Scenario.CurrentAsync");
            Frame parent = Frame.App("Scenario.ParentAsync");
            var scenario = new StitchScenario
            {
                Sync = new[]
                {
                    Frame.App("Scenario.DoWork"),
                    current,
                    Frame.V2Wrapper(0),
                    Frame.V2InstrumentedDispatch,
                    Frame.V2Dispatch,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                Async = new[] { current, parent },
                ContextParent = AsyncSegment.Runtime(current, parent),
                EnableParentContexts = true,
                EnableFrameAnnotations = true,
                ExpectedStitched = new[]
                {
                    Frame.App("Scenario.DoWork"),
                    current,
                    parent,
                    current,
                    parent,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V2PlumbingFramesCollapsed = 4,
                },
                AssertOutputs = (normal, stitched) =>
                {
                    Assert.Contains(
                        "[Async resumed parent context]",
                        stitched.GroupingFrames);
                },
                AssertStitchedStackSource = stackSource =>
                {
                    stackSource.ForEach(sample =>
                    {
                        StackSourceFrameIndex activeParent = StackSourceFrameIndex.Invalid;
                        StackSourceFrameIndex parentContext = StackSourceFrameIndex.Invalid;
                        for (StackSourceCallStackIndex stack = sample.StackIndex;
                             stack != StackSourceCallStackIndex.Invalid;
                             stack = stackSource.GetCallerIndex(stack))
                        {
                            StackSourceFrameIndex frameIndex = stackSource.GetFrameIndex(stack);
                            if (stackSource.GetFrameName(frameIndex, false).IndexOf(
                                "Scenario.ParentAsync", StringComparison.Ordinal) < 0 ||
                                !stackSource.TryGetAsyncProfilerCallStackFrameInfo(
                                    frameIndex, out AsyncProfilerCallStackSourceFrameInfo info))
                            {
                                continue;
                            }
                            if (info.IsParentContext)
                            {
                                parentContext = frameIndex;
                            }
                            else
                            {
                                activeParent = frameIndex;
                            }
                        }
                        Assert.NotEqual(StackSourceFrameIndex.Invalid, activeParent);
                        Assert.NotEqual(StackSourceFrameIndex.Invalid, parentContext);
                        Assert.NotEqual(activeParent, parentContext);
                        Assert.Equal(
                            stackSource.GetFrameName(activeParent, false),
                            stackSource.GetFrameName(parentContext, false));
                    });
                },
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void RuntimeAsync_ReplacesTplReconstructionAndPreservesStartStopGrouping()
        {
            Frame taskExecute = Frame.CoreLib("System.Threading.Tasks.Task.Execute", "Task.Execute");
            var scenario = new StitchScenario
            {
                Sync = new[]
                {
                    Frame.App("Scenario.DoWork"),
                    Frame.App("Scenario.CurrentAsync"),
                    Frame.V2Wrapper(0),
                    Frame.V2InstrumentedDispatch,
                    Frame.V2Dispatch,
                    taskExecute,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                Async = new[]
                {
                    Frame.App("Scenario.CurrentAsync"),
                    Frame.App("Scenario.ParentAsync"),
                    Frame.App("Program.Main"),
                },
                ExpectedStitched = new[]
                {
                    Frame.App("Scenario.DoWork"),
                    Frame.App("Scenario.CurrentAsync"),
                    Frame.App("Scenario.ParentAsync"),
                    Frame.App("Program.Main"),
                    taskExecute,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V2PlumbingFramesCollapsed = 4,
                },
                IncludeTplActivity = true,
                IncludeStartStopActivity = true,
                IncludeEventSourceEvents = false,
                AllowSupplementalSamples = true,
                SkipNormalStackAssertion = true,
                VerifyThreadTimeComputer = true,
                AssertOutputs = (normal, stitched) =>
                {
                    Assert.Contains(normal.GroupingFrames, name =>
                        name.IndexOf("STARTING TASK", StringComparison.Ordinal) >= 0);
                    Assert.DoesNotContain(stitched.GroupingFrames, name =>
                        name.IndexOf("STARTING TASK", StringComparison.Ordinal) >= 0);
                    Assert.Contains(normal.RootFrames, name =>
                        name.IndexOf("Activity Request", StringComparison.Ordinal) >= 0);
                    Assert.Contains(stitched.RootFrames, name =>
                        name.IndexOf("Activity Request", StringComparison.Ordinal) >= 0);
                },
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void RuntimeAsync_SameIpAsSyncFrame_RetainsDistinctFrameIdentity()
        {
            Frame repeated = Frame.App("Scenario.RepeatedWork");
            Frame current = Frame.App("Scenario.CurrentAsync");
            var scenario = new StitchScenario
            {
                Sync = new[]
                {
                    repeated,
                    current,
                    Frame.V2Wrapper(0),
                    Frame.V2InstrumentedDispatch,
                    Frame.V2Dispatch,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                Async = new[]
                {
                    current,
                    repeated,
                    Frame.App("Program.Main"),
                },
                ExpectedStitched = new[]
                {
                    repeated,
                    current,
                    repeated,
                    Frame.App("Program.Main"),
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V2PlumbingFramesCollapsed = 4,
                },
                AssertStitchedStackSource = stackSource =>
                {
                    var repeatedFrames =
                        new List<(StackSourceFrameIndex Index, CodeAddressIndex CodeAddress, bool IsAsync)>();
                    stackSource.ForEach(sample =>
                    {
                        StackSourceCallStackIndex stackIndex = sample.StackIndex;
                        while (stackIndex != StackSourceCallStackIndex.Invalid)
                        {
                            StackSourceFrameIndex frameIndex = stackSource.GetFrameIndex(stackIndex);
                            string name = stackSource.GetFrameName(frameIndex, false);
                            if (name.Contains(repeated.SymbolName))
                            {
                                bool isAsync = stackSource.TryGetAsyncProfilerCallStackFrameInfo(
                                    frameIndex, out AsyncProfilerCallStackSourceFrameInfo info);
                                repeatedFrames.Add(
                                    (frameIndex, stackSource.GetFrameCodeAddress(frameIndex), isAsync));
                                if (isAsync)
                                {
                                    Assert.Equal(AsyncCallstackKind.RuntimeAsync, info.Kind);
                                    Assert.Equal(0, info.State);
                                }
                            }
                            stackIndex = stackSource.GetCallerIndex(stackIndex);
                        }
                    });

                    Assert.Equal(2, repeatedFrames.Count);
                    Assert.NotEqual(repeatedFrames[0].Index, repeatedFrames[1].Index);
                    Assert.Equal(repeatedFrames[0].CodeAddress, repeatedFrames[1].CodeAddress);
                    Assert.Single(repeatedFrames.Where(frame => frame.IsAsync));
                    Assert.Single(repeatedFrames.Where(frame => !frame.IsAsync));
                },
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void RuntimeAsync_DeepResume_StitchesRealCapturedAsyncFrames()
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
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
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
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
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

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, false, true)]
        [InlineData(false, true, false)]
        [InlineData(false, true, true)]
        [InlineData(true, false, false)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        [InlineData(true, true, true)]
        public void RuntimeAsync_WrapperIsSampledLeaf_ProjectsDispatchTarget(
            bool enableFrameAnnotations, bool enableNormalization, bool enableInfrastructureCleanup)
        {
            Frame[] logical =
            {
                Frame.App("Scenario.Level3Async"),
                Frame.App("Scenario.Level2Async"),
                Frame.App("Program.Main"),
            };
            Frame[] root = { Frame.ThreadPoolDispatch, Frame.WorkerThreadStart };
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
                Async = logical,
                ExpectedStitched = logical.Concat(root).ToArray(),
                EnableFrameAnnotations = enableFrameAnnotations,
                EnableNormalization = enableNormalization,
                EnableInfrastructureCleanup = enableInfrastructureCleanup,
                EnableParentContexts = true,
                IncludeEventSourceEvents = false,
                VerifySampleWeights = true,
                VerifyThreadTimeComputer = true,
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V2PlumbingFramesCollapsed = 4,
                    V2LeafWrapperDropped = 2,
                },
                AssertTransformContext = context =>
                {
                    AsyncProfilerStitchedFrame target = Assert.Single(context.Frames.Where(frame =>
                        frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget));
                    Assert.Equal(0, target.SegmentFrameIndex);
                    Assert.Equal(AsyncProfilerStitchedFramePresentation.Native, target.Presentation);
                    Assert.False(target.IsStateMachineBoxCarrier);
                    Assert.Same(context.Segments[0], target.Context);
                    Assert.DoesNotContain(context.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
                    Assert.Equal(0,
                        context.Frames.Count(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                            context.ClassifyBoundary(frame.CodeAddress).Kind ==
                                AsyncProfilerCallStackStitchBoundaryKind.V2ContinuationWrapper));
                    Assert.Equal(0,
                        context.Frames.Count(frame =>
                            frame.Presentation == AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames));
                },
                AssertStitchedStackSource = stackSource =>
                {
                    int targets = 0;
                    for (int i = 0; i < stackSource.CallFrameIndexLimit; i++)
                    {
                        var frameIndex = (StackSourceFrameIndex)i;
                        if (!stackSource.TryGetAsyncProfilerCallStackFrameInfo(frameIndex, out AsyncProfilerCallStackSourceFrameInfo info) ||
                            !info.IsDispatchTarget)
                        {
                            continue;
                        }
                        targets++;
                        Assert.Equal(AsyncCallstackKind.RuntimeAsync, info.Kind);
                        Assert.False(info.IsCurrent);
                        Assert.False(info.IsCompleting);
                        Assert.False(info.IsStateMachineBoxCarrier);
                        Assert.False(info.IsParentContext);
                        Assert.NotEqual(0UL, info.MethodId);
                        Assert.Equal(info.CodeAddress, stackSource.GetFrameCodeAddress(frameIndex));
                        string name = stackSource.GetFrameName(frameIndex, false);
                        Assert.Contains("testapp!Scenario.Level3Async", name);
                        Assert.Equal(enableFrameAnnotations, name.StartsWith("[Async V2] ", StringComparison.Ordinal));
                        Assert.DoesNotContain("state ", name);
                    }
                    Assert.Equal(1, targets);
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void RuntimeAsync_ZeroIpInitialEntry_IsStructuralAndPreservesNativeCallees(
            bool enableNormalization, bool enableInfrastructureCleanup)
        {
            Frame native = Frame.Library("coreclr", "Runtime.NativeWork", "NativeWork");
            Frame entry = Frame.CoreLib(
                "System.Runtime.CompilerServices.RuntimeAsyncTaskContinuation+TaskContinuationResume.ResumeTaskContinuation",
                "ResumeTaskContinuation");
            Frame[] tail =
            {
                Frame.CoreLib("System.Threading.Tasks.Task.RunContinuations", "Task.RunContinuations"),
                Frame.CoreLib("System.Threading.Tasks.Task`1.TrySetResult", "Task.TrySetResult"),
                Frame.V2InstrumentedDispatch, Frame.V2Dispatch,
                Frame.ThreadPoolDispatch, Frame.WorkerThreadStart,
            };
            var scenario = new StitchScenario
            {
                Sync = new[] { native, entry, Frame.V2Wrapper(0), Frame.V2InstrumentedDispatch, Frame.V2Dispatch }
                    .Concat(tail).ToArray(),
                Async = new[] { Frame.Zero, Frame.App("Scenario.ParallelWhenAllAsync") },
                ContextParent = AsyncSegment.Runtime(Frame.App("Scenario.OwnerAsync")),
                ExpectedStitched = new[] { native, entry.DispatcherFrames() }.Concat(tail).ToArray(),
                EnableNormalization = enableNormalization,
                EnableInfrastructureCleanup = enableInfrastructureCleanup,
                EnableFrameAnnotations = true,
                EnableParentContexts = true,
                IncludeEventSourceEvents = false,
                VerifySampleWeights = true,
                VerifyThreadTimeComputer = true,
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V2PlumbingFramesCollapsed = 4,
                },
                AssertTransformContext = context =>
                {
                    Assert.All(context.Frames, frame =>
                        Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, frame.Origin));
                    AsyncProfilerStitchedFrame marker = Assert.Single(context.Frames.Where(frame =>
                        frame.Presentation == AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames));
                    Assert.NotEqual(CodeAddressIndex.Invalid, marker.CodeAddress);
                    Assert.NotEqual(MethodIndex.Invalid, marker.Method);
                    Assert.Same(context.Segments[0], marker.Context);
                    Assert.Equal(0, marker.Context.GetCompletedFrameCount(context.TimestampQpc));
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(2, 3, 0, 0, 0, 1)]
        [InlineData(0, 0, 1, 0, 0, 1)]
        [InlineData(0, 2, 0, 2, 0, 2)]
        [InlineData(0, 0, 1, 1, 0, 2)]
        [InlineData(0, 1, 0, 0, 1, 33)]
        public void RuntimeAsync_WrapperIsSampledLeaf_UsesActivationProgress(
            byte continuationBase, int slot, int methodCompletions, int exceptionCompletions,
            int wrapperResets, int expectedIndex)
        {
            Frame[] logical = Enumerable.Range(0, 36)
                .Select(i => Frame.App("Scenario.Level" + i + "Async")).ToArray();
            var scenario = new StitchScenario
            {
                Sync = new[]
                {
                    Frame.V2Wrapper(slot), Frame.V2InstrumentedDispatch, Frame.V2Dispatch,
                    Frame.ThreadPoolDispatch, Frame.WorkerThreadStart,
                },
                Async = logical,
                ContinuationIndexBase = continuationBase,
                MethodCompletions = methodCompletions,
                ExceptionCompletions = exceptionCompletions,
                WrapperResets = wrapperResets,
                ExpectedStitched = logical.Skip(expectedIndex)
                    .Concat(new[] { Frame.ThreadPoolDispatch, Frame.WorkerThreadStart }).ToArray(),
                EnableFrameAnnotations = true,
                IncludeEventSourceEvents = false,
                VerifySampleWeights = true,
                VerifyThreadTimeComputer = true,
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V2PlumbingFramesCollapsed = 4,
                    V2LeafWrapperDropped = 2,
                },
                AssertTransformContext = context =>
                {
                    AsyncProfilerStitchedFrame target = Assert.Single(context.Frames.Where(frame =>
                        frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget));
                    Assert.Equal(expectedIndex, target.SegmentFrameIndex);
                    Assert.DoesNotContain(context.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
                    Assert.Equal(logical.Length - expectedIndex - 1,
                        context.Frames.Count(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncRemaining));
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RuntimeAsync_WrapperIsSampledLeaf_ParentContextContextsRequireDispatchTarget(bool targetKnown)
        {
            Frame target = Frame.App("Scenario.InnerAsync");
            Frame parent = Frame.App("Scenario.ParentAsync");
            Frame owner = Frame.App("Scenario.OwnerAsync");
            Frame[] root = { Frame.ThreadPoolDispatch, Frame.WorkerThreadStart };
            var scenario = new StitchScenario
            {
                Sync = new[]
                {
                    Frame.V2Wrapper(0), Frame.V2InstrumentedDispatch, Frame.V2Dispatch,
                    Frame.ThreadPoolDispatch, Frame.WorkerThreadStart,
                },
                Async = new[] { targetKnown ? target : Frame.Zero, parent },
                ContextParent = AsyncSegment.Runtime(owner),
                EnableParentContexts = true,
                EnableFrameAnnotations = true,
                IncludeEventSourceEvents = false,
                VerifySampleWeights = true,
                VerifyThreadTimeComputer = true,
                ExpectedStitched = (targetKnown ? new[] { target, parent, owner }
                    : new[] { Frame.V2InstrumentedDispatch, Frame.V2Dispatch }).Concat(root).ToArray(),
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = targetKnown ? 2 : 0,
                    V2PlumbingFramesCollapsed = targetKnown ? 4 : 0,
                    V2SyncLayoutUsed = targetKnown ? 0 : 2,
                    V2LeafWrapperDropped = 2,
                },
                AssertTransformContext = context =>
                {
                    Assert.Equal(targetKnown ? 1 : 0,
                        context.Frames.Count(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget));
                    Assert.Equal(targetKnown ? 1 : 0,
                        context.Frames.Count(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncContextParent));
                    Assert.DoesNotContain(context.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, false, false, false)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, false, false, true)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, false, true, false)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, false, true, true)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, true, false, false)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, true, false, true)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, true, true, false)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, true, true, true)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, false, false, false)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, false, false, true)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, false, true, false)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, false, true, true)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, true, false, false)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, true, false, true)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, true, true, false)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, true, true, true)]
        public void RuntimeAsync_InnerWrapperIsSampledLeaf_FoldsOnlyVerifiedRuntimeBridge(
            int outerKindValue, bool enableNormalization, bool callback, bool targetKnown)
        {
            var outerKind = (AsyncCallstackKind)outerKindValue;
            Frame innerTarget = Frame.App("Scenario.InnerAsync");
            Frame innerParent = Frame.App("Scenario.InnerParentAsync");
            Frame outerIdentity = outerKind == AsyncCallstackKind.RuntimeAsync
                ? Frame.App("Scenario.NestedDispatcherOuterAsync")
                : Frame.App("Scenario+<NestedDispatcherOuterAsync>d__1.MoveNext");
            Frame outerParent = outerKind == AsyncCallstackKind.RuntimeAsync
                ? Frame.App("Scenario.OuterParentAsync")
                : Frame.App("Scenario+<OuterParentAsync>d__2.MoveNext");
            Frame[] entry = { Frame.V2Wrapper(1), Frame.V2InstrumentedDispatch, Frame.V2Dispatch };
            var bridge = new List<Frame>
            {
                Frame.CoreLib("System.Threading.Tasks.Task.RunContinuations(class System.Object)", "Task.RunContinuations"),
                Frame.CoreLib("System.Threading.Tasks.Task`1[System.Boolean].TrySetResult(!0)", "Task.TrySetResult"),
                Frame.CoreLib("System.Threading.Tasks.TaskCompletionSource`1[System.Boolean].SetResult(!0)", "TaskCompletionSource.SetResult"),
            };
            if (callback)
            {
                bridge.Add(Frame.App("Scenario.SynchronousCallback"));
            }
            Frame[] root = { Frame.ThreadPoolDispatch, Frame.WorkerThreadStart };
            Frame[] outerDispatch = outerKind == AsyncCallstackKind.RuntimeAsync
                ? new[] { Frame.V2Wrapper(0), Frame.V2InstrumentedDispatch, Frame.V2Dispatch }
                : new[] { Frame.V1MoveNextAsDispatcher };
            Frame[] logical = outerKind == AsyncCallstackKind.RuntimeAsync
                ? new[] { outerIdentity, outerParent }
                : new[]
                {
                    outerIdentity.Logical("Scenario.NestedDispatcherOuterAsync", -1),
                    outerParent.Logical("Scenario.OuterParentAsync", 5),
                };
            IEnumerable<Frame> inner = targetKnown
                ? new[] { innerTarget, innerParent }
                : (enableNormalization ? entry.Skip(1) : entry);
            bool fold = enableNormalization && targetKnown && !callback;
            Frame[] expected = inner
                .Concat(fold ? Enumerable.Empty<Frame>() : bridge).Concat(logical).Concat(root).ToArray();
            var scenario = new StitchScenario
            {
                Sync = entry.Concat(bridge).Concat(new[] { outerIdentity }).Concat(outerDispatch).Concat(root).ToArray(),
                AsyncSegments = new[]
                {
                    new AsyncSegment(outerKind, new[] { outerIdentity, outerParent }, new[] { 0, 5 }),
                    AsyncSegment.Runtime(Frame.Zero, targetKnown ? innerTarget : Frame.Zero, innerParent),
                },
                ExpectedStitched = expected,
                EnableFrameAnnotations = true,
                EnableNormalization = enableNormalization,
                EnableParentContexts = true,
                OnlyManagedCodeStacks = true,
                IncludeEventSourceEvents = false,
                SkipNormalStackAssertion = true,
                VerifySampleWeights = true,
                VerifyThreadTimeComputer = true,
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 4,
                    V2SyncLayoutUsed = targetKnown ? 0 : 2,
                    V2LeafWrapperDropped = targetKnown || enableNormalization ? 2 : 0,
                    V2PlumbingFramesCollapsed = (outerKind == AsyncCallstackKind.RuntimeAsync ? 4 : 0) +
                        (targetKnown ? 4 : 0),
                    V1InlineFallbackUsed = outerKind == AsyncCallstackKind.StateMachineAsync ? 2 : 0,
                    AsyncReentrantHandoffFramesCollapsed = fold ? 6 : 0,
                    V2ReentrantHandoffFramesCollapsed = fold && outerKind == AsyncCallstackKind.RuntimeAsync ? 6 : 0,
                },
                AssertTransformContext = context =>
                {
                    AsyncProfilerStitchedFrame outerCurrent = Assert.Single(context.Frames.Where(frame =>
                        frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent));
                    Assert.Same(context.Segments[0], outerCurrent.Context);
                    if (targetKnown)
                    {
                        AsyncProfilerStitchedFrame target = Assert.Single(context.Frames.Where(frame =>
                            frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget));
                        Assert.Same(context.Segments[1], target.Context);
                        Assert.Equal(1, target.SegmentFrameIndex);
                    }
                    else
                    {
                        Assert.DoesNotContain(context.Frames, frame => ReferenceEquals(frame.Context, context.Segments[1]));
                        Assert.Equal(enableNormalization ? 2 : 3, context.Frames.TakeWhile(frame => frame.Context == null).Count());
                    }
                    Assert.Equal(fold ? 3 : 0, context.Diagnostics.AsyncReentrantHandoffFramesCollapsed);
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(0, false, true)]
        [InlineData(0, true, true)]
        [InlineData(1, false, true)]
        [InlineData(1, true, true)]
        [InlineData(2, false, true)]
        [InlineData(2, true, true)]
        [InlineData(3, false, true)]
        [InlineData(3, true, true)]
        [InlineData(4, false, true)]
        [InlineData(4, true, true)]
        [InlineData(5, false, true)]
        [InlineData(5, true, true)]
        [InlineData(6, false, true)]
        [InlineData(6, true, true)]
        [InlineData(7, false, true)]
        [InlineData(7, true, true)]
        [InlineData(0, false, false)]
        [InlineData(0, true, false)]
        [InlineData(1, false, false)]
        [InlineData(1, true, false)]
        [InlineData(2, false, false)]
        [InlineData(2, true, false)]
        [InlineData(3, false, false)]
        [InlineData(3, true, false)]
        [InlineData(4, false, false)]
        [InlineData(4, true, false)]
        [InlineData(5, false, false)]
        [InlineData(5, true, false)]
        [InlineData(6, false, false)]
        [InlineData(6, true, false)]
        [InlineData(7, false, false)]
        [InlineData(7, true, false)]
        public void RuntimeAsync_InnerWrapperIsSampledLeaf_StitchesMultipleMixedOuterActivations(
            int protocols, bool enableNormalization, bool callbacks)
        {
            Frame[] entry = { Frame.V2Wrapper(1), Frame.V2InstrumentedDispatch, Frame.V2Dispatch };
            Frame innerTarget = Frame.App("Scenario.InnerAsync");
            Frame innerParent = Frame.App("Scenario.InnerParentAsync");
            Frame[] root = { Frame.ThreadPoolDispatch, Frame.WorkerThreadStart };
            var sync = new List<Frame>(entry);
            var expected = new List<Frame>();
            expected.Add(innerTarget);
            expected.Add(innerParent);
            var segments = new AsyncSegment[4];
            int v1Count = 0;
            int handoffFrames = 0;
            int v2HandoffFrames = 0;
            for (int i = 2; i >= 0; i--)
            {
                bool v1 = (protocols & (1 << i)) != 0;
                AsyncCallstackKind kind = v1
                    ? AsyncCallstackKind.StateMachineAsync : AsyncCallstackKind.RuntimeAsync;
                if (v1)
                {
                    v1Count++;
                }
                Frame current = Frame.App(v1
                    ? "Scenario+<Outer" + i + "Async>d__" + i + ".MoveNext"
                    : "Scenario.Outer" + i + "Async");
                Frame parent = Frame.App(v1
                    ? "Scenario+<Parent" + i + "Async>d__" + (10 + i) + ".MoveNext"
                    : "Scenario.Parent" + i + "Async");
                Frame[] bridge = i == 2 || !callbacks
                    ? new[]
                    {
                        Frame.CoreLib("System.Threading.Tasks.Task.RunContinuations(class System.Object)", "Task.RunContinuations"),
                        Frame.CoreLib("System.Threading.Tasks.Task`1[System.Boolean].TrySetResult(!0)", "Task.TrySetResult"),
                        Frame.CoreLib("System.Threading.Tasks.TaskCompletionSource`1[System.Boolean].SetResult(!0)", "TaskCompletionSource.SetResult"),
                    }
                    : new[] { Frame.App("Scenario.InterContextCallback" + i) };
                segments[i] = new AsyncSegment(kind, new[] { current, parent }, new[] { 0, 5 });
                sync.AddRange(bridge);
                sync.Add(current);
                sync.AddRange(v1
                    ? new[] { Frame.V1MoveNextAsDispatcher }
                    : new[] { Frame.V2Wrapper(0), Frame.V2InstrumentedDispatch, Frame.V2Dispatch });
                if (!enableNormalization || (callbacks && i != 2))
                {
                    expected.AddRange(bridge);
                }
                else
                {
                    handoffFrames += bridge.Length;
                    if (!v1 && (i == 2 || segments[i + 1].Kind == AsyncCallstackKind.RuntimeAsync))
                    {
                        v2HandoffFrames += bridge.Length;
                    }
                }
                expected.AddRange(v1
                    ? new[]
                    {
                        current.Logical("Scenario.Outer" + i + "Async", -1),
                        parent.Logical("Scenario.Parent" + i + "Async", 5),
                    }
                    : new[] { current, parent });
            }
            segments[3] = AsyncSegment.Runtime(
                Frame.Zero, innerTarget, innerParent);
            sync.AddRange(root);
            expected.AddRange(root);
            var scenario = new StitchScenario
            {
                Sync = sync.ToArray(),
                AsyncSegments = segments,
                ExpectedStitched = expected.ToArray(),
                EnableFrameAnnotations = true,
                EnableNormalization = enableNormalization,
                EnableParentContexts = true,
                OnlyManagedCodeStacks = true,
                IncludeEventSourceEvents = false,
                SkipNormalStackAssertion = true,
                VerifySampleWeights = true,
                VerifyThreadTimeComputer = true,
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 8,
                    V2LeafWrapperDropped = 2,
                    V2PlumbingFramesCollapsed = (4 - v1Count) * 4,
                    V1InlineFallbackUsed = v1Count * 2,
                    AsyncReentrantHandoffFramesCollapsed = handoffFrames * 2,
                    V2ReentrantHandoffFramesCollapsed = v2HandoffFrames * 2,
                },
                AssertTransformContext = context =>
                {
                    AsyncProfilerStitchedFrame target = Assert.Single(context.Frames.Where(frame =>
                        frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget));
                    Assert.Same(context.Segments[3], target.Context);
                    Assert.Equal(1, target.SegmentFrameIndex);
                    Assert.Equal(3, context.Frames.Count(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent));
                    for (int i = 0; i < 3; i++)
                    {
                        AsyncProfilerStitchedFrame anchor = Assert.Single(context.Frames.Where(frame =>
                            frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent &&
                            ReferenceEquals(frame.Context, context.Segments[i])));
                        Assert.Equal(0, anchor.SegmentFrameIndex);
                    }
                    Assert.Equal(handoffFrames, context.Diagnostics.AsyncReentrantHandoffFramesCollapsed);
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void RuntimeAsync_DispatchPlumbingWithoutWrapper_PreservesCallerContext(bool enableInfrastructureCleanup, bool flush)
        {
            Frame[] callees = flush
                ? new[]
                {
                    Frame.CoreLib("System.Diagnostics.Tracing.EventPipeInternal.WriteEventData", "EventPipeInternal.WriteEventData"),
                    Frame.CoreLib("System.Diagnostics.Tracing.EventSource.WriteEventWithRelatedActivityIdCore", "EventSource.WriteEventWithRelatedActivityIdCore"),
                    Frame.CoreLib("System.Runtime.CompilerServices.AsyncProfiler+AsyncThreadContext.Flush", "AsyncThreadContext.Flush"),
                }
                : new[]
                {
                    Frame.CoreLib("System.Threading.Thread.PollGCWorker", "Thread.PollGCWorker"),
                    Frame.CoreLib("Interop+Kernel32.QueryPerformanceCounter", "QueryPerformanceCounter"),
                };
            Frame[] dispatch = { Frame.V2InstrumentedDispatch, Frame.V2Dispatch };
            Frame[] root = { Frame.ThreadPoolDispatch, Frame.WorkerThreadStart };
            var scenario = new StitchScenario
            {
                Sync = callees.Concat(dispatch).Concat(root).ToArray(),
                Async = new[]
                {
                    Frame.App("Scenario.Level3Async"),
                    Frame.App("Scenario.Level2Async"),
                    Frame.App("Program.Main"),
                },
                ExpectedStitched = (enableInfrastructureCleanup && flush ? callees.Take(2) : callees)
                    .Concat(enableInfrastructureCleanup
                        ? new[] { (flush ? callees[2] : Frame.V2InstrumentedDispatch).DispatcherFrames() }
                        : dispatch).Concat(root).ToArray(),
                EnableInfrastructureCleanup = enableInfrastructureCleanup,
                IncludeEventSourceEvents = false,
                VerifySampleWeights = true,
                VerifyThreadTimeComputer = true,
                AssertTransformContext = context =>
                {
                    Assert.All(context.Frames, frame => Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, frame.Origin));
                    Assert.All(context.Frames, frame => Assert.Null(frame.Context));
                    Assert.Equal(enableInfrastructureCleanup ? 1 : 0, context.Frames.Count(frame =>
                        frame.Presentation == AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames));
                },
                AssertStitchedStackSource = stackSource =>
                {
                    int markers = 0;
                    for (int i = 0; i < stackSource.CallFrameIndexLimit; i++)
                    {
                        var index = (StackSourceFrameIndex)i;
                        if (stackSource.GetFrameName(index, false) == "[Async dispatcher frames]")
                        {
                            markers++;
                            Assert.False(stackSource.TryGetAsyncProfilerCallStackFrameInfo(index, out _));
                            Assert.Equal(CodeAddressIndex.Invalid, stackSource.GetFrameCodeAddress(index));
                        }
                    }
                    Assert.Equal(enableInfrastructureCleanup ? 1 : 0, markers);
                },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    V2SyncLayoutUsed = 2,
                    AsyncInfrastructureFramesCollapsed = enableInfrastructureCleanup ? (flush ? 6 : 4) : 0,
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void RuntimeAsync_ProfilerDispatchWithoutWrapper_GroupsOnlyMachinery(
            bool normalization, bool completionEvents)
        {
            Frame native = Frame.Library("coreclr", "SampledNativeCallee", "NativeCallee");
            Frame[] helpers =
            {
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncProfiler+AsyncThreadContext.Acquire", "Acquire"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncProfilerEventSource.WriteAsyncEventBuffer", "WriteAsyncEventBuffer"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncProfiler+AsyncCallstack.EmitEvent", "EmitEvent"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncProfiler+CompleteAsyncMethod.Complete", "CompleteAsyncMethod.Complete"),
            };
            Frame completed = Frame.App("Scenario.BurnCpuAsync");
            Frame pending = Frame.App("Scenario.ParallelBranchAsync");
            Frame[] dispatch = { Frame.V2InstrumentedDispatch, Frame.V2Dispatch };
            Frame[] root = { Frame.ThreadPoolDispatch, Frame.WorkerThreadStart };
            Frame[] logical = completionEvents ? new[] { pending } : Array.Empty<Frame>();
            Frame[] structural = new[] { native }.Concat(helpers)
                .Concat(completionEvents ? new[] { Frame.V2InstrumentedDispatch.DispatcherFrames() } : dispatch)
                .Concat(logical).Concat(root).ToArray();
            var scenario = new StitchScenario
            {
                Sync = new[] { native }.Concat(helpers).Concat(dispatch).Concat(root).ToArray(),
                Async = new[] { completed, pending },
                OmitSymbolFor = native,
                MethodCompletions = completionEvents ? 1 : 0,
                ExpectedStitched = new[] { native, helpers[0].DispatcherFrames() }.Concat(logical).Concat(root).ToArray(),
                ExpectedWithoutNormalization = structural,
                ExpectedWithoutInfrastructureCleanup = structural,
                EnableNormalization = normalization,
                EnableInfrastructureCleanup = true,
                IncludeEventSourceEvents = false,
                VerifySampleWeights = true,
                VerifyThreadTimeComputer = true,
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = completionEvents ? 2 : 0,
                    V2SyncLayoutUsed = completionEvents ? 0 : 2,
                    V2PlumbingFramesCollapsed = completionEvents ? 4 : 0,
                    AsyncInfrastructureFramesCollapsed = (helpers.Length + (completionEvents ? 0 : 2)) * 2,
                },
                AssertTransformContext = context =>
                {
                    Assert.Equal(completionEvents ? 1 : 0, context.Segments[0].GetCompletedFrameCount(context.TimestampQpc));
                    AsyncProfilerStitchedFrame marker = Assert.Single(context.Frames.Where(frame =>
                        frame.Presentation == AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames));
                    Assert.Equal(context.Frames[1], marker);
                    Assert.Contains("AsyncThreadContext.Acquire", context.TraceLog.CodeAddresses[marker.CodeAddress].FullMethodName);
                    Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, marker.Origin);
                    Assert.DoesNotContain(context.Frames, frame =>
                        frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent ||
                        frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompleting);
                    if (completionEvents)
                    {
                        AsyncProfilerStitchedFrame target = Assert.Single(context.Frames.Where(frame =>
                            frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget));
                        Assert.Equal(1, target.SegmentFrameIndex);
                        Assert.Same(target.Context, marker.Context);
                    }
                    else
                    {
                        Assert.All(context.Frames, frame => Assert.Null(frame.Context));
                    }
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("foreign-helper")]
        [InlineData("unresolved-helper")]
        [InlineData("unknown-helper")]
        [InlineData("lookalike-profiler")]
        [InlineData("reversed-pair")]
        [InlineData("different-dispatch-type")]
        [InlineData("foreign-dispatch")]
        public void RuntimeAsync_ProfilerDispatchWithoutWrapper_RejectsUnverifiedHelpersOrPairs(string obstruction)
        {
            const string completeName = "System.Runtime.CompilerServices.AsyncProfiler+CompleteAsyncMethod.Complete";
            Frame helper = obstruction == "foreign-helper"
                ? Frame.Library("OtherRuntime", completeName, "ForeignComplete")
                : Frame.CoreLib(obstruction == "unknown-helper"
                    ? "System.Runtime.CompilerServices.UnknownHelper.Complete"
                    : obstruction == "lookalike-profiler"
                        ? "System.Runtime.CompilerServices.AsyncProfilerExtra+CompleteAsyncMethod.Complete"
                        : completeName, "Complete");
            Frame[] dispatch = { Frame.V2InstrumentedDispatch, Frame.V2Dispatch };
            if (obstruction == "reversed-pair")
            {
                Array.Reverse(dispatch);
            }
            else if (obstruction == "different-dispatch-type")
            {
                dispatch[1] = Frame.CoreLib("System.Threading.Tasks.OtherRuntimeAsyncTask.DispatchContinuations", "OtherDispatch");
            }
            else if (obstruction == "foreign-dispatch")
            {
                dispatch[1] = Frame.Library("OtherRuntime", Frame.V2Dispatch.SymbolName, "ForeignDispatch");
            }
            Frame[] root = { Frame.ThreadPoolDispatch, Frame.WorkerThreadStart };
            Frame[] sync = new[] { helper }.Concat(dispatch).Concat(root).ToArray();
            var scenario = new StitchScenario
            {
                Sync = sync,
                Async = new[] { Frame.App("Scenario.PendingAsync") },
                OmitSymbolFor = obstruction == "unresolved-helper" ? helper : null,
                ExpectedStitched = new[] { helper, dispatch[0].DispatcherFrames() }
                    .Concat(obstruction == "foreign-dispatch" ? new[] { dispatch[1] } : Array.Empty<Frame>())
                    .Concat(root).ToArray(),
                ExpectedWithoutNormalization = sync,
                ExpectedWithoutInfrastructureCleanup = sync,
                EnableInfrastructureCleanup = true,
                IncludeEventSourceEvents = false,
                VerifySampleWeights = true,
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    V2SyncLayoutUsed = 2,
                    AsyncInfrastructureFramesCollapsed = obstruction == "foreign-dispatch" ? 2 : 4,
                },
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void RuntimeAsync_ProfilerDispatchWithoutWrapper_DoesNotCrossApplicationCallbacks()
        {
            Frame emission = Frame.CoreLib("System.Runtime.CompilerServices.AsyncProfiler+AsyncCallstack.EmitEvent", "EmitEvent");
            Frame callback = Frame.App("Scenario.SynchronousCallback");
            Frame complete = Frame.CoreLib("System.Runtime.CompilerServices.AsyncProfiler+CompleteAsyncMethod.Complete", "Complete");
            Frame[] root = { Frame.ThreadPoolDispatch, Frame.WorkerThreadStart };
            Frame[] sync = new[] { emission, callback, complete, Frame.V2InstrumentedDispatch, Frame.V2Dispatch }
                .Concat(root).ToArray();
            new StitchScenario
            {
                Sync = sync,
                Async = new[] { Frame.App("Scenario.PendingAsync") },
                ExpectedStitched = new[] { emission, callback, complete.DispatcherFrames() }.Concat(root).ToArray(),
                ExpectedWithoutNormalization = sync,
                ExpectedWithoutInfrastructureCleanup = sync,
                EnableInfrastructureCleanup = true,
                IncludeEventSourceEvents = false,
                VerifySampleWeights = true,
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    V2SyncLayoutUsed = 2,
                    AsyncInfrastructureFramesCollapsed = 6,
                },
            }.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, false, false)]
        [InlineData(true, true, false)]
        [InlineData(true, true, true)]
        public void RuntimeAsync_DispatchPlumbingWithCompletions_ProjectsTargetBeforePresentation(
            bool normalization, bool cleanup, bool callback)
        {
            Frame poll = Frame.CoreLib("System.Threading.Thread.PollGCWorker", "PollGCWorker");
            Frame target = Frame.App("Scenario.ParallelWhenAllAsync");
            Frame parent = Frame.App("Scenario.CallerAsync");
            Frame instrumented = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncHelpers+RuntimeAsyncTask`1[System.Int64].InstrumentedDispatchContinuations(value class Flags)",
                "InstrumentedDispatchContinuations");
            Frame dispatch = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncHelpers+RuntimeAsyncTask`1[System.Int64].DispatchContinuations()",
                "DispatchContinuations");
            Frame[] prefix = { poll, instrumented, dispatch };
            Frame[] completion =
            {
                Frame.CoreLib("System.Threading.Tasks.Task.RunContinuations", "Task.RunContinuations"),
                Frame.CoreLib("System.Threading.Tasks.Task`1[System.Int64].TrySetResult(!0)", "Task.TrySetResult"),
            };
            Frame[] bridge = completion.Concat(new[]
            {
                Frame.CoreLib("System.Threading.Tasks.Task+WhenAllPromise`1[System.Int64].Invoke", "WhenAllPromise.Invoke"),
            }).Concat(completion).Concat(new[] { instrumented, dispatch }).ToArray();
            if (callback)
            {
                bridge = new[] { Frame.App("Scenario.SynchronousCallback") }.Concat(bridge).ToArray();
            }
            Frame[] root = { Frame.ThreadPoolDispatch, Frame.WorkerThreadStart };
            Frame[] structural = new[] { poll, instrumented.DispatcherFrames(), target, parent }
                .Concat(bridge).Concat(root).ToArray();
            var scenario = new StitchScenario
            {
                Sync = prefix.Concat(bridge).Concat(root).ToArray(),
                Async = new[] { Frame.Zero, target, parent },
                ContextParent = AsyncSegment.Runtime(Frame.App("Scenario.CompletedBranchAsync")),
                MethodCompletions = 1,
                ExpectedStitched = cleanup
                    ? new[] { poll, instrumented.DispatcherFrames(), target, parent }
                        .Concat(callback ? bridge : Array.Empty<Frame>()).Concat(root).ToArray()
                    : structural,
                ExpectedWithoutNormalization = structural,
                ExpectedWithoutInfrastructureCleanup = cleanup ? structural : null,
                EnableNormalization = normalization,
                EnableInfrastructureCleanup = cleanup,
                EnableFrameAnnotations = true,
                IncludeEventSourceEvents = false,
                VerifySampleWeights = true,
                VerifyThreadTimeComputer = true,
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V2PlumbingFramesCollapsed = 4,
                    AsyncInfrastructureFramesCollapsed = cleanup && !callback ? 14 : 0,
                    RuntimeResumptionBridgeFramesCollapsed = cleanup && !callback ? 14 : 0,
                },
                AssertTransformContext = context =>
                {
                    AsyncProfilerStitchedFrame projected = Assert.Single(context.Frames.Where(frame =>
                        frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget));
                    Assert.Equal(1, projected.SegmentFrameIndex);
                    Assert.Same(context.Segments[0], projected.Context);
                    Assert.Equal(projected.Segment.CodeAddressAt(1), projected.CodeAddress);
                    Assert.Single(context.Frames.Where(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncRemaining));
                    Assert.DoesNotContain(context.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
                    Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, context.Frames[0].Origin);
                    Assert.Contains("PollGCWorker", context.TraceLog.CodeAddresses[context.Frames[0].CodeAddress].FullMethodName);
                    AsyncProfilerStitchedFrame marker = Assert.Single(context.Frames.Where(frame =>
                        frame.Presentation == AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames));
                    Assert.Equal(marker, context.Frames[1]);
                    Assert.Same(projected.Context, marker.Context);
                },
                AssertStitchedStackSource = stackSource =>
                {
                    int targets = 0;
                    for (int i = 0; i < stackSource.CallFrameIndexLimit; i++)
                    {
                        var frameIndex = (StackSourceFrameIndex)i;
                        if (stackSource.TryGetAsyncProfilerCallStackFrameInfo(frameIndex, out AsyncProfilerCallStackSourceFrameInfo info) &&
                            info.IsDispatchTarget)
                        {
                            targets++;
                            Assert.False(info.IsCurrent);
                            Assert.False(info.IsCompleting);
                            Assert.Equal(AsyncCallstackKind.RuntimeAsync, info.Kind);
                            Assert.NotEqual(0UL, info.MethodId);
                            Assert.Equal(info.CodeAddress, stackSource.GetFrameCodeAddress(frameIndex));
                            Assert.StartsWith("[Async V2] ", stackSource.GetFrameName(frameIndex, false));
                        }
                    }
                    Assert.Equal(1, targets);
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false, true, true)]
        [InlineData(false, true, true, true)]
        [InlineData(true, false, true, true)]
        [InlineData(true, true, true, true)]
        [InlineData(false, false, false, true)]
        [InlineData(false, true, false, true)]
        [InlineData(true, false, false, true)]
        [InlineData(true, true, false, true)]
        [InlineData(false, false, false, false)]
        [InlineData(true, true, true, false)]
        public void RuntimeAsync_DispatchPlumbingWithZeroCompletedFrames_ProjectsFirstTarget(
            bool normalization, bool cleanup, bool sampledCallees, bool annotations)
        {
            Frame poll = Frame.CoreLib("System.Threading.Thread.PollGCWorker", "PollGCWorker");
            Frame counter = Frame.CoreLib("Interop+Kernel32.QueryPerformanceCounter(int64*)", "QueryPerformanceCounter");
            Frame target = Frame.App("V2TraceScenarios.RunBoundaryFallbackSamplingAsync");
            Frame[] callees = sampledCallees ? new[] { poll, counter } : Array.Empty<Frame>();
            Frame[] prefix = callees.Concat(new[] { Frame.V2InstrumentedDispatch, Frame.V2Dispatch }).ToArray();
            Frame[] root = { Frame.ThreadPoolDispatch, Frame.WorkerThreadStart };
            Frame[] structural = callees.Concat(new[] { Frame.V2InstrumentedDispatch.DispatcherFrames(), target })
                .Concat(root).ToArray();
            var scenario = new StitchScenario
            {
                Sync = prefix.Concat(root).ToArray(),
                Async = new[] { target },
                MethodCompletions = 1,
                // Establish event availability, but do not count this completion at either sample.
                MethodCompletionQpc = 100_028,
                ExpectedStitched = structural,
                ExpectedWithoutNormalization = structural,
                ExpectedWithoutInfrastructureCleanup = cleanup ? structural : null,
                EnableNormalization = normalization,
                EnableInfrastructureCleanup = cleanup,
                EnableFrameAnnotations = annotations,
                IncludeEventSourceEvents = false,
                VerifySampleWeights = true,
                VerifyThreadTimeComputer = true,
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V2PlumbingFramesCollapsed = 4,
                },
                AssertTransformContext = context =>
                {
                    Assert.Equal(0, context.Segments[0].GetCompletedFrameCount(context.TimestampQpc));
                    AsyncProfilerStitchedFrame projected = Assert.Single(context.Frames.Where(frame =>
                        frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget));
                    Assert.Equal(0, projected.SegmentFrameIndex);
                    Assert.Same(context.Segments[0], projected.Context);
                    Assert.Equal(projected.Segment.CodeAddressAt(0), projected.CodeAddress);
                    Assert.DoesNotContain(context.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
                    Assert.All(context.Frames.Take(callees.Length), frame =>
                        Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, frame.Origin));
                    AsyncProfilerStitchedFrame marker = Assert.Single(context.Frames.Where(frame =>
                        frame.Presentation == AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames));
                    Assert.Equal(marker, context.Frames[callees.Length]);
                    Assert.Same(projected.Context, marker.Context);
                },
                AssertStitchedStackSource = stackSource =>
                {
                    int targets = 0;
                    int markers = 0;
                    for (int i = 0; i < stackSource.CallFrameIndexLimit; i++)
                    {
                        var frameIndex = (StackSourceFrameIndex)i;
                        if (stackSource.GetFrameName(frameIndex, false) == "[Async dispatcher frames]")
                        {
                            markers++;
                            Assert.False(stackSource.TryGetAsyncProfilerCallStackFrameInfo(frameIndex, out _));
                            Assert.Equal(CodeAddressIndex.Invalid, stackSource.GetFrameCodeAddress(frameIndex));
                        }
                        if (stackSource.TryGetAsyncProfilerCallStackFrameInfo(frameIndex, out AsyncProfilerCallStackSourceFrameInfo info) &&
                            info.IsDispatchTarget)
                        {
                            targets++;
                            Assert.False(info.IsCurrent);
                            Assert.False(info.IsCompleting);
                            Assert.NotEqual(0UL, info.MethodId);
                            Assert.Equal(info.CodeAddress, stackSource.GetFrameCodeAddress(frameIndex));
                        }
                    }
                    Assert.Equal(1, targets);
                    Assert.Equal(1, markers);
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("no-completion-events")]
        [InlineData("completed-single-frame")]
        [InlineData("reversed-pair")]
        [InlineData("different-type")]
        [InlineData("foreign-module")]
        [InlineData("unknown-target")]
        [InlineData("future-completion")]
        [InlineData("popped-inner")]
        public void RuntimeAsync_DispatchPlumbingWithUncertainTargetOrOwner_PreservesPhysicalStack(string obstruction)
        {
            Frame target = Frame.App("Scenario.ParallelWhenAllAsync");
            Frame[] dispatch = { Frame.V2InstrumentedDispatch, Frame.V2Dispatch };
            if (obstruction == "reversed-pair")
            {
                Array.Reverse(dispatch);
            }
            else if (obstruction == "different-type")
            {
                dispatch[1] = Frame.CoreLib(
                    "System.Threading.Tasks.OtherRuntimeAsyncTask.DispatchContinuations", "Other.DispatchContinuations");
            }
            else if (obstruction == "foreign-module")
            {
                dispatch[1] = Frame.Library("OtherRuntime", Frame.V2Dispatch.SymbolName, "Foreign.DispatchContinuations");
            }
            Frame[] sync = new[] { Frame.CoreLib("System.Threading.Thread.PollGCWorker", "PollGCWorker") }
                .Concat(dispatch).Concat(new[] { Frame.ThreadPoolDispatch, Frame.WorkerThreadStart }).ToArray();
            var scenario = new StitchScenario
            {
                Sync = sync,
                Async = obstruction == "no-completion-events" || obstruction == "completed-single-frame"
                    ? new[] { target }
                    : new[] { Frame.Zero, target, Frame.App("Scenario.CallerAsync") },
                MethodCompletions = obstruction == "no-completion-events" ? 0 : 1,
                MethodCompletionQpc = obstruction == "future-completion" ? 100_028 : 100_020,
                IncludeCompletedInnerActivation = obstruction == "popped-inner",
                OmitSymbolFor = obstruction == "unknown-target" ? target : null,
                ExpectedStitched = sync,
                IncludeEventSourceEvents = false,
                VerifySampleWeights = true,
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation { V2SyncLayoutUsed = 2 },
                AssertTransformContext = context =>
                {
                    Assert.All(context.Frames, frame =>
                    {
                        Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, frame.Origin);
                        Assert.Null(frame.Context);
                    });
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void StateMachineAsync_DispatcherCallee_PreservesCallerContext(bool enableInfrastructureCleanup, bool standalone)
        {
            Frame pollGC = Frame.CoreLib("System.Threading.Thread.PollGCWorker", "Thread.PollGCWorker");
            Frame counter = Frame.CoreLib("Interop+Kernel32.QueryPerformanceCounter", "QueryPerformanceCounter");
            Frame dispatcher = standalone
                ? Frame.CoreLib("System.Runtime.CompilerServices.AsyncStateMachineDispatcher.MoveNext()", "AsyncStateMachineDispatcher.MoveNext")
                : Frame.V1MoveNextAsDispatcher;
            Frame[] logical =
            {
                Frame.App("Scenario.Level3Async"),
                Frame.App("Scenario.Level2Async"),
                Frame.App("Program.Main"),
            };
            var scenario = new StitchScenario
            {
                Kind = AsyncCallstackKind.StateMachineAsync,
                Sync = new[] { pollGC, counter, dispatcher, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart },
                Async = logical,
                ExpectedStitched = new[] { pollGC, counter, enableInfrastructureCleanup ? dispatcher.DispatcherFrames() : dispatcher }
                    .Concat(logical).Concat(new[] { Frame.ThreadPoolDispatch, Frame.WorkerThreadStart }).ToArray(),
                EnableInfrastructureCleanup = enableInfrastructureCleanup,
                IncludeEventSourceEvents = false,
                VerifySampleWeights = true,
                VerifyThreadTimeComputer = true,
                AssertTransformContext = context =>
                {
                    AsyncProfilerStitchedFrame target = Assert.Single(context.Frames.Where(frame =>
                        frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget));
                    Assert.DoesNotContain(context.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
                    AsyncProfilerStitchedFrame physicalDispatcher = context.Frames[2];
                    Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, physicalDispatcher.Origin);
                    Assert.Same(target.Context, physicalDispatcher.Context);
                    Assert.Equal(enableInfrastructureCleanup ? AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames :
                        AsyncProfilerStitchedFramePresentation.Native, physicalDispatcher.Presentation);
                },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V1InlineFallbackUsed = 2,
                    AsyncInfrastructureFramesCollapsed = enableInfrastructureCleanup ? 2 : 0,
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RuntimeAsync_OtherProcessHasActiveSegment_DoesNotStitchSample(bool enableInfrastructureCleanup)
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
                EnableInfrastructureCleanup = enableInfrastructureCleanup,
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
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
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
            // Console.ReadKey. The profiler segment contains only AsyncFoo, so there are no suspended async callers to add.
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
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V1InlineFallbackUsed = 2,
                    V1InfrastructureFramesCollapsed = 6,
                },
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void StateMachineAsync_SameMethodDifferentStates_RetainsDistinctFrameIdentity()
        {
            Frame moveNext = Frame.App("Scenario+<RepeatedAsync>d__1.MoveNext");
            Frame logicalState3 = moveNext.Logical("Scenario.RepeatedAsync", state: 3);
            Frame logicalRunning = moveNext.Logical("Scenario.RepeatedAsync", state: -1);
            Frame logicalStateMinus2 = moveNext.Logical("Scenario.RepeatedAsync", state: -2);
            var scenario = new StitchScenario
            {
                Kind = AsyncCallstackKind.StateMachineAsync,
                Sync = new[]
                {
                    moveNext,
                    Frame.V1MoveNextAsDispatcher,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                AsyncSegments = new[]
                {
                    new AsyncSegment(
                        AsyncCallstackKind.StateMachineAsync,
                        new[] { logicalState3, logicalStateMinus2 },
                        new[] { 3, -2 }),
                },
                EnableFrameAnnotations = true,
                SkipNormalStackAssertion = true,
                ExpectedStitched = new[]
                {
                    logicalRunning,
                    logicalStateMinus2,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V1InlineFallbackUsed = 2,
                },
                AssertStitchedStackSource = stackSource =>
                {
                    var asyncFrames = new List<(StackSourceFrameIndex Index, string Name, AsyncProfilerCallStackSourceFrameInfo Info)>();
                    stackSource.ForEach(sample =>
                    {
                        StackSourceCallStackIndex stackIndex = sample.StackIndex;
                        while (stackIndex != StackSourceCallStackIndex.Invalid)
                        {
                            StackSourceFrameIndex frameIndex = stackSource.GetFrameIndex(stackIndex);
                            if (stackSource.TryGetAsyncProfilerCallStackFrameInfo(frameIndex, out AsyncProfilerCallStackSourceFrameInfo info))
                            {
                                asyncFrames.Add((frameIndex, stackSource.GetFrameName(frameIndex, false), info));
                            }
                            stackIndex = stackSource.GetCallerIndex(stackIndex);
                        }
                    });

                    Assert.Equal(2, asyncFrames.Count);
                    Assert.NotEqual(asyncFrames[0].Index, asyncFrames[1].Index);
                    Assert.NotEqual(asyncFrames[0].Name, asyncFrames[1].Name);
                    Assert.All(asyncFrames, frame =>
                    {
                        Assert.StartsWith("[Async V1] ", frame.Name);
                        Assert.DoesNotContain("![Async V1] ", frame.Name);
                    });
                    Assert.Contains(
                        asyncFrames,
                        frame => frame.Info.State == 3 &&
                                 frame.Info.IsCurrent &&
                                 frame.Name.EndsWith("[ordinal 1, state -1]"));
                    Assert.Contains(
                        asyncFrames,
                        frame => frame.Info.State == -2 &&
                                 frame.Name.EndsWith("[ordinal 1, state -2]"));
                    Assert.Equal(AsyncCallstackKind.StateMachineAsync, asyncFrames[0].Info.Kind);
                    Assert.Equal(AsyncCallstackKind.StateMachineAsync, asyncFrames[1].Info.Kind);
                    Assert.Equal(new[] { -2, 3 }, asyncFrames.Select(frame => frame.Info.State).OrderBy(state => state));
                    Assert.Equal(asyncFrames[0].Info.MethodId, asyncFrames[1].Info.MethodId);
                    Assert.Equal(asyncFrames[0].Info.CodeAddress, asyncFrames[1].Info.CodeAddress);
                    Assert.Equal(
                        asyncFrames[0].Info.CodeAddress,
                        stackSource.GetFrameCodeAddress(asyncFrames[0].Index));
                    Assert.Equal(
                        asyncFrames[1].Info.CodeAddress,
                        stackSource.GetFrameCodeAddress(asyncFrames[1].Index));
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, 0, false, false)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, 1, false, false)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, 3, false, false)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, 0, false, true)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, 1, false, true)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, 3, false, true)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, 0, true, true)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, 1, true, true)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, 3, true, true)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, 0, false, false)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, 1, false, false)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, 3, false, false)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, 0, false, true)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, 1, false, true)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, 3, false, true)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, 0, true, true)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, 1, true, true)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, 3, true, true)]
        public void IncompletePhysicalRoot_IsStructuralAndIndependentOfCutDepth(
            int kindValue, int fragmentLength, bool enableInfrastructureCleanup, bool enableNormalization)
        {
            var kind = (AsyncCallstackKind)kindValue;
            Frame work = Frame.App("Scenario.SampledWork");
            Frame[] captured = Enumerable.Range(0, 7)
                .Select(i => Frame.App(kind == AsyncCallstackKind.StateMachineAsync
                    ? "Scenario+<Level" + i + "Async>d__" + i + ".MoveNext"
                    : "Scenario.Level" + i + "Async"))
                .ToArray();
            Frame current = kind == AsyncCallstackKind.StateMachineAsync
                ? captured[4].Logical("Scenario.Level4Async", state: -1) : captured[4];
            Frame parent = kind == AsyncCallstackKind.StateMachineAsync
                ? captured[5].Logical("Scenario.Level5Async", state: 5) : captured[5];
            Frame ancestor = kind == AsyncCallstackKind.StateMachineAsync
                ? captured[6].Logical("Scenario.Level6Async", state: 6) : captured[6];
            Frame[] fragment = Enumerable.Range(0, fragmentLength)
                .Select(i => Frame.App("OpaquePhysicalFragment.Frame" + i))
                .ToArray();
            var scenario = new StitchScenario
            {
                Kind = kind,
                Sync = new[] { work, captured[4], captured[3], captured[2] }.Concat(fragment).ToArray(),
                Async = captured,
                AsyncStates = Enumerable.Range(0, captured.Length).ToArray(),
                ExpectedStitched = new[] { work, current, parent, ancestor },
                EnableFrameAnnotations = true,
                EnableNormalization = enableNormalization,
                EnableInfrastructureCleanup = enableInfrastructureCleanup,
                OnlyManagedCodeStacks = true,
                IncludeEventSourceEvents = false,
                SkipNormalStackAssertion = true,
                VerifySampleWeights = true,
                VerifyThreadTimeComputer = true,
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    BoundariesNotFound = 2,
                    V1InlineFallbackUsed = kind == AsyncCallstackKind.StateMachineAsync ? 2 : 0,
                    V2TruncatedSuffixFallbackUsed = kind == AsyncCallstackKind.RuntimeAsync ? 2 : 0,
                    IncompleteAsyncRootsNormalized = 2,
                },
                AssertTransformContext = context =>
                {
                    Assert.Equal(AsyncProfilerStitchedFrameOrigin.IncompleteRoot, context.Frames[context.Frames.Count - 1].Origin);
                    AsyncProfilerStitchedFrame anchor = Assert.Single(context.Frames.Where(
                        frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent));
                    Assert.Equal(4, anchor.SegmentFrameIndex);
                    if (kind == AsyncCallstackKind.StateMachineAsync)
                    {
                        Assert.Equal(4, anchor.Segment.FrameStateAt(anchor.SegmentFrameIndex));
                    }
                    Assert.NotEqual(CodeAddressIndex.Invalid, anchor.CodeAddress);
                    Assert.Null(context.Frames[context.Frames.Count - 1].Context);
                },
                AssertStitchedStackSource = stackSource =>
                {
                    stackSource.ForEach(sample =>
                    {
                        int brokenRoots = 0;
                        for (StackSourceCallStackIndex stack = sample.StackIndex;
                             stack != StackSourceCallStackIndex.Invalid;
                             stack = stackSource.GetCallerIndex(stack))
                        {
                            StackSourceFrameIndex frame = stackSource.GetFrameIndex(stack);
                            if (stackSource.GetFrameName(frame, false) != "BROKEN")
                            {
                                continue;
                            }
                            brokenRoots++;
                            Assert.NotEqual(StackSourceFrameIndex.Broken, frame);
                            Assert.Equal("BROKEN", stackSource.GetFrameName(frame, false));
                            Assert.Equal(CodeAddressIndex.Invalid, stackSource.GetFrameCodeAddress(frame));
                            Assert.False(stackSource.TryGetAsyncProfilerCallStackFrameInfo(frame, out _));
                            StackSourceCallStackIndex caller = stackSource.GetCallerIndex(stack);
                            Assert.StartsWith("Thread (",
                                stackSource.GetFrameName(stackSource.GetFrameIndex(caller), false));
                        }
                        Assert.Equal(1, brokenRoots);
                    });

                    var exportedNames = new Dictionary<string, int>();
                    var exportedFrames = new Dictionary<int, StackSourceWriterHelper.FrameInfo>();
                    List<StackSourceWriterHelper.Sample> samples =
                        Assert.Single(StackSourceWriterHelper.GetSortedSamplesPerThread(stackSource)).Value;
                    IReadOnlyList<StackSourceWriterHelper.ProfileEvent> events =
                        StackSourceWriterHelper.GetProfileEvents(stackSource, samples, exportedNames, exportedFrames);
                    Assert.True(StackSourceWriterHelper.Validate(events));
                    Assert.True(exportedNames.TryGetValue("BROKEN", out int brokenId));
                    Assert.Contains(events, profileEvent =>
                        profileEvent.FrameId == brokenId &&
                        profileEvent.Type == StackSourceWriterHelper.ProfileEventType.Open);
                    Assert.Contains(events, profileEvent =>
                        profileEvent.FrameId == brokenId &&
                        profileEvent.Type == StackSourceWriterHelper.ProfileEventType.Close);
                    Assert.StartsWith("Thread (",
                        exportedNames.Single(pair => pair.Value == exportedFrames[brokenId].ParentId).Key);
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(0, false, false)]
        [InlineData(1, false, true)]
        [InlineData(3, false, true)]
        [InlineData(3, true, true)]
        public void V1_SurvivingDispatcher_IncompleteCallerTailIsStructural(
            int plumbingLength, bool enableInfrastructureCleanup, bool enableNormalization)
        {
            StitchScenario scenario = CreateV1SurvivingDispatcherRootScenario(plumbingLength);
            scenario.EnableNormalization = enableNormalization;
            scenario.EnableInfrastructureCleanup = enableInfrastructureCleanup;
            scenario.ExpectedDiagnostics.IncompleteAsyncRootsNormalized = 2;
            scenario.AssertTransformContext = context =>
            {
                Assert.Equal(AsyncProfilerStitchedFrameOrigin.IncompleteRoot, context.Frames.Last().Origin);
                Assert.Single(context.Frames.Where(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.IncompleteRoot));
                Assert.Null(context.Frames.Last().Context);
                AsyncProfilerStitchedFrame target = Assert.Single(context.Frames.Where(frame => frame.IsStateMachineBoxCarrier));
                Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget, target.Origin);
                Assert.Equal(0, target.Segment.FrameStateAt(target.SegmentFrameIndex));
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("managed")]
        [InlineData("native")]
        [InlineData("unresolved")]
        [InlineData("foreign")]
        [InlineData("interrupted")]
        public void V1_SurvivingDispatcher_UnverifiedPhysicalCallerTailIsPreserved(string obstruction)
        {
            StitchScenario scenario = CreateV1SurvivingDispatcherRootScenario(3);
            Frame[] tail;
            if (obstruction == "managed" || obstruction == "native")
            {
                Frame host = obstruction == "managed" ? Frame.App("CustomScheduler.Execute") :
                    Frame.Library("NativeHost", "NativeHost.Enter", "NativeHost.Enter");
                scenario.Sync = scenario.Sync.Concat(new[] { host }).ToArray();
                tail = new[] { host };
            }
            else
            {
                Frame last = scenario.Sync.Last();
                if (obstruction == "unresolved")
                {
                    scenario.OmitSymbolFor = last;
                    tail = new[] { last };
                }
                else if (obstruction == "foreign")
                {
                    Frame foreign = Frame.Library("OtherRuntime", last.SymbolName, "ForeignBox");
                    scenario.Sync[scenario.Sync.Length - 1] = foreign;
                    tail = new[] { foreign };
                }
                else
                {
                    Frame interruption = Frame.App("Scenario.SynchronousCallback");
                    scenario.Sync = scenario.Sync.Take(scenario.Sync.Length - 1)
                        .Concat(new[] { interruption, last }).ToArray();
                    tail = new[] { interruption, last };
                }
                scenario.ExpectedDiagnostics.V1InfrastructureFramesCollapsed -= 2;
            }
            scenario.ExpectedStitched = scenario.ExpectedStitched.Concat(tail).ToArray();
            scenario.AssertTransformContext = context =>
                Assert.DoesNotContain(context.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.IncompleteRoot);

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "CustomScheduler.Execute")]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "UiSynchronizationContext.Callback")]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "DedicatedThread.Run")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "CustomScheduler.Execute")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "UiSynchronizationContext.Callback")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "DedicatedThread.Run")]
        public void CompletePhysicalSegment_PreservesNonThreadPoolHosts(int kindValue, string hostName)
        {
            var kind = (AsyncCallstackKind)kindValue;
            Frame work = Frame.App("Scenario.SampledWork");
            Frame current = Frame.App("Scenario+<CurrentAsync>d__1.MoveNext");
            Frame parent = Frame.App("Scenario+<ParentAsync>d__2.MoveNext");
            Frame host = Frame.App(hostName);
            Frame entry = Frame.App("Application.ThreadEntry");
            var scenario = new StitchScenario
            {
                Kind = kind,
                Sync = new[]
                {
                    work, current,
                    kind == AsyncCallstackKind.StateMachineAsync ? Frame.V1MoveNextAsDispatcher : Frame.V2Wrapper(0),
                    host, entry,
                },
                Async = new[] { current, parent },
                AsyncStates = new[] { 0, 1 },
                ExpectedStitched = new[]
                {
                    work,
                    kind == AsyncCallstackKind.StateMachineAsync ? current.Logical("Scenario.CurrentAsync", state: -1) : current,
                    kind == AsyncCallstackKind.StateMachineAsync ? parent.Logical("Scenario.ParentAsync", state: 1) : parent,
                    host, entry,
                },
                EnableFrameAnnotations = true,
                EnableInfrastructureCleanup = true,
                OnlyManagedCodeStacks = true,
                IncludeEventSourceEvents = false,
                SkipNormalStackAssertion = true,
                VerifySampleWeights = true,
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V1InlineFallbackUsed = kind == AsyncCallstackKind.StateMachineAsync ? 2 : 0,
                },
                AssertTransformContext = context =>
                    Assert.DoesNotContain(context.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.IncompleteRoot),
                AssertStitchedStackSource = stackSource =>
                {
                    stackSource.ForEach(sample =>
                    {
                        for (StackSourceCallStackIndex stack = sample.StackIndex;
                             stack != StackSourceCallStackIndex.Invalid;
                             stack = stackSource.GetCallerIndex(stack))
                        {
                            Assert.NotEqual("BROKEN",
                                stackSource.GetFrameName(stackSource.GetFrameIndex(stack), false));
                        }
                    });
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, (int)AsyncCallstackKind.RuntimeAsync)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, (int)AsyncCallstackKind.StateMachineAsync)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, (int)AsyncCallstackKind.RuntimeAsync)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, (int)AsyncCallstackKind.StateMachineAsync)]
        public void NestedIncompleteRoot_PreservesCompleteInnerSegments(
            int outerKindValue, int innerKindValue)
        {
            var outerKind = (AsyncCallstackKind)outerKindValue;
            var innerKind = (AsyncCallstackKind)innerKindValue;
            Frame work = Frame.App("Scenario.SampledWork");
            Frame bridge = Frame.App("Scenario.SynchronousBridge");
            Frame fragment = Frame.App("Scenario.IncompletePhysicalFragment");
            Frame[] outer = Enumerable.Range(0, 7)
                .Select(i => Frame.App(outerKind == AsyncCallstackKind.StateMachineAsync
                    ? "Scenario+<Outer" + i + "Async>d__" + i + ".MoveNext"
                    : "Scenario.Outer" + i + "Async"))
                .ToArray();
            Frame[] inner = Enumerable.Range(0, 2)
                .Select(i => Frame.App(innerKind == AsyncCallstackKind.StateMachineAsync
                    ? "Scenario+<Inner" + i + "Async>d__" + (10 + i) + ".MoveNext"
                    : "Scenario.Inner" + i + "Async"))
                .ToArray();
            var scenario = new StitchScenario
            {
                Sync = new[]
                {
                    work, inner[0],
                    innerKind == AsyncCallstackKind.StateMachineAsync ? Frame.V1MoveNextAsDispatcher : Frame.V2Wrapper(0),
                    bridge, outer[4], outer[3], outer[2], fragment,
                },
                AsyncSegments = new[]
                {
                    new AsyncSegment(outerKind, outer, Enumerable.Range(0, 7).ToArray()),
                    new AsyncSegment(innerKind, inner, new[] { 0, 1 }),
                },
                ExpectedStitched = new[]
                {
                    work,
                    innerKind == AsyncCallstackKind.StateMachineAsync ? inner[0].Logical("Scenario.Inner0Async", state: -1) : inner[0],
                    innerKind == AsyncCallstackKind.StateMachineAsync ? inner[1].Logical("Scenario.Inner1Async", state: 1) : inner[1],
                    bridge,
                    outerKind == AsyncCallstackKind.StateMachineAsync ? outer[4].Logical("Scenario.Outer4Async", state: -1) : outer[4],
                    outerKind == AsyncCallstackKind.StateMachineAsync ? outer[5].Logical("Scenario.Outer5Async", state: 5) : outer[5],
                    outerKind == AsyncCallstackKind.StateMachineAsync ? outer[6].Logical("Scenario.Outer6Async", state: 6) : outer[6],
                },
                EnableFrameAnnotations = true,
                EnableInfrastructureCleanup = true,
                OnlyManagedCodeStacks = true,
                IncludeEventSourceEvents = false,
                SkipNormalStackAssertion = true,
                VerifySampleWeights = true,
                VerifyThreadTimeComputer = true,
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 4,
                    BoundariesNotFound = 2,
                    IncompleteAsyncRootsNormalized = 2,
                    V2TruncatedSuffixFallbackUsed = outerKind == AsyncCallstackKind.RuntimeAsync ? 2 : 0,
                    V1InlineFallbackUsed =
                        (outerKind == AsyncCallstackKind.StateMachineAsync ? 2 : 0) +
                        (innerKind == AsyncCallstackKind.StateMachineAsync ? 2 : 0),
                },
                AssertTransformContext = context =>
                {
                    Assert.Equal(2, context.Frames.Count(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent));
                    Assert.Equal(AsyncProfilerStitchedFrameOrigin.IncompleteRoot, context.Frames[context.Frames.Count - 1].Origin);
                    Assert.Single(context.Frames.Where(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.IncompleteRoot));
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, false, true)]
        [InlineData(false, true, false)]
        [InlineData(false, true, true)]
        [InlineData(true, false, false)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        [InlineData(true, true, true)]
        public void MissingInnerV1Boundary_PreservesOuterV2AndCapturedCarrierState(
            bool carrier, bool enableInfrastructureCleanup, bool enableNormalization)
        {
            Frame work = Frame.App("Scenario.SampledWork");
            Frame bridge = Frame.App("Scenario.InterContextCallback");
            Frame[] inner = Enumerable.Range(0, 4)
                .Select(i => Frame.App("Scenario+<Inner" + i + "Async>d__" + (10 + i) + ".MoveNext"))
                .ToArray();
            Frame[] outer =
            {
                Frame.App("Scenario.OuterV2Async"),
                Frame.App("Scenario.OuterV2ParentAsync"),
            };
            Frame physicalCurrent = carrier
                ? Frame.CoreLib(
                    "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1" +
                    "[System.Int64,Scenario+<Inner2Async>d__12].MoveNext(class System.Threading.Thread,value class Flags)",
                    "InnerBoxCarrier")
                : inner[2];
            Frame[] expected =
            {
                work, inner[2].Logical("Scenario.Inner2Async", carrier ? 5 : -1),
                inner[3].Logical("Scenario.Inner3Async", 3),
                Frame.V1MoveNextAsDispatcher, bridge, outer[0], outer[1],
                Frame.ThreadPoolDispatch, Frame.WorkerThreadStart,
            };
            var scenario = new StitchScenario
            {
                Sync = new[]
                {
                    work, physicalCurrent, inner[1], inner[0], Frame.V1MoveNextAsDispatcher,
                    bridge, outer[0], Frame.V2Wrapper(0), Frame.V2InstrumentedDispatch, Frame.V2Dispatch,
                    Frame.ThreadPoolDispatch, Frame.WorkerThreadStart,
                },
                AsyncSegments = new[]
                {
                    AsyncSegment.Runtime(outer),
                    new AsyncSegment(AsyncCallstackKind.StateMachineAsync, inner, new[] { 0, 1, 5, 3 }),
                },
                OmitSymbolFor = Frame.V1MoveNextAsDispatcher,
                ExpectedStitched = expected,
                ExpectedWithoutInfrastructureCleanup = expected,
                EnableFrameAnnotations = true,
                EnableNormalization = enableNormalization,
                EnableInfrastructureCleanup = enableInfrastructureCleanup,
                OnlyManagedCodeStacks = true,
                IncludeEventSourceEvents = false,
                SkipNormalStackAssertion = true,
                VerifySampleWeights = true,
                VerifyThreadTimeComputer = true,
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 4,
                    BoundariesNotFound = 2,
                    V1InlineFallbackUsed = 2,
                    V2PlumbingFramesCollapsed = 4,
                },
                AssertTransformContext = context =>
                {
                    AsyncProfilerStitchedFrame innerAnchor = Assert.Single(context.Frames.Where(frame =>
                        ReferenceEquals(frame.Context, context.Segments[1]) &&
                        frame.Origin == (carrier
                            ? AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget : AsyncProfilerStitchedFrameOrigin.AsyncCurrent)));
                    Assert.Equal(2, innerAnchor.SegmentFrameIndex);
                    Assert.Equal(carrier, innerAnchor.IsStateMachineBoxCarrier);
                    Assert.Equal(5, innerAnchor.Segment.FrameStateAt(2));
                    AsyncProfilerStitchedFrame outerAnchor = Assert.Single(context.Frames.Where(frame =>
                        frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent &&
                        ReferenceEquals(frame.Context, context.Segments[0])));
                    Assert.Equal(0, outerAnchor.SegmentFrameIndex);
                    Assert.Equal(1, context.Diagnostics.V1TruncatedSuffixFallbackUsed);
                    Assert.DoesNotContain(context.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.IncompleteRoot);
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void StateMachineAsync_CurrentAnnotations_DoNotLeakIntoIdenticalCapturedAncestor(bool enableFrameAnnotations)
        {
            Frame method = Frame.App("Scenario+<RecursiveAsync>d__1.MoveNext");
            Frame captured = method.Logical("Scenario.RecursiveAsync", enableFrameAnnotations ? 1 : (int?)null);
            Frame running = method.Logical("Scenario.RecursiveAsync", enableFrameAnnotations ? -1 : (int?)null);
            var scenario = new StitchScenario
            {
                Kind = AsyncCallstackKind.StateMachineAsync,
                Sync = new[] { method, Frame.V1MoveNextAsDispatcher, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart },
                Async = new[] { captured, captured },
                AsyncStates = new[] { 1, 1 },
                EnableFrameAnnotations = enableFrameAnnotations,
                SkipNormalStackAssertion = enableFrameAnnotations,
                VerifyThreadTimeComputer = true,
                IncludeEventSourceEvents = false,
                AllowSupplementalSamples = true,
                ExpectedStitched = new[] { running, captured, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation { SegmentsProcessed = 2, V1InlineFallbackUsed = 2 },
                AssertStitchedStackSource = stackSource =>
                {
                    var frames = new List<(StackSourceFrameIndex Index, string Name, AsyncProfilerCallStackSourceFrameInfo Info)>();
                    for (int i = 0; i < stackSource.CallFrameIndexLimit; i++)
                    {
                        var index = (StackSourceFrameIndex)i;
                        if (stackSource.TryGetAsyncProfilerCallStackFrameInfo(index, out AsyncProfilerCallStackSourceFrameInfo info))
                        {
                            frames.Add((index, stackSource.GetFrameName(index, false), info));
                        }
                    }
                    var current = Assert.Single(frames.Where(frame => frame.Info.IsCurrent));
                    var ancestor = Assert.Single(frames.Where(frame => !frame.Info.IsCurrent));
                    Assert.NotEqual(current.Index, ancestor.Index);
                    Assert.Equal(current.Info.CodeAddress, ancestor.Info.CodeAddress);
                    Assert.Equal(current.Info.MethodId, ancestor.Info.MethodId);
                    Assert.Equal(1, current.Info.State);
                    Assert.Equal(1, ancestor.Info.State);
                    Assert.False(current.Info.IsStateMachineBoxCarrier);
                    if (enableFrameAnnotations)
                    {
                        Assert.EndsWith("[ordinal 1, state -1]", current.Name);
                        Assert.EndsWith("[ordinal 1, state 1]", ancestor.Name);
                    }
                    else
                    {
                        Assert.Equal(current.Name, ancestor.Name);
                        Assert.DoesNotContain("state ", current.Name);
                    }
                },
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false, true, false)]
        [InlineData(false, true, true, false)]
        [InlineData(true, false, true, false)]
        [InlineData(true, true, true, false)]
        [InlineData(true, false, false, false)]
        [InlineData(true, false, true, true)]
        [InlineData(false, false, true, true)]
        [InlineData(false, true, true, true)]
        [InlineData(true, true, true, true)]
        public void StateMachineAsync_CompletedUnwind_AttributesVerifiedSpanToLogicalRoot(
            bool truncated, bool enableInfrastructureCleanup, bool enableFrameAnnotations, bool preservePrefix)
        {
            StitchScenario scenario = CreateV1CompletedUnwindScenario(truncated);
            int unwindLength = scenario.Sync.Length - (truncated ? 0 : 3);
            Frame root = scenario.Async.Last().Logical("Scenario.RootAsync", enableFrameAnnotations ? -2 : (int?)null);
            scenario.EnableInfrastructureCleanup = enableInfrastructureCleanup;
            scenario.EnableFrameAnnotations = enableFrameAnnotations;
            scenario.ExpectedStitched = new[] { root }
                .Concat(truncated ? Array.Empty<Frame>() : new[] { Frame.ThreadPoolDispatch, Frame.WorkerThreadStart })
                .ToArray();
            if (preservePrefix)
            {
                Frame observer = Frame.App("Observer.ArbitraryCallee");
                Frame marker = scenario.Sync[0].DispatcherFrames();
                scenario.Sync = new[] { observer }.Concat(scenario.Sync).ToArray();
                scenario.ExpectedStitched = new[] { observer, marker }.Concat(scenario.ExpectedStitched).ToArray();
            }
            scenario.ExpectedWithoutNormalization =
                scenario.Sync.Where(frame => frame != Frame.V1MoveNextAsDispatcher).ToArray();
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation
            {
                SegmentsProcessed = 2,
                BoundariesNotFound = truncated ? 2 : 0,
                IncompleteAsyncRootsNormalized = truncated ? 2 : 0,
                V1CompletionUnwindFramesCollapsed = unwindLength * 2,
            };
            scenario.AssertTransformContext = context =>
            {
                AsyncProfilerStitchedFrame unwind = Assert.Single(context.Frames.Where(
                    frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompletionUnwind));
                Assert.Equal(3, unwind.SegmentFrameIndex);
                Assert.Equal(7, unwind.Segment.FrameStateAt(unwind.SegmentFrameIndex));
                Assert.Equal(MethodIndex.Invalid, unwind.Method);
                Assert.Equal(unwind.Segment.CodeAddressAt(3), unwind.CodeAddress);
                Assert.False(unwind.IsStateMachineBoxCarrier);
                AsyncProfilerStitchedFrame[] markers = context.Frames.Where(frame =>
                    frame.Presentation == AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames).ToArray();
                Assert.Equal(preservePrefix ? 1 : 0, markers.Length);
                if (preservePrefix)
                {
                    Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, markers[0].Origin);
                    Assert.Same(unwind.Context, markers[0].Context);
                    Assert.Contains("AsyncStateMachineBox", context.TraceLog.CodeAddresses[markers[0].CodeAddress].FullMethodName);
                    Assert.Equal(2, context.Frames.IndexOf(unwind));
                }
                Assert.DoesNotContain(context.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent ||
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompleting);
                Assert.Equal(truncated ? 1 : 0,
                    context.Frames.Count(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.IncompleteRoot));
            };
            scenario.AssertStitchedStackSource = source =>
            {
                int unwinds = 0;
                for (int i = 0; i < source.CallFrameIndexLimit; i++)
                {
                    var frameIndex = (StackSourceFrameIndex)i;
                    if (!source.TryGetAsyncProfilerCallStackFrameInfo(frameIndex, out AsyncProfilerCallStackSourceFrameInfo info))
                    {
                        continue;
                    }
                    unwinds++;
                    Assert.True(info.IsCompletionUnwind);
                    Assert.False(info.IsCompleting);
                    Assert.False(info.IsCurrent);
                    Assert.False(info.IsDispatchTarget);
                    Assert.Equal(7, info.State);
                    Assert.Contains("Scenario+<RootAsync>d__4.MoveNext",
                        source.TraceLog.CodeAddresses[info.CodeAddress].FullMethodName);
                }
                Assert.Equal(1, unwinds);
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false, 2)]
        [InlineData(true, false, 2)]
        [InlineData(false, true, 3)]
        [InlineData(true, true, 3)]
        public void FullyCompletedRecursiveV1Unwind_AttributesTheCohortWithoutInventingPhysicalCopies(
            bool cleanup, bool trailingHelpers, int bodies)
        {
            StitchScenario scenario = CreateFullyCompletedRecursiveV1UnwindScenario(bodies, trailingHelpers);
            scenario.EnableInfrastructureCleanup = cleanup;
            scenario.AssertTransformContext = context =>
            {
                AsyncProfilerStitchedFrame unwind = Assert.Single(context.Frames.Where(frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompletionUnwind));
                Assert.Equal(unwind.Segment.FrameCount - 1, unwind.SegmentFrameIndex);
                Assert.Equal(7, unwind.Segment.FrameStateAt(unwind.SegmentFrameIndex));
                Assert.Equal(MethodIndex.Invalid, unwind.Method);
                Assert.DoesNotContain(context.Frames, frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent ||
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompleting ||
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget ||
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncRemaining);
                Assert.Single(context.Frames.Where(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.IncompleteRoot));
                Assert.Equal(AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames, context.Frames[2].Presentation);
                Assert.Equal(1, context.Diagnostics.BoundariesNotFound);
            };
            scenario.AssertStitchedStackSource = source =>
            {
                var unwinds = new List<AsyncProfilerCallStackSourceFrameInfo>();
                for (int i = 0; i < source.CallFrameIndexLimit; i++)
                {
                    if (source.TryGetAsyncProfilerCallStackFrameInfo((StackSourceFrameIndex)i, out var info))
                    {
                        unwinds.Add(info);
                        Assert.True(info.IsCompletionUnwind);
                        Assert.False(info.IsCurrent);
                        Assert.False(info.IsCompleting);
                        Assert.False(info.IsDispatchTarget);
                        Assert.Equal(7, info.State);
                    }
                }
                Assert.Single(unwinds);
                AssertScenarioBrokenRoots(source, 1);
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("interrupted")]
        [InlineData("missing-builder")]
        [InlineData("missing-pair")]
        [InlineData("missing-entry")]
        [InlineData("foreign-helper")]
        [InlineData("unresolved-helper")]
        [InlineData("wrong-box")]
        [InlineData("wrong-context")]
        [InlineData("profile-veto")]
        [InlineData("future-completions")]
        [InlineData("single-body")]
        public void FullyCompletedRecursiveV1Unwind_UnprovenSpinesStayPhysical(string obstruction)
        {
            StitchScenario scenario = CreateFullyCompletedRecursiveV1UnwindScenario(2, true);
            scenario.EnableNormalization = false;
            scenario.EnableInfrastructureCleanup = false;
            scenario.ExpectedStitched = scenario.Sync;
            scenario.ExpectedWithoutInfrastructureCleanup = null;
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation
            {
                SegmentsProcessed = 2,
                BoundariesNotFound = 2,
            };
            scenario.AssertTransformContext = context =>
            {
                var frames = context.Frames.ToList();
                int body = frames.FindLastIndex(frame => frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1StateMachineMoveNext);
                int entry = body - 6;
                if (obstruction == "interrupted") { frames.Insert(entry, frames[0]); }
                else if (obstruction == "missing-builder") { frames.RemoveAt(body - 1); }
                else if (obstruction == "missing-pair") { frames.RemoveAt(body - 2); }
                else if (obstruction == "missing-entry") { frames.RemoveAt(entry); }
                else if (obstruction == "single-body") { frames.RemoveRange(2, 9); }
                else if (obstruction == "foreign-helper" || obstruction == "unresolved-helper" ||
                    obstruction == "wrong-box" || obstruction == "wrong-context")
                {
                    AsyncProfilerStitchedFrame original = frames[entry];
                    frames[entry] = AsyncProfilerStitchedFrame.CreateSync(new AsyncProfilerStitchSyncFrame(
                        obstruction == "foreign-helper" ? frames[0].CodeAddress : original.CodeAddress,
                        obstruction == "unresolved-helper" ? MethodIndex.Invalid : original.Method,
                        original.SyncFrameKind, obstruction == "wrong-box" ? frames[0].Method : original.BoxedStateMachineMethod),
                        obstruction == "wrong-context" ? null : original.Context);
                }
                var veto = new RejectingCompletionChainProfile();
                var diagnostics = new AsyncProfilerCallStackDiagnostics { SegmentsProcessed = 1, BoundariesNotFound = 1 };
                int count = frames.Count;
                new AsyncProfilerV1PresentationTransform().Apply(new AsyncProfilerCallStackTransformState(
                    context.TraceLog, context.Segments,
                    context.TimestampQpc - (obstruction == "future-completions" ? 10 : 0),
                    context.ClassifyBoundary, diagnostics, frames, methodOf: context.GetMethod,
                    runtimeProfileResolver: address => obstruction == "profile-veto" ? veto : context.GetRuntimeProfile(address)));
                Assert.Equal(count, frames.Count);
                Assert.Equal(0, diagnostics.V1CompletionUnwindFramesCollapsed);
                Assert.DoesNotContain(frames, frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompletionUnwind ||
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompleting ||
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent ||
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.IncompleteRoot);
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(3, 0)]
        [InlineData(5, 0)]
        [InlineData(3, 1)]
        public void FullyCompletedRecursiveV1Unwind_RequiresExactFullNormalCompletion(int normal, int exceptional)
        {
            StitchScenario scenario = CreateFullyCompletedRecursiveV1UnwindScenario(2, false);
            scenario.MethodCompletions = normal;
            scenario.ExceptionCompletions = exceptional;
            scenario.EnableInfrastructureCleanup = false;
            Frame body = scenario.Async[0];
            scenario.ExpectedStitched = scenario.Sync.Select(frame => frame == body
                ? body.Logical("Scenario.RecursiveReturnAsync") : frame).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = null;
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation
            {
                SegmentsProcessed = 2,
                BoundariesNotFound = 2,
                V1SynchronousMoveNextFramesNormalized = 4,
            };
            scenario.AssertTransformContext = context => Assert.DoesNotContain(context.Frames, frame =>
                frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompletionUnwind ||
                frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompleting ||
                frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void NestedCompletedV1Outer_YieldBridgeKeepsTheInnerTargetPending(
            bool enableNormalization, bool enableInfrastructureCleanup)
        {
            StitchScenario scenario = CreateNestedCompletedV1OuterYieldScenario();
            scenario.EnableNormalization = enableNormalization;
            scenario.EnableInfrastructureCleanup = enableInfrastructureCleanup;
            if (!enableInfrastructureCleanup)
            {
                scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
                scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
                scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = 0;
                scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;
            }
            scenario.AssertTransformContext = context =>
            {
                AsyncProfilerCallStack outer = context.Segments[0];
                Assert.DoesNotContain(context.Frames, frame => ReferenceEquals(frame.Context, outer));
                AsyncProfilerStitchedFrame target = Assert.Single(context.Frames.Where(frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget));
                Assert.Same(context.Segments[1], target.Context);
                Assert.Equal(0, target.Segment.FrameStateAt(target.SegmentFrameIndex));
                Assert.DoesNotContain(context.Frames, frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent ||
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompleting ||
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompletionUnwind);
                CodeAddressIndex anchor = context.Frames.Single(frame =>
                    frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibThreadPoolDispatch).CodeAddress;
                Assert.False(context.HasVerifiedDispatchEntry(outer, anchor));
                Assert.False(context.HasVerifiedV1CompletionAnchor(outer, anchor, allowCompletionUnwind: true));
                Assert.Equal(enableInfrastructureCleanup, context.HasVerifiedCompletedV1OuterDispatch(outer, anchor));
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("foreign-yield")]
        [InlineData("unresolved-yield")]
        [InlineData("interrupted")]
        [InlineData("foreign-anchor")]
        [InlineData("excess-completions")]
        [InlineData("exception-completion")]
        public void NestedCompletedV1Outer_YieldBridgeRejectsUnprovenOwnership(string obstruction)
        {
            StitchScenario scenario = CreateNestedCompletedV1OuterYieldScenario();
            Frame yield = scenario.Sync[6];
            Frame replacement = obstruction == "foreign-yield"
                ? Frame.Library("OtherRuntime", yield.SymbolName, "ForeignYield") : yield;
            if (obstruction == "unresolved-yield") { scenario.OmitSymbolFor = yield; }
            if (obstruction == "foreign-anchor")
            {
                replacement = Frame.Library("OtherRuntime", Frame.ThreadPoolDispatch.SymbolName, "ForeignDispatch");
                scenario.Sync = scenario.Sync.Select(frame => frame == Frame.ThreadPoolDispatch ? replacement : frame).ToArray();
                scenario.ExpectedStitched = scenario.ExpectedStitched.Select(frame => frame == Frame.ThreadPoolDispatch ? replacement : frame).ToArray();
                scenario.ExpectedWithoutInfrastructureCleanup = scenario.ExpectedWithoutInfrastructureCleanup.Select(
                    frame => frame == Frame.ThreadPoolDispatch ? replacement : frame).ToArray();
                replacement = yield;
            }
            if (obstruction == "excess-completions") { scenario.AsyncSegments[0].MethodCompletions = 3; }
            if (obstruction == "exception-completion")
            {
                scenario.AsyncSegments[0].MethodCompletions = 1;
                scenario.AsyncSegments[0].ExceptionCompletions = 1;
            }
            Frame[] helpers = obstruction == "interrupted"
                ? new[] { yield, Frame.App("Observer.Callback"), yield } : new[] { replacement };
            scenario.Sync = scenario.Sync.Take(6).Concat(helpers).Concat(scenario.Sync.Skip(7)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = scenario.ExpectedWithoutInfrastructureCleanup.Take(4)
                .Concat(helpers).Concat(scenario.ExpectedWithoutInfrastructureCleanup.Skip(5)).ToArray();
            scenario.ExpectedStitched = scenario.ExpectedStitched.Take(3).Concat(helpers)
                .Concat(scenario.ExpectedStitched.Skip(3)).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 4;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("missing-placement")]
        [InlineData("nonempty-outer")]
        [InlineData("disconnected-inner")]
        [InlineData("missing-inner-dispatch")]
        [InlineData("missing-boundary")]
        [InlineData("adjacency")]
        [InlineData("ambiguous")]
        [InlineData("future-completion")]
        [InlineData("profile-veto")]
        public void NestedCompletedV1Outer_YieldBridgeRequiresConsumedNestedDispatchEvidence(string obstruction)
        {
            StitchScenario scenario = CreateNestedCompletedV1OuterYieldScenario();
            scenario.EnableInfrastructureCleanup = false;
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;
            scenario.AssertTransformContext = context =>
            {
                var frames = context.Frames.ToList();
                int bridgeStart = frames.FindIndex(frame => frame.Context == null);
                CodeAddressIndex innerDispatch = frames.Single(frame =>
                    context.ClassifyBoundary(frame.CodeAddress).Kind ==
                        AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher).CodeAddress;
                CodeAddressIndex outerDispatch = Enumerable.Range(0, context.TraceLog.CodeAddresses.Count)
                    .Select(i => (CodeAddressIndex)i).Single(ca => ca != innerDispatch &&
                        context.ClassifyBoundary(ca).Kind == AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher);
                var placements = new[]
                {
                    new AsyncProfilerSegmentPlacement(context.Segments[0], bridgeStart,
                        obstruction == "nonempty-outer" ? bridgeStart + 1 : bridgeStart, dispatch: outerDispatch),
                    new AsyncProfilerSegmentPlacement(context.Segments[1], 0,
                        obstruction == "disconnected-inner" ? bridgeStart - 1 : bridgeStart,
                        dispatch: obstruction == "missing-inner-dispatch" ? CodeAddressIndex.Invalid : innerDispatch),
                };
                var diagnostics = new AsyncProfilerCallStackDiagnostics
                {
                    SegmentsProcessed = 2,
                    BoundariesNotFound = obstruction == "missing-boundary" ? 1 : 0,
                    AdjacencyMismatches = obstruction == "adjacency" ? 1 : 0,
                    V1CompletionAlignmentAmbiguous = obstruction == "ambiguous" ? 1 : 0,
                };
                var profile = new AsyncProfilerRejectRuntimeBridgeProfile();
                var state = new AsyncProfilerCallStackTransformState(
                    context.TraceLog, context.Segments,
                    obstruction == "future-completion" ? context.Segments[0].StartQpc - 1 : context.TimestampQpc,
                    context.ClassifyBoundary, diagnostics, frames,
                    runtimeProfileResolver: obstruction == "profile-veto"
                        ? (Func<CodeAddressIndex, AsyncProfilerRuntimeRuleProfile>)(ca => profile) : null,
                    segmentPlacements: obstruction == "missing-placement" ? null : placements);
                CodeAddressIndex bridge = frames[bridgeStart].CodeAddress;
                Assert.Equal(obstruction == "profile-veto",
                    state.HasVerifiedCompletedV1OuterDispatch(context.Segments[0], bridge));
                new AsyncProfilerInfrastructureCleanupTransform().Apply(state);
                Assert.Contains(frames, frame => frame.CodeAddress == bridge &&
                    frame.Presentation == AsyncProfilerStitchedFramePresentation.Native);
                Assert.Equal(0, diagnostics.RuntimeResumptionBridgeFramesCollapsed);
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(true, 1)]
        [InlineData(true, 3)]
        [InlineData(false, 1)]
        [InlineData(false, 3)]
        public void StateMachineAsync_CompletedUnwind_YieldBridgePreservesCompletionRoles(
            bool enableInfrastructureCleanup, int helperCount)
        {
            Frame[] helpers =
            {
                Frame.CoreLib(
                    "System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter+<>c::<OutputCorrelationEtwEvent>b__6_0(System.Action,System.Threading.Tasks.Task)",
                    "Yield.Correlation"),
                Frame.CoreLib("System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter+Helper`1[System.Int64].Invoke()",
                    "Yield.Helper"),
                Frame.CoreLib("System.Runtime.CompilerServices.YieldAwaitable.GetAwaiter()", "Yield.GetAwaiter"),
            };
            StitchScenario scenario = CreateV1CompletedUnwindYieldBridgeScenario(helpers.Take(helperCount).ToArray());
            scenario.EnableInfrastructureCleanup = enableInfrastructureCleanup;
            if (!enableInfrastructureCleanup)
            {
                scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
                scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
                scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;
            }
            scenario.AssertTransformContext = context =>
            {
                AsyncProfilerStitchedFrame unwind = Assert.Single(context.Frames.Where(
                    frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompletionUnwind));
                CodeAddressIndex bridge = context.Frames.Single(frame =>
                    frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibThreadPoolDispatch).CodeAddress;
                Assert.False(context.HasVerifiedDispatchEntry(unwind.Context, bridge));
                Assert.False(context.HasVerifiedV1CompletionAnchor(unwind.Context, bridge));
                Assert.Equal(enableInfrastructureCleanup,
                    context.HasVerifiedV1CompletionAnchor(unwind.Context, bridge, allowCompletionUnwind: true));
                Assert.False(AsyncProfilerCallStackTransformHelpers.HasVerifiedAsyncAnchor(
                    context, context.Frames.IndexOf(unwind), unwind.Context));
                Assert.Equal(MethodIndex.Invalid, unwind.Method);
                Assert.Equal(7, unwind.Segment.FrameStateAt(unwind.SegmentFrameIndex));
                Assert.DoesNotContain(context.Frames, frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent ||
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompleting ||
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget);
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("foreign-yield")]
        [InlineData("unresolved-yield")]
        [InlineData("interrupted")]
        [InlineData("other-runtime-bridge")]
        [InlineData("unresolved-anchor")]
        public void StateMachineAsync_CompletedUnwind_YieldBridgeRejectsUnownedOrInterruptedSpans(string obstruction)
        {
            Frame yield = Frame.CoreLib(
                "System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter+<>c.Callback()", "Yield.Callback");
            Frame[] helpers = obstruction == "interrupted"
                ? new[] { yield, Frame.App("Observer.Callback"), yield }
                : new[] { obstruction == "foreign-yield"
                    ? Frame.Library("OtherRuntime", yield.SymbolName, "ForeignYield") : yield };
            StitchScenario scenario = CreateV1CompletedUnwindYieldBridgeScenario(helpers);
            if (obstruction == "unresolved-yield") { scenario.OmitSymbolFor = yield; }
            else if (obstruction == "unresolved-anchor") { scenario.OmitSymbolFor = Frame.ThreadPoolDispatch; }
            else if (obstruction == "other-runtime-bridge")
            {
                Frame Replace(Frame frame) => frame == yield || frame == Frame.ThreadPoolDispatch
                    ? Frame.Library(@"C:\other\System.Private.CoreLib.dll", frame.SymbolName, "OtherRuntime." + frame.Label)
                    : frame;
                scenario.Sync = scenario.Sync.Select(Replace).ToArray();
                scenario.ExpectedWithoutInfrastructureCleanup = scenario.ExpectedWithoutInfrastructureCleanup.Select(Replace).ToArray();
                scenario.ExpectedWithoutNormalization = scenario.ExpectedWithoutNormalization.Select(Replace).ToArray();
            }
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("missing-placement")]
        [InlineData("missing-boundary")]
        [InlineData("adjacency")]
        [InlineData("ambiguous")]
        [InlineData("profile-veto")]
        public void StateMachineAsync_CompletedUnwind_YieldBridgeRequiresConsistentStructuralEvidence(string obstruction)
        {
            Frame yield = Frame.CoreLib(
                "System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter+<>c.Callback()", "Yield.Callback");
            StitchScenario scenario = CreateV1CompletedUnwindYieldBridgeScenario(yield);
            scenario.EnableInfrastructureCleanup = false;
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;
            scenario.AssertTransformContext = context =>
            {
                var frames = context.Frames.ToList();
                AsyncProfilerCallStack activation = Assert.Single(context.Segments);
                CodeAddressIndex dispatch = Enumerable.Range(0, context.TraceLog.CodeAddresses.Count)
                    .Select(i => (CodeAddressIndex)i).Single(ca =>
                        context.ClassifyBoundary(ca).Kind == AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher);
                var placements = new[] { new AsyncProfilerSegmentPlacement(activation, 0, frames.Count, dispatch: dispatch) };
                var diagnostics = new AsyncProfilerCallStackDiagnostics
                {
                    SegmentsProcessed = 1,
                    BoundariesNotFound = obstruction == "missing-boundary" ? 1 : 0,
                    AdjacencyMismatches = obstruction == "adjacency" ? 1 : 0,
                    V1CompletionAlignmentAmbiguous = obstruction == "ambiguous" ? 1 : 0,
                };
                var profile = new AsyncProfilerRejectRuntimeBridgeProfile();
                var state = new AsyncProfilerCallStackTransformState(
                    context.TraceLog, context.Segments, context.TimestampQpc, context.ClassifyBoundary, diagnostics, frames,
                    runtimeProfileResolver: obstruction == "profile-veto"
                        ? (Func<CodeAddressIndex, AsyncProfilerRuntimeRuleProfile>)(ca => profile) : null,
                    segmentPlacements: obstruction == "missing-placement" ? null : placements);
                CodeAddressIndex bridge = frames.First(frame => frame.Context == null).CodeAddress;
                Assert.Equal(obstruction == "profile-veto",
                    state.HasVerifiedV1CompletionAnchor(activation, bridge, allowCompletionUnwind: true));
                new AsyncProfilerInfrastructureCleanupTransform().Apply(state);
                Assert.Equal(context.Frames, frames);
                Assert.Equal(0, diagnostics.RuntimeResumptionBridgeFramesCollapsed);
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("interrupted")]
        [InlineData("unresolved")]
        [InlineData("foreign")]
        [InlineData("recursive")]
        [InlineData("unordered")]
        [InlineData("unresolved-root")]
        [InlineData("no-completion-spine")]
        [InlineData("foreign-callback")]
        [InlineData("single-identity")]
        public void StateMachineAsync_CompletedUnwind_UnverifiedSpansRemainPhysical(string obstruction)
        {
            StitchScenario scenario = CreateV1CompletedUnwindScenario(false);
            if (obstruction == "interrupted")
            {
                scenario.Sync = scenario.Sync.Take(7).Concat(new[] { Frame.App("Scenario.ArbitraryCallback") })
                    .Concat(scenario.Sync.Skip(7)).ToArray();
            }
            else if (obstruction == "unresolved")
            {
                scenario.OmitSymbolFor = scenario.Sync[7];
            }
            else if (obstruction == "foreign")
            {
                Frame original = scenario.Sync[7];
                scenario.Sync[7] = Frame.Library("OtherRuntime", original.SymbolName, original.Label);
            }
            else if (obstruction == "recursive")
            {
                scenario.Async[3] = scenario.Async[0];
            }
            else if (obstruction == "unordered")
            {
                Frame first = scenario.Async[0];
                scenario.Async[0] = scenario.Async[1];
                scenario.Async[1] = first;
            }
            else if (obstruction == "unresolved-root")
            {
                scenario.OmitSymbolFor = scenario.Async.Last();
            }
            else if (obstruction == "foreign-callback")
            {
                Frame callback = scenario.Sync[6];
                scenario.Sync[6] = Frame.CoreLib(
                    callback.SymbolName.Replace("<ChildAsync>d__2", "<OtherAsync>d__5"), "ForeignCallback");
            }
            else if (obstruction == "single-identity")
            {
                scenario.Sync = scenario.Sync.Skip(4).Take(4)
                    .Concat(new[] { Frame.V1MoveNextAsDispatcher, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart })
                    .ToArray();
            }
            else
            {
                scenario.Sync = scenario.Sync.Where(frame =>
                    !frame.SymbolName.Contains("SetExistingTaskResult")).ToArray();
            }
            scenario.ExpectedStitched = scenario.Sync.Where(frame => frame != Frame.V1MoveNextAsDispatcher)
                .Select(frame => frame.ModuleName == Frame.ApplicationModule && AsyncProfilerCallStackStitchBoundary.TryGetLogicalStateMachineMethodName(
                    frame.SymbolName, out string name) ? frame.Logical(name) : frame).ToArray();
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation
            {
                SegmentsProcessed = 2,
                V1SynchronousMoveNextFramesNormalized = obstruction == "single-identity" ? 2 : 4,
            };
            scenario.AssertTransformContext = context =>
                Assert.DoesNotContain(context.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompletionUnwind);

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(3)]
        [InlineData(5)]
        public void StateMachineAsync_CompletedUnwind_RequiresExactRecordedFullCompletion(int completions)
        {
            StitchScenario scenario = CreateV1CompletedUnwindScenario(false);
            scenario.MethodCompletions = completions;
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation
            {
                SegmentsProcessed = 2,
                V1InlineFallbackUsed = completions == 0 ? 2 : 0,
                V1SynchronousMoveNextFramesNormalized = completions == 0 ? 0 : 4,
            };
            if (completions == 0)
            {
                scenario.ExpectedStitched = new[]
                {
                    scenario.Async[2].Logical("Scenario.CurrentAsync", 0),
                    scenario.Async[3].Logical("Scenario.RootAsync", 7),
                    Frame.ThreadPoolDispatch, Frame.WorkerThreadStart,
                };
            }
            else
            {
                scenario.ExpectedStitched = scenario.Sync.Where(frame => frame != Frame.V1MoveNextAsDispatcher)
                    .Select(frame => frame.ModuleName == Frame.ApplicationModule && AsyncProfilerCallStackStitchBoundary.TryGetLogicalStateMachineMethodName(
                        frame.SymbolName, out string name) ? frame.Logical(name) : frame).ToArray();
                if (completions == 3)
                {
                    int hostStart = scenario.ExpectedStitched.Length - 2;
                    scenario.ExpectedStitched = scenario.ExpectedStitched.Take(hostStart)
                        .Concat(new[] { Frame.V1MoveNextAsDispatcher, scenario.Async[3].Logical("Scenario.RootAsync", 7) })
                        .Concat(scenario.ExpectedStitched.Skip(hostStart)).ToArray();
                }
            }
            scenario.AssertTransformContext = context =>
                Assert.DoesNotContain(context.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompletionUnwind);

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        public void StateMachineAsync_CompletedUnwind_TplCallbacksRetainVerifiedCompletionOwnership(
            bool truncated, bool enableInfrastructureCleanup, bool onlyManagedCodeStacks)
        {
            Frame callback = Frame.CoreLib(
                "System.Runtime.CompilerServices.TaskAwaiter+<>c.<OutputWaitEtwEvents>b__12_0(System.Action,System.Threading.Tasks.Task)",
                "TaskAwaiter.Correlation");
            StitchScenario scenario = CreateV1CompletedUnwindScenario(truncated, callback);
            scenario.OnlyManagedCodeStacks = onlyManagedCodeStacks;
            scenario.EnableInfrastructureCleanup = enableInfrastructureCleanup;
            int unwindLength = scenario.Sync.Length - (truncated ? 0 : 3);
            scenario.ExpectedStitched = new[] { scenario.Async.Last().Logical("Scenario.RootAsync", -2) }
                .Concat(truncated ? Array.Empty<Frame>() : new[] { Frame.ThreadPoolDispatch, Frame.WorkerThreadStart }).ToArray();
            scenario.ExpectedWithoutNormalization = scenario.Sync.Where(frame => frame != Frame.V1MoveNextAsDispatcher).ToArray();
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation
            {
                SegmentsProcessed = 2,
                BoundariesNotFound = truncated ? 2 : 0,
                IncompleteAsyncRootsNormalized = truncated ? 2 : 0,
                V1CompletionUnwindFramesCollapsed = unwindLength * 2,
            };
            scenario.AssertStitchedStackSource = source => AssertScenarioBrokenRoots(source, truncated ? 1 : 0);
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("application", false)]
        [InlineData("foreign", false)]
        [InlineData("unresolved", true)]
        [InlineData("lookalike", true)]
        public void StateMachineAsync_CompletedUnwind_UnverifiedTplCallbacksRemainPhysical(string obstruction, bool truncated)
        {
            const string name = "System.Runtime.CompilerServices.TaskAwaiter+<>c.<OutputWaitEtwEvents>b__12_0";
            Frame callback = obstruction == "application" ? Frame.App("Observer.Callback") :
                obstruction == "foreign" ? Frame.Library("OtherRuntime", name, "ForeignAwaiter") :
                Frame.CoreLib(obstruction == "lookalike" ? name.Replace("TaskAwaiter+", "TaskAwaiterLookalike+") : name, "Awaiter");
            StitchScenario scenario = CreateV1CompletedUnwindScenario(truncated, callback);
            if (obstruction == "unresolved")
            {
                scenario.OmitSymbolFor = callback;
            }
            scenario.ExpectedStitched = scenario.Sync.Where(frame => frame != Frame.V1MoveNextAsDispatcher)
                .Select(frame => frame.ModuleName == Frame.ApplicationModule &&
                    AsyncProfilerCallStackStitchBoundary.TryGetLogicalStateMachineMethodName(frame.SymbolName, out string logical)
                        ? frame.Logical(logical) : frame).ToArray();
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation
            {
                SegmentsProcessed = 2,
                BoundariesNotFound = truncated ? 2 : 0,
                V1SynchronousMoveNextFramesNormalized = 4,
            };
            scenario.AssertTransformContext = context =>
                Assert.DoesNotContain(context.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompletionUnwind);
            scenario.AssertStitchedStackSource = source => AssertScenarioBrokenRoots(source, 1);
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(0, false, true, true, false)]
        [InlineData(1, true, true, true, false)]
        [InlineData(2, false, false, true, false)]
        [InlineData(4, true, false, true, false)]
        [InlineData(7, false, true, false, false)]
        [InlineData(9, true, true, false, false)]
        [InlineData(0, true, true, true, true)]
        public void StateMachineAsync_TruncatedReturn_RetainsFinishingBodyAndCapturedAncestry(
            int rootCut, bool enableInfrastructureCleanup, bool enableFrameAnnotations, bool preservePrefix, bool exceptional)
        {
            StitchScenario scenario = CreateV1TruncatedReturnScenario(rootCut, preservePrefix);
            scenario.EnableInfrastructureCleanup = enableInfrastructureCleanup;
            scenario.EnableFrameAnnotations = enableFrameAnnotations;
            if (exceptional)
            {
                scenario.MethodCompletions--;
                scenario.ExceptionCompletions = 1;
            }
            Frame finishing = scenario.Async[2].Logical("Scenario.CurrentAsync", enableFrameAnnotations ? -2 : (int?)null);
            Frame parent = scenario.Async[3].Logical("Scenario.RootAsync", enableFrameAnnotations ? 7 : (int?)null);
            Frame ancestor = scenario.Async[4].Logical("Scenario.AncestorAsync", enableFrameAnnotations ? 9 : (int?)null);
            int prefixLength = preservePrefix ? 2 : 0;
            scenario.ExpectedStitched = scenario.Sync.Take(prefixLength)
                .Concat(preservePrefix ? new[] { scenario.Sync[prefixLength].DispatcherFrames() } : Array.Empty<Frame>())
                .Concat(new[] { finishing, parent, ancestor }).ToArray();
            if (!enableFrameAnnotations)
            {
                scenario.ExpectedWithoutNormalization =
                    scenario.Sync.Concat(new[] { scenario.Async[4].Logical("Scenario.AncestorAsync") }).ToArray();
            }
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation
            {
                SegmentsProcessed = 2,
                BoundariesNotFound = 2,
                IncompleteAsyncRootsNormalized = 2,
                V1CompletionUnwindFramesCollapsed = (scenario.Sync.Length - prefixLength) * 2,
            };
            scenario.AssertTransformContext = context =>
            {
                AsyncProfilerStitchedFrame completed = Assert.Single(context.Frames.Where(
                    frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompleting));
                Assert.Equal(2, completed.SegmentFrameIndex);
                Assert.Equal(0, completed.Segment.FrameStateAt(2));
                Assert.NotEqual(MethodIndex.Invalid, completed.Method);
                Assert.Equal(completed.Method, context.TraceLog.CodeAddresses.MethodIndex(completed.CodeAddress));
                Assert.Contains("Scenario+<CurrentAsync>d__3.MoveNext",
                    context.TraceLog.CodeAddresses[completed.CodeAddress].FullMethodName);
                Assert.Equal(new[] { 3, 4 }, context.Frames.Where(
                    frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncRemaining)
                    .Select(frame => frame.SegmentFrameIndex).ToArray());
                Assert.DoesNotContain(context.Frames, frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent ||
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget ||
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompletionUnwind);
                Assert.Single(context.Frames.Where(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.IncompleteRoot));
            };
            scenario.AssertStitchedStackSource = source =>
            {
                AssertScenarioBrokenRoots(source, 1);
                var completing = new List<AsyncProfilerCallStackSourceFrameInfo>();
                for (int i = 0; i < source.CallFrameIndexLimit; i++)
                {
                    if (source.TryGetAsyncProfilerCallStackFrameInfo((StackSourceFrameIndex)i, out AsyncProfilerCallStackSourceFrameInfo info))
                    {
                        Assert.False(info.IsCurrent);
                        Assert.False(info.IsDispatchTarget);
                        Assert.False(info.IsCompletionUnwind);
                        if (info.IsCompleting)
                        {
                            completing.Add(info);
                            Assert.Equal(info.CodeAddress, source.GetFrameCodeAddress((StackSourceFrameIndex)i));
                        }
                    }
                }
                Assert.Equal(0, Assert.Single(completing).State);
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("application")]
        [InlineData("foreign")]
        [InlineData("unresolved")]
        [InlineData("unordered")]
        [InlineData("recursive")]
        [InlineData("single-identity")]
        [InlineData("missing-finishing-body")]
        [InlineData("no-completion-spine")]
        [InlineData("records-ahead")]
        [InlineData("excess-records")]
        public void StateMachineAsync_TruncatedReturn_UnverifiedSpansRemainPhysical(string obstruction)
        {
            StitchScenario scenario = CreateV1TruncatedReturnScenario(0, true);
            int finishing = Array.IndexOf(scenario.Sync, scenario.Async[2]);
            int helper = finishing + 2;
            if (obstruction == "application")
            {
                scenario.Sync = scenario.Sync.Take(helper).Concat(new[] { Frame.App("Observer.InterveningCallback") })
                    .Concat(scenario.Sync.Skip(helper)).ToArray();
            }
            else if (obstruction == "foreign")
            {
                Frame original = scenario.Sync[helper];
                scenario.Sync[helper] = Frame.Library("OtherRuntime", original.SymbolName, original.Label);
            }
            else if (obstruction == "unresolved")
            {
                scenario.OmitSymbolFor = scenario.Sync[helper];
            }
            else if (obstruction == "unordered")
            {
                Frame original = scenario.Async[0];
                scenario.Async[0] = scenario.Async[1];
                scenario.Async[1] = original;
            }
            else if (obstruction == "recursive")
            {
                scenario.Async[4] = scenario.Async[0];
            }
            else if (obstruction == "single-identity")
            {
                scenario.Sync = scenario.Sync.Take(finishing + 2).ToArray();
            }
            else if (obstruction == "missing-finishing-body")
            {
                scenario.Sync = scenario.Sync.Where(frame => frame != scenario.Async[2]).ToArray();
            }
            else if (obstruction == "no-completion-spine")
            {
                scenario.Sync = scenario.Sync.Where(frame => !frame.SymbolName.Contains("SetExistingTaskResult")).ToArray();
            }
            else
            {
                scenario.MethodCompletions = obstruction == "records-ahead" ? 4 : 6;
            }
            Frame[] callers = scenario.MethodCompletions == 3
                ? new[] { scenario.Async[4].Logical(obstruction == "recursive" ? "Scenario.DeepAsync" : "Scenario.AncestorAsync", 9) }
                : Array.Empty<Frame>();
            scenario.ExpectedStitched = scenario.Sync.Select(frame =>
                frame.ModuleName == Frame.ApplicationModule &&
                AsyncProfilerCallStackStitchBoundary.TryGetLogicalStateMachineMethodName(frame.SymbolName, out string logical)
                    ? frame.Logical(logical) : frame).Concat(callers).ToArray();
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation
            {
                SegmentsProcessed = 2,
                BoundariesNotFound = 2,
                V1SynchronousMoveNextFramesNormalized = scenario.Sync.Count(frame => scenario.Async.Contains(frame)) * 2,
            };
            scenario.AssertTransformContext = context =>
                Assert.DoesNotContain(context.Frames, frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompleting ||
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompletionUnwind ||
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.IncompleteRoot);
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        [InlineData(9)]
        public void StateMachineAsync_TruncatedReturn_WithoutCompletionRecordsStillRecoversPhysicalProgress(int rootCut)
        {
            StitchScenario scenario = CreateV1TruncatedReturnScenario(rootCut, true);
            scenario.MethodCompletions = 0;
            scenario.ExpectedStitched = scenario.Sync.TakeWhile(frame => frame != scenario.Async[2]).Concat(new[]
            {
                scenario.Async[2].Logical("Scenario.CurrentAsync", -1),
                scenario.Async[3].Logical("Scenario.RootAsync", 7),
                scenario.Async[4].Logical("Scenario.AncestorAsync", 9),
            }).ToArray();
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation
            {
                SegmentsProcessed = 2,
                BoundariesNotFound = 2,
                IncompleteAsyncRootsNormalized = 2,
                V1InlineFallbackUsed = 2,
            };
            scenario.AssertTransformContext = context =>
            {
                Assert.Equal(1, context.Diagnostics.V1TruncatedSuffixFallbackUsed);
                AsyncProfilerStitchedFrame current = Assert.Single(context.Frames.Where(
                    frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent));
                Assert.Equal(2, current.SegmentFrameIndex);
                Assert.Equal(new[] { 3, 4 }, context.Frames.Where(
                    frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncRemaining)
                    .Select(frame => frame.SegmentFrameIndex).ToArray());
                Assert.DoesNotContain(context.Frames, frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompleting ||
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompletionUnwind);
            };
            scenario.AssertStitchedStackSource = source => AssertScenarioBrokenRoots(source, 1);
            scenario.AssertProductionStitch();
        }

        [Fact]
        public void StateMachineAsync_TruncatedReturn_FutureCompletionsDoNotMarkBodiesCompleted()
        {
            StitchScenario scenario = CreateV1TruncatedReturnScenario(0, true);
            scenario.MethodCompletionQpc = 100_030;
            scenario.ExpectedStitched = scenario.Sync.Select(frame =>
                frame.ModuleName == Frame.ApplicationModule &&
                AsyncProfilerCallStackStitchBoundary.TryGetLogicalStateMachineMethodName(frame.SymbolName, out string logical)
                    ? frame.Logical(logical, frame == scenario.Async[0] ? -1 : (int?)null) : frame)
                .Concat(new[]
                {
                    scenario.Async[1].Logical("Scenario.ChildAsync", 0),
                    scenario.Async[2].Logical("Scenario.CurrentAsync", 0),
                    scenario.Async[3].Logical("Scenario.RootAsync", 7),
                    scenario.Async[4].Logical("Scenario.AncestorAsync", 9),
                }).ToArray();
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation
            {
                SegmentsProcessed = 2,
                BoundariesNotFound = 2,
                V1SynchronousMoveNextFramesNormalized = 4,
            };
            scenario.AssertTransformContext = context =>
                Assert.DoesNotContain(context.Frames, frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompleting ||
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompletionUnwind);
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, false, "NativeHost", 1)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, true, "NativeHost", 0)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, false, "ntdll", 0)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, false, "NativeHost", 1)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, true, "NativeHost", 0)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, false, "libc", 0)]
        public void PhysicalRoot_BrokenMarkerMatchesSynchronousPolicy(
            int kindValue, bool onlyManagedCodeStacks, string hostModule, int expectedBroken)
        {
            StitchScenario scenario = CreateRuntimeResumptionBridgeScenario((AsyncCallstackKind)kindValue);
            Frame host = Frame.Library(hostModule, "ThreadEntry", "Host.ThreadEntry");
            scenario.Sync = scenario.Sync.Concat(new[] { host }).ToArray();
            scenario.ExpectedStitched = scenario.ExpectedStitched.Concat(new[] { host }).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = scenario.ExpectedWithoutInfrastructureCleanup.Concat(new[] { host }).ToArray();
            scenario.OnlyManagedCodeStacks = onlyManagedCodeStacks;
            scenario.IncludeEventSourceEvents = false;
            scenario.VerifyThreadTimeComputer = true;
            scenario.AssertStitchedStackSource = source => AssertScenarioBrokenRoots(source, expectedBroken);
            scenario.AssertProductionStitch();
        }

        [Fact]
        public void StateMachineAsync_CompletedUnwind_FutureCompletionsDoNotChangeEarlierSamples()
        {
            StitchScenario scenario = CreateV1CompletedUnwindScenario(false);
            scenario.MethodCompletionQpc = 100_030;
            scenario.ExpectedStitched = scenario.Sync.TakeWhile(frame => frame != scenario.Async[0])
                .Select(frame => frame == scenario.Async[1] ? frame.Logical("Scenario.ChildAsync") : frame)
                .Concat(new[]
                {
                    scenario.Async[0].Logical("Scenario.DeepAsync", -1),
                    scenario.Async[1].Logical("Scenario.ChildAsync", 0),
                    scenario.Async[2].Logical("Scenario.CurrentAsync", 0),
                    scenario.Async[3].Logical("Scenario.RootAsync", 7),
                    Frame.ThreadPoolDispatch, Frame.WorkerThreadStart,
                }).ToArray();
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation
            {
                SegmentsProcessed = 2,
                V1SynchronousMoveNextFramesNormalized = 2,
            };
            scenario.AssertTransformContext = context =>
                Assert.DoesNotContain(context.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompletionUnwind);

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void StateMachineAsync_CompletedUnwind_PreservesEnclosingLiveContextAndBridge()
        {
            StitchScenario scenario = CreateV1CompletedUnwindScenario(false);
            Frame outer = Frame.App("Scenario.OuterCurrent");
            Frame parent = Frame.App("Scenario.OuterParent");
            Frame bridge = Frame.App("Scenario.SynchronousBridge");
            scenario.AsyncSegments = new[]
            {
                AsyncSegment.Runtime(outer, parent),
                AsyncSegment.StateMachine(scenario.Async),
            };
            scenario.Sync = scenario.Sync.Take(scenario.Sync.Length - 2)
                .Concat(new[]
                {
                    bridge, outer, Frame.V2Wrapper(0), Frame.V2InstrumentedDispatch, Frame.V2Dispatch,
                    Frame.ThreadPoolDispatch, Frame.WorkerThreadStart,
                }).ToArray();
            scenario.ExpectedStitched = new[]
            {
                scenario.Async.Last().Logical("Scenario.RootAsync", -2),
                bridge, outer, parent, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart,
            };
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation
            {
                SegmentsProcessed = 4,
                V2PlumbingFramesCollapsed = 4,
                V1CompletionUnwindFramesCollapsed = (scenario.Sync.Length - 8) * 2,
            };
            scenario.AssertTransformContext = context =>
            {
                AsyncProfilerStitchedFrame unwind = Assert.Single(context.Frames.Where(
                    frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompletionUnwind));
                AsyncProfilerStitchedFrame current = Assert.Single(context.Frames.Where(
                    frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent));
                Assert.Equal(unwind.Context.Depth - 1, current.Context.Depth);
                Assert.DoesNotContain(context.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.IncompleteRoot);
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(0, 2, true, false)]
        [InlineData(0, 1, true, false)]
        [InlineData(2, 0, true, false)]
        [InlineData(1, 1, true, false)]
        [InlineData(0, 2, false, false)]
        [InlineData(0, 1, false, false)]
        [InlineData(2, 0, false, false)]
        [InlineData(0, 2, true, true)]
        [InlineData(2, 0, true, true)]
        public void StateMachineAsync_CompletionTail_PresentsFinishingStateInBothHosts(
            int normal, int exceptional, bool enableFrameAnnotations, bool enableInfrastructureCleanup)
        {
            Frame child = Frame.App("Scenario+<ChildAsync>d__12.MoveNext");
            Frame finishing = Frame.App("Scenario+<FinishingAsync>d__11.MoveNext");
            Frame parent = Frame.App("Scenario+<ParentAsync>d__10.MoveNext");
            Frame ancestor = Frame.App("Scenario+<AncestorAsync>d__9.MoveNext");
            bool completed = normal + exceptional == 2;
            Frame logicalFinishing = finishing.Logical("Scenario.FinishingAsync", enableFrameAnnotations ? (completed ? -2 : -1) : (int?)null);
            Frame logicalParent = parent.Logical("Scenario.ParentAsync", enableFrameAnnotations ? 0 : (int?)null);
            Frame logicalAncestor = ancestor.Logical("Scenario.AncestorAsync", enableFrameAnnotations ? 2 : (int?)null);
            Frame observer = Frame.App("CompletionObserver.Record");
            string completionMember = exceptional > 0 ? "SetException" : "SetExistingTaskResult";
            Frame completion = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64]." + completionMember + "(class System.Object)",
                "CompletionHelper");
            Frame bridge = Frame.App("EarlierCompletionBridge");
            var scenario = new StitchScenario
            {
                Kind = AsyncCallstackKind.StateMachineAsync,
                Sync = new[]
                {
                    observer, completion, finishing, bridge, child,
                    Frame.V1MoveNextAsDispatcher, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart,
                },
                Async = new[]
                {
                    child.Logical("Scenario.ChildAsync"),
                    finishing.Logical("Scenario.FinishingAsync", enableFrameAnnotations ? 1 : (int?)null),
                    logicalParent, logicalAncestor,
                },
                AsyncStates = new[] { 1, 1, 0, 2 },
                MethodCompletions = normal,
                ExceptionCompletions = exceptional,
                EnableFrameAnnotations = enableFrameAnnotations,
                EnableInfrastructureCleanup = enableInfrastructureCleanup,
                SkipNormalStackAssertion = true,
                VerifyThreadTimeComputer = true,
                IncludeEventSourceEvents = false,
                AllowSupplementalSamples = true,
                VerifySampleWeights = true,
                ExpectedStitched = (enableInfrastructureCleanup
                    ? new[] { observer, logicalFinishing, logicalParent, logicalAncestor, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart }
                    : new[] { observer, completion, logicalFinishing, logicalParent, logicalAncestor, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart }),
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V1InlineFallbackUsed = normal > 0 ? 0 : 2,
                    V1CompletionTailUsed = completed ? 2 : 0,
                    AsyncInfrastructureFramesCollapsed = enableInfrastructureCleanup ? 2 : 0,
                },
                AssertTransformContext = context =>
                {
                    AsyncProfilerStitchedFrame frame = Assert.Single(context.Frames.Where(candidate =>
                        candidate.Origin == (completed ? AsyncProfilerStitchedFrameOrigin.AsyncCompleting : AsyncProfilerStitchedFrameOrigin.AsyncCurrent)));
                    Assert.Equal(1, frame.SegmentFrameIndex);
                    Assert.Equal(1, frame.Segment.FrameStateAt(frame.SegmentFrameIndex));
                    Assert.False(frame.IsStateMachineBoxCarrier);
                    Assert.DoesNotContain(context.Frames, candidate => candidate.Origin ==
                        (completed ? AsyncProfilerStitchedFrameOrigin.AsyncCurrent : AsyncProfilerStitchedFrameOrigin.AsyncCompleting));
                },
                AssertStitchedStackSource = stackSource =>
                {
                    var frames = new List<(StackSourceFrameIndex Index, string Name, AsyncProfilerCallStackSourceFrameInfo Info)>();
                    for (int i = 0; i < stackSource.CallFrameIndexLimit; i++)
                    {
                        var index = (StackSourceFrameIndex)i;
                        if (stackSource.TryGetAsyncProfilerCallStackFrameInfo(index, out AsyncProfilerCallStackSourceFrameInfo info))
                        {
                            frames.Add((index, stackSource.GetFrameName(index, false), info));
                        }
                    }
                    var completing = Assert.Single(frames.Where(frame => frame.Info.IsCompleting || frame.Info.IsCurrent));
                    Assert.Equal(completed, completing.Info.IsCompleting);
                    Assert.Equal(!completed, completing.Info.IsCurrent);
                    Assert.False(completing.Info.IsDispatchTarget);
                    Assert.Equal(1, completing.Info.State);
                    Assert.Equal(completing.Info.CodeAddress, stackSource.GetFrameCodeAddress(completing.Index));
                    Assert.Contains(finishing.SymbolName,
                        stackSource.TraceLog.CodeAddresses[completing.Info.CodeAddress].FullMethodName);
                    var target = Assert.Single(frames.Where(frame => frame.Name.Contains("Scenario.ParentAsync")));
                    Assert.Equal(completed, target.Info.IsDispatchTarget);
                    Assert.False(target.Info.IsCompleting);
                    Assert.False(target.Info.IsCurrent);
                    Assert.Equal(0, target.Info.State);
                    if (enableFrameAnnotations)
                    {
                        Assert.StartsWith("[Async V1] ", completing.Name);
                        Assert.EndsWith(completed ? "[ordinal 11, state -2]" : "[ordinal 11, state -1]", completing.Name);
                        Assert.EndsWith("[ordinal 10, state 0]", target.Name);
                    }
                    else
                    {
                        Assert.DoesNotContain("[Async V1]", completing.Name);
                        Assert.DoesNotContain("state ", completing.Name);
                    }
                },
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void StateMachineAsync_CompletionTail_AnnotationsDoNotLeakAcrossIdenticalRawIdentities(bool enableFrameAnnotations)
        {
            Frame method = Frame.App("Scenario+<IdentityAsync>d__1.MoveNext");
            Frame parent = Frame.App("Scenario+<ParentAsync>d__2.MoveNext");
            Frame completing = method.Logical("Scenario.IdentityAsync", enableFrameAnnotations ? -2 : (int?)null);
            Frame running = method.Logical("Scenario.IdentityAsync", enableFrameAnnotations ? -1 : (int?)null);
            Frame captured = method.Logical("Scenario.IdentityAsync", enableFrameAnnotations ? 7 : (int?)null);
            Frame target = parent.Logical("Scenario.ParentAsync", enableFrameAnnotations ? 0 : (int?)null);
            var scenario = new StitchScenario
            {
                Kind = AsyncCallstackKind.StateMachineAsync,
                Sync = new[] { method, Frame.V1MoveNextAsDispatcher, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart },
                Async = new[] { captured, target },
                AsyncStates = new[] { 7, 0 },
                MethodCompletions = 1,
                EnableFrameAnnotations = enableFrameAnnotations,
                SkipNormalStackAssertion = true,
                ExpectedStitched = new[] { completing, running, captured, target, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation { SegmentsProcessed = 2, V1CompletionTailUsed = 2 },
                VerifyPublicBuilder = false, // This emitter test deliberately fabricates roles through internal factories.
                AssertTransformContext = context =>
                {
                    // Exercise emitter cache separation with consumer-added roles sharing the same raw identity.
                    AsyncProfilerStitchedFrame frame = context.Frames[0];
                    Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCompleting, frame.Origin);
                    context.Frames.Insert(1, AsyncProfilerStitchedFrame.CreateAsyncCurrent(
                        frame.CodeAddress, frame.Method, frame.Context, frame.SegmentFrameIndex));
                    context.Frames.Insert(2, AsyncProfilerStitchedFrame.CreateAsync(frame.Context, frame.SegmentFrameIndex));
                },
                AssertStitchedStackSource = stackSource =>
                {
                    var frames = new List<(StackSourceFrameIndex Index, string Name, AsyncProfilerCallStackSourceFrameInfo Info)>();
                    for (int i = 0; i < stackSource.CallFrameIndexLimit; i++)
                    {
                        var index = (StackSourceFrameIndex)i;
                        if (stackSource.TryGetAsyncProfilerCallStackFrameInfo(index, out AsyncProfilerCallStackSourceFrameInfo info) && info.State == 7)
                        {
                            frames.Add((index, stackSource.GetFrameName(index, false), info));
                        }
                    }
                    Assert.Equal(3, frames.Count);
                    Assert.Equal(3, frames.Select(frame => frame.Index).Distinct().Count());
                    Assert.Single(frames.Select(frame => frame.Info.CodeAddress).Distinct());
                    Assert.Single(frames.Select(frame => frame.Info.MethodId).Distinct());
                    Assert.Single(frames.Where(frame => frame.Info.IsCompleting && !frame.Info.IsCurrent));
                    Assert.Single(frames.Where(frame => frame.Info.IsCurrent && !frame.Info.IsCompleting));
                    Assert.Single(frames.Where(frame => !frame.Info.IsCurrent && !frame.Info.IsCompleting));
                    Assert.Equal(enableFrameAnnotations ? 3 : 1, frames.Select(frame => frame.Name).Distinct().Count());
                    if (enableFrameAnnotations)
                    {
                        Assert.EndsWith("state -2]", frames.Single(frame => frame.Info.IsCompleting).Name);
                        Assert.EndsWith("state -1]", frames.Single(frame => frame.Info.IsCurrent).Name);
                        Assert.EndsWith("state 7]", frames.Single(frame => !frame.Info.IsCompleting && !frame.Info.IsCurrent).Name);
                    }
                },
            };
            scenario.AssertProductionStitch();
        }

        [Fact]
        public void StateMachineAsync_SameLogicalNameDifferentStateMachines_HaveDistinctDisplayNames()
        {
            Frame firstMoveNext = Frame.App("Scenario+<OverloadedAsync>d__1.MoveNext");
            Frame secondMoveNext = Frame.App("Scenario+<OverloadedAsync>d__2.MoveNext");
            Frame firstLogical = firstMoveNext.Logical("Scenario.OverloadedAsync");
            Frame secondLogical = secondMoveNext.Logical("Scenario.OverloadedAsync");
            var scenario = new StitchScenario
            {
                Kind = AsyncCallstackKind.StateMachineAsync,
                Sync = new[]
                {
                    firstMoveNext,
                    Frame.V1MoveNextAsDispatcher,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                AsyncSegments = new[]
                {
                    AsyncSegment.StateMachine(firstLogical, secondLogical),
                },
                ExpectedStitched = new[]
                {
                    firstLogical,
                    secondLogical,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V1InlineFallbackUsed = 2,
                },
                AssertStitchedStackSource = stackSource =>
                {
                    var names = new HashSet<string>();
                    stackSource.ForEach(sample =>
                    {
                        StackSourceCallStackIndex stackIndex = sample.StackIndex;
                        while (stackIndex != StackSourceCallStackIndex.Invalid)
                        {
                            StackSourceFrameIndex frameIndex = stackSource.GetFrameIndex(stackIndex);
                            if (stackSource.TryGetAsyncProfilerCallStackFrameInfo(frameIndex, out AsyncProfilerCallStackSourceFrameInfo info) &&
                                info.Kind == AsyncCallstackKind.StateMachineAsync)
                            {
                                names.Add(stackSource.GetFrameName(frameIndex, false));
                            }
                            stackIndex = stackSource.GetCallerIndex(stackIndex);
                        }
                    });

                    Assert.Contains(names, name => name.EndsWith("Scenario.OverloadedAsync [ordinal 1]"));
                    Assert.Contains(names, name => name.EndsWith("Scenario.OverloadedAsync [ordinal 2]"));
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false, "none")]
        [InlineData(true, false, "none")]
        [InlineData(true, true, "none")]
        [InlineData(false, true, "none")]
        [InlineData(true, true, "pdb")]
        [InlineData(true, true, "foreign")]
        [InlineData(true, true, "unresolved")]
        [InlineData(true, true, "interrupted")]
        public void StateMachineAsync_BoxAllocation_GroupsConstructorsWithoutHidingCallees(
            bool enableNormalization, bool enableInfrastructureCleanup, string layout)
        {
            Frame[] native =
            {
                Frame.Library("clrjit", "Native.JitCompile", "JitFrame"),
                Frame.Library("coreclr", "Native.PrepareMethod", "RuntimeFrame"),
            };
            const string box = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+";
            const string arguments = "`1[System.Int64,Scenario+<DeepLevel2Async>d__9]";
            string constructor = layout == "pdb" ? "::.ctor()" : "..ctor()";
            string profilerConstructor = box + "AsyncProfilerAsyncStateMachineBox" + arguments + constructor;
            Frame profiler = layout == "foreign"
                ? Frame.Library("OtherLibrary", profilerConstructor, "ProfilerBoxConstructor")
                : Frame.CoreLib(profilerConstructor, "ProfilerBoxConstructor");
            Frame finalizable = Frame.CoreLib(
                box + "DebugFinalizableAsyncStateMachineBox" + arguments + constructor, "FinalizableBoxConstructor");
            Frame callback = Frame.App("Scenario.AllocationCallback");
            Frame[] constructors = layout == "interrupted"
                ? new[] { profiler, callback, finalizable } : new[] { profiler, finalizable };
            Frame[] helpers =
            {
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].CreateDebugFinalizableAsyncStateMachineBox()", "CreateFinalizableBox"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].GetStateMachineBox(!!0&,class System.Threading.Tasks.Task`1<!0>&)", "GetBox"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].AwaitUnsafeOnCompleted(!!0&,!!1&,class System.Threading.Tasks.Task`1<!0>&)", "RegisterWithTask"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].AwaitUnsafeOnCompleted(!!0&,!!1&)", "RegisterAwait"),
            };
            Frame child = Frame.App("Scenario+<DeepLevel2Async>d__9.MoveNext");
            Frame[] startup =
            {
                child,
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start(!!0&)", "MethodBuilderCore.Start"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].Start(!!0&)", "MethodBuilder.Start"),
            };
            Frame kickoff = Frame.App("Scenario.DeepLevel2Async()");
            Frame parent = Frame.App("Scenario+<DeepLevel1Async>d__8.MoveNext");
            Frame current = parent.Logical("Scenario.DeepLevel1Async", -1);
            Frame yield = Frame.CoreLib(
                "System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter+<>c.<OutputCorrelationEtwEvent>b__6_0", "YieldCallback");
            Frame[] root = { yield, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart };
            Frame[] displayedRoot = enableInfrastructureCleanup ? root.Skip(1).ToArray() : root;
            Frame[] prefix = native.Concat(constructors).Concat(helpers).ToArray();
            Frame[] remainingStartup = enableNormalization ? Array.Empty<Frame>() : startup;
            Frame[] tail = remainingStartup.Concat(new[] { kickoff, current }).Concat(displayedRoot).ToArray();
            Frame[] grouped = layout == "interrupted"
                ? new[] { profiler.DispatcherFrames(), callback, finalizable.DispatcherFrames() }
                : layout == "foreign" || layout == "unresolved"
                    ? new[] { profiler, finalizable.DispatcherFrames() }
                    : new[] { profiler.DispatcherFrames() };
            if (!enableNormalization && enableInfrastructureCleanup)
            {
                tail = new[] { child, startup[1].DispatcherFrames(), kickoff, current }.Concat(displayedRoot).ToArray();
            }
            int recognizedHelpers = layout == "foreign" || layout == "unresolved" ? 5 : 6;
            var scenario = new StitchScenario
            {
                Kind = AsyncCallstackKind.StateMachineAsync,
                Sync = prefix.Concat(startup).Concat(new[] { kickoff, current, Frame.V1MoveNextAsDispatcher }).Concat(root).ToArray(),
                Async = new[] { parent },
                AsyncStates = new[] { 0 },
                OmitSymbolFor = layout == "unresolved" ? profiler : null,
                EnableFrameAnnotations = true,
                EnableNormalization = enableNormalization,
                EnableInfrastructureCleanup = enableInfrastructureCleanup,
                IncludeEventSourceEvents = false,
                VerifyThreadTimeComputer = true,
                VerifySampleWeights = true,
                ExpectedStitched = enableInfrastructureCleanup ? native.Concat(grouped).Concat(tail).ToArray() : prefix.Concat(tail).ToArray(),
                ExpectedWithoutInfrastructureCleanup = prefix.Concat(remainingStartup).Concat(new[] { kickoff, current }).Concat(root).ToArray(),
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V1InlineFallbackUsed = 2,
                    V1SynchronousStartupFramesCollapsed = enableNormalization ? 6 : 0,
                    AsyncInfrastructureFramesCollapsed = enableInfrastructureCleanup
                        ? (recognizedHelpers + 1 + (enableNormalization ? 0 : 2)) * 2 : 0,
                    RuntimeResumptionBridgeFramesCollapsed = enableInfrastructureCleanup ? 2 : 0,
                },
                AssertTransformContext = context =>
                {
                    AsyncProfilerStitchedFrame active = Assert.Single(context.Frames.Where(frame =>
                        frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent));
                    Assert.False(active.IsStateMachineBoxCarrier);
                    Assert.DoesNotContain(context.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget);
                    AsyncProfilerStitchedFrame[] markers = context.Frames.Where(frame =>
                        frame.Presentation == AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames).ToArray();
                    Assert.Equal(enableInfrastructureCleanup
                        ? (layout == "interrupted" ? 2 : 1) + (enableNormalization ? 0 : 1) : 0, markers.Length);
                    Assert.All(markers, marker =>
                    {
                        Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, marker.Origin);
                        Assert.Same(active.Context, marker.Context);
                        Assert.Equal(MethodIndex.Invalid, marker.BoxedStateMachineMethod);
                    });
                    Assert.Equal(native.Select(frame => frame.SymbolName),
                        context.Frames.Take(native.Length).Select(frame =>
                            context.TraceLog.CodeAddresses[frame.CodeAddress].FullMethodName));
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, true, false)]
        [InlineData(false, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, false, false)]
        [InlineData(true, true, false)]
        [InlineData(true, false, true)]
        [InlineData(true, true, true)]
        public void AsyncMachineryCallees_KeepTheirBoundaryAboveThePhysicalApplicationBody(
            bool runtimeAsync, bool enableNormalization, bool enableInfrastructureCleanup)
        {
            Frame body = Frame.App(runtimeAsync ? "Scenario.CurrentAsync" : "Scenario+<CurrentAsync>d__8.MoveNext");
            Frame current = runtimeAsync ? body : body.Logical("Scenario.CurrentAsync", -1);
            Frame helper = Frame.CoreLib(runtimeAsync
                ? "System.Threading.Tasks.Task.RunContinuations"
                : "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].AwaitUnsafeOnCompleted(!!0&,!!1&)",
                "RuntimeHelper");
            Frame[] callees = Enumerable.Range(0, 9)
                .Select(i => Frame.Library("coreclr", "Native.Work" + i, "NativeFrame" + i))
                .Concat(new[] { Frame.App("Observer.ArbitraryCallee") }).ToArray();
            Frame[] tail = { Frame.ThreadPoolDispatch, Frame.WorkerThreadStart };
            Frame boundary = runtimeAsync ? Frame.V2Wrapper(0) : Frame.V1MoveNextAsDispatcher;
            Frame[] plumbing = runtimeAsync
                ? new[] { Frame.V2InstrumentedDispatch, Frame.V2Dispatch } : Array.Empty<Frame>();
            var scenario = new StitchScenario
            {
                Kind = runtimeAsync ? AsyncCallstackKind.RuntimeAsync : AsyncCallstackKind.StateMachineAsync,
                Sync = callees.Concat(new[] { helper, current, boundary }).Concat(plumbing).Concat(tail).ToArray(),
                Async = new[] { body },
                AsyncStates = runtimeAsync ? null : new[] { 0 },
                OmitSymbolFor = callees[0],
                EnableNormalization = enableNormalization,
                EnableInfrastructureCleanup = enableInfrastructureCleanup,
                EnableFrameAnnotations = true,
                IncludeEventSourceEvents = false,
                VerifyThreadTimeComputer = true,
                VerifySampleWeights = true,
                ExpectedStitched = callees.Concat(new[]
                    { enableInfrastructureCleanup ? helper.DispatcherFrames() : helper, current }).Concat(tail).ToArray(),
                ExpectedWithoutInfrastructureCleanup = callees.Concat(new[] { helper, current }).Concat(tail).ToArray(),
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V1InlineFallbackUsed = runtimeAsync ? 0 : 2,
                    V2PlumbingFramesCollapsed = runtimeAsync ? 4 : 0,
                    AsyncInfrastructureFramesCollapsed = enableInfrastructureCleanup ? 2 : 0,
                    ProfilerEnvelopeFramesCollapsed = !runtimeAsync && enableInfrastructureCleanup ? 2 : 0,
                },
                AssertTransformContext = context =>
                {
                    AsyncProfilerStitchedFrame active = Assert.Single(context.Frames.Where(frame =>
                        frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent));
                    Assert.False(active.IsStateMachineBoxCarrier);
                    Assert.DoesNotContain(context.Frames, frame =>
                        frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget);
                    Assert.Equal(MethodIndex.Invalid, context.Frames[0].Method);
                    Assert.Equal(callees.Skip(1).Select(frame => frame.SymbolName),
                        context.Frames.Skip(1).Take(callees.Length - 1).Select(frame =>
                            context.TraceLog.CodeAddresses[frame.CodeAddress].FullMethodName));
                    AsyncProfilerStitchedFrame machinery = context.Frames[callees.Length];
                    Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, machinery.Origin);
                    Assert.Same(active.Context, machinery.Context);
                    Assert.Equal(helper.SymbolName, context.TraceLog.CodeAddresses[machinery.CodeAddress].FullMethodName);
                    Assert.Equal(enableInfrastructureCleanup
                        ? AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames
                        : AsyncProfilerStitchedFramePresentation.Native, machinery.Presentation);
                    Assert.Equal(callees.Length + 1, context.Frames.IndexOf(active));
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AsyncMachineryMarkers_MergeOnlyForTheSameCapturedRuntimeOwner(bool nativeAlias)
        {
            Frame first = Frame.Library(@"C:\runtime-a\System.Private.CoreLib.dll",
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1.GetStateMachineBox", "GetBox");
            Frame second = Frame.Library(nativeAlias
                ? @"C:\runtime-a\System.Private.CoreLib.ni.dll" : @"C:\runtime-b\System.Private.CoreLib.dll",
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1.CreateDebugFinalizableAsyncStateMachineBox", "CreateBox");
            Frame body = Frame.App("Scenario+<CurrentAsync>d__8.MoveNext");
            Frame current = body.Logical("Scenario.CurrentAsync", -1);
            Frame callee = Frame.App("Observer.ArbitraryCallee");
            Frame[] tail = { current, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart };
            Frame[] markers = nativeAlias
                ? new[] { first.DispatcherFrames() } : new[] { first.DispatcherFrames(), second.DispatcherFrames() };
            var scenario = new StitchScenario
            {
                Kind = AsyncCallstackKind.StateMachineAsync,
                Sync = new[] { callee, first, second, current, Frame.V1MoveNextAsDispatcher }
                    .Concat(tail.Skip(1)).ToArray(),
                Async = new[] { body },
                AsyncStates = new[] { 0 },
                EnableInfrastructureCleanup = true,
                EnableFrameAnnotations = true,
                IncludeEventSourceEvents = false,
                VerifyThreadTimeComputer = true,
                VerifySampleWeights = true,
                ExpectedStitched = new[] { callee }.Concat(markers).Concat(tail).ToArray(),
                ExpectedWithoutInfrastructureCleanup = new[] { callee, first, second }.Concat(tail).ToArray(),
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V1InlineFallbackUsed = 2,
                    AsyncInfrastructureFramesCollapsed = 4,
                },
                ConfigureTraceLog = log =>
                {
                    if (nativeAlias)
                    {
                        TraceModuleFile managed = log.ModuleFiles.Single(module =>
                            string.Equals(module.FilePath, first.ModuleName, StringComparison.OrdinalIgnoreCase));
                        log.ModuleFiles.Single(module =>
                            string.Equals(module.FilePath, second.ModuleName, StringComparison.OrdinalIgnoreCase)).managedModule = managed;
                    }
                },
                AssertTransformContext = context =>
                {
                    AsyncProfilerStitchedFrame[] actual = context.Frames.Where(frame =>
                        frame.Presentation == AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames).ToArray();
                    Assert.Equal(nativeAlias ? 1 : 2, actual.Length);
                    Assert.All(actual, frame => Assert.Same(context.Segments[0], frame.Context));
                    Assert.Equal(first.SymbolName, context.TraceLog.CodeAddresses[actual[0].CodeAddress].FullMethodName);
                    if (!nativeAlias)
                    {
                        Assert.Same(context.GetRuntimeProfile(actual[0].CodeAddress), context.GetRuntimeProfile(actual[1].CodeAddress));
                        Assert.NotEqual(context.GetHostModuleOwner(actual[0].CodeAddress), context.GetHostModuleOwner(actual[1].CodeAddress));
                    }
                },
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void RuntimeAsync_SynchronousV1Startup_IsNormalizedByNormalizationPipeline()
        {
            Frame work = Frame.App("Scenario.SynchronousV1Work");
            Frame generatedV1 = Frame.App("Scenario+<SynchronousV1Async>d__4.MoveNext");
            Frame builderStart = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder.Start(Scenario+<SynchronousV1Async>d__4&)",
                "AsyncTaskMethodBuilder.Start");
            Frame kickoffV1 = Frame.App("Scenario.SynchronousV1Async(int32)");
            Frame outerCurrent = Frame.App("Scenario.OuterV2Async");
            Frame outerParent = Frame.App("Scenario.OuterV2ParentAsync");

            var scenario = new StitchScenario
            {
                Sync = new[]
                {
                    work,
                    generatedV1,
                    builderStart,
                    kickoffV1,
                    outerCurrent,
                    Frame.V2Wrapper(0),
                    Frame.V2InstrumentedDispatch,
                    Frame.V2Dispatch,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                Async = new[]
                {
                    outerCurrent,
                    outerParent,
                },
                ExpectedStitched = new[]
                {
                    work,
                    kickoffV1,
                    outerCurrent,
                    outerParent,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                ExpectedWithoutNormalization = new[]
                {
                    work,
                    generatedV1,
                    builderStart,
                    kickoffV1,
                    outerCurrent,
                    outerParent,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V1SynchronousStartupFramesCollapsed = 4,
                    V2PlumbingFramesCollapsed = 4,
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void StateMachineAsync_ReentrantAwaitRegistration_DeduplicatesStartupActivation(bool changedLayout)
        {
            Frame work = Frame.App("Scenario.ReentrantWork");
            Frame innerStateMachine = Frame.App("Scenario+<NestedDispatcherInnerAsync>d__15.MoveNext");
            Frame logicalInner = innerStateMachine.Logical("Scenario.NestedDispatcherInnerAsync");
            Frame innerKickoff = Frame.App("Scenario.NestedDispatcherInnerAsync");
            Frame outerStateMachine = Frame.App("Scenario+<NestedDispatcherOuterAsync>d__14.MoveNext");
            Frame logicalOuter = outerStateMachine.Logical("Scenario.NestedDispatcherOuterAsync");
            Frame innerInfrastructure = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncProfilerAsyncStateMachineBox.InstrumentedMoveNext",
                "Inner.InstrumentedMoveNext");
            Frame awaitGeneric = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].AwaitUnsafeOnCompleted(!!0&,!!1&)",
                "AwaitUnsafeOnCompleted(generic)");
            Frame awaitTask = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].AwaitUnsafeOnCompleted(!!0&,!!1&,class System.Threading.Tasks.Task`1<!0>&)",
                "AwaitUnsafeOnCompleted(task)");
            Frame awaitBox = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].AwaitUnsafeOnCompleted(!!0&,class System.Runtime.CompilerServices.IAsyncStateMachineBox)",
                "AwaitUnsafeOnCompleted(box)");
            Frame builderStart = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].Start(!!0&)",
                "AsyncTaskMethodBuilder.Start");
            Frame builderCoreStart = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start(!!0&)",
                "AsyncMethodBuilderCore.Start");

            var scenario = new StitchScenario
            {
                Kind = AsyncCallstackKind.StateMachineAsync,
                Sync = new[]
                {
                    work,
                    innerStateMachine,
                    Frame.V1MoveNextAsDispatcher,
                    innerInfrastructure,
                    awaitGeneric,
                    awaitTask,
                    awaitBox,
                    innerStateMachine,
                    builderStart,
                    builderCoreStart,
                    innerKickoff,
                    outerStateMachine,
                    Frame.V1MoveNextAsDispatcher,
                    innerInfrastructure,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                AsyncSegments = new[]
                {
                    AsyncSegment.StateMachine(logicalOuter),
                    AsyncSegment.StateMachine(logicalInner),
                },
                ExpectedStitched = new[]
                {
                    work,
                    logicalInner,
                    logicalOuter,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                ExpectedWithoutNormalization = new[]
                {
                    work,
                    logicalInner,
                    awaitGeneric,
                    awaitTask,
                    awaitBox,
                    innerStateMachine,
                    builderStart,
                    builderCoreStart,
                    innerKickoff,
                    logicalOuter,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 4,
                    V1InlineFallbackUsed = 4,
                    V1InfrastructureFramesCollapsed = 4,
                    V1ReentrantFramesCollapsed = 14,
                },
            };

            if (changedLayout)
            {
                UseExtendedRuntimeLayouts(scenario);
                Frame helper = Frame.CoreLib(AsyncProfilerExtendedRuntimeLayoutProfile.HelperName, "LayoutHelper");
                Func<Frame[], Frame[]> extend = frames => frames.SelectMany(frame =>
                    frame.Key == awaitBox.Key ? new[] { frame, helper } :
                    frame.Key == builderStart.Key ? new[] { helper, frame } : new[] { frame }).ToArray();
                // Registration helpers precede the duplicate body; startup helpers follow it.
                scenario.Sync = extend(scenario.Sync);
                scenario.ExpectedWithoutNormalization = extend(scenario.ExpectedWithoutNormalization);
                scenario.ExpectedDiagnostics.V1ReentrantFramesCollapsed += 4;
            }
            scenario.AssertProductionStitch();
        }

        [Fact]
        public void StateMachineAsync_ReentrantPatternInterruptedByUnknownFrame_IsPreserved()
        {
            Frame work = Frame.App("Scenario.ReentrantWork");
            Frame innerStateMachine = Frame.App("Scenario+<NestedDispatcherInnerAsync>d__15.MoveNext");
            Frame logicalInner = innerStateMachine.Logical("Scenario.NestedDispatcherInnerAsync");
            Frame innerKickoff = Frame.App("Scenario.NestedDispatcherInnerAsync");
            Frame outerStateMachine = Frame.App("Scenario+<NestedDispatcherOuterAsync>d__14.MoveNext");
            Frame logicalOuter = outerStateMachine.Logical("Scenario.NestedDispatcherOuterAsync");
            Frame infrastructure = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncProfilerAsyncStateMachineBox.InstrumentedMoveNext",
                "InstrumentedMoveNext");
            Frame awaitRegistration = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].AwaitUnsafeOnCompleted(!!0&,!!1&)",
                "AwaitUnsafeOnCompleted");
            Frame unknown = Frame.CoreLib("TestOnly.UnknownRegistrationBridge", "UnknownRegistrationBridge");
            Frame builderStart = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].Start(!!0&)",
                "AsyncTaskMethodBuilder.Start");

            var scenario = new StitchScenario
            {
                Kind = AsyncCallstackKind.StateMachineAsync,
                Sync = new[]
                {
                    work,
                    innerStateMachine,
                    Frame.V1MoveNextAsDispatcher,
                    infrastructure,
                    awaitRegistration,
                    unknown,
                    innerStateMachine,
                    builderStart,
                    innerKickoff,
                    outerStateMachine,
                    Frame.V1MoveNextAsDispatcher,
                    infrastructure,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                AsyncSegments = new[]
                {
                    AsyncSegment.StateMachine(logicalOuter),
                    AsyncSegment.StateMachine(logicalInner),
                },
                ExpectedStitched = new[]
                {
                    work,
                    logicalInner,
                    awaitRegistration,
                    unknown,
                    innerKickoff,
                    logicalOuter,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 4,
                    V1InlineFallbackUsed = 4,
                    V1InfrastructureFramesCollapsed = 4,
                    V1SynchronousStartupFramesCollapsed = 4,
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("common", false)]
        [InlineData("common", true)]
        [InlineData("reordered", false)]
        [InlineData("reordered", true)]
        [InlineData("extended", false)]
        [InlineData("extended", true)]
        public void StateMachineAsync_ReentrantDispatchEntry_DeduplicatesStartupWithoutClaimingBodyExecution(
            string layout, bool enableInfrastructureCleanup)
        {
            StitchScenario scenario = CreateReentrantDispatchEntryScenario();
            if (layout == "extended")
            {
                UseExtendedRuntimeLayouts(scenario);
                Frame helper = Frame.CoreLib(AsyncProfilerExtendedRuntimeLayoutProfile.HelperName, "LayoutHelper");
                Func<Frame[], Frame[]> extend = frames => frames.SelectMany(frame =>
                    frame.Label == "AwaitUnsafeOnCompleted(generic)" ? new[] { frame, helper } :
                    frame.Label == "AsyncMethodBuilderCore.Start" ? new[] { helper, frame } : new[] { frame }).ToArray();
                scenario.Sync = extend(scenario.Sync);
                scenario.ExpectedWithoutNormalization = extend(scenario.ExpectedWithoutNormalization);
                scenario.ExpectedDiagnostics.V1ReentrantFramesCollapsed += 4;
            }
            else if (layout == "reordered")
            {
                Array.Reverse(scenario.Sync, 3, 3);
                Array.Reverse(scenario.Sync, 7, 2);
                Array.Reverse(scenario.ExpectedWithoutNormalization, 3, 3);
                Array.Reverse(scenario.ExpectedWithoutNormalization, 7, 2);
            }
            if (enableInfrastructureCleanup)
            {
                scenario.EnableInfrastructureCleanup = true;
                scenario.ExpectedWithoutInfrastructureCleanup = scenario.ExpectedStitched;
                scenario.ExpectedStitched = scenario.ExpectedStitched.Skip(2).ToArray();
                scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 4;
                scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = 4;
            }
            scenario.AssertTransformContext = context =>
            {
                AsyncProfilerStitchedFrame target = Assert.Single(context.Frames.Where(frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget));
                Assert.Same(context.Segments[1], target.Context);
                Assert.Equal(0, target.Segment.FrameStateAt(target.SegmentFrameIndex));
                Assert.DoesNotContain(context.Frames, frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent &&
                    ReferenceEquals(frame.Context, target.Context));
                Assert.DoesNotContain(context.Frames, frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                    frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1StateMachineMoveNext);
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("different-context")]
        [InlineData("missing-dispatcher")]
        [InlineData("recursive-outer")]
        [InlineData("interrupted-registration")]
        [InlineData("duplicate-startup")]
        public void StateMachineAsync_ReentrantDispatchEntry_PreservesUnverifiedStartup(string obstruction)
        {
            StitchScenario scenario = CreateReentrantDispatchEntryScenario();
            scenario.EnableNormalization = false;
            scenario.ExpectedStitched = scenario.ExpectedWithoutNormalization;
            scenario.ExpectedDiagnostics.V1ReentrantFramesCollapsed = 0;
            scenario.AssertTransformContext = context =>
            {
                var frames = context.Frames.ToList();
                AsyncProfilerStitchedFrame startup = frames[6];
                AsyncProfilerStitchedFrame outer = frames[10];
                if (obstruction == "different-context")
                {
                    AsyncProfilerStitchedFrame registration = frames[3];
                    frames[3] = AsyncProfilerStitchedFrame.CreateSync(
                        new AsyncProfilerStitchSyncFrame(registration.CodeAddress, registration.Method, registration.SyncFrameKind),
                        frames[2].Context);
                }
                else if (obstruction == "missing-dispatcher")
                {
                    frames.RemoveAt(1);
                }
                else if (obstruction == "recursive-outer")
                {
                    frames[10] = AsyncProfilerStitchedFrame.CreateAsyncCurrent(
                        startup.CodeAddress, startup.Method, outer.Context, 0);
                }
                else if (obstruction == "interrupted-registration")
                {
                    frames.Insert(4, AsyncProfilerStitchedFrame.CreateSync(
                        new AsyncProfilerStitchSyncFrame(frames[3].CodeAddress, frames[3].Method), outer.Context));
                }
                else
                {
                    frames.InsertRange(10, frames.Skip(6).Take(4).ToArray());
                }
                var diagnostics = new AsyncProfilerCallStackDiagnostics { SegmentsProcessed = context.Segments.Count };
                new AsyncProfilerCallStackTransformPipeline().ApplyInPlace(new AsyncProfilerCallStackTransformState(
                    context.TraceLog, context.Segments, context.TimestampQpc, context.ClassifyBoundary,
                    diagnostics, frames));

                Assert.Equal(0, diagnostics.V1ReentrantFramesCollapsed);
                Assert.Single(frames.Where(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget));
                Assert.Contains(frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                    frame.CodeAddress == context.Frames[9].CodeAddress);
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void StateMachineAsync_DeepResume_StitchesSuspendedCallersAndRemovesSchedulingFrames()
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
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
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
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V1InlineFallbackUsed = 2,
                    V1InfrastructureFramesCollapsed = 6,
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("AsyncStateMachineBox", "MoveNext()", false, false, true, null)]
        [InlineData("AsyncStateMachineBox", "MoveNext(class System.Threading.Thread)", true, false, true, null)]
        [InlineData("AsyncStateMachineBox", "InstrumentedMoveNext(class System.Threading.Thread)", true, true, true, null)]
        [InlineData("AsyncProfilerAsyncStateMachineBox", "InstrumentedMoveNext(class System.Threading.Thread)", true, false, false, null)]
        [InlineData("ProfilerAsyncStateMachineBox", "MoveNext()", true, false, true, null)]
        [InlineData("DebugFinalizableAsyncStateMachineBox", "MoveNext(class System.Threading.Thread)", true, false, true, null)]
        [InlineData("AsyncStateMachineBox", "InstrumentedMoveNext(class System.Threading.Thread)", false, true, false, null)]
        [InlineData("AsyncStateMachineBox", "MoveNext()", false, false, true, "11.0.0")]
        [InlineData("AsyncStateMachineBox", "InstrumentedMoveNext(class System.Threading.Thread)", true, true, true, "11.0.0")]
        [InlineData("AsyncStateMachineBox", "InstrumentedMoveNext(class System.Threading.Thread)", false, true, false, "11.0.0")]
        [InlineData("AsyncProfilerAsyncStateMachineBox", "InstrumentedMoveNext(class System.Threading.Thread)", true, false, false, "11.0.0")]
        [InlineData("AsyncStateMachineBox", "InstrumentedMoveNext(class System.Threading.Thread)", true, true, true, "12.0.0")]
        [InlineData("AsyncStateMachineBox", "InstrumentedMoveNext(class System.Threading.Thread)", true, true, true, null, true)]
        [InlineData("AsyncStateMachineBox", "InstrumentedMoveNext(class System.Threading.Thread)", true, true, true, "11.0.0", true)]
        public void StateMachineAsync_LeafBoxCarrier_RepresentsTheResumedParentOnce(
            string boxType, string member, bool enableFrameAnnotations, bool enableInfrastructureCleanup, bool enableNormalization, string productVersion,
            bool perfMap = false)
        {
            string separator = perfMap ? "::" : ".";
            Frame child = Frame.App("Scenario+<BurnCpuAsync>d__63" + separator + "MoveNext");
            Frame parent = Frame.App("Scenario+<ParentAsync>d__12" + separator + "MoveNext");
            Frame ancestor = Frame.App("Scenario+<AncestorAsync>d__11" + separator + "MoveNext");
            Frame capturedParent = parent.Logical("Scenario.ParentAsync", enableFrameAnnotations ? 0 : (int?)null);
            Frame runningParent = parent.Logical("Scenario.ParentAsync", enableFrameAnnotations ? 0 : (int?)null);
            Frame capturedAncestor = ancestor.Logical("Scenario.AncestorAsync", enableFrameAnnotations ? 1 : (int?)null);
            const string boxPrefix = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+";
            string boxArguments = "`1[System.Int64,Scenario+<ParentAsync>d__12]" + separator;
            Frame leaf = Frame.CoreLib(boxPrefix + boxType + boxArguments + member, "ParentBoxLeaf");
            Frame profiler = Frame.CoreLib(
                boxPrefix + "AsyncProfilerAsyncStateMachineBox" + boxArguments + "InstrumentedMoveNext()", "ParentBoxOverride");
            Frame boxMoveNext = Frame.CoreLib(
                boxPrefix + "AsyncStateMachineBox" + boxArguments + "MoveNext()", "ParentBoxExecution");
            if (boxMoveNext.Key == leaf.Key)
            {
                boxMoveNext = leaf;
            }
            Frame runContinuations = Frame.CoreLib("System.Threading.Tasks.Task.RunContinuations()", "Task.RunContinuations");
            Frame trySetResult = Frame.CoreLib("System.Threading.Tasks.Task`1[System.Int64].TrySetResult(!0)", "Task.TrySetResult");
            Frame completion = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].SetExistingTaskResult(class System.Threading.Tasks.Task`1<!0>,!0)",
                "SetExistingTaskResult");
            var scenario = new StitchScenario
            {
                Kind = AsyncCallstackKind.StateMachineAsync,
                Sync = new[]
                {
                    leaf, profiler, boxMoveNext, runContinuations, trySetResult, completion, child,
                    Frame.V1MoveNextAsDispatcher, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart,
                },
                Async = new[] { child.Logical("Scenario.BurnCpuAsync"), capturedParent, capturedAncestor },
                AsyncStates = new[] { 0, 0, 1 },
                EnableFrameAnnotations = enableFrameAnnotations,
                EnableInfrastructureCleanup = enableInfrastructureCleanup,
                EnableNormalization = enableNormalization,
                VerifyThreadTimeComputer = true,
                IncludeEventSourceEvents = false,
                AllowSupplementalSamples = true,
                VerifySampleWeights = true,
                ConfigureTraceLog = log => ConfigureCapturedRuntime(log, productVersion),
                ExpectedStitched = new[] { leaf.DispatcherFrames(), runningParent, capturedAncestor, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation { SegmentsProcessed = 2, V1InlineFallbackUsed = 2 },
                AssertTransformContext = context =>
                {
                    AsyncProfilerStitchedFrame current = Assert.Single(context.Frames.Where(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget));
                    Assert.True(current.IsStateMachineBoxCarrier);
                    Assert.Equal(1, current.SegmentFrameIndex);
                    Assert.Equal(0, current.Segment.FrameStateAt(1));
                    Assert.Equal(leaf.SymbolName, context.TraceLog.CodeAddresses[current.CodeAddress].FullMethodName);
                    Assert.NotEqual(current.Method, current.BoxedStateMachineMethod);
                    AsyncProfilerStitchedFrame marker = Assert.Single(context.Frames.Where(frame =>
                        frame.Presentation == AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames));
                    Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, marker.Origin);
                    Assert.Equal(current.CodeAddress, marker.CodeAddress);
                    Assert.Same(current.Context, marker.Context);
                    Assert.Equal(marker, context.Frames[0]);
                    Assert.Equal(current, context.Frames[1]);
                    Assert.DoesNotContain(context.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
                },
                AssertStitchedStackSource = stackSource =>
                {
                    int currents = 0;
                    for (int i = 0; i < stackSource.CallFrameIndexLimit; i++)
                    {
                        var index = (StackSourceFrameIndex)i;
                        if (!stackSource.TryGetAsyncProfilerCallStackFrameInfo(index, out AsyncProfilerCallStackSourceFrameInfo info) || !info.IsStateMachineBoxCarrier)
                        {
                            continue;
                        }
                        currents++;
                        Assert.True(info.IsStateMachineBoxCarrier);
                        Assert.True(info.IsDispatchTarget);
                        Assert.False(info.IsCurrent);
                        Assert.Equal(0, info.State);
                        Assert.Equal(leaf.SymbolName, stackSource.TraceLog.CodeAddresses[info.CodeAddress].FullMethodName);
                        Assert.Equal(info.CodeAddress, stackSource.GetFrameCodeAddress(index));
                        string name = stackSource.GetFrameName(index, false);
                        Assert.Contains("testapp!Scenario.ParentAsync", name);
                        Assert.DoesNotContain("AsyncStateMachineBox", name);
                        if (enableFrameAnnotations)
                        {
                            Assert.StartsWith("[Async V1] ", name);
                            Assert.EndsWith("[ordinal 12, state 0]", name);
                        }
                        else
                        {
                            Assert.DoesNotContain("state ", name);
                        }
                    }
                    Assert.Equal(1, currents);
                },
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false, false, 0)]
        [InlineData(true, false, false, 0)]
        [InlineData(true, true, false, 0)]
        [InlineData(false, false, false, 5)]
        [InlineData(true, true, true, 0)]
        [InlineData(false, true, false, 0)]
        [InlineData(false, true, false, 5)]
        public void StateMachineAsync_BoxCarrierWithEventSourceCallees_MarksMachineryBeforeThePendingTarget(
            bool enableNormalization, bool enableInfrastructureCleanup, bool profilerEntry, int capturedState)
        {
            Frame body = Frame.App("Scenario+<DeepLevel1Async>d__8.MoveNext");
            Frame target = body.Logical("Scenario.DeepLevel1Async", capturedState);
            Frame[] callees =
            {
                Frame.CoreLib("System.Collections.Generic.Dictionary`2[System.Int32,System.Diagnostics.Tracing.EventSource+EventMetadata].FindValue(System.Int32)", "FindValue"),
                Frame.CoreLib("System.Diagnostics.Tracing.EventSource.WriteEventWithRelatedActivityIdCore", "WriteEventWithRelatedActivityIdCore"),
                Frame.CoreLib("System.Diagnostics.Tracing.EventSource.WriteEvent(System.Int32,System.Int32,System.Int32)", "WriteEventPayload"),
            };
            Frame resume = Frame.CoreLib("System.Runtime.CompilerServices.AsyncProfiler+ResumeAsyncContext.Resume", "Resume");
            Frame carrier = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[System.Int64,Scenario+<DeepLevel1Async>d__8].MoveNext(class System.Threading.Thread,value class Flags)",
                "PendingBox");
            Frame yield = Frame.CoreLib(
                "System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter+<>c.<OutputCorrelationEtwEvent>b__6_0",
                "YieldCallback");
            Frame[] tail = { yield, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart };
            Frame[] expected = callees.Concat(new[] { carrier.DispatcherFrames(), target }).Concat(tail).ToArray();
            var scenario = new StitchScenario
            {
                Kind = AsyncCallstackKind.StateMachineAsync,
                Sync = callees.Concat(profilerEntry ? new[] { resume } : Array.Empty<Frame>())
                    .Concat(new[] { carrier, Frame.V1MoveNextAsDispatcher }).Concat(tail).ToArray(),
                Async = new[] { body },
                AsyncStates = new[] { capturedState },
                EnableFrameAnnotations = true,
                EnableNormalization = enableNormalization,
                EnableInfrastructureCleanup = enableInfrastructureCleanup,
                IncludeEventSourceEvents = false,
                VerifyThreadTimeComputer = true,
                VerifySampleWeights = true,
                ExpectedStitched = enableInfrastructureCleanup ? expected.Where(frame => frame != yield).ToArray() : expected,
                ExpectedWithoutInfrastructureCleanup = callees
                    .Concat(profilerEntry ? new[] { resume } : Array.Empty<Frame>())
                    .Concat(expected.Skip(callees.Length)).ToArray(),
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V1InlineFallbackUsed = 2,
                    AsyncInfrastructureFramesCollapsed = (profilerEntry ? 4 : 0) + (enableInfrastructureCleanup ? 2 : 0),
                    RuntimeResumptionBridgeFramesCollapsed = enableInfrastructureCleanup ? 2 : 0,
                    ProfilerEnvelopeFramesCollapsed = profilerEntry ? 4 : 0,
                },
                AssertTransformContext = context =>
                {
                    AsyncProfilerStitchedFrame pending = Assert.Single(context.Frames.Where(frame => frame.IsStateMachineBoxCarrier));
                    Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget, pending.Origin);
                    Assert.Equal(capturedState, pending.Segment.FrameStateAt(pending.SegmentFrameIndex));
                    Assert.Equal(callees.Length + 1, context.Frames.IndexOf(pending));
                    AsyncProfilerStitchedFrame marker = Assert.Single(context.Frames.Where(frame =>
                        frame.Presentation == AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames));
                    Assert.Equal(marker, context.Frames[callees.Length]);
                    Assert.Same(pending.Context, marker.Context);
                    Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, marker.Origin);
                    Assert.Equal(profilerEntry ? resume.SymbolName : carrier.SymbolName,
                        context.TraceLog.CodeAddresses[marker.CodeAddress].FullMethodName);
                    Assert.DoesNotContain(context.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false, 0)]
        [InlineData(false, true, 5)]
        [InlineData(true, false, 0)]
        [InlineData(true, true, 0)]
        [InlineData(true, false, 5)]
        public void StateMachineAsync_BoxCarrierWithInitialization_PreservesCapturedStateAndSynchronousCallees(
            bool truncated, bool enableInfrastructureCleanup, int capturedState)
        {
            Frame child = Frame.App("Scenario+<Level24Async>d__42.MoveNext");
            Frame completing = Frame.App("Scenario+<Level23Async>d__41.MoveNext");
            Frame parent = Frame.App("Scenario+<Level22Async>d__40.MoveNext");
            Frame ancestor = Frame.App("Scenario+<Level21Async>d__39.MoveNext");
            Frame resuming = parent.Logical("Scenario.Level22Async", capturedState);
            Frame suspended = ancestor.Logical("Scenario.Level21Async", 1);
            Frame unknown = Frame.Library("NativeInitializer", "NativeInitializer.Run", "UnknownInitializer");
            Frame[] prefix =
            {
                Frame.CoreLib("System.Runtime.CompilerServices.InitHelpers.CallClassConstructor", "CallClassConstructor"),
                unknown,
                Frame.CoreLib("System.Runtime.CompilerServices.InitHelpers.InitClassSlow", "InitClassSlow"),
                Frame.CoreLib("System.Runtime.CompilerServices.StaticsHelpers.GetGCStaticBaseSlow", "GetGCStaticBaseSlow"),
            };
            const string box = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+";
            const string arguments = "`1[System.Int64,Scenario+<Level22Async>d__40].";
            Frame carrier = Frame.CoreLib(box + "AsyncStateMachineBox" + arguments +
                "MoveNext(class System.Threading.Thread,value class Flags)", "ResumingParentBox");
            Frame[] transition =
            {
                carrier,
                Frame.CoreLib(box + "AsyncStateMachineBox" + arguments +
                    "InstrumentedMoveNext(class System.Threading.Thread,value class Flags)", "ParentBaseInstrumentation"),
                Frame.CoreLib(box + "AsyncProfilerAsyncStateMachineBox" + arguments +
                    "InstrumentedMoveNext(class System.Threading.Thread,value class Flags)", "ParentProfilerInstrumentation"),
                Frame.CoreLib(box + "AsyncStateMachineBox" + arguments + "MoveNext()", "ParentBoxWrapper"),
                Frame.CoreLib("System.Threading.Tasks.Task.RunContinuations", "Task.RunContinuations"),
                Frame.CoreLib("System.Threading.Tasks.Task`1[System.Int64].TrySetResult(!0)", "Task.TrySetResult"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].SetExistingTaskResult", "SetExistingTaskResult"),
                completing,
                child,
            };
            Frame[] root = truncated ? Array.Empty<Frame>() : new[]
            {
                Frame.ThreadPoolDispatch, Frame.WorkerThreadStart,
            };
            Frame[] expected = prefix.Concat(new[] { carrier.DispatcherFrames(), resuming, suspended }).Concat(root).ToArray();
            var scenario = new StitchScenario
            {
                Kind = AsyncCallstackKind.StateMachineAsync,
                Sync = prefix.Concat(transition)
                    .Concat(truncated ? Array.Empty<Frame>() : new[] { Frame.V1MoveNextAsDispatcher })
                    .Concat(root).ToArray(),
                Async = new[] { child, completing, parent, ancestor },
                AsyncStates = new[] { 0, 0, capturedState, 1 },
                OmitSymbolFor = unknown,
                EnableFrameAnnotations = true,
                EnableInfrastructureCleanup = enableInfrastructureCleanup,
                OnlyManagedCodeStacks = true,
                IncludeEventSourceEvents = false,
                SkipNormalStackAssertion = true,
                VerifySampleWeights = true,
                VerifyThreadTimeComputer = true,
                ExpectedStitched = expected,
                ExpectedWithoutInfrastructureCleanup = expected,
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V1InlineFallbackUsed = 2,
                    BoundariesNotFound = truncated ? 2 : 0,
                    IncompleteAsyncRootsNormalized = truncated ? 2 : 0,
                },
                AssertTransformContext = context =>
                {
                    AsyncProfilerStitchedFrame target = Assert.Single(context.Frames.Where(frame => frame.IsStateMachineBoxCarrier));
                    Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget, target.Origin);
                    Assert.Equal(2, target.SegmentFrameIndex);
                    Assert.Equal(capturedState, target.Segment.FrameStateAt(2));
                    Assert.Equal(carrier.SymbolName, context.TraceLog.CodeAddresses[target.CodeAddress].FullMethodName);
                    Assert.Equal(prefix.Length + 1, context.Frames.IndexOf(target));
                    AsyncProfilerStitchedFrame marker = context.Frames[prefix.Length];
                    Assert.Equal(AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames, marker.Presentation);
                    Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, marker.Origin);
                    Assert.Equal(target.CodeAddress, marker.CodeAddress);
                    Assert.Same(target.Context, marker.Context);
                    Assert.DoesNotContain(context.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
                    Assert.Equal(truncated, context.Frames.Any(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.IncompleteRoot));
                },
                AssertStitchedStackSource = stackSource =>
                {
                    var targets = new List<AsyncProfilerCallStackSourceFrameInfo>();
                    for (int i = 0; i < stackSource.CallFrameIndexLimit; i++)
                    {
                        var index = (StackSourceFrameIndex)i;
                        if (!stackSource.TryGetAsyncProfilerCallStackFrameInfo(index, out AsyncProfilerCallStackSourceFrameInfo info) ||
                            !info.IsStateMachineBoxCarrier)
                        {
                            continue;
                        }
                        targets.Add(info);
                        Assert.True(info.IsDispatchTarget);
                        Assert.False(info.IsCurrent);
                        Assert.Equal(capturedState, info.State);
                        Assert.Equal(carrier.SymbolName, stackSource.TraceLog.CodeAddresses[info.CodeAddress].FullMethodName);
                        Assert.Equal(info.CodeAddress, stackSource.GetFrameCodeAddress(index));
                        Assert.EndsWith("[ordinal 40, state " + capturedState + "]", stackSource.GetFrameName(index, false));
                    }
                    Assert.Single(targets);
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("foreign-module")]
        [InlineData("unknown-member")]
        [InlineData("unknown-type")]
        [InlineData("ambiguous-type")]
        [InlineData("unresolved-carrier")]
        [InlineData("no-segment")]
        public void StateMachineAsync_LeafBoxCarrier_LeavesUnverifiedCarriersPhysical(string guard)
        {
            Frame child = Frame.App("Scenario+<BurnCpuAsync>d__63.MoveNext");
            Frame parent = Frame.App("Scenario+<ParentAsync>d__12.MoveNext");
            Frame capturedParent = parent.Logical("Scenario.ParentAsync", 0);
            Frame runningChild = child.Logical("Scenario.BurnCpuAsync", -1);
            string boxedType = guard == "unknown-type" ? "OtherScenario+<ParentAsync>d__12" : "Scenario+<ParentAsync>d__12";
            string member = guard == "unknown-member" ? "UnknownMoveNext" : "InstrumentedMoveNext";
            Frame leaf = Frame.Library(
                guard == "foreign-module" ? "OtherLib" : AsyncProfilerCallStackStitchBoundary.HostModuleName,
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[System.Int64," + boxedType + "]." + member + "()",
                "ParentBoxLeaf");
            var prefix = new List<Frame>();
            if (guard == "non-leaf")
            {
                prefix.Add(Frame.App("Scenario.Work"));
            }
            prefix.Add(leaf);
            Frame[] sync = prefix.Concat(
                new[] { child, Frame.V1MoveNextAsDispatcher, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart }).ToArray();
            var scenario = new StitchScenario
            {
                Kind = AsyncCallstackKind.StateMachineAsync,
                Sync = sync,
                Async = new[] { child, capturedParent },
                AsyncStates = new[] { 0, 0 },
                EnableFrameAnnotations = true,
                SkipNormalStackAssertion = guard != "no-segment",
                VerifyThreadTimeComputer = true,
                IncludeEventSourceEvents = false,
                AllowSupplementalSamples = true,
                VerifySampleWeights = true,
                OmitSymbolFor = guard == "unresolved-carrier" ? leaf : null,
                ContextParent = guard == "ambiguous-type"
                    ? AsyncSegment.StateMachine(Frame.Library("OtherApp", parent.SymbolName, "Ambiguous.Parent"))
                    : null,
                AsyncStartQpc = guard == "no-segment" ? 100_030 : 100_010,
                ExpectedStitched = guard == "no-segment"
                    ? sync
                    : prefix.Concat(new[] { runningChild, capturedParent, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart }).ToArray(),
                ExpectedDiagnostics = guard == "no-segment"
                    ? new CallStackDiagnosticsExpectation()
                    : new CallStackDiagnosticsExpectation { SegmentsProcessed = 2, V1InlineFallbackUsed = 2 },
                AssertStitchedStackSource = stackSource =>
                {
                    for (int i = 0; i < stackSource.CallFrameIndexLimit; i++)
                    {
                        if (stackSource.TryGetAsyncProfilerCallStackFrameInfo((StackSourceFrameIndex)i, out AsyncProfilerCallStackSourceFrameInfo info))
                        {
                            Assert.False(info.IsStateMachineBoxCarrier);
                        }
                    }
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
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
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
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
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
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, (int)AsyncCallstackKind.StateMachineAsync)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, (int)AsyncCallstackKind.StateMachineAsync)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, (int)AsyncCallstackKind.RuntimeAsync)]
        public void NestedSegments_OtherKindOrderings_PreserveUnclassifiedBridge(
            int innerKindValue, int outerKindValue)
        {
            var innerKind = (AsyncCallstackKind)innerKindValue;
            var outerKind = (AsyncCallstackKind)outerKindValue;
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
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
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

        [Theory]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, (int)AsyncCallstackKind.RuntimeAsync, -1)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, (int)AsyncCallstackKind.RuntimeAsync, 0)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, (int)AsyncCallstackKind.RuntimeAsync, 1)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, (int)AsyncCallstackKind.RuntimeAsync, 2)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, (int)AsyncCallstackKind.StateMachineAsync, -1)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, (int)AsyncCallstackKind.StateMachineAsync, 0)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, (int)AsyncCallstackKind.StateMachineAsync, 1)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, (int)AsyncCallstackKind.StateMachineAsync, 2)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, (int)AsyncCallstackKind.RuntimeAsync, -1)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, (int)AsyncCallstackKind.RuntimeAsync, 0)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, (int)AsyncCallstackKind.RuntimeAsync, 1)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, (int)AsyncCallstackKind.RuntimeAsync, 2)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, (int)AsyncCallstackKind.StateMachineAsync, -1)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, (int)AsyncCallstackKind.StateMachineAsync, 0)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, (int)AsyncCallstackKind.StateMachineAsync, 1)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, (int)AsyncCallstackKind.StateMachineAsync, 2)]
        public void NestedAsync_CoreLibOnlyReentrantHandoff_RequiresResolvedMethods(
            int innerKindValue, int outerKindValue, int unresolvedFrame)
        {
            var innerKind = (AsyncCallstackKind)innerKindValue;
            var outerKind = (AsyncCallstackKind)outerKindValue;
            Frame innerWork = Frame.App("Scenario.InnerWork");
            Frame innerIdentity = Frame.App(innerKind == AsyncCallstackKind.RuntimeAsync
                ? "Scenario.InnerAsync" : "Scenario+<InnerAsync>d__1.MoveNext");
            Frame innerAncestor = Frame.App(innerKind == AsyncCallstackKind.RuntimeAsync
                ? "Scenario.InnerParentAsync" : "Scenario+<InnerParentAsync>d__2.MoveNext");
            Frame innerCurrent = innerKind == AsyncCallstackKind.RuntimeAsync
                ? innerIdentity : innerIdentity.Logical("Scenario.InnerAsync", -1);
            Frame innerParent = innerKind == AsyncCallstackKind.RuntimeAsync
                ? innerAncestor : innerAncestor.Logical("Scenario.InnerParentAsync", 5);
            Frame runContinuations = Frame.CoreLib(
                "System.Threading.Tasks.Task.RunContinuations", "Task.RunContinuations");
            Frame trySetResult = Frame.CoreLib(
                "System.Threading.Tasks.Task.TrySetResult", "Task.TrySetResult");
            Frame setResult = Frame.CoreLib(
                "System.Threading.Tasks.TaskCompletionSource`1.SetResult", "TaskCompletionSource.SetResult");
            Frame outerIdentity = Frame.App(outerKind == AsyncCallstackKind.RuntimeAsync
                ? "Scenario.OuterAsync" : "Scenario+<OuterAsync>d__3.MoveNext");
            Frame outerAncestor = Frame.App(outerKind == AsyncCallstackKind.RuntimeAsync
                ? "Scenario.OuterParentAsync" : "Scenario+<OuterParentAsync>d__4.MoveNext");
            Frame outerCurrent = outerKind == AsyncCallstackKind.RuntimeAsync
                ? outerIdentity : outerIdentity.Logical("Scenario.OuterAsync", -1);
            Frame outerParent = outerKind == AsyncCallstackKind.RuntimeAsync
                ? outerAncestor : outerAncestor.Logical("Scenario.OuterParentAsync", 5);
            Frame[] handoff = { runContinuations, trySetResult, setResult };
            Frame[] innerDispatch = innerKind == AsyncCallstackKind.RuntimeAsync
                ? new[] { Frame.V2Wrapper(0), Frame.V2InstrumentedDispatch, Frame.V2Dispatch }
                : new[] { Frame.V1MoveNextAsDispatcher };
            Frame[] outerDispatch = outerKind == AsyncCallstackKind.RuntimeAsync
                ? new[] { Frame.V2Wrapper(0), Frame.V2InstrumentedDispatch, Frame.V2Dispatch }
                : new[] { Frame.V1MoveNextAsDispatcher };

            var scenario = new StitchScenario
            {
                Sync = new[] { innerWork, innerIdentity }.Concat(innerDispatch).Concat(handoff)
                    .Concat(new[] { outerIdentity }).Concat(outerDispatch)
                    .Concat(new[] { Frame.ThreadPoolDispatch, Frame.WorkerThreadStart }).ToArray(),
                AsyncSegments = new[]
                {
                    new AsyncSegment(outerKind, new[] { outerIdentity, outerAncestor }, new[] { 0, 5 }),
                    new AsyncSegment(innerKind, new[] { innerIdentity, innerAncestor }, new[] { 0, 5 }),
                },
                OmitSymbolFor = unresolvedFrame < 0 ? null : handoff[unresolvedFrame],
                EnableFrameAnnotations = true,
                SkipNormalStackAssertion = true,
                VerifyThreadTimeComputer = true,
                VerifySampleWeights = true,
                IncludeEventSourceEvents = false,
                ExpectedStitched = unresolvedFrame < 0 ? new[]
                {
                    innerWork,
                    innerCurrent,
                    innerParent,
                    outerCurrent,
                    outerParent,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                } : new[]
                {
                    innerWork,
                    innerCurrent,
                    innerParent,
                    runContinuations,
                    trySetResult,
                    setResult,
                    outerCurrent,
                    outerParent,
                    Frame.ThreadPoolDispatch,
                    Frame.WorkerThreadStart,
                },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 4,
                    V2PlumbingFramesCollapsed =
                        (innerKind == AsyncCallstackKind.RuntimeAsync ? 4 : 0) +
                        (outerKind == AsyncCallstackKind.RuntimeAsync ? 4 : 0),
                    V1InlineFallbackUsed =
                        (innerKind == AsyncCallstackKind.StateMachineAsync ? 2 : 0) +
                        (outerKind == AsyncCallstackKind.StateMachineAsync ? 2 : 0),
                    V2ReentrantHandoffFramesCollapsed = unresolvedFrame < 0 &&
                        innerKind == AsyncCallstackKind.RuntimeAsync &&
                        outerKind == AsyncCallstackKind.RuntimeAsync ? 6 : 0,
                    AsyncReentrantHandoffFramesCollapsed = unresolvedFrame < 0 ? 6 : 0,
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, false, false, false)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, false, false, true)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, false, true, false)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, false, true, true)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, true, false, false)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, true, false, true)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, true, true, false)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, true, true, true)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, false, false, false)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, false, false, true)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, false, true, false)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, false, true, true)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, true, false, false)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, true, false, true)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, true, true, false)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, true, true, true)]
        public void StateMachineAsync_ReentrantDispatchTarget_RequiresVerifiedProgressOrBoxCarrier(
            int outerKindValue, bool carrier, bool completionObserved, bool enableNormalization)
        {
            var outerKind = (AsyncCallstackKind)outerKindValue;
            Frame completed = Frame.App("Scenario+<CompletedAsync>d__0.MoveNext");
            Frame target = Frame.App("Scenario+<InnerAsync>d__1.MoveNext");
            Frame ancestor = Frame.App("Scenario+<InnerParentAsync>d__2.MoveNext");
            Frame box = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[System.Int64,Scenario+<InnerAsync>d__1].MoveNext()",
                "InnerBox.MoveNext");
            Frame outerIdentity = Frame.App(outerKind == AsyncCallstackKind.RuntimeAsync
                ? "Scenario.OuterAsync" : "Scenario+<OuterAsync>d__3.MoveNext");
            Frame outerAncestor = Frame.App(outerKind == AsyncCallstackKind.RuntimeAsync
                ? "Scenario.OuterParentAsync" : "Scenario+<OuterParentAsync>d__4.MoveNext");
            Frame[] entry = carrier
                ? new[] { box, Frame.V1MoveNextAsDispatcher }
                : new[] { Frame.V1MoveNextAsDispatcher };
            Frame[] bridge =
            {
                Frame.CoreLib("System.Threading.Tasks.Task.RunContinuations", "Task.RunContinuations"),
                Frame.CoreLib("System.Threading.Tasks.Task.TrySetResult", "Task.TrySetResult"),
                Frame.CoreLib("System.Threading.Tasks.TaskCompletionSource`1.SetResult", "TaskCompletionSource.SetResult"),
            };
            Frame[] outerDispatch = outerKind == AsyncCallstackKind.RuntimeAsync
                ? new[] { Frame.V2Wrapper(0), Frame.V2InstrumentedDispatch, Frame.V2Dispatch }
                : new[] { Frame.V1MoveNextAsDispatcher };
            Frame[] root = { Frame.ThreadPoolDispatch, Frame.WorkerThreadStart };
            Frame[] innerFrames = completionObserved
                ? new[] { completed, target, ancestor } : new[] { target, ancestor };
            int[] innerStates = completionObserved ? new[] { 0, 5, 7 } : new[] { 5, 7 };
            bool fold = enableNormalization && (carrier || completionObserved);
            var expected = new List<Frame>();
            if (!carrier)
            {
                expected.Add(Frame.V1MoveNextAsDispatcher);
            }
            expected.Add(target.Logical("Scenario.InnerAsync", 5));
            expected.Add(ancestor.Logical("Scenario.InnerParentAsync", 7));
            if (!fold)
            {
                expected.AddRange(bridge);
            }
            expected.AddRange(outerKind == AsyncCallstackKind.RuntimeAsync
                ? new[] { outerIdentity, outerAncestor }
                : new[]
                {
                    outerIdentity.Logical("Scenario.OuterAsync", -1),
                    outerAncestor.Logical("Scenario.OuterParentAsync", 9),
                });
            expected.AddRange(root);
            var scenario = new StitchScenario
            {
                Sync = entry.Concat(bridge).Concat(new[] { outerIdentity }).Concat(outerDispatch).Concat(root).ToArray(),
                AsyncSegments = new[]
                {
                    new AsyncSegment(outerKind, new[] { outerIdentity, outerAncestor }, new[] { 0, 9 }),
                    new AsyncSegment(AsyncCallstackKind.StateMachineAsync, innerFrames, innerStates),
                },
                MethodCompletions = completionObserved ? 1 : 0,
                EnableFrameAnnotations = true,
                EnableNormalization = enableNormalization,
                SkipNormalStackAssertion = true,
                IncludeEventSourceEvents = false,
                VerifyThreadTimeComputer = true,
                VerifySampleWeights = true,
                ExpectedStitched = expected.ToArray(),
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 4,
                    V1InlineFallbackUsed = completionObserved ? 0 :
                        (outerKind == AsyncCallstackKind.StateMachineAsync ? 4 : 2),
                    V2PlumbingFramesCollapsed = outerKind == AsyncCallstackKind.RuntimeAsync ? 4 : 0,
                    AsyncReentrantHandoffFramesCollapsed = fold ? 6 : 0,
                },
                AssertTransformContext = context =>
                {
                    AsyncProfilerStitchedFrame dispatchTarget = Assert.Single(context.Frames.Where(frame =>
                        frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget));
                    Assert.Same(context.Segments[1], dispatchTarget.Context);
                    Assert.Equal(carrier, dispatchTarget.IsStateMachineBoxCarrier);
                    Assert.Equal(completionObserved ? 1 : 0, dispatchTarget.SegmentFrameIndex);
                    Assert.Equal(5, dispatchTarget.Segment.FrameStateAt(dispatchTarget.SegmentFrameIndex));
                    Assert.DoesNotContain(context.Frames, frame =>
                        frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent &&
                        ReferenceEquals(frame.Context, dispatchTarget.Context));
                    Assert.Equal(fold ? 3 : 0, context.Diagnostics.AsyncReentrantHandoffFramesCollapsed);
                },
                AssertStitchedStackSource = stackSource =>
                {
                    int targets = 0;
                    for (int i = 0; i < stackSource.CallFrameIndexLimit; i++)
                    {
                        if (stackSource.TryGetAsyncProfilerCallStackFrameInfo((StackSourceFrameIndex)i, out AsyncProfilerCallStackSourceFrameInfo info) &&
                            info.IsDispatchTarget && info.Kind == AsyncCallstackKind.StateMachineAsync)
                        {
                            targets++;
                            Assert.False(info.IsCurrent);
                            Assert.Equal(5, info.State);
                        }
                    }
                    Assert.Equal(1, targets);
                },
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("missing-boundary")]
        [InlineData("adjacency-mismatch")]
        [InlineData("ambiguous-v1")]
        [InlineData("outer-target")]
        [InlineData("nonadjacent")]
        [InlineData("no-trace")]
        [InlineData("unresolved-target")]
        [InlineData("unresolved-bridge")]
        [InlineData("foreign-bridge")]
        public void RuntimeAsync_ReentrantDispatchTarget_PreservesUnverifiedHandoff(string obstruction)
        {
            Frame target = Frame.App("Scenario.InnerAsync");
            Frame outer = Frame.App("Scenario.OuterAsync");
            Frame[] bridge =
            {
                Frame.CoreLib("System.Threading.Tasks.Task.RunContinuations", "Task.RunContinuations"),
                Frame.CoreLib("System.Threading.Tasks.Task.TrySetResult", "Task.TrySetResult"),
                obstruction == "foreign-bridge"
                    ? Frame.Library("ForeignLibrary", "System.Threading.Tasks.TaskCompletionSource`1.SetResult", "Foreign.SetResult")
                    : Frame.CoreLib("System.Threading.Tasks.TaskCompletionSource`1.SetResult", "TaskCompletionSource.SetResult"),
            };
            Frame[] root = { Frame.ThreadPoolDispatch, Frame.WorkerThreadStart };
            var scenario = new StitchScenario
            {
                Sync = new[] { Frame.V2Wrapper(0), Frame.V2InstrumentedDispatch, Frame.V2Dispatch }.Concat(bridge)
                    .Concat(new[] { outer, Frame.V2Wrapper(0), Frame.V2InstrumentedDispatch, Frame.V2Dispatch })
                    .Concat(root).ToArray(),
                AsyncSegments = new[] { AsyncSegment.Runtime(outer), AsyncSegment.Runtime(target) },
                OmitSymbolFor = obstruction == "unresolved-target" ? target :
                    obstruction == "unresolved-bridge" ? bridge[1] : null,
                EnableNormalization = false,
                IncludeEventSourceEvents = false,
                VerifyThreadTimeComputer = true,
                VerifySampleWeights = true,
                ExpectedStitched = new[] { target }.Concat(bridge)
                    .Concat(new[] { outer }).Concat(root).ToArray(),
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 4,
                    V2PlumbingFramesCollapsed = 8,
                    V2LeafWrapperDropped = 2,
                },
                AssertTransformContext = context =>
                {
                    var frames = new List<AsyncProfilerStitchedFrame>(context.Frames);
                    var diagnostics = new AsyncProfilerCallStackDiagnostics
                    {
                        BoundariesNotFound = obstruction == "missing-boundary" ? 1 : 0,
                        AdjacencyMismatches = obstruction == "adjacency-mismatch" ? 1 : 0,
                        V1CompletionAlignmentAmbiguous = obstruction == "ambiguous-v1" ? 1 : 0,
                    };
                    int outerIndex = frames.FindIndex(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
                    if (obstruction == "outer-target")
                    {
                        frames[outerIndex] = AsyncProfilerStitchedFrame.CreateAsyncDispatchTarget(context.Segments[0], 0);
                    }
                    else if (obstruction == "nonadjacent")
                    {
                        AsyncProfilerStitchedFrame current = frames[outerIndex];
                        frames[outerIndex] = AsyncProfilerStitchedFrame.CreateAsyncCurrent(
                            current.CodeAddress, current.Method, context.Segments[1], 0);
                        for (int i = 1; i < outerIndex; i++)
                        {
                            AsyncProfilerStitchedFrame frame = frames[i];
                            frames[i] = AsyncProfilerStitchedFrame.CreateSync(
                                new AsyncProfilerStitchSyncFrame(frame.CodeAddress, frame.Method, frame.SyncFrameKind), context.Segments[1]);
                        }
                    }
                    AsyncProfilerStitchedFrame[] expected = frames.ToArray();
                    var pipeline = new AsyncProfilerCallStackTransformPipeline();
                    AsyncProfilerStitchResult result = pipeline.Transform(new AsyncProfilerStitchResult(frames, diagnostics), context.Segments,
                        context.TimestampQpc, context.ClassifyBoundary, obstruction == "no-trace" ? null : context.TraceLog);
                    Assert.Equal(expected, result.Frames);
                    Assert.Equal(0, diagnostics.AsyncReentrantHandoffFramesCollapsed);
                    Assert.Equal(0, diagnostics.V2ReentrantHandoffFramesCollapsed);
                },
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync)]
        public void StateMachineAsync_CompletingInnerInvocation_PreservesReentrantBridge(int outerKindValue)
        {
            var outerKind = (AsyncCallstackKind)outerKindValue;
            Frame completed = Frame.App("Scenario+<CompletedAsync>d__0.MoveNext");
            Frame target = Frame.App("Scenario+<InnerAsync>d__1.MoveNext");
            Frame ancestor = Frame.App("Scenario+<InnerParentAsync>d__2.MoveNext");
            Frame outerIdentity = Frame.App(outerKind == AsyncCallstackKind.RuntimeAsync
                ? "Scenario.OuterAsync" : "Scenario+<OuterAsync>d__3.MoveNext");
            Frame[] bridge =
            {
                Frame.CoreLib("System.Threading.Tasks.Task.RunContinuations", "Task.RunContinuations"),
                Frame.CoreLib("System.Threading.Tasks.Task.TrySetResult", "Task.TrySetResult"),
                Frame.CoreLib("System.Threading.Tasks.TaskCompletionSource`1.SetResult", "TaskCompletionSource.SetResult"),
            };
            Frame[] outerDispatch = outerKind == AsyncCallstackKind.RuntimeAsync
                ? new[] { Frame.V2Wrapper(0), Frame.V2InstrumentedDispatch, Frame.V2Dispatch }
                : new[] { Frame.V1MoveNextAsDispatcher };
            Frame[] root = { Frame.ThreadPoolDispatch, Frame.WorkerThreadStart };
            var scenario = new StitchScenario
            {
                Sync = new[] { completed, Frame.V1MoveNextAsDispatcher }.Concat(bridge)
                    .Concat(new[] { outerIdentity }).Concat(outerDispatch).Concat(root).ToArray(),
                AsyncSegments = new[]
                {
                    new AsyncSegment(outerKind, new[] { outerIdentity }, new[] { 0 }),
                    new AsyncSegment(AsyncCallstackKind.StateMachineAsync,
                        new[] { completed, target, ancestor }, new[] { 0, 5, 7 }),
                },
                MethodCompletions = 1,
                EnableFrameAnnotations = true,
                SkipNormalStackAssertion = true,
                IncludeEventSourceEvents = false,
                VerifyThreadTimeComputer = true,
                VerifySampleWeights = true,
                ExpectedStitched = new[]
                {
                    completed.Logical("Scenario.CompletedAsync", -2),
                    target.Logical("Scenario.InnerAsync", 5),
                    ancestor.Logical("Scenario.InnerParentAsync", 7),
                }.Concat(bridge).Concat(new[]
                {
                    outerKind == AsyncCallstackKind.RuntimeAsync
                        ? outerIdentity : outerIdentity.Logical("Scenario.OuterAsync", -1),
                }).Concat(root).ToArray(),
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 4,
                    V1CompletionTailUsed = 2,
                    V2PlumbingFramesCollapsed = outerKind == AsyncCallstackKind.RuntimeAsync ? 4 : 0,
                },
                AssertTransformContext = context =>
                {
                    Assert.Single(context.Frames.Where(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompleting));
                    Assert.Single(context.Frames.Where(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget));
                    Assert.Equal(0, context.Diagnostics.AsyncReentrantHandoffFramesCollapsed);
                },
            };
            scenario.AssertProductionStitch();
        }

        [Fact]
        public void NestedRuntimeAsync_ReentrantHandoff_TruncatedOuterBoundary_PreservesTheBridge()
        {
            Frame innerWork = Frame.App("Scenario.InnerWork");
            Frame innerCurrent = Frame.App("Scenario.InnerAsync");
            Frame innerParent = Frame.App("Scenario.InnerParentAsync");
            Frame outerCurrent = Frame.App("Scenario.OuterAsync");
            Frame outerParent = Frame.App("Scenario.OuterParentAsync");
            Frame runContinuations = Frame.CoreLib("System.Threading.Tasks.Task.RunContinuations", "Task.RunContinuations");
            Frame trySetResult = Frame.CoreLib("System.Threading.Tasks.Task.TrySetResult", "Task.TrySetResult");
            Frame setResult = Frame.CoreLib("System.Threading.Tasks.TaskCompletionSource`1.SetResult", "TaskCompletionSource.SetResult");
            var scenario = new StitchScenario
            {
                Sync = new[]
                {
                    innerWork, innerCurrent, Frame.V2Wrapper(0), Frame.V2InstrumentedDispatch, Frame.V2Dispatch,
                    runContinuations, trySetResult, setResult, outerCurrent,
                },
                AsyncSegments = new[]
                {
                    AsyncSegment.Runtime(outerCurrent, outerParent),
                    AsyncSegment.Runtime(innerCurrent, innerParent),
                },
                ExpectedStitched = new[]
                {
                    innerWork, innerCurrent, innerParent, runContinuations, trySetResult, setResult,
                    outerCurrent, outerParent,
                },
                VerifyThreadTimeComputer = true,
                VerifySampleWeights = true,
                IncludeEventSourceEvents = false,
                AllowSupplementalSamples = true,
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 4,
                    BoundariesNotFound = 2,
                    V2PlumbingFramesCollapsed = 4,
                },
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DispatcherCreation_CleanupPreservesApplicationKickoffAndSampleWeights(bool enableNormalization)
        {
            StitchScenario scenario = CreateDispatcherCreationScenario();
            scenario.EnableNormalization = enableNormalization;

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, 1, 1, false)]
        [InlineData(true, 1, 1, false)]
        [InlineData(false, 4, 3, true)]
        [InlineData(true, 4, 3, true)]
        [InlineData(false, 9, 0, true)]
        [InlineData(true, 9, 3, true)]
        public void DispatcherCreation_BoundedRegistrationPreservesSynchronousChildrenAndCallees(
            bool enableNormalization, int helperCount, int calleeCount, bool includeStartup)
        {
            StitchScenario scenario = CreateBoundedDispatcherCreationScenario(
                enableNormalization, helperCount, calleeCount, includeStartup);
            scenario.AssertTransformContext = context =>
            {
                AsyncProfilerStitchedFrame marker = context.Frames[calleeCount];
                Assert.Equal(AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames, marker.Presentation);
                Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, marker.Origin);
                Assert.Contains("CreateDispatcher", context.TraceLog.CodeAddresses[marker.CodeAddress].FullMethodName);
                AsyncProfilerStitchedFrame current = Assert.Single(context.Frames.Where(frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent));
                Assert.Same(current.Context, marker.Context);
                Assert.Equal(scenario.Sync.Take(calleeCount).Select(frame => frame.SymbolName),
                    context.Frames.Take(calleeCount).Select(frame =>
                        context.TraceLog.CodeAddresses[frame.CodeAddress].FullMethodName));
                Assert.All(context.Frames.Skip(calleeCount + 1).TakeWhile(frame => frame.Origin ==
                    AsyncProfilerStitchedFrameOrigin.Sync), frame => Assert.Same(current.Context, frame.Context));
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("unresolved-entry")]
        [InlineData("foreign-entry")]
        [InlineData("unresolved-registration")]
        [InlineData("foreign-registration")]
        [InlineData("other-runtime-registration")]
        [InlineData("unknown-registration")]
        [InlineData("application-registration")]
        [InlineData("unresolved-caller")]
        public void DispatcherCreation_BoundedRegistrationRequiresResolvedUninterruptedRuntimeEvidence(string obstruction)
        {
            StitchScenario scenario = CreateBoundedDispatcherCreationScenario(true, 4, 1, false);
            int index = obstruction.EndsWith("entry", StringComparison.Ordinal) ? 1 :
                obstruction.EndsWith("caller", StringComparison.Ordinal) ? 6 : 2;
            Frame original = scenario.Sync[index];
            if (obstruction.StartsWith("unresolved", StringComparison.Ordinal))
            {
                scenario.OmitSymbolFor = original;
            }
            else
            {
                Frame replacement = obstruction == "unknown-registration"
                    ? Frame.CoreLib("System.Runtime.CompilerServices.UnknownAwaiter.Register", "UnknownRegistration")
                    : obstruction == "application-registration"
                        ? Frame.App("Scenario.Callback")
                        : Frame.Library(obstruction == "other-runtime-registration"
                            ? @"C:\other-runtime\System.Private.CoreLib.dll" : "OtherLibrary",
                            original.SymbolName, original.Label + " (foreign)");
                scenario.Sync[index] = replacement;
                scenario.ExpectedWithoutInfrastructureCleanup[index] = replacement;
                scenario.ExpectedWithoutNormalization[index] = replacement;
            }
            if (index == 1)
            {
                scenario.ExpectedStitched = scenario.Sync.Take(2)
                    .Concat(new[] { scenario.Sync[2].DispatcherFrames(), scenario.Sync[6] })
                    .Concat(scenario.ExpectedStitched.Skip(3)).ToArray();
                scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 18;
            }
            else
            {
                scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup.Take(7)
                    .Concat(scenario.ExpectedStitched.Skip(3)).ToArray();
                scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 10;
            }

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("entry")]
        [InlineData("registration")]
        [InlineData("cleanup-policy")]
        public void DispatcherCreation_BoundedRegistrationRespectsProfileVetoes(string rejection)
        {
            StitchScenario scenario = CreateBoundedDispatcherCreationScenario(true, 4, 1, false);
            AsyncProfilerRuntimeRuleProfile profile = rejection == "entry"
                ? (AsyncProfilerRuntimeRuleProfile)new RejectingProfilerEntryProfile()
                : rejection == "registration"
                    ? (AsyncProfilerRuntimeRuleProfile)new RejectingAwaitRegistrationProfile()
                    : new DisabledDispatcherCreationCleanupProfile();
            scenario.RuntimeProfileForModule = module => AsyncProfilerRuntimeRuleProfileCache.IsHostModule(module)
                ? profile : AsyncProfilerRuntimeRuleProfile.Common;
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup.Take(7)
                .Concat(scenario.ExpectedStitched.Skip(3)).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 10;

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DispatcherCreation_BoundedRegistrationDoesNotRequireOwnershipOfItsCallees(bool unresolved)
        {
            StitchScenario scenario = CreateBoundedDispatcherCreationScenario(true, 3, 1, true);
            if (unresolved)
            {
                scenario.OmitSymbolFor = scenario.Sync[0];
            }
            else
            {
                Frame callback = Frame.App("Observer.ArbitraryCallee");
                scenario.Sync[0] = callback;
                scenario.ExpectedStitched[0] = callback;
                scenario.ExpectedWithoutInfrastructureCleanup[0] = callback;
                scenario.ExpectedWithoutNormalization[0] = callback;
            }
            scenario.AssertProductionStitch();
        }

        [Fact]
        public void DispatcherCreation_BoundedRegistrationPreservesRuntimeAsyncParentProvenance()
        {
            CreateBoundedDispatcherCreationScenario(
                true, 3, 1, true, AsyncCallstackKind.RuntimeAsync).AssertProductionStitch();
        }

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(6)]
        public void DispatcherCreation_BoundedRegistrationDoesNotCrossActivationOwnership(int index)
        {
            StitchScenario scenario = CreateBoundedDispatcherCreationScenario(true, 4, 1, false);
            scenario.EnableInfrastructureCleanup = false;
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;
            scenario.AssertTransformContext = context =>
            {
                var frames = context.Frames.ToList();
                AsyncProfilerStitchedFrame frame = frames[index];
                frames[index] = AsyncProfilerStitchedFrame.CreateSync(
                    new AsyncProfilerStitchSyncFrame(frame.CodeAddress, frame.Method, frame.SyncFrameKind), context: null);
                AsyncProfilerStitchedFrame[] prefix = frames.Take(7).ToArray();
                var diagnostics = new AsyncProfilerCallStackDiagnostics { SegmentsProcessed = context.Segments.Count };
                new AsyncProfilerCallStackTransformPipeline
                {
                    EnableNormalization = false,
                    EnableInfrastructureCleanup = true,
                }.ApplyInPlace(new AsyncProfilerCallStackTransformState(
                    context.TraceLog, context.Segments, context.TimestampQpc,
                    context.ClassifyBoundary, diagnostics, frames));
                Assert.Equal(prefix, frames.Take(7));
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, 1)]
        [InlineData(true, 1)]
        [InlineData(true, 100)]
        public void DispatcherCreationOwnership_FoldsTheEntireAwaitRegistrationPrefix(bool enableNormalization, int calleeCount)
        {
            StitchScenario scenario = CreateDispatcherOwnedPrefixScenario();
            scenario.EnableNormalization = enableNormalization;
            if (calleeCount > 1)
            {
                Frame[] callees = Enumerable.Range(0, calleeCount).Select(i => Frame.CoreLib(
                    "System.Runtime.CompilerServices.AsyncProfiler+OtherHelpers.Append" + i,
                    "Profiler.Append" + i)).ToArray();
                scenario.Sync = callees.Concat(scenario.Sync.Skip(1)).ToArray();
                scenario.ExpectedWithoutInfrastructureCleanup =
                    callees.Concat(scenario.ExpectedWithoutInfrastructureCleanup.Skip(1)).ToArray();
                scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed += (calleeCount - 1) * 2;
                scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed += (calleeCount - 1) * 2;
            }

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void V1AwaitRegistrationOwnership_FoldsContiguousHelpersWithoutASpecificProfilerEntry(
            bool enableNormalization, bool genericHelpers)
        {
            StitchScenario scenario = CreateV1AwaitRegistrationScenario();
            scenario.EnableNormalization = enableNormalization;
            if (genericHelpers)
            {
                Frame[] helpers =
                {
                    Frame.CoreLib(
                        "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[System.Int64,Scenario+<Level2Async>d__2].ClearStateUponCompletion()",
                        "Box.ClearState"),
                    Frame.CoreLib("System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`1[Scenario+<Level2Async>d__2].ResolveStateFieldOffset()",
                        "Diagnostics.ResolveState"),
                    Frame.CoreLib("System.Runtime.CompilerServices.AsyncProfiler+OtherHelpers.Append()",
                        "Profiler.OtherHelper"),
                };
                scenario.Sync = scenario.Sync.Take(1).Concat(helpers).Concat(scenario.Sync.Skip(4)).ToArray();
                scenario.ExpectedWithoutInfrastructureCleanup = scenario.Sync.Take(6)
                    .Concat(scenario.ExpectedWithoutInfrastructureCleanup.Skip(6)).ToArray();
            }
            scenario.AssertTransformContext = context =>
            {
                AsyncProfilerStitchedFrame marker = Assert.Single(context.Frames.Where(frame =>
                    frame.Presentation == AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames));
                AsyncProfilerStitchedFrame current = Assert.Single(context.Frames.Where(frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent));
                Assert.Same(current.Context, marker.Context);
                Assert.Equal(0, current.Segment.FrameStateAt(current.SegmentFrameIndex));
                Assert.Equal(context.TraceLog.CodeAddresses.MethodIndex(current.CodeAddress), current.Method);
                Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, context.Frames[0].Origin);
                Assert.Equal(AsyncProfilerStitchedFramePresentation.Native, context.Frames[0].Presentation);
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false, "none", "ValueTaskAwaiter")]
        [InlineData(false, true, "none", "ValueTaskAwaiter")]
        [InlineData(true, false, "none", "ValueTaskAwaiter")]
        [InlineData(true, true, "none", "ValueTaskAwaiter")]
        [InlineData(true, true, "foreign", "ValueTaskAwaiter")]
        [InlineData(true, true, "unresolved", "ValueTaskAwaiter")]
        [InlineData(true, true, "none", "TaskAwaiter")]
        [InlineData(true, true, "none", "TaskAwaiter`1[System.Int64]")]
        [InlineData(true, true, "none", "ConfiguredTaskAwaitable+ConfiguredTaskAwaiter")]
        [InlineData(true, true, "none", "ConfiguredTaskAwaitable`1+ConfiguredTaskAwaiter[System.Int64]")]
        [InlineData(true, true, "none", "ConfiguredTaskAwaitable`1[System.Int64]+ConfiguredTaskAwaiter")]
        [InlineData(true, true, "none", "ConfiguredValueTaskAwaitable+ConfiguredValueTaskAwaiter")]
        [InlineData(true, true, "none", "ConfiguredValueTaskAwaitable`1+ConfiguredValueTaskAwaiter[System.Int64]")]
        [InlineData(true, true, "none", "ConfiguredValueTaskAwaitable`1[System.Int64]+ConfiguredValueTaskAwaiter")]
        public void AwaiterReadinessCheck_GroupsOnlyTheAccessorAndPreservesSynchronousKickoff(
            bool enableNormalization, bool cleanup, string obstruction, string awaiterType)
        {
            Frame native = Frame.Library("coreclr", "MethodDesc.PrepareCode", "NativeWork");
            Frame awaiter = Frame.CoreLib("System.Runtime.CompilerServices." + awaiterType + ".get_IsCompleted()", "Awaiter.IsCompleted");
            if (obstruction == "foreign")
            {
                awaiter = Frame.Library("OtherLibrary", awaiter.SymbolName, "Foreign.IsCompleted");
            }
            Frame kickoff = Frame.App("Scenario.ValueTaskLevel2Async()");
            Frame current = Frame.App("Scenario+<ValueTaskLevel1Async>d__14.MoveNext");
            Frame logical = current.Logical("Scenario.ValueTaskLevel1Async", -1);
            bool grouped = cleanup && obstruction == "none";
            var scenario = new StitchScenario
            {
                Kind = AsyncCallstackKind.StateMachineAsync,
                Sync = new[] { native, awaiter, kickoff, current, Frame.V1MoveNextAsDispatcher, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart },
                Async = new[] { current },
                AsyncStates = new[] { 1 },
                OmitSymbolFor = obstruction == "unresolved" ? awaiter : null,
                EnableFrameAnnotations = true,
                EnableNormalization = enableNormalization,
                EnableInfrastructureCleanup = cleanup,
                IncludeEventSourceEvents = false,
                SkipNormalStackAssertion = true,
                AllowSupplementalSamples = true,
                VerifyThreadTimeComputer = true,
                VerifySampleWeights = true,
                ExpectedStitched = new[] { native, grouped ? awaiter.DispatcherFrames() : awaiter, kickoff, logical, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart },
                ExpectedWithoutInfrastructureCleanup = new[] { native, awaiter, kickoff, logical, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V1InlineFallbackUsed = 2,
                    AsyncInfrastructureFramesCollapsed = grouped ? 2 : 0,
                },
                AssertTransformContext = context =>
                {
                    AsyncProfilerStitchedFrame running = Assert.Single(context.Frames.Where(frame =>
                        frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent));
                    Assert.Equal(1, running.Segment.FrameStateAt(running.SegmentFrameIndex));
                    Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, context.Frames[2].Origin);
                    Assert.Equal(AsyncProfilerStitchedFramePresentation.Native, context.Frames[2].Presentation);
                    Assert.False(context.ClassifyBoundary(context.Frames[1].CodeAddress).IsBoundary);
                    Assert.Equal(grouped ? AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames :
                        AsyncProfilerStitchedFramePresentation.Native, context.Frames[1].Presentation);
                    Assert.DoesNotContain(context.Frames, frame =>
                        frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget);
                },
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("ValueTaskAwaiter")]
        [InlineData("ValueTaskAwaiter`1[System.Int64]")]
        [InlineData("ConfiguredValueTaskAwaitable+ConfiguredValueTaskAwaiter")]
        [InlineData("ConfiguredValueTaskAwaitable`1[System.Int64]+ConfiguredValueTaskAwaiter")]
        public void V1AwaitRegistrationOwnership_GroupsKnownExplicitInterfaceAwaiters(string awaiterType)
        {
            StitchScenario scenario = CreateV1AwaitRegistrationScenario();
            Frame helper = Frame.CoreLib(
                "System.Runtime.CompilerServices." + awaiterType +
                ".System.Runtime.CompilerServices.IStateMachineBoxAwareAwaiter.AwaitUnsafeOnCompleted(class System.Runtime.CompilerServices.IAsyncStateMachineBox)",
                "Explicit await registration");
            scenario.Sync[4] = helper;
            scenario.ExpectedWithoutInfrastructureCleanup[4] = helper;
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("QueueContinuation", false, "none")]
        [InlineData("QueueContinuation", true, "none")]
        [InlineData("QueueContinuation", true, "foreign")]
        [InlineData("QueueContinuation", true, "unresolved")]
        [InlineData("OutputCorrelationEtwEvent", false, "none")]
        [InlineData("OutputCorrelationEtwEvent", true, "none")]
        [InlineData("OutputCorrelationEtwEvent", true, "foreign")]
        [InlineData("OutputCorrelationEtwEvent", true, "unresolved")]
        public void YieldContinuationRegistrationHelpers_GroupMachineryButPreserveRuntimeCallees(
            string method, bool enableNormalization, string obstruction)
        {
            StitchScenario scenario = CreateV1AwaitRegistrationScenario();
            Frame helper = Frame.CoreLib(
                "System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter." + method +
                    (method == "QueueContinuation" ? "(System.Action,bool)" : "(System.Action)"),
                "Yield." + method);
            if (obstruction == "foreign")
            {
                helper = Frame.Library("OtherLibrary", helper.SymbolName, "Foreign." + method);
            }
            else if (obstruction == "unresolved")
            {
                scenario.OmitSymbolFor = helper;
            }
            Frame[] callees = method == "QueueContinuation" ? new[]
            {
                Frame.Library("ntoskrnl", "Kernel.Wake1", "Kernel.Wake1"),
                Frame.Library("ntoskrnl", "Kernel.Wake2", "Kernel.Wake2"),
                Frame.Library("ntdll", "Native.Wake1", "Native.Wake1"),
                Frame.Library("ntdll", "Native.Wake2", "Native.Wake2"),
                Frame.CoreLib("System.Threading.LowLevelLifoSemaphore.WakeOneCore()", "Semaphore.WakeOneCore"),
                Frame.CoreLib("System.Threading.LowLevelLifoSemaphore.MaybeWakeWaiterSlow()", "Semaphore.MaybeWakeWaiterSlow"),
                Frame.CoreLib("System.Threading.PortableThreadPool+WorkerThread.MaybeAddWorkingWorker()", "Pool.MaybeAddWorkingWorker"),
                Frame.CoreLib("System.Threading.PortableThreadPool.EnsureWorkerRequested()", "Pool.EnsureWorkerRequested"),
                Frame.CoreLib("System.Threading.ThreadPool.UnsafeQueueUserWorkItem()", "Pool.UnsafeQueueUserWorkItem"),
            } : new[]
            {
                Frame.Library("coreclr", "Native.Work1", "Native.Work1"),
                Frame.Library("coreclr", "Native.Work2", "Native.Work2"),
                Frame.CoreLib("System.Runtime.CompilerServices.StaticsHelpers.GetGCThreadStaticsByIndexSlow(int32)",
                    "Statics.GetGCThreadStaticsByIndexSlow"),
            };
            Frame[] builders =
            {
                scenario.Sync[5],
                Frame.CoreLib(
                    "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].AwaitUnsafeOnCompleted(!!0&,!!1&,class System.Threading.Tasks.Task`1<!0>&)",
                    "Builder.AwaitUnsafeOnCompleted(task)"),
                Frame.CoreLib(
                    "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].AwaitUnsafeOnCompleted(!!0&,!!1&)",
                    "Builder.AwaitUnsafeOnCompleted(generic)"),
            };
            Frame[] prefix = callees.Concat(new[] { helper }).Concat(builders).ToArray();
            scenario.Sync = prefix.Concat(scenario.Sync.Skip(6)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = prefix
                .Concat(scenario.ExpectedWithoutInfrastructureCleanup.Skip(6)).ToArray();
            scenario.ExpectedStitched = callees.Concat(obstruction == "none"
                ? new[] { helper.DispatcherFrames() } : new[] { helper, builders[0].DispatcherFrames() })
                .Concat(scenario.ExpectedStitched.Skip(1)).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = obstruction == "none" ? 18 : 16;
            scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = obstruction == "none" ? 8 : 6;
            scenario.EnableNormalization = enableNormalization;
            scenario.AssertTransformContext = context =>
            {
                Assert.Equal(callees.Select(frame => frame.SymbolName),
                    context.Frames.Take(callees.Length).Select(frame =>
                        context.TraceLog.CodeAddresses[frame.CodeAddress].FullMethodName));
                AsyncProfilerStitchedFrame marker = context.Frames[callees.Length + (obstruction == "none" ? 0 : 1)];
                Assert.Equal(AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames, marker.Presentation);
                Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, marker.Origin);
                Assert.Contains(obstruction == "none" ? method : "AwaitUnsafeOnCompleted",
                    context.TraceLog.CodeAddresses[marker.CodeAddress].FullMethodName);
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("application")]
        [InlineData("foreign")]
        [InlineData("unresolved")]
        public void V1AwaitRegistrationOwnership_StopsAtABreakAndDoesNotRestartAtEligibleHelpers(string obstruction)
        {
            StitchScenario scenario = CreateV1AwaitRegistrationScenario();
            Frame helper = scenario.Sync[2];
            if (obstruction == "unresolved")
            {
                scenario.OmitSymbolFor = helper;
            }
            else
            {
                Frame replacement = obstruction == "application"
                    ? Frame.App("Scenario.Callback")
                    : Frame.Library("OtherLibrary", helper.SymbolName, "ForeignCapture");
                scenario.Sync[2] = replacement;
                scenario.ExpectedWithoutInfrastructureCleanup[2] = replacement;
            }
            scenario.ExpectedStitched = scenario.Sync.Take(3).Concat(scenario.ExpectedStitched.Skip(1)).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 16;
            scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = 6;

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("dispatch-target")]
        [InlineData("parent-current")]
        [InlineData("completing")]
        [InlineData("unresolved-body")]
        [InlineData("registration-context")]
        [InlineData("missing-boundary")]
        public void V1AwaitRegistrationOwnership_RequiresVerifiedPhysicalCurrentAndConsistentEvidence(string obstruction)
        {
            StitchScenario scenario = CreateV1AwaitRegistrationScenario();
            scenario.EnableInfrastructureCleanup = false;
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = 0;
            scenario.AssertTransformContext = context =>
            {
                var frames = context.Frames.ToList();
                int currentIndex = frames.FindIndex(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
                AsyncProfilerStitchedFrame current = frames[currentIndex];
                if (obstruction == "dispatch-target")
                {
                    frames[currentIndex] = AsyncProfilerStitchedFrame.CreateAsyncDispatchTarget(
                        current.Context, current.SegmentFrameIndex);
                }
                else if (obstruction == "parent-current")
                {
                    frames[currentIndex] = AsyncProfilerStitchedFrame.CreateAsyncContextParent(
                        current.Context, current.SegmentFrameIndex, isCurrent: true);
                }
                else if (obstruction == "completing")
                {
                    frames[currentIndex] = AsyncProfilerStitchedFrame.CreateAsyncCompleting(
                        current.CodeAddress, current.Method, current.Context, current.SegmentFrameIndex);
                }
                else if (obstruction == "unresolved-body")
                {
                    frames[currentIndex] = AsyncProfilerStitchedFrame.CreateAsyncCurrent(
                        current.CodeAddress, MethodIndex.Invalid, current.Context, current.SegmentFrameIndex);
                }
                else if (obstruction == "registration-context")
                {
                    AsyncProfilerStitchedFrame registration = frames[currentIndex - 1];
                    frames[currentIndex - 1] = AsyncProfilerStitchedFrame.CreateSync(
                        new AsyncProfilerStitchSyncFrame(registration.CodeAddress, registration.Method, registration.SyncFrameKind),
                        context: null);
                }
                AsyncProfilerStitchedFrame[] prefix = frames.Take(currentIndex).ToArray();
                var diagnostics = new AsyncProfilerCallStackDiagnostics
                {
                    SegmentsProcessed = context.Segments.Count,
                    BoundariesNotFound = obstruction == "missing-boundary" ? 1 : 0,
                };
                new AsyncProfilerCallStackTransformPipeline
                {
                    EnableNormalization = false,
                    EnableInfrastructureCleanup = true,
                }.ApplyInPlace(new AsyncProfilerCallStackTransformState(
                    context.TraceLog, context.Segments, context.TimestampQpc,
                    context.ClassifyBoundary, diagnostics, frames));

                Assert.Equal(prefix, frames.Take(currentIndex));
                Assert.Equal(0, diagnostics.ProfilerEnvelopeFramesCollapsed);
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void V1AwaitRegistrationOwnership_RespectsProfileRejection()
        {
            StitchScenario scenario = CreateV1AwaitRegistrationScenario();
            var profile = new RejectingAwaitRegistrationProfile();
            scenario.RuntimeProfileForModule = module => AsyncProfilerRuntimeRuleProfileCache.IsHostModule(module)
                ? profile : AsyncProfilerRuntimeRuleProfile.Common;
            scenario.ExpectedStitched = scenario.Sync.Take(6).Concat(scenario.ExpectedStitched.Skip(1)).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 10;
            scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = 0;

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void V1AwaitRegistrationOwnership_NoActiveSegment_IsVerbatim()
        {
            StitchScenario scenario = CreateV1AwaitRegistrationScenario();
            scenario.AsyncStartQpc = 100_030;
            scenario.ExpectedStitched = scenario.Sync;
            scenario.ExpectedWithoutInfrastructureCleanup = scenario.Sync;
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation();

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, 1)]
        [InlineData(true, 1)]
        [InlineData(false, 3)]
        [InlineData(true, 3)]
        public void RecursiveV1Completion_OwnedEntryIdentifiesOnlyTheFinishingChild(bool cleanup, int boxCount)
        {
            StitchScenario scenario = CreateRecursiveV1CompletionScenario(boxCount);
            scenario.EnableInfrastructureCleanup = cleanup;
            scenario.ExpectedWithoutNormalization = null;
            if (!cleanup)
            {
                scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
                scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
                scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = 0;
                scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;
            }
            scenario.AssertTransformContext = context =>
            {
                Assert.Equal(0, context.Diagnostics.V1CompletionAlignmentAmbiguous);
                AsyncProfilerStitchedFrame completing = Assert.Single(context.Frames.Where(frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompleting));
                Assert.Equal(0, completing.SegmentFrameIndex);
                Assert.Equal(0, completing.Segment.FrameStateAt(0));
                Assert.Equal(context.GetMethod(completing.Segment.CodeAddressAt(0)), completing.Method);
                AsyncProfilerStitchedFrame target = Assert.Single(context.Frames.Where(frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget));
                Assert.Equal(1, target.SegmentFrameIndex);
                Assert.False(target.IsStateMachineBoxCarrier);
                Assert.DoesNotContain(context.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
                Assert.False(AsyncProfilerCallStackTransformHelpers.HasVerifiedAsyncAnchor(
                    context, context.Frames.IndexOf(completing), completing.Context));
                Assert.Equal(2, context.Frames.Count(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncRemaining));
            };
            scenario.AssertStitchedStackSource = source =>
            {
                var completing = new List<AsyncProfilerCallStackSourceFrameInfo>();
                for (int i = 0; i < source.CallFrameIndexLimit; i++)
                {
                    if (source.TryGetAsyncProfilerCallStackFrameInfo((StackSourceFrameIndex)i, out var info) && info.IsCompleting)
                    {
                        completing.Add(info);
                        Assert.False(info.IsCurrent);
                        Assert.False(info.IsDispatchTarget);
                        Assert.Equal(0, info.State);
                        Assert.Equal(info.CodeAddress, source.GetFrameCodeAddress((StackSourceFrameIndex)i));
                        Assert.EndsWith("state -2]", source.GetFrameName((StackSourceFrameIndex)i, false));
                    }
                }
                Assert.Single(completing);
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("missing-builder")]
        [InlineData("missing-pair")]
        [InlineData("missing-entry")]
        [InlineData("missing-return-box")]
        [InlineData("interrupted-entry")]
        [InlineData("interrupted-return")]
        [InlineData("second-body")]
        [InlineData("foreign-builder")]
        [InlineData("unresolved-box")]
        [InlineData("wrong-box")]
        [InlineData("wrong-context")]
        [InlineData("cross-profile")]
        [InlineData("profile-veto")]
        [InlineData("missing-boundary")]
        [InlineData("adjacency")]
        public void RecursiveV1Completion_UnprovenLayoutsKeepTheOriginalAmbiguity(string obstruction)
        {
            StitchScenario scenario = CreateRecursiveV1CompletionScenario(3);
            scenario.EnableNormalization = false;
            scenario.EnableInfrastructureCleanup = false;
            scenario.ExpectedStitched = scenario.ExpectedWithoutNormalization;
            scenario.ExpectedWithoutNormalization = null;
            scenario.ExpectedWithoutInfrastructureCleanup = null;
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation
            {
                SegmentsProcessed = 2,
                V1InfrastructureFramesCollapsed = 6,
            };
            scenario.AssertTransformContext = context =>
            {
                Assert.Equal(1, context.Diagnostics.V1CompletionAlignmentAmbiguous);
                var frames = context.Frames.ToList();
                int body = frames.FindIndex(frame => frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1StateMachineMoveNext);
                int builder = body - 1;
                int callback = frames.FindIndex(frame =>
                    frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAwaiterInfrastructure);
                int target = frames.FindIndex(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget);
                AsyncProfilerStitchedFrame originalBody = frames[body];
                if (obstruction == "missing-builder") { frames.RemoveAt(builder); }
                else if (obstruction == "missing-pair") { frames.RemoveAt(builder - 1); }
                else if (obstruction == "missing-entry") { frames.RemoveRange(2, callback - 2); }
                else if (obstruction == "missing-return-box") { frames.RemoveRange(body + 1, target - body - 2); }
                else if (obstruction == "interrupted-entry") { frames.Insert(callback, frames[0]); }
                else if (obstruction == "interrupted-return") { frames.Insert(body + 1, frames[0]); }
                else if (obstruction == "second-body") { frames.Insert(2, originalBody); }
                else if (obstruction == "foreign-builder")
                {
                    frames[builder] = AsyncProfilerStitchedFrame.CreateSync(new AsyncProfilerStitchSyncFrame(
                        frames[0].CodeAddress, frames[0].Method, frames[builder].SyncFrameKind), originalBody.Context);
                }
                else if (obstruction == "unresolved-box" || obstruction == "wrong-box" || obstruction == "wrong-context")
                {
                    AsyncProfilerStitchedFrame box = frames[callback - 1];
                    frames[callback - 1] = AsyncProfilerStitchedFrame.CreateSync(new AsyncProfilerStitchSyncFrame(
                        box.CodeAddress, obstruction == "unresolved-box" ? MethodIndex.Invalid : box.Method,
                        box.SyncFrameKind, obstruction == "wrong-box" ? frames[0].Method : box.BoxedStateMachineMethod),
                        obstruction == "wrong-context" ? null : box.Context);
                }
                else if (obstruction == "missing-boundary") { frames.RemoveAt(target - 1); }
                var diagnostics = new AsyncProfilerCallStackDiagnostics
                {
                    SegmentsProcessed = 1,
                    V1CompletionAlignmentAmbiguous = 1,
                    AdjacencyMismatches = obstruction == "adjacency" ? 1 : 0,
                };
                var veto = new RejectingCompletionChainProfile();
                int expectedCount = frames.Count;
                new AsyncProfilerV1PresentationTransform().Apply(new AsyncProfilerCallStackTransformState(
                    context.TraceLog, context.Segments, context.TimestampQpc, context.ClassifyBoundary,
                    diagnostics, frames, methodOf: context.GetMethod,
                    runtimeProfileResolver: address =>
                        obstruction == "profile-veto" ? veto :
                        obstruction == "cross-profile" && address == context.Frames[builder].CodeAddress
                            ? AsyncProfilerRuntimeRuleProfile.Common : context.GetRuntimeProfile(address)));
                Assert.Equal(1, diagnostics.V1CompletionAlignmentAmbiguous);
                Assert.Equal(expectedCount, frames.Count);
                Assert.Contains(frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                    frame.CodeAddress == originalBody.CodeAddress);
                Assert.DoesNotContain(frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent ||
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompleting);
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(2, 0)]
        [InlineData(1, 1)]
        public void RecursiveV1Completion_AdditionalProgressCannotIdentifyTheSurvivingCopy(int normal, int exceptional)
        {
            StitchScenario scenario = CreateRecursiveV1CompletionScenario(3);
            scenario.MethodCompletions = normal;
            scenario.ExceptionCompletions = exceptional;
            scenario.EnableInfrastructureCleanup = false;
            Frame body = scenario.Sync.Single(frame => frame.ModuleName == Frame.ApplicationModule);
            scenario.ExpectedStitched = scenario.Sync.Take(15)
                .Select(frame => frame == body ? body.Logical("Scenario.RecursiveAsync") : frame)
                .Concat(scenario.Async.Skip(2).Select(frame => frame.Logical("Scenario.RecursiveAsync", 1)))
                .Concat(scenario.Sync.Skip(18)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = null;
            scenario.ExpectedWithoutNormalization = null;
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation
            {
                SegmentsProcessed = 2,
                V1InfrastructureFramesCollapsed = 6,
                V1SynchronousMoveNextFramesNormalized = 2,
            };
            scenario.AssertTransformContext = context =>
            {
                Assert.Equal(1, context.Diagnostics.V1CompletionAlignmentAmbiguous);
                Assert.DoesNotContain(context.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent ||
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompleting);
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void V1CompletionMachinery_FoldsOwnedTaskAwaiterAndYieldWithoutChangingCompletionRoles(
            bool enableNormalization)
        {
            StitchScenario scenario = CreateV1CompletionMachineryScenario();
            scenario.EnableNormalization = enableNormalization;
            scenario.AssertTransformContext = context =>
            {
                AsyncProfilerStitchedFrame completing = Assert.Single(context.Frames.Where(frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompleting));
                AsyncProfilerStitchedFrame marker = Assert.Single(context.Frames.Where(frame =>
                    frame.Presentation == AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames));
                Assert.Same(completing.Context, marker.Context);
                Assert.Contains("TaskAwaiter+", context.TraceLog.CodeAddresses[marker.CodeAddress].FullMethodName);
                Assert.Equal(0, completing.Segment.FrameStateAt(completing.SegmentFrameIndex));
                Assert.True(context.HasVerifiedV1CompletionAnchor(completing.Context, marker.CodeAddress));
                Assert.False(context.HasVerifiedDispatchEntry(completing.Context, marker.CodeAddress));
                Assert.False(AsyncProfilerCallStackTransformHelpers.HasVerifiedAsyncAnchor(
                    context, context.Frames.IndexOf(completing), completing.Context));
            };
            scenario.AssertStitchedStackSource = source =>
            {
                var completing = new List<AsyncProfilerCallStackSourceFrameInfo>();
                for (int i = 0; i < source.CallFrameIndexLimit; i++)
                {
                    if (source.TryGetAsyncProfilerCallStackFrameInfo((StackSourceFrameIndex)i, out var info) &&
                        info.IsCompleting)
                    {
                        completing.Add(info);
                        Assert.Equal(info.CodeAddress, source.GetFrameCodeAddress((StackSourceFrameIndex)i));
                        Assert.False(info.IsCurrent);
                        Assert.False(info.IsDispatchTarget);
                        Assert.Equal(0, info.State);
                    }
                }
                Assert.Single(completing);
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("application")]
        [InlineData("foreign")]
        [InlineData("unresolved")]
        public void V1CompletionMachinery_TaskAwaiterFamilyDoesNotRestartAboveABreak(string obstruction)
        {
            StitchScenario scenario = CreateV1CompletionMachineryScenario();
            Frame outer = Frame.CoreLib("System.Runtime.CompilerServices.TaskAwaiter.GetResult()", "TaskAwaiter.GetResult");
            Frame interruption = obstruction == "application" ? Frame.App("Scenario.Callback") :
                obstruction == "foreign" ? Frame.Library("OtherRuntime", "Helper.Invoke()", "ForeignHelper") :
                Frame.CoreLib("System.Runtime.CompilerServices.TaskAwaiter+OtherHelper.Invoke()", "UnresolvedHelper");
            if (obstruction == "unresolved") { scenario.OmitSymbolFor = interruption; }
            Frame[] prefix = { scenario.Sync[0], outer, interruption };
            scenario.Sync = prefix.Concat(scenario.Sync.Skip(1)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup =
                prefix.Concat(scenario.ExpectedWithoutInfrastructureCleanup.Skip(1)).ToArray();
            scenario.ExpectedStitched = prefix.Concat(scenario.ExpectedStitched.Skip(1)).ToArray();
            scenario.AssertProductionStitch();
        }

        [Fact]
        public void V1CompletionMachinery_TaskAwaiterApplicationWorkWithoutAnEntryIsNotGrouped()
        {
            StitchScenario scenario = CreateYieldRuntimeBridgeScenario(AsyncCallstackKind.StateMachineAsync,
                Frame.CoreLib("System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter.Callback()", "Yield.Callback"));
            Frame work = Frame.CoreLib("System.Runtime.CompilerServices.TaskAwaiter.GetResult()", "TaskAwaiter.GetResult");
            scenario.Sync[0] = work;
            scenario.ExpectedStitched[0] = work;
            scenario.ExpectedWithoutInfrastructureCleanup[0] = work;
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("foreign")]
        [InlineData("lookalike")]
        [InlineData("unresolved")]
        public void V1CompletionMachinery_RequiresResolvedOwnedTaskAwaiterFamily(string obstruction)
        {
            StitchScenario scenario = CreateV1CompletionMachineryScenario();
            Frame callback = scenario.Sync[1];
            Frame retained = obstruction == "foreign"
                ? Frame.Library("OtherRuntime", callback.SymbolName, "ForeignTaskAwaiter") :
                obstruction == "lookalike"
                    ? Frame.CoreLib(callback.SymbolName.Replace("TaskAwaiter+", "TaskAwaiterLookalike+"), "LookalikeTaskAwaiter")
                    : callback;
            scenario.Sync[1] = retained;
            scenario.ExpectedWithoutInfrastructureCleanup[1] = retained;
            if (obstruction == "unresolved") { scenario.OmitSymbolFor = callback; }
            scenario.ExpectedStitched = new[] { scenario.Sync[0], retained, scenario.Sync[2].DispatcherFrames() }
                .Concat(scenario.ExpectedStitched.Skip(2)).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 10;
            scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = 8;
            scenario.AssertProductionStitch();
        }

        [Fact]
        public void V1CompletionMachinery_RespectsCompletionChainProfileRejection()
        {
            StitchScenario scenario = CreateV1CompletionMachineryScenario();
            scenario.EnableInfrastructureCleanup = false;
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;
            var profile = new RejectingCompletionChainProfile();
            scenario.AssertTransformContext = context =>
            {
                var frames = context.Frames.ToList();
                AsyncProfilerCallStack activation = Assert.Single(context.Segments);
                CodeAddressIndex dispatch = Enumerable.Range(0, context.TraceLog.CodeAddresses.Count)
                    .Select(i => (CodeAddressIndex)i).Single(ca =>
                        context.ClassifyBoundary(ca).Kind == AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher);
                var placements = new[] { new AsyncProfilerSegmentPlacement(activation, 0, frames.Count, dispatch: dispatch) };
                var diagnostics = new AsyncProfilerCallStackDiagnostics { SegmentsProcessed = 1 };
                var state = new AsyncProfilerCallStackTransformState(
                    context.TraceLog, context.Segments, context.TimestampQpc, context.ClassifyBoundary, diagnostics, frames,
                    runtimeProfileResolver: ca => context.GetHostModuleOwner(ca) != ModuleFileIndex.Invalid
                        ? profile : AsyncProfilerRuntimeRuleProfile.Common,
                    segmentPlacements: placements);
                CodeAddressIndex helper = frames.Single(frame =>
                    frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1MethodBuilderCompletion).CodeAddress;
                Assert.True(state.HasVerifiedV1CompletionAnchor(activation, helper));
                new AsyncProfilerInfrastructureCleanupTransform().Apply(state);
                Assert.Equal(0, diagnostics.ProfilerEnvelopeFramesCollapsed);
                Assert.Equal(2, diagnostics.AsyncInfrastructureFramesCollapsed);
                Assert.Equal(1, diagnostics.RuntimeResumptionBridgeFramesCollapsed);
                foreach (AsyncProfilerStitchSyncFrameKind kind in new[]
                {
                    AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations,
                    AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult,
                    AsyncProfilerStitchSyncFrameKind.V1MethodBuilderCompletion,
                })
                {
                    Assert.Equal(AsyncProfilerStitchedFramePresentation.Native,
                        Assert.Single(frames.Where(frame => frame.SyncFrameKind == kind)).Presentation);
                }
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("unresolved-physical")]
        [InlineData("wrong-method")]
        [InlineData("wrong-index")]
        [InlineData("dispatch-target")]
        [InlineData("parent-current")]
        [InlineData("unwind")]
        [InlineData("missing-boundary")]
        [InlineData("adjacency")]
        [InlineData("ambiguous")]
        [InlineData("missing-placement")]
        public void V1CompletionMachinery_RequiresPhysicalCompletionAndConsistentStructuralEvidence(string obstruction)
        {
            StitchScenario scenario = CreateV1CompletionMachineryScenario();
            scenario.AssertTransformContext = context =>
            {
                int index = context.Frames.FindIndex(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompleting);
                AsyncProfilerStitchedFrame original = context.Frames[index];
                CodeAddressIndex bridge = context.Frames[index - 1].CodeAddress;
                Assert.True(context.HasVerifiedV1CompletionAnchor(original.Context, bridge));
                try
                {
                    if (obstruction == "unresolved-physical" || obstruction == "wrong-method" || obstruction == "wrong-index")
                    {
                        context.Frames[index] = AsyncProfilerStitchedFrame.CreateAsyncCompleting(
                            obstruction == "unresolved-physical" ? CodeAddressIndex.Invalid : original.CodeAddress,
                            obstruction == "wrong-method"
                                ? context.TraceLog.CodeAddresses.MethodIndex(original.Segment.CodeAddressAt(1)) : original.Method,
                            original.Context, obstruction == "wrong-index" ? 1 : original.SegmentFrameIndex);
                    }
                    else if (obstruction == "dispatch-target")
                    {
                        context.Frames[index] = AsyncProfilerStitchedFrame.CreateAsyncDispatchTarget(
                            original.Context, original.SegmentFrameIndex);
                    }
                    else if (obstruction == "parent-current")
                    {
                        context.Frames[index] = AsyncProfilerStitchedFrame.CreateAsyncContextParent(
                            original.Context, original.SegmentFrameIndex, isCurrent: true);
                    }
                    else if (obstruction == "unwind")
                    {
                        context.Frames[index] = AsyncProfilerStitchedFrame.CreateAsyncCompletionUnwind(original.Context);
                    }
                    else if (obstruction == "missing-boundary") { context.Diagnostics.BoundariesNotFound++; }
                    else if (obstruction == "adjacency") { context.Diagnostics.AdjacencyMismatches++; }
                    else if (obstruction == "ambiguous") { context.Diagnostics.V1CompletionAlignmentAmbiguous++; }
                    if (obstruction == "missing-placement")
                    {
                        var unplaced = new AsyncProfilerCallStackTransformState(
                            context.TraceLog, context.Segments, context.TimestampQpc, context.ClassifyBoundary,
                            context.Diagnostics, context.Frames);
                        Assert.False(unplaced.HasVerifiedV1CompletionAnchor(original.Context, bridge));
                        Assert.False(unplaced.HasVerifiedV1CompletionAnchor(
                            original.Context, bridge, allowCompletionUnwind: true));
                    }
                    else
                    {
                        Assert.False(context.HasVerifiedV1CompletionAnchor(original.Context, bridge));
                        Assert.False(context.HasVerifiedV1CompletionAnchor(
                            original.Context, bridge, allowCompletionUnwind: true));
                    }
                }
                finally
                {
                    context.Frames[index] = original;
                    if (obstruction == "missing-boundary") { context.Diagnostics.BoundariesNotFound--; }
                    if (obstruction == "adjacency") { context.Diagnostics.AdjacencyMismatches--; }
                    if (obstruction == "ambiguous") { context.Diagnostics.V1CompletionAlignmentAmbiguous--; }
                }
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("owned", false)]
        [InlineData("owned", true)]
        [InlineData("application", true)]
        [InlineData("foreign", true)]
        [InlineData("unresolved", true)]
        public void V1CompletionMachinery_IndependentPairGroupsOwnedCallbacksWithoutCrossingWhenAll(
            string callbackOwner, bool enableNormalization)
        {
            StitchScenario scenario = CreateV1CompletionMachineryScenario();
            scenario.EnableNormalization = enableNormalization;
            Frame box = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox.MoveNext()",
                "OtherBox.MoveNext");
            Frame whenAll = Frame.CoreLib(
                "System.Threading.Tasks.Task+WhenAllPromise`1[System.Int64].Invoke(class System.Threading.Tasks.Task)",
                "WhenAllPromise.Invoke");
            Frame[] original = scenario.Sync.Take(6).ToArray();
            Frame callback = callbackOwner == "application" ? Frame.App("Scenario.SynchronousCallback") :
                callbackOwner == "foreign" ? Frame.Library("OtherRuntime", original[1].SymbolName, "ForeignCallback") :
                original[1];
            if (callbackOwner == "unresolved") { scenario.OmitSymbolFor = callback; }
            Frame[] prefix =
            {
                original[0], box, callback, original[2], original[3], original[4],
                whenAll, original[3], original[4], original[5],
            };
            scenario.Sync = prefix.Concat(scenario.Sync.Skip(6)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = prefix
                .Concat(scenario.ExpectedWithoutInfrastructureCleanup.Skip(6)).ToArray();
            Frame[] grouped = callbackOwner == "owned" ? new[]
            {
                original[0], box.DispatcherFrames(), whenAll, original[3].DispatcherFrames(),
            } : new[]
            {
                original[0], box.DispatcherFrames(), callback, original[2].DispatcherFrames(),
                whenAll, original[3].DispatcherFrames(),
            };
            scenario.ExpectedStitched = grouped.Concat(scenario.ExpectedStitched.Skip(2)).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = callbackOwner == "owned" ? 18 : 16;
            scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = callbackOwner == "owned" ? 12 : 8;
            scenario.AssertProductionStitch();
        }

        [Fact]
        public void V1CompletionMachinery_UsesTheStructuralCanonicalMethodResolver()
        {
            StitchScenario scenario = CreateV1CompletionMachineryScenario();
            scenario.AssertTransformContext = context =>
            {
                var frames = context.Frames.ToList();
                int index = frames.FindIndex(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompleting);
                AsyncProfilerStitchedFrame original = frames[index];
                AsyncProfilerCallStack activation = original.Context;
                CodeAddressIndex captured = activation.Frames.CodeAddressAt(original.SegmentFrameIndex);
                MethodIndex canonical = context.TraceLog.CodeAddresses.MethodIndex(activation.Frames.CodeAddressAt(1));
                Assert.NotEqual(canonical, context.TraceLog.CodeAddresses.MethodIndex(original.CodeAddress));
                frames[index] = AsyncProfilerStitchedFrame.CreateAsyncCompleting(
                    original.CodeAddress, canonical, activation, original.SegmentFrameIndex);
                CodeAddressIndex dispatch = Enumerable.Range(0, context.TraceLog.CodeAddresses.Count)
                    .Select(i => (CodeAddressIndex)i).Single(ca =>
                        context.ClassifyBoundary(ca).Kind == AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher);
                var placements = new[] { new AsyncProfilerSegmentPlacement(activation, 0, frames.Count, dispatch: dispatch) };
                var diagnostics = new AsyncProfilerCallStackDiagnostics { SegmentsProcessed = 1 };
                var raw = new AsyncProfilerCallStackTransformState(
                    context.TraceLog, context.Segments, context.TimestampQpc, context.ClassifyBoundary, diagnostics, frames,
                    segmentPlacements: placements);
                var normalized = new AsyncProfilerCallStackTransformState(
                    context.TraceLog, context.Segments, context.TimestampQpc, context.ClassifyBoundary, diagnostics, frames,
                    segmentPlacements: placements,
                    methodOf: ca => ca == original.CodeAddress || ca == captured ? canonical : context.GetMethod(ca));
                Assert.False(raw.HasVerifiedV1CompletionAnchor(activation, frames[index - 1].CodeAddress));
                Assert.True(normalized.HasVerifiedV1CompletionAnchor(activation, frames[index - 1].CodeAddress));
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("foreign-yield")]
        [InlineData("unresolved-yield")]
        [InlineData("interrupted")]
        [InlineData("foreign-anchor")]
        public void V1CompletionMachinery_RejectsUnownedOrInterruptedYieldBridges(string obstruction)
        {
            StitchScenario scenario = CreateV1CompletionMachineryScenario();
            int yieldIndex = Array.IndexOf(scenario.Sync, Frame.ThreadPoolDispatch) - 1;
            Frame yield = scenario.Sync[yieldIndex];
            Frame[] tail = scenario.Sync.Skip(yieldIndex).ToArray();
            if (obstruction == "foreign-yield")
            {
                tail[0] = Frame.Library("OtherRuntime", yield.SymbolName, "ForeignYield");
            }
            else if (obstruction == "unresolved-yield") { scenario.OmitSymbolFor = yield; }
            else if (obstruction == "interrupted")
            {
                tail = new[] { yield, Frame.App("Scenario.Callback") }.Concat(tail.Skip(1)).ToArray();
            }
            else { tail[1] = Frame.Library("OtherRuntime", Frame.ThreadPoolDispatch.SymbolName, "ForeignDispatch"); }
            scenario.Sync = scenario.Sync.Take(yieldIndex).Concat(tail).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = scenario.ExpectedWithoutInfrastructureCleanup
                .Take(scenario.ExpectedWithoutInfrastructureCleanup.Length - 3).Concat(tail).ToArray();
            scenario.ExpectedStitched = scenario.ExpectedStitched.Take(4).Concat(tail).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 10;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DispatcherCreationOwnership_PreservesCalleesAboveTheVerifiedEntry(bool unresolved)
        {
            StitchScenario scenario = CreateDispatcherOwnedPrefixScenario();
            Frame callback = unresolved
                ? Frame.CoreLib("System.Runtime.CompilerServices.UnknownCallback.Run", "UnknownCallback.Run")
                : Frame.App("Scenario.Callback");
            if (unresolved)
            {
                scenario.OmitSymbolFor = callback;
            }
            scenario.Sync = new[] { callback }.Concat(scenario.Sync).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup =
                new[] { callback }.Concat(scenario.ExpectedWithoutInfrastructureCleanup).ToArray();
            scenario.ExpectedStitched = new[] { callback }.Concat(scenario.ExpectedStitched).ToArray();

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("application")]
        [InlineData("foreign")]
        [InlineData("unresolved")]
        public void DispatcherCreationOwnership_UnverifiedRegistration_IsProtected(string obstruction)
        {
            StitchScenario scenario = CreateDispatcherOwnedPrefixScenario();
            Frame awaiter = scenario.Sync[2];
            if (obstruction == "unresolved")
            {
                scenario.OmitSymbolFor = awaiter;
            }
            else
            {
                Frame replacement = obstruction == "application"
                    ? Frame.App("Scenario.Callback")
                    : Frame.Library("OtherLibrary", awaiter.SymbolName, awaiter.Label);
                scenario.Sync[2] = replacement;
                scenario.ExpectedWithoutInfrastructureCleanup[2] = replacement;
            }
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup.Take(6)
                .Concat(scenario.ExpectedStitched).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 10;
            scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = 0;

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void DispatcherCreationOwnership_NoActiveSegment_IsVerbatim()
        {
            StitchScenario scenario = CreateDispatcherOwnedPrefixScenario();
            scenario.AsyncStartQpc = 100_030;
            scenario.ExpectedStitched = scenario.Sync;
            scenario.ExpectedWithoutInfrastructureCleanup = scenario.Sync;
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation();

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void DispatcherCreation_UnresolvedOrForeignHelper_IsPreserved(bool foreign)
        {
            StitchScenario scenario = CreateDispatcherCreationScenario();
            Frame helper = scenario.Sync[0];
            if (foreign)
            {
                helper = Frame.Library("OtherLibrary", helper.SymbolName, helper.Label + " (foreign)");
                scenario.Sync[0] = helper;
                scenario.ExpectedWithoutInfrastructureCleanup[0] = helper;
            }
            else
            {
                scenario.OmitSymbolFor = helper;
            }
            scenario.ExpectedStitched = new[] { helper }.Concat(scenario.ExpectedStitched).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed -= 2;

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void DispatcherCreation_NoActiveSegment_IsVerbatim()
        {
            StitchScenario scenario = CreateDispatcherCreationScenario();
            scenario.AsyncStartQpc = 100_030;
            scenario.ExpectedStitched = scenario.Sync;
            scenario.ExpectedWithoutInfrastructureCleanup = scenario.Sync;
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation();

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void DispatcherCreation_InsideProfilerOwnedPrefix_PreservesItsCallback()
        {
            StitchScenario scenario = CreateProfilerEnvelopeScenario();
            Frame helper = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncStateMachineDispatcherInfo.CreateDispatcher(class System.Runtime.CompilerServices.IAsyncStateMachineBox,value class Flags)",
                "CreateDispatcher");
            Frame callback = Frame.App("Scenario.Callback");
            Frame[] prefix = { helper, callback };
            scenario.Sync = prefix.Concat(scenario.Sync).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup =
                prefix.Concat(scenario.ExpectedWithoutInfrastructureCleanup).ToArray();
            scenario.ExpectedStitched = prefix.Concat(scenario.ExpectedStitched).ToArray();

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, false, true)]
        [InlineData(false, true, false)]
        [InlineData(false, true, true)]
        [InlineData(true, false, false)]
        [InlineData(true, false, true)]
        [InlineData(true, true, false)]
        [InlineData(true, true, true)]
        public void V1DispatchTarget_IsDistinctAndPreservesThePhysicalDispatcherByDefault(
            bool enableInfrastructureCleanup, bool enableFrameAnnotations, bool enableNormalization)
        {
            Frame current = Frame.App("Scenario+<BurnCpuAsync>d__63.MoveNext");
            Frame logicalCurrent = current.Logical("Scenario.BurnCpuAsync", enableFrameAnnotations ? 1 : (int?)null);
            Frame parent = Frame.App("Scenario+<ParentAsync>d__4.MoveNext")
                .Logical("Scenario.ParentAsync", enableFrameAnnotations ? 1 : (int?)null);
            var scenario = new StitchScenario
            {
                Kind = AsyncCallstackKind.StateMachineAsync,
                Sync = new[] { Frame.V1MoveNextAsDispatcher, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart },
                Async = new[] { logicalCurrent, parent },
                AsyncStates = new[] { 1, 1 },
                EnableInfrastructureCleanup = enableInfrastructureCleanup,
                EnableFrameAnnotations = enableFrameAnnotations,
                EnableNormalization = enableNormalization,
                VerifyThreadTimeComputer = true,
                IncludeEventSourceEvents = false,
                AllowSupplementalSamples = true,
                VerifySampleWeights = true,
                ExpectedStitched = (enableInfrastructureCleanup ? Array.Empty<Frame>() : new[] { Frame.V1MoveNextAsDispatcher })
                    .Concat(new[] { logicalCurrent, parent, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart }).ToArray(),
                ExpectedWithoutInfrastructureCleanup =
                    new[] { Frame.V1MoveNextAsDispatcher, logicalCurrent, parent, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V1InlineFallbackUsed = 2,
                    AsyncInfrastructureFramesCollapsed = enableInfrastructureCleanup ? 2 : 0,
                },
                AssertStitchedStackSource = stackSource =>
                {
                    int targets = 0;
                    for (int i = 0; i < (int)stackSource.CallFrameIndexLimit; i++)
                    {
                        var frameIndex = (StackSourceFrameIndex)i;
                        if (!stackSource.TryGetAsyncProfilerCallStackFrameInfo(frameIndex, out AsyncProfilerCallStackSourceFrameInfo info) ||
                            !info.IsDispatchTarget)
                        {
                            continue;
                        }
                        targets++;
                        Assert.False(info.IsParentContext);
                        Assert.Equal(AsyncCallstackKind.StateMachineAsync, info.Kind);
                        Assert.Equal(1, info.State);
                        Assert.NotEqual(CodeAddressIndex.Invalid, info.CodeAddress);
                        Assert.Contains("BurnCpuAsync", stackSource.TraceLog.CodeAddresses[info.CodeAddress].FullMethodName);
                        string name = stackSource.GetFrameName(frameIndex, false);
                        Assert.StartsWith(enableFrameAnnotations ? "[Async V1] " : logicalCurrent.ModuleName.ToLowerInvariant() + "!", name);
                        Assert.DoesNotContain("dispatching", name);
                    }
                    Assert.Equal(1, targets);
                },
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void V1DispatchTarget_KeepsDistinctMetadataButSharesItsExportedName(bool enableFrameAnnotations)
        {
            Frame current = Frame.App("Scenario+<RecursiveAsync>d__1.MoveNext")
                .Logical("Scenario.RecursiveAsync", enableFrameAnnotations ? 1 : (int?)null);
            var scenario = new StitchScenario
            {
                Kind = AsyncCallstackKind.StateMachineAsync,
                Sync = new[] { Frame.V1MoveNextAsDispatcher, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart },
                Async = new[] { current, current },
                AsyncStates = new[] { 1, 1 },
                EnableFrameAnnotations = enableFrameAnnotations,
                ExpectedStitched =
                    new[] { Frame.V1MoveNextAsDispatcher, current, current, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation { SegmentsProcessed = 2, V1InlineFallbackUsed = 2 },
                AssertStitchedStackSource = stackSource =>
                {
                    var matches = new List<(StackSourceFrameIndex Index, string Name, AsyncProfilerCallStackSourceFrameInfo Info)>();
                    for (int i = 0; i < (int)stackSource.CallFrameIndexLimit; i++)
                    {
                        var frameIndex = (StackSourceFrameIndex)i;
                        if (stackSource.TryGetAsyncProfilerCallStackFrameInfo(frameIndex, out AsyncProfilerCallStackSourceFrameInfo info))
                        {
                            matches.Add((frameIndex, stackSource.GetFrameName(frameIndex, false), info));
                        }
                    }
                    Assert.Equal(2, matches.Count);
                    Assert.Single(matches.Where(frame => frame.Info.IsDispatchTarget));
                    Assert.Single(matches.Where(frame => !frame.Info.IsDispatchTarget));
                    Assert.NotEqual(matches[0].Index, matches[1].Index);
                    Assert.Equal(matches[0].Info.CodeAddress, matches[1].Info.CodeAddress);
                    Assert.Equal(matches[0].Info.MethodId, matches[1].Info.MethodId);
                    Assert.Equal(matches[0].Info.State, matches[1].Info.State);
                    Assert.Equal(matches[0].Name, matches[1].Name);
                    Assert.DoesNotContain("dispatching", matches[0].Name);

                    var exportedNames = new Dictionary<string, int>();
                    var exportedFrames = new Dictionary<int, StackSourceWriterHelper.FrameInfo>();
                    List<StackSourceWriterHelper.Sample> samples =
                        Assert.Single(StackSourceWriterHelper.GetSortedSamplesPerThread(stackSource)).Value;
                    IReadOnlyList<StackSourceWriterHelper.ProfileEvent> events =
                        StackSourceWriterHelper.GetProfileEvents(stackSource, samples, exportedNames, exportedFrames);
                    int exportedId = exportedNames[matches[0].Name];
                    Assert.Equal(2, events.Count(profileEvent =>
                        profileEvent.FrameId == exportedId &&
                        profileEvent.Type == StackSourceWriterHelper.ProfileEventType.Open));
                },
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void V1DispatchTarget_PhysicallyMatchedCurrentAndNoSegmentKeepTheirExistingBehavior(bool noSegment)
        {
            StitchScenario scenario = CreateRuntimeResumptionBridgeScenario(AsyncCallstackKind.StateMachineAsync);
            scenario.VerifyThreadTimeComputer = true;
            scenario.IncludeEventSourceEvents = false;
            scenario.AllowSupplementalSamples = true;
            if (noSegment)
            {
                scenario.AsyncStartQpc = 100_030;
                scenario.ExpectedStitched = scenario.Sync;
                scenario.ExpectedWithoutInfrastructureCleanup = scenario.Sync;
                scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation();
            }
            scenario.AssertStitchedStackSource = stackSource =>
            {
                for (int i = 0; i < stackSource.CallFrameIndexLimit; i++)
                {
                    if (stackSource.TryGetAsyncProfilerCallStackFrameInfo((StackSourceFrameIndex)i, out AsyncProfilerCallStackSourceFrameInfo info))
                    {
                        Assert.False(info.IsDispatchTarget);
                    }
                }
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, true, false)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, false, false)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, true, true)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, true, false)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, false, false)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, true, true)]
        public void PdbNames_EnableOnlyVerifiedResumptionBridgeCleanupWithoutRewritingSymbols(
            int kindValue, bool cleanup, bool lookalike)
        {
            StitchScenario scenario = CreateRuntimeResumptionBridgeScenario((AsyncCallstackKind)kindValue);
            Frame[] pdbFrames =
            {
                Frame.CoreLib("System.Void System.Threading.Tasks.Task::RunContinuations(System.Object)", "Task.RunContinuations"),
                Frame.CoreLib(lookalike
                    ? "System.Boolean Other.Namespace.Task::TrySetResult(!0)"
                    : "System.Boolean System.Threading.Tasks.Task`1[System.Int64]::TrySetResult(!0)", "Task.TrySetResult"),
                Frame.CoreLib("System.Threading.ThreadPoolWorkQueue+DispatchResult System.Threading.ThreadPoolWorkQueue::Dispatch()", "ThreadPoolWorkQueue.Dispatch"),
                Frame.CoreLib("System.Void System.Threading.PortableThreadPool+WorkerThread::WorkerThreadStart()", "WorkerThreadStart"),
            };
            foreach (Frame pdbFrame in pdbFrames)
            {
                Frame original = scenario.Sync.Single(frame => frame.Label == pdbFrame.Label);
                scenario.Sync = scenario.Sync.Select(frame => frame == original ? pdbFrame : frame).ToArray();
                scenario.ExpectedStitched = scenario.ExpectedStitched.Select(frame => frame == original ? pdbFrame : frame).ToArray();
                scenario.ExpectedWithoutInfrastructureCleanup = scenario.ExpectedWithoutInfrastructureCleanup
                    .Select(frame => frame == original ? pdbFrame : frame).ToArray();
            }
            scenario.EnableInfrastructureCleanup = cleanup;
            scenario.VerifyThreadTimeComputer = true;
            scenario.IncludeEventSourceEvents = false;
            scenario.AllowSupplementalSamples = true;
            if (!cleanup || lookalike)
            {
                scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
                scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
                scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;
            }
            scenario.AssertPublicApi = (log, path) =>
            {
                TraceEvent sample = log.Events.First(data => data.CallStackIndex() != CallStackIndex.Invalid);
                var names = new List<string>();
                for (CallStackIndex stack = sample.CallStackIndex(); stack != CallStackIndex.Invalid; stack = log.CallStacks.Caller(stack))
                {
                    names.Add(log.CodeAddresses[log.CallStacks.CodeAddressIndex(stack)].FullMethodName);
                }
                foreach (Frame frame in pdbFrames)
                {
                    Assert.Contains(frame.SymbolName, names);
                }
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, false, false)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, false, true)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, true, false)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, true, true)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, false, false)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, false, true)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, true, false)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, true, true)]
        public void RuntimeResumptionBridge_FoldsOnlyThePhysicalRootBridge(
            int kindValue, bool enableNormalization, bool enableParentContexts)
        {
            var kind = (AsyncCallstackKind)kindValue;
            StitchScenario scenario = CreateRuntimeResumptionBridgeScenario(kind);
            scenario.EnableNormalization = enableNormalization;
            scenario.EnableParentContexts = enableParentContexts;
            scenario.VerifyThreadTimeComputer = true;
            scenario.IncludeEventSourceEvents = false;
            scenario.AllowSupplementalSamples = true;
            if (enableParentContexts)
            {
                Frame owner = Frame.App("Scenario.OwnerAsync");
                scenario.ContextParent = AsyncSegment.Runtime(owner);
                scenario.ExpectedStitched = scenario.ExpectedStitched.Take(3)
                    .Concat(new[] { owner }).Concat(scenario.ExpectedStitched.Skip(3)).ToArray();
                scenario.ExpectedWithoutInfrastructureCleanup =
                    scenario.ExpectedWithoutInfrastructureCleanup.Take(3)
                    .Concat(new[] { owner })
                    .Concat(scenario.ExpectedWithoutInfrastructureCleanup.Skip(3)).ToArray();
            }

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, 0)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, 1)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, 2)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, 3)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, 4)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, 5)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, 0)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, 1)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, 2)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, 3)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, 4)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, 5)]
        public void RuntimeResumptionBridge_UnresolvedFrameOrAnchor_PreservesTheCompleteBridge(
            int kindValue, int unresolved)
        {
            var kind = (AsyncCallstackKind)kindValue;
            StitchScenario scenario = CreateRuntimeResumptionBridgeScenario(kind);
            scenario.OmitSymbolFor = scenario.Sync[scenario.Sync.Length - 7 + unresolved];
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, 0)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, 1)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, 2)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, 3)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, 4)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, 5)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, 0)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, 1)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, 2)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, 3)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, 4)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, 5)]
        public void RuntimeResumptionBridge_ForeignFrameOrAnchor_PreservesTheCompleteBridge(
            int kindValue, int foreignIndex)
        {
            var kind = (AsyncCallstackKind)kindValue;
            StitchScenario scenario = CreateRuntimeResumptionBridgeScenario(kind);
            int physicalIndex = scenario.Sync.Length - 7 + foreignIndex;
            Frame original = scenario.Sync[physicalIndex];
            Frame foreign = Frame.Library("OtherLibrary", original.SymbolName, original.Label + " (foreign)");
            scenario.Sync[physicalIndex] = foreign;
            scenario.ExpectedWithoutInfrastructureCleanup[3 + foreignIndex] = foreign;
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "B")]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "BR")]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "BT")]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "BTR")]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "BRXT")]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "BRTX")]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "BRTN")]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "BRTW")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "B")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "BR")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "BT")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "BTR")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "BRXT")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "BRTX")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "BRTN")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "BRTW")]
        public void RuntimeResumptionBridge_IncompleteOrInterrupted_PreservesTheCompleteBridge(
            int kindValue, string pattern)
        {
            var kind = (AsyncCallstackKind)kindValue;
            StitchScenario scenario = CreateRuntimeResumptionBridgeScenario(kind);
            int tailStart = scenario.Sync.Length - 7;
            Frame[] bridge = scenario.Sync.Skip(tailStart).Take(3).ToArray();
            var frames = new Dictionary<char, Frame>
            {
                ['B'] = bridge[0],
                ['R'] = bridge[1],
                ['T'] = bridge[2],
                ['X'] = Frame.App("Scenario.SynchronousCallback"),
                ['N'] = Frame.Library("NativeBridge", "Native.Callback", "Native.Callback"),
                ['W'] = Frame.V2Wrapper(1),
            };
            Frame[] tail = pattern.Select(token => frames[token])
                .Concat(scenario.Sync.Skip(tailStart + 3)).ToArray();
            scenario.Sync = scenario.Sync.Take(tailStart).Concat(tail).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup =
                scenario.ExpectedWithoutInfrastructureCleanup.Take(3).Concat(tail).ToArray();
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync)]
        public void RuntimeResumptionBridge_NoActiveSegment_IsVerbatim(int kindValue)
        {
            var kind = (AsyncCallstackKind)kindValue;
            StitchScenario scenario = CreateRuntimeResumptionBridgeScenario(kind);
            scenario.AsyncStartQpc = 100_030;
            scenario.ExpectedStitched = scenario.Sync;
            scenario.ExpectedWithoutInfrastructureCleanup = scenario.Sync;
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation();

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void RuntimeResumptionBridge_V1ResumePrelude_PreservesProfilerWorkAndLogicalCaller()
        {
            StitchScenario scenario = CreateRuntimeResumptionBridgeScenario(AsyncCallstackKind.StateMachineAsync);
            Frame profiler = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncProfiler+AsyncCallstack.EmitEvent", "Profiler.EmitEvent");
            scenario.Sync = new[] { profiler }.Concat(scenario.Sync.Skip(2)).ToArray();
            Frame[] prelude = { profiler, Frame.V1MoveNextAsDispatcher, scenario.Async[0] };
            scenario.ExpectedStitched = prelude.Concat(scenario.ExpectedStitched.Skip(2)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = prelude
                .Concat(scenario.ExpectedWithoutInfrastructureCleanup.Skip(2)).ToArray();

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(1, false, false)]
        [InlineData(1, false, true)]
        [InlineData(2, false, false)]
        [InlineData(2, false, true)]
        [InlineData(2, true, false)]
        [InlineData(2, true, true)]
        public void ProfilerOwnership_FoldsIntoTheDispatchTarget(
            int resumeCount, bool includeEmission, bool enableNormalization)
        {
            StitchScenario scenario = CreateProfilerHelperTailScenario(resumeCount, includeEmission);
            scenario.EnableNormalization = enableNormalization;
            scenario.AssertStitchedStackSource = stackSource =>
            {
                var targets = new List<AsyncProfilerCallStackSourceFrameInfo>();
                for (int i = 0; i < stackSource.CallFrameIndexLimit; i++)
                {
                    var frameIndex = (StackSourceFrameIndex)i;
                    if (stackSource.TryGetAsyncProfilerCallStackFrameInfo(frameIndex, out AsyncProfilerCallStackSourceFrameInfo info) &&
                        info.IsDispatchTarget)
                    {
                        targets.Add(info);
                        string name = stackSource.GetFrameName(frameIndex, false);
                        Assert.StartsWith(scenario.Async[0].ModuleName.ToLowerInvariant() + "!", name);
                        Assert.DoesNotContain("dispatching", name);
                    }
                }
                AsyncProfilerCallStackSourceFrameInfo target = Assert.Single(targets);
                Assert.False(target.IsParentContext);
                Assert.Equal(AsyncCallstackKind.StateMachineAsync, target.Kind);
                Assert.Equal(0, target.State);
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ProfilerOwnership_CaptureEntryWithoutResume_KeepsNativeWorkAndPendingTarget(bool enableNormalization)
        {
            StitchScenario scenario = CreateProfilerCaptureEntryScenario();
            Frame native = Frame.Library("coreclr", "MethodDesc.PrepareCode", "NativeWork");
            scenario.Sync = new[] { native }.Concat(scenario.Sync).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = new[] { native }
                .Concat(scenario.ExpectedWithoutInfrastructureCleanup).ToArray();
            scenario.ExpectedStitched = new[] { native }.Concat(scenario.ExpectedStitched).ToArray();
            scenario.OmitSymbolFor = native;
            scenario.EnableNormalization = enableNormalization;
            scenario.AssertTransformContext = context =>
            {
                Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, context.Frames[0].Origin);
                Assert.Equal(AsyncProfilerStitchedFramePresentation.Native, context.Frames[0].Presentation);
                Assert.Equal(AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames, context.Frames[1].Presentation);
                AsyncProfilerStitchedFrame target = Assert.Single(context.Frames.Where(frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget));
                Assert.Same(context.Segments[0], target.Context);
                Assert.Equal(0, target.Segment.FrameStateAt(target.SegmentFrameIndex));
                Assert.DoesNotContain(context.Frames, frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent ||
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompleting);
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("unresolved")]
        [InlineData("foreign")]
        [InlineData("interrupted")]
        [InlineData("profile-veto")]
        public void ProfilerOwnership_CaptureEntryWithoutResume_RequiresContiguousResolvedOwnership(string obstruction)
        {
            StitchScenario scenario = CreateProfilerCaptureEntryScenario();
            Frame capture = scenario.Sync[0];
            bool protectedPrefix = obstruction == "interrupted" || obstruction == "profile-veto";
            if (obstruction == "unresolved") { scenario.OmitSymbolFor = capture; }
            if (obstruction == "foreign")
            {
                capture = Frame.Library("OtherLibrary", capture.SymbolName, capture.Label);
                scenario.Sync[0] = capture;
                scenario.ExpectedWithoutInfrastructureCleanup[0] = capture;
            }
            Frame[] prefix = obstruction == "interrupted"
                ? new[] { capture, Frame.App("Scenario.Callback") } : new[] { capture };
            scenario.Sync = prefix.Concat(scenario.Sync.Skip(1)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = prefix
                .Concat(scenario.ExpectedWithoutInfrastructureCleanup.Skip(1)).ToArray();
            scenario.ExpectedStitched = prefix
                .Concat(protectedPrefix ? new[] { Frame.V1MoveNextAsDispatcher } : Array.Empty<Frame>())
                .Concat(scenario.ExpectedStitched).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = protectedPrefix ? 10 : 12;
            scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = 0;
            if (obstruction == "profile-veto")
            {
                var profile = new RejectingProfilerEntryProfile();
                scenario.RuntimeProfileForModule = module => AsyncProfilerRuntimeRuleProfileCache.IsHostModule(module)
                    ? profile : AsyncProfilerRuntimeRuleProfile.Common;
            }

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void ProfilerOwnership_CaptureEntryWithoutResume_DoesNotOwnAPhysicalApplicationBody()
        {
            StitchScenario scenario = CreateProfilerCaptureEntryScenario();
            Frame capture = scenario.Sync[0];
            Frame current = Frame.App("Scenario+<DeepLevel3Async>d__3.MoveNext");
            scenario.Sync = new[] { capture, current }.Concat(scenario.Sync.Skip(1)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = scenario.ExpectedWithoutInfrastructureCleanup
                .Where(frame => frame != Frame.V1MoveNextAsDispatcher).ToArray();
            scenario.ExpectedStitched = new[] { capture }.Concat(scenario.ExpectedStitched).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 10;
            scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = 0;
            scenario.AssertTransformContext = context =>
            {
                Assert.Single(context.Frames.Where(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent));
                Assert.DoesNotContain(context.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget);
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("application")]
        [InlineData("foreign")]
        [InlineData("unknown-corelib")]
        [InlineData("diagnostic")]
        [InlineData("builder")]
        [InlineData("unresolved")]
        [InlineData("native")]
        public void ProfilerOwnership_PreservesUnrecognizedCallees(string callee)
        {
            StitchScenario scenario = CreateProfilerHelperTailScenario(2);
            Frame frame;
            switch (callee)
            {
                case "application":
                    frame = Frame.App("Scenario.Callback");
                    break;
                case "foreign":
                    frame = Frame.Library("OtherLibrary", "OtherLibrary.Callback", "OtherLibrary.Callback");
                    break;
                case "native":
                    frame = Frame.Library("coreclr", "SampledNativeCallee", "NativeCallee");
                    scenario.OmitSymbolFor = frame;
                    break;
                case "unknown-corelib":
                    frame = Frame.CoreLib("System.Runtime.CompilerServices.OtherHelpers.Initialize", "OtherHelpers.Initialize");
                    break;
                case "diagnostic":
                    frame = Frame.CoreLib("System.Runtime.CompilerServices.InitHelpers.InitClassSlow", "InitHelpers.InitClassSlow");
                    break;
                case "builder":
                    frame = Frame.CoreLib("System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start", "AsyncMethodBuilderCore.Start");
                    break;
                default:
                    frame = Frame.CoreLib("System.Runtime.CompilerServices.UnknownCallback.Run", "UnknownCallback.Run");
                    scenario.OmitSymbolFor = frame;
                    break;
            }
            scenario.Sync = new[] { frame }.Concat(scenario.Sync).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup =
                new[] { frame }.Concat(scenario.ExpectedWithoutInfrastructureCleanup).ToArray();
            scenario.ExpectedStitched = new[] { frame }.Concat(scenario.ExpectedStitched).ToArray();
            scenario.AssertTransformContext = context =>
            {
                Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, context.Frames[0].Origin);
                Assert.Equal(AsyncProfilerStitchedFramePresentation.Native, context.Frames[0].Presentation);
                AsyncProfilerStitchedFrame marker = Assert.Single(context.Frames.Where(f =>
                    f.Presentation == AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames));
                Assert.Equal(context.Frames[1], marker);
                Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, marker.Origin);
                Assert.Same(context.Frames[0].Context, marker.Context);
                Assert.Contains(context.Frames, f => f.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget);
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ProfilerOwnership_UnresolvedOrForeignResume_IsPreserved(bool foreign)
        {
            StitchScenario scenario = CreateProfilerHelperTailScenario();
            Frame resume = scenario.Sync[0];
            if (foreign)
            {
                resume = Frame.Library("OtherLibrary", resume.SymbolName, resume.Label);
                scenario.Sync[0] = resume;
                scenario.ExpectedWithoutInfrastructureCleanup[0] = resume;
            }
            else
            {
                scenario.OmitSymbolFor = resume;
            }
            // Without a recognized profiler candidate, existing individual cleanup still removes the dispatcher.
            scenario.ExpectedStitched = new[] { resume }.Concat(scenario.ExpectedStitched).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed -= 2;
            scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = 0;

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("resume-context")]
        [InlineData("dispatcher-context")]
        [InlineData("different-activation")]
        [InlineData("unresolved-resume")]
        [InlineData("unresolved-dispatcher")]
        [InlineData("missing-boundary")]
        [InlineData("adjacency-mismatch")]
        [InlineData("incomplete-structural-processing")]
        [InlineData("missing-trace")]
        public void ProfilerOwnership_RequiresResolvedMetadataAndValidatedContext(string missingEvidence)
        {
            StitchScenario scenario = CreateProfilerHelperTailScenario();
            scenario.EnableInfrastructureCleanup = false;
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = 0;
            scenario.AssertTransformContext = context =>
            {
                List<AsyncProfilerStitchedFrame> frames = context.Frames.ToList();
                var diagnostics = new AsyncProfilerCallStackDiagnostics { SegmentsProcessed = context.Segments.Count };
                if (missingEvidence == "resume-context" || missingEvidence == "dispatcher-context" ||
                    missingEvidence == "different-activation" ||
                    missingEvidence == "unresolved-resume" || missingEvidence == "unresolved-dispatcher")
                {
                    int index = missingEvidence == "dispatcher-context" ||
                                missingEvidence == "unresolved-dispatcher" ? 1 : 0;
                    AsyncProfilerStitchedFrame frame = frames[index];
                    bool unresolved = missingEvidence.StartsWith("unresolved-", StringComparison.Ordinal);
                    AsyncProfilerCallStack owner = unresolved ? frame.Context : null;
                    if (missingEvidence == "different-activation")
                    {
                        owner = new AsyncProfilerCallStackComputer(context.TraceLog).GetCallStacks(1234, 5000, context.TimestampQpc)[0];
                        Assert.NotSame(frame.Context, owner);
                    }
                    frames[index] = AsyncProfilerStitchedFrame.CreateSync(
                        new AsyncProfilerStitchSyncFrame(frame.CodeAddress,
                            unresolved ? MethodIndex.Invalid : frame.Method, frame.SyncFrameKind),
                        owner);
                }
                else if (missingEvidence == "missing-boundary")
                {
                    diagnostics.BoundariesNotFound = 1;
                }
                else if (missingEvidence == "adjacency-mismatch")
                {
                    diagnostics.AdjacencyMismatches = 1;
                }
                else if (missingEvidence == "incomplete-structural-processing")
                {
                    diagnostics.SegmentsProcessed = 0;
                }
                AsyncProfilerStitchedFrame resume = frames[0];
                AsyncProfilerStitchedFrame dispatcher = frames[1];
                var pipeline = new AsyncProfilerCallStackTransformPipeline
                {
                    EnableNormalization = false,
                    EnableInfrastructureCleanup = true,
                };
                pipeline.ApplyInPlace(new AsyncProfilerCallStackTransformState(
                    missingEvidence == "missing-trace" ? null : context.TraceLog,
                    context.Segments, context.TimestampQpc, context.ClassifyBoundary, diagnostics, frames));

                Assert.Equal(resume, frames[0]);
                if (missingEvidence != "missing-trace")
                {
                    Assert.Equal(dispatcher, frames[1]);
                }
                Assert.Equal(0, diagnostics.ProfilerEnvelopeFramesCollapsed);
                Assert.Single(frames.Where(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget));
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void ProfilerOwnership_InterruptedBeforeTheDispatcher_IsProtected()
        {
            StitchScenario scenario = CreateProfilerHelperTailScenario();
            Frame callback = Frame.App("Scenario.Callback");
            scenario.Sync = new[] { scenario.Sync[0], callback }.Concat(scenario.Sync.Skip(1)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup =
                new[] { scenario.Sync[0], callback }
                    .Concat(scenario.ExpectedWithoutInfrastructureCleanup.Skip(1)).ToArray();
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup.Take(3)
                .Concat(scenario.ExpectedStitched).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 10;
            scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = 0;

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void ProfilerOwnership_CallbackAboveTheEntry_InterruptsHelperGrouping()
        {
            StitchScenario scenario = CreateProfilerHelperTailScenario(2);
            Frame callback = Frame.App("Scenario.Callback");
            scenario.Sync = new[] { scenario.Sync[0], callback }.Concat(scenario.Sync.Skip(1)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = new[] { scenario.Sync[0], callback }
                .Concat(scenario.ExpectedWithoutInfrastructureCleanup.Skip(1)).ToArray();
            scenario.ExpectedStitched = new[] { scenario.Sync[0], callback }.Concat(scenario.ExpectedStitched).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed -= 2;
            scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed -= 2;

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void ProfilerOwnership_RejectedRootBridge_RemainsProtectedAfterPrefixRemoval()
        {
            StitchScenario scenario = CreateProfilerHelperTailScenario();
            Frame callback = Frame.App("Scenario.Callback");
            scenario.Sync[scenario.Sync.Length - 3] = callback;
            scenario.ExpectedWithoutInfrastructureCleanup[scenario.ExpectedWithoutInfrastructureCleanup.Length - 3] = callback;
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup.Skip(2).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 4;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void ProfilerOwnership_MissingBoundary_DoesNotInventDispatchAttribution()
        {
            StitchScenario scenario = CreateProfilerHelperTailScenario();
            scenario.Sync = scenario.Sync.Take(1).Concat(scenario.Sync.Skip(3)).ToArray();
            scenario.ExpectedStitched = scenario.Sync.Concat(scenario.Async.Skip(1)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = scenario.ExpectedStitched;
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation
            {
                SegmentsProcessed = 2,
                BoundariesNotFound = 2,
                V1InlineFallbackUsed = 2,
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void ProfilerOwnership_NoActiveSegment_IsVerbatim()
        {
            StitchScenario scenario = CreateProfilerHelperTailScenario();
            scenario.AsyncStartQpc = 100_030;
            scenario.ExpectedStitched = scenario.Sync;
            scenario.ExpectedWithoutInfrastructureCleanup = scenario.Sync;
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation();

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void ProfilerOwnership_PhysicallyMatchedCurrent_PreservesTheApplicationAnchor()
        {
            StitchScenario scenario = CreateProfilerHelperTailScenario();
            Frame current = Frame.App("Scenario+<DeepLevel3Async>d__3.MoveNext");
            scenario.Sync = scenario.Sync.Take(1).Concat(new[] { current }).Concat(scenario.Sync.Skip(1)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup =
                scenario.ExpectedWithoutInfrastructureCleanup.Where(frame => frame != Frame.V1MoveNextAsDispatcher).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 12;
            scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = 2;

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ProfilerOwnership_CaptureWithoutEmission_PreservesDiagnosticsAndUnresolvedInitialization(
            bool initializeDiagnostics)
        {
            StitchScenario scenario = CreateProfilerEnvelopeScenario();
            Frame[] prefix = initializeDiagnostics
                ? scenario.Sync.Take(8).Concat(new[] { scenario.Sync[10] }).ToArray()
                : new[] { scenario.Sync[6], scenario.Sync[7], scenario.Sync[10] };
            if (initializeDiagnostics)
            {
                scenario.OmitSymbolFor = prefix[2];
            }
            scenario.Sync = prefix.Concat(scenario.Sync.Skip(14)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = prefix
                .Concat(scenario.ExpectedWithoutInfrastructureCleanup.Skip(14)).ToArray();
            int retained = initializeDiagnostics ? 5 : 0;
            scenario.ExpectedStitched = prefix.Take(retained).Concat(scenario.ExpectedStitched.Skip(5)).ToArray();
            scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = initializeDiagnostics ? 10 : 8;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed =
                10 + scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed;
            scenario.AssertTransformContext = context =>
            {
                AsyncProfilerStitchedFrame target = Assert.Single(context.Frames.Where(frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget));
                Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget, target.Origin);
                Assert.Equal(0, target.Segment.FrameStateAt(target.SegmentFrameIndex));
                Assert.Equal(retained + 4, context.Frames.Count(frame =>
                    frame.ContextKind == AsyncProfilerStitchedFrameContextKind.Active));
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void ProfilerOwnership_DispatcherWithoutProfilerEntry_PreservesApplicationWork()
        {
            StitchScenario scenario = CreateProfilerHelperTailScenario();
            Frame work = Frame.App("Scenario.Work");
            scenario.Sync[0] = work;
            scenario.ExpectedWithoutInfrastructureCleanup[0] = work;
            scenario.ExpectedStitched = new[] { work }.Concat(scenario.ExpectedStitched).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 12;
            scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = 0;

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("callee-context")]
        [InlineData("different-activation")]
        [InlineData("nested-boundary")]
        [InlineData("physical-anchor")]
        [InlineData("logical-activation")]
        public void ProfilerOwnership_DoesNotCrossContextBoundariesOrApplicationAnchors(string obstruction)
        {
            StitchScenario scenario = CreateProfilerHelperTailScenario();
            Frame callee = Frame.App("Scenario.Callback");
            scenario.Sync = new[] { callee }.Concat(scenario.Sync).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = new[] { callee }
                .Concat(scenario.ExpectedWithoutInfrastructureCleanup).ToArray();
            scenario.EnableInfrastructureCleanup = false;
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;
            scenario.AssertTransformContext = context =>
            {
                List<AsyncProfilerStitchedFrame> frames = context.Frames.ToList();
                AsyncProfilerStitchedFrame original = frames[0];
                AsyncProfilerStitchedFrame entry = frames[1];
                AsyncProfilerCallStack owner = original.Context;
                if (obstruction == "different-activation")
                {
                    owner = new AsyncProfilerCallStackComputer(context.TraceLog).GetCallStacks(1234, 5000, context.TimestampQpc)[0];
                    Assert.NotSame(original.Context, owner);
                }
                frames[0] = obstruction == "physical-anchor"
                    ? AsyncProfilerStitchedFrame.CreateAsyncCurrent(original.CodeAddress, original.Method, owner, 0)
                    : obstruction == "logical-activation"
                        ? AsyncProfilerStitchedFrame.CreateAsync(owner, 0)
                        : obstruction == "nested-boundary"
                            ? frames[2]
                            : AsyncProfilerStitchedFrame.CreateSync(
                                new AsyncProfilerStitchSyncFrame(original.CodeAddress, original.Method, original.SyncFrameKind),
                                obstruction == "callee-context" ? null : owner);
                AsyncProfilerStitchedFrame protectedFrame = frames[0];
                var diagnostics = new AsyncProfilerCallStackDiagnostics { SegmentsProcessed = context.Segments.Count };
                var pipeline = new AsyncProfilerCallStackTransformPipeline
                {
                    EnableNormalization = false,
                    EnableInfrastructureCleanup = true,
                };
                pipeline.ApplyInPlace(new AsyncProfilerCallStackTransformState(
                    context.TraceLog, context.Segments, context.TimestampQpc,
                    context.ClassifyBoundary, diagnostics, frames));

                Assert.Equal(protectedFrame, frames[0]);
                Assert.Equal(entry, frames[1]);
                Assert.Equal(0, diagnostics.ProfilerEnvelopeFramesCollapsed);
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, (int)AsyncCallstackKind.StateMachineAsync)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, (int)AsyncCallstackKind.RuntimeAsync)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, (int)AsyncCallstackKind.StateMachineAsync)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, (int)AsyncCallstackKind.RuntimeAsync)]
        public void ProfilerOwnership_NestedProtocols_PreserveOuterActivationAndInterContextWork(
            int innerKindValue, int outerKindValue)
        {
            var innerKind = (AsyncCallstackKind)innerKindValue;
            var outerKind = (AsyncCallstackKind)outerKindValue;
            StitchScenario source = CreateProfilerEnvelopeScenario();
            Frame[] prefix = { source.Sync[2], source.Sync[6], source.Sync[7], source.Sync[10] };
            Frame innerCurrent = innerKind == AsyncCallstackKind.StateMachineAsync
                ? source.Async[0] : Frame.App("Scenario.InnerV2Async");
            Frame innerParent = innerKind == AsyncCallstackKind.StateMachineAsync
                ? source.Async[1] : Frame.App("Scenario.InnerV2ParentAsync");
            Frame outerIdentity = outerKind == AsyncCallstackKind.StateMachineAsync
                ? Frame.App("Scenario+<OuterAsync>d__2.MoveNext") : Frame.App("Scenario.OuterV2Async");
            Frame outerCurrent = outerKind == AsyncCallstackKind.StateMachineAsync
                ? outerIdentity.Logical("Scenario.OuterAsync") : outerIdentity;
            Frame outerParent = outerKind == AsyncCallstackKind.StateMachineAsync
                ? Frame.App("Scenario+<OuterParentAsync>d__1.MoveNext").Logical("Scenario.OuterParentAsync")
                : Frame.App("Scenario.OuterV2ParentAsync");
            Frame bridge = Frame.App("Scenario.InterContextCallback");
            Frame infrastructure = source.Sync[15];
            var sync = prefix.ToList();
            if (innerKind == AsyncCallstackKind.StateMachineAsync)
            {
                sync.Add(Frame.V1MoveNextAsDispatcher);
                sync.Add(infrastructure);
            }
            else
            {
                sync.Add(innerCurrent);
                AddSyntheticDispatcher(sync, innerKind, null, null);
            }
            sync.Add(bridge);
            sync.Add(outerIdentity);
            if (outerKind == AsyncCallstackKind.StateMachineAsync)
            {
                sync.Add(Frame.V1MoveNextAsDispatcher);
                sync.Add(infrastructure);
            }
            else
            {
                AddSyntheticDispatcher(sync, outerKind, null, null);
            }
            sync.Add(Frame.ThreadPoolDispatch);
            sync.Add(Frame.WorkerThreadStart);
            Frame[] logical =
            {
                innerCurrent, innerParent, bridge, outerCurrent, outerParent,
                Frame.ThreadPoolDispatch, Frame.WorkerThreadStart,
            };
            int v1Segments = (innerKind == AsyncCallstackKind.StateMachineAsync ? 1 : 0) +
                (outerKind == AsyncCallstackKind.StateMachineAsync ? 1 : 0);
            int ownedCount = 3 + (innerKind == AsyncCallstackKind.StateMachineAsync ? 1 : 0);
            var scenario = new StitchScenario
            {
                Sync = sync.ToArray(),
                AsyncSegments = new[]
                {
                    outerKind == AsyncCallstackKind.StateMachineAsync
                        ? AsyncSegment.StateMachine(outerCurrent, outerParent)
                        : AsyncSegment.Runtime(outerCurrent, outerParent),
                    innerKind == AsyncCallstackKind.StateMachineAsync
                        ? AsyncSegment.StateMachine(innerCurrent, innerParent)
                        : AsyncSegment.Runtime(innerCurrent, innerParent),
                },
                OmitSymbolFor = prefix[0],
                EnableInfrastructureCleanup = true,
                VerifySampleWeights = true,
                ExpectedStitched = prefix.Take(1).Concat(logical).ToArray(),
                ExpectedWithoutInfrastructureCleanup = prefix
                    .Concat(innerKind == AsyncCallstackKind.StateMachineAsync
                        ? new[] { Frame.V1MoveNextAsDispatcher } : Array.Empty<Frame>())
                    .Concat(logical).ToArray(),
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 4,
                    V1InlineFallbackUsed = v1Segments * 2,
                    V1InfrastructureFramesCollapsed = v1Segments * 2,
                    V2PlumbingFramesCollapsed = (2 - v1Segments) * 4,
                    AsyncInfrastructureFramesCollapsed = ownedCount * 2,
                    ProfilerEnvelopeFramesCollapsed = ownedCount * 2,
                },
                AssertTransformContext = context =>
                {
                    Assert.Same(context.Segments[1], context.Frames[0].Context);
                    AsyncProfilerStitchedFrame inner = context.Frames.First(frame =>
                        frame.Origin != AsyncProfilerStitchedFrameOrigin.Sync);
                    Assert.Equal(innerKind == AsyncCallstackKind.StateMachineAsync
                        ? AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget : AsyncProfilerStitchedFrameOrigin.AsyncCurrent,
                        inner.Origin);
                    Assert.Contains(context.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent &&
                        ReferenceEquals(frame.Context, context.Segments[0]));
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void ProfilerEnvelope_PreservesCalleesAndGroupsOnlyVerifiedHelpers(bool physicalCurrent, bool enableNormalization)
        {
            StitchScenario scenario = CreateProfilerEnvelopeScenario(physicalCurrent);
            scenario.EnableNormalization = enableNormalization;
            scenario.VerifyThreadTimeComputer = true;
            scenario.IncludeEventSourceEvents = false;
            scenario.AllowSupplementalSamples = true;

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`1[Scenario+<DeepLevel3Async>d__3].ResolveMethodId()")]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`1[Scenario+<DeepLevel3Async>d__3].GetState(!!0&)")]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`1[Scenario+<DeepLevel3Async>d__3]..cctor()")]
        [InlineData("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[System.Int64,Scenario+<DeepLevel3Async>d__3]..ctor()")]
        [InlineData("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncProfilerAsyncStateMachineBox`1[System.Int64,Scenario+<DeepLevel3Async>d__3].ClearStateUponCompletion()")]
        public void ProfilerHelpers_ExtendTheVerifiedSpanButDoNotRestartAboveAnInitializationBreak(string method)
        {
            StitchScenario scenario = CreateProfilerHelperTailScenario();
            Frame helper = Frame.CoreLib(method, "ContiguousHelper");
            Frame initializer = Frame.CoreLib(
                "System.Runtime.CompilerServices.StaticsHelpers.GetNonGCStaticBaseSlow", "StaticInitialization");
            Frame callee = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`1[Scenario+<DeepLevel3Async>d__3].ResolveStateFieldOffset()",
                "DiagnosticsAboveBreak");
            Frame[] prefix = { callee, initializer, helper };
            scenario.Sync = prefix.Concat(scenario.Sync).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup =
                prefix.Concat(scenario.ExpectedWithoutInfrastructureCleanup).ToArray();
            scenario.ExpectedStitched = prefix.Take(2).Concat(scenario.ExpectedStitched).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed += 2;
            scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed += 2;

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void ProfilerHelpers_ASeparateDispatcherStartsANewSpanAfterAnApplicationBreak()
        {
            Frame inner = Frame.App("Scenario+<InnerAsync>d__3.MoveNext").Logical("Scenario.InnerAsync");
            Frame outer = Frame.App("Scenario+<OuterAsync>d__2.MoveNext").Logical("Scenario.OuterAsync");
            Frame innerHelper = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`1[Scenario+<InnerAsync>d__3].ResolveMethodId()",
                "InnerDiagnostics");
            Frame outerHelper = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`1[Scenario+<OuterAsync>d__2].ResolveMethodId()",
                "OuterDiagnostics");
            Frame callback = Frame.App("Scenario.InterContextCallback");
            var scenario = new StitchScenario
            {
                Sync = new[]
                {
                    innerHelper, Frame.V1MoveNextAsDispatcher, callback,
                    outerHelper, Frame.V1MoveNextAsDispatcher,
                    Frame.ThreadPoolDispatch, Frame.WorkerThreadStart,
                },
                AsyncSegments = new[]
                {
                    AsyncSegment.StateMachine(outer),
                    AsyncSegment.StateMachine(inner),
                },
                EnableInfrastructureCleanup = true,
                VerifySampleWeights = true,
                IncludeEventSourceEvents = false,
                ExpectedStitched = new[] { inner, callback, outer, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart },
                ExpectedWithoutInfrastructureCleanup = new[]
                {
                    innerHelper, Frame.V1MoveNextAsDispatcher, inner, callback,
                    outerHelper, Frame.V1MoveNextAsDispatcher, outer,
                    Frame.ThreadPoolDispatch, Frame.WorkerThreadStart,
                },
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 4,
                    V1InlineFallbackUsed = 4,
                    AsyncInfrastructureFramesCollapsed = 8,
                    ProfilerEnvelopeFramesCollapsed = 4,
                },
                AssertTransformContext = context =>
                {
                    AsyncProfilerStitchedFrame[] markers = context.Frames.Where(frame =>
                        frame.Presentation == AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames).ToArray();
                    Assert.Equal(2, markers.Length);
                    Assert.NotSame(markers[0].Context, markers[1].Context);
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(9)]
        [InlineData(10)]
        [InlineData(11)]
        [InlineData(12)]
        [InlineData(13)]
        public void ProfilerEnvelope_UnresolvedCallee_StopsHelperGrouping(int index)
        {
            StitchScenario scenario = CreateProfilerEnvelopeScenario();
            Frame[] logicalAndRoot = scenario.ExpectedStitched.Skip(5).ToArray();
            scenario.OmitSymbolFor = scenario.Sync.First(frame => frame.Key == scenario.Sync[index].Key);
            if (index == 10 || index == 12 || index == 13)
            {
                scenario.ExpectedStitched = scenario.Sync.Take(15).Concat(logicalAndRoot).ToArray();
                scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 10;
                scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = 0;
            }
            else
            {
                int retained = Math.Max(5, index + 1);
                scenario.ExpectedStitched = scenario.Sync.Take(retained).Concat(logicalAndRoot).ToArray();
                scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = (15 - retained) * 2;
                scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed =
                    10 + scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed;
            }

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        [InlineData(7)]
        [InlineData(8)]
        [InlineData(9)]
        [InlineData(10)]
        [InlineData(11)]
        [InlineData(12)]
        [InlineData(13)]
        public void ProfilerEnvelope_ForeignCallee_StopsHelperGrouping(int index)
        {
            StitchScenario scenario = CreateProfilerEnvelopeScenario();
            Frame[] logicalAndRoot = scenario.ExpectedStitched.Skip(5).ToArray();
            Frame original = scenario.Sync[index];
            Frame foreign = Frame.Library("OtherLibrary", original.SymbolName, original.Label);
            scenario.Sync[index] = foreign;
            scenario.ExpectedWithoutInfrastructureCleanup[index] = foreign;
            if (index == 13)
            {
                scenario.ExpectedStitched = scenario.Sync.Take(15).Concat(logicalAndRoot).ToArray();
                scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 10;
                scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = 0;
            }
            else
            {
                int retained = Math.Max(5, index + 1);
                scenario.ExpectedStitched = scenario.Sync.Take(retained).Concat(logicalAndRoot).ToArray();
                scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = (15 - retained) * 2;
                scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed =
                    10 + scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed;
            }

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("application")]
        [InlineData("native")]
        [InlineData("unknown-corelib")]
        [InlineData("missing-capture")]
        [InlineData("missing-emission")]
        [InlineData("missing-resume")]
        [InlineData("reordered")]
        public void ProfilerEnvelope_CalleeLayout_PreservesFramesOutsideTheVerifiedHelperSpan(string interruption)
        {
            StitchScenario scenario = CreateProfilerEnvelopeScenario();
            Frame[] logicalAndRoot = scenario.ExpectedStitched.Skip(5).ToArray();
            var prefix = scenario.Sync.Take(14).ToList();
            int retained = 5;
            switch (interruption)
            {
                case "application":
                    prefix.Insert(1, Frame.App("Scenario.UserInitializer"));
                    retained++;
                    break;
                case "native":
                    prefix.Insert(1, Frame.Library("NativeBridge", "Native.Initializer", "Native.Initializer"));
                    retained++;
                    break;
                case "unknown-corelib":
                    prefix.Insert(1, Frame.CoreLib("System.Runtime.CompilerServices.OtherHelpers.Initialize", "OtherHelpers.Initialize"));
                    retained++;
                    break;
                case "missing-capture":
                    prefix.RemoveAt(7);
                    break;
                case "missing-emission":
                    prefix.RemoveRange(8, 2);
                    break;
                case "missing-resume":
                    prefix.RemoveAll(frame => frame.Label == "Profiler.Resume");
                    break;
                case "reordered":
                    Frame capture = prefix[7];
                    prefix[7] = prefix[10];
                    prefix[10] = capture;
                    break;
            }
            scenario.Sync = prefix.Concat(scenario.Sync.Skip(14)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = prefix
                .Concat(scenario.ExpectedWithoutInfrastructureCleanup.Skip(14)).ToArray();
            if (interruption == "missing-resume")
            {
                scenario.ExpectedStitched = prefix.Concat(new[] { Frame.V1MoveNextAsDispatcher })
                    .Concat(logicalAndRoot).ToArray();
                scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 10;
                scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = 0;
            }
            else
            {
                scenario.ExpectedStitched = prefix.Take(retained).Concat(logicalAndRoot).ToArray();
                scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = (prefix.Count - retained + 1) * 2;
                scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed =
                    10 + scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed;
            }

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void ProfilerEnvelope_RejectedRootBridge_StillFoldsTheLeafEnvelope()
        {
            StitchScenario scenario = CreateProfilerEnvelopeScenario();
            Frame callback = Frame.App("Scenario.SynchronousCallback");
            scenario.Sync[scenario.Sync.Length - 3] = callback;
            scenario.ExpectedWithoutInfrastructureCleanup[scenario.ExpectedWithoutInfrastructureCleanup.Length - 3] = callback;
            scenario.ExpectedStitched = scenario.Sync.Take(5)
                .Concat(scenario.ExpectedWithoutInfrastructureCleanup.Skip(15)).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 20;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void ProfilerEnvelope_NoActiveSegment_IsVerbatim()
        {
            StitchScenario scenario = CreateProfilerEnvelopeScenario();
            scenario.AsyncStartQpc = 100_030;
            scenario.ExpectedStitched = scenario.Sync;
            scenario.ExpectedWithoutInfrastructureCleanup = scenario.Sync;
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation();

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void ProfilerEnvelope_MissingBoundary_RetainsTheUnverifiedEnvelope()
        {
            StitchScenario scenario = CreateProfilerEnvelopeScenario();
            scenario.Sync = scenario.Sync.Take(14).Concat(scenario.Sync.Skip(16)).ToArray();
            scenario.ExpectedStitched = scenario.Sync.Concat(scenario.Async.Skip(1)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = scenario.ExpectedStitched;
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation
            {
                SegmentsProcessed = 2,
                BoundariesNotFound = 2,
                V1InlineFallbackUsed = 2,
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void ProfilerEnvelope_PreservesParentContextContexts()
        {
            StitchScenario scenario = CreateProfilerEnvelopeScenario();
            Frame owner = Frame.App("Scenario.OwnerAsync");
            scenario.ContextParent = AsyncSegment.Runtime(owner);
            scenario.EnableParentContexts = true;
            scenario.ExpectedStitched = scenario.ExpectedStitched.Take(8)
                .Concat(new[] { owner }).Concat(scenario.ExpectedStitched.Skip(8)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup =
                scenario.ExpectedWithoutInfrastructureCleanup.Take(18)
                .Concat(new[] { owner }).Concat(scenario.ExpectedWithoutInfrastructureCleanup.Skip(18)).ToArray();

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ProfilerEnvelope_CalledFromRuntimeAsync_PreservesThePhysicalCurrentMethod(bool unresolvedCallee)
        {
            StitchScenario scenario = CreateProfilerEnvelopeScenario();
            Frame current = Frame.App("Scenario.RuntimeAsync");
            Frame parent = Frame.App("Scenario.RuntimeParentAsync");
            Frame[] prefix = scenario.Sync.Take(14).ToArray();
            Frame[] tail = scenario.Sync.Skip(scenario.Sync.Length - 7).ToArray();
            scenario.Kind = AsyncCallstackKind.RuntimeAsync;
            scenario.Async = new[] { current, parent };
            scenario.AsyncStates = null;
            scenario.Sync = prefix.Concat(new[]
            {
                current, Frame.V2Wrapper(0), Frame.V2InstrumentedDispatch, Frame.V2Dispatch,
            }).Concat(tail).ToArray();
            if (unresolvedCallee)
            {
                scenario.OmitSymbolFor = prefix[2];
            }
            scenario.ExpectedStitched = prefix.Take(5)
                .Concat(new[] { current, parent }).Concat(tail.Skip(5)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = prefix.Concat(new[] { current, parent }).Concat(tail).ToArray();
            scenario.ExpectedDiagnostics.V1InlineFallbackUsed = 0;
            scenario.ExpectedDiagnostics.V1InfrastructureFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.V2PlumbingFramesCollapsed = 4;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 28;
            scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = 18;

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, false, false)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, false, true)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, true, false)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, true, true)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, false, false)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, false, true)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, true, false)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, true, true)]
        public void RuntimeResumptionBridge_ApplicationCallback_GroupsOnlyTheCompletionPrefix(
            int kindValue, bool enableNormalization, bool enableParentContexts)
        {
            var kind = (AsyncCallstackKind)kindValue;
            StitchScenario scenario = CreateCallbackCompletionBridgeScenario(kind);
            scenario.ExpectedStitched = scenario.ExpectedStitched.Take(3)
                .Concat(scenario.ExpectedStitched.Skip(6)).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 6;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 6;
            Frame prefix = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start", "AsyncMethodBuilderCore.Start");
            scenario.Sync = new[] { prefix }.Concat(scenario.Sync).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup =
                new[] { prefix }.Concat(scenario.ExpectedWithoutInfrastructureCleanup).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed += 2;
            scenario.EnableNormalization = enableNormalization;
            scenario.EnableParentContexts = enableParentContexts;
            if (enableParentContexts)
            {
                Frame owner = Frame.App("Scenario.OwnerAsync");
                scenario.ContextParent = AsyncSegment.Runtime(owner);
                scenario.ExpectedStitched = scenario.ExpectedStitched.Take(3).Concat(new[] { owner })
                    .Concat(scenario.ExpectedStitched.Skip(3)).ToArray();
                scenario.ExpectedWithoutInfrastructureCleanup =
                    scenario.ExpectedWithoutInfrastructureCleanup.Take(4).Concat(new[] { owner })
                        .Concat(scenario.ExpectedWithoutInfrastructureCleanup.Skip(4)).ToArray();
            }
            scenario.AssertTransformContext = context =>
            {
                AsyncProfilerStitchedFrame current = Assert.Single(context.Frames.Where(frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent));
                Assert.Same(context.Segments[0], current.Context);
                Assert.Equal(0, current.SegmentFrameIndex);
                Assert.Equal(0, current.Segment.FrameStateAt(0));
                Assert.Contains(context.Frames, frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                    context.TraceLog.CodeAddresses[frame.CodeAddress].FullMethodName == "Scenario.SynchronousCallback");
                Assert.Contains(context.Frames, frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                    context.TraceLog.CodeAddresses[frame.CodeAddress].FullMethodName == "System.Threading.TimerQueueTimer.Fire");
                AsyncProfilerStitchedFrame marker = Assert.Single(context.Frames.Where(frame =>
                    frame.Context == null &&
                    frame.Presentation == AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames));
                Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, marker.Origin);
                Assert.Equal(AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations, marker.SyncFrameKind);
                Assert.Equal("System.Threading.Tasks.Task.RunContinuations",
                    context.TraceLog.CodeAddresses[marker.CodeAddress].FullMethodName);
                Assert.True(context.Diagnostics.RuntimeResumptionBridgeFramesCollapsed > 0);
                Assert.Equal(0, context.Diagnostics.AsyncReentrantHandoffFramesCollapsed);
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, false)]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, true)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, false)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, true)]
        public void RuntimeResumptionBridge_ApplicationCallback_AwaiterPrefixIsMachineryOnly(
            int kindValue, bool cleanup)
        {
            StitchScenario scenario = CreateCallbackCompletionBridgeScenario((AsyncCallstackKind)kindValue, includeAwaiterHelpers: true);
            scenario.EnableInfrastructureCleanup = cleanup;
            scenario.ExpectedWithoutNormalization = scenario.ExpectedWithoutInfrastructureCleanup;
            if (cleanup)
            {
                scenario.ExpectedStitched = scenario.ExpectedStitched.Take(3)
                    .Concat(scenario.ExpectedStitched.Skip(8)).ToArray();
                scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 10;
                scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 10;
            }
            scenario.AssertTransformContext = context =>
            {
                AsyncProfilerStitchedFrame[] markers = context.Frames.Where(frame =>
                    frame.Context == null &&
                    frame.Presentation == AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames).ToArray();
                Assert.Equal(cleanup ? 1 : 0, markers.Length);
                if (cleanup)
                {
                    Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, markers[0].Origin);
                    Assert.Equal(AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAwaiterInfrastructure, markers[0].SyncFrameKind);
                    Assert.Contains("TaskAwaiter+<>c", context.TraceLog.CodeAddresses[markers[0].CodeAddress].FullMethodName);
                    int markerIndex = context.Frames.IndexOf(markers[0]);
                    Assert.Equal("Scenario.SynchronousCallback",
                        context.TraceLog.CodeAddresses[context.Frames[markerIndex + 1].CodeAddress].FullMethodName);
                }
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "unresolved-awaiter")]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "foreign-awaiter")]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "other-runtime")]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "unknown-helper")]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "interrupted")]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "unresolved-anchor")]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "foreign-anchor")]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "profile-veto")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "unresolved-awaiter")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "foreign-awaiter")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "other-runtime")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "unknown-helper")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "interrupted")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "unresolved-anchor")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "foreign-anchor")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "profile-veto")]
        public void RuntimeResumptionBridge_ApplicationCallback_UnverifiedAwaiterPrefixRemainsPhysical(
            int kindValue, string obstruction)
        {
            StitchScenario scenario = CreateCallbackCompletionBridgeScenario((AsyncCallstackKind)kindValue, includeAwaiterHelpers: true);
            int prefixStart = scenario.Sync.Length - 9;
            if (obstruction == "unresolved-awaiter" || obstruction == "unresolved-anchor")
            {
                scenario.OmitSymbolFor = obstruction == "unresolved-awaiter"
                    ? scenario.Sync[prefixStart] : Frame.ThreadPoolDispatch;
            }
            else if (obstruction == "profile-veto")
            {
                var profile = new AsyncProfilerRejectRuntimeBridgeProfile();
                scenario.RuntimeProfileForModule = module => AsyncProfilerRuntimeRuleProfileCache.IsHostModule(module)
                    ? profile : AsyncProfilerRuntimeRuleProfile.Common;
            }
            else
            {
                int index = obstruction == "foreign-anchor" ? scenario.Sync.Length - 2 : prefixStart;
                Frame original = scenario.Sync[index];
                Frame replacement = obstruction == "unknown-helper"
                    ? Frame.CoreLib("System.Runtime.CompilerServices.UnknownAwaiter.Invoke", "UnknownAwaiter")
                    : obstruction == "interrupted"
                        ? Frame.App("Scenario.InterruptedCompletion")
                        : Frame.Library(obstruction == "other-runtime" ? @"C:\other\System.Private.CoreLib.dll" : "OtherLibrary",
                            original.SymbolName, original.Label + " (foreign)");
                scenario.Sync[index] = replacement;
                scenario.ExpectedWithoutInfrastructureCleanup = scenario.ExpectedWithoutInfrastructureCleanup
                    .Select(frame => frame.Key == original.Key ? replacement : frame).ToArray();
            }
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync)]
        public void RuntimeResumptionBridge_ApplicationCallback_UnresolvedHostDoesNotBlockVerifiedPrefix(int kindValue)
        {
            StitchScenario scenario = CreateCallbackCompletionBridgeScenario((AsyncCallstackKind)kindValue);
            scenario.OmitSymbolFor = scenario.Sync[scenario.Sync.Length - 3];
            scenario.ExpectedStitched = scenario.ExpectedStitched.Take(3)
                .Concat(scenario.ExpectedStitched.Skip(6)).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 6;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 6;
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RuntimeResumptionBridge_ApplicationCallback_PreservesPendingV1BoxTarget(bool cleanup)
        {
            StitchScenario scenario = CreateCallbackCompletionBridgeScenario(
                AsyncCallstackKind.StateMachineAsync, includeAwaiterHelpers: true);
            scenario.Sync[1] = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[System.Int64,Scenario+<Level2Async>d__2].MoveNext(class System.Threading.Thread,value class Flags)",
                "PendingBox");
            scenario.EnableInfrastructureCleanup = cleanup;
            scenario.AssertStitchedStackSource = null;
            if (cleanup)
            {
                scenario.ExpectedStitched = scenario.ExpectedStitched.Take(3)
                    .Concat(scenario.ExpectedStitched.Skip(8)).ToArray();
                scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 10;
                scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 10;
            }
            scenario.AssertTransformContext = context =>
            {
                AsyncProfilerStitchedFrame target = Assert.Single(context.Frames.Where(frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget));
                Assert.True(target.IsStateMachineBoxCarrier);
                Assert.Equal(0, target.SegmentFrameIndex);
                Assert.Equal(0, target.Segment.FrameStateAt(0));
                Assert.DoesNotContain(context.Frames, frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "unresolved-0")]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "unresolved-1")]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "unresolved-2")]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "foreign-0")]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "foreign-1")]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "foreign-2")]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "interrupted")]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "not-root-adjacent")]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "incomplete")]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync, "missing-dispatch")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "unresolved-0")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "unresolved-1")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "unresolved-2")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "foreign-0")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "foreign-1")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "foreign-2")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "interrupted")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "not-root-adjacent")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "incomplete")]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync, "missing-dispatch")]
        public void RuntimeResumptionBridge_ApplicationCallback_PreservesUnverifiedCompletionChain(
            int kindValue, string obstruction)
        {
            var kind = (AsyncCallstackKind)kindValue;
            StitchScenario scenario = CreateCallbackCompletionBridgeScenario(kind);
            int tailStart = scenario.Sync.Length - 7;
            if (obstruction.StartsWith("unresolved-", StringComparison.Ordinal))
            {
                scenario.OmitSymbolFor = scenario.Sync[tailStart + int.Parse(obstruction.Substring(11))];
            }
            else if (obstruction.StartsWith("foreign-", StringComparison.Ordinal))
            {
                int index = int.Parse(obstruction.Substring(8));
                Frame original = scenario.Sync[tailStart + index];
                Frame foreign = Frame.Library("OtherLibrary", original.SymbolName, original.Label + " (foreign)");
                scenario.Sync[tailStart + index] = foreign;
                scenario.ExpectedWithoutInfrastructureCleanup[3 + index] = foreign;
            }
            else if (obstruction == "interrupted" || obstruction == "not-root-adjacent")
            {
                Frame callback = Frame.App("Scenario.InterruptedCompletion");
                int offset = obstruction == "interrupted" ? 1 : 0;
                scenario.Sync = scenario.Sync.Take(tailStart + offset).Concat(new[] { callback })
                    .Concat(scenario.Sync.Skip(tailStart + offset)).ToArray();
                scenario.ExpectedWithoutInfrastructureCleanup =
                    scenario.ExpectedWithoutInfrastructureCleanup.Take(3 + offset).Concat(new[] { callback })
                        .Concat(scenario.ExpectedWithoutInfrastructureCleanup.Skip(3 + offset)).ToArray();
            }
            else if (obstruction == "incomplete")
            {
                Frame omitted = scenario.Sync[tailStart + 2];
                scenario.Sync = scenario.Sync.Where(frame => frame != omitted).ToArray();
                scenario.ExpectedWithoutInfrastructureCleanup =
                    scenario.ExpectedWithoutInfrastructureCleanup.Where(frame => frame != omitted).ToArray();
            }
            else
            {
                Assert.Equal("missing-dispatch", obstruction);
                scenario.Sync = scenario.Sync.Where(frame => frame != Frame.ThreadPoolDispatch).ToArray();
                scenario.ExpectedWithoutInfrastructureCleanup = scenario.ExpectedWithoutInfrastructureCleanup
                    .Where(frame => frame != Frame.ThreadPoolDispatch).ToArray();
            }
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync)]
        public void RuntimeResumptionBridge_ApplicationCallback_PreservesTheChainAndUnresolvedOuterWork(
            int kindValue)
        {
            var kind = (AsyncCallstackKind)kindValue;
            StitchScenario scenario = CreateCallbackCompletionBridgeScenario(kind);
            scenario.OmitSymbolFor = scenario.Sync[scenario.Sync.Length - 4];
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("completing")]
        [InlineData("ambiguous")]
        public void RuntimeResumptionBridge_ApplicationCallback_PreservesUnverifiedV1Anchor(string obstruction)
        {
            StitchScenario scenario = CreateCallbackCompletionBridgeScenario(AsyncCallstackKind.StateMachineAsync);
            scenario.EnableInfrastructureCleanup = false;
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
            scenario.AssertTransformContext = context =>
            {
                var frames = new List<AsyncProfilerStitchedFrame>(context.Frames);
                var diagnostics = new AsyncProfilerCallStackDiagnostics { SegmentsProcessed = context.Segments.Count };
                if (obstruction == "completing")
                {
                    int index = frames.FindIndex(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
                    AsyncProfilerStitchedFrame current = frames[index];
                    frames[index] = AsyncProfilerStitchedFrame.CreateAsyncCompleting(
                        current.CodeAddress, current.Method, current.Context, current.SegmentFrameIndex);
                }
                else
                {
                    diagnostics.V1CompletionAlignmentAmbiguous = 1;
                }
                AsyncProfilerStitchedFrame[] expected = frames.ToArray();
                var pipeline = new AsyncProfilerCallStackTransformPipeline
                {
                    EnableNormalization = false,
                    EnableInfrastructureCleanup = true,
                };
                AsyncProfilerStitchResult result = pipeline.Transform(new AsyncProfilerStitchResult(frames, diagnostics), context.Segments,
                    context.TimestampQpc, context.ClassifyBoundary, context.TraceLog);
                Assert.Equal(expected, result.Frames);
                Assert.Equal(0, diagnostics.AsyncInfrastructureFramesCollapsed);
                Assert.Equal(0, diagnostics.RuntimeResumptionBridgeFramesCollapsed);
            };
            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync)]
        public void RuntimeResumptionBridge_RejectedTail_DoesNotBlockLeafwardHelperCleanup(int kindValue)
        {
            var kind = (AsyncCallstackKind)kindValue;
            StitchScenario scenario = CreateRuntimeResumptionBridgeScenario(kind);
            Frame[] prefix =
            {
                Frame.CoreLib("System.Threading.Tasks.Task.RunContinuations", "Task.RunContinuations"),
                Frame.CoreLib("System.Threading.Tasks.Task.TrySetResult", "Task.TrySetResult"),
                Frame.CoreLib("System.Threading.Tasks.TaskCompletionSource.SetResult", "TaskCompletionSource.SetResult"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start", "AsyncMethodBuilderCore.Start"),
            };
            int timerIndex = scenario.Sync.Length - 3;
            Frame callback = Frame.App("Scenario.SynchronousCallback");
            scenario.Sync[timerIndex] = callback;
            scenario.Sync = prefix.Concat(scenario.Sync).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup[7] = callback;
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
            scenario.ExpectedWithoutInfrastructureCleanup =
                prefix.Concat(scenario.ExpectedWithoutInfrastructureCleanup).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 8;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void RuntimeAsync_TaskContinuationResume_DispatchOnlyPreservesCalleesAndRejectedBridge(
            bool helperIsLeaf, bool enableInfrastructureCleanup)
        {
            Frame resume = Frame.CoreLib(
                "System.Runtime.CompilerServices.RuntimeAsyncTaskContinuation+TaskContinuationResume." +
                "ResumeTaskContinuation(class System.Runtime.CompilerServices.Continuation,unsigned int8&)",
                "ResumeTaskContinuation");
            Frame current = Frame.App("Scenario.ChildAsync");
            Frame parent = Frame.App("Scenario.ParentAsync");
            Frame owner = Frame.App("Scenario+<OwnerAsync>d__1.MoveNext").Logical("Scenario.OwnerAsync", 0);
            Frame[] callees = helperIsLeaf ? Array.Empty<Frame>() : new[] { Frame.App("Scenario.HelperCallee") };
            Frame[] tail =
            {
                Frame.CoreLib("System.Threading.Tasks.Task.RunContinuations", "Task.RunContinuations"),
                Frame.CoreLib("System.Threading.Tasks.Task`1[System.Boolean].TrySetResult(!0)", "Task.TrySetResult"),
                Frame.CoreLib("System.Threading.Tasks.TaskCompletionSource`1[System.Boolean].SetResult(!0)", "TaskCompletionSource.SetResult"),
                Frame.App("Scenario.CompleteOnNewThread"),
                Frame.CoreLib("System.Threading.Thread.StartCallback", "Thread.StartCallback"),
            };
            var scenario = new StitchScenario
            {
                Sync = callees.Concat(new[]
                {
                    resume, Frame.V2Wrapper(0), Frame.V2InstrumentedDispatch, Frame.V2Dispatch,
                }).Concat(tail).ToArray(),
                Async = new[] { Frame.Zero, current, parent },
                ContextParent = AsyncSegment.StateMachine(owner),
                EnableParentContexts = true,
                EnableInfrastructureCleanup = enableInfrastructureCleanup,
                IncludeEventSourceEvents = false,
                VerifySampleWeights = true,
                VerifyThreadTimeComputer = true,
                ExpectedStitched = callees.Concat(new[] { resume.DispatcherFrames() }).Concat(tail).ToArray(),
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V2PlumbingFramesCollapsed = 4,
                },
                AssertTransformContext = context =>
                {
                    Assert.All(context.Frames, frame =>
                        Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, frame.Origin));
                    Assert.Single(context.Frames.Where(frame =>
                        frame.Presentation == AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames));
                },
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void RuntimeResumptionBridge_V2WithoutPhysicalBody_FoldsOnlyVerifiedDispatchTarget(
            bool leafWrapper, bool targetKnown)
        {
            StitchScenario scenario = CreateRuntimeResumptionBridgeScenario(AsyncCallstackKind.RuntimeAsync);
            if (!targetKnown)
            {
                scenario.Async[0] = Frame.Zero;
            }
            scenario.Sync = leafWrapper
                ? scenario.Sync.Skip(2).ToArray()
                : scenario.Sync.Where(frame => frame.Kind != FrameKind.V2Wrapper).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup =
                leafWrapper ? (targetKnown ? scenario.Async.Concat(scenario.Sync.Skip(3))
                    : scenario.Sync.Skip(1)).ToArray() : scenario.Sync;
            scenario.ExpectedStitched = targetKnown
                ? scenario.Async.Concat(scenario.Sync.Skip(scenario.Sync.Length - 2)).ToArray()
                : (leafWrapper ? Enumerable.Empty<Frame>() : scenario.Sync.Take(2))
                    .Concat(new[] { Frame.V2InstrumentedDispatch.DispatcherFrames() })
                    .Concat(scenario.Sync.Skip(scenario.Sync.Length - 7)).ToArray();
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation
            {
                SegmentsProcessed = targetKnown ? 2 : 0,
                V2PlumbingFramesCollapsed = targetKnown ? 4 : 0,
                V2SyncLayoutUsed = targetKnown ? 0 : 2,
                V2LeafWrapperDropped = leafWrapper ? 2 : 0,
                AsyncInfrastructureFramesCollapsed = targetKnown ? 10 : 4,
                RuntimeResumptionBridgeFramesCollapsed = targetKnown ? 10 : 0,
            };
            scenario.AssertTransformContext = context =>
            {
                Assert.Equal(targetKnown ? 1 : 0,
                    context.Frames.Count(frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget));
                Assert.DoesNotContain(context.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false, true, true)]
        [InlineData(false, true, true, true)]
        [InlineData(true, false, true, true)]
        [InlineData(true, true, true, true)]
        [InlineData(false, false, false, false)]
        [InlineData(true, false, false, false)]
        [InlineData(false, true, true, false)]
        [InlineData(true, true, true, false)]
        [InlineData(false, false, false, true)]
        [InlineData(true, false, false, true)]
        public void RuntimeResumptionBridge_V2NativePrefix_PreservesWorkAndProjectsOnlyADispatchTarget(
            bool resumeStub, bool methodEvents, bool enableNormalization, bool enableInfrastructureCleanup)
        {
            StitchScenario scenario = CreateNativeRuntimeDispatchScenario(resumeStub, methodEvents);
            scenario.EnableNormalization = enableNormalization;
            scenario.EnableInfrastructureCleanup = enableInfrastructureCleanup;
            if (!enableInfrastructureCleanup)
            {
                scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
                scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
                scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;
            }
            scenario.OmitSymbolFor = scenario.Sync[0];
            scenario.AssertTransformContext = context =>
            {
                AsyncProfilerStitchedFrame target = Assert.Single(context.Frames.Where(frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget));
                Assert.Equal(1, target.SegmentFrameIndex);
                Assert.Same(context.Segments[0], target.Context);
                Assert.Equal(context.Segments[0].Frames.CodeAddressAt(1), target.CodeAddress);
                Assert.DoesNotContain(context.Frames, frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
                Assert.All(context.Frames.Take(2), frame =>
                    Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, frame.Origin));
                Assert.Equal(MethodIndex.Invalid, context.Frames[0].Method);
                Assert.NotEqual(MethodIndex.Invalid, context.Frames[1].Method);
                AsyncProfilerStitchedFrame marker = context.Frames[2];
                Assert.Equal(AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames, marker.Presentation);
                Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, marker.Origin);
                Assert.Same(target.Context, marker.Context);
                Assert.Equal(resumeStub ? "dynamicClass.IL_STUB_AsyncResume" : Frame.V2Wrapper(1).SymbolName,
                    context.TraceLog.CodeAddresses[marker.CodeAddress].FullMethodName);
                Assert.Equal(3, context.Frames.IndexOf(target));
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void RuntimeResumptionBridge_V2NativeEntry_PreservesArbitraryCallees(
            bool resumeStub, bool unresolvedEntry)
        {
            StitchScenario scenario = CreateNativeRuntimeDispatchScenario(resumeStub, false);
            Frame entry = scenario.Sync[1];
            Frame[] callees =
            {
                Frame.Library("ntdll", "RtlAllocateHeap", "OS work"),
                Frame.App("Scenario.LoaderCallback"),
                Frame.Library("thirdparty", "ThirdParty.NativeHelper", "Native callback work"),
            };
            scenario.Sync = callees.Concat(scenario.Sync).ToArray();
            scenario.ExpectedStitched = callees.Concat(scenario.ExpectedStitched).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup =
                callees.Concat(scenario.ExpectedWithoutInfrastructureCleanup).ToArray();
            scenario.OmitSymbolFor = unresolvedEntry ? entry : callees[0];
            scenario.AssertTransformContext = context =>
            {
                AsyncProfilerStitchedFrame target = Assert.Single(context.Frames.Where(frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget));
                Assert.Equal(1, target.SegmentFrameIndex);
                Assert.DoesNotContain(context.Frames, frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
                Assert.All(context.Frames.Take(5), frame =>
                    Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, frame.Origin));
                Assert.Equal(MethodIndex.Invalid, context.Frames[unresolvedEntry ? 4 : 0].Method);
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("physical-target")]
        [InlineData("v1-dispatcher")]
        [InlineData("v2-dispatch")]
        [InlineData("extra-stub")]
        public void RuntimeResumptionBridge_V2NativeEntry_RejectsConflictingCalleeEvidence(string obstruction)
        {
            StitchScenario scenario = CreateNativeRuntimeDispatchScenario(false, false);
            Frame conflict = obstruction == "physical-target" ? scenario.Async[1] :
                obstruction == "v1-dispatcher" ? Frame.V1MoveNextAsDispatcher :
                obstruction == "v2-dispatch" ? Frame.V2InstrumentedDispatch :
                Frame.App("dynamicClass.IL_STUB_AsyncResume");
            scenario.Sync = new[] { conflict }.Concat(scenario.Sync).ToArray();
            scenario.EnableNormalization = false;
            scenario.EnableInfrastructureCleanup = false;
            scenario.ExpectedStitched = scenario.Sync.Take(3).Concat(scenario.Async.Skip(2))
                .Concat(scenario.Sync.Skip(6)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = scenario.ExpectedStitched;
            scenario.ExpectedDiagnostics.AdjacencyMismatches = 2;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;
            scenario.AssertTransformContext = context =>
            {
                Assert.DoesNotContain(context.Frames, frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget ||
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void RuntimeResumptionBridge_V2NativePrefix_ProjectsInnerTargetWithoutReplacingTheOuterBody()
        {
            StitchScenario scenario = CreateNativeRuntimeDispatchScenario(false, false);
            Frame outer = Frame.App("Scenario.OuterAsync");
            Frame outerParent = Frame.App("Scenario.OuterParentAsync");
            scenario.AsyncSegments = new[]
            {
                AsyncSegment.Runtime(outer, outerParent),
                AsyncSegment.Runtime(scenario.Async),
            };
            scenario.Sync = scenario.Sync.Take(5).Concat(new[]
            {
                outer, Frame.V2Wrapper(0), Frame.V2InstrumentedDispatch, Frame.V2Dispatch,
            }).Concat(scenario.Sync.Skip(5)).ToArray();
            Frame[] logical = scenario.Sync.Take(2).Concat(new[] { Frame.V2Wrapper(1).DispatcherFrames() }).Concat(scenario.Async.Skip(1))
                .Concat(new[] { outer, outerParent }).ToArray();
            scenario.ExpectedStitched = logical.Concat(scenario.Sync.Skip(scenario.Sync.Length - 2)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = logical.Concat(scenario.Sync.Skip(9)).ToArray();
            scenario.ExpectedDiagnostics.SegmentsProcessed = 4;
            scenario.ExpectedDiagnostics.V2PlumbingFramesCollapsed = 8;
            scenario.AssertTransformContext = context =>
            {
                Assert.Same(context.Segments[1], Assert.Single(context.Frames.Where(frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget)).Context);
                Assert.Same(context.Segments[0], Assert.Single(context.Frames.Where(frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent)).Context);
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("foreign-runtime")]
        [InlineData("managed-image")]
        [InlineData("missing-image")]
        [InlineData("application-callback")]
        [InlineData("unresolved-target")]
        [InlineData("completed")]
        [InlineData("before-base")]
        public void RuntimeResumptionBridge_V2NativePrefix_UnverifiedEvidenceDoesNotProjectATarget(string obstruction)
        {
            StitchScenario scenario = CreateNativeRuntimeDispatchScenario(false, false);
            Action<TraceLog> configure = scenario.ConfigureTraceLog;
            scenario.ConfigureTraceLog = log =>
            {
                configure(log);
                TraceModuleFile native = log.ModuleFiles.First(module => module.Name == "coreclr");
                if (obstruction == "foreign-runtime")
                {
                    native.fileName = @"C:\other-runtime\coreclr.dll";
                }
                else if (obstruction == "managed-image")
                {
                    native.managedModule = log.ModuleFiles.First(module => string.Equals(
                        module.Name, AsyncProfilerCallStackStitchBoundary.HostModuleName, StringComparison.OrdinalIgnoreCase));
                }
                else if (obstruction == "missing-image")
                {
                    native.imageBase = 0;
                }
            };
            if (obstruction == "application-callback")
            {
                scenario.Sync = scenario.Sync.Take(2).Concat(new[] { Frame.App("Scenario.UnrelatedCallback") })
                    .Concat(scenario.Sync.Skip(2)).ToArray();
            }
            else if (obstruction == "unresolved-target")
            {
                scenario.OmitSymbolFor = scenario.Async[1];
            }
            else if (obstruction == "completed")
            {
                scenario.MethodCompletions = scenario.Async.Length;
            }
            else if (obstruction == "before-base")
            {
                scenario.ContinuationIndexBase = 2;
            }
            int wrapper = Array.FindIndex(scenario.Sync, frame => frame.Kind == FrameKind.V2Wrapper);
            int completed = obstruction == "completed" ? scenario.Async.Length - 1 :
                obstruction == "before-base" ? 0 : 1;
            scenario.ExpectedStitched = scenario.Sync.Take(wrapper).Concat(scenario.Async.Skip(completed + 1))
                .Concat(scenario.Sync.Skip(wrapper + 3)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = scenario.ExpectedStitched;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.AdjacencyMismatches =
                obstruction == "unresolved-target" || obstruction == "before-base" ? 0 : 2;
            scenario.AssertTransformContext = context =>
            {
                Assert.DoesNotContain(context.Frames, frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget ||
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, false, false)]
        [InlineData(true, true, false)]
        [InlineData(false, false, true)]
        [InlineData(false, true, true)]
        [InlineData(true, false, true)]
        [InlineData(true, true, true)]
        public void RuntimeResumptionBridge_YieldFamily_FoldsOnlyTheBoundedMachinerySpan(
            bool runtimeAsync, bool multiple, bool enableNormalization)
        {
            Frame[] yieldFrames = multiple ? new[]
            {
                Frame.CoreLib("System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter+<>c::<DifferentCallback>b__91_2(System.Action)",
                    "Yield.GeneratedCallback"),
                Frame.CoreLib("System.Void System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter+Helper`1[System.Int64]::Invoke()",
                    "Yield.NestedHelper"),
                Frame.CoreLib("System.Runtime.CompilerServices.YieldAwaitable.GetAwaiter()", "Yield.GetAwaiter"),
            } : new[]
            {
                Frame.CoreLib("System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter+<>c::<OutputCorrelationEtwEvent>b__6_0(System.Action, System.Threading.Tasks.Task)",
                    "Yield.Correlation"),
            };
            StitchScenario scenario = CreateYieldRuntimeBridgeScenario(runtimeAsync
                ? AsyncCallstackKind.RuntimeAsync : AsyncCallstackKind.StateMachineAsync, yieldFrames);
            scenario.EnableNormalization = enableNormalization;
            Frame leafSideYield = Frame.CoreLib(
                "System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter.GetResult()", "Yield.ApplicationWork");
            scenario.Sync = new[] { leafSideYield }.Concat(scenario.Sync).ToArray();
            scenario.ExpectedStitched = new[] { leafSideYield }.Concat(scenario.ExpectedStitched).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup =
                new[] { leafSideYield }.Concat(scenario.ExpectedWithoutInfrastructureCleanup).ToArray();
            scenario.ExpectedWithoutNormalization = scenario.ExpectedWithoutInfrastructureCleanup;

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("missing-instrumented", true)]
        [InlineData("reversed-pair", true)]
        [InlineData("duplicate-pair", true)]
        [InlineData("foreign-module", true)]
        [InlineData("other-corelib", true)]
        [InlineData("whole-bridge-other-runtime", true)]
        [InlineData("lookalike", true)]
        [InlineData("namespace-lookalike", true)]
        [InlineData("interrupted", true)]
        [InlineData("unresolved-yield", true)]
        [InlineData("unresolved-anchor", true)]
        [InlineData("unverified-current", true)]
        [InlineData("foreign-module", false)]
        [InlineData("other-corelib", false)]
        [InlineData("whole-bridge-other-runtime", false)]
        [InlineData("lookalike", false)]
        [InlineData("namespace-lookalike", false)]
        [InlineData("interrupted", false)]
        [InlineData("unresolved-yield", false)]
        [InlineData("unresolved-anchor", false)]
        public void RuntimeResumptionBridge_YieldFamily_UnverifiedSpansRemainIntact(string obstruction, bool runtimeAsync)
        {
            Frame yield = Frame.CoreLib(
                "System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter+<>c.Callback()", "Yield.Callback");
            Frame[] span = obstruction == "interrupted" ? new[]
            {
                yield, Frame.App("Scenario.UnrelatedCallback"),
                Frame.CoreLib(
                    "System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter.System.Runtime.CompilerServices.IStateMachineBoxAwareAwaiter.AwaitUnsafeOnCompleted(class System.Runtime.CompilerServices.IAsyncStateMachineBox)",
                    "Yield.Registration"),
            } : new[]
            {
                obstruction == "foreign-module" ? Frame.Library("Other.CoreLib", yield.SymbolName, "Foreign.Yield") :
                obstruction == "other-corelib" ? Frame.Library(
                    @"C:\other\System.Private.CoreLib.dll", yield.SymbolName, "OtherCoreLib.Yield") :
                obstruction == "lookalike" ? Frame.CoreLib(
                    "System.Runtime.CompilerServices.YieldAwaitableLookalike+YieldAwaiter.Callback()", "Lookalike.Yield") :
                obstruction == "namespace-lookalike" ? Frame.CoreLib(
                    "System.Runtime.CompilerServices.YieldAwaitable.OtherType.Callback()", "NamespaceLookalike.Yield") : yield,
            };
            StitchScenario scenario = CreateYieldRuntimeBridgeScenario(runtimeAsync
                ? AsyncCallstackKind.RuntimeAsync : AsyncCallstackKind.StateMachineAsync, span);
            if (obstruction == "missing-instrumented")
            {
                scenario.Sync = scenario.Sync.Where(frame => frame != Frame.V2InstrumentedDispatch).ToArray();
                scenario.ExpectedDiagnostics.V2PlumbingFramesCollapsed = 2;
            }
            else if (obstruction == "reversed-pair")
            {
                scenario.Sync[3] = Frame.V2Dispatch;
                scenario.Sync[4] = Frame.V2InstrumentedDispatch;
            }
            else if (obstruction == "duplicate-pair")
            {
                scenario.Sync[4] = Frame.V2InstrumentedDispatch;
            }
            else if (obstruction == "unresolved-yield")
            {
                scenario.OmitSymbolFor = yield;
            }
            else if (obstruction == "unresolved-anchor")
            {
                scenario.OmitSymbolFor = scenario.Sync[scenario.Sync.Length - 2];
            }
            else if (obstruction == "unverified-current")
            {
                Frame other = Frame.App("Scenario.OtherAsync");
                scenario.Sync[1] = other;
                scenario.ExpectedWithoutInfrastructureCleanup[1] = other;
                scenario.ExpectedDiagnostics.AdjacencyMismatches = 2;
            }
            else if (obstruction == "whole-bridge-other-runtime")
            {
                Frame Replace(Frame frame) => frame == yield || frame == Frame.ThreadPoolDispatch
                    ? Frame.Library(@"C:\other\System.Private.CoreLib.dll", frame.SymbolName, "OtherRuntime." + frame.Label)
                    : frame;
                scenario.Sync = scenario.Sync.Select(Replace).ToArray();
                scenario.ExpectedWithoutInfrastructureCleanup = scenario.ExpectedWithoutInfrastructureCleanup.Select(Replace).ToArray();
            }
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
            scenario.ExpectedWithoutNormalization = scenario.ExpectedStitched;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void RuntimeResumptionBridge_YieldFamily_WithoutStructuralPairEvidenceRemainsIntact(
            bool traceContext, bool runtimeAsync)
        {
            StitchScenario scenario = CreateYieldRuntimeBridgeScenario(runtimeAsync
                ? AsyncCallstackKind.RuntimeAsync : AsyncCallstackKind.StateMachineAsync, Frame.CoreLib(
                "System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter.Callback()", "Yield.Callback"));
            scenario.EnableInfrastructureCleanup = false;
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;
            scenario.AssertTransformContext = context =>
            {
                var diagnostics = new AsyncProfilerCallStackDiagnostics { SegmentsProcessed = context.Segments.Count };
                var pipeline = new AsyncProfilerCallStackTransformPipeline
                {
                    EnableNormalization = false,
                    EnableInfrastructureCleanup = true,
                };
                AsyncProfilerStitchResult result = pipeline.Transform(
                    new AsyncProfilerStitchResult(context.Frames.ToArray(), diagnostics),
                    context.Segments, context.TimestampQpc, context.ClassifyBoundary,
                    traceContext ? context.TraceLog : null);
                Assert.Equal(context.Frames, result.Frames);
                Assert.Equal(0, diagnostics.RuntimeResumptionBridgeFramesCollapsed);
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RuntimeResumptionBridge_V1YieldFamily_UnverifiedTargetRetainsTheBridge(bool enableNormalization)
        {
            StitchScenario scenario = CreateYieldRuntimeBridgeScenario(AsyncCallstackKind.StateMachineAsync, Frame.CoreLib(
                "System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter+<>c.Callback()", "Yield.Callback"));
            Frame other = Frame.App("Scenario.UnrelatedSynchronousBody");
            scenario.Sync[1] = other;
            Frame[] prefix = { scenario.Sync[0], other };
            Frame[] tail = new[] { scenario.Async[0] }.Concat(scenario.ExpectedWithoutInfrastructureCleanup.Skip(2)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = prefix.Concat(new[] { Frame.V1MoveNextAsDispatcher }).Concat(tail).ToArray();
            scenario.ExpectedStitched = prefix.Concat(new[] { Frame.V1MoveNextAsDispatcher.DispatcherFrames() }).Concat(tail).ToArray();
            scenario.EnableNormalization = enableNormalization;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 2;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;
            scenario.AssertTransformContext = context =>
            {
                Assert.DoesNotContain(context.Frames, frame => frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent);
                Assert.False(Assert.Single(context.Frames.Where(frame =>
                    frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget)).IsStateMachineBoxCarrier);
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void RuntimeResumptionBridge_YieldFamily_RequiresTheRootDispatchPairNotAnInnerPair()
        {
            Frame yield = Frame.CoreLib(
                "System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter.Callback()", "Yield.Callback");
            StitchScenario scenario = CreateYieldRuntimeBridgeScenario(yield);
            Frame inner = Frame.App("Scenario.InnerAsync");
            Frame innerParent = Frame.App("Scenario.InnerParentAsync");
            scenario.AsyncSegments = new[]
            {
                AsyncSegment.Runtime(scenario.Async),
                AsyncSegment.Runtime(inner, innerParent),
            };
            scenario.Sync = new[]
            {
                inner, Frame.V2Wrapper(0), Frame.V2InstrumentedDispatch, Frame.V2Dispatch,
            }.Concat(scenario.Sync.Where(frame => frame != Frame.V2InstrumentedDispatch)).ToArray();
            scenario.ExpectedStitched = new[] { inner, innerParent }
                .Concat(scenario.ExpectedWithoutInfrastructureCleanup).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = scenario.ExpectedStitched;
            scenario.ExpectedWithoutNormalization = scenario.ExpectedStitched;
            scenario.ExpectedDiagnostics.SegmentsProcessed = 4;
            scenario.ExpectedDiagnostics.V2PlumbingFramesCollapsed = 6;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void RuntimeResumptionBridge_MissingBoundary_DoesNotFoldTimerFrames()
        {
            StitchScenario scenario = CreateRuntimeResumptionBridgeScenario(AsyncCallstackKind.RuntimeAsync);
            scenario.Sync = scenario.Sync.Take(2).Concat(scenario.Sync.Skip(5)).ToArray();
            Frame parent = scenario.Async[1];
            scenario.ExpectedWithoutInfrastructureCleanup = scenario.Sync.Concat(new[] { parent }).ToArray();
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation
            {
                SegmentsProcessed = 2,
                BoundariesNotFound = 2,
            };

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void RuntimeResumptionBridge_MismatchedV2CurrentFrame_DoesNotFoldTimerFrames()
        {
            StitchScenario scenario = CreateRuntimeResumptionBridgeScenario(AsyncCallstackKind.RuntimeAsync);
            Frame callback = Frame.App("Scenario.UnrelatedCallback");
            scenario.Sync = scenario.Sync.Take(2).Concat(new[] { callback }).Concat(scenario.Sync.Skip(2)).ToArray();
            Frame[] logical = { scenario.Sync[0], scenario.Async[0], callback, scenario.Async[1] };
            scenario.ExpectedWithoutInfrastructureCleanup = logical.Concat(scenario.Sync.Skip(6)).ToArray();
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
            scenario.ExpectedDiagnostics.AdjacencyMismatches = 2;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void RuntimeResumptionBridge_UnresolvedV2CurrentFrame_DoesNotFoldTimerFrames()
        {
            StitchScenario scenario = CreateRuntimeResumptionBridgeScenario(AsyncCallstackKind.RuntimeAsync);
            scenario.OmitSymbolFor = scenario.Sync[1];
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync)]
        public void RuntimeResumptionBridge_WithoutTraceLog_PreservesTheCompleteBridge(int kindValue)
        {
            var kind = (AsyncCallstackKind)kindValue;
            StitchScenario scenario = CreateRuntimeResumptionBridgeScenario(kind);
            scenario.EnableInfrastructureCleanup = false;
            scenario.ExpectedStitched = scenario.ExpectedWithoutInfrastructureCleanup;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;
            scenario.AssertTransformContext = context =>
            {
                var diagnostics = new AsyncProfilerCallStackDiagnostics { SegmentsProcessed = context.Segments.Count };
                var pipeline = new AsyncProfilerCallStackTransformPipeline
                {
                    EnableNormalization = false,
                    EnableInfrastructureCleanup = true,
                };
                AsyncProfilerStitchResult result = pipeline.Transform(
                    new AsyncProfilerStitchResult(context.Frames.ToArray(), diagnostics),
                    context.Segments, context.TimestampQpc, context.ClassifyBoundary);
                Assert.Equal(context.Frames, result.Frames);
                Assert.Equal(0, diagnostics.AsyncInfrastructureFramesCollapsed);
                Assert.Equal(0, diagnostics.RuntimeResumptionBridgeFramesCollapsed);
            };

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData((int)AsyncCallstackKind.StateMachineAsync)]
        [InlineData((int)AsyncCallstackKind.RuntimeAsync)]
        public void RuntimeResumptionBridge_DirectTaskContinuation_UsesTheResolvedCompletionPair(int kindValue)
        {
            var kind = (AsyncCallstackKind)kindValue;
            StitchScenario scenario = CreateRuntimeResumptionBridgeScenario(kind);
            Frame continuation = scenario.Sync[scenario.Sync.Length - 7];
            scenario.Sync = scenario.Sync.Where(frame => frame != continuation).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup =
                scenario.ExpectedWithoutInfrastructureCleanup.Where(frame => frame != continuation).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 8;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 8;

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void RuntimeResumptionBridge_NestedActiveContexts_FoldsVerifiedInterContextSpan()
        {
            StitchScenario scenario = CreateRuntimeResumptionBridgeScenario(AsyncCallstackKind.StateMachineAsync);
            Frame outerCurrent = Frame.App("Scenario.OuterAsync");
            Frame outerParent = Frame.App("Scenario.OuterParentAsync");
            Frame[] innerLogical = scenario.ExpectedStitched.Take(3).ToArray();
            Frame[] tail = scenario.Sync.Skip(scenario.Sync.Length - 7).ToArray();
            Frame[] outer =
            {
                outerCurrent, Frame.V2Wrapper(0), Frame.V2InstrumentedDispatch, Frame.V2Dispatch,
                Frame.ThreadPoolDispatch, Frame.WorkerThreadStart,
            };
            scenario.AsyncSegments = new[]
            {
                AsyncSegment.Runtime(outerCurrent, outerParent),
                AsyncSegment.StateMachine(scenario.Async),
            };
            scenario.Sync = scenario.Sync.Take(scenario.Sync.Length - 2).Concat(outer).ToArray();
            scenario.ExpectedWithoutNormalization = innerLogical.Concat(tail.Take(5))
                .Concat(new[] { outerCurrent, outerParent, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart }).ToArray();
            scenario.ExpectedStitched = innerLogical
                .Concat(new[] { outerCurrent, outerParent, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart }).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = scenario.ExpectedStitched;
            scenario.ExpectedDiagnostics.SegmentsProcessed = 4;
            scenario.ExpectedDiagnostics.V2PlumbingFramesCollapsed = 4;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.AsyncReentrantHandoffFramesCollapsed = 10;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void V1AfterV2Completion_CollapsesOnlyTheVerifiedHandoff(
            bool enableParentContexts, bool applicationCaller)
        {
            StitchScenario scenario = CreateV1AfterV2CompletionScenario();
            scenario.EnableParentContexts = enableParentContexts;
            scenario.VerifyThreadTimeComputer = true;
            scenario.IncludeEventSourceEvents = false;
            scenario.AllowSupplementalSamples = true;
            if (applicationCaller)
            {
                Frame caller = Frame.App("Scenario.SynchronousCaller");
                scenario.Sync[9] = caller;
                scenario.ExpectedStitched[3] = caller;
                scenario.ExpectedWithoutNormalization[8] = caller;
            }

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void V1AfterV2Completion_UnresolvedHandoffOrCaller_PreservesTheCompleteBridge(int unresolvedFrame)
        {
            StitchScenario scenario = CreateV1AfterV2CompletionScenario();
            scenario.OmitSymbolFor = scenario.Sync[4 + unresolvedFrame];
            scenario.ExpectedStitched = scenario.ExpectedWithoutNormalization;
            scenario.ExpectedDiagnostics.V2CompletionHandoffFramesCollapsed = 0;

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void V1AfterV2Completion_SynchronousStartupNormalization_DoesNotSkipTheHandoff()
        {
            StitchScenario scenario = CreateV1AfterV2CompletionScenario();
            Frame generated = Frame.App("Scenario+<SynchronousChildAsync>d__62.MoveNext");
            Frame builder = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder.Start(Scenario+<SynchronousChildAsync>d__62&)",
                "AsyncTaskMethodBuilder.Start");
            Frame kickoff = Frame.App("Scenario.SynchronousChildAsync()");
            Frame[] prefix = { generated, builder, kickoff };
            scenario.Sync = prefix.Concat(scenario.Sync.Skip(1)).ToArray();
            scenario.ExpectedStitched = new[] { kickoff }.Concat(scenario.ExpectedStitched.Skip(1)).ToArray();
            scenario.ExpectedWithoutNormalization =
                prefix.Concat(scenario.ExpectedWithoutNormalization.Skip(1)).ToArray();
            scenario.ExpectedDiagnostics.V1SynchronousStartupFramesCollapsed = 4;
            scenario.VerifySampleWeights = true;
            scenario.VerifyThreadTimeComputer = true;
            scenario.IncludeEventSourceEvents = false;
            scenario.AllowSupplementalSamples = true;

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void V1AfterV2Completion_NonCoreLibHandoff_PreservesTheCompleteBridge(int foreignFrame)
        {
            StitchScenario scenario = CreateV1AfterV2CompletionScenario();
            Frame original = scenario.Sync[4 + foreignFrame];
            Frame foreign = Frame.Library("OtherLibrary", original.SymbolName, original.Label + " (foreign)");
            scenario.Sync[4 + foreignFrame] = foreign;
            scenario.ExpectedWithoutNormalization[3 + foreignFrame] = foreign;
            scenario.ExpectedStitched = scenario.ExpectedWithoutNormalization;
            scenario.ExpectedDiagnostics.V2CompletionHandoffFramesCollapsed = 0;

            scenario.AssertProductionStitch();
        }

        [Theory]
        [InlineData("")]
        [InlineData("B")]
        [InlineData("BR")]
        [InlineData("BRT")]
        [InlineData("RTID")]
        [InlineData("BTID")]
        [InlineData("BRID")]
        [InlineData("BRTI")]
        [InlineData("BRTD")]
        [InlineData("RBTID")]
        [InlineData("BTRID")]
        [InlineData("BERTID")]
        [InlineData("BARTID")]
        [InlineData("BNRTID")]
        [InlineData("BRTWID")]
        [InlineData("BRTII")]
        [InlineData("BRTIID")]
        [InlineData("BRTIDD")]
        public void V1AfterV2Completion_IncompleteOrInterruptedHandoff_PreservesTheCompleteBridge(string pattern)
        {
            StitchScenario scenario = CreateV1AfterV2CompletionScenario();
            Frame[] bridge = scenario.Sync.Skip(4).Take(5).ToArray();
            var frames = new Dictionary<char, Frame>
            {
                ['B'] = bridge[0],
                ['R'] = bridge[1],
                ['T'] = bridge[2],
                ['I'] = bridge[3],
                ['D'] = bridge[4],
                ['E'] = Frame.CoreLib("TestOnly.UnclassifiedFrame", "UnclassifiedFrame"),
                ['A'] = Frame.App("Scenario.ApplicationBridge"),
                ['N'] = Frame.Library("NativeBridge", "NativeBridge.Frame", "NativeBridge"),
                ['W'] = Frame.V2Wrapper(0),
            };
            Frame[] tail = pattern.Select(token => frames[token])
                .Concat(scenario.Sync.Skip(9)).ToArray();
            scenario.Sync = scenario.Sync.Take(4).Concat(tail).ToArray();
            scenario.ExpectedWithoutNormalization =
                scenario.ExpectedWithoutNormalization.Take(3).Concat(tail).ToArray();
            scenario.ExpectedStitched = scenario.ExpectedWithoutNormalization;
            scenario.ExpectedDiagnostics.V2CompletionHandoffFramesCollapsed = 0;

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void V1AfterV2Completion_UnmatchedV1Current_PreservesTheCompleteBridge()
        {
            StitchScenario scenario = CreateV1AfterV2CompletionScenario();
            Frame otherCurrent = Frame.App("Scenario+<OtherAsync>d__99.MoveNext")
                .Logical("Scenario.OtherAsync");
            scenario.Async[0] = otherCurrent;
            scenario.ExpectedStitched = scenario.ExpectedWithoutNormalization.Take(2)
                .Concat(new[] { Frame.V1MoveNextAsDispatcher, otherCurrent })
                .Concat(scenario.ExpectedWithoutNormalization.Skip(2)).ToArray();
            scenario.ExpectedWithoutNormalization = null;
            scenario.ExpectedDiagnostics.V2CompletionHandoffFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.V1SynchronousMoveNextFramesNormalized = 2;

            scenario.AssertProductionStitch();
        }

        [Fact]
        public void V1AfterV2Completion_BridgeAssignedToLiveV2Segment_FoldsVerifiedReentrantHandoff()
        {
            StitchScenario scenario = CreateV1AfterV2CompletionScenario();
            Frame outerCurrent = Frame.App("Scenario.ActiveOuterAsync");
            scenario.AsyncSegments = new[]
            {
                AsyncSegment.Runtime(outerCurrent),
                AsyncSegment.StateMachine(scenario.Async),
            };
            scenario.Sync = scenario.Sync.Take(9).Concat(new[]
            {
                outerCurrent,
                Frame.V2Wrapper(0),
                Frame.V2InstrumentedDispatch,
                Frame.V2Dispatch,
            }).Concat(scenario.Sync.Skip(9)).ToArray();
            Frame[] previous = scenario.ExpectedWithoutNormalization;
            scenario.ExpectedWithoutNormalization = previous.Take(8)
                .Concat(new[] { outerCurrent })
                .Concat(previous.Skip(8)).ToArray();
            scenario.ExpectedStitched = previous.Take(3).Concat(new[] { outerCurrent })
                .Concat(previous.Skip(8)).ToArray();
            scenario.ExpectedDiagnostics.SegmentsProcessed = 4;
            scenario.ExpectedDiagnostics.V2PlumbingFramesCollapsed = 4;
            scenario.ExpectedDiagnostics.V2CompletionHandoffFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.AsyncReentrantHandoffFramesCollapsed = 10;

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
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
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

        private static void AssertScenarioBrokenRoots(MutableTraceEventStackSource source, int expectedCount)
        {
            bool found = false;
            source.ForEach(sample =>
            {
                int broken = 0;
                bool scenario = false;
                for (var stack = sample.StackIndex; stack != StackSourceCallStackIndex.Invalid; stack = source.GetCallerIndex(stack))
                {
                    StackSourceFrameIndex frame = source.GetFrameIndex(stack);
                    string name = source.GetFrameName(frame, false);
                    scenario |= name.Contains("Scenario");
                    if (name != "BROKEN")
                    {
                        continue;
                    }
                    broken++;
                    Assert.NotEqual(StackSourceFrameIndex.Broken, frame);
                    Assert.Equal(CodeAddressIndex.Invalid, source.GetFrameCodeAddress(frame));
                    Assert.False(source.TryGetAsyncProfilerCallStackFrameInfo(frame, out _));
                }
                if (scenario)
                {
                    found = true;
                    Assert.Equal(expectedCount, broken);
                }
            });
            Assert.True(found);
        }

        private static StitchScenario CreateFullyCompletedRecursiveV1UnwindScenario(int bodies, bool trailingHelpers)
        {
            Frame method = Frame.App("Scenario+<RecursiveReturnAsync>d__20.MoveNext");
            const string box = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[System.Int64,Scenario+<RecursiveReturnAsync>d__20]";
            Frame[] unit =
            {
                Frame.CoreLib(box + ".MoveNext(class System.Threading.Thread,value class Flags)", "Recursive entry"),
                Frame.CoreLib("System.Runtime.CompilerServices.TaskAwaiter+<>c.<OutputWaitEtwEvents>b__12_0(System.Action,System.Threading.Tasks.Task)",
                    "Awaiter correlation"),
                Frame.CoreLib("System.Threading.Tasks.AwaitTaskContinuation.RunOrScheduleAction", "Continuation bridge"),
                Frame.CoreLib("System.Threading.Tasks.Task.RunContinuations", "Run continuations"),
                Frame.CoreLib("System.Threading.Tasks.Task`1[System.Int64].TrySetResult", "Set task result"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].SetExistingTaskResult", "Complete builder"),
                method,
                Frame.CoreLib(box + ".ExecutionContextCallback(class System.Object)", "Recursive callback"),
                Frame.CoreLib("System.Threading.ExecutionContext.RunInternal", "Execution-context bridge"),
            };
            Frame[] spine = Enumerable.Range(0, bodies).SelectMany(_ => unit).ToArray();
            if (!trailingHelpers) { spine = spine.Take(spine.Length - 2).ToArray(); }
            Frame[] native =
            {
                Frame.Library("coreclr", "Native.ReturnWork", "Runtime return work"),
                Frame.Library("ntdll", "Native.ReturnTransition", "Native return transition"),
            };
            int captured = bodies + 2;
            int[] states = Enumerable.Repeat(1, captured).ToArray();
            states[0] = 0;
            states[captured - 1] = 7;
            Frame[] output = native.Concat(new[]
            {
                unit[0].DispatcherFrames(),
                method.Logical("Scenario.RecursiveReturnAsync", -2),
            }).ToArray();
            return new StitchScenario
            {
                Kind = AsyncCallstackKind.StateMachineAsync,
                Sync = native.Concat(spine).ToArray(),
                Async = Enumerable.Repeat(method, captured).ToArray(),
                AsyncStates = states,
                MethodCompletions = captured,
                EnableFrameAnnotations = true,
                IncludeEventSourceEvents = false,
                SkipNormalStackAssertion = true,
                AllowSupplementalSamples = true,
                VerifyThreadTimeComputer = true,
                VerifySampleWeights = true,
                ExpectedStitched = output,
                ExpectedWithoutInfrastructureCleanup = output,
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    BoundariesNotFound = 2,
                    IncompleteAsyncRootsNormalized = 2,
                    V1CompletionUnwindFramesCollapsed = spine.Length * 2,
                },
            };
        }

        private static StitchScenario CreateV1CompletedUnwindScenario(bool truncated, Frame awaiterCallback = null)
        {
            Frame deep = Frame.App("Scenario+<DeepAsync>d__1.MoveNext");
            Frame child = Frame.App("Scenario+<ChildAsync>d__2.MoveNext");
            Frame current = Frame.App("Scenario+<CurrentAsync>d__3.MoveNext");
            Frame root = Frame.App("Scenario+<RootAsync>d__4.MoveNext");
            const string box = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1";
            Frame currentBox = Frame.CoreLib(
                box + "[System.Int64,Scenario+<CurrentAsync>d__3].MoveNext(class System.Threading.Thread,value class Flags)",
                "CurrentBox.MoveNext");
            Frame childBox = Frame.CoreLib(
                box + "[System.Int64,Scenario+<ChildAsync>d__2].MoveNext(class System.Threading.Thread)",
                "ChildBox.MoveNext");
            Frame callback = Frame.CoreLib(
                box + "[System.Int64,Scenario+<ChildAsync>d__2].ExecutionContextCallback(class System.Object)",
                "ChildBox.ExecutionContextCallback");
            Frame deepBox = Frame.CoreLib(
                box + "[System.Int64,Scenario+<DeepAsync>d__1].MoveNext()",
                "DeepBox.MoveNext");
            Frame continuation = Frame.CoreLib(
                "System.Threading.Tasks.AwaitTaskContinuation.RunOrScheduleAction(class System.Runtime.CompilerServices.IAsyncStateMachineBox,bool)",
                "AwaitTaskContinuation.RunOrScheduleAction");
            Frame runContinuations = Frame.CoreLib("System.Threading.Tasks.Task.RunContinuations", "Task.RunContinuations");
            Frame result = Frame.CoreLib("System.Threading.Tasks.Task`1[System.Int64].TrySetResult", "Task.TrySetResult");
            Frame completion = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].SetExistingTaskResult",
                "Builder.SetExistingTaskResult");
            Frame[] unwind =
            {
                currentBox, continuation, runContinuations, result, completion, child, callback, childBox,
                continuation, runContinuations, result, completion, deep, deepBox,
            };
            return new StitchScenario
            {
                Kind = AsyncCallstackKind.StateMachineAsync,
                Sync = unwind.SelectMany(frame => awaiterCallback != null && frame == continuation
                    ? new[] { awaiterCallback, frame } : new[] { frame }).Concat(truncated ? Array.Empty<Frame>() :
                    new[] { Frame.V1MoveNextAsDispatcher, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart }).ToArray(),
                Async = new[] { deep, child, current, root },
                AsyncStates = new[] { 0, 0, 0, 7 },
                MethodCompletions = 4,
                EnableFrameAnnotations = true,
                IncludeEventSourceEvents = false,
                SkipNormalStackAssertion = true,
                AllowSupplementalSamples = true,
                VerifyThreadTimeComputer = true,
                VerifySampleWeights = true,
            };
        }

        private static StitchScenario CreateV1CompletedUnwindYieldBridgeScenario(params Frame[] helpers)
        {
            StitchScenario scenario = CreateV1CompletedUnwindScenario(false);
            int unwindLength = scenario.Sync.Length - 3;
            Frame native = Frame.Library("coreclr", "MethodDesc.PrepareCode", "NativeWork");
            Frame marker = scenario.Sync[0].DispatcherFrames();
            Frame root = scenario.Async.Last().Logical("Scenario.RootAsync", -2);
            Frame[] tail = { Frame.ThreadPoolDispatch, Frame.WorkerThreadStart };
            scenario.Sync = new[] { native }.Concat(scenario.Sync.Take(scenario.Sync.Length - 2))
                .Concat(helpers).Concat(tail).ToArray();
            scenario.EnableInfrastructureCleanup = true;
            scenario.ExpectedStitched = new[] { native, marker, root }.Concat(tail).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = new[] { native, marker, root }.Concat(helpers).Concat(tail).ToArray();
            scenario.ExpectedWithoutNormalization = scenario.Sync.Where(frame => frame != Frame.V1MoveNextAsDispatcher).ToArray();
            scenario.ExpectedDiagnostics = new CallStackDiagnosticsExpectation
            {
                SegmentsProcessed = 2,
                V1CompletionUnwindFramesCollapsed = unwindLength * 2,
                AsyncInfrastructureFramesCollapsed = helpers.Length * 2,
                RuntimeResumptionBridgeFramesCollapsed = helpers.Length * 2,
            };
            return scenario;
        }

        private static StitchScenario CreateV1TruncatedReturnScenario(int rootCut, bool preservePrefix)
        {
            Frame callback = Frame.CoreLib(
                "System.Runtime.CompilerServices.TaskAwaiter+<>c.<OutputWaitEtwEvents>b__12_0(System.Action,System.Threading.Tasks.Task)",
                "TaskAwaiter.Correlation");
            StitchScenario scenario = CreateV1CompletedUnwindScenario(true, callback);
            int firstBody = Array.IndexOf(scenario.Sync, scenario.Async[1]);
            Frame[] prefix = preservePrefix ? new[]
            {
                Frame.Library("coreclr", "JIT_CompileMethod", "Native.Jit"),
                Frame.Library("clrjit", "Compiler.Compile", "Native.Compiler"),
            } : Array.Empty<Frame>();
            scenario.Sync = prefix.Concat(scenario.Sync.Skip(1).Take(firstBody - 1))
                .Concat(new[] { scenario.Async[2] })
                .Concat(scenario.Sync.Take(scenario.Sync.Length - rootCut)).ToArray();
            scenario.Async = scenario.Async.Concat(new[] { Frame.App("Scenario+<AncestorAsync>d__5.MoveNext") }).ToArray();
            scenario.AsyncStates = new[] { 0, 0, 0, 7, 9 };
            scenario.MethodCompletions = 3;
            return scenario;
        }

        private static StitchScenario CreateV1SurvivingDispatcherRootScenario(int plumbingLength)
        {
            Frame child = Frame.App("Scenario+<ChildAsync>d__1.MoveNext");
            Frame current = Frame.App("Scenario+<CurrentAsync>d__2.MoveNext");
            Frame parent = Frame.App("Scenario+<ParentAsync>d__3.MoveNext");
            const string box = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1";
            Frame carrier = Frame.CoreLib(
                box + "[System.Int64,Scenario+<CurrentAsync>d__2].MoveNext(class System.Threading.Thread)",
                "CurrentBox.MoveNext");
            Frame[] plumbing =
            {
                Frame.CoreLib(box + "[System.Int64,Scenario+<ChildAsync>d__1].InstrumentedMoveNext(class System.Threading.Thread,value class Flags)",
                    "ChildBox.InstrumentedMoveNext"),
                Frame.CoreLib(box + "[System.Int64,Scenario+<ChildAsync>d__1].MoveNext(class System.Threading.Thread)",
                    "ChildBox.MoveNext(Thread)"),
                Frame.CoreLib(box + "[System.Int64,Scenario+<ChildAsync>d__1].MoveNext()",
                    "ChildBox.MoveNext()"),
            };
            return new StitchScenario
            {
                Kind = AsyncCallstackKind.StateMachineAsync,
                Sync = new[] { carrier, child, Frame.V1MoveNextAsDispatcher }.Concat(plumbing.Take(plumbingLength)).ToArray(),
                Async = new[] { child, current, parent },
                AsyncStates = new[] { 0, 0, 1 },
                ExpectedStitched = new[]
                {
                    current.Logical("Scenario.CurrentAsync", state: 0),
                    parent.Logical("Scenario.ParentAsync", state: 1),
                },
                EnableFrameAnnotations = true,
                OnlyManagedCodeStacks = true,
                IncludeEventSourceEvents = false,
                SkipNormalStackAssertion = true,
                VerifySampleWeights = true,
                VerifyThreadTimeComputer = true,
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V1InlineFallbackUsed = 2,
                    V1InfrastructureFramesCollapsed = plumbingLength * 2,
                },
            };
        }

        private static StitchScenario CreateReentrantDispatchEntryScenario()
        {
            Frame inner = Frame.App("Scenario+<NestedDispatcherInnerAsync>d__15.MoveNext");
            Frame logicalInner = inner.Logical("Scenario.NestedDispatcherInnerAsync");
            Frame outer = Frame.App("Scenario+<NestedDispatcherOuterAsync>d__14.MoveNext");
            Frame logicalOuter = outer.Logical("Scenario.NestedDispatcherOuterAsync");
            Frame kickoff = Frame.App("Scenario.NestedDispatcherInnerAsync");
            Frame resume = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncProfiler+ResumeAsyncContext.Resume",
                "Profiler.Resume");
            Frame infrastructure = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncProfilerAsyncStateMachineBox.InstrumentedMoveNext",
                "InstrumentedMoveNext");
            Frame[] registration =
            {
                Frame.CoreLib(
                    "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].AwaitUnsafeOnCompleted(!!0&,class System.Runtime.CompilerServices.IAsyncStateMachineBox)",
                    "AwaitUnsafeOnCompleted(box)"),
                Frame.CoreLib(
                    "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].AwaitUnsafeOnCompleted(!!0&,!!1&,class System.Threading.Tasks.Task`1<!0>&)",
                    "AwaitUnsafeOnCompleted(task)"),
                Frame.CoreLib(
                    "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].AwaitUnsafeOnCompleted(!!0&,!!1&)",
                    "AwaitUnsafeOnCompleted(generic)"),
            };
            Frame[] startup =
            {
                inner,
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start(!!0&)",
                    "AsyncMethodBuilderCore.Start"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].Start(!!0&)",
                    "AsyncTaskMethodBuilder.Start"),
                kickoff,
            };
            Frame[] root = { Frame.ThreadPoolDispatch, Frame.WorkerThreadStart };
            return new StitchScenario
            {
                Sync = new[] { resume, Frame.V1MoveNextAsDispatcher, infrastructure }
                    .Concat(registration).Concat(startup)
                    .Concat(new[] { outer, Frame.V1MoveNextAsDispatcher, infrastructure }).Concat(root).ToArray(),
                AsyncSegments = new[]
                {
                    AsyncSegment.StateMachine(logicalOuter),
                    AsyncSegment.StateMachine(logicalInner),
                },
                ExpectedStitched = new[] { resume, Frame.V1MoveNextAsDispatcher, logicalInner, logicalOuter }
                    .Concat(root).ToArray(),
                ExpectedWithoutNormalization = new[] { resume, Frame.V1MoveNextAsDispatcher, logicalInner }
                    .Concat(registration).Concat(startup).Concat(new[] { logicalOuter }).Concat(root).ToArray(),
                VerifyThreadTimeComputer = true,
                VerifySampleWeights = true,
                IncludeEventSourceEvents = false,
                AllowSupplementalSamples = true,
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 4,
                    V1InlineFallbackUsed = 4,
                    V1InfrastructureFramesCollapsed = 4,
                    V1ReentrantFramesCollapsed = 14,
                },
            };
        }

        private static StitchScenario CreateProfilerHelperTailScenario(
            int resumeCount = 1, bool includeEmission = false)
        {
            StitchScenario scenario = CreateProfilerEnvelopeScenario();
            var prefix = new List<Frame>();
            if (includeEmission)
            {
                prefix.Add(scenario.Sync[8]);
                prefix.Add(scenario.Sync[9]);
            }
            prefix.AddRange(Enumerable.Repeat(scenario.Sync[10], resumeCount));
            scenario.Sync = prefix.Concat(scenario.Sync.Skip(14)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = prefix
                .Concat(scenario.ExpectedWithoutInfrastructureCleanup.Skip(14)).ToArray();
            scenario.ExpectedStitched = scenario.ExpectedStitched.Skip(5).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 10 + (prefix.Count + 1) * 2;
            scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = (prefix.Count + 1) * 2;
            scenario.VerifyThreadTimeComputer = true;
            scenario.IncludeEventSourceEvents = false;
            scenario.AllowSupplementalSamples = true;
            return scenario;
        }

        private static StitchScenario CreateProfilerCaptureEntryScenario()
        {
            StitchScenario scenario = CreateProfilerHelperTailScenario();
            Frame capture = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncProfiler+AsyncCallstack.CaptureStateMachineAsyncCallstack(unsigned int8[],int32&,value class CaptureStateMachineAsyncCallstackState&)",
                "Profiler.Capture");
            scenario.Sync[0] = capture;
            scenario.ExpectedWithoutInfrastructureCleanup[0] = capture;
            return scenario;
        }

        private static StitchScenario CreateProfilerEnvelopeScenario(bool physicalCurrent = false)
        {
            StitchScenario scenario = CreateRuntimeResumptionBridgeScenario(AsyncCallstackKind.StateMachineAsync);
            Frame current = Frame.App("Scenario+<DeepLevel3Async>d__3.MoveNext");
            Frame logicalCurrent = current.Logical("Scenario.DeepLevel3Async");
            Frame parent = Frame.App("Scenario+<DeepLevel2Async>d__2.MoveNext").Logical("Scenario.DeepLevel2Async");
            Frame root = Frame.App("Scenario+<DeepLevel1Async>d__1.MoveNext").Logical("Scenario.DeepLevel1Async");
            Frame[] prefix =
            {
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`1[Scenario+<DeepLevel3Async>d__3]..cctor()", "Diagnostics..cctor"),
                Frame.CoreLib("System.Runtime.CompilerServices.InitHelpers.CallClassConstructor", "InitHelpers.CallClassConstructor"),
                Frame.CoreLib("System.Runtime.CompilerServices.InitHelpers.InitClassHelper", "InitHelpers.InitClassHelper"),
                Frame.CoreLib("System.Runtime.CompilerServices.InitHelpers.InitClassSlow", "InitHelpers.InitClassSlow"),
                Frame.CoreLib("System.Runtime.CompilerServices.StaticsHelpers.GetNonGCStaticBaseSlow", "StaticsHelpers.GetNonGCStaticBaseSlow"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`1[Scenario+<DeepLevel3Async>d__3].get_MethodId()", "Diagnostics.get_MethodId"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[System.Int64,Scenario+<DeepLevel3Async>d__3].System.Runtime.CompilerServices.IAsyncStateMachineBox.GetDiagnosticData(unsigned int64&,int32&,class System.Object&)", "Box.GetDiagnosticData"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncProfiler+AsyncCallstack.CaptureStateMachineAsyncCallstack", "Profiler.Capture"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncProfiler+AsyncCallstack.EmitAsyncCallstack(unsigned int8[],int32&,!!0&)", "Profiler.EmitAsyncCallstack"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncProfiler+AsyncCallstack.EmitEvent", "Profiler.EmitEvent"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncProfiler+ResumeAsyncContext.Resume", "Profiler.Resume"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncProfiler+SyncPoint.ResetContext", "Profiler.ResetContext"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncProfiler+ResumeAsyncContext.Resume", "Profiler.Resume"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncProfiler+ResumeAsyncContext.Resume", "Profiler.Resume"),
            };
            Frame[] logical = { logicalCurrent, parent, root };
            Frame[] tail = scenario.Sync.Skip(scenario.Sync.Length - 7).ToArray();
            scenario.Sync = prefix.Concat(physicalCurrent ? new[] { current } : Array.Empty<Frame>())
                .Concat(scenario.Sync.Skip(2)).ToArray();
            scenario.Async = new[] { logicalCurrent, parent, root };
            scenario.AsyncStates = new[] { 0, 1, 1 };
            scenario.ExpectedStitched = prefix.Take(5).Concat(logical).Concat(tail.Skip(5)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = prefix
                .Concat(physicalCurrent ? Array.Empty<Frame>() : new[] { Frame.V1MoveNextAsDispatcher })
                .Concat(logical).Concat(tail).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = physicalCurrent ? 28 : 30;
            scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = physicalCurrent ? 18 : 20;
            return scenario;
        }

        private static StitchScenario CreateDispatcherCreationScenario(
            AsyncCallstackKind kind = AsyncCallstackKind.StateMachineAsync)
        {
            StitchScenario scenario = CreateRuntimeResumptionBridgeScenario(kind);
            Frame kickoff = Frame.App("Scenario.BurnCpuAsync(int32,int64)");
            Frame[] prefix =
            {
                Frame.CoreLib(
                    "System.Runtime.CompilerServices.AsyncStateMachineDispatcherInfo.CreateDispatcher(class System.Runtime.CompilerServices.IAsyncStateMachineBox,value class Flags)",
                    "CreateDispatcher"),
                Frame.CoreLib(
                    "System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter.System.Runtime.CompilerServices.IStateMachineBoxAwareAwaiter.AwaitUnsafeOnCompleted(class System.Runtime.CompilerServices.IAsyncStateMachineBox)",
                    "YieldAwaiter.AwaitUnsafeOnCompleted"),
                Frame.CoreLib(
                    "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].AwaitUnsafeOnCompleted(!!0&,class System.Runtime.CompilerServices.IAsyncStateMachineBox)",
                    "AwaitUnsafeOnCompleted(box)"),
                Frame.CoreLib(
                    "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].AwaitUnsafeOnCompleted(!!0&,!!1&,class System.Threading.Tasks.Task`1<!0>&)",
                    "AwaitUnsafeOnCompleted(task)"),
                Frame.CoreLib(
                    "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].AwaitUnsafeOnCompleted(!!0&,!!1&)",
                    "AwaitUnsafeOnCompleted(generic)"),
                kickoff,
            };
            scenario.Sync = prefix.Concat(scenario.Sync.Skip(1)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup =
                prefix.Concat(scenario.ExpectedWithoutInfrastructureCleanup.Skip(1)).ToArray();
            scenario.ExpectedStitched = new[] { kickoff }.Concat(scenario.ExpectedStitched.Skip(1)).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed += 10;
            scenario.VerifyThreadTimeComputer = true;
            scenario.IncludeEventSourceEvents = false;
            scenario.AllowSupplementalSamples = true;
            return scenario;
        }

        private static StitchScenario CreateDispatcherOwnedPrefixScenario()
        {
            StitchScenario scenario = CreateDispatcherCreationScenario();
            Frame append = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncProfiler+CreateAsyncContext.Append(value class System.Runtime.CompilerServices.AsyncStateMachineDispatcherInfo&)",
                "Profiler.Append");
            Frame[] prefix = new[] { append }.Concat(scenario.Sync.Take(5)).ToArray();
            // Registration belongs directly to the physical current method, not a synchronous child kickoff.
            scenario.Sync = prefix.Concat(scenario.Sync.Skip(6)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup =
                prefix.Concat(scenario.ExpectedWithoutInfrastructureCleanup.Skip(6)).ToArray();
            scenario.ExpectedStitched = scenario.ExpectedStitched.Skip(1).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 22;
            scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = 12;
            return scenario;
        }

        private static StitchScenario CreateBoundedDispatcherCreationScenario(
            bool enableNormalization, int helperCount, int calleeCount, bool includeStartup,
            AsyncCallstackKind kind = AsyncCallstackKind.StateMachineAsync)
        {
            StitchScenario scenario = CreateDispatcherCreationScenario(kind);
            Frame entry = scenario.Sync[0];
            Frame kickoff = scenario.Sync[5];
            Frame[] callees = Enumerable.Range(0, calleeCount).Select(i =>
                Frame.Library("coreclr", "Native.Work" + i, "NativeWork" + i)).ToArray();
            Frame[] helperKinds =
            {
                Frame.CoreLib(
                    "System.Runtime.CompilerServices.TaskAwaiter.UnsafeOnCompletedInternal(class System.Threading.Tasks.Task,class System.Runtime.CompilerServices.IAsyncStateMachineBox,bool)",
                    "TaskAwaiter.UnsafeOnCompletedInternal"),
                scenario.Sync[3], scenario.Sync[4], scenario.Sync[1], scenario.Sync[2],
            };
            Frame[] helpers = Enumerable.Range(0, helperCount).Select(i => helperKinds[i % helperKinds.Length]).ToArray();
            Frame[] startup =
            {
                Frame.App("Scenario+<BurnCpuAsync>d__3.MoveNext"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start(!!0&)", "BuilderCore.Start"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].Start(!!0&)", "Builder.Start"),
            };
            Frame[] child = (includeStartup ? startup : Array.Empty<Frame>()).Concat(new[] { kickoff }).ToArray();
            Frame[] prefix = callees.Concat(new[] { entry }).Concat(helpers).ToArray();
            Frame[] logicalAndHost = scenario.ExpectedStitched.Skip(1).ToArray();
            scenario.Sync = prefix.Concat(child).Concat(scenario.Sync.Skip(6)).ToArray();
            scenario.ExpectedWithoutNormalization = prefix.Concat(child)
                .Concat(scenario.ExpectedWithoutInfrastructureCleanup.Skip(6)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = prefix
                .Concat(enableNormalization ? new[] { kickoff } : child)
                .Concat(scenario.ExpectedWithoutInfrastructureCleanup.Skip(6)).ToArray();
            Frame[] displayedChild = includeStartup && !enableNormalization
                ? new[] { startup[0], startup[1].DispatcherFrames(), kickoff } : new[] { kickoff };
            scenario.ExpectedStitched = callees.Concat(new[] { entry.DispatcherFrames() })
                .Concat(displayedChild).Concat(logicalAndHost).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed =
                10 + 2 * (1 + helperCount) + (includeStartup && !enableNormalization ? 4 : 0);
            scenario.ExpectedDiagnostics.V1SynchronousStartupFramesCollapsed = includeStartup && enableNormalization ? 6 : 0;
            scenario.EnableNormalization = enableNormalization;
            return scenario;
        }

        private static StitchScenario CreateRecursiveV1CompletionScenario(int boxCount)
        {
            Frame method = Frame.App("Scenario+<RecursiveAsync>d__1.MoveNext()");
            Frame captured = method.Logical("Scenario.RecursiveAsync", 1);
            const string boxType = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[System.Int64,Scenario+<RecursiveAsync>d__1]";
            Frame[] boxes =
            {
                Frame.CoreLib(boxType + ".InstrumentedMoveNext(System.Threading.Thread,Flags)", "Entry wrapper"),
                Frame.CoreLib(boxType + ".MoveNext(System.Threading.Thread)", "Entry with thread"),
                Frame.CoreLib(boxType + ".MoveNext()", "Entry without thread"),
            };
            Frame[] prefix = new[]
            {
                Frame.Library("coreclr", "MethodDesc.PrepareCode", "Runtime work"),
                Frame.Library("clrjit", "JitCompiler.compileMethod", "JIT work"),
            }.Concat(boxes.Take(boxCount)).Concat(new[]
            {
                Frame.CoreLib("System.Runtime.CompilerServices.TaskAwaiter+<>c.<OutputWaitEtwEvents>b__12_0(System.Action,System.Threading.Tasks.Task)",
                    "TaskAwaiter.Correlation"),
                Frame.CoreLib("System.Threading.Tasks.AwaitTaskContinuation.RunOrScheduleAction", "Continuation.RunOrScheduleAction"),
                Frame.CoreLib("System.Threading.Tasks.Task.RunContinuations", "Task.RunContinuations"),
                Frame.CoreLib("System.Threading.Tasks.Task`1[System.Int64].TrySetResult(!0)", "Task.TrySetResult"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].SetExistingTaskResult(class System.Object)",
                    "Builder.SetExistingTaskResult"),
            }).ToArray();
            Frame[] returnTail =
            {
                Frame.CoreLib(boxType + ".ExecutionContextCallback(System.Object)", "Return callback"),
                Frame.CoreLib("System.Threading.ExecutionContext.RunInternal", "ExecutionContext.RunInternal"),
                Frame.CoreLib(boxType + ".MoveNext(System.Threading.Thread,Flags)", "Return with flags"),
                Frame.V1MoveNextAsDispatcher,
            };
            Frame yield = Frame.CoreLib(
                "System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter+<>c.<OutputCorrelationEtwEvent>b__6_0(System.Action,System.Threading.Tasks.Task)",
                "Yield.Correlation");
            Frame[] logical = Enumerable.Repeat(captured, 3).ToArray();
            Frame[] host = { Frame.ThreadPoolDispatch, Frame.WorkerThreadStart };
            return new StitchScenario
            {
                Kind = AsyncCallstackKind.StateMachineAsync,
                Sync = prefix.Concat(new[] { method }).Concat(returnTail).Concat(boxes)
                    .Concat(new[] { yield }).Concat(host).ToArray(),
                Async = Enumerable.Repeat(captured, 4).ToArray(),
                AsyncStates = new[] { 0, 1, 1, 1 },
                MethodCompletions = 1,
                EnableInfrastructureCleanup = true,
                EnableFrameAnnotations = true,
                SkipNormalStackAssertion = true,
                IncludeEventSourceEvents = false,
                VerifyThreadTimeComputer = true,
                VerifySampleWeights = true,
                ExpectedStitched = prefix.Take(2).Concat(new[] { prefix[2].DispatcherFrames(),
                    method.Logical("Scenario.RecursiveAsync", -2) }).Concat(logical).Concat(host).ToArray(),
                ExpectedWithoutInfrastructureCleanup = prefix.Concat(new[] { method.Logical("Scenario.RecursiveAsync", -2) })
                    .Concat(logical).Concat(new[] { yield }).Concat(host).ToArray(),
                ExpectedWithoutNormalization = prefix.Concat(new[] { captured }).Concat(returnTail)
                    .Concat(logical).Concat(new[] { yield }).Concat(host).ToArray(),
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V1CompletionTailUsed = 2,
                    V1InfrastructureFramesCollapsed = 14,
                    AsyncInfrastructureFramesCollapsed = 2 * (boxCount + 6),
                    ProfilerEnvelopeFramesCollapsed = 2 * (boxCount + 5),
                    RuntimeResumptionBridgeFramesCollapsed = 2,
                },
            };
        }

        private static StitchScenario CreateV1CompletionMachineryScenario()
        {
            Frame yield = Frame.CoreLib(
                "System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter+<>c.<OutputCorrelationEtwEvent>b__6_0(System.Action,System.Threading.Tasks.Task)",
                "Yield.Correlation");
            StitchScenario scenario = CreateYieldRuntimeBridgeScenario(AsyncCallstackKind.StateMachineAsync, yield);
            Frame[] prefix =
            {
                Frame.Library("coreclr", "MethodDesc.PrepareCode", "NativeWork"),
                Frame.CoreLib(
                    "System.Runtime.CompilerServices.TaskAwaiter+<>c.<OutputWaitEtwEvents>b__12_0(System.Action,System.Threading.Tasks.Task)",
                    "TaskAwaiter.Correlation"),
                Frame.CoreLib("System.Threading.Tasks.AwaitTaskContinuation.RunOrScheduleAction",
                    "AwaitTaskContinuation.RunOrScheduleAction"),
                Frame.CoreLib("System.Threading.Tasks.Task.RunContinuations", "Task.RunContinuations"),
                Frame.CoreLib("System.Threading.Tasks.Task`1[System.Int64].TrySetResult(!0)", "Task.TrySetResult"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].SetExistingTaskResult(class System.Object)",
                    "Builder.SetExistingTaskResult"),
            };
            Frame[] logical = scenario.ExpectedStitched.Skip(1).Take(2).ToArray();
            scenario.Sync = prefix.Concat(scenario.Sync.Skip(1)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = prefix.Concat(logical)
                .Concat(new[] { yield, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart }).ToArray();
            scenario.ExpectedStitched = new[] { prefix[0], prefix[1].DispatcherFrames() }
                .Concat(logical).Concat(new[] { Frame.ThreadPoolDispatch, Frame.WorkerThreadStart }).ToArray();
            scenario.AsyncStates = new[] { 0, 1 };
            scenario.MethodCompletions = 1;
            scenario.ExpectedDiagnostics.V1InlineFallbackUsed = 0;
            scenario.ExpectedDiagnostics.V1CompletionTailUsed = 2;
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 12;
            scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = 10;
            return scenario;
        }

        private static StitchScenario CreateV1AwaitRegistrationScenario()
        {
            StitchScenario scenario = CreateRuntimeResumptionBridgeScenario(AsyncCallstackKind.StateMachineAsync);
            Frame[] prefix =
            {
                Frame.Library("coreclr", "MethodDesc.PrepareCode", "NativeWork"),
                Frame.CoreLib(
                    "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[System.Int64,Scenario+<Level1Async>d__1].System.Runtime.CompilerServices.IAsyncStateMachineBox.GetDiagnosticData(unsigned int64&,int32&,class System.Object&)",
                    "Box.GetDiagnosticData"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncProfiler+AsyncCallstack.CaptureStateMachineAsyncCallstack",
                    "Profiler.Capture"),
                Frame.CoreLib("System.Runtime.CompilerServices.AsyncProfiler+AsyncCallstack.EmitAsyncCallstack",
                    "Profiler.Emit"),
                Frame.CoreLib(
                    "System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter.System.Runtime.CompilerServices.IStateMachineBoxAwareAwaiter.AwaitUnsafeOnCompleted(class System.Runtime.CompilerServices.IAsyncStateMachineBox)",
                    "YieldAwaiter.AwaitUnsafeOnCompleted"),
                Frame.CoreLib(
                    "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].AwaitUnsafeOnCompleted(!!0&,class System.Runtime.CompilerServices.IAsyncStateMachineBox)",
                    "AwaitUnsafeOnCompleted(box)"),
            };
            scenario.Sync = prefix.Concat(scenario.Sync.Skip(1)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup =
                prefix.Concat(scenario.ExpectedWithoutInfrastructureCleanup.Skip(1)).ToArray();
            scenario.ExpectedStitched = prefix.Take(1).Concat(scenario.ExpectedStitched.Skip(1)).ToArray();
            scenario.AsyncStates = new[] { 0, 1 };
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 20;
            scenario.ExpectedDiagnostics.ProfilerEnvelopeFramesCollapsed = 10;
            scenario.VerifyThreadTimeComputer = true;
            scenario.IncludeEventSourceEvents = false;
            scenario.AllowSupplementalSamples = true;
            return scenario;
        }

        private static StitchScenario CreateCallbackCompletionBridgeScenario(
            AsyncCallstackKind kind, bool includeAwaiterHelpers = false)
        {
            StitchScenario scenario = CreateRuntimeResumptionBridgeScenario(kind);
            Frame[] logical = scenario.ExpectedStitched.Take(3).ToArray();
            Frame[] chain =
            {
                Frame.CoreLib("System.Threading.Tasks.Task.RunContinuations", "Task.RunContinuations"),
                Frame.CoreLib("System.Threading.Tasks.Task`1[System.Boolean].TrySetResult(!0)", "Task.TrySetResult"),
                Frame.CoreLib("System.Threading.Tasks.TaskCompletionSource`1[System.Boolean].SetResult(!0)",
                    "TaskCompletionSource.SetResult"),
            };
            if (includeAwaiterHelpers)
            {
                chain = new[]
                {
                    Frame.CoreLib(
                        "System.Runtime.CompilerServices.TaskAwaiter+<>c::<OutputWaitEtwEvents>b__12_0(System.Action, System.Threading.Tasks.Task)",
                        "TaskAwaiter.Callback"),
                    Frame.CoreLib("System.Threading.Tasks.AwaitTaskContinuation.RunOrScheduleAction",
                        "AwaitTaskContinuation.RunOrScheduleAction"),
                }.Concat(chain).ToArray();
            }
            Frame[] tail =
            {
                Frame.App("Scenario.SynchronousCallback"),
                Frame.CoreLib("System.Threading.TimerQueueTimer.Fire", "TimerQueueTimer.Fire"),
                Frame.ThreadPoolDispatch,
                Frame.WorkerThreadStart,
            };
            scenario.Sync = scenario.Sync.Take(scenario.Sync.Length - 7).Concat(chain).Concat(tail).ToArray();
            scenario.ExpectedStitched = logical.Concat(chain).Concat(tail).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = logical.Concat(chain).Concat(tail).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = 0;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = 0;
            scenario.IncludeEventSourceEvents = false;
            scenario.AllowSupplementalSamples = true;
            scenario.VerifyThreadTimeComputer = true;
            scenario.AssertStitchedStackSource = stackSource =>
            {
                for (int i = 0; i < stackSource.CallFrameIndexLimit; i++)
                {
                    if (stackSource.TryGetAsyncProfilerCallStackFrameInfo((StackSourceFrameIndex)i, out AsyncProfilerCallStackSourceFrameInfo info))
                    {
                        Assert.False(info.IsDispatchTarget);
                        Assert.False(info.IsCompleting);
                    }
                }
            };
            return scenario;
        }

        private static StitchScenario CreateRuntimeResumptionBridgeScenario(AsyncCallstackKind kind)
        {
            Frame work = Frame.App("Scenario.Work");
            Frame current = kind == AsyncCallstackKind.StateMachineAsync
                ? Frame.App("Scenario+<Level2Async>d__2.MoveNext")
                : Frame.App("Scenario.Level2Async");
            Frame logicalCurrent = kind == AsyncCallstackKind.StateMachineAsync
                ? current.Logical("Scenario.Level2Async") : current;
            Frame parent = kind == AsyncCallstackKind.StateMachineAsync
                ? Frame.App("Scenario+<Level1Async>d__1.MoveNext").Logical("Scenario.Level1Async")
                : Frame.App("Scenario.Level1Async");
            var sync = new List<Frame> { work, current };
            if (kind == AsyncCallstackKind.StateMachineAsync)
            {
                sync.Add(Frame.V1MoveNextAsDispatcher);
                sync.Add(Frame.CoreLib(
                    "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncProfilerAsyncStateMachineBox.InstrumentedMoveNext",
                    "V1.InstrumentedMoveNext"));
            }
            else
            {
                sync.Add(Frame.V2Wrapper(0));
                sync.Add(Frame.V2InstrumentedDispatch);
                sync.Add(Frame.V2Dispatch);
            }
            Frame[] tail =
            {
                Frame.CoreLib("System.Threading.Tasks.AwaitTaskContinuation.RunOrScheduleAction",
                    "AwaitTaskContinuation.RunOrScheduleAction"),
                Frame.CoreLib("System.Threading.Tasks.Task.RunContinuations", "Task.RunContinuations"),
                Frame.CoreLib("System.Threading.Tasks.Task`1[System.Int64].TrySetResult(!0)", "Task.TrySetResult"),
                Frame.CoreLib("System.Threading.Tasks.Task+DelayPromise.CompleteTimedOut", "DelayPromise.CompleteTimedOut"),
                Frame.CoreLib("System.Threading.TimerQueueTimer.Fire", "TimerQueueTimer.Fire"),
                Frame.ThreadPoolDispatch,
                Frame.WorkerThreadStart,
            };
            sync.AddRange(tail);
            Frame[] logical = { work, logicalCurrent, parent };
            return new StitchScenario
            {
                Kind = kind,
                Sync = sync.ToArray(),
                Async = new[] { logicalCurrent, parent },
                EnableInfrastructureCleanup = true,
                VerifySampleWeights = true,
                ExpectedStitched = logical.Concat(tail.Skip(5)).ToArray(),
                ExpectedWithoutInfrastructureCleanup = logical.Concat(tail).ToArray(),
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V1InlineFallbackUsed = kind == AsyncCallstackKind.StateMachineAsync ? 2 : 0,
                    V1InfrastructureFramesCollapsed = kind == AsyncCallstackKind.StateMachineAsync ? 2 : 0,
                    V2PlumbingFramesCollapsed = kind == AsyncCallstackKind.RuntimeAsync ? 4 : 0,
                    AsyncInfrastructureFramesCollapsed = 10,
                    RuntimeResumptionBridgeFramesCollapsed = 10,
                },
            };
        }

        private static StitchScenario CreateYieldRuntimeBridgeScenario(params Frame[] yieldFrames) =>
            CreateYieldRuntimeBridgeScenario(AsyncCallstackKind.RuntimeAsync, yieldFrames);

        private static StitchScenario CreateYieldRuntimeBridgeScenario(AsyncCallstackKind kind, params Frame[] yieldFrames)
        {
            StitchScenario scenario = CreateRuntimeResumptionBridgeScenario(kind);
            Frame[] tail = scenario.Sync.Skip(scenario.Sync.Length - 2).ToArray();
            scenario.Sync = scenario.Sync.Take(kind == AsyncCallstackKind.RuntimeAsync ? 5 : 4).Concat(yieldFrames).Concat(tail).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup =
                scenario.ExpectedStitched.Take(3).Concat(yieldFrames).Concat(tail).ToArray();
            scenario.ExpectedDiagnostics.AsyncInfrastructureFramesCollapsed = yieldFrames.Length * 2;
            scenario.ExpectedDiagnostics.RuntimeResumptionBridgeFramesCollapsed = yieldFrames.Length * 2;
            scenario.IncludeEventSourceEvents = false;
            scenario.VerifyThreadTimeComputer = true;
            return scenario;
        }

        private static StitchScenario CreateNestedCompletedV1OuterYieldScenario()
        {
            Frame native = Frame.Library("clrjit", "JitCompiler.compileMethod", "JIT work");
            Frame resume = Frame.CoreLib("System.Runtime.CompilerServices.AsyncProfiler+ResumeAsyncContext.Resume", "Profiler.Resume");
            Frame dispatch = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncProfilerAsyncStateMachineBox`1[System.Int64,Scenario+<ParallelWhenAllAsync>d__12].MoveNextAsDispatcher(class System.Threading.Thread,value class Flags)",
                "Nested dispatch");
            Frame inner = Frame.App("Scenario+<ParallelWhenAllAsync>d__12.MoveNext")
                .Logical("Scenario.ParallelWhenAllAsync", state: 0);
            Frame outerLeaf = Frame.App("Scenario+<BurnCpuAsync>d__115.MoveNext");
            Frame outerRoot = Frame.App("Scenario+<ParallelBranchAsync>d__13.MoveNext");
            Frame yield = Frame.CoreLib(
                "System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter+<>c.<OutputCorrelationEtwEvent>b__6_0(System.Action,System.Threading.Tasks.Task)",
                "Yield.Correlation");
            Frame[] structural = { native, resume, dispatch, inner, yield, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart };
            return new StitchScenario
            {
                Kind = AsyncCallstackKind.StateMachineAsync,
                Sync = new[] { native, resume, dispatch, outerRoot, outerLeaf, Frame.V1MoveNextAsDispatcher,
                    yield, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart },
                AsyncSegments = new[]
                {
                    new AsyncSegment(AsyncCallstackKind.StateMachineAsync, new[] { outerLeaf, outerRoot }, new[] { 0, 1 })
                        { MethodCompletions = 2 },
                    AsyncSegment.StateMachine(inner),
                },
                ExpectedStitched = new[] { native, resume.DispatcherFrames(), inner, Frame.ThreadPoolDispatch, Frame.WorkerThreadStart },
                ExpectedWithoutInfrastructureCleanup = structural,
                EnableFrameAnnotations = true,
                EnableInfrastructureCleanup = true,
                IncludeEventSourceEvents = false,
                VerifySampleWeights = true,
                VerifyThreadTimeComputer = true,
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 4,
                    AsyncInfrastructureFramesCollapsed = 6,
                    ProfilerEnvelopeFramesCollapsed = 4,
                    RuntimeResumptionBridgeFramesCollapsed = 2,
                },
            };
        }

        private static StitchScenario CreateNativeRuntimeDispatchScenario(bool resumeStub, bool methodEvents)
        {
            StitchScenario scenario = CreateRuntimeResumptionBridgeScenario(AsyncCallstackKind.RuntimeAsync);
            Frame[] native =
            {
                Frame.Library("clrjit", "JitCompiler.compileMethod", "JIT work"),
                Frame.Library("coreclr", "MethodDesc.PrepareCode", "Runtime work"),
            };
            Frame[] tail = scenario.Sync.Skip(5).ToArray();
            Frame[] entry = resumeStub
                ? new[] { Frame.App("dynamicClass.IL_STUB_AsyncResume") } : Array.Empty<Frame>();
            scenario.Sync = native.Concat(entry).Concat(new[]
            {
                Frame.V2Wrapper(1), Frame.V2InstrumentedDispatch, Frame.V2Dispatch,
            }).Concat(tail).ToArray();
            Frame[] logical = scenario.Async;
            scenario.Async = new[] { Frame.Zero }.Concat(logical).ToArray();
            scenario.MethodCompletions = methodEvents ? 1 : 0;
            scenario.IncludeEventSourceEvents = false;
            scenario.VerifyThreadTimeComputer = true;
            Frame marker = (resumeStub ? entry[0] : Frame.V2Wrapper(1)).DispatcherFrames();
            scenario.ExpectedStitched = native.Concat(new[] { marker }).Concat(logical).Concat(tail.Skip(5)).ToArray();
            scenario.ExpectedWithoutInfrastructureCleanup = native.Concat(new[] { marker }).Concat(logical).Concat(tail).ToArray();
            scenario.ExpectedDiagnostics.V2PlumbingFramesCollapsed = resumeStub ? 6 : 4;
            scenario.ConfigureTraceLog = log =>
            {
                ConfigureCapturedRuntime(log, "12.0.0");
                foreach (TraceModuleFile module in log.ModuleFiles)
                {
                    if (module.Name == "coreclr" || module.Name == "clrjit" ||
                        string.Equals(module.Name, AsyncProfilerCallStackStitchBoundary.HostModuleName, StringComparison.OrdinalIgnoreCase))
                    {
                        module.fileName = @"C:\runtime\" + module.Name + ".dll";
                    }
                }
            };
            return scenario;
        }

        private static StitchScenario CreateV1AfterV2CompletionScenario()
        {
            Frame work = Frame.App("Scenario.Work");
            Frame current = Frame.App("Scenario+<ParentAsync>d__60.MoveNext");
            Frame logicalCurrent = current.Logical("Scenario.ParentAsync");
            Frame logicalParent = Frame.App("Scenario+<GrandparentAsync>d__61.MoveNext")
                .Logical("Scenario.GrandparentAsync");
            Frame infrastructure = Frame.CoreLib(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncProfilerAsyncStateMachineBox.InstrumentedMoveNext",
                "V1.InstrumentedMoveNext");
            Frame bridge = Frame.CoreLib(
                "System.Threading.Tasks.AwaitTaskContinuation.RunOrScheduleAction",
                "AwaitTaskContinuation.RunOrScheduleAction");
            Frame runContinuations = Frame.CoreLib(
                "System.Threading.Tasks.Task.RunContinuations", "Task.RunContinuations");
            Frame trySetResult = Frame.CoreLib(
                "System.Threading.Tasks.Task`1[System.Int64].TrySetResult(!0)", "Task.TrySetResult");
            Frame[] tail =
            {
                bridge, runContinuations, trySetResult,
                Frame.V2InstrumentedDispatch, Frame.V2Dispatch,
                Frame.ThreadPoolDispatch, Frame.WorkerThreadStart,
            };
            Frame[] logical = { work, logicalCurrent, logicalParent };
            return new StitchScenario
            {
                Kind = AsyncCallstackKind.StateMachineAsync,
                Sync = new[] { work, current, Frame.V1MoveNextAsDispatcher, infrastructure }
                    .Concat(tail).ToArray(),
                Async = new[] { logicalCurrent, logicalParent },
                ExpectedStitched = logical.Concat(tail.Skip(5)).ToArray(),
                ExpectedWithoutNormalization = logical.Concat(tail).ToArray(),
                ExpectedDiagnostics = new CallStackDiagnosticsExpectation
                {
                    SegmentsProcessed = 2,
                    V1InlineFallbackUsed = 2,
                    V1InfrastructureFramesCollapsed = 2,
                    V2CompletionHandoffFramesCollapsed = 10,
                },
            };
        }

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

        private static void UseExtendedCompletionLayout(StitchScenario scenario)
        {
            var profile = new AsyncProfilerExtendedCompletionRuntimeProfile();
            scenario.RuntimeProfileForModule = module => AsyncProfilerRuntimeRuleProfileCache.IsHostModule(module)
                ? profile : AsyncProfilerRuntimeRuleProfile.Common;
            scenario.Sync = ExpandCompletionPair(scenario.Sync);
            if (scenario.ExpectedWithoutInfrastructureCleanup != null)
            {
                scenario.ExpectedWithoutInfrastructureCleanup =
                    ExpandCompletionPair(scenario.ExpectedWithoutInfrastructureCleanup);
            }
            if (scenario.ExpectedWithoutNormalization != null)
            {
                scenario.ExpectedWithoutNormalization =
                    ExpandCompletionPair(scenario.ExpectedWithoutNormalization);
            }
            scenario.VerifyThreadTimeComputer = true;
            scenario.IncludeEventSourceEvents = false;
            scenario.AllowSupplementalSamples = true;
            scenario.VerifySampleWeights = true;
        }

        private static Frame[] InsertEntryHelper(Frame[] frames, Frame helper)
        {
            int resume = Array.FindLastIndex(frames, frame =>
                frame.SymbolName == "System.Runtime.CompilerServices.AsyncProfiler+ResumeAsyncContext.Resume");
            Assert.True(resume >= 0);
            return frames.Take(resume + 1).Concat(new[] { helper }).Concat(frames.Skip(resume + 1)).ToArray();
        }

        private static void UseExtendedRuntimeLayouts(StitchScenario scenario)
        {
            var profile = new AsyncProfilerExtendedRuntimeLayoutProfile();
            scenario.RuntimeProfileForModule = module => AsyncProfilerRuntimeRuleProfileCache.IsHostModule(module)
                ? profile : AsyncProfilerRuntimeRuleProfile.Common;
            Frame anchor = Frame.CoreLib(AsyncProfilerExtendedRuntimeLayoutProfile.AnchorName, "ChangedDispatch");
            Func<Frame[], Frame[]> replace = frames => frames?.Select(frame =>
                frame.Key == Frame.ThreadPoolDispatch.Key ? anchor : frame).ToArray();
            scenario.Sync = replace(scenario.Sync);
            scenario.ExpectedStitched = replace(scenario.ExpectedStitched);
            scenario.ExpectedWithoutNormalization = replace(scenario.ExpectedWithoutNormalization);
            scenario.ExpectedWithoutInfrastructureCleanup = replace(scenario.ExpectedWithoutInfrastructureCleanup);
            scenario.VerifyThreadTimeComputer = true;
            scenario.IncludeEventSourceEvents = false;
            scenario.AllowSupplementalSamples = true;
            scenario.VerifySampleWeights = true;
        }

        private static Frame[] ExpandCompletionPair(Frame[] frames) =>
            frames.SelectMany(frame =>
                frame.Label == "Task.RunContinuations"
                    ? new[]
                    {
                        frame,
                        Frame.CoreLib(AsyncProfilerExtendedCompletionRuntimeProfile.InjectedHelperName, "CompletionHelper"),
                    }
                    : new[]
                    {
                        frame.Label == "Task.TrySetResult"
                            ? Frame.CoreLib(AsyncProfilerExtendedCompletionRuntimeProfile.RenamedCompletionName, "Task.TryComplete")
                            : frame,
                    }).ToArray();

        private static TraceModuleFile ConfigureCapturedRuntime(
            TraceLog log, string productVersion, string engineModule = "coreclr.dll")
        {
            TraceProcess process = log.Processes.First(p => p.ProcessID == 1234);
            TraceModuleFile host = log.ModuleFiles.FirstOrDefault(module =>
                string.Equals(module.Name, AsyncProfilerCallStackStitchBoundary.HostModuleName, StringComparison.OrdinalIgnoreCase));
            if (host == null)
            {
                return null;
            }
            host.productVersion = productVersion ?? "";
            if (engineModule != null)
            {
                process.LoadedModules.UniversalMapping(
                    @"C:\runtime\" + engineModule, 0x100000, 0x101000, 100_001, null);
            }
            return host;
        }

        private sealed class CountingCallStacks : IReadOnlyList<AsyncProfilerCallStack>
        {
            public CountingCallStacks(IReadOnlyList<AsyncProfilerCallStack> callStacks) { m_callStacks = callStacks; }
            public int Count => m_callStacks.Count;
            public int ReadCount { get; set; }
            public AsyncProfilerCallStack this[int index]
            {
                get
                {
                    ReadCount++;
                    return m_callStacks[index];
                }
            }
            public IEnumerator<AsyncProfilerCallStack> GetEnumerator()
            {
                for (int i = 0; i < Count; i++)
                {
                    yield return this[i];
                }
            }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
            private readonly IReadOnlyList<AsyncProfilerCallStack> m_callStacks;
        }

        private sealed class RejectingCompletionChainProfile : AsyncProfilerRuntimeRuleProfile
        {
            public RejectingCompletionChainProfile() : base("Rejected completion chain") { }

            protected override AsyncProfilerRuntimeSequenceMatch MatchTaskCompletionChain(
                AsyncProfilerCallStackTransformState context, int start, int end)
            {
                AsyncProfilerRuntimeSequenceMatch pair = MatchTaskCompletionPair(context, start, end);
                return pair.IsMatch && end - start > pair.Length
                    ? AsyncProfilerRuntimeSequenceMatch.Reject(pair.Length + 1) : default;
            }
        }

        private sealed class RejectingAwaitRegistrationProfile : AsyncProfilerRuntimeRuleProfile
        {
            public RejectingAwaitRegistrationProfile() : base("Rejected await registration") { }

            protected override AsyncProfilerRuntimeSequenceMatch MatchV1AwaitRegistration(
                AsyncProfilerCallStackTransformState context, int start, int end) =>
                start < end ? AsyncProfilerRuntimeSequenceMatch.Reject(end - start) : default;
        }

        private sealed class RejectingProfilerEntryProfile : AsyncProfilerRuntimeRuleProfile
        {
            public RejectingProfilerEntryProfile() : base("Rejected profiler entry") { }

            protected override AsyncProfilerRuntimeSequenceMatch MatchProfilerEntry(
                AsyncProfilerCallStackTransformState context, int start, int end) =>
                AsyncProfilerRuntimeSequenceMatch.Reject(end - start);
        }

        private sealed class DisabledDispatcherCreationCleanupProfile : AsyncProfilerRuntimeRuleProfile
        {
            public DisabledDispatcherCreationCleanupProfile() : base("Disabled dispatcher creation cleanup") { }

            public override bool IsOptionalInfrastructureFrame(
                AsyncProfilerStitchedFrame frame, AsyncProfilerCallStackStitchBoundaryKind boundary) =>
                frame.SyncFrameKind != AsyncProfilerStitchSyncFrameKind.V1DispatcherCreationInfrastructure &&
                base.IsOptionalInfrastructureFrame(frame, boundary);
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
            private Dictionary<string, ulong> _addresses;

            public Frame[] Sync { get; set; }
            public Frame[] Async { get; set; }
            public AsyncSegment[] AsyncSegments { get; set; }
            public Frame[] ExpectedStitched { get; set; }
            public Frame[] ExpectedWithoutNormalization { get; set; }
            public Frame[] ExpectedWithoutInfrastructureCleanup { get; set; }
            public Frame[] FilteredOut { get; set; }
            public Frame OmitSymbolFor { get; set; }
            public AsyncSegment ContextParent { get; set; }
            public int ParentMethodCompletions { get; set; }
            public int ParentExceptionCompletions { get; set; }
            public CallStackDiagnosticsExpectation ExpectedDiagnostics { get; set; }
            public AsyncCallstackKind Kind { get; set; } = AsyncCallstackKind.RuntimeAsync;
            public long AsyncStartQpc { get; set; } = StartQpc + 10;
            public long AsyncEndQpc { get; set; } = StartQpc + 40;
            public bool SampleHasStack { get; set; } = true;
            public bool OnlyManagedCodeStacks { get; set; }
            public bool IncludeEventSourceEvents { get; set; } = true;
            public bool GroupByStartStopActivity { get; set; } = true;
            public bool AddForeignProcessActiveSegment { get; set; }
            public bool IncludeCoreLibUnloadRundown { get; set; }
            public Frame CoreLibMethodBeforeUnload { get; set; }
            public byte ContinuationIndexBase { get; set; }
            public int[] AsyncStates { get; set; }
            public int MethodCompletions { get; set; }
            public long MethodCompletionQpc { get; set; } = StartQpc + 20;
            public int ExceptionCompletions { get; set; }
            public bool IncludeCompletedInnerActivation { get; set; }
            public int WrapperResets { get; set; }
            public bool VerifyComputerAndIndexLifecycle { get; set; }
            public bool VerifyThreadTimeComputer { get; set; }
            public bool IncludeTplActivity { get; set; }
            public bool IncludeStartStopActivity { get; set; }
            public bool AllowSupplementalSamples { get; set; }
            public bool SkipNormalStackAssertion { get; set; }
            public bool EnableParentContexts { get; set; }
            public bool EnableFrameAnnotations { get; set; }
            public bool EnableNormalization { get; set; } = true;
            public bool EnableInfrastructureCleanup { get; set; }
            public bool VerifySampleWeights { get; set; }
            public bool VerifyRemovingParentContextRemovesPresentation { get; set; }
            public Action<MutableTraceEventStackSource> AssertStitchedStackSource { get; set; }
            public Action<EmittedStack, EmittedStack> AssertOutputs { get; set; }
            public Action<AsyncProfilerCallStackTransformState> AssertTransformContext { get; set; }
            public Action<TraceLog, string> AssertPublicApi { get; set; }
            public bool VerifyPublicBuilder { get; set; } = true;
            public Action<TraceLog> ConfigureTraceLog { get; set; } =
                log => ConfigureCapturedRuntime(log, "11.0.0");
            public Func<TraceModuleFile, AsyncProfilerRuntimeRuleProfile> RuntimeProfileForModule { get; set; }
            public AsyncProfilerRuntimeRuleProfile ExpectedRuntimeProfile { get; set; }

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
                        ConfigureTraceLog?.Invoke(traceLog);
                        AsyncSegment[] expectedSegments = GetAsyncSegments();
                        long asyncProbeQpc = AsyncStartQpc + expectedSegments.Length;
                        IReadOnlyList<AsyncProfilerCallStack> segments = new AsyncProfilerCallStackComputer(traceLog).GetCallStacks(ProcessId, OsThreadId, asyncProbeQpc);
                        Assert.Equal(expectedSegments.Length, segments.Count);
                        for (int i = 0; i < expectedSegments.Length; i++)
                        {
                            Assert.Equal(Labels(expectedSegments[i].Frames),
                                AsyncFrameLabels(traceLog, segments[i].Frames, expectedSegments[i]));
                            Assert.Equal(segments[i].Frames.FrameCount, segments[i].CapturedFrames.Count);
                            for (int j = 0; j < segments[i].CapturedFrames.Count; j++)
                            {
                                AsyncProfilerCallStackFrame frame = segments[i].CapturedFrames[j];
                                Assert.Equal(segments[i].Frames.MethodIdAt(j), frame.CapturedInstructionPointer);
                                Assert.Equal(segments[i].Frames.CodeAddressAt(j), frame.CodeAddress);
                                Assert.Equal(segments[i].Frames.Kind == AsyncCallstackKind.StateMachineAsync
                                    ? segments[i].Frames.FrameStateAt(j) : (int?)null, frame.CapturedState);
                            }
                        }
                        if (AddForeignProcessActiveSegment)
                        {
                            Assert.Empty(new AsyncProfilerCallStackComputer(traceLog).GetCallStacks(ProcessId, OsThreadId, SampleQpc));
                            Assert.Single(new AsyncProfilerCallStackComputer(traceLog).GetCallStacks(ForeignProcessId, OsThreadId, SampleQpc));
                        }

                        var syncStackSource = new MutableTraceEventStackSource(traceLog)
                        {
                            OnlyManagedCodeStacks = OnlyManagedCodeStacks,
                        };
                        var syncComputer = new SampleProfilerThreadTimeComputer(traceLog, symbolReader)
                        {
                            IncludeEventSourceEvents = IncludeEventSourceEvents,
                            GroupByStartStopActivity = GroupByStartStopActivity,
                        };
                        syncComputer.GenerateThreadTimeStacks(syncStackSource);
                        EmittedStack syncOutput = ReadSingleStack(syncStackSource);
                        if (!SkipNormalStackAssertion)
                        {
                            Assert.Equal(Labels(Sync), syncOutput.ScenarioFrames);
                        }

                        var stitchedStackSource = new MutableTraceEventStackSource(traceLog)
                        {
                            OnlyManagedCodeStacks = OnlyManagedCodeStacks,
                        };
                        var stitchedComputer = new SampleProfilerThreadTimeComputer(traceLog, symbolReader)
                        {
                            EnableAsyncCallStacks = true,
                            IncludeEventSourceEvents = IncludeEventSourceEvents,
                            GroupByStartStopActivity = GroupByStartStopActivity,
                        };
                        stitchedComputer.AsyncCallStackTransforms.EnableParentContexts =
                            EnableParentContexts;
                        stitchedComputer.AsyncCallStackTransforms.EnableFrameAnnotations =
                            EnableFrameAnnotations;
                        ConfigurePresentation(stitchedComputer.AsyncCallStackTransforms, stitchedComputer.AsyncProfilerCallStackTransformPipeline,
                            enableNormalization: EnableNormalization, enableInfrastructureCleanup: EnableInfrastructureCleanup);
                        if (AssertTransformContext != null)
                        {
                            stitchedComputer.AsyncProfilerCallStackTransformPipeline.Add(context => AssertTransformContext(context));
                        }
                        stitchedComputer.GenerateThreadTimeStacks(stitchedStackSource);
                        if (VerifySampleWeights)
                        {
                            var syncWeights = new List<KeyValuePair<double, float>>();
                            var stitchedWeights = new List<KeyValuePair<double, float>>();
                            syncStackSource.ForEach(sample =>
                                syncWeights.Add(new KeyValuePair<double, float>(sample.TimeRelativeMSec, sample.Metric)));
                            stitchedStackSource.ForEach(sample =>
                                stitchedWeights.Add(new KeyValuePair<double, float>(sample.TimeRelativeMSec, sample.Metric)));
                            Assert.Equal(syncWeights, stitchedWeights);
                        }
                        if (VerifyComputerAndIndexLifecycle)
                        {
                            Assert.Throws<InvalidOperationException>(() =>
                                stitchedComputer.GenerateThreadTimeStacks(new MutableTraceEventStackSource(traceLog)));
                            Assert.True(traceLog.IsAsyncProfilerCallStacksLoaded);
                            Assert.True(traceLog.ReleaseAsyncProfilerCallStacks());
                            Assert.False(traceLog.IsAsyncProfilerCallStacksLoaded);
                        }
                        EmittedStack stitchedOutput = ReadSingleStack(stitchedStackSource);
                        Assert.Equal(Labels(ExpectedStitched), stitchedOutput.ScenarioFrames);
                        AssertStitchedStackSource?.Invoke(stitchedStackSource);
                        AssertOutputs?.Invoke(syncOutput, stitchedOutput);
                        AssertPublicApi?.Invoke(traceLog, etlxPath);
                        if (RuntimeProfileForModule == null && VerifyPublicBuilder)
                        {
                            AssertPublicBuilderEquivalence(traceLog);
                        }

                        Assert.Equal(syncOutput.RootFrames, stitchedOutput.RootFrames);
                        Assert.True(stitchedComputer.AsyncCallStacksActive);
                        (ExpectedDiagnostics ?? new CallStackDiagnosticsExpectation()).Assert(stitchedComputer.AsyncProfilerCallStackDiagnostics);

                        if (VerifyRemovingParentContextRemovesPresentation)
                        {
                            var filteredContextStackSource =
                                new MutableTraceEventStackSource(traceLog);
                            var filteredContextComputer =
                                new SampleProfilerThreadTimeComputer(
                                    traceLog, symbolReader)
                                {
                                    EnableAsyncCallStacks = true,
                                    IncludeEventSourceEvents = IncludeEventSourceEvents,
                                    GroupByStartStopActivity = GroupByStartStopActivity,
                                };
                            filteredContextComputer.AsyncCallStackTransforms.EnableParentContexts = true;
                            filteredContextComputer.AsyncCallStackTransforms.Add(context =>
                            {
                                for (int i = context.Frames.Count - 1; i >= 0; i--)
                                {
                                    if (context.Frames[i].IsParentContext)
                                    {
                                        context.Frames.RemoveAt(i);
                                    }
                                }
                            });
                            filteredContextComputer.GenerateThreadTimeStacks(
                                filteredContextStackSource);

                            EmittedStack filteredContextOutput =
                                ReadSingleStack(filteredContextStackSource);
                            var parentKeys = new HashSet<string>(
                                ContextParent.Frames.Select(frame => frame.Key));
                            Assert.Equal(
                                Labels(ExpectedStitched.Where(
                                    frame => !parentKeys.Contains(frame.Key))),
                                filteredContextOutput.ScenarioFrames);
                            Assert.DoesNotContain(
                                "[Async resumed parent context]",
                                filteredContextOutput.GroupingFrames);
                        }

                        if (VerifyThreadTimeComputer)
                        {
                            var threadTimeStackSource = new MutableTraceEventStackSource(traceLog)
                            {
                                OnlyManagedCodeStacks = OnlyManagedCodeStacks,
                            };
#pragma warning disable CS0618 // ThreadTimeStackComputer is intentionally experimental.
                            var threadTimeComputer = new ThreadTimeStackComputer(
                                traceLog, symbolReader)
#pragma warning restore CS0618
                            {
                                EnableAsyncCallStacks = true,
                                GroupByStartStopActivity = GroupByStartStopActivity,
                                IncludeEventSourceEvents = false,
                            };
                            threadTimeComputer.AsyncCallStackTransforms.EnableParentContexts = EnableParentContexts;
                            threadTimeComputer.AsyncCallStackTransforms.EnableFrameAnnotations = EnableFrameAnnotations;
                            ConfigurePresentation(threadTimeComputer.AsyncCallStackTransforms, threadTimeComputer.AsyncProfilerCallStackTransformPipeline,
                                enableNormalization: EnableNormalization, enableInfrastructureCleanup: EnableInfrastructureCleanup);
                            threadTimeComputer.GenerateThreadTimeStacks(threadTimeStackSource);
                            AssertThreadTimeStack(
                                threadTimeStackSource, "CPU_TIME", Labels(ExpectedStitched));
                            AssertThreadTimeStack(
                                threadTimeStackSource, "BLOCKED_TIME", Labels(ExpectedStitched));
                            Assert.True(threadTimeComputer.AsyncCallStacksActive);
                        }

                        if (ExpectedWithoutInfrastructureCleanup != null)
                        {
                            var defaultStackSource = new MutableTraceEventStackSource(traceLog);
                            var defaultComputer = new SampleProfilerThreadTimeComputer(
                                traceLog, symbolReader)
                            {
                                EnableAsyncCallStacks = true,
                                IncludeEventSourceEvents = IncludeEventSourceEvents,
                                GroupByStartStopActivity = GroupByStartStopActivity,
                            };
                            defaultComputer.AsyncCallStackTransforms.EnableParentContexts = EnableParentContexts;
                            defaultComputer.AsyncCallStackTransforms.EnableFrameAnnotations = EnableFrameAnnotations;
                            ConfigurePresentation(defaultComputer.AsyncCallStackTransforms, defaultComputer.AsyncProfilerCallStackTransformPipeline,
                                enableNormalization: EnableNormalization, enableInfrastructureCleanup: false);
                            defaultComputer.GenerateThreadTimeStacks(defaultStackSource);
                            Assert.Equal(Labels(ExpectedWithoutInfrastructureCleanup),
                                ReadSingleStack(defaultStackSource).ScenarioFrames);
                            Assert.Equal(0, defaultComputer.AsyncProfilerCallStackDiagnostics.AsyncInfrastructureFramesCollapsed);
                            Assert.Equal(0, defaultComputer.AsyncProfilerCallStackDiagnostics.RuntimeResumptionBridgeFramesCollapsed);
                        }

                        if (ExpectedWithoutNormalization != null)
                        {
                            var structuralStackSource = new MutableTraceEventStackSource(traceLog);
                            var structuralComputer = new SampleProfilerThreadTimeComputer(
                                traceLog, symbolReader)
                            {
                                EnableAsyncCallStacks = true,
                                IncludeEventSourceEvents = IncludeEventSourceEvents,
                                GroupByStartStopActivity = GroupByStartStopActivity,
                            };
                            ConfigurePresentation(structuralComputer.AsyncCallStackTransforms, structuralComputer.AsyncProfilerCallStackTransformPipeline,
                                enableNormalization: false, enableInfrastructureCleanup: false);
                            structuralComputer.GenerateThreadTimeStacks(structuralStackSource);
                            EmittedStack structuralOutput = ReadSingleStack(structuralStackSource);
                            Assert.Equal(
                                Labels(ExpectedWithoutNormalization),
                                structuralOutput.ScenarioFrames);
                            Assert.Equal(syncOutput.RootFrames, structuralOutput.RootFrames);
                            Assert.Equal(
                                0,
                                structuralComputer.AsyncProfilerCallStackDiagnostics.V1SynchronousMoveNextFramesNormalized);
                            Assert.Equal(
                                0,
                                structuralComputer.AsyncProfilerCallStackDiagnostics.V1SynchronousStartupFramesCollapsed);
                            Assert.Equal(
                                0,
                                structuralComputer.AsyncProfilerCallStackDiagnostics.V2CompletionHandoffFramesCollapsed);
                            Assert.Equal(
                                0,
                                structuralComputer.AsyncProfilerCallStackDiagnostics.AsyncReentrantHandoffFramesCollapsed);
                        }

                        if (VerifyComputerAndIndexLifecycle)
                        {
                            Assert.Equal(
                                expectedSegments.Length,
                                new AsyncProfilerCallStackComputer(traceLog).GetCallStacks(ProcessId, OsThreadId, asyncProbeQpc).Count);
                            Assert.True(traceLog.IsAsyncProfilerCallStacksLoaded);
                        }

                        if (FilteredOut != null && FilteredOut.Length != 0)
                        {
                            var filteredStackSource = new MutableTraceEventStackSource(traceLog);
                            var filteredComputer = new SampleProfilerThreadTimeComputer(
                                traceLog, symbolReader)
                            {
                                EnableAsyncCallStacks = true,
                                IncludeEventSourceEvents = IncludeEventSourceEvents,
                                GroupByStartStopActivity = GroupByStartStopActivity,
                            };
                            filteredComputer.AsyncCallStackTransforms.Add(context =>
                            {
                                for (int i = context.Frames.Count - 1; i >= 0; i--)
                                {
                                    if (!IncludeFilteredFrame(traceLog, context.Frames[i]))
                                    {
                                        context.Frames.RemoveAt(i);
                                    }
                                }
                            });
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

            public void AssertWithoutAsyncIndex()
            {
                string path = Path.Combine(Path.GetTempPath(), $"asyncprofiler_public_{Guid.NewGuid():N}.nettrace");
                string etlxPath = null;
                try
                {
                    File.WriteAllBytes(path, BuildNettrace(includeAsyncData: false));
                    etlxPath = TraceLog.CreateFromEventPipeDataFile(path);
                    using (var log = new TraceLog(etlxPath))
                    {
                        Assert.Null(log.AsyncProfilerCallStacks);
                        var query = new AsyncProfilerCallStackComputer(log);
                        var output = new MutableTraceEventStackSource(log);
                        var builder = new AsyncProfilerCallStackSourceBuilder(output);
                        builder.Transforms.Add(context => throw new InvalidOperationException("Fallback must not run transforms."));
                        foreach (TraceEvent data in log.Events.Where(e => e.CallStackIndex() != CallStackIndex.Invalid))
                        {
                            Assert.Empty(query.GetCallStacks(data));
                            Assert.Equal(output.GetCallStackThread(data.CallStackIndex(), data.Thread()), builder.GetCallStack(data));
                        }
                    }
                }
                finally
                {
                    File.Delete(path);
                    if (etlxPath != null)
                    {
                        File.Delete(etlxPath);
                    }
                }
            }

            private void AssertPublicBuilderEquivalence(TraceLog log)
            {
                var output = new MutableTraceEventStackSource(log) { OnlyManagedCodeStacks = OnlyManagedCodeStacks };
                var builder = new AsyncProfilerCallStackSourceBuilder(output);
                builder.Transforms.EnableNormalization = EnableNormalization;
                builder.Transforms.EnableInfrastructureCleanup = EnableInfrastructureCleanup;
                builder.Transforms.EnableFrameAnnotations = EnableFrameAnnotations;
                builder.Transforms.EnableParentContexts = EnableParentContexts;
                int count = 0;
                foreach (TraceEvent data in log.Events)
                {
#pragma warning disable CS0618
                    long timestampQpc = data.TimeStampQPC;
#pragma warning restore CS0618
                    if (timestampQpc != SampleQpc && timestampQpc != SampleQpc + 1)
                    {
                        continue;
                    }
                    var stack = builder.GetCallStack(data);
                    if (!SampleHasStack)
                    {
                        Assert.Equal(StackSourceCallStackIndex.Invalid, stack);
                    }
                    else
                    {
                        Assert.Equal(Labels(ExpectedStitched), ReadStack(output, stack).ScenarioFrames);
                        Assert.True(data.TryGetCallStackIndex(out var physicalStack, out long userModeTimeStampQPC));
                        Assert.Equal(stack, builder.GetCallStack(physicalStack, userModeTimeStampQPC));
                    }
                    count++;
                }
                Assert.Equal(2, count);
                Assert.Equal(0, output.SampleIndexLimit);
            }

            private byte[] BuildNettrace(bool includeAsyncData = true)
            {
                Dictionary<string, ModuleMapping> mappings = CreateModuleMappings();
                Dictionary<string, ulong> addresses = _addresses = AssignAddresses(mappings);
                var asyncBufferBuilder = new AsyncProfilerBufferBuilder(OsThreadId, 0x0BADF00D, StartQpc)
                    .Armed(StartQpc);
                AsyncSegment[] segments = GetAsyncSegments();
                if (ContextParent != null)
                {
                    ulong[] ownerFrameAddresses =
                        ContextParent.Frames.Select(frame => addresses[frame.Key]).ToArray();
                    if (ContextParent.Kind == AsyncCallstackKind.RuntimeAsync)
                    {
                        asyncBufferBuilder
                            .ResumeRuntimeStack(StartQpc + 1, dispatcher: 99, ownerFrameAddresses);
                    }
                    else
                    {
                        asyncBufferBuilder
                            .ResumeStack(
                                StartQpc + 1,
                                dispatcher: 99,
                                ownerFrameAddresses,
                                ContextParent.States ?? new int[ownerFrameAddresses.Length]);
                    }
                    for (int i = 0; i < ParentMethodCompletions; i++)
                    {
                        asyncBufferBuilder.Method(
                            ContextParent.Kind == AsyncCallstackKind.RuntimeAsync
                                ? AsyncEventID.CompleteRuntimeAsyncMethod : AsyncEventID.CompleteStateMachineAsyncMethod,
                            StartQpc + 2);
                    }
                    if (ParentExceptionCompletions > 0)
                    {
                        asyncBufferBuilder.Unwind(
                            ContextParent.Kind == AsyncCallstackKind.RuntimeAsync
                                ? AsyncEventID.UnwindRuntimeAsyncException : AsyncEventID.UnwindStateMachineAsyncException,
                            StartQpc + 3, (uint)ParentExceptionCompletions);
                    }
                    asyncBufferBuilder.CreateContext(
                        segments[0].Kind == AsyncCallstackKind.RuntimeAsync
                            ? AsyncEventID.CreateRuntimeAsyncContext : AsyncEventID.CreateStateMachineAsyncContext,
                        StartQpc + 5, parent: 99, dispatcher: 1);
                    if (ContextParent.Kind == AsyncCallstackKind.RuntimeAsync)
                    {
                        asyncBufferBuilder.SuspendRuntime(StartQpc + 8);
                    }
                    else
                    {
                        asyncBufferBuilder.Suspend(StartQpc + 8);
                    }
                }
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
                    for (int completed = 0; completed < segment.MethodCompletions; completed++)
                    {
                        asyncBufferBuilder.Method(segment.Kind == AsyncCallstackKind.RuntimeAsync
                            ? AsyncEventID.CompleteRuntimeAsyncMethod : AsyncEventID.CompleteStateMachineAsyncMethod,
                            AsyncStartQpc + i);
                    }
                    if (segment.ExceptionCompletions > 0)
                    {
                        asyncBufferBuilder.Unwind(segment.Kind == AsyncCallstackKind.RuntimeAsync
                            ? AsyncEventID.UnwindRuntimeAsyncException : AsyncEventID.UnwindStateMachineAsyncException,
                            AsyncStartQpc + i, (uint)segment.ExceptionCompletions);
                    }
                }

                AsyncCallstackKind leafKind = segments[segments.Length - 1].Kind;
                for (int i = 0; i < WrapperResets; i++)
                {
                    asyncBufferBuilder.ContextNoPayload(AsyncEventID.ResetAsyncContinuationWrapperIndex, StartQpc + 20);
                }
                for (int i = 0; i < MethodCompletions; i++)
                {
                    asyncBufferBuilder.Method(leafKind == AsyncCallstackKind.RuntimeAsync
                        ? AsyncEventID.CompleteRuntimeAsyncMethod : AsyncEventID.CompleteStateMachineAsyncMethod, MethodCompletionQpc);
                }
                if (ExceptionCompletions > 0)
                {
                    asyncBufferBuilder.Unwind(
                        leafKind == AsyncCallstackKind.RuntimeAsync
                            ? AsyncEventID.UnwindRuntimeAsyncException : AsyncEventID.UnwindStateMachineAsyncException,
                        StartQpc + 20, (uint)ExceptionCompletions);
                }
                if (IncludeCompletedInnerActivation)
                {
                    asyncBufferBuilder.ResumeRuntimeStack(StartQpc + 21, dispatcher: 100, new ulong[] { 0 })
                        .SuspendRuntime(StartQpc + 22);
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
                var taskScheduledMetadata = new EventMetadata(
                    5, TplEtwProviderTraceEventParser.ProviderName, "TaskScheduled", 7)
                {
                    ProviderId = TplEtwProviderTraceEventParser.ProviderGuid,
                };
                var taskStartedMetadata = new EventMetadata(
                    6, TplEtwProviderTraceEventParser.ProviderName, "TaskExecuteStart", 8)
                {
                    ProviderId = TplEtwProviderTraceEventParser.ProviderGuid,
                };
                var activityStartMetadata = new EventMetadata(
                    7, "Microsoft-Diagnostics-ActivityTracking", "RequestStart", 1)
                {
                    ProviderId = new Guid("3b268b3d-903f-5835-c77e-790d518a26c4"),
                    OpCode = (byte)EventOpcode.Start,
                };
                var universalCpuMetadata = new EventMetadata(
                    8, UniversalEventsTraceEventParser.ProviderName, "cpu", 1,
                    new MetadataParameter("Value", MetadataTypeCode.VarUInt))
                {
                    ProviderId = UniversalEventsTraceEventParser.ProviderGuid,
                };
                var universalCSwitchMetadata = new EventMetadata(
                    9, UniversalEventsTraceEventParser.ProviderName, "cswitch", 2,
                    new MetadataParameter("Value", MetadataTypeCode.VarUInt))
                {
                    ProviderId = UniversalEventsTraceEventParser.ProviderGuid,
                };

                var writer = new EventPipeFixtureWriter();
                writer.WriteHeadersWithNonZeroSyncTime();
                writer.WriteMetadataBlock(
                    asyncMetadata,
                    sampleMetadata,
                    mappingMetadata,
                    symbolMetadata,
                    taskScheduledMetadata,
                    taskStartedMetadata,
                    activityStartMetadata,
                    universalCpuMetadata,
                    universalCSwitchMetadata);
                if (IncludeCoreLibUnloadRundown)
                {
                    writer.WriteMetadataBlock(
                        new EventMetadata(10, ClrTraceEventParser.ProviderName, "MethodUnloadVerbose", 144)
                        {
                            ProviderId = ClrTraceEventParser.ProviderGuid, Version = 2,
                        },
                        new EventMetadata(11, ClrTraceEventParser.ProviderName, "ModuleUnload", 153)
                        {
                            ProviderId = ClrTraceEventParser.ProviderGuid, Version = 2,
                        },
                        new EventMetadata(12, Microsoft.Diagnostics.Tracing.Parsers.Clr.ClrRundownTraceEventParser.ProviderName,
                            "MethodDCStopVerbose", 144)
                        {
                            ProviderId = Microsoft.Diagnostics.Tracing.Parsers.Clr.ClrRundownTraceEventParser.ProviderGuid,
                            Version = 2,
                        },
                        new EventMetadata(13, Microsoft.Diagnostics.Tracing.Parsers.Clr.ClrRundownTraceEventParser.ProviderName,
                            "ModuleDCStop", 154)
                        {
                            ProviderId = Microsoft.Diagnostics.Tracing.Parsers.Clr.ClrRundownTraceEventParser.ProviderGuid,
                            Version = 2,
                        });
                }
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
                        if (IncludeCoreLibUnloadRundown &&
                            mapping.Name == AsyncProfilerCallStackStitchBoundary.HostModuleName)
                        {
                            continue;
                        }
                        w.WriteEventBlob(EventOptions(3, sequence++, StartQpc + 1), p =>
                            WriteProcessMappingPayload(p, mapping.Id, mapping.StartAddress, mapping.EndAddress, mapping.Name));
                    }

                    ulong symbolId = 1;
                    foreach (Frame frame in InputFrames())
                    {
                        if (frame == Frame.Zero || frame == OmitSymbolFor)
                        {
                            continue;
                        }
                        if (IncludeCoreLibUnloadRundown &&
                            frame.ModuleName == AsyncProfilerCallStackStitchBoundary.HostModuleName)
                        {
                            continue;
                        }

                        ulong mappingId = mappings[frame.ModuleName].Id;
                        ulong address = addresses[frame.Key];
                        w.WriteEventBlob(EventOptions(4, sequence++, StartQpc + 2), p =>
                            WriteProcessSymbolPayload(p, symbolId++, mappingId, address, frame.RawSymbolName));
                    }

                    Guid activityId = new Guid("9f7e55d2-694f-4cc2-a68d-df35efca1c81");
                    if (IncludeStartStopActivity)
                    {
                        w.WriteEventBlob(
                            EventOptions(7, sequence++, StartQpc + 3, activityId: activityId),
                            p => { });
                    }
                    if (IncludeTplActivity)
                    {
                        w.WriteEventBlob(
                            EventOptions(5, sequence++, StartQpc + 4, stackId: 1, activityId: activityId),
                            p =>
                            {
                                p.Write(1);
                                p.Write(0);
                                p.Write(42);
                                p.Write(0);
                                p.Write(0);
                            });
                        w.WriteEventBlob(
                            EventOptions(6, sequence++, StartQpc + 5, stackId: 1, activityId: activityId),
                            p =>
                            {
                                p.Write(1);
                                p.Write(0);
                                p.Write(42);
                            });
                    }

                    // Thread-time computation emits a CPU sample when the following sample arrives.
                    int stackId = SampleHasStack ? 1 : 0;
                    if (VerifyThreadTimeComputer)
                    {
                        w.WriteEventBlob(EventOptions(9, sequence++, SampleQpc - 3, stackId),
                            p => p.WriteVarUInt(1));
                        w.WriteEventBlob(EventOptions(8, sequence++, SampleQpc - 2, stackId),
                            p => p.WriteVarUInt(1));
                        w.WriteEventBlob(EventOptions(8, sequence++, SampleQpc - 1, stackId),
                            p => p.WriteVarUInt(1));
                    }
                    w.WriteEventBlob(EventOptions(2, sequence++, SampleQpc, stackId),
                        p => p.Write((int)ClrThreadSampleType.Managed));
                    w.WriteEventBlob(EventOptions(2, sequence++, SampleQpc + 1, stackId),
                        p => p.Write((int)ClrThreadSampleType.Managed));
                    if (includeAsyncData)
                    {
                        w.WriteEventBlob(EventOptions(1, sequence, StartQpc + 50),
                            p => p.Write(AsyncEventsPayload(asyncBuffer)));
                    }
                    if (foreignAsyncBuffer != null)
                    {
                        w.WriteEventBlob(EventOptions(1, ++sequence, StartQpc + 51, threadIndex: ForeignThreadStreamIndex),
                            p => p.Write(AsyncEventsPayload(foreignAsyncBuffer)));
                    }
                    if (IncludeCoreLibUnloadRundown)
                    {
                        Frame[] coreLib = InputFrames().Where(frame =>
                            frame.ModuleName == AsyncProfilerCallStackStitchBoundary.HostModuleName).ToArray();
                        foreach (Frame frame in coreLib.Where(ResolvedBeforeCoreLibUnload))
                        {
                            w.WriteEventBlob(EventOptions(10, ++sequence, StartQpc + 60),
                                p => WriteCoreLibMethod(p, frame, addresses[frame.Key]));
                        }
                        w.WriteEventBlob(EventOptions(11, ++sequence, StartQpc + 61), WriteCoreLibModule);
                        foreach (Frame frame in coreLib.Where(frame => !ResolvedBeforeCoreLibUnload(frame)))
                        {
                            w.WriteEventBlob(EventOptions(12, ++sequence, StartQpc + 62),
                                p => WriteCoreLibMethod(p, frame, addresses[frame.Key]));
                        }
                        w.WriteEventBlob(EventOptions(13, ++sequence, StartQpc + 63), WriteCoreLibModule);
                    }
                });
                writer.WriteEndBlock();
                return writer.ToArray();
            }

            private bool ResolvedBeforeCoreLibUnload(Frame frame) =>
                frame == CoreLibMethodBeforeUnload || frame.Kind == FrameKind.V2Wrapper || frame == Frame.ThreadPoolDispatch ||
                frame.SymbolName == "System.Threading.Tasks.Task.RunContinuations";

            private static void WriteCoreLibMethod(BinaryWriter writer, Frame frame, ulong address)
            {
                int signature = frame.SymbolName.IndexOf('(');
                int methodEnd = signature < 0 ? frame.SymbolName.Length : signature;
                int typeEnd = frame.SymbolName.LastIndexOf('.', methodEnd - 1);
                writer.Write(address);
                writer.Write(0xcafeUL); // ModuleID is shared by unload and rundown.
                writer.Write(address);
                writer.Write(0x20);
                writer.Write(0x06000000 | (int)(address >> 5));
                writer.Write(8); // MethodFlags.Jitted
                writer.WriteNullTerminatedUTF16String(frame.SymbolName.Substring(0, typeEnd));
                writer.WriteNullTerminatedUTF16String(frame.SymbolName.Substring(typeEnd + 1, methodEnd - typeEnd - 1));
                writer.WriteNullTerminatedUTF16String(signature < 0 ? "" : frame.SymbolName.Substring(signature));
                writer.Write((ushort)1);
                writer.Write(0L);
            }

            private static void WriteCoreLibModule(BinaryWriter writer)
            {
                writer.Write(0xcafeUL);
                writer.Write(0xbabeUL);
                writer.Write(0);
                writer.Write(0);
                writer.WriteNullTerminatedUTF16String(@"C:\runtime\System.Private.CoreLib.dll");
                writer.WriteNullTerminatedUTF16String("");
                writer.Write((ushort)1);
                writer.Write(new Guid("45b6c23e-36c5-4926-b688-817c8a9b12b1").ToByteArray());
                writer.Write(1);
                writer.WriteNullTerminatedUTF16String("System.Private.CoreLib.pdb");
                writer.Write(Guid.Empty.ToByteArray());
                writer.Write(0);
                writer.WriteNullTerminatedUTF16String("");
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
                IEnumerable<Frame> frames =
                    Sync.Concat(GetAsyncSegments().SelectMany(segment => segment.Frames));
                if (ContextParent != null)
                {
                    frames = frames.Concat(ContextParent.Frames);
                }
                foreach (Frame frame in frames)
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
                int matchingSampleCount = 0;
                stackSource.ForEach(sample =>
                {
                    sampleCount++;
                    EmittedStack candidate = ReadStack(stackSource, sample.StackIndex);
                    if (!AllowSupplementalSamples || candidate.ScenarioFrames.Length != 0)
                    {
                        matchingSampleCount++;
                        result = candidate;
                    }
                });
                if (AllowSupplementalSamples)
                {
                    Assert.True(matchingSampleCount > 0);
                }
                else
                {
                    Assert.Equal(1, sampleCount);
                }
                return result;
            }

            private void AssertThreadTimeStack(
                MutableTraceEventStackSource stackSource,
                string metricFrame,
                string[] expectedScenarioFrames)
            {
                bool found = false;
                stackSource.ForEach(sample =>
                {
                    EmittedStack stack = ReadStack(stackSource, sample.StackIndex);
                    if (stack.GroupingFrames.Contains(metricFrame) &&
                        stack.ScenarioFrames.SequenceEqual(expectedScenarioFrames))
                    {
                        if (IncludeStartStopActivity)
                        {
                            Assert.Contains(stack.RootFrames, name =>
                                name.IndexOf("Activity Request", StringComparison.Ordinal) >= 0);
                        }
                        found = true;
                    }
                });
                Assert.True(found, $"No {metricFrame} sample was emitted.");
            }

            private EmittedStack ReadStack(MutableTraceEventStackSource stackSource, StackSourceCallStackIndex stackIndex)
            {
                var scenarioFrames = new List<string>();
                var groupingFrames = new List<string>();
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
                        Frame frame = DeclaredFrames()
                            .Where(candidate =>
                                candidate != Frame.Zero &&
                                (name.Contains(candidate.SymbolName) || name.Contains(candidate.Label)))
                            .OrderByDescending(candidate =>
                                Math.Max(candidate.SymbolName.Length, candidate.Label.Length))
                            .FirstOrDefault();
                        if (frame == null && OmitSymbolFor != null)
                        {
                            CodeAddressIndex address = stackSource.GetFrameCodeAddress(
                                stackSource.GetFrameIndex(stackIndex));
                            if (address != CodeAddressIndex.Invalid &&
                                stackSource.TraceLog.CodeAddresses[address].Address == _addresses[OmitSymbolFor.Key])
                            {
                                Assert.Equal(MethodIndex.Invalid, stackSource.TraceLog.CodeAddresses.MethodIndex(address));
                                Assert.Equal(OmitSymbolFor.ModuleName, stackSource.TraceLog.CodeAddresses[address].ModuleName, ignoreCase: true);
                                frame = OmitSymbolFor;
                            }
                        }
                        if (frame != null)
                        {
                            scenarioFrames.Add(frame.Label);
                        }
                        else if (!string.Equals(name, "BROKEN", StringComparison.Ordinal))
                        {
                            groupingFrames.Add(name);
                        }
                    }
                    stackIndex = stackSource.GetCallerIndex(stackIndex);
                }

                return new EmittedStack(
                    scenarioFrames.ToArray(),
                    groupingFrames.ToArray(),
                    rootFrames.ToArray());
            }

            private string[] AsyncFrameLabels(
                TraceLog traceLog, AsyncProfilerCallStackFrames frames, AsyncSegment expectedSegment)
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
                    else if (expected.Key == OmitSymbolFor?.Key)
                    {
                        Assert.NotEqual(CodeAddressIndex.Invalid, codeAddress);
                        Assert.Equal(MethodIndex.Invalid, traceLog.CodeAddresses.MethodIndex(codeAddress));
                        Assert.Equal(_addresses[expected.Key], traceLog.CodeAddresses[codeAddress].Address);
                        Assert.Equal(expected.ModuleName, traceLog.CodeAddresses[codeAddress].ModuleName, ignoreCase: true);
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
                    .Concat(ContextParent?.Frames ?? Array.Empty<Frame>())
                    .Concat(ExpectedStitched);

            private IEnumerable<Frame> ExpectedFilteredFrames()
            {
                var filteredKeys = new HashSet<string>(FilteredOut.Select(frame => frame.Key));
                return ExpectedStitched.Where(frame => !filteredKeys.Contains(frame.Key));
            }

            private bool IncludeFilteredFrame(TraceLog traceLog, AsyncProfilerCallStackFrame stitchedFrame)
            {
                if (stitchedFrame.CodeAddress == CodeAddressIndex.Invalid)
                {
                    return true;
                }

                string methodName = traceLog.CodeAddresses[stitchedFrame.CodeAddress].FullMethodName;
                return !(FilteredOut ?? Array.Empty<Frame>()).Any(frame => methodName.Contains(frame.SymbolName));
            }

            private void ConfigurePresentation(
                AsyncCallStackTransforms transforms, AsyncProfilerCallStackTransformPipeline pipeline,
                bool enableNormalization, bool enableInfrastructureCleanup)
            {
                if (ExpectedRuntimeProfile != null)
                {
                    pipeline.Add(context =>
                    {
                        Assert.Same(ExpectedRuntimeProfile, context.RuntimeProfile);
                        var standalone = new AsyncProfilerCallStackTransformState(context.TraceLog, context.Segments,
                            context.TimestampQpc, context.ClassifyBoundary, new AsyncProfilerCallStackDiagnostics(),
                            new List<AsyncProfilerStitchedFrame>(context.Frames));
                        Assert.Same(ExpectedRuntimeProfile, standalone.RuntimeProfile);
                    });
                }
                transforms.EnableNormalization = RuntimeProfileForModule == null && enableNormalization;
                transforms.EnableInfrastructureCleanup = RuntimeProfileForModule == null && enableInfrastructureCleanup;
                if (RuntimeProfileForModule == null)
                {
                    return;
                }

                // Install a hypothetical test profile after stitching, without adding fake production versions.
                var presentation = new AsyncProfilerCallStackTransformPipeline
                {
                    EnableNormalization = enableNormalization,
                    EnableInfrastructureCleanup = enableInfrastructureCleanup,
                };
                pipeline.Add(context =>
                {
                    Func<CodeAddressIndex, AsyncProfilerRuntimeRuleProfile> runtimeProfileResolver = ca =>
                        RuntimeProfileForModule(ca == CodeAddressIndex.Invalid
                            ? null : context.TraceLog.CodeAddresses.ModuleFile(ca));
                    for (int i = 0; i < context.Frames.Count; i++)
                    {
                        AsyncProfilerStitchedFrame frame = context.Frames[i];
                        if (frame.Origin != AsyncProfilerStitchedFrameOrigin.Sync)
                        {
                            continue;
                        }
                        string name = frame.CodeAddress == CodeAddressIndex.Invalid
                            ? null : context.TraceLog.CodeAddresses[frame.CodeAddress].FullMethodName;
                        AsyncProfilerStitchSyncFrameKind kind = runtimeProfileResolver(frame.CodeAddress).ClassifySynchronousFrame(
                            name, context.GetHostModuleOwner(frame.CodeAddress) != ModuleFileIndex.Invalid);
                        context.Frames[i] = AsyncProfilerStitchedFrame.CreateSync(
                            new AsyncProfilerStitchSyncFrame(frame.CodeAddress, frame.Method, kind, frame.BoxedStateMachineMethod),
                            frame.Context);
                    }
                    presentation.ApplyInPlace(new AsyncProfilerCallStackTransformState(context.TraceLog, context.Segments,
                        context.TimestampQpc, context.ClassifyBoundary, context.Diagnostics, context.Frames, runtimeProfileResolver));
                });
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
                if (ContextParent != null)
                {
                    Assert.NotNull(ContextParent.Frames);
                    Assert.NotEmpty(ContextParent.Frames);
                }
                Assert.NotNull(ExpectedStitched);
                Assert.True(AsyncStartQpc < AsyncEndQpc);

                var inputKeys = new HashSet<string>(
                    Sync.Concat(segments.SelectMany(segment => segment.Frames))
                        .Concat(ContextParent?.Frames ?? Array.Empty<Frame>())
                        .Select(frame => frame.Key));
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
                int metadataId,
                int sequence,
                long timestamp,
                int stackId = 0,
                long threadIndex = ThreadStreamIndex,
                Guid activityId = default)
            {
                return new WriteEventOptions
                {
                    MetadataId = metadataId,
                    ThreadIndexOrId = threadIndex,
                    CaptureThreadIndexOrId = threadIndex,
                    SequenceNumber = sequence,
                    StackId = stackId,
                    Timestamp = timestamp,
                    ActivityId = activityId,
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

            private Frame(string moduleName, string symbolName, string label, FrameKind kind = FrameKind.Ordinary,
                byte wrapperIndex = 0, string rawSymbolName = null)
            {
                ModuleName = moduleName;
                SymbolName = symbolName;
                RawSymbolName = rawSymbolName ?? symbolName;
                Label = label;
                Kind = kind;
                WrapperIndex = wrapperIndex;
            }

            public string ModuleName { get; }
            public string SymbolName { get; }
            public string RawSymbolName { get; }
            public string Label { get; }
            public FrameKind Kind { get; }
            public byte WrapperIndex { get; }
            public string Key => ModuleName + "!" + SymbolName;

            public static readonly Frame Zero = new Frame(null, null, "?? (0x0)");

            public static Frame App(string name) => new Frame(ApplicationModule, name, name);

            public static Frame Library(string moduleName, string symbolName, string label) =>
                new Frame(moduleName, symbolName, label);

            public static Frame CoreLib(string symbolName, string label) =>
                new Frame(AsyncProfilerCallStackStitchBoundary.HostModuleName, symbolName, label);

            public static Frame PerfMap(string moduleName, string methodSignature, string label) =>
                new Frame(moduleName, methodSignature, label,
                    rawSymbolName: "instance void [" + moduleName + "] " + methodSignature + "[Optimized]");

            public Frame DispatcherFrames() =>
                new Frame(ModuleName, SymbolName, "[Async dispatcher frames]", Kind, WrapperIndex);

            public Frame Logical(string label, int? state = null)
            {
                Assert.True(
                    AsyncProfilerCallStackStitchBoundary.TryGetLogicalStateMachineMethodName(
                        AsyncProfilerCallStackStitchBoundary.NormalizeMethodName(SymbolName), out _, out string stateMachineSuffix));
                string ordinal = stateMachineSuffix.StartsWith("d__", StringComparison.Ordinal)
                    ? stateMachineSuffix.Substring(3)
                    : (stateMachineSuffix.StartsWith("d_", StringComparison.Ordinal)
                        ? stateMachineSuffix.Substring(2)
                        : stateMachineSuffix);
                return new Frame(
                    ModuleName,
                    SymbolName,
                    state.HasValue
                        ? label + " [ordinal " + ordinal + ", state " + state.Value + "]"
                        : label + " [ordinal " + ordinal + "]",
                    Kind,
                    WrapperIndex,
                    RawSymbolName);
            }

            public static Frame V1InlineInfrastructure(string level, string name) =>
                new Frame(AsyncProfilerCallStackStitchBoundary.HostModuleName, level + "." + name, level + "." + name);

            public static Frame V2Wrapper(int index) =>
                new Frame(AsyncProfilerCallStackStitchBoundary.HostModuleName,
                    "System.Runtime.CompilerServices.AsyncProfiler.ContinuationWrapper." +
                    AsyncProfilerCallStackStitchBoundary.ContinuationWrapperPrefix + index,
                    AsyncProfilerCallStackStitchBoundary.ContinuationWrapperPrefix + index,
                    FrameKind.V2Wrapper,
                    checked((byte)index));

            public static readonly Frame V2InstrumentedDispatch =
                new Frame(AsyncProfilerCallStackStitchBoundary.HostModuleName,
                    "System.Threading.Tasks.RuntimeAsyncTask." + AsyncProfilerCallStackStitchBoundary.InstrumentedDispatchContinuationsName,
                    AsyncProfilerCallStackStitchBoundary.InstrumentedDispatchContinuationsName);

            public static readonly Frame V2Dispatch =
                new Frame(AsyncProfilerCallStackStitchBoundary.HostModuleName,
                    "System.Threading.Tasks.RuntimeAsyncTask." + AsyncProfilerCallStackStitchBoundary.DispatchContinuationsName,
                    AsyncProfilerCallStackStitchBoundary.DispatchContinuationsName);

            public static readonly Frame V1MoveNextAsDispatcher =
                new Frame(AsyncProfilerCallStackStitchBoundary.HostModuleName,
                    "System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncProfilerAsyncStateMachineBox." +
                    AsyncProfilerCallStackStitchBoundary.MoveNextAsDispatcherName + "(Thread,Flags)",
                    AsyncProfilerCallStackStitchBoundary.MoveNextAsDispatcherName);

            public static readonly Frame ThreadPoolDispatch =
                new Frame(AsyncProfilerCallStackStitchBoundary.HostModuleName,
                    "System.Threading.ThreadPoolWorkQueue.Dispatch",
                    "ThreadPoolWorkQueue.Dispatch");

            public static readonly Frame WorkerThreadStart =
                new Frame(AsyncProfilerCallStackStitchBoundary.HostModuleName,
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
            public int MethodCompletions { get; set; }
            public int ExceptionCompletions { get; set; }

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
            public EmittedStack(string[] scenarioFrames, string[] groupingFrames, string[] rootFrames)
            {
                ScenarioFrames = scenarioFrames;
                GroupingFrames = groupingFrames;
                RootFrames = rootFrames;
            }

            public string[] ScenarioFrames { get; }
            public string[] GroupingFrames { get; }
            public string[] RootFrames { get; }
        }

        private sealed class CallStackDiagnosticsExpectation
        {
            public int SegmentsProcessed { get; set; }
            public int BoundariesNotFound { get; set; }
            public int IncompleteAsyncRootsNormalized { get; set; }
            public int V2TruncatedSuffixFallbackUsed { get; set; }
            public int AdjacencyMismatches { get; set; }
            public int V2PlumbingFramesCollapsed { get; set; }
            public int V1InfrastructureFramesCollapsed { get; set; }
            public int V1SynchronousMoveNextFramesNormalized { get; set; }
            public int V1ReentrantFramesCollapsed { get; set; }
            public int V1SynchronousStartupFramesCollapsed { get; set; }
            public int AsyncInfrastructureFramesCollapsed { get; set; }
            public int RuntimeResumptionBridgeFramesCollapsed { get; set; }
            public int ProfilerEnvelopeFramesCollapsed { get; set; }
            public int AsyncReentrantHandoffFramesCollapsed { get; set; }
            public int V2ReentrantHandoffFramesCollapsed { get; set; }
            public int V2CompletionHandoffFramesCollapsed { get; set; }
            public int V1InlineFallbackUsed { get; set; }
            public int V1CompletionTailUsed { get; set; }
            public int V1CompletionUnwindFramesCollapsed { get; set; }
            public int V2SyncLayoutUsed { get; set; }
            public int V2LeafWrapperDropped { get; set; }

            public void Assert(AsyncProfilerCallStackDiagnostics actual)
            {
                Xunit.Assert.Equal(SegmentsProcessed, actual.SegmentsProcessed);
                Xunit.Assert.Equal(BoundariesNotFound, actual.BoundariesNotFound);
                Xunit.Assert.Equal(IncompleteAsyncRootsNormalized, actual.IncompleteAsyncRootsNormalized);
                Xunit.Assert.Equal(V2TruncatedSuffixFallbackUsed, actual.V2TruncatedSuffixFallbackUsed);
                Xunit.Assert.Equal(AdjacencyMismatches, actual.AdjacencyMismatches);
                Xunit.Assert.Equal(V2PlumbingFramesCollapsed, actual.V2PlumbingFramesCollapsed);
                Xunit.Assert.Equal(V1InfrastructureFramesCollapsed, actual.V1InfrastructureFramesCollapsed);
                Xunit.Assert.Equal(V1SynchronousMoveNextFramesNormalized, actual.V1SynchronousMoveNextFramesNormalized);
                Xunit.Assert.Equal(V1ReentrantFramesCollapsed, actual.V1ReentrantFramesCollapsed);
                Xunit.Assert.Equal(V1SynchronousStartupFramesCollapsed, actual.V1SynchronousStartupFramesCollapsed);
                Xunit.Assert.Equal(AsyncInfrastructureFramesCollapsed, actual.AsyncInfrastructureFramesCollapsed);
                Xunit.Assert.Equal(RuntimeResumptionBridgeFramesCollapsed, actual.RuntimeResumptionBridgeFramesCollapsed);
                Xunit.Assert.Equal(ProfilerEnvelopeFramesCollapsed, actual.ProfilerEnvelopeFramesCollapsed);
                Xunit.Assert.Equal(AsyncReentrantHandoffFramesCollapsed, actual.AsyncReentrantHandoffFramesCollapsed);
                Xunit.Assert.Equal(V2ReentrantHandoffFramesCollapsed, actual.V2ReentrantHandoffFramesCollapsed);
                Xunit.Assert.Equal(V2CompletionHandoffFramesCollapsed, actual.V2CompletionHandoffFramesCollapsed);
                Xunit.Assert.Equal(V1InlineFallbackUsed, actual.V1InlineFallbackUsed);
                Xunit.Assert.Equal(V1CompletionTailUsed, actual.V1CompletionTailUsed);
                Xunit.Assert.Equal(V1CompletionUnwindFramesCollapsed, actual.V1CompletionUnwindFramesCollapsed);
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
