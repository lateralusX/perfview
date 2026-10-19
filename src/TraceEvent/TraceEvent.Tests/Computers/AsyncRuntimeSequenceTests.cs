// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Etlx;

using Xunit;

namespace TraceEventTests
{
    public class AsyncRuntimeSequenceTests
    {
        [Theory]
        [InlineData("RT", 0, 2)]
        [InlineData("RTS", 1, 3)]
        [InlineData("BRTID", 2, 5)]
        [InlineData("R", 0, 0)]
        [InlineData("RATS", 1, 0)]
        [InlineData("BRTII", 2, 0)]
        public void CommonAndCoreClr11_PreserveExistingLayouts(string pattern, int kind, int length)
        {
            var fixture = new SequenceFixture(pattern);
            foreach (AsyncRuntimeRuleProfile profile in new[] { AsyncRuntimeRuleProfile.Common, AsyncRuntimeRuleProfile.CoreClr11 })
            {
                AsyncRuntimeSequenceMatch match = fixture.Context(_ => profile).MatchRuntimeSequence(
                    (AsyncRuntimeSequenceKind)kind, 0, fixture.Frames.Count);
                Assert.Equal(length, match.Length);
                Assert.Equal(length != 0, match.IsMatch);
                Assert.False(match.IsRejected);
            }
        }

        [Theory]
        [InlineData("RHT", 0, 3)]
        [InlineData("RHTS", 1, 4)]
        [InlineData("BRHTID", 2, 6)]
        public void ChangedPair_IsReusedByChainsAndCompletedDispatchTails(string pattern, int kind, int length)
        {
            var fixture = new SequenceFixture(pattern);
            AsyncRuntimeSequenceMatch match = fixture.Context().MatchRuntimeSequence(
                (AsyncRuntimeSequenceKind)kind, 0, fixture.Frames.Count);
            Assert.True(match.IsMatch);
            Assert.Equal(length, match.Length);
            Assert.Equal(StitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult,
                fixture.Profile.ClassifySynchronousFrame(ExtendedCompletionRuntimeProfile.RenamedCompletionName, true));
            Assert.Equal(StitchSyncFrameKind.None,
                fixture.Profile.ClassifySynchronousFrame(ExtendedCompletionRuntimeProfile.RenamedCompletionName, false));
        }

        [Theory]
        [InlineData("XRATSXRHTSXRHTS", "RATS")]
        [InlineData("RATSXRHTSXRATSXRHTS", "RATSRATS")]
        [InlineData("R", "R")]
        [InlineData("RH", "RH")]
        [InlineData("RHT", "RHT")]
        [InlineData("RHTA", "RHTA")]
        [InlineData("RUTS", "RUTS")]
        [InlineData("BRATID", "BRATID")]
        [InlineData("BRHTII", "BRHTII")]
        [InlineData("BRHTI", "BRHTI")]
        public void RejectedCandidates_StayProtectedWhileOtherSpansAreRemoved(string pattern, string expected)
        {
            foreach (bool conservative in new[] { false, true })
            {
                var fixture = new SequenceFixture(pattern);
                new AsyncStackTransformPipeline
                {
                    EnableConservativeTransforms = conservative,
                    EnableAsyncInfrastructureCleanup = true,
                }.ApplyInPlace(fixture.Context());
                Assert.Equal(expected, fixture.RemainingPattern);
                Assert.Equal(pattern.Length - expected.Length, fixture.Diagnostics.AsyncInfrastructureFramesCollapsed);
            }
        }

        [Fact]
        public void OnePipeline_UsesEachCandidatesProfileAndRejectsCrossProfileSpans()
        {
            var fixture = new SequenceFixture("RHTSRTS");
            var pipeline = new AsyncStackTransformPipeline { EnableAsyncInfrastructureCleanup = true };
            pipeline.ApplyInPlace(fixture.Context(ca =>
                (int)ca < 4 ? fixture.Profile : AsyncRuntimeRuleProfile.Common));
            Assert.Empty(fixture.Frames);
            Assert.Equal(7, fixture.Diagnostics.AsyncInfrastructureFramesCollapsed);

            fixture = new SequenceFixture("RHTS");
            pipeline.ApplyInPlace(fixture.Context(ca =>
                (int)ca == 1 ? AsyncRuntimeRuleProfile.Common : fixture.Profile));
            Assert.Equal("RHTS", fixture.RemainingPattern);
            Assert.Equal(0, fixture.Diagnostics.AsyncInfrastructureFramesCollapsed);
        }

        [Fact]
        public void SequenceBounds_DoNotCrossProtectedEndsAndInvalidMatchersThrow()
        {
            var fixture = new SequenceFixture("RHTS");
            AsyncStackTransformContext context = fixture.Context();
            Assert.False(context.MatchRuntimeSequence(AsyncRuntimeSequenceKind.TaskCompletionChain, 0, 3).IsMatch);
            Assert.Equal(0, context.MatchRuntimeSequence(AsyncRuntimeSequenceKind.TaskCompletionChain, 4, 4).Length);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                context.MatchRuntimeSequence(AsyncRuntimeSequenceKind.TaskCompletionChain, 0, 5));
            Assert.Throws<InvalidOperationException>(() =>
                fixture.Context(_ => new OutOfBoundsProfile()).MatchRuntimeSequence(
                    AsyncRuntimeSequenceKind.TaskCompletionChain, 0, 4));
        }

