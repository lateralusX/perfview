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
            "System.Private.CoreLib!System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start(!!0&)",
            StitchSyncFrameKind.V1MethodBuilderStart)]
        [InlineData(
            "System.Private.CoreLib!System.Runtime.CompilerServices.AsyncTaskMethodBuilder`1[System.Int64].SetExistingTaskResult(class System.Threading.Tasks.Task`1<!0>,!0)",
            StitchSyncFrameKind.V1MethodBuilderCompletion)]
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
            "MyApp!MyType.MoveNext()",
            StitchSyncFrameKind.None)]
        public void ClassifyV1SynchronousFrame_RecognizesKnownStartupFrames(
            string frameName,
            StitchSyncFrameKind expected)
        {
            Assert.Equal(expected, AsyncStitchBoundary.ClassifyV1SynchronousFrame(frameName));
        }

        [Fact]
        public void ClassifyV1SynchronousFrame_SplitTraceEventModuleAndMethod_RequiresCoreLib()
        {
            const string method =
                "System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start(!!0&)";

            Assert.Equal(
                StitchSyncFrameKind.V1MethodBuilderStart,
                AsyncStitchBoundary.ClassifyV1SynchronousFrame(method, isHostModule: true));
            Assert.Equal(
                StitchSyncFrameKind.None,
                AsyncStitchBoundary.ClassifyV1SynchronousFrame(method, isHostModule: false));
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
            Assert.Equal("AsyncStateMachineBox", AsyncStitchBoundary.AsyncStateMachineBoxTypeSuffix);
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
