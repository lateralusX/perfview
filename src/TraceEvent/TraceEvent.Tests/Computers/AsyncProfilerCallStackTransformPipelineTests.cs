// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

using Xunit;

namespace TraceEventTests
{
    public sealed class AsyncProfilerCallStackTransformPipelineTests
    {
        [Fact]
        public void ConservativeTransforms_AreEnabledByDefault()
        {
            var diagnostics = new AsyncProfilerCallStackDiagnostics();
            List<AsyncProfilerStitchedFrame> frames = StartupFrames();
            var pipeline = new AsyncProfilerCallStackTransformPipeline();

            Assert.False(pipeline.EnableFrameAnnotations);
            pipeline.ApplyInPlace(Context(frames, diagnostics));

            Assert.Equal(new[] { CA(10), CA(11), CA(14) }, frames.Select(frame => frame.CodeAddress));
            Assert.Equal(
                AsyncProfilerStitchedFramePresentation.LogicalStateMachineMethod,
                frames[1].Presentation);
            Assert.Equal(1, diagnostics.V1SynchronousMoveNextFramesNormalized);
            Assert.Equal(2, diagnostics.V1SynchronousStartupFramesCollapsed);
        }

        [Fact]
        public void MaximumContextAncestryDepth_MustBePositive()
        {
            var pipeline = new AsyncProfilerCallStackTransformPipeline();

            Assert.Throws<ArgumentOutOfRangeException>(
                () => pipeline.MaximumContextAncestryDepth = 0);
            Assert.Equal(
                AsyncProfilerContextAncestryAugmenter.DefaultMaximumDepth,
                pipeline.MaximumContextAncestryDepth);
        }

        [Fact]
        public void ConservativeTransforms_CanBeDisabled()
        {
            var diagnostics = new AsyncProfilerCallStackDiagnostics();
            List<AsyncProfilerStitchedFrame> frames = StartupFrames();
            var pipeline = new AsyncProfilerCallStackTransformPipeline
            {
                EnableNormalization = false,
            };

            pipeline.ApplyInPlace(Context(frames, diagnostics));

            Assert.Equal(
                new[] { CA(10), CA(11), CA(12), CA(13), CA(14) },
                frames.Select(frame => frame.CodeAddress));
            Assert.All(frames, frame => Assert.Equal(AsyncProfilerStitchedFramePresentation.Native, frame.Presentation));
            Assert.Equal(0, diagnostics.V1SynchronousMoveNextFramesNormalized);
            Assert.Equal(0, diagnostics.V1SynchronousStartupFramesCollapsed);
        }

        [Fact]
        public void ConservativeTransforms_NormalizeStandaloneV1MoveNext()
        {
            var diagnostics = new AsyncProfilerCallStackDiagnostics();
            var frames = new List<AsyncProfilerStitchedFrame>
            {
                Sync(15),
                Sync(16, AsyncProfilerStitchSyncFrameKind.V1StateMachineMoveNext),
                Sync(17),
            };
            var pipeline = new AsyncProfilerCallStackTransformPipeline();

            pipeline.ApplyInPlace(Context(frames, diagnostics));

            Assert.Equal(new[] { CA(15), CA(16), CA(17) }, frames.Select(frame => frame.CodeAddress));
            Assert.Equal(
                AsyncProfilerStitchedFramePresentation.LogicalStateMachineMethod,
                frames[1].Presentation);
            Assert.Equal(1, diagnostics.V1SynchronousMoveNextFramesNormalized);
            Assert.Equal(0, diagnostics.V1SynchronousStartupFramesCollapsed);
        }

