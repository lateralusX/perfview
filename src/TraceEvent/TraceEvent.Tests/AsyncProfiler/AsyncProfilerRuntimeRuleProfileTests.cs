// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

using Xunit;

namespace TraceEventTests
{
    /// <summary>Checks captured-runtime selection and ownership-sensitive frame classification.</summary>
    public class AsyncProfilerRuntimeRuleProfileTests
    {
        [Theory]
        [InlineData("11.0.26.12345", "11.0.0", RuntimeSku.CoreClr, true)]
        [InlineData(null, "11.0.0-preview.1.12345+abcdef", RuntimeSku.CoreClr, true)]
        [InlineData("", "11.0.0-dev Commit Hash: abcdef", RuntimeSku.CoreClr, true)]
        [InlineData("11.0.0.0", null, RuntimeSku.CoreClr, true)]
        [InlineData("11.0", "", RuntimeSku.CoreClr, true)]
        [InlineData(" 11.0.0 ", " 11.0.0+abcdef ", RuntimeSku.CoreClr, true)]
        [InlineData("invalid", "11.0.0", RuntimeSku.CoreClr, true)]
        [InlineData("11.0.0", "invalid", RuntimeSku.CoreClr, true)]
        [InlineData(null, null, RuntimeSku.CoreClr, false)]
        [InlineData("", "", RuntimeSku.CoreClr, false)]
        [InlineData("invalid", "not-a-version", RuntimeSku.CoreClr, false)]
        [InlineData("11", null, RuntimeSku.CoreClr, false)]
        [InlineData(null, "11.0garbage", RuntimeSku.CoreClr, false)]
        [InlineData(null, "111.0.0", RuntimeSku.CoreClr, false)]
        [InlineData(null, "12.0.0-preview.1", RuntimeSku.CoreClr, false)]
        [InlineData(null, "11.1.0", RuntimeSku.CoreClr, false)]
        [InlineData(null, "10.0.0", RuntimeSku.CoreClr, false)]
        [InlineData(null, "8.0.0", RuntimeSku.CoreClr, false)]
        [InlineData("12.0.0.0", "11.0.0", RuntimeSku.CoreClr, false)]
        [InlineData("11.0.0.0", "12.0.0", RuntimeSku.CoreClr, false)]
        [InlineData("11.1.0.0", "11.0.0", RuntimeSku.CoreClr, false)]
        [InlineData("11.0.0", null, RuntimeSku.None, false)]
        [InlineData("11.0.0", null, RuntimeSku.Mono, false)]
        [InlineData("11.0.0", null, RuntimeSku.DesktopClr, false)]
        [InlineData("11.0.0", null, RuntimeSku.CoreClr | RuntimeSku.Mono, false)]
        public void Select_UsesOnlySupportedCapturedVersionsAndFlavors(
            string fileVersion, string productVersion, RuntimeSku flavor, bool supported)
        {
            AsyncProfilerRuntimeRuleProfile profile = AsyncProfilerRuntimeRuleProfile.Select(fileVersion, productVersion, flavor);
            Assert.Same(supported ? AsyncProfilerRuntimeRuleProfile.CoreClr11 : AsyncProfilerRuntimeRuleProfile.Common, profile);
            Assert.Equal(supported ? "CoreCLR 11.0" : "Common", profile.Name);
        }

