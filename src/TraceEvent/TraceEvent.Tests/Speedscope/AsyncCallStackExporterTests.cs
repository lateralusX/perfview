// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;
using Microsoft.Diagnostics.Tracing.Stacks.Formats;
using Xunit;

using static Microsoft.Diagnostics.Tracing.Stacks.StackSourceWriterHelper;

namespace TraceEventTests
{
    public class AsyncCallStackExporterTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RecordedProgress_CombinesSimultaneousCompletionsAndClipsOpenContext(bool runtime)
        {
            var context = Context(0, long.MaxValue, new ulong[] { 1, 2, 3, 4, 5 }, runtime: runtime,
                methods: new[] { Delta(5), Delta(20), Delta(20) }, exceptions: new[] { Delta(20) });
            var events = Export(new[] { context }, 10, 40);
            AssertEvents(events,
                (ProfileEventType.Open, 5, 10, 0),
                (ProfileEventType.Open, 4, 10, 1),
                (ProfileEventType.Open, 3, 10, 2),
                (ProfileEventType.Open, 2, 10, 3),
                (ProfileEventType.Close, 2, 20, 3),
                (ProfileEventType.Close, 3, 20, 2),
                (ProfileEventType.Close, 4, 20, 1),
                (ProfileEventType.Close, 5, 40, 0));
        }

        [Fact]
        public void NestedContexts_KeepParentAndApplyProgressAtSharedBoundary()
        {
            var outer = Context(10, 50, new ulong[] { 1, 2 }, methods: new[] { Delta(20) });
            var firstChild = Context(15, 20, new ulong[] { 3 }, depth: 1);
            var secondChild = Context(20, 25, new ulong[] { 4 }, depth: 1);
            var events = Export(new[] { firstChild, outer, secondChild });
            AssertEvents(events,
                (ProfileEventType.Open, 2, 10, 0),
                (ProfileEventType.Open, 1, 10, 1),
                (ProfileEventType.Open, 3, 15, 2),
                (ProfileEventType.Close, 3, 20, 2),
                (ProfileEventType.Close, 1, 20, 1),
                (ProfileEventType.Open, 4, 20, 1),
                (ProfileEventType.Close, 4, 25, 1),
                (ProfileEventType.Close, 2, 50, 0));
        }

        [Fact]
        public void AdjacentIdenticalContexts_KeepOccurrenceBoundariesAndGaps()
        {
            var events = Export(new[]
            {
                Context(10, 20, new ulong[] { 1 }),
                Context(20, 30, new ulong[] { 1 }),
                Context(40, 50, new ulong[] { 1 }),
                Context(60, 60, new ulong[] { 2 }),
                Context(100, 110, new ulong[] { 3 })
            });
            AssertEvents(events,
                (ProfileEventType.Open, 1, 10, 0),
                (ProfileEventType.Close, 1, 20, 0),
                (ProfileEventType.Open, 1, 20, 0),
                (ProfileEventType.Close, 1, 30, 0),
                (ProfileEventType.Open, 1, 40, 0),
                (ProfileEventType.Close, 1, 50, 0));
        }

