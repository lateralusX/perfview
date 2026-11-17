// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

using Xunit;

using static Microsoft.Diagnostics.Tracing.Computers.AsyncProfilerRuntimeSupport;

namespace TraceEventTests
{
    /// <summary>Exercises runtime-support overrides and the ownership checks that keep their layouts separate.</summary>
    public class AsyncProfilerRuntimeSupportExtensibilityTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void ReplacedStages_PreserveFlagsOrderAndPerSampleSelection(bool enableNormalization, bool enableInfrastructureCleanup)
        {
            var calls = new List<string>();
            var first = new StageSupport("First", calls);
            var second = new StageSupport("Second", calls);
            var pipeline = new AsyncProfilerCallStackTransformPipeline
            {
                EnableNormalization = enableNormalization,
                EnableInfrastructureCleanup = enableInfrastructureCleanup,
            };
            pipeline.AddCustomTransform(context => calls.Add("Custom"));

            foreach (StageSupport support in new[] { first, second })
            {
                pipeline.ApplyInPlace(Context(new List<AsyncProfilerStitchedFrame> { Sync(1) }, support));
            }

            var expected = new List<string>();
            foreach (string name in new[] { "First", "Second" })
            {
                if (enableNormalization)
                {
                    expected.Add(name + ":SampledV2Wrapper");
                    expected.Add(name + ":ReentrantHandoff");
                    expected.Add(name + ":V1Presentation");
                }
                if (enableInfrastructureCleanup)
                {
                    expected.Add(name + ":InfrastructureCleanup");
                }
                expected.Add("Custom");
            }
            Assert.Equal(expected, calls);

            calls.Clear();
            pipeline.EnableNormalization = !enableNormalization;
            pipeline.EnableInfrastructureCleanup = !enableInfrastructureCleanup;
            pipeline.ApplyInPlace(Context(new List<AsyncProfilerStitchedFrame> { Sync(1) }, first));
            Assert.Equal((enableNormalization ? 0 : 3) + (enableInfrastructureCleanup ? 0 : 1) + 1, calls.Count);
            Assert.Equal("Custom", calls.Last());
        }

        [Fact]
        public void MixedRuntimeSupport_DoNotSelectAnArbitraryReplacement()
        {
            var calls = new List<string>();
            var first = new StageSupport("First", calls);
            var second = new StageSupport("Second", calls);
            var frames = new List<AsyncProfilerStitchedFrame> { Sync(1), Sync(2) };
            var context = new AsyncProfilerCallStackTransformState(null, new List<AsyncProfilerCallStack>(), 1,
                _ => AsyncProfilerCallStackStitchBoundaryInfo.None, new AsyncProfilerCallStackStitchingState(), new AsyncProfilerCallStackDiagnostics(), frames,
                ca => (int)ca == 1 ? first : second);
            Assert.Same(AsyncProfilerRuntimeSupport.Common, context.RuntimeSupport);
            new AsyncProfilerCallStackTransformPipeline().ApplyInPlace(context);
            Assert.Empty(calls);
            Assert.Equal(2, frames.Count);
        }

        [Fact]
        public void ReplacementSelection_SurvivesRemovalOfAllRuntimeEvidence()
        {
            var calls = new List<string>();
            var support = new StageSupport("Selected", calls);
            var frames = new List<AsyncProfilerStitchedFrame> { Sync(1) };
            new AsyncProfilerCallStackTransformPipeline { EnableInfrastructureCleanup = true }
                .ApplyInPlace(Context(frames, support));
            Assert.Empty(frames);
            Assert.Equal(4, calls.Count);
        }

