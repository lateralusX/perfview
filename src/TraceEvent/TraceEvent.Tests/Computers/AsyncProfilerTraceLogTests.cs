// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;
using Microsoft.Diagnostics.Tracing.Stacks;

using Xunit;

namespace TraceEventTests
{
    /// <summary>
    /// End-to-end validation of the async-profiler TraceLog integration: synthesize an EventPipe (.nettrace)
    /// stream that carries a CPU-sample event plus an <c>AsyncEvents</c> buffer whose (nested) async call
    /// stacks span the sample's QPC, convert it to ETLX (which runs the build hook + serializes the index),
    /// reopen the ETLX, then query <see cref="TraceLog.GetAsyncProfilerCallStacks(ThreadIndex, long)"/> using the
    /// sample event's own thread and QPC.
    /// </summary>
    public class AsyncProfilerTraceLogTests
    {
        [Fact]
        public void SampleProfilerThreadTimeComputer_PreservesTwoArgumentConstructor()
        {
            Assert.NotNull(typeof(SampleProfilerThreadTimeComputer).GetConstructor(new[] { typeof(TraceLog), typeof(SymbolReader) }));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ParseErrors_AreLoggedWithBoundedDetailsAndRecoveredCoverageIsPersisted(bool keepRawEvents)
        {
            const int ProcessId = 1234;
            const int OsThreadId = 5000;
            const long ThreadStreamIndex = 7;
            const long StartQpc = 100_000;
            var buffer = new AsyncProfilerBufferBuilder(OsThreadId, startQpc: StartQpc)
                .Armed(StartQpc).ResumeStack(StartQpc + 10, 1, new ulong[] { 0xA });
            for (int i = 0; i < 12; i++)
            {
                buffer.RawEvent((byte)AsyncEventID.UnwindStateMachineAsyncException,
                    StartQpc + 20 + i, PayloadLengthFieldSize.Byte, Array.Empty<byte>());
            }
            byte[] raw = buffer.Reset(StartQpc + 40)
                .ResumeStack(StartQpc + 41, 2, new ulong[] { 0xB }).Build();
            var payload = new byte[sizeof(int) + raw.Length];
            Array.Copy(BitConverter.GetBytes(raw.Length), payload, sizeof(int));
            Array.Copy(raw, 0, payload, sizeof(int), raw.Length);
            var writer = new EventPipeWriterV6();
            writer.WriteHeaders();
            writer.WriteMetadataBlock(
                new EventMetadata(1, AsyncProfilerTraceEventParser.ProviderName, "AsyncEvents", AsyncProfilerTraceEventParser.AsyncEventsEventId),
                new EventMetadata(2, "TestSampleProfiler", "Sample", 1));
            writer.WriteThreadBlock(w => w.WriteThreadEntry(ThreadStreamIndex, threadId: OsThreadId, processId: ProcessId));
            writer.WriteEventBlock(w =>
            {
                w.WriteEventBlob(new WriteEventOptions
                {
                    MetadataId = 2,
                    ThreadIndexOrId = ThreadStreamIndex,
                    CaptureThreadIndexOrId = ThreadStreamIndex,
                    SequenceNumber = 1,
                    Timestamp = StartQpc + 5,
                    IsSorted = true,
                }, p => { });
                w.WriteEventBlob(new WriteEventOptions
                {
                    MetadataId = 1,
                    ThreadIndexOrId = ThreadStreamIndex,
                    CaptureThreadIndexOrId = ThreadStreamIndex,
                    SequenceNumber = 2,
                    Timestamp = StartQpc + 50,
                    IsSorted = true,
                }, p => p.Write(payload));
            });
            writer.WriteEndBlock();

            string nettracePath = Path.Combine(Path.GetTempPath(), $"asyncprofiler_errors_{Guid.NewGuid():N}.nettrace");
            string etlxPath = null;
            try
            {
                File.WriteAllBytes(nettracePath, writer.ToArray());
                using (var conversionLog = new StringWriter())
                {
                    etlxPath = TraceLog.CreateFromEventPipeDataFile(nettracePath, null,
                        new TraceLogOptions { ConversionLog = conversionLog, KeepAsyncProfilerEvents = keepRawEvents });
                    string diagnostics = conversionLog.ToString();
                    Assert.Contains("AsyncProfiler reported 12 parse errors", diagnostics);
                    Assert.Contains("AsyncProfiler PID 1234, process index", diagnostics);
                    Assert.Contains("thread 5000, QPC 100020", diagnostics);
                    Assert.Contains("Invalid or truncated payload for UnwindStateMachineAsyncException", diagnostics);
                    Assert.Equal(10, diagnostics.Split(new[] { "AsyncProfiler PID" }, StringSplitOptions.None).Length - 1);
                }
                using (var traceLog = new TraceLog(etlxPath))
                {
                    Assert.True(traceLog.HasAsyncProfilerEvents);
                    Assert.False(traceLog.IsAsyncProfilerCallStacksLoaded);
                    Assert.Equal(StartQpc + 20,
                        Assert.Single(traceLog.GetAsyncProfilerCallStacks(ProcessId, (ulong)OsThreadId, StartQpc + 15)).EndQpc);
                    Assert.Empty(traceLog.GetAsyncProfilerCallStacks(ProcessId, (ulong)OsThreadId, StartQpc + 20));
                    Assert.Empty(traceLog.GetAsyncProfilerCallStacks(ProcessId, (ulong)OsThreadId, StartQpc + 35));
                    AsyncProfilerCallStack recovered = Assert.Single(traceLog.GetAsyncProfilerCallStacks(ProcessId, (ulong)OsThreadId, StartQpc + 45));
                    Assert.Equal(0xBUL, recovered.Frames.MethodIdAt(0));
                    Assert.Equal(long.MaxValue, recovered.EndQpc);
                    Assert.True(traceLog.ReleaseAsyncProfilerCallStacks());
                    Assert.Empty(traceLog.GetAsyncProfilerCallStacks(ProcessId, (ulong)OsThreadId, StartQpc + 35));
                }
            }
            finally
            {
                if (File.Exists(nettracePath))
                {
                    File.Delete(nettracePath);
                }
                if (etlxPath != null && File.Exists(etlxPath))
                {
                    File.Delete(etlxPath);
                }
            }
        }

        [Fact]
        public void EndToEnd_CallStacks_RoundTripThroughTraceLog()
        {
            const int ProcessId = 1234;
            const int OsThreadId = 5000;
            const int GhostOsThreadId = 6000;   // emits async data but has NO thread entry / TraceThread
            const long ThreadStreamIndex = 7;
            const long StartQpc = 100_000;

            // Async buffer on OsThreadId: D1 [+10,+40) with D2 [+20,+30) nested inside it.
            byte[] buffer = new AsyncProfilerBufferBuilder(OsThreadId, 0x0BADF00D, StartQpc)
                .Armed(StartQpc)
                .ResumeStack(StartQpc + 10, dispatcher: 1, new ulong[] { 0xA, 0xB }, new[] { 0, 1 })
                .CompleteMethod(StartQpc + 15)
                .CreateContext(
                    AsyncEventID.CreateStateMachineAsyncContext,
                    StartQpc + 18,
                    parent: 1,
                    dispatcher: 2)
                .ResumeStack(StartQpc + 20, dispatcher: 2, new ulong[] { 0xC }, new[] { 2 })
                .Suspend(StartQpc + 30)   // pop D2 -> [+20,+30)
                .Suspend(StartQpc + 40)   // pop D1 -> [+10,+40)
                .Build();

            // AsyncEvents payload = int length + raw buffer bytes (as the runtime writes it via WriteEventCore).
            byte[] asyncPayload = new byte[4 + buffer.Length];
            Array.Copy(BitConverter.GetBytes(buffer.Length), 0, asyncPayload, 0, 4);
            Array.Copy(buffer, 0, asyncPayload, 4, buffer.Length);

            // A second buffer whose header OS thread id is the "ghost" thread (no thread block entry). The index
            // keys by (carrying-event ProcessID, buffer-header OsThreadId), so this is reachable only by OS thread id.
            byte[] ghostBuffer = new AsyncProfilerBufferBuilder(GhostOsThreadId, 0x0BADBEEF, StartQpc)
                .Armed(StartQpc)
                .ResumeStack(StartQpc + 10, dispatcher: 1, new ulong[] { 0xE, 0xF }, new[] { 0, 1 })
                .Suspend(StartQpc + 40)   // [+10,+40)
                .Build();
            byte[] ghostPayload = new byte[4 + ghostBuffer.Length];
            Array.Copy(BitConverter.GetBytes(ghostBuffer.Length), 0, ghostPayload, 0, 4);
            Array.Copy(ghostBuffer, 0, ghostPayload, 4, ghostBuffer.Length);

            long sampleQpc = StartQpc + 25;   // a CPU sample landing inside both nested async ranges

            var writer = new EventPipeWriterV6();
            writer.WriteHeaders(null, syncTimeQpc: StartQpc);
            writer.WriteMetadataBlock(
                new EventMetadata(1, AsyncProfilerTraceEventParser.ProviderName, "AsyncEvents", AsyncProfilerTraceEventParser.AsyncEventsEventId),
                new EventMetadata(2, "TestSampleProfiler", "Sample", 1));
            writer.WriteThreadBlock(w => w.WriteThreadEntry(ThreadStreamIndex, threadId: OsThreadId, processId: ProcessId));
            writer.WriteEventBlock(w =>
            {
                // CPU sample on the thread, inside the async call stack ranges.
                w.WriteEventBlob(new WriteEventOptions
                {
                    MetadataId = 2,
                    ThreadIndexOrId = ThreadStreamIndex,
                    CaptureThreadIndexOrId = ThreadStreamIndex,
                    SequenceNumber = 1,
                    Timestamp = sampleQpc,
                    IsSorted = true,
                }, p => { });

                // The AsyncEvents buffer, flushed just after (its internal timestamps precede this event's timestamp).
                w.WriteEventBlob(new WriteEventOptions
                {
                    MetadataId = 1,
                    ThreadIndexOrId = ThreadStreamIndex,
                    CaptureThreadIndexOrId = ThreadStreamIndex,
                    SequenceNumber = 2,
                    Timestamp = StartQpc + 50,
                    IsSorted = true,
                }, p => p.Write(asyncPayload));

                // A second AsyncEvents buffer whose data belongs to the ghost OS thread (no thread block entry).
                w.WriteEventBlob(new WriteEventOptions
                {
                    MetadataId = 1,
                    ThreadIndexOrId = ThreadStreamIndex,
                    CaptureThreadIndexOrId = ThreadStreamIndex,
                    SequenceNumber = 3,
                    Timestamp = StartQpc + 60,
                    IsSorted = true,
                }, p => p.Write(ghostPayload));
            });
            writer.WriteEndBlock();

            string nettracePath = Path.Combine(Path.GetTempPath(), $"asyncprofiler_{Guid.NewGuid():N}.nettrace");
            string etlxPath = null;
            string preservedEtlxPath = null;
            try
            {
                File.WriteAllBytes(nettracePath, writer.ToArray());
                etlxPath = TraceLog.CreateFromEventPipeDataFile(nettracePath);

                using (var traceLog = new TraceLog(etlxPath))
                {
                    Assert.True(traceLog.HasAsyncProfilerEvents);
                    Assert.False(traceLog.IsAsyncProfilerCallStacksLoaded);

                    using (var symbols = new SymbolReader(TextWriter.Null))
                    {
                        foreach (bool enableAsyncCallStacks in new[] { false, true })
                        {
                            var sampleComputer = new SampleProfilerThreadTimeComputer(traceLog, symbols)
                            {
                                EnableAsyncCallStacks = enableAsyncCallStacks,
                                IncludeEventSourceEvents = false,
                            };
                            Assert.False(sampleComputer.AsyncCallStacksActive);
                            sampleComputer.GenerateThreadTimeStacks(new MutableTraceEventStackSource(traceLog));
                            Assert.Equal(enableAsyncCallStacks, sampleComputer.AsyncCallStacksActive);
                            Assert.Equal(enableAsyncCallStacks, traceLog.IsAsyncProfilerCallStacksLoaded);
                            if (enableAsyncCallStacks)
                            {
                                Assert.True(traceLog.ReleaseAsyncProfilerCallStacks());
                            }

                            var threadTimeComputer = new ThreadTimeStackComputer(traceLog, symbols)
                            {
                                EnableAsyncCallStacks = enableAsyncCallStacks,
                                IncludeEventSourceEvents = false,
                            };
                            Assert.False(threadTimeComputer.AsyncCallStacksActive);
                            threadTimeComputer.GenerateThreadTimeStacks(new MutableTraceEventStackSource(traceLog));
                            Assert.Equal(enableAsyncCallStacks, threadTimeComputer.AsyncCallStacksActive);
                            Assert.Equal(enableAsyncCallStacks, traceLog.IsAsyncProfilerCallStacksLoaded);
                            if (enableAsyncCallStacks)
                            {
                                Assert.True(traceLog.ReleaseAsyncProfilerCallStacks());
                            }
                        }
                    }

                    // Find the CPU sample event's thread + QPC from the reopened ETLX. The raw AsyncEvents
                    // records are omitted by default because the persisted index supersedes them.
                    int asyncEventsCount = 0;
                    ThreadIndex sampleThreadIndex = ThreadIndex.Invalid;
                    long sampleEventQpc = 0;
                    var seen = new List<string>();
#pragma warning disable CS0618 // TimeStampQPC is discouraged, but we want exact QPC (no lossy msec conversion).
                    foreach (TraceEvent e in traceLog.Events)
                    {
                        seen.Add(e.ProviderName + "/" + e.EventName + "#" + (int)e.ID);
                        if (e.ProviderGuid == AsyncProfilerTraceEventParser.ProviderGuid &&
                            e.ID == (TraceEventID)AsyncProfilerTraceEventParser.AsyncEventsEventId)
                        {
                            asyncEventsCount++;
                        }
                        else if (e.ProviderName == "TestSampleProfiler")
                        {
                            sampleThreadIndex = e.Thread()?.ThreadIndex ?? ThreadIndex.Invalid;
                            sampleEventQpc = e.TimeStampQPC;
                        }
                    }
#pragma warning restore CS0618

                    Assert.True(asyncEventsCount == 0, "events seen: " + string.Join(", ", seen));
                    Assert.NotEqual(ThreadIndex.Invalid, sampleThreadIndex);
                    Assert.Equal(sampleQpc, sampleEventQpc);

                    // The async call stacks active at the CPU sample's QPC: nested D1 (outer) + D2 (inner).
                    IReadOnlyList<AsyncProfilerCallStack> nested = traceLog.GetAsyncProfilerCallStacks(sampleThreadIndex, sampleEventQpc);
                    Assert.Equal(2, nested.Count);
                    Assert.Equal(0, nested[0].Depth);
                    Assert.Equal(1UL, nested[0].DispatcherId);
                    Assert.Equal(new ulong[] { 0xA, 0xB }, MethodIds(nested[0].Frames));
                    Assert.Equal(1, nested[1].Depth);
                    Assert.Equal(2UL, nested[1].DispatcherId);
                    Assert.Equal(new ulong[] { 0xC }, MethodIds(nested[1].Frames));

                    Assert.Same(
                        nested[1],
                        traceLog.GetAsyncProfilerCallStack(sampleThreadIndex, dispatcherId: 2, timeQPC: sampleEventQpc));
                    Assert.Same(
                        nested[1],
                        traceLog.GetAsyncProfilerCallStack(
                            ProcessId, (ulong)OsThreadId, dispatcherId: 2, timeQPC: sampleEventQpc));

                    AsyncProfilerDispatcherInfo dispatcher = traceLog.GetAsyncProfilerDispatcherInfo(nested[1]);
                    Assert.NotNull(dispatcher);
                    Assert.Equal(2UL, dispatcher.DispatcherId);
                    Assert.Equal(1UL, dispatcher.ParentDispatcherId);
                    Assert.Equal(StartQpc + 18, dispatcher.CreationQpc);

                    Assert.True(traceLog.TryGetParentAsyncProfilerCallStack(
                        nested[1],
                        out AsyncProfilerCallStack parent,
                        out long parentQpc));
                    Assert.Same(nested[0], parent);
                    Assert.Equal(StartQpc + 18, parentQpc);
                    Assert.True(traceLog.AsyncProfilerMethodCompletionObserved(parent));
                    Assert.Equal(1, parent.GetCompletedFrameCount(parentQpc));
                    Assert.Same(parent, traceLog.GetParentAsyncProfilerCallStack(nested[1]));
                    AsyncProfilerDispatcherInfo childDispatcher =
                        Assert.Single(traceLog.GetChildAsyncProfilerDispatcherInfos(parent));
                    Assert.Equal(dispatcher.CreationThread, childDispatcher.CreationThread);
                    Assert.Equal(dispatcher.DispatcherId, childDispatcher.DispatcherId);
                    Assert.Equal(dispatcher.ParentDispatcherId, childDispatcher.ParentDispatcherId);
                    Assert.Equal(dispatcher.CreationQpc, childDispatcher.CreationQpc);

                    // Releasing the materialized index preserves already-returned immutable objects and makes the
                    // next public query transparently deserialize a fresh index from the deferred ETLX region.
                    AsyncProfilerCallStack retainedOuter = nested[0];
                    Assert.True(traceLog.IsAsyncProfilerCallStacksLoaded);
                    Assert.True(traceLog.ReleaseAsyncProfilerCallStacks());
                    Assert.False(traceLog.IsAsyncProfilerCallStacksLoaded);
                    Assert.Equal(new ulong[] { 0xA, 0xB }, MethodIds(retainedOuter.Frames));

                    IReadOnlyList<AsyncProfilerCallStack> reloaded = traceLog.GetAsyncProfilerCallStacks(sampleThreadIndex, sampleEventQpc);
                    Assert.Equal(2, reloaded.Count);
                    Assert.NotSame(retainedOuter, reloaded[0]);
                    Assert.True(traceLog.IsAsyncProfilerCallStacksLoaded);

                    // Release and reload are synchronized. A query that obtained the old index can finish while
                    // another caller releases it, and later callers load a complete replacement.
                    Parallel.For(0, 64, i =>
                    {
                        if ((i & 3) == 0)
                        {
                            traceLog.ReleaseAsyncProfilerCallStacks();
                        }
                        else
                        {
                            Assert.Equal(
                                2,
                                traceLog.GetAsyncProfilerCallStacks(sampleThreadIndex, sampleEventQpc).Count);
                        }
                    });
                    Assert.Equal(2, traceLog.GetAsyncProfilerCallStacks(sampleThreadIndex, sampleEventQpc).Count);

                    // The (processId, osThreadId) overload resolves the same real thread identically.
                    IReadOnlyList<AsyncProfilerCallStack> byId = traceLog.GetAsyncProfilerCallStacks(ProcessId, (ulong)OsThreadId, sampleEventQpc);
                    Assert.Equal(2, byId.Count);
                    Assert.Equal(new ulong[] { 0xA, 0xB }, MethodIds(byId[0].Frames));
                    Assert.Equal(new ulong[] { 0xC }, MethodIds(byId[1].Frames));

                    // The ghost thread has async data but no TraceThread, so the ThreadIndex-based lookup can't
                    // reach it, yet the (processId, osThreadId) overload can.
                    Assert.Equal(ThreadIndex.Invalid, ThreadIndexForOsThread(traceLog, GhostOsThreadId));
                    IReadOnlyList<AsyncProfilerCallStack> ghost = traceLog.GetAsyncProfilerCallStacks(ProcessId, (ulong)GhostOsThreadId, StartQpc + 25);
                    Assert.Single(ghost);
                    Assert.Equal(new ulong[] { 0xE, 0xF }, MethodIds(ghost[0].Frames));

                    // Only the outer D1 is active earlier; nothing after both suspend.
                    Assert.Single(traceLog.GetAsyncProfilerCallStacks(sampleThreadIndex, StartQpc + 15));
                    Assert.Empty(traceLog.GetAsyncProfilerCallStacks(sampleThreadIndex, StartQpc + 45));

                    // An invalid thread yields nothing (both overloads).
                    Assert.Empty(traceLog.GetAsyncProfilerCallStacks(ThreadIndex.Invalid, sampleEventQpc));
                    Assert.Empty(traceLog.GetAsyncProfilerCallStacks(ProcessId, 999999, sampleEventQpc));
                    Assert.Empty(traceLog.GetAsyncProfilerCallStacks(99999, (ulong)OsThreadId, sampleEventQpc));
                }

                preservedEtlxPath = nettracePath + ".preserved.etlx";
                TraceLog.CreateFromEventPipeDataFile(
                    nettracePath,
                    preservedEtlxPath,
                    new TraceLogOptions { KeepAsyncProfilerEvents = true });
                using (var traceLog = new TraceLog(preservedEtlxPath))
                {
                    Assert.True(traceLog.HasAsyncProfilerEvents);
                    Assert.False(traceLog.IsAsyncProfilerCallStacksLoaded);

                    int asyncEventsCount = 0;
                    foreach (TraceEvent e in traceLog.Events)
                    {
                        if (e.ProviderGuid == AsyncProfilerTraceEventParser.ProviderGuid &&
                            e.ID == (TraceEventID)AsyncProfilerTraceEventParser.AsyncEventsEventId)
                        {
                            asyncEventsCount++;
                        }
                    }

                    Assert.Equal(2, asyncEventsCount);
                    Assert.Equal(
                        2,
                        traceLog.GetAsyncProfilerCallStacks(ProcessId, (ulong)OsThreadId, sampleQpc).Count);
                }
            }
            finally
            {
                if (File.Exists(nettracePath))
                {
                    File.Delete(nettracePath);
                }
                if (etlxPath != null && File.Exists(etlxPath))
                {
                    File.Delete(etlxPath);
                }
                if (preservedEtlxPath != null && File.Exists(preservedEtlxPath))
                {
                    File.Delete(preservedEtlxPath);
                }
            }
        }

        private static ThreadIndex ThreadIndexForOsThread(TraceLog traceLog, int osThreadId)
        {
            foreach (TraceThread t in traceLog.Threads)
            {
                if (t.ThreadID == osThreadId)
                {
                    return t.ThreadIndex;
                }
            }
            return ThreadIndex.Invalid;
        }

        private static ulong[] MethodIds(AsyncProfilerCallStackFrames frames)
        {
            var ids = new ulong[frames.FrameCount];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = frames.MethodIdAt(i);
            }
            return ids;
        }
    }
}
