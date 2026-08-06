// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;
using Microsoft.Diagnostics.Tracing.Stacks;
using Microsoft.Diagnostics.Symbols;

using Xunit;
using Xunit.Abstractions;

namespace TraceEventTests
{
    /// <summary>
    /// Local-only diagnostic harness (not a CI test): points at a real .nettrace that contains async-profiler
    /// data plus CPU samples and quantifies, sample-by-sample, whether each CPU sample matches an async call
    /// stack segment. It buckets misses into "thread not in index" (thread-key mismatch) vs "on an async thread
    /// but no covering QPC window" (QPC-window mismatch), which is exactly the axis that decides why a running
    /// continuation sample produces no spliced stack. Skips cleanly when the trace file is absent, so it never
    /// affects CI. Set ASYNC_STITCH_NETTRACE to override the default path.
    /// </summary>
    public class AsyncStitchRealTraceDiagnostics
    {
        private readonly ITestOutputHelper _output;

        public AsyncStitchRealTraceDiagnostics(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Diagnose_RealTrace_SampleToSegmentMatching()
        {
            string path = Environment.GetEnvironmentVariable("ASYNC_STITCH_NETTRACE") ?? @"C:\traces\async.nettrace";
            if (!File.Exists(path))
            {
                return; // diagnostic only; nothing to do without a local trace
            }

            var sb = new StringBuilder();
            string etlx = null;
            try
            {
                etlx = TraceLog.CreateFromEventPipeDataFile(path);
                using (var traceLog = new TraceLog(etlx))
                {
                    AsyncCallStacksIndex index = traceLog.AsyncCallStacks;
                    sb.AppendLine($"trace={path}");
                    sb.AppendLine($"AsyncCallStacks present? {index != null}");
                    if (index == null)
                    {
                        WriteOut(sb);
                        Assert.True(true);
                        return;
                    }

                    // ---- Index summary: thread keys + per-thread segment windows. ----
                    var keySet = new HashSet<(int Pid, ulong Tid)>();
                    long idxMinStart = long.MaxValue, idxMaxEndClosed = long.MinValue;
                    int totalSegments = 0, openSegments = 0;
                    foreach (AsyncThreadKey key in index.Threads)
                    {
                        keySet.Add((key.ProcessId, key.OsThreadId));
                        IReadOnlyList<AsyncCallStack> segs = index.GetAsyncCallStacks(key);
                        totalSegments += segs.Count;
                        foreach (AsyncCallStack s in segs)
                        {
                            if (s.StartQpc < idxMinStart) idxMinStart = s.StartQpc;
                            if (s.EndQpc == long.MaxValue) openSegments++;
                            else if (s.EndQpc > idxMaxEndClosed) idxMaxEndClosed = s.EndQpc;
                        }
                    }

                    sb.AppendLine($"index: threads={keySet.Count} segments={totalSegments} open={openSegments} " +
                                  $"startQpc.min={idxMinStart} endQpc.maxClosed={idxMaxEndClosed}");
                    foreach (AsyncThreadKey key in index.Threads)
                    {
                        IReadOnlyList<AsyncCallStack> segs = index.GetAsyncCallStacks(key);
                        long mn = segs.Count > 0 ? segs.Min(s => s.StartQpc) : 0;
                        long mx = segs.Count > 0 ? segs.Max(s => s.EndQpc == long.MaxValue ? s.StartQpc : s.EndQpc) : 0;
                        int open = segs.Count(s => s.EndQpc == long.MaxValue);
                        sb.AppendLine($"  pid={key.ProcessId} tid={key.OsThreadId} segs={segs.Count} open={open} qpc[{mn}..{mx}]");
                    }

                    // ---- Iterate CPU samples exactly like the computer (SampleProfilerTraceEventParser.ThreadSample). ----
                    TraceLogEventSource source = traceLog.Events.GetSource();
                    var sampleParser = new SampleProfilerTraceEventParser(source);

                    var boundaries = new AsyncStitchBoundaryCache(traceLog.CodeAddresses);
                    Func<CodeAddressIndex, MethodIndex> methodOf = ca => traceLog.CodeAddresses.MethodIndex(ca);
                    TraceCallStacks callStacks = traceLog.CallStacks;
                    TraceCodeAddresses codeAddresses = traceLog.CodeAddresses;

                    // Per-thread bounds (min start, max closed end, has-open) to classify misses.
                    var tMinStart = new Dictionary<(int, ulong), long>();
                    var tMaxClosedEnd = new Dictionary<(int, ulong), long>();
                    var tHasOpen = new Dictionary<(int, ulong), bool>();
                    foreach (AsyncThreadKey key in index.Threads)
                    {
                        var k = (key.ProcessId, key.OsThreadId);
                        IReadOnlyList<AsyncCallStack> segs2 = index.GetAsyncCallStacks(key);
                        tMinStart[k] = segs2.Count > 0 ? segs2.Min(s => s.StartQpc) : 0;
                        tMaxClosedEnd[k] = segs2.Where(s => s.EndQpc != long.MaxValue).Select(s => s.EndQpc).DefaultIfEmpty(long.MinValue).Max();
                        tHasOpen[k] = segs2.Any(s => s.EndQpc == long.MaxValue);
                    }

                    int totalSamples = 0, onAsyncThread = 0, matched = 0, unmatchedOnAsyncThread = 0;
                    int missBeforeFirst = 0, missInGap = 0, missAfterLast = 0;
                    int gapActiveContinuation = 0, gapParkedOrDispatch = 0;
                    int stitchWithAncestry = 0, stitchNoAncestry = 0;
                    const long Reset112416 = 17074500567992L;
                    const long LastAsyncClosedEnd = 17074505956348L; // idxMaxEndClosed
                    int matchedBeforeReset = 0, matchedAfterReset = 0;
                    int preResetSamplesAll = 0, preResetSamplesAsyncThread = 0, preResetActiveCont = 0;
                    int dropCandidates = 0, dropPreOurReset = 0, dropAtOrAfterReset = 0, dropBeforeFirst = 0, dropInGap = 0, dropAfterLast = 0;
                    int matchedViaOpenSegTail = 0; // matched but qpc beyond all closed async data (open-segment-only)
                    int matchedTailActive = 0, matchedTailParked = 0;
                    var tailParkedExamples = new List<string>();
                    var matchedByTid = new Dictionary<ulong, int>();
                    long smpMinQpc = long.MaxValue, smpMaxQpc = long.MinValue;
                    var sampleTids = new HashSet<(int, ulong)>();
                    var stitchExamples = new List<string>();
                    var gapExamples = new List<string>();

                    sampleParser.ThreadSample += delegate (ClrThreadSampleTraceData data)
                    {
                        totalSamples++;
#pragma warning disable CS0618 // exact QPC needed to align with the async index (no lossy msec conversion)
                        long qpc = data.TimeStampQPC;
#pragma warning restore CS0618
                        if (qpc < smpMinQpc) smpMinQpc = qpc;
                        if (qpc > smpMaxQpc) smpMaxQpc = qpc;

                        var tkey = (data.ProcessID, (ulong)data.ThreadID);
                        sampleTids.Add(tkey);
                        bool threadInIndex = keySet.Contains(tkey);
                        if (threadInIndex) onAsyncThread++;

                        if (qpc < Reset112416)
                        {
                            preResetSamplesAll++;
                            if (threadInIndex)
                            {
                                preResetSamplesAsyncThread++;
                                if (SyncStackHasActiveContinuation(data)) preResetActiveCont++;
                            }
                        }

                        IReadOnlyList<AsyncCallStack> segs = traceLog.GetAsyncCallStacks(data);
                        if (segs != null && segs.Count > 0)
                        {
                            matched++;
                            if (qpc < Reset112416) matchedBeforeReset++; else matchedAfterReset++;
                            if (qpc > LastAsyncClosedEnd)
                            {
                                matchedViaOpenSegTail++;
                                if (SyncStackHasActiveContinuation(data)) matchedTailActive++;
                                else
                                {
                                    matchedTailParked++;
                                    if (tailParkedExamples.Count < 8)
                                        tailParkedExamples.Add($"tailMatchedParked tid={data.ThreadID} qpc={qpc} syncLeaf: " + SyncLeaf(data, 5));
                                }
                            }
                            matchedByTid.TryGetValue((ulong)data.ThreadID, out int mc);
                            matchedByTid[(ulong)data.ThreadID] = mc + 1;

                            // Run the REAL stitcher and measure how many async-ancestry frames it actually splices.
                            List<StitchSyncFrame> sync = MaterializeSync(data.CallStackIndex(), callStacks, codeAddresses);
                            StitchResult result = AsyncCpuStackStitcher.Stitch(sync, segs, qpc, boundaries, index, methodOf, false);
                            int asyncFrames = result.Frames.Count(f => f.Origin == StitchedFrameOrigin.AsyncRemaining);
                            if (asyncFrames > 0) stitchWithAncestry++;
                            else stitchNoAncestry++;

                            if (asyncFrames == 0 && stitchExamples.Count < 15)
                            {
                                AsyncCallStack leaf = segs[segs.Count - 1]; // depth-ascending -> last is innermost
                                bool compObs = index.MethodCompletionObserved(leaf.Frames.Kind);
                                int completedByEvents = leaf.GetCompletedFrameCount(qpc);
                                int wrapReset = leaf.GetWrapperResetCount(qpc);
                                var g = new StringBuilder();
                                g.AppendLine($"noAncestry tid={data.ThreadID} qpc={qpc} segs={segs.Count} kind={leaf.Frames.Kind} " +
                                             $"frameCount={leaf.Frames.FrameCount} depth={leaf.Depth}");
                                g.AppendLine($"    completionObserved={compObs} completedByEvents={completedByEvents} " +
                                             $"wrapperResetCount={wrapReset} wrapperCount={leaf.WrapperCount} continuationIndexBase={leaf.ContinuationIndexBase}");
                                g.AppendLine("    syncLeaf: " + SyncLeaf(data, 8));
                                g.AppendLine("    segFrames: " + FramesOf(traceLog, leaf, 8));
                                stitchExamples.Add(g.ToString());
                            }
                        }
                        else if (threadInIndex)
                        {
                            unmatchedOnAsyncThread++;
                            bool hasBoundary = SyncStackHasBoundaryFrame(data.CallStackIndex(), callStacks, boundaries);
                            if (hasBoundary)
                            {
                                dropCandidates++;
                                if (qpc < Reset112416) dropPreOurReset++; else dropAtOrAfterReset++;
                            }
                            long minStart = tMinStart[tkey], maxEnd = tMaxClosedEnd[tkey];
                            if (qpc < minStart) { missBeforeFirst++; if (hasBoundary) dropBeforeFirst++; }
                            else if (qpc > maxEnd && !tHasOpen[tkey]) { missAfterLast++; if (hasBoundary) dropAfterLast++; }
                            else
                            {
                                missInGap++;
                                if (hasBoundary) dropInGap++;
                                bool activeContinuation = SyncStackHasActiveContinuation(data);
                                if (activeContinuation) gapActiveContinuation++;
                                else gapParkedOrDispatch++;
                                if (activeContinuation && gapExamples.Count < 20)
                                {
                                    gapExamples.Add($"inGapActive tid={data.ThreadID} qpc={qpc} syncLeaf: " + SyncLeaf(data, 6));
                                }
                            }
                        }
                    };

                    source.Process();

                    sb.AppendLine($"samples: total={totalSamples} onAsyncThread={onAsyncThread} matched={matched} " +
                                  $"unmatchedOnAsyncThread={unmatchedOnAsyncThread} distinctSampleThreads={sampleTids.Count}");
                    sb.AppendLine($"matched stitch: withAncestry={stitchWithAncestry} noAncestry={stitchNoAncestry}");
                    sb.AppendLine($"matched split @tid112416-reset(500567992): before={matchedBeforeReset} after={matchedAfterReset}");
                    sb.AppendLine($"matched via open-seg tail (qpc>lastClosed 505956348): {matchedViaOpenSegTail}");
                    sb.AppendLine($"  matched-tail detail: active={matchedTailActive} parked={matchedTailParked}");
                    foreach (var ex in tailParkedExamples) sb.AppendLine("  " + ex);
                    sb.AppendLine("matched by tid: " + string.Join(" ", matchedByTid.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));
                    sb.AppendLine($"unmatched breakdown: beforeFirst={missBeforeFirst} inGap={missInGap} afterLast={missAfterLast}");
                    sb.AppendLine($"  inGap detail: activeContinuation={gapActiveContinuation} parkedOrDispatch={gapParkedOrDispatch}");
                    sb.AppendLine($"DROP candidates (no-seg + sync has boundary frame): total={dropCandidates} preOurReset={dropPreOurReset} atOrAfterReset={dropAtOrAfterReset} | byRegion beforeFirst={dropBeforeFirst} inGap={dropInGap} afterLast={dropAfterLast}");
                    sb.AppendLine($"sample qpc[{smpMinQpc}..{smpMaxQpc}]");
                    sb.AppendLine($"async-only prefix: firstAsyncQpc={idxMinStart} firstSampleQpc={smpMinQpc} gapTicks={smpMinQpc - idxMinStart}");
                    sb.AppendLine($"pre-our-reset({Reset112416}) samples: all={preResetSamplesAll} onAsyncThread={preResetSamplesAsyncThread} activeContinuation={preResetActiveCont}");

                    // Overlap between sample threads and index threads.
                    int overlap = sampleTids.Count(t => keySet.Contains(t));
                    sb.AppendLine($"sampleThreads∩indexThreads={overlap} indexThreadsNotSampled={keySet.Count(k => !sampleTids.Contains(k))}");

                    foreach (string e in stitchExamples)
                    {
                        sb.Append(e);
                    }
                    sb.AppendLine("---- inGap sync leaves ----");
                    foreach (string e in gapExamples)
                    {
                        sb.AppendLine(e);
                    }

                    // Full segment dump for tid 112416 (the thread with actively-running-continuation gaps).
                    sb.AppendLine("---- tid=112416 all segments ----");
                    foreach (AsyncThreadKey key in index.Threads)
                    {
                        if (key.OsThreadId != 112416) continue;
                        foreach (AsyncCallStack s in index.GetAsyncCallStacks(key).OrderBy(s => s.StartQpc))
                        {
                            sb.AppendLine($"  [{s.StartQpc}..{s.EndQpc}] depth={s.Depth} kind={s.Frames.Kind} fc={s.Frames.FrameCount} " +
                                          $"cib={s.ContinuationIndexBase} wc={s.WrapperCount} frames: {FramesOf(traceLog, s, 6)}");
                        }
                    }

                    // ---- Raw sub-event dump for tid 112416 up through the anomaly window. ----
                    // A second decode pass over the raw AsyncEvents buffers, dispatching each sub-event to a
                    // dumping sink filtered to tid 112416, to see resume/suspend/complete/reset/metadata ordering
                    // around the missing episode [500314742..500568009].
                    sb.AppendLine("---- tid=112416 raw sub-events (qpc <= 17074500700000) ----");
                    var dump = new DumpSink(112416, 17074500700000L);
                    var control = new ControlEventSink(); // all-thread Metadata/Reset/SyncClock, whole trace
                    var startWin = new GlobalStartSink(17074496200000L); // every sub-event, any kind, in first ~12ms
                    var delivery = new DeliveryOrderSink(); // true delivery order across the whole trace
                    var manifest = new AsyncProfilerManifest();
                    TraceLogEventSource rawSource = traceLog.Events.GetSource();
                    var asyncParser = new AsyncProfilerTraceEventParser(rawSource);
                    asyncParser.AsyncEvents += raw =>
                    {
                        try
                        {
                            delivery.NextBuffer();
                            AsyncProfilerTraceEventParser.ParseBuffer(raw.Buffer, manifest, dump);
                            AsyncProfilerTraceEventParser.ParseBuffer(raw.Buffer, manifest, control);
                            AsyncProfilerTraceEventParser.ParseBuffer(raw.Buffer, manifest, startWin);
                            AsyncProfilerTraceEventParser.ParseBuffer(raw.Buffer, manifest, delivery);
                        }
                        catch { /* best effort */ }
                    };
                    rawSource.Process();
                    foreach (string line in dump.Lines)
                    {
                        sb.AppendLine(line);
                    }

                    // ---- Delivery-order (NOT QPC-order) analysis of the metadata gate. ----
                    sb.AppendLine("---- DELIVERY ORDER: metadata-gate analysis ----");
                    sb.AppendLine($"  totalSubEvents={delivery.Total} firstMetadata: seq={delivery.FirstMetadataSeq} buffer={delivery.FirstMetadataBuffer} qpc={delivery.FirstMetadataQpc}");
                    sb.AppendLine($"  subEventsDeliveredBeforeFirstMetadata={delivery.SubEventsBeforeMetadata}");
                    foreach (string line in delivery.BeforeMetadata)
                    {
                        sb.AppendLine("    pre-meta " + line);
                    }
                    sb.AppendLine("  first delivery per thread (seq, qpc):");
                    foreach (var kv in delivery.FirstPerThread.OrderBy(k => k.Value.Seq))
                    {
                        sb.AppendLine($"    tid={kv.Key} firstSeq={kv.Value.Seq} firstQpc={kv.Value.Qpc}");
                    }
                    sb.AppendLine("  post-metadata delivery carrying pre-metadata QPCs (leftover not excluded by gate):");
                    foreach (var kv in delivery.PostMetaCountQpcBeforeMeta.OrderByDescending(k => k.Value))
                    {
                        long mn = delivery.PostMetaMinQpc.TryGetValue(kv.Key, out long a) ? a : 0;
                        long mx = delivery.PostMetaMaxQpc.TryGetValue(kv.Key, out long b) ? b : 0;
                        sb.AppendLine($"    tid={kv.Key} countQpc<meta={kv.Value} postMetaQpc[{mn}..{mx}]");
                    }

                    // ---- Global control-event timeline (Metadata / Reset / WrapperReset / SyncClock, all threads). ----
                    sb.AppendLine($"---- global control events (async qpc[{idxMinStart}..], sample qpc[{smpMinQpc}..{smpMaxQpc}]) ----");
                    sb.AppendLine($"  counts: metadata={control.MetadataCount} resetThreadCtx={control.ResetThreadCount} " +
                                  $"wrapperReset={control.WrapperResetCount} syncClock={control.SyncClockCount} qpcFreq={control.QpcFrequency}");
                    if (control.QpcFrequency > 0)
                    {
                        double asyncSpanMs = (idxMaxEndClosed - idxMinStart) * 1000.0 / control.QpcFrequency;
                        double sampleSpanMs = (smpMaxQpc - smpMinQpc) * 1000.0 / control.QpcFrequency;
                        double tailMs = (smpMaxQpc - idxMaxEndClosed) * 1000.0 / control.QpcFrequency;
                        sb.AppendLine($"  spans(ms): asyncData={asyncSpanMs:F0} sampleWindow={sampleSpanMs:F0} tailAfterAsync={tailMs:F0}");
                    }
                    foreach (string line in control.Lines.OrderBy(l => l.Qpc).Select(l => l.Text))
                    {
                        sb.AppendLine(line);
                    }

                    // ---- Global first async sub-events (any kind, any thread), to see whether an initial
                    // full metadata precedes the first arming ResetThreadContext. ----
                    sb.AppendLine($"---- global first sub-events (qpc <= {startWin.WindowMax}); globalMinQpc={startWin.GlobalMinQpc} firstMetadataQpc={startWin.FirstMetadataQpc} ----");
                    foreach (string line in startWin.Lines.OrderBy(l => l.Qpc).Take(40).Select(l => l.Text))
                    {
                        sb.AppendLine(line);
                    }
                }
            }
            finally
            {
                if (etlx != null && File.Exists(etlx))
                {
                    File.Delete(etlx);
                }
            }

            WriteOut(sb);
            Assert.True(true);
        }

        /// <summary>
        /// Local-only: reproduces exactly what <c>dotnet-stack report --async</c> would print for the captured
        /// trace, by driving the same pipeline (MutableTraceEventStackSource + SampleProfilerThreadTimeComputer +
        /// GenerateThreadTimeStacks) and replicating dotnet-stack's ForEach/PrintStack emission (first sample per
        /// thread, leaf-first, UNMANAGED_CODE_TIME -> [Native Frames]). Emits both the plain and the --async
        /// (stitched) report so the effect of async stitching is visible. Writes to C:\traces\async-dotnet-stack.txt.
        /// </summary>
        [Fact]
        public void Emulate_DotnetStack_Report()
        {
            string path = Environment.GetEnvironmentVariable("ASYNC_STITCH_NETTRACE") ?? @"C:\traces\async.nettrace";
            if (!File.Exists(path))
            {
                return;
            }

            var sb = new StringBuilder();
            string etlx = null;
            try
            {
                etlx = TraceLog.CreateFromEventPipeDataFile(path);
                using (var symbolReader = new SymbolReader(TextWriter.Null))
                using (var traceLog = new TraceLog(etlx))
                {
                    sb.AppendLine("================ dotnet-stack report (no --async) ================");
                    EmulateReport(traceLog, symbolReader, stitchAsync: false, sb);
                    sb.AppendLine();
                    sb.AppendLine("================ dotnet-stack report --async ================");
                    EmulateReport(traceLog, symbolReader, stitchAsync: true, sb);
                    sb.AppendLine();
                    sb.AppendLine("================ SAME sample, before vs after stitching ================");
                    DumpSameSampleBeforeAfter(traceLog, symbolReader, "Level4Async", sb);
                }
            }
            finally
            {
                if (etlx != null && File.Exists(etlx))
                {
                    File.Delete(etlx);
                }
            }

            try { File.WriteAllText(@"C:\traces\async-dotnet-stack.txt", sb.ToString()); } catch { }
            _output.WriteLine(sb.ToString());
            Assert.True(true);
        }

        // Mirrors dotnet-stack's ReportCommand: build the thread-time stacks, then for each thread print the
        // frames of its FIRST sample (leaf-first, up to the synthetic "Thread (" frame).
        private static void EmulateReport(TraceLog traceLog, SymbolReader symbolReader, bool stitchAsync, StringBuilder sb)
        {
            var stackSource = new MutableTraceEventStackSource(traceLog) { OnlyManagedCodeStacks = true };
            var computer = new SampleProfilerThreadTimeComputer(traceLog, symbolReader, stitchAsyncCallStacks: stitchAsync);
            computer.GenerateThreadTimeStacks(stackSource);

            var samplesForThread = new Dictionary<int, List<StackSourceSample>>();
            stackSource.ForEach(sample =>
            {
                StackSourceCallStackIndex stackIndex = sample.StackIndex;
                while (!stackSource.GetFrameName(stackSource.GetFrameIndex(stackIndex), false).StartsWith("Thread ("))
                {
                    stackIndex = stackSource.GetCallerIndex(stackIndex);
                }

                string threadFrame = stackSource.GetFrameName(stackSource.GetFrameIndex(stackIndex), false);
                const string template = "Thread (";
                int firstIndex = threadFrame.IndexOf(')');
                int threadId = int.Parse(threadFrame.Substring(template.Length, firstIndex - template.Length));

                if (!samplesForThread.TryGetValue(threadId, out List<StackSourceSample> list))
                {
                    samplesForThread[threadId] = list = new List<StackSourceSample>();
                }
                list.Add(sample);
            });

            foreach (KeyValuePair<int, List<StackSourceSample>> entry in samplesForThread.OrderByDescending(kv => kv.Value.Count))
            {
                int threadId = entry.Key;
                List<StackSourceSample> samples = entry.Value;
                sb.AppendLine($"Found {samples.Count} stacks for thread 0x{threadId:X}");
                sb.AppendLine($"Thread (0x{threadId:X}):");
                StackSourceCallStackIndex stackIndex = samples[0].StackIndex;
                while (!stackSource.GetFrameName(stackSource.GetFrameIndex(stackIndex), false).StartsWith("Thread ("))
                {
                    sb.AppendLine($"  {stackSource.GetFrameName(stackSource.GetFrameIndex(stackIndex), false)}"
                        .Replace("UNMANAGED_CODE_TIME", "[Native Frames]"));
                    stackIndex = stackSource.GetCallerIndex(stackIndex);
                }
                sb.AppendLine();
            }
        }

        // Compares the SAME physical CPU sample (matched by timestamp) with and without async stitching, so the
        // before/after is apples-to-apples (not two different sample instants). Picks the sample by finding, in the
        // stitched source, the first sample whose stack contains <paramref name="needle"/>, then looks up the same
        // timestamp in the non-stitched source.
        private static void DumpSameSampleBeforeAfter(TraceLog traceLog, SymbolReader symbolReader, string needle, StringBuilder sb)
        {
            var noAsync = new MutableTraceEventStackSource(traceLog) { OnlyManagedCodeStacks = true };
            new SampleProfilerThreadTimeComputer(traceLog, symbolReader, stitchAsyncCallStacks: false).GenerateThreadTimeStacks(noAsync);
            var noAsyncByTime = new Dictionary<double, string>();
            noAsync.ForEach(s => noAsyncByTime[s.TimeRelativeMSec] = StackToString(noAsync, s.StackIndex));

            var async = new MutableTraceEventStackSource(traceLog) { OnlyManagedCodeStacks = true };
            new SampleProfilerThreadTimeComputer(traceLog, symbolReader, stitchAsyncCallStacks: true).GenerateThreadTimeStacks(async);

            double matchTime = double.NaN;
            StackSourceCallStackIndex matchStack = StackSourceCallStackIndex.Invalid;
            async.ForEach(s =>
            {
                if (!double.IsNaN(matchTime)) return;
                if (StackContains(async, s.StackIndex, needle)) { matchTime = s.TimeRelativeMSec; matchStack = s.StackIndex; }
            });

            if (double.IsNaN(matchTime))
            {
                sb.AppendLine($"  (no sample containing '{needle}' found)");
                return;
            }

            sb.AppendLine($"sample @ {matchTime:F4} ms on the async worker thread:");
            sb.AppendLine($"  --- BEFORE (no --async): physical stack ---");
            sb.AppendLine(noAsyncByTime.TryGetValue(matchTime, out string before) ? before : "  (same-timestamp sample not found in non-stitched source)");
            sb.AppendLine($"  --- AFTER (--async): logical async stack spliced in ---");
            sb.AppendLine(StackToString(async, matchStack));
        }

        private static bool StackContains(MutableTraceEventStackSource src, StackSourceCallStackIndex idx, string needle)
        {
            while (idx != StackSourceCallStackIndex.Invalid)
            {
                string frame = src.GetFrameName(src.GetFrameIndex(idx), false);
                if (frame.StartsWith("Thread (")) return false;
                if (frame.IndexOf(needle, StringComparison.Ordinal) >= 0) return true;
                idx = src.GetCallerIndex(idx);
            }
            return false;
        }

        private static string StackToString(MutableTraceEventStackSource src, StackSourceCallStackIndex idx)
        {
            var sb = new StringBuilder();
            while (idx != StackSourceCallStackIndex.Invalid)
            {
                string frame = src.GetFrameName(src.GetFrameIndex(idx), false).Replace("UNMANAGED_CODE_TIME", "[Native Frames]");
                sb.AppendLine($"    {frame}");
                if (frame.StartsWith("Thread (")) break;
                idx = src.GetCallerIndex(idx);
            }
            return sb.ToString().TrimEnd();
        }

        private sealed class ControlEventSink : IAsyncProfilerSubEventSink
        {
            public readonly List<(long Qpc, string Text)> Lines = new List<(long, string)>();
            public int MetadataCount, ResetThreadCount, WrapperResetCount, SyncClockCount;
            public ulong QpcFrequency;

            public void OnContextCreate(in AsyncContextEvent e) { }
            public void OnContextResume(in AsyncContextEvent e) { }
            public void OnContextSuspend(in AsyncContextEvent e) { }
            public void OnContextComplete(in AsyncContextEvent e) { }
            public void OnException(in AsyncUnwindEvent e) { }
            public void OnCallstack(in AsyncCallstackEvent e) { }
            public void OnMethodResume(in AsyncMethodEvent e) { }
            public void OnMethodComplete(in AsyncMethodEvent e) { }
            public void OnResetThreadContext(in AsyncNeutralEvent e)
            {
                ResetThreadCount++;
                Lines.Add((e.TimestampQpc, $"  qpc={e.TimestampQpc} tid={e.OsThreadId} *** ResetThreadContext ***"));
            }
            public void OnResetContinuationWrapperIndex(in AsyncNeutralEvent e) { WrapperResetCount++; }
            public void OnMetadata(in AsyncMetadataEvent e)
            {
                MetadataCount++;
                QpcFrequency = e.QpcFrequency;
                Lines.Add((e.TimestampQpc, $"  qpc={e.TimestampQpc} tid={e.OsThreadId} Metadata wrapperCount={e.WrapperCount} qpcFreq={e.QpcFrequency}"));
            }
            public void OnSyncClock(in AsyncSyncClockEvent e)
            {
                SyncClockCount++;
                Lines.Add((e.TimestampQpc, $"  qpc={e.TimestampQpc} tid={e.OsThreadId} SyncClock"));
            }
            public void OnUnknown(in AsyncUnknownEvent e) { }
            public void OnParseError(in AsyncProfilerParseError e) { }
        }

        private sealed class GlobalStartSink : IAsyncProfilerSubEventSink
        {
            public readonly List<(long Qpc, string Text)> Lines = new List<(long, string)>();
            public readonly long WindowMax;
            public long GlobalMinQpc = long.MaxValue;
            public long FirstMetadataQpc = -1;

            public GlobalStartSink(long windowMax) { WindowMax = windowMax; }

            private void Add(ulong tid, long qpc, string desc)
            {
                if (qpc < GlobalMinQpc) GlobalMinQpc = qpc;
                if (qpc <= WindowMax) Lines.Add((qpc, $"  qpc={qpc} tid={tid} {desc}"));
            }

            public void OnContextCreate(in AsyncContextEvent e) => Add(e.OsThreadId, e.TimestampQpc, $"ContextCreate id={e.EventId}");
            public void OnContextResume(in AsyncContextEvent e) => Add(e.OsThreadId, e.TimestampQpc, $"ContextResume id={e.EventId}");
            public void OnContextSuspend(in AsyncContextEvent e) => Add(e.OsThreadId, e.TimestampQpc, $"ContextSuspend id={e.EventId}");
            public void OnContextComplete(in AsyncContextEvent e) => Add(e.OsThreadId, e.TimestampQpc, $"ContextComplete id={e.EventId}");
            public void OnException(in AsyncUnwindEvent e) => Add(e.OsThreadId, e.TimestampQpc, $"Exception id={e.EventId}");
            public void OnCallstack(in AsyncCallstackEvent e) => Add(e.OsThreadId, e.TimestampQpc, $"Callstack id={e.EventId} kind={e.Kind} mc={(e.MethodIds?.Length ?? 0)}");
            public void OnMethodResume(in AsyncMethodEvent e) => Add(e.OsThreadId, e.TimestampQpc, $"MethodResume id={e.EventId}");
            public void OnMethodComplete(in AsyncMethodEvent e) => Add(e.OsThreadId, e.TimestampQpc, $"MethodComplete id={e.EventId}");
            public void OnResetThreadContext(in AsyncNeutralEvent e) => Add(e.OsThreadId, e.TimestampQpc, "*** ResetThreadContext ***");
            public void OnResetContinuationWrapperIndex(in AsyncNeutralEvent e) => Add(e.OsThreadId, e.TimestampQpc, "ResetContinuationWrapperIndex");
            public void OnMetadata(in AsyncMetadataEvent e)
            {
                if (FirstMetadataQpc < 0) FirstMetadataQpc = e.TimestampQpc;
                Add(e.OsThreadId, e.TimestampQpc, $"Metadata wrapperCount={e.WrapperCount} qpcFreq={e.QpcFrequency}");
            }
            public void OnSyncClock(in AsyncSyncClockEvent e) => Add(e.OsThreadId, e.TimestampQpc, "SyncClock");
            public void OnUnknown(in AsyncUnknownEvent e) => Add(e.Header.OsThreadId, e.TimestampQpc, $"Unknown id={e.EventId}");
            public void OnParseError(in AsyncProfilerParseError e) { }
        }

        /// <summary>
        /// Captures sub-events in true DELIVERY (processing) order (the order raw AsyncEvents buffers are delivered
        /// x in-buffer order), NOT QPC order. Answers: is the first metadata the first thing delivered, or does
        /// other data precede it? And do buffers delivered AFTER the metadata still carry pre-metadata QPCs
        /// (leftover prior-session data that the stream-order gate does not exclude)?
        /// </summary>
        private sealed class DeliveryOrderSink : IAsyncProfilerSubEventSink
        {
            public int Total;
            public int BufferIndex = -1;
            public int FirstMetadataSeq = -1;
            public long FirstMetadataQpc = -1;
            public int FirstMetadataBuffer = -1;
            public int SubEventsBeforeMetadata;
            public readonly List<string> BeforeMetadata = new List<string>();
            // Per thread: (deliverySeq, qpc) of its first sub-event, and min/max qpc of sub-events delivered AFTER
            // the first metadata (to detect post-metadata delivery of pre-metadata-QPC leftover data).
            public readonly Dictionary<ulong, (int Seq, long Qpc)> FirstPerThread = new Dictionary<ulong, (int, long)>();
            public readonly Dictionary<ulong, long> PostMetaMinQpc = new Dictionary<ulong, long>();
            public readonly Dictionary<ulong, long> PostMetaMaxQpc = new Dictionary<ulong, long>();
            public readonly Dictionary<ulong, int> PostMetaCountQpcBeforeMeta = new Dictionary<ulong, int>();

            public void NextBuffer() => BufferIndex++;

            private void Record(ulong tid, long qpc, string desc)
            {
                int seq = Total++;
                if (!FirstPerThread.ContainsKey(tid)) FirstPerThread[tid] = (seq, qpc);

                if (FirstMetadataSeq < 0)
                {
                    SubEventsBeforeMetadata++;
                    if (BeforeMetadata.Count < 40) BeforeMetadata.Add($"seq={seq} buf={BufferIndex} tid={tid} qpc={qpc} {desc}");
                }
                else
                {
                    // Delivered after the first metadata: track qpc extents per thread and count any qpc that
                    // predates the metadata (leftover prior-session data not excluded by the stream-order gate).
                    if (!PostMetaMinQpc.TryGetValue(tid, out long mn) || qpc < mn) PostMetaMinQpc[tid] = qpc;
                    if (!PostMetaMaxQpc.TryGetValue(tid, out long mx) || qpc > mx) PostMetaMaxQpc[tid] = qpc;
                    if (qpc < FirstMetadataQpc)
                    {
                        PostMetaCountQpcBeforeMeta.TryGetValue(tid, out int c);
                        PostMetaCountQpcBeforeMeta[tid] = c + 1;
                    }
                }
            }

            public void OnContextCreate(in AsyncContextEvent e) => Record(e.OsThreadId, e.TimestampQpc, $"ContextCreate id={e.EventId}");
            public void OnContextResume(in AsyncContextEvent e) => Record(e.OsThreadId, e.TimestampQpc, $"ContextResume id={e.EventId}");
            public void OnContextSuspend(in AsyncContextEvent e) => Record(e.OsThreadId, e.TimestampQpc, $"ContextSuspend id={e.EventId}");
            public void OnContextComplete(in AsyncContextEvent e) => Record(e.OsThreadId, e.TimestampQpc, $"ContextComplete id={e.EventId}");
            public void OnException(in AsyncUnwindEvent e) => Record(e.OsThreadId, e.TimestampQpc, $"Exception id={e.EventId}");
            public void OnCallstack(in AsyncCallstackEvent e) => Record(e.OsThreadId, e.TimestampQpc, $"Callstack id={e.EventId} kind={e.Kind}");
            public void OnMethodResume(in AsyncMethodEvent e) => Record(e.OsThreadId, e.TimestampQpc, $"MethodResume id={e.EventId}");
            public void OnMethodComplete(in AsyncMethodEvent e) => Record(e.OsThreadId, e.TimestampQpc, $"MethodComplete id={e.EventId}");
            public void OnResetThreadContext(in AsyncNeutralEvent e) => Record(e.OsThreadId, e.TimestampQpc, "*** ResetThreadContext ***");
            public void OnResetContinuationWrapperIndex(in AsyncNeutralEvent e) => Record(e.OsThreadId, e.TimestampQpc, "ResetContinuationWrapperIndex");
            public void OnMetadata(in AsyncMetadataEvent e)
            {
                if (FirstMetadataSeq < 0)
                {
                    FirstMetadataSeq = Total;
                    FirstMetadataQpc = e.TimestampQpc;
                    FirstMetadataBuffer = BufferIndex;
                }
                Record(e.OsThreadId, e.TimestampQpc, $"*** Metadata wc={e.WrapperCount} ***");
            }
            public void OnSyncClock(in AsyncSyncClockEvent e) => Record(e.OsThreadId, e.TimestampQpc, "SyncClock");
            public void OnUnknown(in AsyncUnknownEvent e) => Record(e.Header.OsThreadId, e.TimestampQpc, $"Unknown id={e.EventId}");
            public void OnParseError(in AsyncProfilerParseError e) { }
        }

        private sealed class DumpSink : IAsyncProfilerSubEventSink
        {
            private readonly ulong _tid;
            private readonly long _maxQpc;
            public readonly List<string> Lines = new List<string>();

            public DumpSink(ulong tid, long maxQpc) { _tid = tid; _maxQpc = maxQpc; }

            private void Add(ulong tid, long qpc, string desc)
            {
                if (tid == _tid && qpc <= _maxQpc)
                {
                    Lines.Add($"  qpc={qpc} {desc}");
                }
            }

            public void OnContextCreate(in AsyncContextEvent e) => Add(e.OsThreadId, e.TimestampQpc, $"ContextCreate id={e.EventId}");
            public void OnContextResume(in AsyncContextEvent e) => Add(e.OsThreadId, e.TimestampQpc, $"ContextResume id={e.EventId}");
            public void OnContextSuspend(in AsyncContextEvent e) => Add(e.OsThreadId, e.TimestampQpc, $"ContextSuspend id={e.EventId}");
            public void OnContextComplete(in AsyncContextEvent e) => Add(e.OsThreadId, e.TimestampQpc, $"ContextComplete id={e.EventId}");
            public void OnException(in AsyncUnwindEvent e) => Add(e.OsThreadId, e.TimestampQpc, $"Exception id={e.EventId} unwound={e.UnwoundFrameCount}");
            public void OnCallstack(in AsyncCallstackEvent e)
            {
                ulong m0 = e.MethodIds != null && e.MethodIds.Length > 0 ? e.MethodIds[0] : 0;
                int mc = e.MethodIds?.Length ?? 0;
                Add(e.OsThreadId, e.TimestampQpc, $"Callstack id={e.EventId} kind={e.Kind} methodCount={mc} m0=0x{m0:x}");
            }
            public void OnMethodResume(in AsyncMethodEvent e) => Add(e.OsThreadId, e.TimestampQpc, $"MethodResume id={e.EventId}");
            public void OnMethodComplete(in AsyncMethodEvent e) => Add(e.OsThreadId, e.TimestampQpc, $"MethodComplete id={e.EventId}");
            public void OnResetThreadContext(in AsyncNeutralEvent e) => Add(e.OsThreadId, e.TimestampQpc, "*** ResetThreadContext ***");
            public void OnResetContinuationWrapperIndex(in AsyncNeutralEvent e) => Add(e.OsThreadId, e.TimestampQpc, "ResetContinuationWrapperIndex");
            public void OnMetadata(in AsyncMetadataEvent e) => Add(e.OsThreadId, e.TimestampQpc, $"Metadata wrapperCount={e.WrapperCount}");
            public void OnSyncClock(in AsyncSyncClockEvent e) => Add(e.OsThreadId, e.TimestampQpc, "SyncClock");
            public void OnUnknown(in AsyncUnknownEvent e) { }
            public void OnParseError(in AsyncProfilerParseError e) { }
        }

        private static bool SyncStackHasBoundaryFrame(CallStackIndex callStackIndex, TraceCallStacks callStacks, AsyncStitchBoundaryCache boundaries)
        {
            for (CallStackIndex csi = callStackIndex; csi != CallStackIndex.Invalid; csi = callStacks.Caller(csi))
            {
                CodeAddressIndex ca = callStacks.CodeAddressIndex(csi);
                if (ca != CodeAddressIndex.Invalid && boundaries.Classify(ca).Kind != AsyncStitchBoundaryKind.None)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool SyncStackHasActiveContinuation(TraceEvent data)
        {
            // A continuation is actively executing if a ContinuationWrapper frame is present AND there is a user
            // frame (leaf-ward of the wrapper) running inside it. We approximate by: the wrapper is present and it
            // is NOT the leaf (something is running above it).
            TraceCallStack cs = data.CallStack();
            bool sawWrapper = false;
            int idx = 0, wrapperIdx = -1;
            for (; cs != null; cs = cs.Caller, idx++)
            {
                string n = cs.CodeAddress != null ? cs.CodeAddress.FullMethodName : "";
                if (n.Contains("Continuation_Wrapper_") || n.Contains("MoveNextAsDispatcher"))
                {
                    sawWrapper = true;
                    wrapperIdx = idx;
                    break;
                }
            }
            return sawWrapper && wrapperIdx > 0; // wrapper present and something running above it
        }

        private static List<StitchSyncFrame> MaterializeSync(CallStackIndex callStackIndex, TraceCallStacks callStacks, TraceCodeAddresses codeAddresses)
        {
            var sync = new List<StitchSyncFrame>();
            for (CallStackIndex csi = callStackIndex; csi != CallStackIndex.Invalid; csi = callStacks.Caller(csi))
            {
                CodeAddressIndex ca = callStacks.CodeAddressIndex(csi);
                MethodIndex method = ca != CodeAddressIndex.Invalid ? codeAddresses.MethodIndex(ca) : MethodIndex.Invalid;
                sync.Add(new StitchSyncFrame(ca, method));
            }
            return sync;
        }

        private static string SyncLeaf(TraceEvent data, int depth)
        {
            TraceCallStack cs = data.CallStack();
            var parts = new List<string>();
            for (int i = 0; cs != null && i < depth; i++)
            {
                parts.Add(cs.CodeAddress != null ? cs.CodeAddress.FullMethodName : "?");
                cs = cs.Caller;
            }
            return parts.Count > 0 ? string.Join(" <- ", parts) : "(no stack)";
        }

        private static string FramesOf(TraceLog log, AsyncCallStack seg, int depth)
        {
            AsyncCallStackFrames f = seg.Frames;
            var parts = new List<string>();
            int n = Math.Min(depth, f.FrameCount);
            for (int i = 0; i < n; i++)
            {
                CodeAddressIndex cai = f.CodeAddressAt(i);
                string name = cai != CodeAddressIndex.Invalid ? log.CodeAddresses[cai].FullMethodName : "0x" + f.MethodIdAt(i).ToString("x");
                parts.Add(name);
            }
            return string.Join(" <- ", parts);
        }

        private void WriteOut(StringBuilder sb)
        {
            try
            {
                File.WriteAllText(@"C:\traces\async-diag.txt", sb.ToString());
            }
            catch
            {
                // best-effort; the ITestOutputHelper copy below is the fallback
            }
            _output.WriteLine(sb.ToString());
        }
    }
}