        [Fact]
        public void InvalidReplacement_FailsExplicitly()
        {
            var context = Context(new List<AsyncProfilerStitchedFrame> { Sync(1) }, new NullStageSupport());
            Assert.Throws<InvalidOperationException>(() => new AsyncProfilerCallStackTransformPipeline().ApplyInPlace(context));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void StartupLayout_UsesOwningRuntimeRatherThanApplicationModule(bool foreignHelper)
        {
            var support = new AsyncProfilerExtendedRuntimeLayoutSupport();
            var frames = new List<AsyncProfilerStitchedFrame>
            {
                Sync(1, AsyncProfilerStitchSyncFrameKind.V1StateMachineBody),
                Sync(2, AsyncProfilerStitchSyncFrameKind.V1MethodHelper),
                Sync(3, RuntimeFrameKind.V1Startup),
                Sync(4),
            };
            var context = new AsyncProfilerCallStackTransformState(null, new List<AsyncProfilerCallStack>(), 1,
                _ => AsyncProfilerCallStackStitchBoundaryInfo.None, new AsyncProfilerCallStackStitchingState(), new AsyncProfilerCallStackDiagnostics(), frames,
                ca => (int)ca == 1 || (int)ca == 4 || (foreignHelper && (int)ca == 2)
                    ? AsyncProfilerRuntimeSupport.Common : support,
                support);
            new AsyncProfilerCallStackTransformPipeline().ApplyInPlace(context);
            Assert.Equal(foreignHelper ? new[] { 1, 2, 3, 4 } : new[] { 1, 4 },
                frames.Select(frame => (int)frame.CodeAddress));
            Assert.Equal(foreignHelper ? AsyncProfilerStitchedFramePresentation.Native :
                AsyncProfilerStitchedFramePresentation.LogicalStateMachineMethod, frames[0].Presentation);
        }

        [Fact]
        public void ExtendedRegistrationAndEntrySpines_UseSupportLayouts()
        {
            var support = new AsyncProfilerExtendedRuntimeLayoutSupport();
            var registration = new List<AsyncProfilerStitchedFrame>
            {
                Sync(1, RuntimeFrameKind.V1AwaiterRegistration),
                Sync(2, AsyncProfilerStitchSyncFrameKind.V1MethodHelper),
                Sync(3, AsyncProfilerStitchSyncFrameKind.V1StateMachineBody),
            };
            Assert.Equal(2, Context(registration, support).MatchRuntimeSequence(
                AsyncProfilerRuntimeSequenceKind.V1AwaitRegistration, 0, 3).Length);
            Assert.Equal(1, Context(registration, AsyncProfilerRuntimeSupport.Common).MatchRuntimeSequence(
                AsyncProfilerRuntimeSequenceKind.V1AwaitRegistration, 0, 3).Length);

            var segment = Segment();
            var entry = new List<AsyncProfilerStitchedFrame>
            {
                Sync(1, RuntimeFrameKind.ProfilerResume),
                Sync(2, AsyncProfilerStitchSyncFrameKind.V1MethodHelper),
                AsyncProfilerStitchedFrame.CreateAsyncCurrent((CodeAddressIndex)3, (MethodIndex)3, segment, 0),
            };
            Assert.True(Context(entry, support).MatchRuntimeSequence(
                AsyncProfilerRuntimeSequenceKind.ProfilerEntry, 0, 2).IsMatch);
            Assert.False(Context(entry, AsyncProfilerRuntimeSupport.Common).MatchRuntimeSequence(
                AsyncProfilerRuntimeSequenceKind.ProfilerEntry, 0, 2).IsMatch);
        }

        [Fact]
        public void WholeBridgeAndAnchor_CanChangeWithoutChangingTheCleanupPass()
        {
            var support = new AsyncProfilerExtendedRuntimeLayoutSupport();
            var frames = new List<AsyncProfilerStitchedFrame>
            {
                Sync(1, RuntimeFrameKind.TaskContinuations),
                Sync(2, RuntimeFrameKind.TaskResult),
                Sync(3, AsyncProfilerStitchSyncFrameKind.V1DispatchSetup),
            };
            Assert.True(support.IsRuntimeResumptionAnchor(frames[2]));
            Assert.True(Context(frames, support).MatchRuntimeSequence(
                AsyncProfilerRuntimeSequenceKind.RuntimeResumptionBridge, 0, 3).IsMatch);
            Assert.True(Context(frames, AsyncProfilerRuntimeSupport.Common).MatchRuntimeSequence(
                AsyncProfilerRuntimeSequenceKind.RuntimeResumptionBridge, 0, 3).IsRejected);
            Assert.False(support.IsOptionalInfrastructureFrame(frames[2], AsyncProfilerCallStackStitchBoundaryKind.None));
        }

        [Fact]
        public void RejectedStartupAndRegistration_ProtectHelpersFromIndividualCleanup()
        {
            var frames = new List<AsyncProfilerStitchedFrame>
            {
                Sync(1, AsyncProfilerStitchSyncFrameKind.V1StateMachineBody),
                Sync(2, RuntimeFrameKind.V1Startup),
                Sync(3, RuntimeFrameKind.V1AwaiterRegistration),
                Sync(4, AsyncProfilerStitchSyncFrameKind.V1MethodHelper),
            };
            var support = new RejectingLayoutSupport();
            new AsyncProfilerCallStackTransformPipeline { EnableInfrastructureCleanup = true }
                .ApplyInPlace(Context(frames, support));
            Assert.Equal(new[] { 1, 2, 3, 4 }, frames.Select(frame => (int)frame.CodeAddress));
        }

        [Fact]
        public void RejectedRegistration_PreventsMarkerMergingIntoItsProtectedSpan()
        {
            var frames = new List<AsyncProfilerStitchedFrame>
            {
                Sync(1, RuntimeFrameKind.V1BuilderAwaitRegistration),
                Sync(2, RuntimeFrameKind.V1AwaiterRegistration)
                    .WithPresentation(AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames),
                Sync(3, AsyncProfilerStitchSyncFrameKind.V1MethodHelper),
                Sync(4, RuntimeFrameKind.V1BuilderAwaitRegistration),
                Sync(5, RuntimeFrameKind.V1BuilderAwaitRegistration),
            };
            AsyncProfilerStitchedFrame[] protectedSpan = frames.Skip(1).Take(2).ToArray();
            var context = Context(frames, new RejectingLayoutSupport());
            var pipeline = new AsyncProfilerCallStackTransformPipeline
            {
                EnableNormalization = false,
                EnableInfrastructureCleanup = true,
            };
            pipeline.ApplyInPlace(context);

            Assert.Equal(new[] { 1, 2, 3, 4 }, frames.Select(frame => (int)frame.CodeAddress));
            Assert.Equal(protectedSpan, frames.Skip(1).Take(2));
            Assert.Equal(AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames, frames[0].Presentation);
            Assert.Equal(AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames, frames[3].Presentation);
            Assert.Equal(3, context.Diagnostics.AsyncInfrastructureFramesCollapsed);
            pipeline.ApplyInPlace(context);
            Assert.Equal(protectedSpan, frames.Skip(1).Take(2));
            Assert.Equal(3, context.Diagnostics.AsyncInfrastructureFramesCollapsed);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void OptionalCleanup_MergesOnlyMatchingRuntimeSupport(bool sameSupport)
        {
            var first = new AsyncProfilerExtendedRuntimeLayoutSupport();
            AsyncProfilerRuntimeSupport second = sameSupport ? first : AsyncProfilerRuntimeSupport.Common;
            var frames = new List<AsyncProfilerStitchedFrame>
            {
                Sync(1), Sync(2),
            };
            var context = new AsyncProfilerCallStackTransformState(null, new List<AsyncProfilerCallStack>(), 1,
                _ => new AsyncProfilerCallStackStitchBoundaryInfo(
                    AsyncProfilerCallStackStitchBoundaryKind.V1DispatcherInfrastructure, -1),
                new AsyncProfilerCallStackStitchingState(), new AsyncProfilerCallStackDiagnostics(), frames,
                ca => (int)ca == 1 ? first : second);
            new AsyncProfilerCallStackTransformPipeline
            {
                EnableNormalization = false,
                EnableInfrastructureCleanup = true,
            }.ApplyInPlace(context);

            Assert.Equal(sameSupport ? new[] { 1 } : new[] { 1, 2 }, frames.Select(frame => (int)frame.CodeAddress));
            Assert.All(frames, frame =>
                Assert.Equal(AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames, frame.Presentation));
            Assert.Equal(2, context.Diagnostics.AsyncInfrastructureFramesCollapsed);
        }

        [Fact]
        public void OptionalCleanup_DoesNotResolveRuntimeLayoutsForLogicalFrames()
        {
            var frames = new List<AsyncProfilerStitchedFrame>
            {
                AsyncProfilerStitchedFrame.CreateAsyncCurrent((CodeAddressIndex)1, (MethodIndex)1, Segment(), 0),
            };
            var context = new AsyncProfilerCallStackTransformState(null, new List<AsyncProfilerCallStack>(), 1,
                _ => AsyncProfilerCallStackStitchBoundaryInfo.None, new AsyncProfilerCallStackStitchingState(), new AsyncProfilerCallStackDiagnostics(), frames,
                _ => throw new InvalidOperationException("Logical frames cannot start synchronous cleanup layouts."),
                AsyncProfilerRuntimeSupport.Common);

            new AsyncProfilerCallStackTransformPipeline
            {
                EnableNormalization = false,
                EnableInfrastructureCleanup = true,
            }.ApplyInPlace(context);

            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCurrent, Assert.Single(frames).Origin);
        }

        [Fact]
        public void InitialSupports_ShareCachedCommonStages()
        {
            foreach (AsyncProfilerRuntimeTransformKind kind in Enum.GetValues(typeof(AsyncProfilerRuntimeTransformKind)))
            {
                Assert.Same(AsyncProfilerRuntimeSupport.Common.GetTransform(kind),
                    AsyncProfilerRuntimeSupport.CoreClr11.GetTransform(kind));
            }
        }

        [Fact]
        public void OneStageReplacement_InheritsOtherCommonStagesRatherThanAppendingToTheDefault()
        {
            int calls = 0;
            var support = new SingleStageSupport(context => calls++);
            var frames = new List<AsyncProfilerStitchedFrame>
            {
                Sync(1), Sync(2, AsyncProfilerStitchSyncFrameKind.V1StateMachineBody),
                Sync(3, RuntimeFrameKind.V1Startup), Sync(4),
            };
            var context = new AsyncProfilerCallStackTransformState(null, new List<AsyncProfilerCallStack>(), 1,
                ca => (int)ca == 1 ? new AsyncProfilerCallStackStitchBoundaryInfo(AsyncProfilerCallStackStitchBoundaryKind.V2Dispatcher, 0) :
                    AsyncProfilerCallStackStitchBoundaryInfo.None,
                new AsyncProfilerCallStackStitchingState { UsesV2SynchronousLayout = true },
                new AsyncProfilerCallStackDiagnostics(), frames, _ => support);
            new AsyncProfilerCallStackTransformPipeline().ApplyInPlace(context);
            Assert.Equal(1, calls);
            Assert.Equal(new[] { 2, 3, 4 }, frames.Select(frame => (int)frame.CodeAddress));
            Assert.Equal(AsyncProfilerStitchedFramePresentation.Native, frames[0].Presentation);
            Assert.Same(AsyncProfilerRuntimeSupport.Common.GetTransform(AsyncProfilerRuntimeTransformKind.SampledV2Wrapper),
                support.GetTransform(AsyncProfilerRuntimeTransformKind.SampledV2Wrapper));
        }

        [Fact]
        public void ReplacementExceptions_AreNotSwallowed()
        {
            var error = new InvalidOperationException("Replacement failed");
            var support = new SingleStageSupport(context => throw error);
            Assert.Same(error, Assert.Throws<InvalidOperationException>(() =>
                new AsyncProfilerCallStackTransformPipeline().ApplyInPlace(
                    Context(new List<AsyncProfilerStitchedFrame> { Sync(1) }, support))));
        }

        #region private

        private static AsyncProfilerCallStackTransformState Context(
            List<AsyncProfilerStitchedFrame> frames, AsyncProfilerRuntimeSupport support) =>
            new AsyncProfilerCallStackTransformState(null, new List<AsyncProfilerCallStack>(), 1,
                _ => AsyncProfilerCallStackStitchBoundaryInfo.None, new AsyncProfilerCallStackStitchingState(), new AsyncProfilerCallStackDiagnostics(), frames, _ => support);

        private static AsyncProfilerStitchedFrame Sync(int ca, FrameClassification kind = default) =>
            AsyncProfilerStitchedFrame.CreateSync(new AsyncProfilerStitchSyncFrame((CodeAddressIndex)ca, (MethodIndex)ca, kind), null);

        private static AsyncProfilerCallStack Segment() =>
            new AsyncProfilerCallStack(0, (AsyncProfilerCallStackFramesIndex)0,
                new AsyncProfilerCallStackFrames(AsyncCallstackKind.StateMachineAsync, new ulong[] { 3 }, new[] { 0 }),
                0, 32, 0, long.MaxValue, Array.Empty<AsyncProfilerCallStack.CompletionDelta>(),
                Array.Empty<AsyncProfilerCallStack.CompletionDelta>(), Array.Empty<long>());

        private sealed class StageSupport : AsyncProfilerRuntimeSupport
        {
            public StageSupport(string name, List<string> calls) : base(name)
            {
                m_stages = new AsyncProfilerCallStackTransform[4];
                for (int i = 0; i < m_stages.Length; i++)
                {
                    var kind = (AsyncProfilerRuntimeTransformKind)i;
                    m_stages[i] = context =>
                    {
                        calls.Add(name + ":" + kind);
                        context.Frames.Clear();
                    };
                }
            }

            public override AsyncProfilerCallStackTransform GetTransform(AsyncProfilerRuntimeTransformKind kind) => m_stages[(int)kind];

            #region private

            private readonly AsyncProfilerCallStackTransform[] m_stages;

            #endregion
        }

        private sealed class NullStageSupport : AsyncProfilerRuntimeSupport
        {
            public NullStageSupport() : base("Invalid test profile")
            {
            }

            public override AsyncProfilerCallStackTransform GetTransform(AsyncProfilerRuntimeTransformKind kind) => null;
        }

        private sealed class SingleStageSupport : AsyncProfilerRuntimeSupport
        {
            public SingleStageSupport(AsyncProfilerCallStackTransform transform) : base("Single-stage test profile")
            {
                m_transform = transform;
            }

            public override AsyncProfilerCallStackTransform GetTransform(AsyncProfilerRuntimeTransformKind kind) =>
                kind == AsyncProfilerRuntimeTransformKind.V1Presentation ? m_transform : base.GetTransform(kind);

            #region private

            private readonly AsyncProfilerCallStackTransform m_transform;

            #endregion
        }

        private sealed class RejectingLayoutSupport : AsyncProfilerRuntimeSupport
        {
            public RejectingLayoutSupport() : base("Rejected test layouts")
            {
            }

            protected override AsyncProfilerRuntimeSequenceMatch MatchV1Startup(
                AsyncProfilerCallStackTransformState context, int start, int end) =>
                context.Frames[start].SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1StateMachineBody
                    ? AsyncProfilerRuntimeSequenceMatch.Reject(Math.Min(2, end - start)) : default;

            protected override AsyncProfilerRuntimeSequenceMatch MatchV1AwaitRegistration(
                AsyncProfilerCallStackTransformState context, int start, int end) =>
                context.Frames[start].SyncClassification.RuntimeKind == RuntimeFrameKind.V1AwaiterRegistration
                    ? AsyncProfilerRuntimeSequenceMatch.Reject(Math.Min(2, end - start)) : default;
        }

        #endregion
    }