        [Fact]
        public void V1FinalizedAppends_AreVisibleFromResumeWithoutInventedWrapperProgress()
        {
            var builder = new AsyncProfilerIndexBuilder();
            builder.Process(new AsyncProfilerBufferBuilder(123, startQpc: 1).Armed(1)
                .ResumeStack(10, 1, new ulong[] { 1 })
                .WrapperReset(15)
                .AppendStack(20, 1, new ulong[] { 2, 0 })
                .Suspend(30).Build());
            var contexts = builder.Index.GetCallStacks(new AsyncProfilerThreadKey(0, 123));
            AssertEvents(Export(contexts),
                (ProfileEventType.Open, 2, 10, 0),
                (ProfileEventType.Open, 1, 10, 1),
                (ProfileEventType.Close, 1, 30, 1),
                (ProfileEventType.Close, 2, 30, 0));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void HiddenZeroIPs_PreserveOriginalSlotsForMethodAndUnwindProgress(bool runtime)
        {
            var context = Context(10, 50, new ulong[] { 0, 1, 0, 2, 0 }, runtime: runtime,
                methods: new[] { Delta(20), Delta(40) }, exceptions: new[] { Delta(30, 2) });
            AssertEvents(Export(new[] { context }),
                (ProfileEventType.Open, 2, 10, 0),
                (ProfileEventType.Open, 1, 10, 1),
                (ProfileEventType.Close, 1, 30, 1),
                (ProfileEventType.Close, 2, 40, 0));
            Assert.Equal(5, context.CapturedFrames.Count);
            Assert.Equal(1, context.GetCompletedFrameCount(20));
            Assert.Equal(3, context.GetCompletedFrameCount(30));
            Assert.Equal(4, context.GetCompletedFrameCount(40));
        }

        [Fact]
        public void ZeroIPOnlyContexts_DoNotEmitFramesOrHideNestedNonzeroFrames()
        {
            var outer = Context(10, 50, new ulong[] { 0, 0 });
            Assert.Empty(Export(new[] { outer }));
            AssertEvents(Export(new[] { outer, Context(20, 30, new ulong[] { 1 }, depth: 1) }),
                (ProfileEventType.Open, 1, 20, 0),
                (ProfileEventType.Close, 1, 30, 0));
        }

        [Fact]
        public void ExcessRecordedCompletions_DoNotInventFrames()
        {
            var context = Context(10, 50, new ulong[] { 1 }, exceptions: new[] { Delta(20, 3) });
            AssertEvents(Export(new[] { context }),
                (ProfileEventType.Open, 1, 10, 0),
                (ProfileEventType.Close, 1, 20, 0));
        }

        [Fact]
        public void OverlappingSameDepthContexts_AreRejected()
        {
            Assert.Throws<InvalidDataException>(() => Export(new[]
            {
                Context(10, 30, new ulong[] { 1 }),
                Context(20, 40, new ulong[] { 2 })
            }));
        }

        [Fact]
        public void VirtualSampling_UsesSharedTraceGridSkipsShortContextsAndClipsFinalInterval()
        {
            AssertEvents(Export(new[]
            {
                Context(104, 108, new ulong[] { 1 }),
                Context(109, 113, new ulong[] { 2 }),
                Context(113, 114, new ulong[] { 3 }),
                Context(132, long.MaxValue, new ulong[] { 4 })
            }, start: 103, end: 138, interval: 10),
                (ProfileEventType.Open, 3, 113, 0),
                (ProfileEventType.Close, 3, 123, 0),
                (ProfileEventType.Open, 4, 133, 0),
                (ProfileEventType.Close, 4, 138, 0));
            AssertEvents(Export(new[] { Context(114, 124, new ulong[] { 5 }) },
                start: 103, end: 138, interval: 10),
                (ProfileEventType.Open, 5, 123, 0),
                (ProfileEventType.Close, 5, 133, 0));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void VirtualSampling_AppliesRecordedProgressAndNestingAtSampleInstant(bool runtime)
        {
            var outer = Context(0, 95, new ulong[] { 0, 1, 0, 2, 3 }, runtime: runtime,
                methods: new[] { Delta(15), Delta(30) }, exceptions: new[] { Delta(25, 2) });
            var child = Context(22, 35, new ulong[] { 4 }, depth: 1, runtime: runtime);
            AssertEvents(Export(new[] { outer, child }, end: 47, interval: 10),
                (ProfileEventType.Open, 3, 0, 0),
                (ProfileEventType.Open, 2, 0, 1),
                (ProfileEventType.Open, 1, 0, 2),
                (ProfileEventType.Close, 1, 30, 2),
                (ProfileEventType.Close, 2, 30, 1),
                (ProfileEventType.Open, 4, 30, 1),
                (ProfileEventType.Close, 4, 40, 1),
                (ProfileEventType.Close, 3, 47, 0));
        }

        [Fact]
        public void VirtualSampling_IntervalLongerThanTraceObservesOnlyTraceStart()
        {
            Assert.Empty(Export(new[] { Context(1, 4, new ulong[] { 1 }) }, end: 5, interval: 20));
            AssertEvents(Export(new[] { Context(0, 4, new ulong[] { 1 }) }, end: 5, interval: 20),
                (ProfileEventType.Open, 1, 0, 0),
                (ProfileEventType.Close, 1, 5, 0));
            Assert.Empty(Export(new[] { Context(0, 4, new ulong[] { 0 }) }, end: 5, interval: 20));
        }

        [Fact]
        public void VirtualSampling_UsesIntegerQpcGridNearClockLimit()
        {
            long start = long.MaxValue - 50;
            var events = SpeedScopeStackSourceWriter.GetAsyncProfileEvents(
                new[] { Context(start + 1, long.MaxValue, new ulong[] { 1 }) },
                start, start + 47, qpc => qpc - start,
                (context, slot) => (int)context.Frames.MethodIdAt(slot), sampleIntervalQpc: 20);
            AssertEvents(events,
                (ProfileEventType.Open, 1, 20, 0),
                (ProfileEventType.Close, 1, 47, 0));
        }

        [Fact]
        public void PersistedIndexOnly_ExportsWithoutSamplesOrRawEventsAndSeparatesProcessesAndThreads()
        {
            const long Start = 100_000;
            const int Process = 1234;
            const int OtherProcess = 5678;
            const ulong GhostThread = 6000;
            const string MoveNext = "Namespace.Scenarios+<DeepLevel1Async>d__8::MoveNext()";
            const string GenericMoveNext = "System.Void Namespace.Outer`1+Nested+<RunAsync>d_4`1[System.Int64]::MoveNext()";
            const string LogicalName = "[Async V1] testapp!Namespace.Scenarios.DeepLevel1Async [ordinal 8, state 4]";
            const string GenericLogicalName = "[Async V1] testapp!Namespace.Outer`1.Nested.RunAsync [ordinal 4`1[System.Int64], state 3]";
            var universalSystemProvider = new Guid("8c107b6c-79f8-5231-4de6-2a0e20a3f562");
            var buffers = new[]
            {
                new AsyncProfilerBufferBuilder(5000, startQpc: Start).Armed(Start)
                    .ResumeRuntimeStack(Start + 12, 1, new ulong[] { 0xA0 })
                    .SuspendRuntime(Start + 50).Build(),
                new AsyncProfilerBufferBuilder(GhostThread, startQpc: Start).Armed(Start)
                    .ResumeStack(Start + 10, 1, new ulong[] { 0, 0xB0 }, new[] { 2, 3 })
                    .CompleteMethod(Start + 20)
                    .AppendStack(Start + 25, 1, new ulong[] { 0xC0 }, new[] { 4 })
                    .UnwindException(Start + 30, 1)
                    .Suspend(Start + 40)
                    .ResumeStack(Start + 45, 2, new ulong[] { 0xD0 }).Build(),
                new AsyncProfilerBufferBuilder(GhostThread, startQpc: Start).Armed(Start)
                    .ResumeRuntimeStack(Start + 15, 1, new ulong[] { 0xE0 })
                    .SuspendRuntime(Start + 25).Build()
            };
            var writer = new EventPipeWriterV6();
            writer.WriteHeaders(null, syncTimeQpc: Start);
            writer.WriteMetadataBlock(
                new EventMetadata(1, AsyncProfilerTraceEventParser.ProviderName,
                    "AsyncEvents", AsyncProfilerTraceEventParser.AsyncEventsEventId),
                new EventMetadata(2, "TimelineAnchor", "End", 1),
                new EventMetadata(3, "Universal.System", "ProcessMapping", 3)
                {
                    ProviderId = universalSystemProvider,
                },
                new EventMetadata(4, "Universal.System", "ProcessSymbol", 4)
                {
                    ProviderId = universalSystemProvider,
                });
            writer.WriteThreadBlock(w =>
            {
                w.WriteThreadEntry(7, threadId: 5000, processId: Process);
                w.WriteThreadEntry(8, threadId: 7000, processId: OtherProcess);
            });
            writer.WriteEventBlock(w =>
            {
                w.WriteEventBlob(new WriteEventOptions
                {
                    MetadataId = 3, ThreadIndexOrId = 7, CaptureThreadIndexOrId = 7,
                    SequenceNumber = 1, Timestamp = Start + 1, IsSorted = true
                }, p =>
                {
                    p.WriteVarUInt(1);
                    p.WriteVarUInt(1);
                    p.WriteVarUInt(0x100);
                    p.WriteVarUInt(0);
                    WriteShortUTF8String(p, "testapp.dll");
                    p.WriteVarUInt(0);
                });
                for (int i = 0; i < buffers.Length; i++)
                {
                    int slot = i;
                    w.WriteEventBlob(new WriteEventOptions
                    {
                        MetadataId = 1,
                        ThreadIndexOrId = i == 2 ? 8 : 7,
                        CaptureThreadIndexOrId = i == 2 ? 8 : 7,
                        SequenceNumber = i + 2,
                        Timestamp = Start + 60 + i * 10,
                        IsSorted = true
                    }, p =>
                    {
                        p.Write(buffers[slot].Length);
                        p.Write(buffers[slot]);
                    });
                }
                var symbols = new (ulong Address, string Name)[]
                {
                    (0xA0, MoveNext), (0xB0, GenericMoveNext), (0xC0, MoveNext)
                };
                for (int i = 0; i < symbols.Length; i++)
                {
                    var symbol = symbols[i];
                    w.WriteEventBlob(new WriteEventOptions
                    {
                        MetadataId = 4, ThreadIndexOrId = 7, CaptureThreadIndexOrId = 7,
                        SequenceNumber = 5 + i, Timestamp = Start + 81 + i, IsSorted = true
                    }, p =>
                    {
                        p.WriteVarUInt(symbol.Address);
                        p.WriteVarUInt(1);
                        p.WriteVarUInt(symbol.Address);
                        p.WriteVarUInt(symbol.Address + 1);
                        WriteShortUTF8String(p, symbol.Name);
                    });
                }
                w.WriteEventBlob(new WriteEventOptions
                {
                    MetadataId = 2,
                    ThreadIndexOrId = 7,
                    CaptureThreadIndexOrId = 7,
                    SequenceNumber = 8,
                    Timestamp = Start + 90,
                    IsSorted = true
                }, p => { });
            });
            writer.WriteEndBlock();

            string input = Path.Combine(Path.GetTempPath(), $"async_export_{Guid.NewGuid():N}.nettrace");
            string output = Path.ChangeExtension(input, ".speedscope.json");
            string etlx = null;
            try
            {
                File.WriteAllBytes(input, writer.ToArray());
                etlx = TraceLog.CreateFromEventPipeDataFile(input);
                using (var log = new TraceLog(etlx))
                {
                    Assert.True(log.HasAsyncProfilerEvents);
                    Assert.False(log.IsAsyncProfilerCallStacksLoaded);
                    Assert.Single(log.Events.Where(e => e.ProviderName == "TimelineAnchor"));
                    Assert.DoesNotContain(log.Threads, t => (ulong)t.ThreadID == GhostThread);
                    SpeedScopeStackSourceWriter.WriteAsyncCallStacksAsJson(log, output);
                    using (var json = JsonDocument.Parse(File.ReadAllText(output)))
                    {
                        Assert.Equal(3, json.RootElement.GetProperty("profiles").GetArrayLength());
                        ValidateJson(json);
                    }

                    SpeedScopeStackSourceWriter.WriteAsyncCallStacksAsJson(log, output, Process);
                    using (var json = JsonDocument.Parse(File.ReadAllText(output)))
                    {
                        ValidateJson(json);
                        var root = json.RootElement;
                        var profiles = root.GetProperty("profiles").EnumerateArray().ToArray();
                        Assert.Equal(2, profiles.Length);
                        Assert.All(profiles, p => Assert.Contains($"({Process}, instance ", p.GetProperty("name").GetString()));
                        var ghost = profiles.Single(p => p.GetProperty("name").GetString().Contains("Thread (6000)"));
                        var frames = root.GetProperty("shared").GetProperty("frames");
                        string Name(JsonElement e) => frames[e.GetProperty("frame").GetInt32()].GetProperty("name").GetString();
                        var events = ghost.GetProperty("events").EnumerateArray().ToArray();
                        Assert.Equal(LogicalName, Name(events[0]));
                        Assert.Equal(GenericLogicalName, Name(events[1]));
                        Assert.Equal(log.QPCTimeToRelMSec(Start + 10),
                            events.Single(e => e.GetProperty("type").GetString() == "O" &&
                                Name(e) == LogicalName).GetProperty("at").GetDouble());
                        Assert.DoesNotContain(events, e => e.GetProperty("at").GetDouble() ==
                            log.QPCTimeToRelMSec(Start + 20));
                        Assert.Equal(log.QPCTimeToRelMSec(Start + 30),
                            events.Single(e => e.GetProperty("type").GetString() == "C" &&
                                Name(e) == GenericLogicalName).GetProperty("at").GetDouble());
                        Assert.Contains("[Async V1] AsyncFrame(0xd0) [captured state 0]",
                            frames.EnumerateArray().Select(f => f.GetProperty("name").GetString()));
                        Assert.Contains("[Async V2] testapp!" + MoveNext,
                            frames.EnumerateArray().Select(f => f.GetProperty("name").GetString()));
                        Assert.Equal(log.QPCTimeToRelMSec(log.sessionEndTimeQPC),
                            events.Last().GetProperty("at").GetDouble());
                    }

                    string exact = File.ReadAllText(output);
                    var interval = TimeSpan.FromSeconds(20.25 / log.QPCFreq);
                    SpeedScopeStackSourceWriter.WriteAsyncCallStacksAsJson(log, output, interval, Process);
                    using (var json = JsonDocument.Parse(File.ReadAllText(output)))
                    {
                        ValidateJson(json);
                        var root = json.RootElement;
                        var profiles = root.GetProperty("profiles").EnumerateArray().ToArray();
                        Assert.Equal(2, profiles.Length);
                        Assert.All(profiles, p => Assert.Contains("(virtual 20 ms)", p.GetProperty("name").GetString()));
                        var ghost = profiles.Single(p => p.GetProperty("name").GetString().Contains("Thread (6000)"));
                        var frames = root.GetProperty("shared").GetProperty("frames");
                        string Name(JsonElement e) => frames[e.GetProperty("frame").GetInt32()].GetProperty("name").GetString();
                        var events = ghost.GetProperty("events").EnumerateArray().ToArray();
                        Assert.Equal(new[]
                        {
                            log.QPCTimeToRelMSec(Start + 20), log.QPCTimeToRelMSec(Start + 20),
                            log.QPCTimeToRelMSec(Start + 40), log.QPCTimeToRelMSec(Start + 40),
                            log.QPCTimeToRelMSec(Start + 60), log.QPCTimeToRelMSec(Start + 90)
                        }, events.Select(e => e.GetProperty("at").GetDouble()));
                        Assert.Equal(LogicalName, Name(events[0]));
                        Assert.Equal(GenericLogicalName, Name(events[1]));
                        Assert.Contains("AsyncFrame(0xd0)", Name(events[4]));
                        var normal = profiles.Single(p => p.GetProperty("name").GetString().Contains("Thread (5000)"));
                        Assert.Equal(new[] { log.QPCTimeToRelMSec(Start + 20), log.QPCTimeToRelMSec(Start + 60) },
                            normal.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("at").GetDouble()));
                    }
                    SpeedScopeStackSourceWriter.WriteAsyncCallStacksAsJson(log, output, TimeSpan.FromSeconds(1), Process);
                    using (var json = JsonDocument.Parse(File.ReadAllText(output)))
                    {
                        Assert.Empty(json.RootElement.GetProperty("profiles").EnumerateArray());
                    }
                    foreach (var invalid in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(-1), TimeSpan.FromTicks(1) })
                    {
                        Assert.Throws<ArgumentOutOfRangeException>(() =>
                            SpeedScopeStackSourceWriter.WriteAsyncCallStacksAsJson(log, output, invalid));
                    }
                    SpeedScopeStackSourceWriter.WriteAsyncCallStacksAsJson(log, output, Process);
                    Assert.Equal(exact, File.ReadAllText(output));
                    Assert.True(log.ReleaseAsyncProfilerCallStacks());
                    SpeedScopeStackSourceWriter.WriteAsyncCallStacksAsJson(log, output, OtherProcess);
                    using (var json = JsonDocument.Parse(File.ReadAllText(output)))
                    {
                        Assert.Single(json.RootElement.GetProperty("profiles").EnumerateArray());
                        ValidateJson(json);
                    }
                    SpeedScopeStackSourceWriter.WriteAsyncCallStacksAsJson(log, output, 9999);
                    using (var json = JsonDocument.Parse(File.ReadAllText(output)))
                    {
                        Assert.Empty(json.RootElement.GetProperty("profiles").EnumerateArray());
                    }
                }
            }
            finally
            {
                File.Delete(input);
                File.Delete(output);
                if (etlx != null) { File.Delete(etlx); }
            }
        }