        [Theory]
        [InlineData("App!Scenario+<Run>d__1.MoveNext()", false, (int)AsyncProfilerStitchSyncFrameKind.V1StateMachineMoveNext)]
        [InlineData("System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start(!!0&)", true, (int)AsyncProfilerStitchSyncFrameKind.V1MethodBuilderStart)]
        [InlineData("System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start(!!0&)", false, (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData("Other.CoreLib!System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start()", true, (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData("System.Threading.Tasks.Task.RunContinuations()", true, (int)AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations)]
        [InlineData("System.Threading.Tasks.Task.RunContinuations()", false, (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData("Other.Namespace.Task.RunContinuations()", true, (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData("System.Runtime.CompilerServices.AsyncProfiler+ResumeAsyncContext.Resume()", true, (int)AsyncProfilerStitchSyncFrameKind.ProfilerContextResume)]
        [InlineData("System.Runtime.CompilerServices.AsyncProfiler+ResumeAsyncContext.Resume()", false, (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData("System.Runtime.CompilerServices.ValueTaskAwaiter.System.Runtime.CompilerServices.IStateMachineBoxAwareAwaiter.AwaitUnsafeOnCompleted(class System.Runtime.CompilerServices.IAsyncStateMachineBox)", true, (int)AsyncProfilerStitchSyncFrameKind.V1AwaiterRegistrationInfrastructure)]
        [InlineData("System.Runtime.CompilerServices.ValueTaskAwaiter`1[System.Int64].System.Runtime.CompilerServices.IStateMachineBoxAwareAwaiter.AwaitUnsafeOnCompleted(class System.Runtime.CompilerServices.IAsyncStateMachineBox)", true, (int)AsyncProfilerStitchSyncFrameKind.V1AwaiterRegistrationInfrastructure)]
        [InlineData("System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable+ConfiguredValueTaskAwaiter.System.Runtime.CompilerServices.IStateMachineBoxAwareAwaiter.AwaitUnsafeOnCompleted(class System.Runtime.CompilerServices.IAsyncStateMachineBox)", true, (int)AsyncProfilerStitchSyncFrameKind.V1AwaiterRegistrationInfrastructure)]
        [InlineData("System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable`1[System.Int64]+ConfiguredValueTaskAwaiter.System.Runtime.CompilerServices.IStateMachineBoxAwareAwaiter.AwaitUnsafeOnCompleted(class System.Runtime.CompilerServices.IAsyncStateMachineBox)", true, (int)AsyncProfilerStitchSyncFrameKind.V1AwaiterRegistrationInfrastructure)]
        [InlineData("System.Runtime.CompilerServices.ValueTaskAwaiter.System.Runtime.CompilerServices.IStateMachineBoxAwareAwaiter.AwaitUnsafeOnCompleted()", false, (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData("Other.Namespace.ValueTaskAwaiter.System.Runtime.CompilerServices.IStateMachineBoxAwareAwaiter.AwaitUnsafeOnCompleted()", true, (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData("System.Runtime.CompilerServices.OtherAwaiter.System.Runtime.CompilerServices.IStateMachineBoxAwareAwaiter.AwaitUnsafeOnCompleted()", true, (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData("System.Runtime.CompilerServices.ValueTaskAwaiter.Other.Namespace.IStateMachineBoxAwareAwaiter.AwaitUnsafeOnCompleted()", true, (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData("System.Runtime.CompilerServices.AsyncProfiler+Other.Resume()", true, (int)AsyncProfilerStitchSyncFrameKind.ProfilerInfrastructure)]
        [InlineData(null, true, (int)AsyncProfilerStitchSyncFrameKind.None)]
        public void InitialProfiles_ShareExistingEvidenceAndOwnershipChecks(
            string method, bool isHostModule, int expectedValue)
        {
            var expected = (AsyncProfilerStitchSyncFrameKind)expectedValue;
            foreach (AsyncProfilerRuntimeRuleProfile profile in InitialProfiles())
            {
                Assert.Equal(expected, profile.ClassifySynchronousFrame(method, isHostModule));
            }
        }

        [Theory]
        [InlineData("System.Boolean System.Threading.Tasks.Task::TrySetResult()",
            (int)AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult)]
        [InlineData("System.Boolean System.Threading.Tasks.Task`1[System.Int64]::TrySetResult(!0)",
            (int)AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult)]
        [InlineData("System.Void System.Threading.Tasks.Task::RunContinuations(System.Object)",
            (int)AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations)]
        [InlineData("System.Threading.ThreadPoolWorkQueue+DispatchResult System.Threading.ThreadPoolWorkQueue::Dispatch()",
            (int)AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibThreadPoolDispatch)]
        [InlineData("System.Private.CoreLib!System.Boolean System.Threading.Tasks.Task::TrySetResult()",
            (int)AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult)]
        [InlineData("System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter::QueueContinuation(System.Action,System.Boolean)",
            (int)AsyncProfilerStitchSyncFrameKind.V1AwaiterRegistrationInfrastructure)]
        [InlineData("System.Private.CoreLib!System.Void System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter::QueueContinuation(System.Action,System.Boolean)",
            (int)AsyncProfilerStitchSyncFrameKind.V1AwaiterRegistrationInfrastructure)]
        [InlineData("Other.Namespace.YieldAwaitable+YieldAwaiter::QueueContinuation()", (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData("System.Runtime.CompilerServices.OtherAwaitable+YieldAwaiter::QueueContinuation()", (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData("System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter::QueueContinuationExtra()", (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData("Other.CoreLib!System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter::QueueContinuation()", (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData("System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter::OutputCorrelationEtwEvent(System.Action)",
            (int)AsyncProfilerStitchSyncFrameKind.V1AwaiterRegistrationInfrastructure)]
        [InlineData("System.Private.CoreLib!System.Action System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter::OutputCorrelationEtwEvent(System.Action)",
            (int)AsyncProfilerStitchSyncFrameKind.V1AwaiterRegistrationInfrastructure)]
        [InlineData("Other.Namespace.YieldAwaitable+YieldAwaiter::OutputCorrelationEtwEvent()", (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData("System.Runtime.CompilerServices.OtherAwaitable+YieldAwaiter::OutputCorrelationEtwEvent()", (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData("System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter::OutputCorrelationEtwEventExtra()", (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData("System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter+<>c::<OutputCorrelationEtwEvent>b__6_0(System.Action,System.Threading.Tasks.Task)",
            (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData("Other.CoreLib!System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter::OutputCorrelationEtwEvent()", (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData("System.Boolean System.Threading.ThreadPool::UnsafeQueueUserWorkItem(System.Threading.WaitCallback,System.Object)",
            (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData("System.Threading.LowLevelLifoSemaphore::WakeOneCore()", (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData("System.Boolean Other.Namespace.Task::TrySetResult()", (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData("System.Threading.Tasks.Task Other.Namespace.Task::TrySetResult()", (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData("System.Boolean System.Threading.Tasks.Task+Other::TrySetResult()", (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData("System.Boolean System.Threading.Tasks.Task`2[System.Int32,System.Int64]::TrySetResult()", (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData("System.Boolean System.Threading.Tasks.Task`1[System.Int64::TrySetResult()", (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData("Other.CoreLib!System.Boolean System.Threading.Tasks.Task::TrySetResult()", (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData("System.Boolean System.Threading.Tasks.Task::TrySetResultExtra()", (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData("System.Threading.ThreadPoolWorkQueue+DispatchResult Other.Namespace.ThreadPoolWorkQueue::Dispatch()", (int)AsyncProfilerStitchSyncFrameKind.None)]
        public void PdbNames_PreserveExactDeclaringTypeAndModuleOwnershipChecks(string name, int expected)
        {
            string normalized = AsyncProfilerMethodNames.NormalizeMethodName(name);
            foreach (AsyncProfilerRuntimeRuleProfile profile in InitialProfiles())
            {
                Assert.Equal((AsyncProfilerStitchSyncFrameKind)expected, profile.ClassifySynchronousFrame(normalized, true));
                Assert.Equal(AsyncProfilerStitchSyncFrameKind.None, profile.ClassifySynchronousFrame(normalized, false));
            }
        }

        [Theory]
        [InlineData("dynamicClass.IL_STUB_AsyncResume(class System.Object,unsigned int8&)", true)]
        [InlineData("dynamicClass.IL_STUB_AsyncResume", true)]
        [InlineData("Other.dynamicClass.IL_STUB_AsyncResume()", false)]
        [InlineData("Other+dynamicClass.IL_STUB_AsyncResume()", false)]
        [InlineData("dynamicClass.IL_STUB_AsyncResumeOther()", false)]
        [InlineData("Other.IL_STUB_AsyncResume()", false)]
        [InlineData("IL_STUB_AsyncResume()", false)]
        public void ResumeStub_IsProfileSpecificApplicationInfrastructureNotABoundary(string name, bool recognized)
        {
            foreach (AsyncProfilerRuntimeRuleProfile profile in InitialProfiles())
            {
                foreach (bool hostModule in new[] { false, true })
                {
                    Assert.Equal(recognized ? AsyncProfilerStitchSyncFrameKind.V2ResumeStub : AsyncProfilerStitchSyncFrameKind.None,
                        profile.ClassifySynchronousFrame(name, hostModule));
                }
                Assert.Equal(AsyncProfilerCallStackStitchBoundaryKind.None, profile.ClassifyBoundary(name).Kind);
            }
        }

        [Theory]
        [InlineData(@"C:\runtime\coreclr.dll", @"C:\runtime\System.Private.CoreLib.dll", true)]
        [InlineData(@"C:\RUNTIME\CLRJIT.DLL", @"c:\runtime\System.Private.CoreLib.dll", true)]
        [InlineData("/runtime/libcoreclr.so", "/runtime/System.Private.CoreLib.dll", true)]
        [InlineData("/runtime/libclrjit.so", "/runtime/System.Private.CoreLib.dll", true)]
        [InlineData("/runtime/libcoreclr.dylib", "/runtime/System.Private.CoreLib.dll", true)]
        [InlineData("/runtime/libclrjit.dylib", "/runtime/System.Private.CoreLib.dll", true)]
        [InlineData(@"C:\other\coreclr.dll", @"C:\runtime\System.Private.CoreLib.dll", false)]
        [InlineData(@"C:\runtime\coreclr.extra.dll", @"C:\runtime\System.Private.CoreLib.dll", false)]
        [InlineData("clrjit.dll", "System.Private.CoreLib.dll", false)]
        public void NativeDispatchModules_RequireCapturedRuntimeImagesInTheHostDirectory(
            string path, string hostPath, bool recognized)
        {
            var module = new TraceModuleFile(path, 0x1000, (ModuleFileIndex)0);
            var host = new TraceModuleFile(hostPath, 0x2000, (ModuleFileIndex)1);
            foreach (AsyncProfilerRuntimeRuleProfile profile in InitialProfiles())
            {
                Assert.Equal(recognized, profile.IsV2NativeDispatchModule(module, host));
            }
        }

        [Theory]
        [InlineData("Continuation_Wrapper_31", (int)AsyncProfilerCallStackStitchBoundaryKind.V2Dispatcher, 31)]
        [InlineData("Continuation_Wrapper_32", (int)AsyncProfilerCallStackStitchBoundaryKind.None, -1)]
        [InlineData("RuntimeAsyncTask.DispatchContinuations()", (int)AsyncProfilerCallStackStitchBoundaryKind.V2DispatcherInfrastructure, -1)]
        [InlineData("RuntimeAsyncTask.InstrumentedDispatchContinuations()", (int)AsyncProfilerCallStackStitchBoundaryKind.V2DispatcherInfrastructure, -1)]
        [InlineData("Box.MoveNextAsDispatcher()", (int)AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher, -1)]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDispatcher.MoveNext()", (int)AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher, -1)]
        [InlineData(null, (int)AsyncProfilerCallStackStitchBoundaryKind.None, -1)]
        public void BoundaryContracts_AreSharedEvenWhenAProfileDisablesDispatcherInfrastructure(
            string method, int expectedValue, int expectedWrapperIndex)
        {
            var expected = (AsyncProfilerCallStackStitchBoundaryKind)expectedValue;
            var profiles = new[]
            {
                AsyncProfilerRuntimeRuleProfile.Common,
                AsyncProfilerRuntimeRuleProfile.CoreClr11,
                new NoDispatcherInfrastructureProfile(),
            };
            foreach (AsyncProfilerRuntimeRuleProfile profile in profiles)
            {
                AsyncProfilerCallStackStitchBoundaryInfo info = profile.ClassifyBoundary(method);
                Assert.Equal(expected, info.Kind);
                Assert.Equal(expectedWrapperIndex, info.WrapperIndex);
            }
        }

        [Theory]
        [InlineData("System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox`1.MoveNext()")]
        [InlineData("System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncProfilerAsyncStateMachineBox`1.InstrumentedMoveNext()")]
        [InlineData("System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder+ProfilerAsyncStateMachineBox`1.ExecuteDirectly()")]
        [InlineData("System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder+DebugFinalizableAsyncStateMachineBox`1.MoveNext()")]
        [InlineData("System.Private.CoreLib!System.Runtime.CompilerServices.AsyncStateMachineDispatcher.ExecuteDirectly()")]
        [InlineData("System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[System.Int64,V1TraceScenarios+<BurnCpuAsync>d__15].ExecuteDirectly(class System.Threading.Thread)")]
        [InlineData("System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[System.Int64,V1TraceScenarios+<BurnCpuAsync>d__15].MoveNext(class System.Threading.Thread)")]
        [InlineData("System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncProfilerAsyncStateMachineBox`1[System.Int64,V1TraceScenarios+<BurnCpuAsync>d__15].InstrumentedMoveNext(class System.Threading.Thread,value class Flags)")]
        public void ClassifyBoundary_RecognizesV1DispatcherInfrastructure(string frameName)
        {
            AsyncProfilerCallStackStitchBoundaryInfo info = AsyncProfilerRuntimeRuleProfile.Common.ClassifyBoundary(frameName);
            Assert.Equal(AsyncProfilerCallStackStitchBoundaryKind.V1DispatcherInfrastructure, info.Kind);
            Assert.Equal(-1, info.WrapperIndex);
        }

        [Fact]
        public void BoxRecognition_IsImplementationDependentRatherThanABoundaryContract()
        {
            const string box =
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+" +
                "AsyncProfilerAsyncStateMachineBox`1[System.Int64,Scenario+<Run>d__1].MoveNext()";
            Assert.Equal(AsyncProfilerCallStackStitchBoundaryKind.None, AsyncProfilerCallStackStitchBoundary.ClassifyContractBoundary(box, out _));
            Assert.Equal(
                AsyncProfilerCallStackStitchBoundaryKind.None,
                new NoDispatcherInfrastructureProfile().ClassifyBoundary(box).Kind);

            foreach (AsyncProfilerRuntimeRuleProfile profile in InitialProfiles())
            {
                Assert.Equal(AsyncProfilerCallStackStitchBoundaryKind.V1DispatcherInfrastructure, profile.ClassifyBoundary(box).Kind);
                Assert.True(profile.TryGetBoxedStateMachineTypeName(box, true, out string stateMachineType));
                Assert.Equal("Scenario+<Run>d__1", stateMachineType);
                Assert.False(profile.TryGetBoxedStateMachineTypeName(box, false, out _));
            }
        }

        [Theory]
        [InlineData("AsyncStateMachineBox`1[App+<Run>d__1].MoveNext()", "App+<Run>d__1")]
        [InlineData("AsyncProfilerAsyncStateMachineBox`1[System.Int64,App+<Run>d__1].InstrumentedMoveNext(class System.Threading.Thread)", "App+<Run>d__1")]
        [InlineData("ProfilerAsyncStateMachineBox`1[System.Collections.Generic.List`1[System.Int64],App`1+<Run>d__1`1[System.Int32,System.String]].MoveNext()", "App`1+<Run>d__1`1[System.Int32,System.String]")]
        [InlineData("DebugFinalizableAsyncStateMachineBox`1[System.Int64, App+<Run>d__1].MoveNext()", "App+<Run>d__1")]
        public void BoxCarrier_ExtractsTheFinalConstructedTypeArgument(string boxMethod, string expected)
        {
            string name = "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+" + boxMethod;
            foreach (AsyncProfilerRuntimeRuleProfile profile in InitialProfiles())
            {
                Assert.True(profile.TryGetBoxedStateMachineTypeName(name, true, out string type));
                Assert.Equal(expected, type);
            }
        }

        [Theory]
        [InlineData(null, true)]
        [InlineData("", true)]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineBox`1[App].MoveNext()", false)]
        [InlineData("Other.Namespace.AsyncStateMachineBox`1[App].MoveNext()", true)]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineBox`1[App].MoveNextAsDispatcher()", true)]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineBox`1[App].UnknownMoveNext()", true)]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDispatcher.MoveNext()", true)]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineBox`1.MoveNext()", true)]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineBox`10[App].MoveNext()", true)]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineBox`1[].MoveNext()", true)]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineBox`1[App,].MoveNext()", true)]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineBox`1[,App].MoveNext()", true)]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineBox`1[App`1[System.Int32].MoveNext()", true)]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineBox`1[App]].MoveNext()", true)]
        public void BoxCarrier_RejectsUnknownUnconstructedAndMalformedNames(string name, bool isHostModule)
        {
            foreach (AsyncProfilerRuntimeRuleProfile profile in InitialProfiles())
            {
                Assert.False(profile.TryGetBoxedStateMachineTypeName(name, isHostModule, out _));
            }
        }

        [Fact]
        public void BoundaryInfrastructureOverride_DoesNotChangeInheritedCarrierAndHelperRules()
        {
            const string boxType =
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+" +
                "AsyncProfilerAsyncStateMachineBox`1[System.Int64,Scenario+<Run>d__1]";
            var profile = new NoDispatcherInfrastructureProfile();

            Assert.Equal(AsyncProfilerCallStackStitchBoundaryKind.None, profile.ClassifyBoundary(boxType + ".MoveNext()").Kind);
            Assert.True(profile.TryGetBoxedStateMachineTypeName(boxType + ".MoveNext()", true, out string type));
            Assert.Equal("Scenario+<Run>d__1", type);
            Assert.Equal(
                AsyncProfilerStitchSyncFrameKind.ProfilerInfrastructure,
                profile.ClassifySynchronousFrame(boxType + ".GetDiagnosticData()", true));
            Assert.Equal(
                AsyncProfilerStitchSyncFrameKind.None,
                profile.ClassifySynchronousFrame(boxType + ".GetDiagnosticData()", false));
        }

        [Theory]
        [InlineData("System.Runtime.CompilerServices.AsyncProfiler..cctor()", true)]
        [InlineData("System.Runtime.CompilerServices.AsyncProfiler+OtherHelpers.Initialize()", true)]
        [InlineData("System.Runtime.CompilerServices.AsyncProfilerEventSource..ctor()", true)]
        [InlineData("System.Runtime.CompilerServices.TaskAwaiter.GetResult()", true)]
        [InlineData("System.Runtime.CompilerServices.TaskAwaiter`1[System.Int64].GetResult()", true)]
        [InlineData("System.Runtime.CompilerServices.TaskAwaiter+<>c.<OutputWaitEtwEvents>b__12_0(System.Action,System.Threading.Tasks.Task)", true)]
        [InlineData("System.Runtime.CompilerServices.TaskAwaiter+OtherHelper.Invoke()", true)]
        [InlineData("System.Runtime.CompilerServices.TaskAwaiterLookalike.GetResult()", false)]
        [InlineData("System.Runtime.CompilerServices.TaskAwaiter.OtherType.Invoke()", false)]
        [InlineData("System.Runtime.CompilerServices.TaskAwaiter`10[System.Int64].GetResult()", false)]
        [InlineData("Other.Namespace.TaskAwaiter.GetResult()", false)]
        [InlineData("Other.CoreLib!System.Runtime.CompilerServices.TaskAwaiter.GetResult()", false)]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`1[App+<Run>d__1].ResolveMethodId()", true)]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`1[App+<Run>d__1].GetState(!!0&)", true)]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`1[App+<Run>d__1]..cctor()", true)]
        [InlineData("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[System.Int64,App+<Run>d__1]..ctor()", true)]
        [InlineData("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncProfilerAsyncStateMachineBox`1[System.Int64,App+<Run>d__1].ClearStateUponCompletion()", true)]
        [InlineData("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+DebugFinalizableAsyncStateMachineBox`1[System.Int64,App+<Run>d__1].System.Runtime.CompilerServices.IAsyncStateMachineBox.GetDiagnosticData()", true)]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`10[App].ResolveMethodId()", false)]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`1[App]+Other.Initialize()", false)]
        [InlineData("System.Runtime.CompilerServices.StaticsHelpers.GetNonGCStaticBaseSlow()", false)]
        [InlineData("System.Runtime.CompilerServices.InitHelpers.CallClassConstructor()", false)]
        [InlineData("Other.Namespace.AsyncStateMachineDiagnostics`1[App].ResolveMethodId()", false)]
        [InlineData("Other.Namespace.AsyncStateMachineBox`1[App].GetDiagnosticData()", false)]
        [InlineData("App+<Run>d__1.MoveNext()", false)]
        public void DispatchSpanHelpers_RecognizeTypeFamiliesButNotGenericInitialization(string name, bool expected)
        {
            foreach (AsyncProfilerRuntimeRuleProfile profile in InitialProfiles())
            {
                AsyncProfilerStitchSyncFrameKind kind = profile.ClassifySynchronousFrame(name, true);
                Assert.Equal(expected, profile.IsProfilerDispatchInfrastructure(kind));
                Assert.False(profile.IsProfilerDispatchInfrastructure(profile.ClassifySynchronousFrame(name, false)));
            }
        }

        [Theory]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDispatcher.System.Runtime.CompilerServices.IAsyncStateMachineDispatcher.get_DispatcherId()", true)]
        [InlineData("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncProfilerAsyncStateMachineBox`1[System.Int64,App+<Run>d__1].System.Runtime.CompilerServices.IAsyncStateMachineDispatcher.get_DispatcherId()", true)]
        [InlineData("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncProfilerAsyncStateMachineBox`1[System.Int64,App+<Run>d__1].System.Runtime.CompilerServices.IAsyncStateMachineDispatcher.get_IsLeaf()", true)]
        [InlineData("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncProfilerAsyncStateMachineBox`1[System.Int64,App+<Run>d__1].Other.Interface.FutureMember()", true)]
        [InlineData("System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncProfilerAsyncStateMachineBox`1[System.Int64,App+<Run>d__1].System.IDisposable.Dispose()", true)]
        [InlineData("Other.Namespace.AsyncStateMachineDispatcher.System.Runtime.CompilerServices.IAsyncStateMachineDispatcher.get_DispatcherId()", false)]
        [InlineData("System.Runtime.CompilerServices.OrdinaryType.System.Runtime.CompilerServices.IAsyncStateMachineDispatcher.get_DispatcherId()", false)]
        [InlineData("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncProfilerAsyncStateMachineBoxLookalike`1[System.Int64,App+<Run>d__1].System.Runtime.CompilerServices.IAsyncStateMachineDispatcher.get_DispatcherId()", false)]
        [InlineData("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncProfilerAsyncStateMachineBox`1[System.Int64,App+<Run>d__1.System.Runtime.CompilerServices.IAsyncStateMachineDispatcher.get_DispatcherId()", false)]
        public void ProfilerImplementingTypes_RecognizeInterfaceMembersWithoutMethodLists(string name, bool expected)
        {
            foreach (AsyncProfilerRuntimeRuleProfile profile in InitialProfiles())
            {
                Assert.Equal(expected, profile.IsProfilerDispatchInfrastructure(
                    profile.ClassifySynchronousFrame(name, true)));
                Assert.Equal(AsyncProfilerStitchSyncFrameKind.None, profile.ClassifySynchronousFrame(name, false));
                Assert.Equal(AsyncProfilerCallStackStitchBoundaryKind.None, profile.ClassifyBoundary(name).Kind);
            }
        }

        [Theory]
        [InlineData("System.Threading.Tasks.Task.TrySetException(System.Object)", true)]
        [InlineData("System.Boolean System.Threading.Tasks.Task::TrySetException(System.Object)", true)]
        [InlineData("System.Threading.Tasks.Task`1[System.Int64].TrySetException(System.Object)", true)]
        [InlineData("System.Threading.Tasks.Task.AddException(System.Object,System.Boolean)", true)]
        [InlineData("System.Threading.Tasks.TaskExceptionHolder.AddFaultException(System.Object)", true)]
        [InlineData("System.Threading.Tasks.Task.TrySetExceptionExtra(System.Object)", false)]
        [InlineData("System.Threading.Tasks.Task+Other.TrySetException(System.Object)", false)]
        [InlineData("System.Threading.Tasks.Task`10[System.Int64].TrySetException(System.Object)", false)]
        [InlineData("System.Threading.Tasks.TaskExceptionHolderLookalike.AddFaultException(System.Object)", false)]
        [InlineData("Other.Namespace.Task.AddException(System.Object)", false)]
        [InlineData("Other.CoreLib!System.Threading.Tasks.Task.TrySetException(System.Object)", false)]
        public void TaskExceptionInfrastructure_IsClassifiedButNotIndividuallyCleaned(string name, bool expected)
        {
            foreach (AsyncProfilerRuntimeRuleProfile profile in InitialProfiles())
            {
                string normalized = AsyncProfilerMethodNames.NormalizeMethodName(name);
                AsyncProfilerStitchSyncFrameKind kind = profile.ClassifySynchronousFrame(normalized, true);
                Assert.Equal(expected, kind == AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskExceptionInfrastructure);
                Assert.Equal(expected, profile.IsProfilerDispatchInfrastructure(kind));
                Assert.Equal(AsyncProfilerStitchSyncFrameKind.None, profile.ClassifySynchronousFrame(normalized, false));
                var frame = AsyncProfilerStitchedFrame.CreateSync(
                    new AsyncProfilerStitchSyncFrame(CodeAddressIndex.Invalid, MethodIndex.Invalid, kind), null);
                Assert.False(profile.IsOptionalInfrastructureFrame(frame, AsyncProfilerCallStackStitchBoundaryKind.None));
                Assert.False(profile.IsV1CompletionUnwindInfrastructure(frame, AsyncProfilerCallStackStitchBoundaryKind.None));
            }
        }

        [Theory]
        [InlineData("System.Runtime.CompilerServices.TaskAwaiter.GetResult()", true)]
        [InlineData("System.Runtime.CompilerServices.TaskAwaiter`1[System.Int64].GetResult()", true)]
        [InlineData("System.Runtime.CompilerServices.TaskAwaiter+<>c.<OutputWaitEtwEvents>b__12_0(System.Action,System.Threading.Tasks.Task)", true)]
        [InlineData("System.Runtime.CompilerServices.TaskAwaiterLookalike.GetResult()", false)]
        [InlineData("System.Runtime.CompilerServices.TaskAwaiter.OtherType.Invoke()", false)]
        [InlineData("System.Runtime.CompilerServices.TaskAwaiter`10[System.Int64].GetResult()", false)]
        [InlineData("Other.Namespace.TaskAwaiter.GetResult()", false)]
        [InlineData("Other.CoreLib!System.Runtime.CompilerServices.TaskAwaiter.GetResult()", false)]
        public void AwaiterInfrastructure_IsClassifiedWithoutAuthorizingIndividualCleanup(string name, bool expected)
        {
            foreach (AsyncProfilerRuntimeRuleProfile profile in InitialProfiles())
            {
                AsyncProfilerStitchSyncFrameKind kind = profile.ClassifySynchronousFrame(name, true);
                Assert.Equal(expected, kind == AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAwaiterInfrastructure);
                Assert.Equal(AsyncProfilerStitchSyncFrameKind.None, profile.ClassifySynchronousFrame(name, false));
                var frame = AsyncProfilerStitchedFrame.CreateSync(
                    new AsyncProfilerStitchSyncFrame(CodeAddressIndex.Invalid, MethodIndex.Invalid, kind), null);
                Assert.False(profile.IsOptionalInfrastructureFrame(frame, AsyncProfilerCallStackStitchBoundaryKind.None));
                Assert.Equal(expected, profile.IsV1CompletionUnwindInfrastructure(frame, AsyncProfilerCallStackStitchBoundaryKind.None));
            }
        }

        [Theory]
        [InlineData("System.Runtime.CompilerServices.TaskAwaiter.get_IsCompleted()")]
        [InlineData("System.Runtime.CompilerServices.TaskAwaiter`1[System.Int64].get_IsCompleted()")]
        [InlineData("System.Runtime.CompilerServices.ValueTaskAwaiter.get_IsCompleted()")]
        [InlineData("System.Runtime.CompilerServices.ValueTaskAwaiter`1[System.Int64].get_IsCompleted()")]
        [InlineData("System.Runtime.CompilerServices.ConfiguredTaskAwaitable+ConfiguredTaskAwaiter.get_IsCompleted()")]
        [InlineData("System.Runtime.CompilerServices.ConfiguredTaskAwaitable`1+ConfiguredTaskAwaiter[System.Int64].get_IsCompleted()")]
        [InlineData("System.Runtime.CompilerServices.ConfiguredTaskAwaitable`1[System.Int64]+ConfiguredTaskAwaiter.get_IsCompleted()")]
        [InlineData("System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable+ConfiguredValueTaskAwaiter.get_IsCompleted()")]
        [InlineData("System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable`1+ConfiguredValueTaskAwaiter[System.Int64].get_IsCompleted()")]
        [InlineData("System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable`1[System.Int64]+ConfiguredValueTaskAwaiter.get_IsCompleted()")]
        public void AwaiterReadinessCheck_IsOptionalMachineryNotDispatchOrCompletionEvidence(string name)
        {
            foreach (AsyncProfilerRuntimeRuleProfile profile in InitialProfiles())
            {
                AsyncProfilerStitchSyncFrameKind kind = profile.ClassifySynchronousFrame(name, true);
                Assert.Equal(AsyncProfilerStitchSyncFrameKind.V1AwaiterCompletionCheck, kind);
                Assert.Equal(AsyncProfilerStitchSyncFrameKind.None, profile.ClassifySynchronousFrame(name, false));
                Assert.False(profile.ClassifyBoundary(name).IsDispatchMarker);
                var frame = AsyncProfilerStitchedFrame.CreateSync(
                    new AsyncProfilerStitchSyncFrame(CodeAddressIndex.Invalid, MethodIndex.Invalid, kind), null);
                Assert.True(profile.IsOptionalInfrastructureFrame(frame, AsyncProfilerCallStackStitchBoundaryKind.None));
                Assert.False(profile.IsProfilerDispatchInfrastructure(kind));
                Assert.False(profile.IsV1CompletionUnwindInfrastructure(frame, AsyncProfilerCallStackStitchBoundaryKind.None));
            }
        }

        [Theory]
        [InlineData("Other.Namespace.ValueTaskAwaiter.get_IsCompleted()")]
        [InlineData("Other.CoreLib!System.Runtime.CompilerServices.ValueTaskAwaiter.get_IsCompleted()")]
        [InlineData("System.Runtime.CompilerServices.ValueTaskAwaiter+Other.get_IsCompleted()")]
        [InlineData("System.Runtime.CompilerServices.ValueTaskAwaiter`10[System.Int64].get_IsCompleted()")]
        [InlineData("System.Runtime.CompilerServices.ValueTaskAwaiter.get_IsCompletedExtra()")]
        [InlineData("System.Runtime.CompilerServices.ValueTaskAwaiter.GetResult()")]
        [InlineData("Other.Namespace.TaskAwaiter.get_IsCompleted()")]
        [InlineData("System.Runtime.CompilerServices.TaskAwaiter+Other.get_IsCompleted()")]
        [InlineData("System.Runtime.CompilerServices.OtherAwaitable+ConfiguredTaskAwaiter.get_IsCompleted()")]
        [InlineData("System.Runtime.CompilerServices.ConfiguredTaskAwaitable+Other+ConfiguredTaskAwaiter.get_IsCompleted()")]
        [InlineData("System.Runtime.CompilerServices.ConfiguredTaskAwaitable`10[System.Int64]+ConfiguredTaskAwaiter.get_IsCompleted()")]
        [InlineData("System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable`1[System.Int64+ConfiguredValueTaskAwaiter.get_IsCompleted()")]
        [InlineData("System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable+ConfiguredValueTaskAwaiter.GetResult()")]
        [InlineData("System.Threading.Tasks.Sources.ManualResetValueTaskSourceCore`1[System.Int64].GetStatus(int16)")]
        [InlineData("Scenario.PooledSource.GetStatus(int16)")]
        public void AwaiterReadinessCheck_DoesNotBroadenOtherAwaiterCleanup(string name)
        {
            foreach (AsyncProfilerRuntimeRuleProfile profile in InitialProfiles())
            {
                AsyncProfilerStitchSyncFrameKind kind = profile.ClassifySynchronousFrame(name, true);
                Assert.NotEqual(AsyncProfilerStitchSyncFrameKind.V1AwaiterCompletionCheck, kind);
                var frame = AsyncProfilerStitchedFrame.CreateSync(
                    new AsyncProfilerStitchSyncFrame(CodeAddressIndex.Invalid, MethodIndex.Invalid, kind), null);
                Assert.False(profile.IsOptionalInfrastructureFrame(frame, AsyncProfilerCallStackStitchBoundaryKind.None));
            }
        }

        [Theory]
        [InlineData("System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder.SetResult()", true)]
        [InlineData("System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder`1[System.Int64].SetResult(!0)", true)]
        [InlineData("System.Private.CoreLib!System.Void System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder`1[System.Int64]::SetResult(System.Int64)", true)]
        [InlineData("System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder.SetException(System.Exception)", false)]
        [InlineData("System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder.SetResultExtra()", false)]
        [InlineData("System.Runtime.CompilerServices.AsyncValueTaskMethodBuilderLookalike.SetResult()", false)]
        [InlineData("System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder`10[System.Int64].SetResult(!0)", false)]
        [InlineData("System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder`1[System.Int64.SetResult(!0)", false)]
        [InlineData("System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder+Other.SetResult()", false)]
        [InlineData("Other.Namespace.AsyncValueTaskMethodBuilder.SetResult()", false)]
        [InlineData("Other.CoreLib!System.Runtime.CompilerServices.AsyncValueTaskMethodBuilder.SetResult()", false)]
        public void ValueTaskCompletionWrapper_RequiresExactBuilderAndNormalCompletion(string name, bool expected)
        {
            string normalized = AsyncProfilerMethodNames.NormalizeMethodName(name);
            foreach (AsyncProfilerRuntimeRuleProfile profile in InitialProfiles())
            {
                Assert.Equal(expected, profile.ClassifySynchronousFrame(normalized, true) ==
                    AsyncProfilerStitchSyncFrameKind.V1MethodBuilderCompletion);
                Assert.Equal(AsyncProfilerStitchSyncFrameKind.None, profile.ClassifySynchronousFrame(normalized, false));
            }
        }

        [Theory]
        [InlineData(
            "AsyncProfilerScenarios!V1TraceScenarios+<BurnCpuAsync>d__15.MoveNext()",
            (int)AsyncProfilerStitchSyncFrameKind.V1StateMachineMoveNext)]
        [InlineData(
            "Namespace.Type+<>c+<<Run>b__0_0>d.MoveNext()",
            (int)AsyncProfilerStitchSyncFrameKind.V1StateMachineMoveNext)]
        [InlineData(
            "Namespace.Type+<<Run>g__Inner|1_0>d.MoveNext()",
            (int)AsyncProfilerStitchSyncFrameKind.V1StateMachineMoveNext)]
        [InlineData(
            "Namespace.Type`1+<<Run>b__0_0>d[MyApp.MoveNextPayload].MoveNext()",
            (int)AsyncProfilerStitchSyncFrameKind.V1StateMachineMoveNext)]
        [InlineData(
            "Namespace.Type+<Run>d_4.MoveNext()",
            (int)AsyncProfilerStitchSyncFrameKind.V1StateMachineMoveNext)]
        [InlineData(
            "Namespace.Type+<Run>display.MoveNext()",
            (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].Start(!!0&)",
            (int)AsyncProfilerStitchSyncFrameKind.V1MethodBuilderStart)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].AwaitUnsafeOnCompleted(!!0&,!!1&)",
            (int)AsyncProfilerStitchSyncFrameKind.V1MethodBuilderAwaitUnsafeOnCompleted)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].AwaitUnsafeOnCompleted(!!0&,!!1&,class System.Threading.Tasks.Task`1<!0>&)",
            (int)AsyncProfilerStitchSyncFrameKind.V1MethodBuilderAwaitUnsafeOnCompleted)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].AwaitUnsafeOnCompleted(!!0&,class System.Runtime.CompilerServices.IAsyncStateMachineBox)",
            (int)AsyncProfilerStitchSyncFrameKind.V1MethodBuilderAwaitUnsafeOnCompleted)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start(!!0&)",
            (int)AsyncProfilerStitchSyncFrameKind.V1MethodBuilderStart)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].SetExistingTaskResult(class System.Threading.Tasks.Task`1<!0>,!0)",
            (int)AsyncProfilerStitchSyncFrameKind.V1MethodBuilderCompletion)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].GetStateMachineBox(!!0&,class System.Threading.Tasks.Task`1<!0>&)",
            (int)AsyncProfilerStitchSyncFrameKind.V1MethodBuilderInfrastructure)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].SetException(class System.Exception)",
            (int)AsyncProfilerStitchSyncFrameKind.V1MethodBuilderInfrastructure)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.AsyncIteratorMethodBuilder.Complete()",
            (int)AsyncProfilerStitchSyncFrameKind.V1MethodBuilderInfrastructure)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter.System.Runtime.CompilerServices.IStateMachineBoxAwareAwaiter.AwaitUnsafeOnCompleted(class System.Runtime.CompilerServices.IAsyncStateMachineBox)",
            (int)AsyncProfilerStitchSyncFrameKind.V1AwaiterRegistrationInfrastructure)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.TaskAwaiter.UnsafeOnCompletedInternal(class System.Threading.Tasks.Task,class System.Action,bool)",
            (int)AsyncProfilerStitchSyncFrameKind.V1AwaiterRegistrationInfrastructure)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable+ConfiguredValueTaskAwaiter.UnsafeOnCompleted(class System.Action)",
            (int)AsyncProfilerStitchSyncFrameKind.V1AwaiterRegistrationInfrastructure)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.TaskAwaiter.GetResult()",
            (int)AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAwaiterInfrastructure)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.Tasks.Task.RunContinuations(class System.Object)",
            (int)AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.ThreadPoolWorkQueue.Dispatch()",
            (int)AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibThreadPoolDispatch)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.Tasks.Task.TrySetResult()",
            (int)AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.Tasks.Task`1[System.Boolean].TrySetResult(!0)",
            (int)AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.Tasks.TaskCompletionSource`1[System.Boolean].SetResult(!0)",
            (int)AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskCompletionSourceSetResult)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.Tasks.Task`1[[System.Collections.Generic.List`1[System.Int32]]].TrySetResult(!0)",
            (int)AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.Tasks.Task`1.TrySetResult(!0)",
            (int)AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult)]
        [InlineData(
            "System.Private.CoreLib!Other.Namespace.Task.RunContinuations()",
            (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData(
            "System.Private.CoreLib!Other.Namespace.TaskCompletionSource`1[System.Boolean].SetResult(!0)",
            (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData(
            "System.Private.CoreLib!Other.Namespace.ExecutionContext.RunInternal()",
            (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.Tasks.TaskExtra.RunContinuations()",
            (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.Tasks.Task`1[System.Boolean]+Other.TrySetResult(!0)",
            (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.Tasks.Task`2[System.Boolean,System.Int32].TrySetResult(!0)",
            (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.Tasks.TaskCompletionSource`10.SetResult(!0)",
            (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.Tasks.AwaitTaskContinuation`1[System.Boolean].RunOrScheduleAction()",
            (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.ExecutionContext`1.RunInternal()",
            (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.Tasks.Task`1[System.Boolean.TrySetResult(!0)",
            (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.Tasks.AwaitTaskContinuation.RunOrScheduleAction(class System.Action,bool)",
            (int)AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.RuntimeAsyncTaskContinuation+TaskContinuationResume.ResumeTaskContinuation(class System.Runtime.CompilerServices.Continuation,unsigned int8&)",
            (int)AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.RuntimeAsyncTaskContinuation+Other.ResumeTaskContinuation()",
            (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.RuntimeAsyncTaskContinuation+TaskContinuationResume.ResumeTaskContinuationExtra()",
            (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.ExecutionContext.RunInternal(class System.Threading.ExecutionContext,class System.Threading.ContextCallback,class System.Object)",
            (int)AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.ExecutionContext.RunFromThreadPoolDispatchLoop(class System.Threading.Thread,class System.Threading.ExecutionContext,class System.Threading.ContextCallback,class System.Object)",
            (int)AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure)]
        [InlineData(
            "Other.CoreLib!System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start(!!0&)",
            (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData(
            "System.Private.CoreLib!Other.Namespace.AsyncMethodBuilderCore.Start(!!0&)",
            (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData(
            "MyBuilders!CustomAsyncMethodBuilder.Start(!!0&)",
            (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData(
            "MyBuilders!CustomAsyncMethodBuilder.AwaitUnsafeOnCompleted(!!0&,!!1&)",
            (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData(
            "MyBuilders!CustomAsyncMethodBuilder.SetException(class System.Exception)",
            (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData(
            "MyCoreLib!System.Threading.Tasks.Task.RunContinuations(class System.Object)",
            (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData(
            "MyApp!InlineContinuationAwaiter.UnsafeOnCompleted(class System.Action)",
            (int)AsyncProfilerStitchSyncFrameKind.None)]
        [InlineData(
            "MyApp!MyType.MoveNext()",
            (int)AsyncProfilerStitchSyncFrameKind.None)]
        public void ClassifySynchronousFrame_RecognizesKnownAsyncRelatedFrames(string frameName, int expectedValue)
        {
            Assert.Equal("System.Private.CoreLib", AsyncProfilerRuntimeRuleProfile.HostModuleName);
            var expected = (AsyncProfilerStitchSyncFrameKind)expectedValue;
            bool isHostModule = AsyncProfilerRuntimeRuleProfile.IsHostModuleQualifiedFrame(frameName);
            Assert.Equal(expected, AsyncProfilerRuntimeRuleProfile.Common.ClassifySynchronousFrame(frameName, isHostModule));
        }

        [Theory]
        [InlineData(
            "System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start(!!0&)",
            (int)AsyncProfilerStitchSyncFrameKind.V1MethodBuilderStart)]
        [InlineData(
            "System.Threading.ThreadPoolWorkQueue.Dispatch()",
            (int)AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibThreadPoolDispatch)]
        [InlineData(
            "System.Threading.Tasks.Task`1[System.Int64].TrySetResult(!0)",
            (int)AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult)]
        [InlineData(
            "System.Threading.Tasks.Task`1[!0].TrySetResult(!0)",
            (int)AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult)]
        [InlineData(
            "System.Threading.Tasks.Task`1[[System.Collections.Generic.List`1[System.Int32]]].TrySetResult(!0)",
            (int)AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult)]
        [InlineData(
            "System.Threading.Tasks.TaskCompletionSource`1[System.Boolean].SetResult(!0)",
            (int)AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskCompletionSourceSetResult)]
        [InlineData(
            "System.Runtime.CompilerServices.RuntimeAsyncTaskContinuation+TaskContinuationResume.ResumeTaskContinuation(class System.Runtime.CompilerServices.Continuation,unsigned int8&)",
            (int)AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure)]
        public void ClassifySynchronousFrame_SplitTraceEventModuleAndMethod_RequiresCoreLib(
            string method, int expectedValue)
        {
            var expected = (AsyncProfilerStitchSyncFrameKind)expectedValue;
            Assert.Equal(
                expected,
                AsyncProfilerRuntimeRuleProfile.Common.ClassifySynchronousFrame(method, isHostModule: true));
            Assert.Equal(
                AsyncProfilerStitchSyncFrameKind.None,
                AsyncProfilerRuntimeRuleProfile.Common.ClassifySynchronousFrame(method, isHostModule: false));
        }

        [Theory]
        [InlineData("Other.Namespace.Task`1[System.Int64].TrySetResult(!0)")]
        [InlineData("System.Threading.Tasks.Task`2[System.Int64,System.Int32].TrySetResult(!0)")]
        [InlineData("System.Threading.Tasks.Task`1[System.Int64]+Other.TrySetResult(!0)")]
        [InlineData("System.Threading.Tasks.Task`1[System.Int64.TrySetResult(!0)")]
        [InlineData("System.Threading.Tasks.TaskCompletionSource`10.SetResult(!0)")]
        [InlineData("Other.CoreLib!System.Threading.Tasks.Task`1[System.Int64].TrySetResult(!0)")]
        [InlineData("Other.Namespace.ThreadPoolWorkQueue.Dispatch()")]
        [InlineData("System.Threading.ThreadPoolWorkQueue`1[System.Int64].Dispatch()")]
        [InlineData("System.Threading.ThreadPoolWorkQueue.DispatchExtra()")]
        [InlineData("Other.CoreLib!System.Threading.ThreadPoolWorkQueue.Dispatch()")]
        [InlineData("Other.Namespace.AsyncProfiler+AsyncCallstack.CaptureStateMachineAsyncCallstack()")]
        [InlineData("System.Runtime.CompilerServices.OtherAsyncProfiler+AsyncCallstack.EmitEvent()")]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`2[!0,!1].get_MethodId()")]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`10.get_MethodId()")]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`1[!0].cctor()")]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`1[!0]+Other..cctor()")]
        [InlineData("Other.Namespace.AsyncStateMachineDiagnostics`1[!0]..cctor()")]
        [InlineData("Other.Namespace.InitHelpers.InitClassSlow()")]
        [InlineData("System.Runtime.CompilerServices.InitHelpers.Other()")]
        [InlineData("System.Runtime.CompilerServices.StaticsHelpers.GetNonGCStaticBaseSlowExtra()")]
        [InlineData("Other.CoreLib!System.Runtime.CompilerServices.AsyncProfiler+AsyncCallstack.EmitEvent()")]
        public void ClassifySynchronousFrame_SplitModuleAndMethod_RejectsLookalikes(string method)
        {
            Assert.Equal(
                AsyncProfilerStitchSyncFrameKind.None,
                AsyncProfilerRuntimeRuleProfile.Common.ClassifySynchronousFrame(method, isHostModule: true));
        }

        [Theory]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDispatcherInfo.CreateDispatcher")]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDispatcherInfo.CreateDispatcher(class System.Runtime.CompilerServices.IAsyncStateMachineBox,value class Flags)")]
        public void ClassifySynchronousFrame_DispatcherCreation_RequiresCoreLibAndIsNotABoundary(string method)
        {
            Assert.Equal(
                AsyncProfilerStitchSyncFrameKind.V1DispatcherCreationInfrastructure,
                AsyncProfilerRuntimeRuleProfile.Common.ClassifySynchronousFrame("System.Private.CoreLib!" + method, isHostModule: true));
            Assert.Equal(
                AsyncProfilerStitchSyncFrameKind.V1DispatcherCreationInfrastructure,
                AsyncProfilerRuntimeRuleProfile.Common.ClassifySynchronousFrame(method, isHostModule: true));
            Assert.Equal(
                AsyncProfilerStitchSyncFrameKind.None,
                AsyncProfilerRuntimeRuleProfile.Common.ClassifySynchronousFrame(method, isHostModule: false));
            AsyncProfilerCallStackStitchBoundaryInfo info = AsyncProfilerRuntimeRuleProfile.Common.ClassifyBoundary("System.Private.CoreLib!" + method);
            Assert.Equal(AsyncProfilerCallStackStitchBoundaryKind.None, info.Kind);
            Assert.Equal(-1, info.WrapperIndex);
        }

        [Theory]
        [InlineData("Other.CoreLib!System.Runtime.CompilerServices.AsyncStateMachineDispatcherInfo.CreateDispatcher()")]
        [InlineData("Other.Namespace.AsyncStateMachineDispatcherInfo.CreateDispatcher()")]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDispatcherInfoExtra.CreateDispatcher()")]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDispatcherInfo`1[!0].CreateDispatcher()")]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDispatcherInfo+Other.CreateDispatcher()")]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDispatcherInfo.CreateDispatcherExtra()")]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDispatcherInfo.Other(System.Runtime.CompilerServices.AsyncStateMachineDispatcherInfo.CreateDispatcher)")]
        public void ClassifySynchronousFrame_DispatcherCreation_RejectsLookalikes(string method)
        {
            Assert.Equal(
                AsyncProfilerStitchSyncFrameKind.None,
                AsyncProfilerRuntimeRuleProfile.Common.ClassifySynchronousFrame(method, isHostModule: true));
            Assert.Equal(
                AsyncProfilerStitchSyncFrameKind.None,
                AsyncProfilerRuntimeRuleProfile.Common.ClassifySynchronousFrame("System.Private.CoreLib!" + method, isHostModule: true));
        }

        [Theory]
        [InlineData("System.Runtime.CompilerServices.AsyncProfiler+AsyncCallstack.CaptureStateMachineAsyncCallstack(unsigned int8[],int32&,value class State&)", (int)AsyncProfilerStitchSyncFrameKind.ProfilerCallstackCapture)]
        [InlineData("System.Runtime.CompilerServices.AsyncProfiler+AsyncCallstack.EmitAsyncCallstack(unsigned int8[],int32&,!!0&)", (int)AsyncProfilerStitchSyncFrameKind.ProfilerCallstackEmission)]
        [InlineData("System.Runtime.CompilerServices.AsyncProfiler+AsyncCallstack.EmitEvent()", (int)AsyncProfilerStitchSyncFrameKind.ProfilerCallstackEmission)]
        [InlineData("System.Runtime.CompilerServices.AsyncProfiler+ResumeAsyncContext.Resume()", (int)AsyncProfilerStitchSyncFrameKind.ProfilerContextResume)]
        [InlineData("System.Runtime.CompilerServices.AsyncProfiler+SyncPoint.ResetContext()", (int)AsyncProfilerStitchSyncFrameKind.ProfilerInfrastructure)]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`1[Scenario+<ChildAsync>d__3]..cctor()", (int)AsyncProfilerStitchSyncFrameKind.ProfilerInfrastructure)]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`1[Scenario+<ChildAsync>d__3].get_MethodId()", (int)AsyncProfilerStitchSyncFrameKind.ProfilerInfrastructure)]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`1[!0].get_StateOffset()", (int)AsyncProfilerStitchSyncFrameKind.ProfilerInfrastructure)]
        [InlineData("System.Runtime.CompilerServices.InitHelpers.CallClassConstructor(void*,void*,class System.Exception*)", (int)AsyncProfilerStitchSyncFrameKind.RuntimeInitializationInfrastructure)]
        [InlineData("System.Runtime.CompilerServices.InitHelpers.InitClassHelper(value class MethodTable*)", (int)AsyncProfilerStitchSyncFrameKind.RuntimeInitializationInfrastructure)]
        [InlineData("System.Runtime.CompilerServices.InitHelpers.InitClassSlow(value class MethodTable*)", (int)AsyncProfilerStitchSyncFrameKind.RuntimeInitializationInfrastructure)]
        [InlineData("System.Runtime.CompilerServices.StaticsHelpers.GetNonGCStaticBaseSlow(value class MethodTable*)", (int)AsyncProfilerStitchSyncFrameKind.RuntimeInitializationInfrastructure)]
        [InlineData("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[System.Int64,Scenario+<ChildAsync>d__3].System.Runtime.CompilerServices.IAsyncStateMachineBox.GetDiagnosticData(unsigned int64&,int32&,class System.Object&)", (int)AsyncProfilerStitchSyncFrameKind.ProfilerInfrastructure)]
        [InlineData("System.Runtime.CompilerServices.AsyncProfiler+Other.EmitEvent()", (int)AsyncProfilerStitchSyncFrameKind.ProfilerInfrastructure)]
        [InlineData("System.Runtime.CompilerServices.AsyncProfiler+AsyncCallstack.EmitEventExtra()", (int)AsyncProfilerStitchSyncFrameKind.ProfilerInfrastructure)]
        [InlineData("System.Runtime.CompilerServices.AsyncProfiler+ResumeAsyncContext.Other()", (int)AsyncProfilerStitchSyncFrameKind.ProfilerInfrastructure)]
        [InlineData("System.Runtime.CompilerServices.AsyncProfiler+Other.Resume()", (int)AsyncProfilerStitchSyncFrameKind.ProfilerInfrastructure)]
        public void ClassifySynchronousFrame_ProfilerEnvelope_PreservesSpecializedKindsAndOwnership(
            string method, int expectedValue)
        {
            var expected = (AsyncProfilerStitchSyncFrameKind)expectedValue;
            Assert.Equal(expected, AsyncProfilerRuntimeRuleProfile.Common.ClassifySynchronousFrame("System.Private.CoreLib!" + method, isHostModule: true));
            Assert.Equal(expected, AsyncProfilerRuntimeRuleProfile.Common.ClassifySynchronousFrame(method, isHostModule: true));
            Assert.Equal(AsyncProfilerStitchSyncFrameKind.None, AsyncProfilerRuntimeRuleProfile.Common.ClassifySynchronousFrame(method, isHostModule: false));
        }

        #region private

        private static AsyncProfilerRuntimeRuleProfile[] InitialProfiles() =>
            new[] { AsyncProfilerRuntimeRuleProfile.Common, AsyncProfilerRuntimeRuleProfile.CoreClr11 };

        private sealed class NoDispatcherInfrastructureProfile : AsyncProfilerRuntimeRuleProfile
        {
            public NoDispatcherInfrastructureProfile() : base("Test profile")
            {
            }

            protected override bool IsV1DispatcherInfrastructure(string frameName) => false;
        }

        #endregion
    }
}
