// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Diagnostics.Tracing.Computers;
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
        [InlineData("System.Runtime.CompilerServices.AsyncProfiler+Other.Resume()", true, (int)AsyncProfilerStitchSyncFrameKind.None)]
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