    internal sealed class AsyncProfilerRejectRuntimeBridgeSupport : AsyncProfilerRuntimeSupport
    {
        public AsyncProfilerRejectRuntimeBridgeSupport() : base("Test-only rejected bridge layout")
        {
        }

        protected override AsyncProfilerRuntimeSequenceMatch MatchRuntimeResumptionBridge(
            AsyncProfilerCallStackTransformState context, int start, int end) =>
            end > start ? AsyncProfilerRuntimeSequenceMatch.Reject(end - start) : default;
    }

    // Hypothetical implementation layouts, never selected by production version detection.
    internal sealed class AsyncProfilerExtendedRuntimeLayoutSupport : AsyncProfilerRuntimeSupport
    {
        public AsyncProfilerExtendedRuntimeLayoutSupport() : base("Test-only extended runtime layouts")
        {
        }

        public const string HelperName = "System.Runtime.CompilerServices.AsyncProfiler.LayoutHelper";
        public const string AnchorName = "System.Threading.ThreadPoolWorkQueue.ChangedDispatch";

        public override FrameClassification ClassifySynchronousFrame(string frameName, bool isHostModule)
        {
            if (!isHostModule)
            {
                return base.ClassifySynchronousFrame(frameName, false);
            }
            if (frameName == HelperName)
            {
                return AsyncProfilerStitchSyncFrameKind.V1MethodHelper;
            }
            if (frameName == AnchorName)
            {
                return AsyncProfilerStitchSyncFrameKind.V1DispatchSetup;
            }
            return base.ClassifySynchronousFrame(frameName, true);
        }