        private sealed class SequenceFixture
        {
            public SequenceFixture(string pattern)
            {
                m_pattern = pattern;
                Profile = new ExtendedCompletionRuntimeProfile((context, frame) =>
                    pattern[(int)frame.CodeAddress] == 'H'
                        ? ExtendedCompletionRuntimeProfile.InjectedHelperName : "Unknown.Helper");
                for (int i = 0; i < pattern.Length; i++)
                {
                    StitchSyncFrameKind kind;
                    switch (pattern[i])
                    {
                        case 'R': kind = StitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations; break;
                        case 'T': kind = StitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult; break;
                        case 'S': kind = StitchSyncFrameKind.SystemPrivateCoreLibTaskCompletionSourceSetResult; break;
                        case 'B': kind = StitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure; break;
                        case 'X': kind = StitchSyncFrameKind.V1AwaiterRegistrationInfrastructure; break;
                        default: kind = StitchSyncFrameKind.None; break;
                    }
                    Frames.Add(StitchedFrame.CreateSync(
                        new StitchSyncFrame((CodeAddressIndex)i,
                            (MethodIndex)(pattern[i] == 'I' ? 100 : pattern[i] == 'D' ? 101 : i), kind), null));
                }
            }

            public ExtendedCompletionRuntimeProfile Profile { get; }
            public List<StitchedFrame> Frames { get; } = new List<StitchedFrame>();
            public StitchDiagnostics Diagnostics { get; } = new StitchDiagnostics();
            public string RemainingPattern => new string(Frames.Select(frame => m_pattern[(int)frame.CodeAddress]).ToArray());

            public AsyncStackTransformContext Context(Func<CodeAddressIndex, AsyncRuntimeRuleProfile> runtimeProfileResolver = null) =>
                new AsyncStackTransformContext(null, new List<AsyncCallStack>(), 1,
                    ca => m_pattern[(int)ca] == 'I' || m_pattern[(int)ca] == 'D'
                        ? new AsyncStitchBoundaryInfo(AsyncStitchBoundaryKind.V2DispatchContinuation, -1)
                        : AsyncStitchBoundaryInfo.None,
                    Diagnostics, Frames, runtimeProfileResolver ?? (_ => Profile));

            private readonly string m_pattern;
        }

        private sealed class OutOfBoundsProfile : AsyncRuntimeRuleProfile
        {
            public OutOfBoundsProfile() : base("Invalid test profile")
            {
            }

            protected override AsyncRuntimeSequenceMatch MatchTaskCompletionChain(
                AsyncStackTransformContext context, int start, int end) =>
                AsyncRuntimeSequenceMatch.Match(end - start + 1);
        }
    }

    // A hypothetical layout, never selected by production version detection.
    internal sealed class ExtendedCompletionRuntimeProfile : AsyncRuntimeRuleProfile
    {
        public ExtendedCompletionRuntimeProfile(
            Func<AsyncStackTransformContext, StitchedFrame, string> methodName = null)
            : base("Test-only extended completion layout")
        {
            m_methodName = methodName ??
                ((context, frame) => context.TraceLog.CodeAddresses[frame.CodeAddress].FullMethodName);
        }

        public const string InjectedHelperName = "System.Threading.Tasks.Task.CompletionHelper";
        public const string RenamedCompletionName = "System.Threading.Tasks.Task.TryComplete";

        public override StitchSyncFrameKind ClassifySynchronousFrame(string frameName, bool isHostModule) =>
            isHostModule && frameName == RenamedCompletionName
                ? StitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult
                : base.ClassifySynchronousFrame(frameName, isHostModule);

        protected override AsyncRuntimeSequenceMatch MatchTaskCompletionPair(
            AsyncStackTransformContext context, int start, int end)
        {
            if (start == end || !IsKind(context.Frames[start], StitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations))
            {
                return default;
            }
            return end - start >= 3 &&
                   context.Frames[start + 1].Origin == StitchedFrameOrigin.Sync &&
                   m_methodName(context, context.Frames[start + 1]) == InjectedHelperName &&
                   IsKind(context.Frames[start + 2], StitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult)
                ? AsyncRuntimeSequenceMatch.Match(3)
                : AsyncRuntimeSequenceMatch.Reject(Math.Min(3, end - start));
        }

        protected override AsyncRuntimeSequenceMatch MatchTaskCompletionChain(
            AsyncStackTransformContext context, int start, int end)
        {
            AsyncRuntimeSequenceMatch match = base.MatchTaskCompletionChain(context, start, end);
            return match.Length == 0 && start < end &&
                   IsKind(context.Frames[start], StitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations)
                ? AsyncRuntimeSequenceMatch.Reject(Math.Min(4, end - start))
                : match;
        }

        protected override AsyncRuntimeSequenceMatch MatchV2CompletionHandoff(
            AsyncStackTransformContext context, int start, int end)
        {
            AsyncRuntimeSequenceMatch match = base.MatchV2CompletionHandoff(context, start, end);
            return match.Length == 0 && end - start >= 2 &&
                   IsKind(context.Frames[start], StitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure) &&
                   IsKind(context.Frames[start + 1], StitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations)
                ? AsyncRuntimeSequenceMatch.Reject(Math.Min(6, end - start))
                : match;
        }

        #region private

        private static bool IsKind(StitchedFrame frame, StitchSyncFrameKind kind) =>
            frame.Origin == StitchedFrameOrigin.Sync && frame.SyncFrameKind == kind;

        private readonly Func<AsyncStackTransformContext, StitchedFrame, string> m_methodName;

        #endregion
    }
}
