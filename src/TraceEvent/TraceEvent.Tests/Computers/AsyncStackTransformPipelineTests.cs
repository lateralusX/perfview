// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

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
            Assert.Equal(new[] { CA(30), CA(31), CA(32) }, frames.Select(frame => frame.CodeAddress));
            Assert.Equal(StitchedFramePresentation.AsyncDispatcherFrames, frames[1].Presentation);
            Assert.Equal(1, diagnostics.AsyncInfrastructureFramesCollapsed);
        }

        [Theory]
        [InlineData(AsyncStitchBoundaryKind.V1Dispatcher, AsyncStitchBoundaryKind.V1DispatcherInfrastructure, false)]
        [InlineData(AsyncStitchBoundaryKind.V1Dispatcher, AsyncStitchBoundaryKind.V1DispatcherInfrastructure, true)]
        [InlineData(AsyncStitchBoundaryKind.V2DispatchContinuation, AsyncStitchBoundaryKind.V2DispatchContinuation, false)]
        [InlineData(AsyncStitchBoundaryKind.V2DispatchContinuation, AsyncStitchBoundaryKind.V2DispatchContinuation, true)]
        [InlineData(AsyncStitchBoundaryKind.V2ContinuationWrapper, AsyncStitchBoundaryKind.V2DispatchContinuation, false)]
        [InlineData(AsyncStitchBoundaryKind.V2ContinuationWrapper, AsyncStitchBoundaryKind.V2DispatchContinuation, true)]
        public void AsyncInfrastructureCleanup_RepresentsResidualDispatcherSpans(
            AsyncStitchBoundaryKind leafKind, AsyncStitchBoundaryKind rootKind, bool conservative)
        {
            var diagnostics = new StitchDiagnostics();
            var frames = new List<StitchedFrame> { Sync(30), Sync(31), Sync(32), Sync(33) };
            var pipeline = new AsyncStackTransformPipeline
            {
                EnableConservativeTransforms = conservative,
            };
            var context = Context(frames, diagnostics, classifyBoundary: ca =>
                new AsyncStitchBoundaryInfo(ca == CA(31) ? leafKind :
                    ca == CA(32) ? rootKind : AsyncStitchBoundaryKind.None, 0));

            pipeline.ApplyInPlace(context);
            Assert.Equal(new[] { CA(30), CA(31), CA(32), CA(33) }, frames.Select(frame => frame.CodeAddress));
            Assert.All(frames, frame => Assert.Equal(StitchedFramePresentation.Native, frame.Presentation));

            pipeline.EnableAsyncInfrastructureCleanup = true;
            pipeline.ApplyInPlace(context);

            Assert.Equal(new[] { CA(30), CA(31), CA(33) }, frames.Select(frame => frame.CodeAddress));
            Assert.Equal(StitchedFramePresentation.AsyncDispatcherFrames, frames[1].Presentation);
            Assert.Equal(StitchedFrameOrigin.Sync, frames[1].Origin);
            Assert.Null(frames[1].Context);
            Assert.Equal(2, diagnostics.AsyncInfrastructureFramesCollapsed);

            pipeline.ApplyInPlace(context);
            Assert.Equal(new[] { CA(30), CA(31), CA(33) }, frames.Select(frame => frame.CodeAddress));
            Assert.Equal(2, diagnostics.AsyncInfrastructureFramesCollapsed);
        }

        [Fact]
        public void AsyncInfrastructureCleanup_DoesNotMergeDispatcherSpansAcrossCallees()
        {
            var frames = new List<StitchedFrame> { Sync(30), Sync(31), Sync(32), Sync(33) };
            var diagnostics = new StitchDiagnostics();
            var pipeline = new AsyncStackTransformPipeline { EnableAsyncInfrastructureCleanup = true };

            pipeline.ApplyInPlace(Context(frames, diagnostics, classifyBoundary: ca =>
                new AsyncStitchBoundaryInfo(ca == CA(30) || ca == CA(32)
                    ? AsyncStitchBoundaryKind.V2DispatchContinuation : AsyncStitchBoundaryKind.None, -1)));

            Assert.Equal(new[] { CA(30), CA(31), CA(32), CA(33) }, frames.Select(frame => frame.CodeAddress));
            Assert.Equal(StitchedFramePresentation.AsyncDispatcherFrames, frames[0].Presentation);
            Assert.Equal(StitchedFramePresentation.Native, frames[1].Presentation);
            Assert.Equal(StitchedFramePresentation.AsyncDispatcherFrames, frames[2].Presentation);
            Assert.Equal(StitchedFramePresentation.Native, frames[3].Presentation);
            Assert.Equal(2, diagnostics.AsyncInfrastructureFramesCollapsed);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AsyncInfrastructureCleanup_PreservesDispatcherContextBoundaries(bool sameContext)
        {
            AsyncCallStack CreateContext() =>
                new AsyncCallStack(0, (AsyncCallStackFramesIndex)0,
                    new AsyncCallStackFrames(AsyncCallstackKind.StateMachineAsync, new ulong[] { 3 }, new[] { 0 }),
                    0, 32, 0, long.MaxValue, Array.Empty<AsyncCallStack.CompletionDelta>(),
                    Array.Empty<AsyncCallStack.CompletionDelta>(), Array.Empty<long>());
            AsyncCallStack leafContext = CreateContext();
            AsyncCallStack rootContext = sameContext ? leafContext : CreateContext();
            var frames = new List<StitchedFrame>
            {
                StitchedFrame.CreateSync(new StitchSyncFrame(CA(30), MethodIndex.Invalid), leafContext),
                StitchedFrame.CreateSync(new StitchSyncFrame(CA(31), MethodIndex.Invalid), rootContext),
                Sync(32),
            };
            var diagnostics = new StitchDiagnostics();
            var pipeline = new AsyncStackTransformPipeline { EnableAsyncInfrastructureCleanup = true };

            pipeline.ApplyInPlace(Context(frames, diagnostics, classifyBoundary: ca =>
                new AsyncStitchBoundaryInfo(ca == CA(32) ? AsyncStitchBoundaryKind.None :
                    AsyncStitchBoundaryKind.V1DispatcherInfrastructure, -1)));

            Assert.Equal(sameContext ? 2 : 3, frames.Count);
            Assert.Equal(StitchedFramePresentation.AsyncDispatcherFrames, frames[0].Presentation);
            Assert.Same(leafContext, frames[0].Context);
            if (!sameContext)
            {
                Assert.Equal(StitchedFramePresentation.AsyncDispatcherFrames, frames[1].Presentation);
                Assert.Same(rootContext, frames[1].Context);
            }
            Assert.Equal(CA(32), frames[frames.Count - 1].CodeAddress);
            Assert.Equal(2, diagnostics.AsyncInfrastructureFramesCollapsed);
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
        public void Pipeline_RecomposesWhenFlagsChange()
        {
            var pipeline = new AsyncStackTransformPipeline();
            var observed = new List<int>();
            pipeline.Add(context => observed.Add(context.Frames.Count));
            bool[,] configurations =
            {
                { true, false },
                { false, false },
                { false, true },
                { true, true },
                { true, false },
                { false, false },
                { true, false },
            };

            for (int configuration = 0; configuration < configurations.GetLength(0); configuration++)
            {
                bool conservative = configurations[configuration, 0];
                bool cleanup = configurations[configuration, 1];
                pipeline.EnableConservativeTransforms = conservative;
                pipeline.EnableAsyncInfrastructureCleanup = cleanup;
                int[] expected = !conservative && !cleanup
                    ? new[] { 20, 10, 11, 12, 13, 14, 15, 16, 17 }
                    : cleanup
                        ? conservative ? new[] { 10, 11, 14, 16, 17 } : new[] { 20, 10, 11, 14, 16, 17 }
                        : new[] { 10, 11, 14, 15, 16, 17 };

                for (int sample = 0; sample < 2; sample++)
                {
                    observed.Clear();
                    var diagnostics = new StitchDiagnostics { V2SyncLayoutUsed = 1 };
                    List<StitchedFrame> frames = CompositionFrames();

                    pipeline.ApplyInPlace(Context(frames, diagnostics, wrapperCodeAddress: CA(20)));

                    Assert.Equal(expected.Select(CA), frames.Select(frame => frame.CodeAddress));
                    Assert.Equal(new[] { expected.Length }, observed);
                    Assert.Equal(conservative ? 1 : 0, diagnostics.V2LeafWrapperDropped);
                    Assert.Equal(conservative ? 1 : 0, diagnostics.V1SynchronousMoveNextFramesNormalized);
                    Assert.Equal(conservative ? 2 : 0, diagnostics.V1SynchronousStartupFramesCollapsed);
                    Assert.Equal(cleanup ? (conservative ? 1 : 4) : 0,
                        diagnostics.AsyncInfrastructureFramesCollapsed);
                    Assert.Equal(
                        conservative ? StitchedFramePresentation.LogicalStateMachineMethod
                            : StitchedFramePresentation.Native,
                        frames.Single(frame => frame.CodeAddress == CA(11)).Presentation);
                }
            }
        }

        [Fact]
        public void CustomTransforms_AddedAfterFirstSample_RunInRegistrationOrder()
        {
            var pipeline = new AsyncStackTransformPipeline { EnableConservativeTransforms = false };
            var observed = new List<int>();
            pipeline.ApplyInPlace(Context(StartupFrames(), new StitchDiagnostics()));

            pipeline.Add(context =>
            {
                observed.Add(context.Frames.Count);
                context.Frames.RemoveAt(0);
            });
            pipeline.ApplyInPlace(Context(StartupFrames(), new StitchDiagnostics()));
            Assert.Equal(new[] { 5 }, observed);

            pipeline.Add(context => observed.Add(context.Frames.Count));
            observed.Clear();
            pipeline.ApplyInPlace(Context(StartupFrames(), new StitchDiagnostics()));
            Assert.Equal(new[] { 5, 4 }, observed);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ClearCustomTransforms_PreservesEnabledBuiltIns(bool cleanup)
        {
            var pipeline = new AsyncStackTransformPipeline { EnableAsyncInfrastructureCleanup = cleanup };
            pipeline.Add(context => context.Frames.Clear());
            List<StitchedFrame> frames = CompositionFrames();
            pipeline.ApplyInPlace(Context(frames, new StitchDiagnostics(), wrapperCodeAddress: CA(20)));
            Assert.Empty(frames);

            pipeline.ClearCustomTransforms();
            var diagnostics = new StitchDiagnostics { V2SyncLayoutUsed = 1 };
            frames = CompositionFrames();
            pipeline.ApplyInPlace(Context(frames, diagnostics, wrapperCodeAddress: CA(20)));

            int[] expected = cleanup ? new[] { 10, 11, 14, 16, 17 } : new[] { 10, 11, 14, 15, 16, 17 };
            Assert.Equal(expected.Select(CA), frames.Select(frame => frame.CodeAddress));
            Assert.True(pipeline.EnableConservativeTransforms);
            Assert.Equal(cleanup, pipeline.EnableAsyncInfrastructureCleanup);
            Assert.Equal(1, diagnostics.V2LeafWrapperDropped);
            Assert.Equal(1, diagnostics.V1SynchronousMoveNextFramesNormalized);
            Assert.Equal(2, diagnostics.V1SynchronousStartupFramesCollapsed);
            Assert.Equal(cleanup ? 1 : 0, diagnostics.AsyncInfrastructureFramesCollapsed);
        }

        [Fact]
        public void BuiltInComponents_CanBeRegisteredThroughExistingDelegateAPI()
        {
            var pipeline = new AsyncStackTransformPipeline { EnableConservativeTransforms = false };
            pipeline.Add(new AsyncSampledV2WrapperTransform().Apply);
            pipeline.Add(new AsyncReentrantHandoffTransform().Apply);
            pipeline.Add(new AsyncV1PresentationTransform().Apply);
            pipeline.Add(new AsyncInfrastructureCleanupTransform().Apply);

            for (int sample = 0; sample < 2; sample++)
            {
                var diagnostics = new StitchDiagnostics { V2SyncLayoutUsed = 1 };
                List<StitchedFrame> frames = CompositionFrames();

                pipeline.ApplyInPlace(Context(frames, diagnostics, wrapperCodeAddress: CA(20)));

                Assert.Equal(new[] { CA(10), CA(11), CA(14), CA(16), CA(17) },
                    frames.Select(frame => frame.CodeAddress));
                Assert.Equal(1, diagnostics.V2LeafWrapperDropped);
                Assert.Equal(1, diagnostics.V1SynchronousMoveNextFramesNormalized);
                Assert.Equal(2, diagnostics.V1SynchronousStartupFramesCollapsed);
                Assert.Equal(1, diagnostics.AsyncInfrastructureFramesCollapsed);
            }
        }

        [Fact]
        public void Add_RejectsNullTransform()
        {
            var pipeline = new AsyncStackTransformPipeline();

            Assert.Throws<ArgumentNullException>(() => pipeline.Add(null));
        }

        [Fact]
        public void CustomTransformFailure_PropagatesAndStopsThePipeline()
        {
            var pipeline = new AsyncStackTransformPipeline();
            var failure = new InvalidOperationException("Custom transform failed.");
            bool laterTransformRan = false;
            pipeline.Add(_ => throw failure);
            pipeline.Add(_ => laterTransformRan = true);

            InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
                () => pipeline.ApplyInPlace(Context(StartupFrames(), new StitchDiagnostics())));

            Assert.Same(failure, actual);
            Assert.False(laterTransformRan);
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

        private static List<StitchedFrame> CompositionFrames()
        {
            List<StitchedFrame> frames = StartupFrames();
            frames.Insert(0, Sync(20));
            frames.Add(Sync(15, StitchSyncFrameKind.V1AwaiterRegistrationInfrastructure));
            frames.Add(Sync(16, StitchSyncFrameKind.SystemPrivateCoreLibTaskCompletionSourceSetResult));
            frames.Add(Sync(17, StitchSyncFrameKind.SystemPrivateCoreLibThreadPoolDispatch));
            return frames;
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
            CodeAddressIndex infrastructureCodeAddress = CodeAddressIndex.Invalid,
            Func<CodeAddressIndex, AsyncStitchBoundaryInfo> classifyBoundary = null) =>
            new AsyncStackTransformContext(
                traceLog: null,
                segments: new List<AsyncCallStack>(),
                timestampQpc: 1,
                classifyBoundary: classifyBoundary ?? (codeAddress =>
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
                }),
                diagnostics,
                frames);

        private static CodeAddressIndex CA(int value) => (CodeAddressIndex)value;
    }
}
