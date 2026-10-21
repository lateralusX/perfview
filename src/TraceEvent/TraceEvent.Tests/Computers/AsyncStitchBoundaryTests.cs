// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Diagnostics.Tracing.Computers;

using Xunit;

namespace TraceEventTests
{
    /// <summary>
    /// Unit tests for the pure (dependency-free) boundary recognizer <see cref="AsyncStitchBoundary"/>: name
    /// parsing (<see cref="AsyncStitchBoundary.GetBareMethodName"/>), continuation-wrapper index extraction
    /// (<see cref="AsyncStitchBoundary.TryGetWrapperIndex"/>) and full classification
    /// (<see cref="AsyncStitchBoundary.Classify"/>). These are the string-based contract with the runtime async
    /// profiler; the per-trace <c>AsyncStitchBoundaryCache</c> (which needs a real symbol table) is exercised
    /// separately by the TraceLog integration tests.
    /// </summary>
    public class AsyncStitchBoundaryTests
    {
        [Theory]
        [InlineData(null, null)]
        [InlineData("", "")]
        [InlineData("(", "(")]
        [InlineData("Namespace.Type.Method()", "Namespace.Type.Method()")]
        [InlineData("Namespace.Type.Method(class Other::Parameter)", "Namespace.Type.Method(class Other::Parameter)")]
        [InlineData("Namespace.Type::Method(class Other::Parameter)", "Namespace.Type.Method(class Other::Parameter)")]
        [InlineData("Namespace.Type::Method", "Namespace.Type.Method")]
        [InlineData("Namespace.Type::.ctor()", "Namespace.Type..ctor()")]
        [InlineData("Namespace.Type::.cctor()", "Namespace.Type..cctor()")]
        [InlineData("Namespace.Type::System.IDisposable.Dispose()", "Namespace.Type.System.IDisposable.Dispose()")]
        [InlineData("System.Runtime.CompilerServices.AsyncHelpers+RuntimeAsyncTask`1[System.Threading.Tasks.VoidTaskResult]::DispatchContinuations()",
                    "System.Runtime.CompilerServices.AsyncHelpers+RuntimeAsyncTask`1[System.Threading.Tasks.VoidTaskResult].DispatchContinuations()")]
        [InlineData("System.Runtime.CompilerServices.AsyncHelpers+RuntimeAsyncTask`1[System.Threading.Tasks.VoidTaskResult]::InstrumentedDispatchContinuations(valuetype System.Runtime.CompilerServices.AsyncInstrumentation/Flags)",
                    "System.Runtime.CompilerServices.AsyncHelpers+RuntimeAsyncTask`1[System.Threading.Tasks.VoidTaskResult].InstrumentedDispatchContinuations(valuetype System.Runtime.CompilerServices.AsyncInstrumentation/Flags)")]
        [InlineData("System.Private.CoreLib!System.Runtime.CompilerServices.AsyncProfiler+ContinuationWrapper::Continuation_Wrapper_1(class System.Runtime.CompilerServices.Continuation,uint8&)",
                    "System.Private.CoreLib!System.Runtime.CompilerServices.AsyncProfiler+ContinuationWrapper.Continuation_Wrapper_1(class System.Runtime.CompilerServices.Continuation,uint8&)")]
        public void NormalizeMethodName_ChangesOnlyTheTypeMethodSeparator(string name, string expected)
        {
            string normalized = AsyncStitchBoundary.NormalizeMethodName(name);
            Assert.Equal(expected, normalized);
            Assert.Same(normalized, AsyncStitchBoundary.NormalizeMethodName(normalized));
            if (name == expected)
            {
                Assert.Same(name, normalized);
            }
        }