        #region private

        private static void WriteShortUTF8String(BinaryWriter writer, string value)
        {
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(value);
            writer.Write((ushort)utf8.Length);
            writer.Write(utf8);
        }

        private static AsyncProfilerCallStack Context(long start, long end, ulong[] frames, int depth = 0,
            bool runtime = false, AsyncProfilerCallStack.CompletionDelta[] methods = null,
            AsyncProfilerCallStack.CompletionDelta[] exceptions = null) =>
            new AsyncProfilerCallStack(depth, 0,
                new AsyncProfilerCallStackFrames(runtime ? AsyncCallstackKind.RuntimeAsync : AsyncCallstackKind.StateMachineAsync,
                    frames, runtime ? null : new int[frames.Length]),
                0, 32, start, end,
                methods ?? Array.Empty<AsyncProfilerCallStack.CompletionDelta>(),
                exceptions ?? Array.Empty<AsyncProfilerCallStack.CompletionDelta>(), Array.Empty<long>());

        private static AsyncProfilerCallStack.CompletionDelta Delta(long qpc, int count = 1) =>
            new AsyncProfilerCallStack.CompletionDelta(qpc, count);

        private static IReadOnlyList<ProfileEvent> Export(IReadOnlyList<AsyncProfilerCallStack> contexts,
            long start = 0, long end = 100, long interval = 0) =>
            SpeedScopeStackSourceWriter.GetAsyncProfileEvents(contexts, start, end,
                qpc => qpc, (context, slot) => (int)context.Frames.MethodIdAt(slot), interval);

