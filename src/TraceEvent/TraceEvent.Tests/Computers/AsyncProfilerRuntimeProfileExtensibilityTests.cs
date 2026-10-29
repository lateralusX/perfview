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
    public class AsyncProfilerRuntimeProfileExtensibilityTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void ReplacedStages_PreserveFlagsOrderAndPerSampleSelection(bool enableNormalization, bool enableInfrastructureCleanup)
        {
            var calls = new List<string>();
            var first = new StageProfile("First", calls);
            var second = new StageProfile("Second", calls);
            var pipeline = new AsyncProfilerCallStackTransformPipeline
            {
                EnableNormalization = enableNormalization,
                EnableInfrastructureCleanup = enableInfrastructureCleanup,
            };
            pipeline.Add(context => calls.Add("Custom"));

            foreach (StageProfile profile in new[] { first, second })
            {
                pipeline.ApplyInPlace(Context(new List<AsyncProfilerStitchedFrame> { Sync(1) }, profile));
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
        public void MixedProfiles_DoNotSelectAnArbitraryReplacement()
        {
            var calls = new List<string>();
            var first = new StageProfile("First", calls);
            var second = new StageProfile("Second", calls);
            var frames = new List<AsyncProfilerStitchedFrame> { Sync(1), Sync(2) };
            var context = new AsyncProfilerCallStackTransformState(null, new List<AsyncProfilerCallStack>(), 1,
                _ => AsyncProfilerCallStackStitchBoundaryInfo.None, new AsyncProfilerCallStackDiagnostics(), frames,
                ca => (int)ca == 1 ? first : second);
            Assert.Same(AsyncProfilerRuntimeRuleProfile.Common, context.RuntimeProfile);
            new AsyncProfilerCallStackTransformPipeline().ApplyInPlace(context);
            Assert.Empty(calls);
            Assert.Equal(2, frames.Count);
        }

        [Fact]
        public void ReplacementSelection_SurvivesRemovalOfAllRuntimeEvidence()
        {
            var calls = new List<string>();
            var profile = new StageProfile("Selected", calls);
            var frames = new List<AsyncProfilerStitchedFrame> { Sync(1) };
            new AsyncProfilerCallStackTransformPipeline { EnableInfrastructureCleanup = true }
                .ApplyInPlace(Context(frames, profile));
            Assert.Empty(frames);
            Assert.Equal(4, calls.Count);
        }

        [Fact]
        public void InvalidReplacement_FailsExplicitly()
        {
            var context = Context(new List<AsyncProfilerStitchedFrame> { Sync(1) }, new NullStageProfile());
            Assert.Throws<InvalidOperationException>(() => new AsyncProfilerCallStackTransformPipeline().ApplyInPlace(context));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void StartupLayout_UsesOwningRuntimeRatherThanApplicationModule(bool foreignHelper)
        {
            var profile = new AsyncProfilerExtendedRuntimeLayoutProfile();
            var frames = new List<AsyncProfilerStitchedFrame>
            {
                Sync(1, AsyncProfilerStitchSyncFrameKind.V1StateMachineMoveNext),
                Sync(2, AsyncProfilerStitchSyncFrameKind.V1MethodBuilderInfrastructure),
                Sync(3, AsyncProfilerStitchSyncFrameKind.V1MethodBuilderStart),
                Sync(4),
            };
            var context = new AsyncProfilerCallStackTransformState(null, new List<AsyncProfilerCallStack>(), 1,
                _ => AsyncProfilerCallStackStitchBoundaryInfo.None, new AsyncProfilerCallStackDiagnostics(), frames,
                ca => (int)ca == 1 || (int)ca == 4 || (foreignHelper && (int)ca == 2)
                    ? AsyncProfilerRuntimeRuleProfile.Common : profile,
                profile);
            new AsyncProfilerCallStackTransformPipeline().ApplyInPlace(context);
            Assert.Equal(foreignHelper ? new[] { 1, 2, 3, 4 } : new[] { 1, 4 },
                frames.Select(frame => (int)frame.CodeAddress));
            Assert.Equal(foreignHelper ? AsyncProfilerStitchedFramePresentation.Native :
                AsyncProfilerStitchedFramePresentation.LogicalStateMachineMethod, frames[0].Presentation);
        }

        [Fact]
        public void ExtendedRegistrationAndEntrySpines_UseProfileLayouts()
        {
            var profile = new AsyncProfilerExtendedRuntimeLayoutProfile();
            var registration = new List<AsyncProfilerStitchedFrame>
            {
                Sync(1, AsyncProfilerStitchSyncFrameKind.V1AwaiterRegistrationInfrastructure),
                Sync(2, AsyncProfilerStitchSyncFrameKind.V1MethodBuilderInfrastructure),
                Sync(3, AsyncProfilerStitchSyncFrameKind.V1StateMachineMoveNext),
            };
            Assert.Equal(2, Context(registration, profile).MatchRuntimeSequence(
                AsyncProfilerRuntimeSequenceKind.V1AwaitRegistration, 0, 3).Length);
            Assert.Equal(1, Context(registration, AsyncProfilerRuntimeRuleProfile.Common).MatchRuntimeSequence(
                AsyncProfilerRuntimeSequenceKind.V1AwaitRegistration, 0, 3).Length);

            var segment = Segment();
            var entry = new List<AsyncProfilerStitchedFrame>
            {
                Sync(1, AsyncProfilerStitchSyncFrameKind.ProfilerContextResume),
                Sync(2, AsyncProfilerStitchSyncFrameKind.V1MethodBuilderInfrastructure),
                AsyncProfilerStitchedFrame.CreateAsyncCurrent((CodeAddressIndex)3, (MethodIndex)3, segment, 0),
            };
            Assert.True(Context(entry, profile).MatchRuntimeSequence(
                AsyncProfilerRuntimeSequenceKind.ProfilerEntry, 0, 2).IsMatch);
            Assert.False(Context(entry, AsyncProfilerRuntimeRuleProfile.Common).MatchRuntimeSequence(
                AsyncProfilerRuntimeSequenceKind.ProfilerEntry, 0, 2).IsMatch);
        }

        [Fact]
        public void WholeBridgeAndAnchor_CanChangeWithoutChangingTheCleanupPass()
        {
            var profile = new AsyncProfilerExtendedRuntimeLayoutProfile();
            var frames = new List<AsyncProfilerStitchedFrame>
            {
                Sync(1, AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations),
                Sync(2, AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult),
                Sync(3, AsyncProfilerStitchSyncFrameKind.V1DispatcherCreationInfrastructure),
            };
            Assert.True(profile.IsRuntimeResumptionAnchor(frames[2]));
            Assert.True(Context(frames, profile).MatchRuntimeSequence(
                AsyncProfilerRuntimeSequenceKind.RuntimeResumptionBridge, 0, 3).IsMatch);
            Assert.True(Context(frames, AsyncProfilerRuntimeRuleProfile.Common).MatchRuntimeSequence(
                AsyncProfilerRuntimeSequenceKind.RuntimeResumptionBridge, 0, 3).IsRejected);
            Assert.False(profile.IsOptionalInfrastructureFrame(frames[2], AsyncProfilerCallStackStitchBoundaryKind.None));
        }

        [Fact]
        public void RejectedStartupAndRegistration_ProtectHelpersFromIndividualCleanup()
        {
            var frames = new List<AsyncProfilerStitchedFrame>
            {
                Sync(1, AsyncProfilerStitchSyncFrameKind.V1StateMachineMoveNext),
                Sync(2, AsyncProfilerStitchSyncFrameKind.V1MethodBuilderStart),
                Sync(3, AsyncProfilerStitchSyncFrameKind.V1AwaiterRegistrationInfrastructure),
                Sync(4, AsyncProfilerStitchSyncFrameKind.V1MethodBuilderInfrastructure),
            };
            var profile = new RejectingLayoutProfile();
            new AsyncProfilerCallStackTransformPipeline { EnableInfrastructureCleanup = true }
                .ApplyInPlace(Context(frames, profile));
            Assert.Equal(new[] { 1, 2, 3, 4 }, frames.Select(frame => (int)frame.CodeAddress));
        }

        [Fact]
        public void OptionalCleanup_DoesNotResolveRuntimeLayoutsForLogicalFrames()
        {
            var frames = new List<AsyncProfilerStitchedFrame>
            {
                AsyncProfilerStitchedFrame.CreateAsyncCurrent((CodeAddressIndex)1, (MethodIndex)1, Segment(), 0),
            };
            var context = new AsyncProfilerCallStackTransformState(null, new List<AsyncProfilerCallStack>(), 1,
                _ => AsyncProfilerCallStackStitchBoundaryInfo.None, new AsyncProfilerCallStackDiagnostics(), frames,
                _ => throw new InvalidOperationException("Logical frames cannot start synchronous cleanup layouts."),
                AsyncProfilerRuntimeRuleProfile.Common);

            new AsyncProfilerCallStackTransformPipeline
            {
                EnableNormalization = false,
                EnableInfrastructureCleanup = true,
            }.ApplyInPlace(context);

            Assert.Equal(AsyncProfilerStitchedFrameOrigin.AsyncCurrent, Assert.Single(frames).Origin);
        }

        [Fact]
        public void InitialProfiles_ShareCachedCommonStages()
        {
            foreach (AsyncProfilerRuntimeTransformKind kind in Enum.GetValues(typeof(AsyncProfilerRuntimeTransformKind)))
            {
                Assert.Same(AsyncProfilerRuntimeRuleProfile.Common.GetTransform(kind),
                    AsyncProfilerRuntimeRuleProfile.CoreClr11.GetTransform(kind));
            }
        }

        [Fact]
        public void OneStageReplacement_InheritsOtherCommonStagesRatherThanAppendingToTheDefault()
        {
            int calls = 0;
            var profile = new SingleStageProfile(context => calls++);
            var frames = new List<AsyncProfilerStitchedFrame>
            {
                Sync(1), Sync(2, AsyncProfilerStitchSyncFrameKind.V1StateMachineMoveNext),
                Sync(3, AsyncProfilerStitchSyncFrameKind.V1MethodBuilderStart), Sync(4),
            };
            var context = new AsyncProfilerCallStackTransformState(null, new List<AsyncProfilerCallStack>(), 1,
                ca => (int)ca == 1 ? new AsyncProfilerCallStackStitchBoundaryInfo(AsyncProfilerCallStackStitchBoundaryKind.V2ContinuationWrapper, 0) :
                    AsyncProfilerCallStackStitchBoundaryInfo.None,
                new AsyncProfilerCallStackDiagnostics { V2SyncLayoutUsed = 1 }, frames, _ => profile);
            new AsyncProfilerCallStackTransformPipeline().ApplyInPlace(context);
            Assert.Equal(1, calls);
            Assert.Equal(new[] { 2, 3, 4 }, frames.Select(frame => (int)frame.CodeAddress));
            Assert.Equal(AsyncProfilerStitchedFramePresentation.Native, frames[0].Presentation);
            Assert.Same(AsyncProfilerRuntimeRuleProfile.Common.GetTransform(AsyncProfilerRuntimeTransformKind.SampledV2Wrapper),
                profile.GetTransform(AsyncProfilerRuntimeTransformKind.SampledV2Wrapper));
        }

        [Fact]
        public void ReplacementExceptions_AreNotSwallowed()
        {
            var error = new InvalidOperationException("Replacement failed");
            var profile = new SingleStageProfile(context => throw error);
            Assert.Same(error, Assert.Throws<InvalidOperationException>(() =>
                new AsyncProfilerCallStackTransformPipeline().ApplyInPlace(
                    Context(new List<AsyncProfilerStitchedFrame> { Sync(1) }, profile))));
        }

        private static AsyncProfilerCallStackTransformState Context(List<AsyncProfilerStitchedFrame> frames, AsyncProfilerRuntimeRuleProfile profile) =>
            new AsyncProfilerCallStackTransformState(null, new List<AsyncProfilerCallStack>(), 1,
                _ => AsyncProfilerCallStackStitchBoundaryInfo.None, new AsyncProfilerCallStackDiagnostics(), frames, _ => profile);

        private static AsyncProfilerStitchedFrame Sync(int ca, AsyncProfilerStitchSyncFrameKind kind = AsyncProfilerStitchSyncFrameKind.None) =>
            AsyncProfilerStitchedFrame.CreateSync(new AsyncProfilerStitchSyncFrame((CodeAddressIndex)ca, (MethodIndex)ca, kind), null);

        private static AsyncProfilerCallStack Segment() =>
            new AsyncProfilerCallStack(0, (AsyncProfilerCallStackFramesIndex)0,
                new AsyncProfilerCallStackFrames(AsyncCallstackKind.StateMachineAsync, new ulong[] { 3 }, new[] { 0 }),
                0, 32, 0, long.MaxValue, Array.Empty<AsyncProfilerCallStack.CompletionDelta>(),
                Array.Empty<AsyncProfilerCallStack.CompletionDelta>(), Array.Empty<long>());

        private sealed class StageProfile : AsyncProfilerRuntimeRuleProfile
        {
            public StageProfile(string name, List<string> calls) : base(name)
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

            private readonly AsyncProfilerCallStackTransform[] m_stages;
        }

        private sealed class NullStageProfile : AsyncProfilerRuntimeRuleProfile
        {
            public NullStageProfile() : base("Invalid test profile") { }
            public override AsyncProfilerCallStackTransform GetTransform(AsyncProfilerRuntimeTransformKind kind) => null;
        }

        private sealed class SingleStageProfile : AsyncProfilerRuntimeRuleProfile
        {
            public SingleStageProfile(AsyncProfilerCallStackTransform transform) : base("Single-stage test profile")
            {
                m_transform = transform;
            }

            public override AsyncProfilerCallStackTransform GetTransform(AsyncProfilerRuntimeTransformKind kind) =>
                kind == AsyncProfilerRuntimeTransformKind.V1Presentation ? m_transform : base.GetTransform(kind);

            private readonly AsyncProfilerCallStackTransform m_transform;
        }

        private sealed class RejectingLayoutProfile : AsyncProfilerRuntimeRuleProfile
        {
            public RejectingLayoutProfile() : base("Rejected test layouts") { }

            protected override AsyncProfilerRuntimeSequenceMatch MatchV1Startup(
                AsyncProfilerCallStackTransformState context, int start, int end) =>
                context.Frames[start].SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1StateMachineMoveNext
                    ? AsyncProfilerRuntimeSequenceMatch.Reject(Math.Min(2, end - start)) : default;

            protected override AsyncProfilerRuntimeSequenceMatch MatchV1AwaitRegistration(
                AsyncProfilerCallStackTransformState context, int start, int end) =>
                context.Frames[start].SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1AwaiterRegistrationInfrastructure
                    ? AsyncProfilerRuntimeSequenceMatch.Reject(Math.Min(2, end - start)) : default;
        }
    }

    internal sealed class AsyncProfilerRejectRuntimeBridgeProfile : AsyncProfilerRuntimeRuleProfile
    {
        public AsyncProfilerRejectRuntimeBridgeProfile() : base("Test-only rejected bridge layout") { }

        protected override AsyncProfilerRuntimeSequenceMatch MatchRuntimeResumptionBridge(
            AsyncProfilerCallStackTransformState context, int start, int end) =>
            end > start ? AsyncProfilerRuntimeSequenceMatch.Reject(end - start) : default;
    }

    // Hypothetical implementation layouts, never selected by production version detection.
    internal sealed class AsyncProfilerExtendedRuntimeLayoutProfile : AsyncProfilerRuntimeRuleProfile
    {
        public AsyncProfilerExtendedRuntimeLayoutProfile() : base("Test-only extended runtime layouts") { }

        public const string HelperName = "System.Runtime.CompilerServices.AsyncProfiler.LayoutHelper";
        public const string AnchorName = "System.Threading.ThreadPoolWorkQueue.ChangedDispatch";

        public override AsyncProfilerStitchSyncFrameKind ClassifySynchronousFrame(string frameName, bool isHostModule) =>
            !isHostModule ? base.ClassifySynchronousFrame(frameName, false) :
            frameName == HelperName ? AsyncProfilerStitchSyncFrameKind.V1MethodBuilderInfrastructure :
            frameName == AnchorName ? AsyncProfilerStitchSyncFrameKind.V1DispatcherCreationInfrastructure :
            base.ClassifySynchronousFrame(frameName, true);

        public override bool IsRuntimeResumptionAnchor(AsyncProfilerStitchedFrame frame) =>
            frame.Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
            frame.SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1DispatcherCreationInfrastructure;

        public override bool IsOptionalInfrastructureFrame(AsyncProfilerStitchedFrame frame, AsyncProfilerCallStackStitchBoundaryKind boundary) =>
            !IsRuntimeResumptionAnchor(frame) && base.IsOptionalInfrastructureFrame(frame, boundary);

        protected override AsyncProfilerRuntimeSequenceMatch MatchV1Startup(
            AsyncProfilerCallStackTransformState context, int start, int end)
        {
            AsyncProfilerRuntimeSequenceMatch common = base.MatchV1Startup(context, start, end);
            if (!common.IsMatch || end - start < 3 ||
                context.Frames[start + 1].SyncFrameKind != AsyncProfilerStitchSyncFrameKind.V1MethodBuilderInfrastructure ||
                context.Frames[start + 2].SyncFrameKind != AsyncProfilerStitchSyncFrameKind.V1MethodBuilderStart)
            {
                return common;
            }
            int next = start + 3;
            while (next < end && context.Frames[next].SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1MethodBuilderStart)
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
                   context.Frames[start + common.Length].SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1MethodBuilderInfrastructure
                ? AsyncProfilerRuntimeSequenceMatch.Match(common.Length + 1) : common;
        }

        protected override AsyncProfilerRuntimeSequenceMatch MatchProfilerEntry(
            AsyncProfilerCallStackTransformState context, int start, int end)
        {
            if (end - start >= 2 && end < context.Frames.Count &&
                context.Frames[start].SyncFrameKind == AsyncProfilerStitchSyncFrameKind.ProfilerContextResume &&
                context.Frames[start + 1].SyncFrameKind == AsyncProfilerStitchSyncFrameKind.V1MethodBuilderInfrastructure)
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
