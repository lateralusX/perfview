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
    public class AsyncProfilerRuntimeSequenceTests
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
            foreach (AsyncProfilerRuntimeRuleProfile profile in new[] { AsyncProfilerRuntimeRuleProfile.Common, AsyncProfilerRuntimeRuleProfile.CoreClr11 })
            {
                AsyncProfilerRuntimeSequenceMatch match = fixture.Context(_ => profile).MatchRuntimeSequence(
                    (AsyncProfilerRuntimeSequenceKind)kind, 0, fixture.Frames.Count);
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
            AsyncProfilerRuntimeSequenceMatch match = fixture.Context().MatchRuntimeSequence(
                (AsyncProfilerRuntimeSequenceKind)kind, 0, fixture.Frames.Count);
            Assert.True(match.IsMatch);
            Assert.Equal(length, match.Length);
            Assert.Equal(AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult,
                fixture.Profile.ClassifySynchronousFrame(AsyncProfilerExtendedCompletionRuntimeProfile.RenamedCompletionName, true));
            Assert.Equal(AsyncProfilerStitchSyncFrameKind.None,
                fixture.Profile.ClassifySynchronousFrame(AsyncProfilerExtendedCompletionRuntimeProfile.RenamedCompletionName, false));
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
            foreach (bool enableNormalization in new[] { false, true })
            {
                var fixture = new SequenceFixture(pattern);
                new AsyncProfilerCallStackTransformPipeline
                {
                    EnableNormalization = enableNormalization,
                    EnableInfrastructureCleanup = true,
                }.ApplyInPlace(fixture.Context());
                Assert.Equal(expected, fixture.RemainingPattern);
                Assert.Equal(pattern.Length - expected.Length, fixture.Diagnostics.AsyncInfrastructureFramesCollapsed);
            }
        }

        [Fact]
        public void OnePipeline_UsesEachCandidatesProfileAndRejectsCrossProfileSpans()
        {
            var fixture = new SequenceFixture("RHTSRTS");
            var pipeline = new AsyncProfilerCallStackTransformPipeline { EnableInfrastructureCleanup = true };
            pipeline.ApplyInPlace(fixture.Context(ca =>
                (int)ca < 4 ? fixture.Profile : AsyncProfilerRuntimeRuleProfile.Common));
            Assert.Empty(fixture.Frames);
            Assert.Equal(7, fixture.Diagnostics.AsyncInfrastructureFramesCollapsed);

            fixture = new SequenceFixture("RHTS");
            pipeline.ApplyInPlace(fixture.Context(ca =>
                (int)ca == 1 ? AsyncProfilerRuntimeRuleProfile.Common : fixture.Profile));
            Assert.Equal("RHTS", fixture.RemainingPattern);
            Assert.Equal(0, fixture.Diagnostics.AsyncInfrastructureFramesCollapsed);
        }

        [Fact]
        public void SequenceBounds_DoNotCrossProtectedEndsAndInvalidMatchersThrow()
        {
            var fixture = new SequenceFixture("RHTS");
            AsyncProfilerCallStackTransformContext context = fixture.Context();
            Assert.False(context.MatchRuntimeSequence(AsyncProfilerRuntimeSequenceKind.TaskCompletionChain, 0, 3).IsMatch);
            Assert.Equal(0, context.MatchRuntimeSequence(AsyncProfilerRuntimeSequenceKind.TaskCompletionChain, 4, 4).Length);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                context.MatchRuntimeSequence(AsyncProfilerRuntimeSequenceKind.TaskCompletionChain, 0, 5));
            Assert.Throws<InvalidOperationException>(() =>
                fixture.Context(_ => new OutOfBoundsProfile()).MatchRuntimeSequence(
                    AsyncProfilerRuntimeSequenceKind.TaskCompletionChain, 0, 4));
        }

        private sealed class SequenceFixture
        {
            public SequenceFixture(string pattern)
            {
                m_pattern = pattern;
                Profile = new AsyncProfilerExtendedCompletionRuntimeProfile((context, frame) =>
                    pattern[(int)frame.CodeAddress] == 'H'
                        ? AsyncProfilerExtendedCompletionRuntimeProfile.InjectedHelperName : "Unknown.Helper");
                for (int i = 0; i < pattern.Length; i++)
                {
                    AsyncProfilerStitchSyncFrameKind kind;
                    switch (pattern[i])
                    {
                        case 'R': kind = AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations; break;
                        case 'T': kind = AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult; break;
                        case 'S': kind = AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskCompletionSourceSetResult; break;
                        case 'B': kind = AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure; break;
                        case 'X': kind = AsyncProfilerStitchSyncFrameKind.V1AwaiterRegistrationInfrastructure; break;
                        default: kind = AsyncProfilerStitchSyncFrameKind.None; break;
                    }
                    Frames.Add(AsyncProfilerStitchedFrame.CreateSync(
                        new AsyncProfilerStitchSyncFrame((CodeAddressIndex)i,
                            (MethodIndex)(pattern[i] == 'I' ? 100 : pattern[i] == 'D' ? 101 : i), kind), null));
                }
            }

            public AsyncProfilerExtendedCompletionRuntimeProfile Profile { get; }
            public List<AsyncProfilerStitchedFrame> Frames { get; } = new List<AsyncProfilerStitchedFrame>();
            public AsyncProfilerCallStackDiagnostics Diagnostics { get; } = new AsyncProfilerCallStackDiagnostics();
            public string RemainingPattern => new string(Frames.Select(frame => m_pattern[(int)frame.CodeAddress]).ToArray());

            public AsyncProfilerCallStackTransformContext Context(Func<CodeAddressIndex, AsyncProfilerRuntimeRuleProfile> runtimeProfileResolver = null) =>
                new AsyncProfilerCallStackTransformContext(null, new List<AsyncProfilerCallStack>(), 1,
                    ca => m_pattern[(int)ca] == 'I' || m_pattern[(int)ca] == 'D'
                        ? new AsyncProfilerCallStackStitchBoundaryInfo(AsyncProfilerCallStackStitchBoundaryKind.V2DispatchContinuation, -1)
                        : AsyncProfilerCallStackStitchBoundaryInfo.None,
                    Diagnostics, Frames, runtimeProfileResolver ?? (_ => Profile));

            private readonly string m_pattern;
        }

        private sealed class OutOfBoundsProfile : AsyncProfilerRuntimeRuleProfile
        {
            public OutOfBoundsProfile() : base("Invalid test profile")
            {
            }

            protected override AsyncProfilerRuntimeSequenceMatch MatchTaskCompletionChain(
                AsyncProfilerCallStackTransformContext context, int start, int end) =>
                AsyncProfilerRuntimeSequenceMatch.Match(end - start + 1);
        }
    }

    // A hypothetical layout, never selected by production version detection.
    internal sealed class AsyncProfilerExtendedCompletionRuntimeProfile : AsyncProfilerRuntimeRuleProfile
    {
        public AsyncProfilerExtendedCompletionRuntimeProfile(
            Func<AsyncProfilerCallStackTransformContext, AsyncProfilerStitchedFrame, string> methodName = null)
            : base("Test-only extended completion layout")
        {
            m_methodName = methodName ??
                ((context, frame) => context.TraceLog.CodeAddresses[frame.CodeAddress].FullMethodName);
        }

        public const string InjectedHelperName = "System.Threading.Tasks.Task.CompletionHelper";
        public const string RenamedCompletionName = "System.Threading.Tasks.Task.TryComplete";

        public override AsyncProfilerStitchSyncFrameKind ClassifySynchronousFrame(string frameName, bool isHostModule) =>
            isHostModule && frameName == RenamedCompletionName
                ? AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult
                : base.ClassifySynchronousFrame(frameName, isHostModule);

        protected override AsyncProfilerRuntimeSequenceMatch MatchTaskCompletionPair(
            AsyncProfilerCallStackTransformContext context, int start, int end)
        {
            if (start == end || !IsKind(context.Frames[start], AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations))
            {
                return default;
            }
            return end - start >= 3 &&
                   context.Frames[start + 1].Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                   m_methodName(context, context.Frames[start + 1]) == InjectedHelperName &&
                   IsKind(context.Frames[start + 2], AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskTrySetResult)
                ? AsyncProfilerRuntimeSequenceMatch.Match(3)
                : AsyncProfilerRuntimeSequenceMatch.Reject(Math.Min(3, end - start));
        }

        protected override AsyncProfilerRuntimeSequenceMatch MatchTaskCompletionChain(
            AsyncProfilerCallStackTransformContext context, int start, int end)
        {
            AsyncProfilerRuntimeSequenceMatch match = base.MatchTaskCompletionChain(context, start, end);
            return match.Length == 0 && start < end &&
                   IsKind(context.Frames[start], AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations)
                ? AsyncProfilerRuntimeSequenceMatch.Reject(Math.Min(4, end - start))
                : match;
        }

        protected override AsyncProfilerRuntimeSequenceMatch MatchV2CompletionHandoff(
            AsyncProfilerCallStackTransformContext context, int start, int end)
        {
            AsyncProfilerRuntimeSequenceMatch match = base.MatchV2CompletionHandoff(context, start, end);
            return match.Length == 0 && end - start >= 2 &&
                   IsKind(context.Frames[start], AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibAsyncBridgeInfrastructure) &&
                   IsKind(context.Frames[start + 1], AsyncProfilerStitchSyncFrameKind.SystemPrivateCoreLibTaskRunContinuations)
                ? AsyncProfilerRuntimeSequenceMatch.Reject(Math.Min(6, end - start))
                : match;
        }

        #region private

        private static bool IsKind(AsyncProfilerStitchedFrame frame, AsyncProfilerStitchSyncFrameKind kind) =>
            frame.Origin == AsyncProfilerStitchedFrameOrigin.Sync && frame.SyncFrameKind == kind;

        private readonly Func<AsyncProfilerCallStackTransformContext, AsyncProfilerStitchedFrame, string> m_methodName;

        #endregion
    }
}