        private static void AssertEvents(IReadOnlyList<ProfileEvent> actual,
            params (ProfileEventType Type, int Frame, double Time, int Depth)[] expected)
        {
            Assert.True(Validate(actual));
            Assert.Equal(expected, actual.Select(e => (e.Type, e.FrameId, e.RelativeTime, e.Depth)));
        }

        private static void ValidateJson(JsonDocument document)
        {
            var frames = document.RootElement.GetProperty("shared").GetProperty("frames");
            int frameCount = frames.GetArrayLength();
            Assert.DoesNotContain(frames.EnumerateArray(),
                frame => frame.GetProperty("name").GetString().Contains("AsyncFrame(0x0)"));
            var profiles = document.RootElement.GetProperty("profiles").EnumerateArray().ToArray();
            Assert.Equal(profiles.Length, profiles.Select(p => p.GetProperty("name").GetString()).Distinct().Count());
            foreach (var profile in profiles)
            {
                double previous = profile.GetProperty("startValue").GetDouble();
                double end = profile.GetProperty("endValue").GetDouble();
                var stack = new Stack<int>();
                foreach (var e in profile.GetProperty("events").EnumerateArray())
                {
                    int frame = e.GetProperty("frame").GetInt32();
                    double time = e.GetProperty("at").GetDouble();
                    Assert.InRange(frame, 0, frameCount - 1);
                    Assert.InRange(time, previous, end);
                    previous = time;
                    if (e.GetProperty("type").GetString() == "O") { stack.Push(frame); }
                    else { Assert.Equal(frame, stack.Pop()); }
                }
                Assert.Empty(stack);
            }
        }

        #endregion
    }
}