        [Fact]
        public void ConservativeTransforms_DropV2SampledLeafWrapper()
        {
            var diagnostics = new AsyncProfilerCallStackDiagnostics
            {
                V2SyncLayoutUsed = 1,
            };
            var frames = new List<AsyncProfilerStitchedFrame>
            {
                Sync(20),
                Sync(21),
                Sync(22),
            };
            var pipeline = new AsyncProfilerCallStackTransformPipeline();

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
            var diagnostics = new AsyncProfilerCallStackDiagnostics();
            var frames = new List<AsyncProfilerStitchedFrame>
            {
                Sync(30),
                Sync(31),
                Sync(32),
            };
            var pipeline = new AsyncProfilerCallStackTransformPipeline
            {
                EnableNormalization = false,
            };
            AsyncProfilerCallStackTransformContext context = Context(
                frames,
                diagnostics,
                infrastructureCodeAddress: CA(31));

            Assert.False(pipeline.EnableInfrastructureCleanup);
            pipeline.ApplyInPlace(context);
            Assert.Equal(new[] { CA(30), CA(31), CA(32) }, frames.Select(frame => frame.CodeAddress));

            pipeline.EnableInfrastructureCleanup = true;
            pipeline.ApplyInPlace(context);
            Assert.Equal(new[] { CA(30), CA(31), CA(32) }, frames.Select(frame => frame.CodeAddress));
            Assert.Equal(AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames, frames[1].Presentation);
            Assert.Equal(1, diagnostics.AsyncInfrastructureFramesCollapsed);
        }

        [Theory]
        [InlineData((int)AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher, (int)AsyncProfilerCallStackStitchBoundaryKind.V1DispatcherInfrastructure, false)]
        [InlineData((int)AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher, (int)AsyncProfilerCallStackStitchBoundaryKind.V1DispatcherInfrastructure, true)]
        [InlineData((int)AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation, (int)AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation, false)]
        [InlineData((int)AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation, (int)AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation, true)]
        [InlineData((int)AsyncProfilerCallStackStitchBoundaryKind.V2ContinuationWrapper, (int)AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation, false)]
        [InlineData((int)AsyncProfilerCallStackStitchBoundaryKind.V2ContinuationWrapper, (int)AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation, true)]
        public void AsyncInfrastructureCleanup_RepresentsResidualDispatcherSpans(
            int leafKindValue, int rootKindValue, bool enableNormalization)
        {
            var leafKind = (AsyncProfilerCallStackStitchBoundaryKind)leafKindValue;
            var rootKind = (AsyncProfilerCallStackStitchBoundaryKind)rootKindValue;
            var diagnostics = new AsyncProfilerCallStackDiagnostics();
            var frames = new List<AsyncProfilerStitchedFrame> { Sync(30), Sync(31), Sync(32), Sync(33) };
            var pipeline = new AsyncProfilerCallStackTransformPipeline
            {
                EnableNormalization = enableNormalization,
            };
            var context = Context(frames, diagnostics, classifyBoundary: ca =>
                new AsyncProfilerCallStackStitchBoundaryInfo(ca == CA(31) ? leafKind :
                    ca == CA(32) ? rootKind : AsyncProfilerCallStackStitchBoundaryKind.None, 0));

            pipeline.ApplyInPlace(context);
            Assert.Equal(new[] { CA(30), CA(31), CA(32), CA(33) }, frames.Select(frame => frame.CodeAddress));
            Assert.All(frames, frame => Assert.Equal(AsyncProfilerStitchedFramePresentation.Native, frame.Presentation));

            pipeline.EnableInfrastructureCleanup = true;
            pipeline.ApplyInPlace(context);

            Assert.Equal(new[] { CA(30), CA(31), CA(33) }, frames.Select(frame => frame.CodeAddress));
            Assert.Equal(AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames, frames[1].Presentation);
            Assert.Equal(AsyncProfilerStitchedFrameOrigin.Sync, frames[1].Origin);
            Assert.Null(frames[1].Context);
            Assert.Equal(2, diagnostics.AsyncInfrastructureFramesCollapsed);

            pipeline.ApplyInPlace(context);
            Assert.Equal(new[] { CA(30), CA(31), CA(33) }, frames.Select(frame => frame.CodeAddress));
            Assert.Equal(2, diagnostics.AsyncInfrastructureFramesCollapsed);
        }

