// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Etlx;

using Xunit;

namespace Microsoft.Diagnostics.Tracing.Tests.Computers
{
    public sealed class AsyncStackTransformPipelineTests
    {
        [Fact]
        public void ConservativeTransforms_AreEnabledByDefault()
        {
            var diagnostics = new StitchDiagnostics();
            List<StitchedFrame> frames = StartupFrames();
            var pipeline = new AsyncStackTransformPipeline();

            Assert.False(pipeline.EnableActiveAsyncFrameAnnotations);
            pipeline.ApplyInPlace(Context(frames, diagnostics));

            Assert.Equal(new[] { CA(10), CA(11), CA(14) }, frames.Select(frame => frame.CodeAddress));
            Assert.Equal(
                StitchedFramePresentation.LogicalStateMachineMethod,
                frames[1].Presentation);
            Assert.Equal(1, diagnostics.V1SynchronousMoveNextFramesNormalized);
            Assert.Equal(2, diagnostics.V1SynchronousStartupFramesCollapsed);
        }

        [Fact]
        public void MaximumContextAncestryDepth_MustBePositive()
        {
            var pipeline = new AsyncStackTransformPipeline();

            Assert.Throws<ArgumentOutOfRangeException>(
                () => pipeline.MaximumContextAncestryDepth = 0);
            Assert.Equal(
                AsyncContextAncestryAugmenter.DefaultMaximumDepth,
                pipeline.MaximumContextAncestryDepth);
        }

        [Fact]
        public void ConservativeTransforms_CanBeDisabled()
        {
            var diagnostics = new StitchDiagnostics();
            List<StitchedFrame> frames = StartupFrames();
            var pipeline = new AsyncStackTransformPipeline
            {
                EnableConservativeTransforms = false,
            };

            pipeline.ApplyInPlace(Context(frames, diagnostics));

            Assert.Equal(
                new[] { CA(10), CA(11), CA(12), CA(13), CA(14) },
                frames.Select(frame => frame.CodeAddress));
            Assert.All(frames, frame => Assert.Equal(StitchedFramePresentation.Native, frame.Presentation));
            Assert.Equal(0, diagnostics.V1SynchronousMoveNextFramesNormalized);
            Assert.Equal(0, diagnostics.V1SynchronousStartupFramesCollapsed);
        }

        [Fact]
        public void ConservativeTransforms_NormalizeStandaloneV1MoveNext()
        {
            var diagnostics = new StitchDiagnostics();
            var frames = new List<StitchedFrame>
            {
                Sync(15),
                Sync(16, StitchSyncFrameKind.V1StateMachineMoveNext),
                Sync(17),
            };
            var pipeline = new AsyncStackTransformPipeline();

            pipeline.ApplyInPlace(Context(frames, diagnostics));

            Assert.Equal(new[] { CA(15), CA(16), CA(17) }, frames.Select(frame => frame.CodeAddress));
            Assert.Equal(
                StitchedFramePresentation.LogicalStateMachineMethod,
                frames[1].Presentation);
            Assert.Equal(1, diagnostics.V1SynchronousMoveNextFramesNormalized);
            Assert.Equal(0, diagnostics.V1SynchronousStartupFramesCollapsed);
        }

        [Fact]
        public void ConservativeTransforms_DropV2SampledLeafWrapper()
        {
            var diagnostics = new StitchDiagnostics
            {
                V2SyncLayoutUsed = 1,
            };
            var frames = new List<StitchedFrame>
            {
                Sync(20),
                Sync(21),
                Sync(22),
            };
            var pipeline = new AsyncStackTransformPipeline();

            pipeline.ApplyInPlace(Context(
                frames,
                diagnostics,
                wrapperCodeAddress: CA(20)));

            Assert.Equal(new[] { CA(21), CA(22) }, frames.Select(frame => frame.CodeAddress));
            Assert.Equal(1, diagnostics.V2LeafWrapperDropped);
        }

