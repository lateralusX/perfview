// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Etlx;

using Xunit;

using static Microsoft.Diagnostics.Tracing.Computers.AsyncProfilerRuntimeRuleProfile;

namespace TraceEventTests
{
    /// <summary>Checks runtime-specific sequence matching and protection of rejected spans.</summary>
    public class AsyncProfilerRuntimeSequenceTests
    {
        [Theory]
        [InlineData("RT", (int)AsyncProfilerRuntimeSequenceKind.TaskCompletion, 2)]
        [InlineData("RTS", (int)AsyncProfilerRuntimeSequenceKind.TaskCompletionChain, 3)]
        [InlineData("BRTID", (int)AsyncProfilerRuntimeSequenceKind.V2CompletionHandoff, 5)]
        [InlineData("R", (int)AsyncProfilerRuntimeSequenceKind.TaskCompletion, 0)]
        [InlineData("RATS", (int)AsyncProfilerRuntimeSequenceKind.TaskCompletionChain, 0)]
        [InlineData("BRTII", (int)AsyncProfilerRuntimeSequenceKind.V2CompletionHandoff, 0)]
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
        [InlineData("RHT", (int)AsyncProfilerRuntimeSequenceKind.TaskCompletion, 3)]
        [InlineData("RHTS", (int)AsyncProfilerRuntimeSequenceKind.TaskCompletionChain, 4)]
        [InlineData("BRHTID", (int)AsyncProfilerRuntimeSequenceKind.V2CompletionHandoff, 6)]
        public void ExtendedTaskCompletion_IsReusedByChainsAndCompletedDispatchTails(string pattern, int kind, int length)
        {
            var fixture = new SequenceFixture(pattern);
            AsyncProfilerRuntimeSequenceMatch match = fixture.Context().MatchRuntimeSequence(
                (AsyncProfilerRuntimeSequenceKind)kind, 0, fixture.Frames.Count);
            Assert.True(match.IsMatch);
            Assert.Equal(length, match.Length);
            Assert.Equal(HelperKind.TaskResult,
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
        public void RejectedCandidates_StayProtectedWhileOtherSpansAreGrouped(string pattern, string expected)
        {
            foreach (bool enableNormalization in new[] { false, true })
            {
                var fixture = new SequenceFixture(pattern);
                new AsyncProfilerCallStackTransformPipeline
                {
                    EnableNormalization = enableNormalization,
                    EnableInfrastructureCleanup = true,
                }.ApplyInPlace(fixture.Context());
                Assert.Equal(expected, fixture.RemainingPhysicalPattern);
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
            Assert.Equal(new[] { 0, 4 }, fixture.Frames.Select(frame => (int)frame.CodeAddress));
            Assert.All(fixture.Frames, frame =>
                Assert.Equal(AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames, frame.Presentation));
            Assert.Equal(7, fixture.Diagnostics.AsyncInfrastructureFramesCollapsed);

            fixture = new SequenceFixture("RHTS");
            pipeline.ApplyInPlace(fixture.Context(ca =>
                (int)ca == 1 ? AsyncProfilerRuntimeRuleProfile.Common : fixture.Profile));
            Assert.Equal("RHTS", fixture.RemainingPhysicalPattern);
            Assert.Equal(0, fixture.Diagnostics.AsyncInfrastructureFramesCollapsed);
        }

        [Fact]
        public void SequenceBounds_DoNotCrossProtectedEndsAndInvalidMatchersThrow()
        {
            var fixture = new SequenceFixture("RHTS");
            AsyncProfilerCallStackTransformState context = fixture.Context();
            Assert.False(context.MatchRuntimeSequence(AsyncProfilerRuntimeSequenceKind.TaskCompletionChain, 0, 3).IsMatch);
            Assert.Equal(0, context.MatchRuntimeSequence(AsyncProfilerRuntimeSequenceKind.TaskCompletionChain, 4, 4).Length);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                context.MatchRuntimeSequence(AsyncProfilerRuntimeSequenceKind.TaskCompletionChain, 0, 5));
            Assert.Throws<InvalidOperationException>(() =>
                fixture.Context(_ => new OutOfBoundsProfile()).MatchRuntimeSequence(
                    AsyncProfilerRuntimeSequenceKind.TaskCompletionChain, 0, 4));
        }

        [Theory]
        [InlineData("System.Runtime.CompilerServices.AsyncTaskMethodBuilder.AwaitUnsafeOnCompleted()", true)]
        [InlineData("System.Runtime.CompilerServices.TaskAwaiter.OnCompleted()", true)]
        [InlineData("System.Runtime.CompilerServices.TaskAwaiter.get_IsCompleted()", false)]
        [InlineData("System.Runtime.CompilerServices.AsyncMethodBuilderCore.Start()", false)]
        public void ProfileOnlyDetails_PreserveRegistrationMatchingAfterPresentationChanges(string name, bool registration)
        {
            AsyncProfilerRuntimeRuleProfile profile = AsyncProfilerRuntimeRuleProfile.Common;
            FrameClassification classification = profile.ClassifySynchronousFrame(name, true);
            Assert.Equal(AsyncProfilerStitchSyncFrameKind.None, classification.Kind);
            var frames = new List<AsyncProfilerStitchedFrame>
            {
                AsyncProfilerStitchedFrame.CreateSync(
                    new AsyncProfilerStitchSyncFrame((CodeAddressIndex)1, MethodIndex.Invalid, classification), null)
                    .WithPresentation(AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames),
            };
            var context = new AsyncProfilerCallStackTransformState(
                null, new List<AsyncProfilerCallStack>(), 1, _ => AsyncProfilerCallStackStitchBoundaryInfo.None,
                new AsyncProfilerCallStackDiagnostics(), frames);

            AsyncProfilerRuntimeSequenceMatch match = context.MatchRuntimeSequence(
                AsyncProfilerRuntimeSequenceKind.V1AwaitRegistration, 0, 1);
            Assert.Equal(registration, match.IsMatch);
            Assert.Equal(registration ? 1 : 0, match.Length);
        }

        [Theory]
        [InlineData("AsyncCallstack.CaptureStateMachineAsyncCallstack()", false)]
        [InlineData("AsyncCallstack.EmitAsyncCallstack()", false)]
        [InlineData("ResumeAsyncContext.Resume()", true)]
        public void SharedProfilerEntryRole_DoesNotMakeEntryPhasesInterchangeable(string method, bool entry)
        {
            AsyncProfilerRuntimeRuleProfile profile = AsyncProfilerRuntimeRuleProfile.Common;
            FrameClassification classification = profile.ClassifySynchronousFrame(
                "System.Runtime.CompilerServices.AsyncProfiler+" + method, true);
            Assert.Equal(AsyncProfilerStitchSyncFrameKind.ProfilerEntry, classification.Kind);
            var frames = new List<AsyncProfilerStitchedFrame>
            {
                AsyncProfilerStitchedFrame.CreateSync(
                    new AsyncProfilerStitchSyncFrame((CodeAddressIndex)1, MethodIndex.Invalid, classification), null),
                AsyncProfilerStitchedFrame.CreateSync(
                    new AsyncProfilerStitchSyncFrame((CodeAddressIndex)2, MethodIndex.Invalid), null),
            };
            var context = new AsyncProfilerCallStackTransformState(
                null, new List<AsyncProfilerCallStack>(), 1, _ => AsyncProfilerCallStackStitchBoundaryInfo.None,
                new AsyncProfilerCallStackDiagnostics(), frames);

            Assert.True(AsyncProfilerCallStackTransformHelpers.IsSyncFrameKind(
                frames[0], AsyncProfilerStitchSyncFrameKind.ProfilerEntry));
            AsyncProfilerRuntimeSequenceMatch match = context.MatchRuntimeSequence(
                AsyncProfilerRuntimeSequenceKind.ProfilerEntry, 0, 1);
            Assert.Equal(entry, match.IsMatch);
            Assert.Equal(entry ? 1 : 0, match.Length);
        }

        #region private

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
                    FrameClassification kind;
                    switch (pattern[i])
                    {
                        case 'R':
                            kind = HelperKind.TaskContinuations;
                            break;
                        case 'T':
                            kind = HelperKind.TaskResult;
                            break;
                        case 'S':
                            kind = HelperKind.TaskCompletionSourceResult;
                            break;
                        case 'B':
                            kind = AsyncProfilerStitchSyncFrameKind.ContinuationBridge;
                            break;
                        case 'X':
                            kind = HelperKind.V1AwaiterRegistration;
                            break;
                        default:
                            kind = AsyncProfilerStitchSyncFrameKind.None;
                            break;
                    }
                    // I/D are distinct dispatch methods; repeated I models the same dispatch method twice.
                    Frames.Add(AsyncProfilerStitchedFrame.CreateSync(
                        new AsyncProfilerStitchSyncFrame((CodeAddressIndex)i,
                            (MethodIndex)(pattern[i] == 'I' ? 100 : pattern[i] == 'D' ? 101 : i), kind), null));
                }
            }

