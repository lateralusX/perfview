// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;

using Xunit;

namespace TraceEventTests
{
    /// <summary>
    /// End-to-end validation of the async-profiler TraceLog integration: synthesize an EventPipe (.nettrace)
    /// stream that carries a CPU-sample event plus an <c>AsyncEvents</c> buffer whose (nested) async call
    /// stacks span the sample's QPC, convert it to ETLX (which runs the build hook + serializes the index),
    /// reopen the ETLX, then query <see cref="TraceLog.GetAsyncCallStacks(ThreadIndex, long)"/> using the
    /// sample event's own thread and QPC.
    /// </summary>
    public class AsyncProfilerTraceLogTests
    {
        [Fact]
        public void EndToEnd_AsyncCallStacks_RoundTripThroughTraceLog()
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
            writer.WriteHeaders();
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
            try
            {
                File.WriteAllBytes(nettracePath, writer.ToArray());
                etlxPath = TraceLog.CreateFromEventPipeDataFile(nettracePath);

                using (var traceLog = new TraceLog(etlxPath))
                {
                    // Find the CPU sample event's thread + QPC from the reopened ETLX, and confirm the raw
                    // AsyncEvents event round-tripped as a first-class event.
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

                    Assert.True(asyncEventsCount == 2, "events seen: " + string.Join(", ", seen));
                    Assert.NotEqual(ThreadIndex.Invalid, sampleThreadIndex);
                    Assert.Equal(sampleQpc, sampleEventQpc);

                    // The async call stacks active at the CPU sample's QPC: nested D1 (outer) + D2 (inner).
                    IReadOnlyList<AsyncCallStack> nested = traceLog.GetAsyncCallStacks(sampleThreadIndex, sampleEventQpc);
                    Assert.Equal(2, nested.Count);
                    Assert.Equal(0, nested[0].Depth);
                    Assert.Equal(new ulong[] { 0xA, 0xB }, MethodIds(nested[0].Frames));
                    Assert.Equal(1, nested[1].Depth);
                    Assert.Equal(new ulong[] { 0xC }, MethodIds(nested[1].Frames));

                    // The (processId, osThreadId) overload resolves the same real thread identically.
                    IReadOnlyList<AsyncCallStack> byId = traceLog.GetAsyncCallStacks(ProcessId, (ulong)OsThreadId, sampleEventQpc);
                    Assert.Equal(2, byId.Count);
                    Assert.Equal(new ulong[] { 0xA, 0xB }, MethodIds(byId[0].Frames));
                    Assert.Equal(new ulong[] { 0xC }, MethodIds(byId[1].Frames));

                    // The ghost thread has async data but no TraceThread, so the ThreadIndex-based lookup can't
                    // reach it, yet the (processId, osThreadId) overload can.
                    Assert.Equal(ThreadIndex.Invalid, ThreadIndexForOsThread(traceLog, GhostOsThreadId));
                    IReadOnlyList<AsyncCallStack> ghost = traceLog.GetAsyncCallStacks(ProcessId, (ulong)GhostOsThreadId, StartQpc + 25);
                    Assert.Single(ghost);
                    Assert.Equal(new ulong[] { 0xE, 0xF }, MethodIds(ghost[0].Frames));

                    // Only the outer D1 is active earlier; nothing after both suspend.
                    Assert.Single(traceLog.GetAsyncCallStacks(sampleThreadIndex, StartQpc + 15));
                    Assert.Empty(traceLog.GetAsyncCallStacks(sampleThreadIndex, StartQpc + 45));

                    // An invalid thread yields nothing (both overloads).
                    Assert.Empty(traceLog.GetAsyncCallStacks(ThreadIndex.Invalid, sampleEventQpc));
                    Assert.Empty(traceLog.GetAsyncCallStacks(ProcessId, 999999, sampleEventQpc));
                    Assert.Empty(traceLog.GetAsyncCallStacks(99999, (ulong)OsThreadId, sampleEventQpc));
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

        private static ulong[] MethodIds(AsyncCallStackFrames frames)
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