        [Fact]
        public void AsyncInfrastructureCleanup_IsOptIn()
        {
            var diagnostics = new StitchDiagnostics();
            var frames = new List<StitchedFrame>
            {
                Sync(30),
                Sync(31),
                Sync(32),
            };
            var pipeline = new AsyncStackTransformPipeline
            {
                EnableConservativeTransforms = false,
            };
            AsyncStackTransformContext context = Context(
                frames,
                diagnostics,
                infrastructureCodeAddress: CA(31));

            Assert.False(pipeline.EnableAsyncInfrastructureCleanup);
            pipeline.ApplyInPlace(context);
            Assert.Equal(new[] { CA(30), CA(31), CA(32) }, frames.Select(frame => frame.CodeAddress));

            pipeline.EnableAsyncInfrastructureCleanup = true;
            pipeline.ApplyInPlace(context);
            Assert.Equal(new[] { CA(30), CA(32) }, frames.Select(frame => frame.CodeAddress));
            Assert.Equal(1, diagnostics.AsyncInfrastructureFramesCollapsed);
        }

        [Fact]
        public void AsyncInfrastructureCleanup_DispatcherCreation_IsOptInAndPreservesTheApplicationFrame()
        {
            var diagnostics = new StitchDiagnostics();
            var frames = new List<StitchedFrame>
            {
                Sync(34, StitchSyncFrameKind.V1DispatcherCreationInfrastructure),
                Sync(35),
                Sync(36, StitchSyncFrameKind.SystemPrivateCoreLibThreadPoolDispatch),
            };
            var pipeline = new AsyncStackTransformPipeline();
            AsyncStackTransformContext context = Context(frames, diagnostics);

            pipeline.ApplyInPlace(context);
            Assert.Equal(new[] { CA(34), CA(35), CA(36) }, frames.Select(frame => frame.CodeAddress));
            Assert.Equal(0, diagnostics.AsyncInfrastructureFramesCollapsed);

            pipeline.EnableAsyncInfrastructureCleanup = true;
            pipeline.ApplyInPlace(context);
            Assert.Equal(new[] { CA(35), CA(36) }, frames.Select(frame => frame.CodeAddress));
            Assert.Equal(1, diagnostics.AsyncInfrastructureFramesCollapsed);
        }

        [Fact]
        public void AsyncInfrastructureCleanup_RemovesBuilderAwaitUnsafeOnCompletedOverloads()
        {
            var diagnostics = new StitchDiagnostics();
            var frames = new List<StitchedFrame>
            {
                Sync(40),
                Sync(41, StitchSyncFrameKind.V1MethodBuilderAwaitUnsafeOnCompleted),
                Sync(42, StitchSyncFrameKind.V1MethodBuilderAwaitUnsafeOnCompleted),
                Sync(43, StitchSyncFrameKind.V1MethodBuilderAwaitUnsafeOnCompleted),
                Sync(44, StitchSyncFrameKind.V1MethodBuilderInfrastructure),
                Sync(45, StitchSyncFrameKind.V1MethodBuilderCompletion),
                Sync(46, StitchSyncFrameKind.V1AwaiterRegistrationInfrastructure),
                Sync(47, StitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure),
                Sync(48, StitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure),
                Sync(49), // User/custom awaiter, builder, or bridge method remains ordinary.
                Sync(50, StitchSyncFrameKind.SystemPrivateCoreLibThreadPoolDispatch),
            };
            var pipeline = new AsyncStackTransformPipeline
            {
                EnableConservativeTransforms = false,
                EnableAsyncInfrastructureCleanup = true,
            };

            pipeline.ApplyInPlace(Context(frames, diagnostics));

            Assert.Equal(new[] { CA(40), CA(49), CA(50) }, frames.Select(frame => frame.CodeAddress));
            Assert.Equal(8, diagnostics.AsyncInfrastructureFramesCollapsed);
        }