            public AsyncProfilerExtendedCompletionRuntimeProfile Profile { get; }
            public List<AsyncProfilerStitchedFrame> Frames { get; } = new List<AsyncProfilerStitchedFrame>();
            public AsyncProfilerCallStackDiagnostics Diagnostics { get; } = new AsyncProfilerCallStackDiagnostics();
            public string RemainingPhysicalPattern => new string(Frames
                .Where(frame => frame.Presentation != AsyncProfilerStitchedFramePresentation.AsyncDispatcherFrames)
                .Select(frame => m_pattern[(int)frame.CodeAddress]).ToArray());

            public AsyncProfilerCallStackTransformState Context(Func<CodeAddressIndex, AsyncProfilerRuntimeRuleProfile> runtimeProfileResolver = null) =>
                new AsyncProfilerCallStackTransformState(null, new List<AsyncProfilerCallStack>(), 1,
                    ca => m_pattern[(int)ca] == 'I' || m_pattern[(int)ca] == 'D'
                        ? new AsyncProfilerCallStackStitchBoundaryInfo(AsyncProfilerCallStackStitchBoundaryKind.V2DispatcherInfrastructure, -1)
                        : AsyncProfilerCallStackStitchBoundaryInfo.None,
                    Diagnostics, Frames, runtimeProfileResolver ?? (_ => Profile));