        [Theory]
        [InlineData("Namespace.Type+<RunAsync>d__1::MoveNext()", "Namespace.Type+<RunAsync>d__1")]
        [InlineData("Namespace.Type`1+<RunAsync>d__1`1[System.Int64,System.String]::MoveNext()",
                    "Namespace.Type`1+<RunAsync>d__1`1[System.Int64,System.String]")]
        public void NormalizeMethodName_SupportsExistingV1CompilerNameDecoding(string name, string expectedType)
        {
            string normalized = AsyncStitchBoundary.NormalizeMethodName(name);
            Assert.Equal(expectedType, AsyncStitchBoundary.GetStateMachineTypeName(normalized));
            Assert.True(AsyncStitchBoundary.TryGetLogicalStateMachineMethodName(
                normalized, out string logicalName, out string suffix));
            Assert.StartsWith("Namespace.Type", logicalName);
            Assert.EndsWith(".RunAsync", logicalName);
            Assert.StartsWith("d__1", suffix);
        }

        [Fact]
        public void NormalizeMethodName_PreservesV1CarrierTypeIdentity()
        {
            const string stateMachineType = "Namespace.Type`1+<RunAsync>d__1`1[System.Int64,System.String]";
            string box = AsyncStitchBoundary.NormalizeMethodName(
                "System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncProfilerAsyncStateMachineBox`1[" +
                "System.Int64," + stateMachineType + "]::InstrumentedMoveNext(class System.Threading.Thread)");
            string moveNext = AsyncStitchBoundary.NormalizeMethodName(stateMachineType + "::MoveNext()");
            foreach (AsyncRuntimeRuleProfile profile in new[] { AsyncRuntimeRuleProfile.Common, AsyncRuntimeRuleProfile.CoreClr11 })
            {
                Assert.True(profile.TryGetBoxedStateMachineTypeName(box, true, out string boxedType));
                Assert.Equal(AsyncStitchBoundary.GetStateMachineTypeName(moveNext), boxedType);
                Assert.False(profile.TryGetBoxedStateMachineTypeName(box, false, out _));
            }
        }