        [Fact]
        public void AsyncInfrastructureCleanup_RemovesCompleteTaskCompletionChain()
        {
            var diagnostics = new StitchDiagnostics();
            var frames = new List<StitchedFrame>
            {
                Sync(60),
                Sync(61, StitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations),
                Sync(62, StitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult),
                Sync(63, StitchSyncFrameKind.SystemPrivateCoreLibTaskCompletionSourceSetResult),
                Sync(64),
            };
            var pipeline = new AsyncStackTransformPipeline
            {
                EnableConservativeTransforms = false,
                EnableAsyncInfrastructureCleanup = true,
            };

            pipeline.ApplyInPlace(Context(frames, diagnostics));

            Assert.Equal(new[] { CA(60), CA(64) }, frames.Select(frame => frame.CodeAddress));
            Assert.Equal(3, diagnostics.AsyncInfrastructureFramesCollapsed);
        }

        [Fact]
        public void AsyncInfrastructureCleanup_PreservesIsolatedTaskCompletionSourceSetResult()
        {
            var diagnostics = new StitchDiagnostics();
            var frames = new List<StitchedFrame>
            {
                Sync(70),
                Sync(71, StitchSyncFrameKind.SystemPrivateCoreLibTaskCompletionSourceSetResult),
                Sync(72),
            };
            var pipeline = new AsyncStackTransformPipeline
            {
                EnableConservativeTransforms = false,
                EnableAsyncInfrastructureCleanup = true,
            };

            pipeline.ApplyInPlace(Context(frames, diagnostics));

            Assert.Equal(new[] { CA(70), CA(71), CA(72) }, frames.Select(frame => frame.CodeAddress));
            Assert.Equal(0, diagnostics.AsyncInfrastructureFramesCollapsed);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void AsyncInfrastructureCleanup_TruncatedPrefixBeforeCompleteChains_IsBoundsSafe(int chainCount)
        {
            var diagnostics = new StitchDiagnostics();
            var frames = new List<StitchedFrame>
            {
                Sync(80, StitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations),
                Sync(81, StitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult),
            };
            for (int i = 0; i < chainCount; i++)
            {
                frames.Add(Sync(82, StitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations));
                frames.Add(Sync(83, StitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult));
                frames.Add(Sync(84, StitchSyncFrameKind.SystemPrivateCoreLibTaskCompletionSourceSetResult));
            }
            var pipeline = new AsyncStackTransformPipeline
            {
                EnableConservativeTransforms = false,
                EnableAsyncInfrastructureCleanup = true,
            };

            pipeline.ApplyInPlace(Context(frames, diagnostics));

            Assert.Empty(frames);
            Assert.Equal(2 + 3 * chainCount, diagnostics.AsyncInfrastructureFramesCollapsed);
        }

        [Theory]
        [InlineData("", 0, "")]
        [InlineData("R", 1, "")]
        [InlineData("RT", 2, "")]
        [InlineData("RS", 1, "S")]
        [InlineData("TRS", 2, "S")]
        [InlineData("RATS", 2, "AS")]
        [InlineData("RSTR", 3, "S")]
        [InlineData("RTRTSS", 5, "S")]
        public void AsyncInfrastructureCleanup_IncompleteChains_PreserveSetResult(string pattern, int collapsed, string expected)
        {
            var diagnostics = new StitchDiagnostics();
            var frames = new List<StitchedFrame>();
            foreach (char frame in pattern)
            {
                StitchSyncFrameKind kind = frame == 'R' ? StitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations
                    : frame == 'T' ? StitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult
                    : frame == 'S' ? StitchSyncFrameKind.SystemPrivateCoreLibTaskCompletionSourceSetResult
                    : StitchSyncFrameKind.None;
                frames.Add(Sync(frame, kind));
            }
            var pipeline = new AsyncStackTransformPipeline
            {
                EnableConservativeTransforms = false,
                EnableAsyncInfrastructureCleanup = true,
            };

            pipeline.ApplyInPlace(Context(frames, diagnostics));

            Assert.Equal(expected, new string(frames.ConvertAll(frame => (char)frame.CodeAddress).ToArray()));
            Assert.Equal(collapsed, diagnostics.AsyncInfrastructureFramesCollapsed);
        }

        [Fact]
        public void CustomTransforms_RunAfterBuiltInsInRegistrationOrder()
        {
            var diagnostics = new StitchDiagnostics();
            List<StitchedFrame> frames = StartupFrames();
            var observed = new List<int>();
            var pipeline = new AsyncStackTransformPipeline();
            pipeline.Add(context =>
            {
                observed.Add(context.Frames.Count);
                context.Frames.RemoveAt(0);
            });
            pipeline.Add(context => observed.Add(context.Frames.Count));

            pipeline.ApplyInPlace(Context(frames, diagnostics));

            Assert.Equal(new[] { 3, 2 }, observed);
            Assert.Equal(new[] { CA(11), CA(14) }, frames.Select(frame => frame.CodeAddress));
        }

        [Fact]
        public void Transform_AppliesPipelineWithoutComputerAndPreservesStructuralFrames()
        {
            var diagnostics = new StitchDiagnostics();
            List<StitchedFrame> structuralFrames = StartupFrames();
            var structuralResult = new StitchResult(structuralFrames, diagnostics);
            var pipeline = new AsyncStackTransformPipeline();

            StitchResult transformed = pipeline.Transform(
                structuralResult,
                segments: new List<AsyncCallStack>(),
                timestampQpc: 1,
                classifyBoundary: _ => AsyncStitchBoundaryInfo.None);

            Assert.Equal(
                new[] { CA(10), CA(11), CA(12), CA(13), CA(14) },
                structuralResult.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(
                new[] { CA(10), CA(11), CA(14) },
                transformed.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(
                StitchedFramePresentation.LogicalStateMachineMethod,
                transformed.Frames[1].Presentation);
            Assert.Same(structuralResult.Diagnostics, transformed.Diagnostics);
        }

        private static List<StitchedFrame> StartupFrames() =>
            new List<StitchedFrame>
            {
                Sync(10),
                Sync(11, StitchSyncFrameKind.V1StateMachineMoveNext),
                Sync(12, StitchSyncFrameKind.V1MethodBuilderStart),
                Sync(13, StitchSyncFrameKind.V1MethodBuilderStart),
                Sync(14),
            };

        private static StitchedFrame Sync(int codeAddress, StitchSyncFrameKind kind = StitchSyncFrameKind.None) =>
            StitchedFrame.CreateSync(
                new StitchSyncFrame(CA(codeAddress), MethodIndex.Invalid, kind),
                null);

        private static AsyncStackTransformContext Context(
            List<StitchedFrame> frames,
            StitchDiagnostics diagnostics,
            CodeAddressIndex wrapperCodeAddress = CodeAddressIndex.Invalid,
            CodeAddressIndex infrastructureCodeAddress = CodeAddressIndex.Invalid) =>
            new AsyncStackTransformContext(
                traceLog: null,
                segments: new List<AsyncCallStack>(),
                timestampQpc: 1,
                classifyBoundary: codeAddress =>
                {
                    if (codeAddress == wrapperCodeAddress)
                    {
                        return new AsyncStitchBoundaryInfo(
                            AsyncStitchBoundaryKind.V2ContinuationWrapper,
                            wrapperIndex: 0);
                    }
                    if (codeAddress == infrastructureCodeAddress)
                    {
                        return new AsyncStitchBoundaryInfo(
                            AsyncStitchBoundaryKind.V1DispatcherInfrastructure,
                            wrapperIndex: -1);
                    }
                    return AsyncStitchBoundaryInfo.None;
                },
                diagnostics,
                frames);

        private static CodeAddressIndex CA(int value) => (CodeAddressIndex)value;
    }
}
