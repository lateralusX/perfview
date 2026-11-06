// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

using Xunit;

namespace TraceEventTests
{
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
            string normalized = AsyncProfilerCallStackStitchBoundary.NormalizeMethodName(name);
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
        public void ResumeStub_IsProfileSpecificApplicationPlumbingNotABoundary(string name, bool recognized)
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
        [InlineData("Continuation_Wrapper_31", (int)AsyncProfilerCallStackStitchBoundaryKind.V2ContinuationWrapper, 31)]
        [InlineData("Continuation_Wrapper_32", (int)AsyncProfilerCallStackStitchBoundaryKind.None, -1)]
        [InlineData("RuntimeAsyncTask.DispatchContinuations()", (int)AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation, -1)]
        [InlineData("RuntimeAsyncTask.InstrumentedDispatchContinuations()", (int)AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation, -1)]
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
        public void BoundaryPlumbingOverride_DoesNotChangeInheritedCarrierAndHelperRules()
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
                Assert.False(profile.ClassifyBoundary(name).IsBoundary);
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

        private static AsyncProfilerRuntimeRuleProfile[] InitialProfiles() =>
            new[] { AsyncProfilerRuntimeRuleProfile.Common, AsyncProfilerRuntimeRuleProfile.CoreClr11 };

        private sealed class NoDispatcherInfrastructureProfile : AsyncProfilerRuntimeRuleProfile
        {
            public NoDispatcherInfrastructureProfile() : base("Test profile")
            {
            }

            protected override bool IsV1DispatcherInfrastructure(string frameName) => false;
        }
    }
}
