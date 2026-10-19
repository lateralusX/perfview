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
    public class AsyncRuntimeProfileExtensibilityTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void ReplacedStages_PreserveFlagsOrderAndPerSampleSelection(bool conservative, bool cleanup)
        {
            var calls = new List<string>();
            var first = new StageProfile("First", calls);
            var second = new StageProfile("Second", calls);
            var pipeline = new AsyncStackTransformPipeline
            {
                EnableConservativeTransforms = conservative,
                EnableAsyncInfrastructureCleanup = cleanup,
            };
            pipeline.Add(context => calls.Add("Custom"));

            foreach (StageProfile profile in new[] { first, second })
            {
                pipeline.ApplyInPlace(Context(new List<StitchedFrame> { Sync(1) }, profile));
            }

            var expected = new List<string>();
            foreach (string name in new[] { "First", "Second" })
            {
                if (conservative)
                {
                    expected.Add(name + ":SampledV2Wrapper");
                    expected.Add(name + ":ReentrantHandoff");
                    expected.Add(name + ":V1Presentation");
                }
                if (cleanup)
                {
                    expected.Add(name + ":InfrastructureCleanup");
                }
                expected.Add("Custom");
            }
            Assert.Equal(expected, calls);

            calls.Clear();
            pipeline.EnableConservativeTransforms = !conservative;
            pipeline.EnableAsyncInfrastructureCleanup = !cleanup;
            pipeline.ApplyInPlace(Context(new List<StitchedFrame> { Sync(1) }, first));
            Assert.Equal((conservative ? 0 : 3) + (cleanup ? 0 : 1) + 1, calls.Count);
            Assert.Equal("Custom", calls.Last());
        }

        [Fact]
        public void MixedProfiles_DoNotSelectAnArbitraryReplacement()
        {
            var calls = new List<string>();
            var first = new StageProfile("First", calls);
            var second = new StageProfile("Second", calls);
            var frames = new List<StitchedFrame> { Sync(1), Sync(2) };
            var context = new AsyncStackTransformContext(null, new List<AsyncCallStack>(), 1,
                _ => AsyncStitchBoundaryInfo.None, new StitchDiagnostics(), frames,
                ca => (int)ca == 1 ? first : second);
            Assert.Same(AsyncRuntimeRuleProfile.Common, context.RuntimeProfile);
            new AsyncStackTransformPipeline().ApplyInPlace(context);
            Assert.Empty(calls);
            Assert.Equal(2, frames.Count);
        }

        [Fact]
        public void ReplacementSelection_SurvivesRemovalOfAllRuntimeEvidence()
        {
            var calls = new List<string>();
            var profile = new StageProfile("Selected", calls);
            var frames = new List<StitchedFrame> { Sync(1) };
            new AsyncStackTransformPipeline { EnableAsyncInfrastructureCleanup = true }
                .ApplyInPlace(Context(frames, profile));
            Assert.Empty(frames);
            Assert.Equal(4, calls.Count);
        }

        [Fact]
        public void InvalidReplacement_FailsExplicitly()
        {
            var context = Context(new List<StitchedFrame> { Sync(1) }, new NullStageProfile());
            Assert.Throws<InvalidOperationException>(() => new AsyncStackTransformPipeline().ApplyInPlace(context));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void StartupLayout_UsesOwningRuntimeRatherThanApplicationModule(bool foreignHelper)
        {
            var profile = new ExtendedRuntimeLayoutProfile();
            var frames = new List<StitchedFrame>
            {
                Sync(1, StitchSyncFrameKind.V1StateMachineMoveNext),
                Sync(2, StitchSyncFrameKind.V1MethodBuilderInfrastructure),
                Sync(3, StitchSyncFrameKind.V1MethodBuilderStart),
                Sync(4),
            };
            var context = new AsyncStackTransformContext(null, new List<AsyncCallStack>(), 1,
                _ => AsyncStitchBoundaryInfo.None, new StitchDiagnostics(), frames,
                ca => (int)ca == 1 || (int)ca == 4 || (foreignHelper && (int)ca == 2)
                    ? AsyncRuntimeRuleProfile.Common : profile,
                profile);
            new AsyncStackTransformPipeline().ApplyInPlace(context);
            Assert.Equal(foreignHelper ? new[] { 1, 2, 3, 4 } : new[] { 1, 4 },
                frames.Select(frame => (int)frame.CodeAddress));
            Assert.Equal(foreignHelper ? StitchedFramePresentation.Native :
                StitchedFramePresentation.LogicalStateMachineMethod, frames[0].Presentation);
        }

        [Fact]
        public void ExtendedRegistrationAndEntrySpines_UseProfileLayouts()
        {
            var profile = new ExtendedRuntimeLayoutProfile();
            var registration = new List<StitchedFrame>
            {
                Sync(1, StitchSyncFrameKind.V1AwaiterRegistrationInfrastructure),
                Sync(2, StitchSyncFrameKind.V1MethodBuilderInfrastructure),
                Sync(3, StitchSyncFrameKind.V1StateMachineMoveNext),
            };
            Assert.Equal(2, Context(registration, profile).MatchRuntimeSequence(
                AsyncRuntimeSequenceKind.V1AwaitRegistration, 0, 3).Length);
            Assert.Equal(1, Context(registration, AsyncRuntimeRuleProfile.Common).MatchRuntimeSequence(
                AsyncRuntimeSequenceKind.V1AwaitRegistration, 0, 3).Length);

            var segment = Segment();
            var entry = new List<StitchedFrame>
            {
                Sync(1, StitchSyncFrameKind.AsyncProfilerContextResume),
                Sync(2, StitchSyncFrameKind.V1MethodBuilderInfrastructure),
                StitchedFrame.CreateAsyncCurrent((CodeAddressIndex)3, (MethodIndex)3, segment, 0),
            };
            Assert.True(Context(entry, profile).MatchRuntimeSequence(
                AsyncRuntimeSequenceKind.AsyncProfilerEntry, 0, 2).IsMatch);
            Assert.False(Context(entry, AsyncRuntimeRuleProfile.Common).MatchRuntimeSequence(
                AsyncRuntimeSequenceKind.AsyncProfilerEntry, 0, 2).IsMatch);
        }

        [Fact]
        public void WholeBridgeAndAnchor_CanChangeWithoutChangingTheCleanupPass()
        {
            var profile = new ExtendedRuntimeLayoutProfile();
            var frames = new List<StitchedFrame>
            {
                Sync(1, StitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations),
                Sync(2, StitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult),
                Sync(3, StitchSyncFrameKind.V1DispatcherCreationInfrastructure),
            };
            Assert.True(profile.IsRuntimeResumptionAnchor(frames[2]));
            Assert.True(Context(frames, profile).MatchRuntimeSequence(
                AsyncRuntimeSequenceKind.RuntimeResumptionBridge, 0, 3).IsMatch);
            Assert.True(Context(frames, AsyncRuntimeRuleProfile.Common).MatchRuntimeSequence(
                AsyncRuntimeSequenceKind.RuntimeResumptionBridge, 0, 3).IsRejected);
            Assert.False(profile.IsOptionalInfrastructureFrame(frames[2], AsyncStitchBoundaryKind.None));
        }

        [Fact]
        public void RejectedStartupAndRegistration_ProtectHelpersFromIndividualCleanup()
        {
            var frames = new List<StitchedFrame>
            {
                Sync(1, StitchSyncFrameKind.V1StateMachineMoveNext),
                Sync(2, StitchSyncFrameKind.V1MethodBuilderStart),
                Sync(3, StitchSyncFrameKind.V1AwaiterRegistrationInfrastructure),
                Sync(4, StitchSyncFrameKind.V1MethodBuilderInfrastructure),
            };
            var profile = new RejectingLayoutProfile();
            new AsyncStackTransformPipeline { EnableAsyncInfrastructureCleanup = true }
                .ApplyInPlace(Context(frames, profile));
            Assert.Equal(new[] { 1, 2, 3, 4 }, frames.Select(frame => (int)frame.CodeAddress));
        }

        [Fact]
        public void OptionalCleanup_DoesNotResolveRuntimeLayoutsForLogicalFrames()
        {
            var frames = new List<StitchedFrame>
            {
                StitchedFrame.CreateAsyncCurrent((CodeAddressIndex)1, (MethodIndex)1, Segment(), 0),
            };
            var context = new AsyncStackTransformContext(null, new List<AsyncCallStack>(), 1,
                _ => AsyncStitchBoundaryInfo.None, new StitchDiagnostics(), frames,
                _ => throw new InvalidOperationException("Logical frames cannot start synchronous cleanup layouts."),
                AsyncRuntimeRuleProfile.Common);

            new AsyncStackTransformPipeline
            {
                EnableConservativeTransforms = false,
                EnableAsyncInfrastructureCleanup = true,
            }.ApplyInPlace(context);

            Assert.Equal(StitchedFrameOrigin.AsyncCurrent, Assert.Single(frames).Origin);
        }

        [Fact]
        public void InitialProfiles_ShareCachedCommonStages()
        {
            foreach (AsyncRuntimeTransformKind kind in Enum.GetValues(typeof(AsyncRuntimeTransformKind)))
            {
                Assert.Same(AsyncRuntimeRuleProfile.Common.GetTransform(kind),
                    AsyncRuntimeRuleProfile.CoreClr11.GetTransform(kind));
            }
        }

        [Fact]
        public void OneStageReplacement_InheritsOtherCommonStagesRatherThanAppendingToTheDefault()
        {
            int calls = 0;
            var profile = new SingleStageProfile(context => calls++);
            var frames = new List<StitchedFrame>
            {
                Sync(1), Sync(2, StitchSyncFrameKind.V1StateMachineMoveNext),
                Sync(3, StitchSyncFrameKind.V1MethodBuilderStart), Sync(4),
            };
            var context = new AsyncStackTransformContext(null, new List<AsyncCallStack>(), 1,
                ca => (int)ca == 1 ? new AsyncStitchBoundaryInfo(AsyncStitchBoundaryKind.V2ContinuationWrapper, 0) :
                    AsyncStitchBoundaryInfo.None,
                new StitchDiagnostics { V2SyncLayoutUsed = 1 }, frames, _ => profile);
            new AsyncStackTransformPipeline().ApplyInPlace(context);
            Assert.Equal(1, calls);
            Assert.Equal(new[] { 2, 3, 4 }, frames.Select(frame => (int)frame.CodeAddress));
            Assert.Equal(StitchedFramePresentation.Native, frames[0].Presentation);
            Assert.Same(AsyncRuntimeRuleProfile.Common.GetTransform(AsyncRuntimeTransformKind.SampledV2Wrapper),
                profile.GetTransform(AsyncRuntimeTransformKind.SampledV2Wrapper));
        }

        [Fact]
        public void ReplacementExceptions_AreNotSwallowed()
        {
            var error = new InvalidOperationException("Replacement failed");
            var profile = new SingleStageProfile(context => throw error);
            Assert.Same(error, Assert.Throws<InvalidOperationException>(() =>
                new AsyncStackTransformPipeline().ApplyInPlace(
                    Context(new List<StitchedFrame> { Sync(1) }, profile))));
        }

        private static AsyncStackTransformContext Context(List<StitchedFrame> frames, AsyncRuntimeRuleProfile profile) =>
            new AsyncStackTransformContext(null, new List<AsyncCallStack>(), 1,
                _ => AsyncStitchBoundaryInfo.None, new StitchDiagnostics(), frames, _ => profile);

        private static StitchedFrame Sync(int ca, StitchSyncFrameKind kind = StitchSyncFrameKind.None) =>
            StitchedFrame.CreateSync(new StitchSyncFrame((CodeAddressIndex)ca, (MethodIndex)ca, kind), null);

        private static AsyncCallStack Segment() =>
            new AsyncCallStack(0, (AsyncCallStackFramesIndex)0,
                new AsyncCallStackFrames(AsyncCallstackKind.StateMachineAsync, new ulong[] { 3 }, new[] { 0 }),
                0, 32, 0, long.MaxValue, Array.Empty<AsyncCallStack.CompletionDelta>(),
                Array.Empty<AsyncCallStack.CompletionDelta>(), Array.Empty<long>());

        private sealed class StageProfile : AsyncRuntimeRuleProfile
        {
            public StageProfile(string name, List<string> calls) : base(name)
            {
                m_stages = new AsyncStackTransform[4];
                for (int i = 0; i < m_stages.Length; i++)
                {
                    var kind = (AsyncRuntimeTransformKind)i;
                    m_stages[i] = context =>
                    {
                        calls.Add(name + ":" + kind);
                        context.Frames.Clear();
                    };
                }
            }

            public override AsyncStackTransform GetTransform(AsyncRuntimeTransformKind kind) => m_stages[(int)kind];

            private readonly AsyncStackTransform[] m_stages;
        }

        private sealed class NullStageProfile : AsyncRuntimeRuleProfile
        {
            public NullStageProfile() : base("Invalid test profile") { }
            public override AsyncStackTransform GetTransform(AsyncRuntimeTransformKind kind) => null;
        }

        private sealed class SingleStageProfile : AsyncRuntimeRuleProfile
        {
            public SingleStageProfile(AsyncStackTransform transform) : base("Single-stage test profile")
            {
                m_transform = transform;
            }

            public override AsyncStackTransform GetTransform(AsyncRuntimeTransformKind kind) =>
                kind == AsyncRuntimeTransformKind.V1Presentation ? m_transform : base.GetTransform(kind);

            private readonly AsyncStackTransform m_transform;
        }

        private sealed class RejectingLayoutProfile : AsyncRuntimeRuleProfile
        {
            public RejectingLayoutProfile() : base("Rejected test layouts") { }

            protected override AsyncRuntimeSequenceMatch MatchV1Startup(
                AsyncStackTransformContext context, int start, int end) =>
                context.Frames[start].SyncFrameKind == StitchSyncFrameKind.V1StateMachineMoveNext
                    ? AsyncRuntimeSequenceMatch.Reject(Math.Min(2, end - start)) : default;

            protected override AsyncRuntimeSequenceMatch MatchV1AwaitRegistration(
                AsyncStackTransformContext context, int start, int end) =>
                context.Frames[start].SyncFrameKind == StitchSyncFrameKind.V1AwaiterRegistrationInfrastructure
                    ? AsyncRuntimeSequenceMatch.Reject(Math.Min(2, end - start)) : default;
        }
    }

    internal sealed class RejectRuntimeBridgeProfile : AsyncRuntimeRuleProfile
    {
        public RejectRuntimeBridgeProfile() : base("Test-only rejected bridge layout") { }

        protected override AsyncRuntimeSequenceMatch MatchRuntimeResumptionBridge(
            AsyncStackTransformContext context, int start, int end) =>
            end > start ? AsyncRuntimeSequenceMatch.Reject(end - start) : default;
    }

    // Hypothetical implementation layouts, never selected by production version detection.
    internal sealed class ExtendedRuntimeLayoutProfile : AsyncRuntimeRuleProfile
    {
        public ExtendedRuntimeLayoutProfile() : base("Test-only extended runtime layouts") { }

        public const string HelperName = "System.Runtime.CompilerServices.AsyncProfiler.LayoutHelper";
        public const string AnchorName = "System.Threading.ThreadPoolWorkQueue.ChangedDispatch";

        public override StitchSyncFrameKind ClassifySynchronousFrame(string frameName, bool isHostModule) =>
            !isHostModule ? base.ClassifySynchronousFrame(frameName, false) :
            frameName == HelperName ? StitchSyncFrameKind.V1MethodBuilderInfrastructure :
            frameName == AnchorName ? StitchSyncFrameKind.V1DispatcherCreationInfrastructure :
            base.ClassifySynchronousFrame(frameName, true);

        public override bool IsRuntimeResumptionAnchor(StitchedFrame frame) =>
            frame.Origin == StitchedFrameOrigin.Sync &&
            frame.SyncFrameKind == StitchSyncFrameKind.V1DispatcherCreationInfrastructure;

        public override bool IsOptionalInfrastructureFrame(StitchedFrame frame, AsyncStitchBoundaryKind boundary) =>
            !IsRuntimeResumptionAnchor(frame) && base.IsOptionalInfrastructureFrame(frame, boundary);

        protected override AsyncRuntimeSequenceMatch MatchV1Startup(
            AsyncStackTransformContext context, int start, int end)
        {
            AsyncRuntimeSequenceMatch common = base.MatchV1Startup(context, start, end);
            if (!common.IsMatch || end - start < 3 ||
                context.Frames[start + 1].SyncFrameKind != StitchSyncFrameKind.V1MethodBuilderInfrastructure ||
                context.Frames[start + 2].SyncFrameKind != StitchSyncFrameKind.V1MethodBuilderStart)
            {
                return common;
            }
            int next = start + 3;
            while (next < end && context.Frames[next].SyncFrameKind == StitchSyncFrameKind.V1MethodBuilderStart)
            {
                next++;
            }
            return AsyncRuntimeSequenceMatch.Match(next - start);
        }

        protected override AsyncRuntimeSequenceMatch MatchV1AwaitRegistration(
            AsyncStackTransformContext context, int start, int end)
        {
            AsyncRuntimeSequenceMatch common = base.MatchV1AwaitRegistration(context, start, end);
            return common.IsMatch && start + common.Length < end &&
                   context.Frames[start + common.Length].SyncFrameKind == StitchSyncFrameKind.V1MethodBuilderInfrastructure
                ? AsyncRuntimeSequenceMatch.Match(common.Length + 1) : common;
        }

        protected override AsyncRuntimeSequenceMatch MatchAsyncProfilerEntry(
            AsyncStackTransformContext context, int start, int end)
        {
            if (end - start >= 2 && end < context.Frames.Count &&
                context.Frames[start].SyncFrameKind == StitchSyncFrameKind.AsyncProfilerContextResume &&
                context.Frames[start + 1].SyncFrameKind == StitchSyncFrameKind.V1MethodBuilderInfrastructure)
            {
                bool dispatcher = context.Frames[end].Origin == StitchedFrameOrigin.AsyncDispatchTarget &&
                    !context.Frames[end].IsStateMachineBoxCarrier;
                return end - start == (dispatcher ? 3 : 2) &&
                       (!dispatcher || context.ClassifyBoundary(context.Frames[start + 2].CodeAddress).Kind ==
                            AsyncStitchBoundaryKind.V1Dispatcher)
                    ? AsyncRuntimeSequenceMatch.Match(end - start) : default;
            }
            return base.MatchAsyncProfilerEntry(context, start, end);
        }
    }
}
