// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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

            pipeline.ApplyInPlace(Context(frames, diagnostics));

            Assert.Equal(new[] { CA(10), CA(11), CA(14) }, frames.Select(frame => frame.CodeAddress));
            Assert.Equal(
                StitchedFramePresentation.LogicalStateMachineMethod,
                frames[1].Presentation);
            Assert.Equal(1, diagnostics.V1SynchronousMoveNextFramesNormalized);
            Assert.Equal(2, diagnostics.V1SynchronousStartupFramesCollapsed);
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
        public void SystemPrivateCoreLibCleanup_IsOptIn()
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

            pipeline.ApplyInPlace(context);
            Assert.Equal(new[] { CA(30), CA(31), CA(32) }, frames.Select(frame => frame.CodeAddress));

            pipeline.EnableSystemPrivateCoreLibCleanup = true;
            pipeline.ApplyInPlace(context);
            Assert.Equal(new[] { CA(30), CA(32) }, frames.Select(frame => frame.CodeAddress));
            Assert.Equal(1, diagnostics.SystemPrivateCoreLibFramesCollapsed);
        }

        [Fact]
        public void SystemPrivateCoreLibCleanup_RemovesBuilderAwaitUnsafeOnCompletedOverloads()
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
                Sync(50),
            };
            var pipeline = new AsyncStackTransformPipeline
            {
                EnableConservativeTransforms = false,
                EnableSystemPrivateCoreLibCleanup = true,
            };

            pipeline.ApplyInPlace(Context(frames, diagnostics));

            Assert.Equal(new[] { CA(40), CA(49), CA(50) }, frames.Select(frame => frame.CodeAddress));
            Assert.Equal(8, diagnostics.SystemPrivateCoreLibFramesCollapsed);
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
            StitchedFrame.CreateSync(new StitchSyncFrame(CA(codeAddress), MethodIndex.Invalid, kind));

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