        public override bool IsRuntimeResumptionAnchor(AsyncProfilerStitchedFrame frame) =>
            frame.Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1DispatchSetup;

        public override bool IsOptionalInfrastructureFrame(AsyncProfilerStitchedFrame frame, AsyncProfilerCallStackStitchBoundaryKind boundary) =>
            !IsRuntimeResumptionAnchor(frame) && base.IsOptionalInfrastructureFrame(frame, boundary);

        protected override AsyncProfilerRuntimeSequenceMatch MatchV1Startup(
            AsyncProfilerCallStackTransformState context, int start, int end)
        {
            AsyncProfilerRuntimeSequenceMatch common = base.MatchV1Startup(context, start, end);
            if (!common.IsMatch || end - start < 3 ||
                context.Frames[start + 1].SyncFrameKind != AsyncProfilerStitchSyncFrameKind.V1MethodHelper ||
                context.Frames[start + 2].SyncClassification.RuntimeKind != RuntimeFrameKind.V1Startup)
            {
                return common;
            }
            int next = start + 3;
            while (next < end && context.Frames[next].SyncClassification.RuntimeKind == RuntimeFrameKind.V1Startup)
            {
                next++;
            }
            return AsyncProfilerRuntimeSequenceMatch.Match(next - start);
        }

        protected override AsyncProfilerRuntimeSequenceMatch MatchV1AwaitRegistration(
            AsyncProfilerCallStackTransformState context, int start, int end)
        {
            AsyncProfilerRuntimeSequenceMatch common = base.MatchV1AwaitRegistration(context, start, end);
            return common.IsMatch && start + common.Length < end &&
                   context.Frames[start + common.Length].SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1MethodHelper
                ? AsyncProfilerRuntimeSequenceMatch.Match(common.Length + 1) : common;
        }

        protected override AsyncProfilerRuntimeSequenceMatch MatchProfilerEntry(
            AsyncProfilerCallStackTransformState context, int start, int end)
        {
            if (end - start >= 2 && end < context.Frames.Count &&
                context.Frames[start].SyncClassification.RuntimeKind == RuntimeFrameKind.ProfilerResume &&
                context.Frames[start + 1].SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1MethodHelper)
            {
                bool dispatcher = context.Frames[end].Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget &&
                    !context.Frames[end].IsStateMachineBoxCarrier;
                return end - start == (dispatcher ? 3 : 2) &&
                       (!dispatcher || context.ClassifyBoundary(context.Frames[start + 2].CodeAddress).Kind ==
                            AsyncProfilerCallStackStitchBoundaryKind.V1Dispatcher)
                    ? AsyncProfilerRuntimeSequenceMatch.Match(end - start) : default;
            }
            return base.MatchProfilerEntry(context, start, end);
        }
    }
}
