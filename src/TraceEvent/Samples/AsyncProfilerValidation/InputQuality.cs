// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

namespace AsyncProfilerValidation
{
    internal static class InputQuality
    {
        internal static void Validate(string etlx, string output, bool kernelCpu, int pid, bool allowNoIndex)
        {
            using var log = new TraceLog(etlx);
            using var events = log.Events.GetSource();
            using var symbols = new SymbolReader(Console.Error);
            if (kernelCpu)
            {
                ResolveRuntimeSymbols(log, symbols, pid);
            }
            var builder = new AsyncProfilerIndexBuilder();
            var errors = new List<string>();
            builder.ParseError += (process, error) =>
            {
                if (errors.Count < 20)
                {
                    errors.Add($"ProcessIndex {(int)process}: {error}");
                }
            };
            var counts = new EventCounts();
            var manifests = new Dictionary<ProcessIndex, Manifest>();
            long buffers = 0;
            new AsyncProfilerTraceEventParser(events).AsyncEvents += data =>
            {
                if (pid != 0 && data.ProcessID != pid)
                {
                    return;
                }
                TraceProcess process = data.Process();
                if (process == null)
                {
                    throw new InvalidDataException("Async buffer has no process-instance identity.");
                }
                buffers++;
                if (!manifests.TryGetValue(process.ProcessIndex, out var manifest))
                {
                    manifest = new Manifest();
                    manifests.Add(process.ProcessIndex, manifest);
                }
                byte[] buffer = data.Buffer;
                AsyncProfilerTraceEventParser.ParseBuffer(buffer, manifest, counts);
                builder.Process(buffer, process.ProcessIndex);
            };
            var samples = new List<Anchor>();
            int withPhysicalStack = 0;
            using var physical = new StreamWriter(Path.Combine(output, "cpu-anchors.jsonl"));
            void OnSample(TraceEvent data)
            {
                if (pid != 0 && data.ProcessID != pid)
                {
                    return;
                }
                TraceThread thread = data.Thread();
                if (thread == null)
                {
                    throw new InvalidDataException("CPU sample has no thread/process-instance identity.");
                }
#pragma warning disable CS0618 // Preserve the exact kernel/EventPipe anchor for forensic correlation.
                long qpc = data.TimeStampQPC;
#pragma warning restore CS0618
                samples.Add(new Anchor(new AsyncProfilerThreadKey(thread.Process.ProcessIndex, (ulong)data.ThreadID), qpc));
                var frames = new List<object>();
                for (var stack = data.CallStackIndex(); stack != CallStackIndex.Invalid; stack = log.CallStacks.Caller(stack))
                {
                    var index = log.CallStacks.CodeAddressIndex(stack);
                    var address = log.CodeAddresses[index];
                    frames.Add(new { CodeAddressIndex = (int)index, address.Address, address.FullMethodName, address.ModuleFilePath });
                }
                if (frames.Count != 0)
                {
                    withPhysicalStack++;
                }
                physical.WriteLine(JsonSerializer.Serialize(new
                {
                    EventIndex = (uint)data.EventIndex, TimestampQpc = qpc,
                    data.TimeStampRelativeMSec, data.ProcessID, data.ThreadID,
                    ProcessIndex = (int)thread.Process.ProcessIndex, Frames = frames
                }));
            }
            if (kernelCpu)
            {
                events.Kernel.PerfInfoSample += OnSample;
            }
            else { new SampleProfilerTraceEventParser(events).ThreadSample += OnSample; }
            events.Process();
            builder.Finish();

            var persisted = log.AsyncProfilerCallStacks;
            string persistedHash = IndexHash(persisted, samples);
            string rebuiltHash = IndexHash(builder.Index, samples);
            string reopenedHash;
            using (var reopened = new TraceLog(etlx))
            {
                reopenedHash = IndexHash(reopened.AsyncProfilerCallStacks, samples);
            }
            int withAsync = 0;
            var kinds = new Dictionary<string, int>();
            using (var activations = new StreamWriter(Path.Combine(output, "cpu-activations.jsonl")))
            {
                foreach (var sample in samples)
                {
                    var active = persisted?.GetCallStacks(sample.Thread, sample.Qpc);
                    if (active == null || active.Count == 0)
                    {
                        continue;
                    }
                    withAsync++;
                    foreach (var activation in active)
                    {
                        string kind = activation.Frames.Kind.ToString();
                        kinds[kind] = kinds.TryGetValue(kind, out int n) ? n + 1 : 1;
                        activations.WriteLine(JsonSerializer.Serialize(new
                        {
                            TimestampQpc = sample.Qpc, ProcessIndex = (int)sample.Thread.ProcessIndex,
                            sample.Thread.OsThreadId, Kind = kind, activation.DispatcherId, activation.Depth,
                            activation.StartQpc, activation.EndQpc,
                            MethodCompleted = activation.GetMethodCompletedFrameCount(sample.Qpc),
                            ExceptionCompleted = activation.GetExceptionCompletedFrameCount(sample.Qpc),
                            Frames = Enumerable.Range(0, activation.Frames.FrameCount).Select(i =>
                            {
                                var address = activation.Frames.CodeAddressAt(i);
                                return new
                                {
                                    MethodId = activation.Frames.InstructionPointerAt(i),
                                    State = activation.Frames.FrameStateAt(i), CodeAddressIndex = (int)address,
                                    Name = address == CodeAddressIndex.Invalid ? null : log.CodeAddresses[address].FullMethodName
                                };
                            }).ToArray()
                        }));
                    }
                }
            }
            File.WriteAllText(Path.Combine(output, "input-quality.json"), JsonSerializer.Serialize(new
            {
                CpuSource = kernelCpu ? "ETW kernel PerfInfoSample" : "EventPipe ThreadSample",
                TargetProcessID = pid, log.EventsLost, log.Truncated, AsyncBuffers = buffers,
                builder.ParseErrorCount, ParseErrors = errors, SubEvents = counts.Values,
                CpuSamples = samples.Count, SamplesWithPhysicalStack = withPhysicalStack,
                SamplesWithAsyncActivation = withAsync, SamplesWithoutAsyncActivation = samples.Count - withAsync,
                SampledActivationKinds = kinds, PersistedIndexPresent = persisted != null,
                PersistedIndexSampleSha256 = persistedHash, RebuiltIndexSampleSha256 = rebuiltHash,
                ReopenedIndexSampleSha256 = reopenedHash,
                RuntimeModules = log.Processes.Where(process => pid == 0 || process.ProcessID == pid)
                    .SelectMany(process => process.LoadedModules)
                    .Select(loaded => loaded.ModuleFile)
                    .Where(AsyncProfilerRuntimeRuleProfileCache.IsHostModule)
                    .Select(module => new
                    {
                        ModuleFileIndex = (int)module.ModuleFileIndex, module.FilePath,
                        module.FileVersion, module.ProductVersion, module.PdbSignature, module.PdbAge
                    }).Distinct().ToArray(),
                CoverageNote = "No activation is a physical-stack fallback, not proof of missing events. Unsampled scenarios are not validated."
            }));
            if (log.EventsLost != 0 || log.Truncated || builder.ParseErrorCount != 0 || counts.Errors != 0)
            {
                throw new InvalidDataException("Capture loss, truncation, or async parse errors; see input-quality.json.");
            }
            if (samples.Count == 0 || withPhysicalStack == 0)
            {
                throw new InvalidDataException("No selected CPU samples or physical stacks; see input-quality.json.");
            }
            if (persistedHash != rebuiltHash || persistedHash != reopenedHash)
            {
                throw new InvalidDataException("Persisted, freshly rebuilt, and reopened async index queries differ.");
            }
            if (!allowNoIndex && (persisted == null || withAsync == 0))
            {
                throw new InvalidDataException("No sampled async activations; capture is inconclusive, not validated.");
            }
        }