        [Fact]
        public void AsyncInfrastructureCleanup_DoesNotMergeDispatcherSpansAcrossCallees()
        {
            var frames = new List<AsyncProfilerStitchedFrame> { Sync(30), Sync(31), Sync(32), Sync(33) };
            var diagnostics = new AsyncProfilerCallStackDiagnostics();
            var pipeline = new AsyncProfilerCallStackTransformPipeline { EnableInfrastructureCleanup = true };

            pipeline.ApplyInPlace(Context(frames, diagnostics, classifyBoundary: ca =>
                new AsyncProfilerCallStackStitchBoundaryInfo(ca == CA(30) || ca == CA(32)
                    ? AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation : AsyncProfilerCallStackStitchBoundaryKind.None, -1)));

            Assert.Equal(new[] { CA(30), CA(31), CA(32), CA(33) }, frames.Select(frame => frame.CodeAddress));
            Assert.Equal(AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames, frames[0].Presentation);
            Assert.Equal(AsyncProfilerStitchedFramePresentation.Native, frames[1].Presentation);
            Assert.Equal(AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames, frames[2].Presentation);
            Assert.Equal(AsyncProfilerStitchedFramePresentation.Native, frames[3].Presentation);
            Assert.Equal(2, diagnostics.AsyncInfrastructureFramesCollapsed);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AsyncInfrastructureCleanup_PreservesDispatcherContextBoundaries(bool sameContext)
        {
            AsyncProfilerCallStack CreateContext() =>
                new AsyncProfilerCallStack(0, (AsyncProfilerCallStackFramesIndex)0,
                    new AsyncProfilerCallStackFrames(AsyncCallstackKind.StateMachineAsync, new ulong[] { 3 }, new[] { 0 }),
                    0, 32, 0, long.MaxValue, Array.Empty<AsyncProfilerCallStack.CompletionDelta>(),
                    Array.Empty<AsyncProfilerCallStack.CompletionDelta>(), Array.Empty<long>());
            AsyncProfilerCallStack leafContext = CreateContext();
            AsyncProfilerCallStack rootContext = sameContext ? leafContext : CreateContext();
            var frames = new List<AsyncProfilerStitchedFrame>
            {
                AsyncProfilerStitchedFrame.CreateSync(new AsyncProfilerStitchSyncFrame(CA(30), MethodIndex.Invalid), leafContext),
                AsyncProfilerStitchedFrame.CreateSync(new AsyncProfilerStitchSyncFrame(CA(31), MethodIndex.Invalid), rootContext),
                Sync(32),
            };
            var diagnostics = new AsyncProfilerCallStackDiagnostics();
            var pipeline = new AsyncProfilerCallStackTransformPipeline { EnableInfrastructureCleanup = true };

            pipeline.ApplyInPlace(Context(frames, diagnostics, classifyBoundary: ca =>
                new AsyncProfilerCallStackStitchBoundaryInfo(ca == CA(32) ? AsyncProfilerCallStackStitchBoundaryKind.None :
                    AsyncProfilerCallStackStitchBoundaryKind.V1DispatcherInfrastructure, -1)));

            Assert.Equal(sameContext ? 2 : 3, frames.Count);
            Assert.Equal(AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames, frames[0].Presentation);
            Assert.Same(leafContext, frames[0].Context);
            if (!sameContext)
            {
                Assert.Equal(AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames, frames[1].Presentation);
                Assert.Same(rootContext, frames[1].Context);
            }
            Assert.Equal(CA(32), frames[frames.Count - 1].CodeAddress);
            Assert.Equal(2, diagnostics.AsyncInfrastructureFramesCollapsed);
        }

        [Fact]
        public void AsyncInfrastructureCleanup_DispatcherCreation_IsOptInAndPreservesTheApplicationFrame()
        {
            var diagnostics = new AsyncProfilerCallStackDiagnostics();
            var frames = new List<AsyncProfilerStitchedFrame>
            {
                Sync(34, AsyncProfilerStitchSyncFrameKind.V1DispatcherCreationInfrastructure),
                Sync(35),
                Sync(36, AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibThreadPoolDispatch),
            };
            var pipeline = new AsyncProfilerCallStackTransformPipeline();
            AsyncProfilerCallStackTransformContext context = Context(frames, diagnostics);

            pipeline.ApplyInPlace(context);
            Assert.Equal(new[] { CA(34), CA(35), CA(36) }, frames.Select(frame => frame.CodeAddress));
            Assert.Equal(0, diagnostics.AsyncInfrastructureFramesCollapsed);

            pipeline.EnableInfrastructureCleanup = true;
            pipeline.ApplyInPlace(context);
            Assert.Equal(new[] { CA(35), CA(36) }, frames.Select(frame => frame.CodeAddress));
            Assert.Equal(1, diagnostics.AsyncInfrastructureFramesCollapsed);
        }

        [Fact]
        public void AsyncInfrastructureCleanup_RemovesBuilderAwaitUnsafeOnCompletedOverloads()
        {
            var diagnostics = new AsyncProfilerCallStackDiagnostics();
            var frames = new List<AsyncProfilerStitchedFrame>
            {
                Sync(40),
                Sync(41, AsyncProfilerStitchSyncFrameKind.V1MethodBuilderAwaitUnsafeOnCompleted),
                Sync(42, AsyncProfilerStitchSyncFrameKind.V1MethodBuilderAwaitUnsafeOnCompleted),
                Sync(43, AsyncProfilerStitchSyncFrameKind.V1MethodBuilderAwaitUnsafeOnCompleted),
                Sync(44, AsyncProfilerStitchSyncFrameKind.V1MethodBuilderInfrastructure),
                Sync(45, AsyncProfilerStitchSyncFrameKind.V1MethodBuilderCompletion),
                Sync(46, AsyncProfilerStitchSyncFrameKind.V1AwaiterRegistrationInfrastructure),
                Sync(47, AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure),
                Sync(48, AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure),
                Sync(49), // User/custom awaiter, builder, or bridge method remains ordinary.
                Sync(50, AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibThreadPoolDispatch),
            };
            var pipeline = new AsyncProfilerCallStackTransformPipeline
            {
                EnableNormalization = false,
                EnableInfrastructureCleanup = true,
            };

            pipeline.ApplyInPlace(Context(frames, diagnostics));

            Assert.Equal(new[] { CA(40), CA(49), CA(50) }, frames.Select(frame => frame.CodeAddress));
            Assert.Equal(8, diagnostics.AsyncInfrastructureFramesCollapsed);
        }

        [Fact]
        public void AsyncInfrastructureCleanup_RemovesCompleteTaskCompletionChain()
        {
            var diagnostics = new AsyncProfilerCallStackDiagnostics();
            var frames = new List<AsyncProfilerStitchedFrame>
            {
                Sync(60),
                Sync(61, AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations),
                Sync(62, AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult),
                Sync(63, AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskCompletionSourceSetResult),
                Sync(64),
            };
            var pipeline = new AsyncProfilerCallStackTransformPipeline
            {
                EnableNormalization = false,
                EnableInfrastructureCleanup = true,
            };

            pipeline.ApplyInPlace(Context(frames, diagnostics));

            Assert.Equal(new[] { CA(60), CA(64) }, frames.Select(frame => frame.CodeAddress));
            Assert.Equal(3, diagnostics.AsyncInfrastructureFramesCollapsed);
        }

        [Fact]
        public void AsyncInfrastructureCleanup_PreservesIsolatedTaskCompletionSourceSetResult()
        {
            var diagnostics = new AsyncProfilerCallStackDiagnostics();
            var frames = new List<AsyncProfilerStitchedFrame>
            {
                Sync(70),
                Sync(71, AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskCompletionSourceSetResult),
                Sync(72),
            };
            var pipeline = new AsyncProfilerCallStackTransformPipeline
            {
                EnableNormalization = false,
                EnableInfrastructureCleanup = true,
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
            var diagnostics = new AsyncProfilerCallStackDiagnostics();
            var frames = new List<AsyncProfilerStitchedFrame>
            {
                Sync(80, AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations),
                Sync(81, AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult),
            };
            for (int i = 0; i < chainCount; i++)
            {
                frames.Add(Sync(82, AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations));
                frames.Add(Sync(83, AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult));
                frames.Add(Sync(84, AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskCompletionSourceSetResult));
            }
            var pipeline = new AsyncProfilerCallStackTransformPipeline
            {
                EnableNormalization = false,
                EnableInfrastructureCleanup = true,
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
            var diagnostics = new AsyncProfilerCallStackDiagnostics();
            var frames = new List<AsyncProfilerStitchedFrame>();
            foreach (char frame in pattern)
            {
                AsyncProfilerStitchSyncFrameKind kind = frame == 'R' ? AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations
                    : frame == 'T' ? AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult
                    : frame == 'S' ? AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskCompletionSourceSetResult
                    : AsyncProfilerStitchSyncFrameKind.None;
                frames.Add(Sync(frame, kind));
            }
            var pipeline = new AsyncProfilerCallStackTransformPipeline
            {
                EnableNormalization = false,
                EnableInfrastructureCleanup = true,
            };

            pipeline.ApplyInPlace(Context(frames, diagnostics));

            Assert.Equal(expected, new string(frames.ConvertAll(frame => (char)frame.CodeAddress).ToArray()));
            Assert.Equal(collapsed, diagnostics.AsyncInfrastructureFramesCollapsed);
        }

        [Fact]
        public void CustomTransforms_RunAfterBuiltInsInRegistrationOrder()
        {
            var diagnostics = new AsyncProfilerCallStackDiagnostics();
            List<AsyncProfilerStitchedFrame> frames = StartupFrames();
            var observed = new List<int>();
            var pipeline = new AsyncProfilerCallStackTransformPipeline();
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
            var pipeline = new AsyncProfilerCallStackTransformPipeline();
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
                bool enableNormalization = configurations[configuration, 0];
                bool enableInfrastructureCleanup = configurations[configuration, 1];
                pipeline.EnableNormalization = enableNormalization;
                pipeline.EnableInfrastructureCleanup = enableInfrastructureCleanup;
                int[] expected = !enableNormalization && !enableInfrastructureCleanup
                    ? new[] { 20, 10, 11, 12, 13, 14, 15, 16, 17 }
                    : enableInfrastructureCleanup
                        ? enableNormalization ? new[] { 10, 11, 14, 16, 17 } : new[] { 20, 10, 11, 14, 16, 17 }
                        : new[] { 10, 11, 14, 15, 16, 17 };

                for (int sample = 0; sample < 2; sample++)
                {
                    observed.Clear();
                    var diagnostics = new AsyncProfilerCallStackDiagnostics { V2SyncLayoutUsed = 1 };
                    List<AsyncProfilerStitchedFrame> frames = CompositionFrames();

                    pipeline.ApplyInPlace(Context(frames, diagnostics, wrapperCodeAddress: CA(20)));

                    Assert.Equal(expected.Select(CA), frames.Select(frame => frame.CodeAddress));
                    Assert.Equal(new[] { expected.Length }, observed);
                    Assert.Equal(enableNormalization ? 1 : 0, diagnostics.V2LeafWrapperDropped);
                    Assert.Equal(enableNormalization ? 1 : 0, diagnostics.V1SynchronousMoveNextFramesNormalized);
                    Assert.Equal(enableNormalization ? 2 : 0, diagnostics.V1SynchronousStartupFramesCollapsed);
                    Assert.Equal(enableInfrastructureCleanup ? (enableNormalization ? 1 : 4) : 0,
                        diagnostics.AsyncInfrastructureFramesCollapsed);
                    Assert.Equal(
                        enableNormalization ? AsyncProfilerStitchedFramePresentation.LogicalStateMachineMethod
                            : AsyncProfilerStitchedFramePresentation.Native,
                        frames.Single(frame => frame.CodeAddress == CA(11)).Presentation);
                }
            }
        }

        [Fact]
        public void CustomTransforms_AddedAfterFirstSample_RunInRegistrationOrder()
        {
            var pipeline = new AsyncProfilerCallStackTransformPipeline { EnableNormalization = false };
            var observed = new List<int>();
            pipeline.ApplyInPlace(Context(StartupFrames(), new AsyncProfilerCallStackDiagnostics()));

            pipeline.Add(context =>
            {
                observed.Add(context.Frames.Count);
                context.Frames.RemoveAt(0);
            });
            pipeline.ApplyInPlace(Context(StartupFrames(), new AsyncProfilerCallStackDiagnostics()));
            Assert.Equal(new[] { 5 }, observed);

            pipeline.Add(context => observed.Add(context.Frames.Count));
            observed.Clear();
            pipeline.ApplyInPlace(Context(StartupFrames(), new AsyncProfilerCallStackDiagnostics()));
            Assert.Equal(new[] { 5, 4 }, observed);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ClearCustomTransforms_PreservesEnabledBuiltIns(bool enableInfrastructureCleanup)
        {
            var pipeline = new AsyncProfilerCallStackTransformPipeline { EnableInfrastructureCleanup = enableInfrastructureCleanup };
            pipeline.Add(context => context.Frames.Clear());
            List<AsyncProfilerStitchedFrame> frames = CompositionFrames();
            pipeline.ApplyInPlace(Context(frames, new AsyncProfilerCallStackDiagnostics(), wrapperCodeAddress: CA(20)));
            Assert.Empty(frames);

            pipeline.ClearCustomTransforms();
            var diagnostics = new AsyncProfilerCallStackDiagnostics { V2SyncLayoutUsed = 1 };
            frames = CompositionFrames();
            pipeline.ApplyInPlace(Context(frames, diagnostics, wrapperCodeAddress: CA(20)));

            int[] expected = enableInfrastructureCleanup ? new[] { 10, 11, 14, 16, 17 } : new[] { 10, 11, 14, 15, 16, 17 };
            Assert.Equal(expected.Select(CA), frames.Select(frame => frame.CodeAddress));
            Assert.True(pipeline.EnableNormalization);
            Assert.Equal(enableInfrastructureCleanup, pipeline.EnableInfrastructureCleanup);
            Assert.Equal(1, diagnostics.V2LeafWrapperDropped);
            Assert.Equal(1, diagnostics.V1SynchronousMoveNextFramesNormalized);
            Assert.Equal(2, diagnostics.V1SynchronousStartupFramesCollapsed);
            Assert.Equal(enableInfrastructureCleanup ? 1 : 0, diagnostics.AsyncInfrastructureFramesCollapsed);
        }

        [Fact]
        public void BuiltInComponents_CanBeRegisteredThroughExistingDelegateAPI()
        {
            var pipeline = new AsyncProfilerCallStackTransformPipeline { EnableNormalization = false };
            pipeline.Add(new AsyncProfilerSampledV2WrapperTransform().Apply);
            pipeline.Add(new AsyncProfilerReentrantHandoffTransform().Apply);
            pipeline.Add(new AsyncProfilerV1PresentationTransform().Apply);
            pipeline.Add(new AsyncProfilerInfrastructureCleanupTransform().Apply);

            for (int sample = 0; sample < 2; sample++)
            {
                var diagnostics = new AsyncProfilerCallStackDiagnostics { V2SyncLayoutUsed = 1 };
                List<AsyncProfilerStitchedFrame> frames = CompositionFrames();

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
            var pipeline = new AsyncProfilerCallStackTransformPipeline();

            Assert.Throws<ArgumentNullException>(() => pipeline.Add(null));
        }

        [Fact]
        public void CustomTransformFailure_PropagatesAndStopsThePipeline()
        {
            var pipeline = new AsyncProfilerCallStackTransformPipeline();
            var failure = new InvalidOperationException("Custom transform failed.");
            bool laterTransformRan = false;
            pipeline.Add(_ => throw failure);
            pipeline.Add(_ => laterTransformRan = true);

            InvalidOperationException actual = Assert.Throws<InvalidOperationException>(
                () => pipeline.ApplyInPlace(Context(StartupFrames(), new AsyncProfilerCallStackDiagnostics())));

            Assert.Same(failure, actual);
            Assert.False(laterTransformRan);
        }

        [Fact]
        public void Transform_AppliesPipelineWithoutComputerAndPreservesStructuralFrames()
        {
            var diagnostics = new AsyncProfilerCallStackDiagnostics();
            List<AsyncProfilerStitchedFrame> structuralFrames = StartupFrames();
            var structuralResult = new AsyncProfilerStitchResult(structuralFrames, diagnostics);
            var pipeline = new AsyncProfilerCallStackTransformPipeline();

            AsyncProfilerStitchResult transformed = pipeline.Transform(
                structuralResult,
                segments: new List<AsyncProfilerCallStack>(),
                timestampQpc: 1,
                classifyBoundary: _ => AsyncProfilerCallStackStitchBoundaryInfo.None);

            Assert.Equal(
                new[] { CA(10), CA(11), CA(12), CA(13), CA(14) },
                structuralResult.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(
                new[] { CA(10), CA(11), CA(14) },
                transformed.Frames.Select(frame => frame.CodeAddress));
            Assert.Equal(
                AsyncProfilerStitchedFramePresentation.LogicalStateMachineMethod,
                transformed.Frames[1].Presentation);
            Assert.Same(structuralResult.Diagnostics, transformed.Diagnostics);
        }

        private static List<AsyncProfilerStitchedFrame> CompositionFrames()
        {
            List<AsyncProfilerStitchedFrame> frames = StartupFrames();
            frames.Insert(0, Sync(20));
            frames.Add(Sync(15, AsyncProfilerStitchSyncFrameKind.V1AwaiterRegistrationInfrastructure));
            frames.Add(Sync(16, AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskCompletionSourceSetResult));
            frames.Add(Sync(17, AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibThreadPoolDispatch));
            return frames;
        }

        private static List<AsyncProfilerStitchedFrame> StartupFrames() =>
            new List<AsyncProfilerStitchedFrame>
            {
                Sync(10),
                Sync(11, AsyncProfilerStitchSyncFrameKind.V1StateMachineMoveNext),
                Sync(12, AsyncProfilerStitchSyncFrameKind.V1MethodBuilderStart),
                Sync(13, AsyncProfilerStitchSyncFrameKind.V1MethodBuilderStart),
                Sync(14),
            };

        private static AsyncProfilerStitchedFrame Sync(int codeAddress, AsyncProfilerStitchSyncFrameKind kind = AsyncProfilerStitchSyncFrameKind.None) =>
            AsyncProfilerStitchedFrame.CreateSync(
                new AsyncProfilerStitchSyncFrame(CA(codeAddress), MethodIndex.Invalid, kind),
                null);

        private static AsyncProfilerCallStackTransformContext Context(
            List<AsyncProfilerStitchedFrame> frames,
            AsyncProfilerCallStackDiagnostics diagnostics,
            CodeAddressIndex wrapperCodeAddress = CodeAddressIndex.Invalid,
            CodeAddressIndex infrastructureCodeAddress = CodeAddressIndex.Invalid,
            Func<CodeAddressIndex, AsyncProfilerCallStackStitchBoundaryInfo> classifyBoundary = null) =>
            new AsyncProfilerCallStackTransformContext(
                traceLog: null,
                segments: new List<AsyncProfilerCallStack>(),
                timestampQpc: 1,
                classifyBoundary: classifyBoundary ?? (codeAddress =>
                {
                    if (codeAddress == wrapperCodeAddress)
                    {
                        return new AsyncProfilerCallStackStitchBoundaryInfo(
                            AsyncProfilerCallStackStitchBoundaryKind.V2ContinuationWrapper,
                            wrapperIndex: 0);
                    }
                    if (codeAddress == infrastructureCodeAddress)
                    {
                        return new AsyncProfilerCallStackStitchBoundaryInfo(
                            AsyncProfilerCallStackStitchBoundaryKind.V1DispatcherInfrastructure,
                            wrapperIndex: -1);
                    }
                    return AsyncProfilerCallStackStitchBoundaryInfo.None;
                }),
                diagnostics,
                frames);

        private static CodeAddressIndex CA(int value) => (CodeAddressIndex)value;
    }
}