        [Theory]
        [InlineData("Continuation_Wrapper_0", "Continuation_Wrapper_0")]                                     // already bare
        [InlineData("System.Private.CoreLib!AsyncProfiler+ContinuationWrapper.Continuation_Wrapper_7", "Continuation_Wrapper_7")] // module + nested type
        [InlineData("SomeModule!Ns.Type.Method(System.Int32, System.String)", "Method")]                     // signature stripped
        [InlineData("Ns.Type.MoveNextAsDispatcher", "MoveNextAsDispatcher")]                                 // dotted, no module
        [InlineData("ntdll!RtlUserThreadStart", "RtlUserThreadStart")]                                        // module only, no dot
        [InlineData("BareToken", "BareToken")]                                                                // unqualified
        public void GetBareMethodName_ExtractsMethodComponent(string frameName, string expected)
        {
            Assert.Equal(expected, AsyncStitchBoundary.GetBareMethodName(frameName));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void GetBareMethodName_NullOrEmpty_ReturnedUnchanged(string frameName)
        {
            Assert.Equal(frameName, AsyncStitchBoundary.GetBareMethodName(frameName));
        }

        [Theory]
        [InlineData(
            "Namespace.Type+<FooAsync>d__17.MoveNext()",
            "Namespace.Type.FooAsync",
            "d__17")]
        [InlineData(
            "Namespace.Outer+Nested+<Run>d_4.MoveNext()",
            "Namespace.Outer.Nested.Run",
            "d_4")]
        public void TryGetLogicalStateMachineMethodName_ReturnsGeneratedSuffix(
            string methodName,
            string expectedLogicalName,
            string expectedStateMachineSuffix)
        {
            Assert.True(
                AsyncStitchBoundary.TryGetLogicalStateMachineMethodName(
                    methodName,
                    out string logicalName,
                    out string stateMachineSuffix));
            Assert.Equal(expectedLogicalName, logicalName);
            Assert.Equal(expectedStateMachineSuffix, stateMachineSuffix);
        }

        [Theory]
        [InlineData("TestApp!Namespace.App+<Run>d__1.MoveNext()", "Namespace.App+<Run>d__1")]
        [InlineData("Namespace.App`1+<Run>d__1`1[System.Int32,System.String].MoveNext()", "Namespace.App`1+<Run>d__1`1[System.Int32,System.String]")]
        [InlineData("Namespace.App.Run()", null)]
        [InlineData(null, null)]
        public void GetStateMachineTypeName_ExtractsQualifiedStateMachineMethodTypes(string name, string expected)
        {
            Assert.Equal(expected, AsyncStitchBoundary.GetStateMachineTypeName(name));
        }

        [Theory]
        [InlineData("Continuation_Wrapper_0", true, 0)]
        [InlineData("Continuation_Wrapper_31", true, 31)]                                                     // last valid slot
        [InlineData("Continuation_Wrapper_32", false, -1)]                                                    // one past the pool
        [InlineData("Continuation_Wrapper_", false, -1)]                                                      // empty suffix
        [InlineData("Continuation_Wrapper_x", false, -1)]                                                     // non-numeric
        [InlineData("Continuation_Wrapper_-1", false, -1)]                                                    // sign rejected (NumberStyles.None)
        [InlineData("Continuation_Wrapper_007", true, 7)]                                                     // leading zeros ok
        [InlineData("NotAWrapper", false, -1)]
        [InlineData(null, false, -1)]
        public void TryGetWrapperIndex_ParsesPoolSlot(string method, bool expectedResult, int expectedIndex)
        {
            bool result = AsyncStitchBoundary.TryGetWrapperIndex(method, out int wrapperIndex);
            Assert.Equal(expectedResult, result);
            Assert.Equal(expectedIndex, wrapperIndex);
        }

        [Theory]
        // V2 continuation wrapper (carries an index)
        [InlineData("System.Private.CoreLib!AsyncProfiler+ContinuationWrapper.Continuation_Wrapper_0", AsyncStitchBoundaryKind.V2ContinuationWrapper, 0)]
        [InlineData("System.Private.CoreLib!AsyncProfiler+ContinuationWrapper.Continuation_Wrapper_31", AsyncStitchBoundaryKind.V2ContinuationWrapper, 31)]
        // V2 dispatch continuations (no wrapper -> index -1)
        [InlineData("System.Private.CoreLib!System.Runtime.CompilerServices.RuntimeAsyncTask`1.DispatchContinuations()", AsyncStitchBoundaryKind.V2DispatchContinuation, -1)]
        [InlineData("System.Private.CoreLib!System.Runtime.CompilerServices.RuntimeAsyncTask`1.InstrumentedDispatchContinuations()", AsyncStitchBoundaryKind.V2DispatchContinuation, -1)]
        // V1 dispatcher (merged box)
        [InlineData("System.Private.CoreLib!...AsyncProfilerAsyncStateMachineBox`1.MoveNextAsDispatcher()", AsyncStitchBoundaryKind.V1Dispatcher, -1)]
        // V1 dispatcher (non-merged/standalone wrapper) -- recognized type-qualified
        [InlineData("System.Private.CoreLib!System.Runtime.CompilerServices.AsyncStateMachineDispatcher.MoveNext()", AsyncStitchBoundaryKind.V1Dispatcher, -1)]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDispatcher.MoveNext", AsyncStitchBoundaryKind.V1Dispatcher, -1)] // no module
        // V1 dispatcher infrastructure (type-family based, not method-name based)
        [InlineData("System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncStateMachineBox`1.MoveNext()", AsyncStitchBoundaryKind.V1DispatcherInfrastructure, -1)]
        [InlineData("System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder+AsyncProfilerAsyncStateMachineBox`1.InstrumentedMoveNext()", AsyncStitchBoundaryKind.V1DispatcherInfrastructure, -1)]
        [InlineData("System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder+ProfilerAsyncStateMachineBox`1.ExecuteDirectly()", AsyncStitchBoundaryKind.V1DispatcherInfrastructure, -1)]
        [InlineData("System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder+DebugFinalizableAsyncStateMachineBox`1.MoveNext()", AsyncStitchBoundaryKind.V1DispatcherInfrastructure, -1)]
        [InlineData("System.Private.CoreLib!System.Runtime.CompilerServices.AsyncStateMachineDispatcher.ExecuteDirectly()", AsyncStitchBoundaryKind.V1DispatcherInfrastructure, -1)]
        // Not a boundary
        [InlineData("System.Private.CoreLib!System.Threading.Tasks.Task.RunContinuations(System.Object)", AsyncStitchBoundaryKind.None, -1)]
        [InlineData("MyApp!MyApp.Program.MoveNext()", AsyncStitchBoundaryKind.None, -1)]
        [InlineData("MyApp!MyApp.AsyncStateMachineBox`1.MoveNext()", AsyncStitchBoundaryKind.None, -1)]
        // A bare "MoveNext" (or on any other type) must NOT be mistaken for the non-merged dispatcher.
        [InlineData("System.Private.CoreLib!Some.Other.StateMachineBox`1.MoveNext()", AsyncStitchBoundaryKind.None, -1)]
        [InlineData("MoveNext", AsyncStitchBoundaryKind.None, -1)]
        [InlineData(null, AsyncStitchBoundaryKind.None, -1)]
        [InlineData("", AsyncStitchBoundaryKind.None, -1)]
        public void Classify_RecognizesBoundaryKinds(string frameName, AsyncStitchBoundaryKind expectedKind, int expectedWrapperIndex)
        {
            AsyncStitchBoundaryKind kind = AsyncStitchBoundary.Classify(frameName, out int wrapperIndex);
            Assert.Equal(expectedKind, kind);
            Assert.Equal(expectedWrapperIndex, wrapperIndex);
        }

        [Theory]
        [InlineData("System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[System.Int64,V1TraceScenarios+<BurnCpuAsync>d__15].ExecuteDirectly(class System.Threading.Thread)")]
        [InlineData("System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[System.Int64,V1TraceScenarios+<BurnCpuAsync>d__15].MoveNext(class System.Threading.Thread)")]
        [InlineData("System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncProfilerAsyncStateMachineBox`1[System.Int64,V1TraceScenarios+<BurnCpuAsync>d__15].InstrumentedMoveNext(class System.Threading.Thread,value class Flags)")]
        public void Classify_RecognizesConstructedV1StateMachineBoxFramesFromRealTrace(string frameName)
        {
            Assert.Equal(
                AsyncStitchBoundaryKind.V1DispatcherInfrastructure,
                AsyncStitchBoundary.Classify(frameName, out int wrapperIndex));
            Assert.Equal(-1, wrapperIndex);
        }

        [Theory]
        [InlineData(
            "AsyncProfilerScenarios!V1TraceScenarios+<BurnCpuAsync>d__15.MoveNext()",
            StitchSyncFrameKind.V1StateMachineMoveNext)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].Start(!!0&)",
            StitchSyncFrameKind.V1MethodBuilderStart)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].AwaitUnsafeOnCompleted(!!0&,!!1&)",
            StitchSyncFrameKind.V1MethodBuilderAwaitUnsafeOnCompleted)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].AwaitUnsafeOnCompleted(!!0&,!!1&,class System.Threading.Tasks.Task`1<!0>&)",
            StitchSyncFrameKind.V1MethodBuilderAwaitUnsafeOnCompleted)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].AwaitUnsafeOnCompleted(!!0&,class System.Runtime.CompilerServices.IAsyncStateMachineBox)",
            StitchSyncFrameKind.V1MethodBuilderAwaitUnsafeOnCompleted)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start(!!0&)",
            StitchSyncFrameKind.V1MethodBuilderStart)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].SetExistingTaskResult(class System.Threading.Tasks.Task`1<!0>,!0)",
            StitchSyncFrameKind.V1MethodBuilderCompletion)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].GetStateMachineBox(!!0&,class System.Threading.Tasks.Task`1<!0>&)",
            StitchSyncFrameKind.V1MethodBuilderInfrastructure)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].SetException(class System.Exception)",
            StitchSyncFrameKind.V1MethodBuilderInfrastructure)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.AsyncIteratorMethodBuilder.Complete()",
            StitchSyncFrameKind.V1MethodBuilderInfrastructure)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter.System.Runtime.CompilerServices.IStateMachineBoxAwareAwaiter.AwaitUnsafeOnCompleted(class System.Runtime.CompilerServices.IAsyncStateMachineBox)",
            StitchSyncFrameKind.V1AwaiterRegistrationInfrastructure)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.TaskAwaiter.UnsafeOnCompletedInternal(class System.Threading.Tasks.Task,class System.Action,bool)",
            StitchSyncFrameKind.V1AwaiterRegistrationInfrastructure)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.ConfiguredValueTaskAwaitable+ConfiguredValueTaskAwaiter.UnsafeOnCompleted(class System.Action)",
            StitchSyncFrameKind.V1AwaiterRegistrationInfrastructure)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.TaskAwaiter.GetResult()",
            StitchSyncFrameKind.None)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.Tasks.Task.RunContinuations(class System.Object)",
            StitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.ThreadPoolWorkQueue.Dispatch()",
            StitchSyncFrameKind.SystemPrivateCoreLibThreadPoolDispatch)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.Tasks.Task.TrySetResult()",
            StitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.Tasks.Task`1[System.Boolean].TrySetResult(!0)",
            StitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.Tasks.TaskCompletionSource`1[System.Boolean].SetResult(!0)",
            StitchSyncFrameKind.SystemPrivateCoreLibTaskCompletionSourceSetResult)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.Tasks.Task`1[[System.Collections.Generic.List`1[System.Int32]]].TrySetResult(!0)",
            StitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.Tasks.Task`1.TrySetResult(!0)",
            StitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult)]
        [InlineData(
            "System.Private.CoreLib!Other.Namespace.Task.RunContinuations()",
            StitchSyncFrameKind.None)]
        [InlineData(
            "System.Private.CoreLib!Other.Namespace.TaskCompletionSource`1[System.Boolean].SetResult(!0)",
            StitchSyncFrameKind.None)]
        [InlineData(
            "System.Private.CoreLib!Other.Namespace.ExecutionContext.RunInternal()",
            StitchSyncFrameKind.None)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.Tasks.TaskExtra.RunContinuations()",
            StitchSyncFrameKind.None)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.Tasks.Task`1[System.Boolean]+Other.TrySetResult(!0)",
            StitchSyncFrameKind.None)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.Tasks.Task`2[System.Boolean,System.Int32].TrySetResult(!0)",
            StitchSyncFrameKind.None)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.Tasks.TaskCompletionSource`10.SetResult(!0)",
            StitchSyncFrameKind.None)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.Tasks.AwaitTaskContinuation`1[System.Boolean].RunOrScheduleAction()",
            StitchSyncFrameKind.None)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.ExecutionContext`1.RunInternal()",
            StitchSyncFrameKind.None)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.Tasks.Task`1[System.Boolean.TrySetResult(!0)",
            StitchSyncFrameKind.None)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.Tasks.AwaitTaskContinuation.RunOrScheduleAction(class System.Action,bool)",
            StitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.ExecutionContext.RunInternal(class System.Threading.ExecutionContext,class System.Threading.ContextCallback,class System.Object)",
            StitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure)]
        [InlineData(
            "System.Private.CoreLib!System.Threading.ExecutionContext.RunFromThreadPoolDispatchLoop(class System.Threading.Thread,class System.Threading.ExecutionContext,class System.Threading.ContextCallback,class System.Object)",
            StitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure)]
        [InlineData(
            "Other.CoreLib!System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start(!!0&)",
            StitchSyncFrameKind.None)]
        [InlineData(
            "System.Private.CoreLib!Other.Namespace.AsyncMethodBuilderCore.Start(!!0&)",
            StitchSyncFrameKind.None)]
        [InlineData(
            "MyBuilders!CustomAsyncMethodBuilder.Start(!!0&)",
            StitchSyncFrameKind.None)]
        [InlineData(
            "MyBuilders!CustomAsyncMethodBuilder.AwaitUnsafeOnCompleted(!!0&,!!1&)",
            StitchSyncFrameKind.None)]
        [InlineData(
            "MyBuilders!CustomAsyncMethodBuilder.SetException(class System.Exception)",
            StitchSyncFrameKind.None)]
        [InlineData(
            "MyCoreLib!System.Threading.Tasks.Task.RunContinuations(class System.Object)",
            StitchSyncFrameKind.None)]
        [InlineData(
            "MyApp!InlineContinuationAwaiter.UnsafeOnCompleted(class System.Action)",
            StitchSyncFrameKind.None)]
        [InlineData(
            "MyApp!MyType.MoveNext()",
            StitchSyncFrameKind.None)]
        public void ClassifySynchronousFrame_RecognizesKnownAsyncRelatedFrames(
            string frameName,
            StitchSyncFrameKind expected)
        {
            Assert.Equal(expected, AsyncStitchBoundary.ClassifySynchronousFrame(frameName));
        }

        [Theory]
        [InlineData(
            "System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start(!!0&)",
            StitchSyncFrameKind.V1MethodBuilderStart)]
        [InlineData(
            "System.Threading.ThreadPoolWorkQueue.Dispatch()",
            StitchSyncFrameKind.SystemPrivateCoreLibThreadPoolDispatch)]
        [InlineData(
            "System.Threading.Tasks.Task`1[System.Int64].TrySetResult(!0)",
            StitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult)]
        [InlineData(
            "System.Threading.Tasks.Task`1[!0].TrySetResult(!0)",
            StitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult)]
        [InlineData(
            "System.Threading.Tasks.Task`1[[System.Collections.Generic.List`1[System.Int32]]].TrySetResult(!0)",
            StitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult)]
        [InlineData(
            "System.Threading.Tasks.TaskCompletionSource`1[System.Boolean].SetResult(!0)",
            StitchSyncFrameKind.SystemPrivateCoreLibTaskCompletionSourceSetResult)]
        public void ClassifySynchronousFrame_SplitTraceEventModuleAndMethod_RequiresCoreLib(
            string method, StitchSyncFrameKind expected)
        {
            Assert.Equal(
                expected,
                AsyncStitchBoundary.ClassifySynchronousFrame(method, isHostModule: true));
            Assert.Equal(
                StitchSyncFrameKind.None,
                AsyncStitchBoundary.ClassifySynchronousFrame(method, isHostModule: false));
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
        public void ClassifySynchronousFrame_SplitTraceEventModuleAndMethod_RejectsLookalikes(
            string method)
        {
            Assert.Equal(
                StitchSyncFrameKind.None,
                AsyncStitchBoundary.ClassifySynchronousFrame(method, isHostModule: true));
        }

        [Theory]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDispatcherInfo.CreateDispatcher")]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDispatcherInfo.CreateDispatcher(class System.Runtime.CompilerServices.IAsyncStateMachineBox,value class Flags)")]
        public void ClassifySynchronousFrame_DispatcherCreation_RequiresCoreLibAndIsNotABoundary(string method)
        {
            Assert.Equal(
                StitchSyncFrameKind.V1DispatcherCreationInfrastructure,
                AsyncStitchBoundary.ClassifySynchronousFrame("System.Private.CoreLib!" + method));
            Assert.Equal(
                StitchSyncFrameKind.V1DispatcherCreationInfrastructure,
                AsyncStitchBoundary.ClassifySynchronousFrame(method, isHostModule: true));
            Assert.Equal(
                StitchSyncFrameKind.None,
                AsyncStitchBoundary.ClassifySynchronousFrame(method, isHostModule: false));
            Assert.Equal(
                AsyncStitchBoundaryKind.None,
                AsyncStitchBoundary.Classify("System.Private.CoreLib!" + method, out int wrapperIndex));
            Assert.Equal(-1, wrapperIndex);
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
                StitchSyncFrameKind.None,
                AsyncStitchBoundary.ClassifySynchronousFrame(method, isHostModule: true));
            Assert.Equal(
                StitchSyncFrameKind.None,
                AsyncStitchBoundary.ClassifySynchronousFrame("System.Private.CoreLib!" + method));
        }

        [Theory]
        [InlineData("System.Runtime.CompilerServices.AsyncProfiler+AsyncCallstack.CaptureStateMachineAsyncCallstack(unsigned int8[],int32&,value class State&)", StitchSyncFrameKind.AsyncProfilerCallstackCapture)]
        [InlineData("System.Runtime.CompilerServices.AsyncProfiler+AsyncCallstack.EmitAsyncCallstack(unsigned int8[],int32&,!!0&)", StitchSyncFrameKind.AsyncProfilerCallstackEmission)]
        [InlineData("System.Runtime.CompilerServices.AsyncProfiler+AsyncCallstack.EmitEvent()", StitchSyncFrameKind.AsyncProfilerCallstackEmission)]
        [InlineData("System.Runtime.CompilerServices.AsyncProfiler+ResumeAsyncContext.Resume()", StitchSyncFrameKind.AsyncProfilerContextResume)]
        [InlineData("System.Runtime.CompilerServices.AsyncProfiler+SyncPoint.ResetContext()", StitchSyncFrameKind.AsyncProfilerInfrastructure)]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`1[Scenario+<ChildAsync>d__3]..cctor()", StitchSyncFrameKind.AsyncProfilerInfrastructure)]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`1[Scenario+<ChildAsync>d__3].get_MethodId()", StitchSyncFrameKind.AsyncProfilerInfrastructure)]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`1[!0].get_StateOffset()", StitchSyncFrameKind.AsyncProfilerInfrastructure)]
        [InlineData("System.Runtime.CompilerServices.InitHelpers.CallClassConstructor(void*,void*,class System.Exception*)", StitchSyncFrameKind.AsyncProfilerInfrastructure)]
        [InlineData("System.Runtime.CompilerServices.InitHelpers.InitClassHelper(value class MethodTable*)", StitchSyncFrameKind.AsyncProfilerInfrastructure)]
        [InlineData("System.Runtime.CompilerServices.InitHelpers.InitClassSlow(value class MethodTable*)", StitchSyncFrameKind.AsyncProfilerInfrastructure)]
        [InlineData("System.Runtime.CompilerServices.StaticsHelpers.GetNonGCStaticBaseSlow(value class MethodTable*)", StitchSyncFrameKind.AsyncProfilerInfrastructure)]
        [InlineData("System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+AsyncStateMachineBox`1[System.Int64,Scenario+<ChildAsync>d__3].System.Runtime.CompilerServices.IAsyncStateMachineBox.GetDiagnosticData(unsigned int64&,int32&,class System.Object&)", StitchSyncFrameKind.AsyncProfilerInfrastructure)]
        public void ClassifySynchronousFrame_ProfilerEnvelope_RequiresExactCoreLibMethods(
            string method, StitchSyncFrameKind expected)
        {
            Assert.Equal(expected, AsyncStitchBoundary.ClassifySynchronousFrame("System.Private.CoreLib!" + method));
            Assert.Equal(expected, AsyncStitchBoundary.ClassifySynchronousFrame(method, isHostModule: true));
            Assert.Equal(StitchSyncFrameKind.None, AsyncStitchBoundary.ClassifySynchronousFrame(method, isHostModule: false));
        }

        [Theory]
        [InlineData("Other.Namespace.AsyncProfiler+AsyncCallstack.CaptureStateMachineAsyncCallstack()")]
        [InlineData("System.Runtime.CompilerServices.OtherAsyncProfiler+AsyncCallstack.EmitEvent()")]
        [InlineData("System.Runtime.CompilerServices.AsyncProfiler+Other.EmitEvent()")]
        [InlineData("System.Runtime.CompilerServices.AsyncProfiler+AsyncCallstack.EmitEventExtra()")]
        [InlineData("System.Runtime.CompilerServices.AsyncProfiler+ResumeAsyncContext.Other()")]
        [InlineData("System.Runtime.CompilerServices.AsyncProfiler+Other.Resume()")]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`2[!0,!1].get_MethodId()")]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`10.get_MethodId()")]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`1[!0].cctor()")]
        [InlineData("System.Runtime.CompilerServices.AsyncStateMachineDiagnostics`1[!0]+Other..cctor()")]
        [InlineData("Other.Namespace.AsyncStateMachineDiagnostics`1[!0]..cctor()")]
        [InlineData("Other.Namespace.InitHelpers.InitClassSlow()")]
        [InlineData("System.Runtime.CompilerServices.InitHelpers.Other()")]
        [InlineData("System.Runtime.CompilerServices.StaticsHelpers.GetNonGCStaticBaseSlowExtra()")]
        [InlineData("Other.CoreLib!System.Runtime.CompilerServices.AsyncProfiler+AsyncCallstack.EmitEvent()")]
        public void ClassifySynchronousFrame_ProfilerEnvelope_RejectsLookalikes(string method)
        {
            Assert.Equal(StitchSyncFrameKind.None, AsyncStitchBoundary.ClassifySynchronousFrame(method, isHostModule: true));
        }

        [Fact]
        public void Contract_ConstantsMatchRuntime()
        {
            // These values are a contract with the dotnet/runtime async profiler; guard against accidental drift.
            Assert.Equal("Continuation_Wrapper_", AsyncStitchBoundary.ContinuationWrapperPrefix);
            Assert.Equal(32, AsyncStitchBoundary.WrapperPoolCount);
            Assert.Equal("DispatchContinuations", AsyncStitchBoundary.DispatchContinuationsName);
            Assert.Equal("InstrumentedDispatchContinuations", AsyncStitchBoundary.InstrumentedDispatchContinuationsName);
            Assert.Equal("MoveNextAsDispatcher", AsyncStitchBoundary.MoveNextAsDispatcherName);
            Assert.Equal("AsyncStateMachineDispatcher", AsyncStitchBoundary.AsyncStateMachineDispatcherTypeName);
            Assert.Equal("MoveNext", AsyncStitchBoundary.AsyncStateMachineDispatcherMethodName);
            Assert.Equal("System.Private.CoreLib", AsyncStitchBoundary.HostModuleName);
        }

        [Theory]
        [InlineData("System.Private.CoreLib!System.Runtime.CompilerServices.AsyncStateMachineDispatcher.MoveNext()", "AsyncStateMachineDispatcher")]
        [InlineData("Ns.Outer+Inner.Method", "Inner")]                       // nested type ('+') separator
        [InlineData("Module!Type.Method(System.Int32)", "Type")]             // module + signature stripped
        [InlineData("Type.Method", "Type")]                                  // no module/namespace
        [InlineData("BareToken", null)]                                       // no type qualifier
        [InlineData("Module!Method", null)]                                   // module but no type
        [InlineData(null, null)]
        [InlineData("", null)]
        public void GetDeclaringTypeName_ExtractsTypeComponent(string frameName, string expected)
        {
            Assert.Equal(expected, AsyncStitchBoundary.GetDeclaringTypeName(frameName));
        }

        [Fact]
        public void GetDeclaringTypeName_ConstructedNestedGenericType_IgnoresSeparatorsInTypeArguments()
        {
            const string frameName =
                "System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1+" +
                "AsyncProfilerAsyncStateMachineBox`1[System.Int64,V1TraceScenarios+<BurnCpuAsync>d__15]." +
                "InstrumentedMoveNext(class System.Threading.Thread,value class Flags)";

            Assert.Equal(
                "AsyncProfilerAsyncStateMachineBox`1[System.Int64,V1TraceScenarios+<BurnCpuAsync>d__15]",
                AsyncStitchBoundary.GetDeclaringTypeName(frameName));
        }
    }
}