            #region private

            private readonly string m_pattern;

            #endregion
        }

        private sealed class OutOfBoundsProfile : AsyncProfilerRuntimeRuleProfile
        {
            public OutOfBoundsProfile() : base("Invalid test profile")
            {
            }

            protected override AsyncProfilerRuntimeSequenceMatch MatchTaskCompletionChain(
                AsyncProfilerCallStackTransformState context, int start, int end) =>
                AsyncProfilerRuntimeSequenceMatch.Match(end - start + 1);
        }

        #endregion
    }

    // A hypothetical layout, never selected by production version detection.
    internal sealed class AsyncProfilerExtendedCompletionRuntimeProfile : AsyncProfilerRuntimeRuleProfile
    {
        public AsyncProfilerExtendedCompletionRuntimeProfile(
            Func<AsyncProfilerCallStackTransformState, AsyncProfilerStitchedFrame, string> methodName = null)
            : base("Test-only extended completion layout")
        {
            m_methodName = methodName ??
                ((context, frame) => context.TraceLog.CodeAddresses[frame.CodeAddress].FullMethodName);
        }

        public const string InjectedHelperName = "System.Threading.Tasks.Task.CompletionHelper";
        public const string RenamedCompletionName = "System.Threading.Tasks.Task.TryComplete";

        public override FrameClassification ClassifySynchronousFrame(string frameName, bool isHostModule) =>
            isHostModule && frameName == RenamedCompletionName
                ? (FrameClassification)HelperKind.TaskResult
                : base.ClassifySynchronousFrame(frameName, isHostModule);

        protected override AsyncProfilerRuntimeSequenceMatch MatchTaskCompletion(
            AsyncProfilerCallStackTransformState context, int start, int end)
        {
            if (start == end || !IsKind(context.Frames[start], HelperKind.TaskContinuations))
            {
                return default;
            }
            return end - start >= 3 &&
                   context.Frames[start + 1].Origin == AsyncProfilerStitchedFrameOrigin.Sync &&
                   m_methodName(context, context.Frames[start + 1]) == InjectedHelperName &&
                   IsKind(context.Frames[start + 2], HelperKind.TaskResult)
                ? AsyncProfilerRuntimeSequenceMatch.Match(3)
                : AsyncProfilerRuntimeSequenceMatch.Reject(Math.Min(3, end - start));
        }

        protected override AsyncProfilerRuntimeSequenceMatch MatchTaskCompletionChain(
            AsyncProfilerCallStackTransformState context, int start, int end)
        {
            AsyncProfilerRuntimeSequenceMatch match = base.MatchTaskCompletionChain(context, start, end);
            return match.Length == 0 && start < end &&
                   IsKind(context.Frames[start], HelperKind.TaskContinuations)
                ? AsyncProfilerRuntimeSequenceMatch.Reject(Math.Min(4, end - start))
                : match;
        }

        protected override AsyncProfilerRuntimeSequenceMatch MatchV2CompletionHandoff(
            AsyncProfilerCallStackTransformState context, int start, int end)
        {
            AsyncProfilerRuntimeSequenceMatch match = base.MatchV2CompletionHandoff(context, start, end);
            return match.Length == 0 && end - start >= 2 &&
                   IsKind(context.Frames[start], AsyncProfilerStitchSyncFrameKind.ContinuationBridge) &&
                   IsKind(context.Frames[start + 1], HelperKind.TaskContinuations)
                ? AsyncProfilerRuntimeSequenceMatch.Reject(Math.Min(6, end - start))
                : match;
        }

        #region private

        private static bool IsKind(AsyncProfilerStitchedFrame frame, FrameClassification kind) =>
            frame.Origin == AsyncProfilerStitchedFrameOrigin.Sync && frame.SyncClassification == kind;

        private readonly Func<AsyncProfilerCallStackTransformState, AsyncProfilerStitchedFrame, string> m_methodName;

        #endregion
    }
}