        internal static void ResolveRuntimeSymbols(TraceLog log, SymbolReader symbols, int pid)
        {
            var seen = new HashSet<ModuleFileIndex>();
            foreach (TraceProcess process in log.Processes)
            {
                if (pid != 0 && process.ProcessID != pid)
                {
                    continue;
                }
                foreach (TraceLoadedModule loaded in process.LoadedModules)
                {
                    TraceModuleFile module = loaded.ModuleFile;
                    if (AsyncProfilerRuntimeRuleProfileCache.IsHostModule(module) && seen.Add(module.ModuleFileIndex))
                    {
                        // Normal ETW collection uses native PDBs for precompiled CoreLib methods, not NGen rundown.
                        log.CodeAddresses.LookupSymbolsForModule(symbols, module);
                    }
                }
            }
        }

        #region private

        private static string IndexHash(AsyncProfilerCallStacksIndex index, List<Anchor> samples)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            void Add(long value) => hash.AppendData(BitConverter.GetBytes(value));
            foreach (var sample in samples)
            {
                Add(sample.Qpc);
                Add((int)sample.Thread.ProcessIndex);
                Add(unchecked((long)sample.Thread.OsThreadId));
                var active = index?.GetCallStacks(sample.Thread, sample.Qpc);
                Add(active?.Count ?? 0);
                if (active == null)
                {
                    continue;
                }
                foreach (var activation in active)
                {
                    Add(unchecked((long)activation.DispatcherId));
                    Add(activation.StartQpc);
                    Add(activation.EndQpc);
                    Add(activation.Depth);
                    Add((int)activation.Frames.Kind);
                    Add(activation.GetMethodCompletedFrameCount(sample.Qpc));
                    Add(activation.GetExceptionCompletedFrameCount(sample.Qpc));
                    Add(activation.GetWrapperResetCount(sample.Qpc));
                    Add(index.MethodCompletionObserved(activation) ? 1 : 0);
                    Add(index.ExceptionCompletionObserved(activation) ? 1 : 0);
                    Add(activation.Frames.FrameCount);
                    for (int i = 0; i < activation.Frames.FrameCount; i++)
                    {
                        Add(unchecked((long)activation.Frames.InstructionPointerAt(i)));
                        Add(activation.Frames.FrameStateAt(i));
                    }
                }
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        private readonly record struct Anchor(AsyncProfilerThreadKey Thread, long Qpc);

        private sealed class EventCounts : ISubEventSink
        {
            public readonly Dictionary<string, long> Values = new Dictionary<string, long>();
            public long Errors;
            public void OnContextCreate(in AsyncContextEvent e) => Count(e.EventId);
            public void OnContextResume(in AsyncContextEvent e) => Count(e.EventId);
            public void OnContextSuspend(in AsyncContextEvent e) => Count(e.EventId);
            public void OnContextComplete(in AsyncContextEvent e) => Count(e.EventId);
            public void OnException(in AsyncUnwindEvent e) => Count(e.EventId);
            public void OnCallstack(in AsyncCallstackEvent e) => Count(e.EventId);
            public void OnMethodResume(in AsyncMethodEvent e) => Count(e.EventId);
            public void OnMethodComplete(in AsyncMethodEvent e) => Count(e.EventId);
            public void OnResetThreadContext(in AsyncNeutralEvent e) => Count(e.EventId);
            public void OnResetContinuationWrapperIndex(in AsyncNeutralEvent e) => Count(e.EventId);
            public void OnMetadata(in AsyncMetadataEvent e) => Count(AsyncEventID.AsyncProfilerMetadata);
            public void OnSyncClock(in AsyncSyncClockEvent e) => Count(AsyncEventID.AsyncProfilerSyncClock);
            public void OnUnknown(in AsyncUnknownEvent e) => Count(e.EventId);
            public void OnParseError(in ParseError e) => Errors++;
            private void Count(AsyncEventID id) =>
                Values[id.ToString()] = Values.TryGetValue(id.ToString(), out long n) ? n + 1 : 1;
        }

        #endregion
    }
}
