// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Globalization;
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
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;
using Microsoft.Diagnostics.Tracing.Stacks.Formats;

namespace AsyncProfilerValidation
{
    internal static class Program
    {
        #region private

        private static void Main(string[] args)
        {
            string[] originalArgs = args;
            bool enableParentContexts = false;
            bool allowNoIndex = false;
            bool preserveSampleGaps = true;
            TimeSpan? asyncSampleInterval = null;
            int targetPid = 0;
            var positional = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--parent-contexts")
                {
                    enableParentContexts = true;
                }
                else if (args[i] == "--allow-no-index")
                {
                    allowNoIndex = true;
                }
                else if (args[i] == "--legacy-sample-timing")
                {
                    preserveSampleGaps = false;
                }
                else if (args[i] == "--pid")
                {
                    if (++i == args.Length || !int.TryParse(args[i], NumberStyles.None,
                        CultureInfo.InvariantCulture, out targetPid) || targetPid <= 0)
                    {
                        throw new ArgumentException("--pid requires a positive process ID.");
                    }
                }
                else if (args[i] == "--async-sample-interval-ms")
                {
                    if (++i == args.Length || !double.TryParse(args[i], NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double interval) ||
                        !double.IsFinite(interval) || interval <= 0 ||
                        interval >= TimeSpan.MaxValue.TotalMilliseconds)
                    {
                        throw new ArgumentException("--async-sample-interval-ms requires a finite, positive interval.");
                    }
                    asyncSampleInterval = TimeSpan.FromMilliseconds(interval);
                    if (asyncSampleInterval.Value <= TimeSpan.Zero)
                    {
                        throw new ArgumentException("--async-sample-interval-ms is below TimeSpan resolution.");
                    }
                }
                else
                {
                    positional.Add(args[i]);
                }
            }
            args = positional.ToArray();

            if (!((args.Length == 3 && args[0] == "export") ||
                  (args.Length == 7 && args[0] == "probe") ||
                  (args.Length == 6 && args[0] == "probe-qpc")))
            {
                throw new ArgumentException(
                    "Usage:\n  AsyncProfilerValidation export <capture.nettrace|capture.etl> <new-output-directory> [--pid <pid>] [--parent-contexts] [--allow-no-index] [--legacy-sample-timing] [--async-sample-interval-ms <ms>]\n" +
                    "  AsyncProfilerValidation probe <capture.etlx> <pid> <os-thread-id> <start-ms> <end-ms> <new-output-directory> [--parent-contexts]\n" +
                    "  AsyncProfilerValidation probe-qpc <capture.etlx> <pid> <os-thread-id> <qpc> <new-output-directory> [--parent-contexts]");
            }

            string input = Path.GetFullPath(args[1]);
            if (!File.Exists(input))
            {
                throw new FileNotFoundException("Input capture/cache is missing.", input);
            }
            bool export = args[0] == "export";
            bool kernelCpu = string.Equals(Path.GetExtension(input), ".etl", StringComparison.OrdinalIgnoreCase);
            if (export ? !kernelCpu && !string.Equals(Path.GetExtension(input), ".nettrace", StringComparison.OrdinalIgnoreCase)
                       : !string.Equals(Path.GetExtension(input), ".etlx", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(export ? "Export requires a .nettrace or merged .etl." : "Probe requires an ETLX produced by export.");
            }

            int pid = 0;
            ulong tid = 0;
            long? exactQpc = null;
            double start = 0;
            double end = 0;
            if (!export)
            {
                if (targetPid != 0 || allowNoIndex || !preserveSampleGaps || asyncSampleInterval.HasValue)
                {
                    throw new ArgumentException("--pid, --allow-no-index, --legacy-sample-timing and --async-sample-interval-ms apply only to export.");
                }
                pid = int.Parse(args[2], NumberStyles.None, CultureInfo.InvariantCulture);
                tid = ulong.Parse(args[3], NumberStyles.None, CultureInfo.InvariantCulture);
                if (pid == 0 || tid == 0)
                {
                    throw new ArgumentException("PID and OS thread ID must be positive.");
                }
                using var log = new TraceLog(input);
                kernelCpu = log.Events.Any(data => data is SampledProfileTraceData && data.ProcessID == pid);
                if (args[0] == "probe-qpc")
                {
                    exactQpc = long.Parse(args[4], NumberStyles.None, CultureInfo.InvariantCulture);
                }
                else
                {
                    start = double.Parse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture);
                    end = double.Parse(args[5], NumberStyles.Float, CultureInfo.InvariantCulture);
                    if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end < start)
                    {
                        throw new ArgumentException("Use a finite, nonnegative, inclusive millisecond window with end >= start.");
                    }
                }
            }

            string output = Path.GetFullPath(args[args.Length - 1]);
            if (Directory.Exists(output) || File.Exists(output))
            {
                throw new IOException("Use a new output directory; preserve earlier runs.");
            }
            Directory.CreateDirectory(output);
            WriteJson(Path.Combine(output, "run.json"), new
            {
                Arguments = originalArgs,
                Input = input,
                InputSha256 = FileHash(input),
                StartedUtc = DateTime.UtcNow,
                TraceEvent = typeof(TraceLog).Assembly.Location,
                TraceEventSha256 = FileHash(typeof(TraceLog).Assembly.Location),
                Helper = typeof(Program).Assembly.Location,
                HelperSha256 = FileHash(typeof(Program).Assembly.Location),
                SymbolPathEnvironment = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH"),
                CpuSource = kernelCpu ? "ETW kernel PerfInfoSample" : "EventPipe ThreadSample",
                TargetProcessID = export ? targetPid : pid,
                OnlyManagedCodeStacks = !kernelCpu,
                IncludeEventSourceEvents = !kernelCpu,
                ResolvePrecompiledRuntimeSymbols = kernelCpu,
                AllowNoIndex = allowNoIndex,
                PreserveSampleGaps = preserveSampleGaps,
                AsyncSampleIntervalMSec = asyncSampleInterval?.TotalMilliseconds,
                EnableFrameAnnotations = true,
                EnableParentContexts = enableParentContexts
            });

            if (export)
            {
                var options = new TraceLogOptions
                {
                    KeepAsyncProfilerEvents = true,
                    ConversionLog = Console.Error,
                    OnLostEvents = (truncated, lost, count) =>
                        throw new InvalidDataException($"Capture loss/truncation: truncated={truncated}, lost={lost}, events={count}.")
                };
                string cache = Path.Combine(output, "capture.etlx");
                string etlx = kernelCpu ? TraceLog.CreateFromEventTraceLogFile(input, cache, options)
                    : TraceLog.CreateFromEventPipeDataFile(input, cache, options);
                InputQuality.Validate(etlx, output, kernelCpu, targetPid, allowNoIndex);
                Export(etlx, output, enableParentContexts, kernelCpu, targetPid, allowNoIndex, preserveSampleGaps, asyncSampleInterval);
            }
            else
            {
                Probe(input, output, pid, tid, start, end, exactQpc, enableParentContexts, kernelCpu);
            }
        }

        private static void Export(string etlx, string output, bool enableParentContexts,
            bool kernelCpu, int targetPid, bool allowNoIndex, bool preserveSampleGaps, TimeSpan? asyncSampleInterval)
        {
            string baselineHash = null;
            int baselineCount = 0;
            foreach (var view in s_views)
            {
                using var log = new TraceLog(etlx);
                using var symbols = new SymbolReader(Console.Error);
                if (kernelCpu) { InputQuality.ResolveRuntimeSymbols(log, symbols, targetPid); }
                var source = new MutableTraceEventStackSource(log) { OnlyManagedCodeStacks = !kernelCpu };
                var computer = CreateComputer(log, symbols, view, enableParentContexts, kernelCpu);
                computer.TargetPid = targetPid;
                computer.GenerateThreadTimeStacks(source);
                if (view.EnableAsyncCallStacks && !computer.AsyncCallStacksActive && !allowNoIndex)
                {
                    throw new InvalidDataException("No usable async index initialized stitching; check producer, keywords, and flush timing.");
                }

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var samples = new StreamWriter(Path.Combine(output, view.Name + ".samples.jsonl"));
                int count = 0;
                double weight = 0;
                source.ForEach(sample =>
                {
                    int ordinal = count++;
                    weight += sample.Metric;
                    hash.AppendData(BitConverter.GetBytes(sample.TimeRelativeMSec));
                    hash.AppendData(BitConverter.GetBytes(sample.Metric));
                    var frames = new List<string>();
                    for (var stack = sample.StackIndex; stack != StackSourceCallStackIndex.Invalid; stack = source.GetCallerIndex(stack))
                    {
                        frames.Add(source.GetFrameName(source.GetFrameIndex(stack), false));
                    }
                    WriteLine(samples, new { Ordinal = ordinal, sample.TimeRelativeMSec, sample.Metric, Frames = frames });
                });
                if (count == 0)
                {
                    throw new InvalidDataException("No samples were generated; verify collection and host settings.");
                }
                string digest = Convert.ToHexString(hash.GetHashAndReset());
                if (baselineHash == null)
                {
                    baselineHash = digest;
                    baselineCount = count;
                }
                else if (digest != baselineHash || count != baselineCount)
                {
                    throw new InvalidDataException("Sample timestamps/metrics/count changed between views.");
                }

                string path = Path.Combine(output, view.Name + ".speedscope.json");
                SpeedScopeStackSourceWriter.WriteStackViewAsJson(source, path, preserveSampleGaps);
                ValidateExport(path);
                WriteJson(Path.Combine(output, view.Name + ".summary.json"), new
                {
                    view.Name,
                    Count = count,
                    Weight = weight,
                    TimestampMetricSha256 = digest,
                    PreserveSampleGaps = preserveSampleGaps,
                    computer.AsyncCallStacksActive,
                    computer.AsyncCallStackTransforms.EnableParentContexts,
                    computer.UseTasks,
                    computer.GroupByStartStopActivity,
                    SymbolPath = symbols.SymbolPath,
                    Diagnostics = computer.AsyncProfilerCallStackDiagnostics
                });
                Console.WriteLine($"{view.Name}: {count} samples, weight {weight:R}, timestamp/metric SHA-256 {digest}");
            }

            using (var log = new TraceLog(etlx))
            using (var symbols = new SymbolReader(Console.Error))
            {
                if (kernelCpu) { InputQuality.ResolveRuntimeSymbols(log, symbols, targetPid); }
                string path = Path.Combine(output, "async-timeline.speedscope.json");
                if (asyncSampleInterval.HasValue)
                {
                    SpeedScopeStackSourceWriter.WriteAsyncCallStacksAsJson(log, path, asyncSampleInterval.Value, targetPid);
                }
                else
                {
                    SpeedScopeStackSourceWriter.WriteAsyncCallStacksAsJson(log, path, targetPid);
                }
                var summary = ValidateExport(path, allowEmpty: allowNoIndex || asyncSampleInterval.HasValue);
                WriteJson(Path.Combine(output, "async-timeline.summary.json"), new
                {
                    Name = "async-timeline", summary.Profiles, summary.Frames, summary.Events,
                    TargetProcessID = targetPid,
                    Measurement = asyncSampleInterval.HasValue
                        ? "Virtually sampled async-context occupancy; not CPU time or exact method lifetimes."
                        : "Elapsed time with recorded async contexts active; not CPU time or full method lifetimes.",
                    AsyncSampleIntervalMSec = asyncSampleInterval?.TotalMilliseconds,
                    EffectiveAsyncSampleIntervalMSec = asyncSampleInterval.HasValue
                        ? Math.Round(asyncSampleInterval.Value.Ticks * (log.QPCFreq / (double)TimeSpan.TicksPerSecond),
                            MidpointRounding.AwayFromZero) * 1000.0 / log.QPCFreq : (double?)null,
                    SamplingGrid = asyncSampleInterval.HasValue ? "Trace start, shared across threads; final interval clipped at trace end." : null,
                    FinalizedV1AppendsIncludedAtResume = true,
                    CompletionProgress = "Recorded method-completion and exception-unwind events only.",
                    MissingAsyncData = log.AsyncProfilerCallStacks == null,
                    NoVisibleAsyncContexts = summary.Profiles == 0
                });
                Console.WriteLine($"async-timeline: {summary.Profiles} profiles, {summary.Events} timestamped events " +
                    $"({(asyncSampleInterval.HasValue ? "virtually sampled occupancy" : "elapsed time")}, not CPU samples)");
            }
        }

        private static void Probe(string etlx, string output, int pid, ulong tid, double start, double end,
            long? exactQpc, bool enableParentContexts, bool kernelCpu)
        {
            var targets = new Dictionary<(long Qpc, ProcessIndex Process, ulong Thread), List<uint>>();
            var times = new Dictionary<double, List<uint>>();
            using (var log = new TraceLog(etlx))
            using (var events = log.Events.GetSource())
            using (var symbols = new SymbolReader(Console.Error))
            using (var raw = new StreamWriter(Path.Combine(output, "physical-and-index.jsonl")))
            {
                if (kernelCpu) { InputQuality.ResolveRuntimeSymbols(log, symbols, pid); }
                var callStackComputer = new AsyncProfilerCallStackComputer(log);
                void OnSample(TraceEvent data)
                {
                    long qpc = GetQpc(data);
                    double time = data.TimeStampRelativeMSec;
                    if (data.ProcessID != pid || (ulong)data.ThreadID != tid ||
                        (exactQpc.HasValue ? qpc != exactQpc.Value : time < start || time > end))
                    {
                        return;
                    }
                    TraceThread thread = data.Thread();
                    if (thread == null)
                    {
                        throw new InvalidDataException("Selected sample has no TraceThread identity.");
                    }
                    var key = (qpc, thread.Process.ProcessIndex, tid);
                    if (!targets.TryGetValue(key, out var indices))
                    {
                        indices = new List<uint>();
                        targets.Add(key, indices);
                    }
                    indices.Add((uint)data.EventIndex);
                    if (!times.TryGetValue(time, out var atTime))
                    {
                        atTime = new List<uint>();
                        times.Add(time, atTime);
                    }
                    atTime.Add((uint)data.EventIndex);
                    var physical = new List<object>();
                    for (var stack = data.CallStackIndex(); stack != CallStackIndex.Invalid; stack = log.CallStacks.Caller(stack))
                    {
                        physical.Add(AddressInfo(log, log.CallStacks.CodeAddressIndex(stack)));
                    }
                    WriteLine(raw, new
                    {
                        EventIndex = (uint)data.EventIndex,
                        data.ProviderName,
                        data.EventName,
                        ProviderGuid = data.ProviderGuid,
                        EventId = (int)data.ID,
                        SampleType = data is ClrThreadSampleTraceData sample ? sample.Type.ToString() : "KernelCpu",
                        TimestampQpc = qpc,
                        TimeRelativeMSec = time,
                        data.ProcessID,
                        OsThreadId = tid,
                        ProcessIndex = (int)thread.Process.ProcessIndex,
                        ThreadIndex = (int)thread.ThreadIndex,
                        ProcessStartMSec = thread.Process.StartTimeRelativeMsec,
                        ProcessEndMSec = thread.Process.EndTimeRelativeMsec,
                        ThreadStartMSec = thread.StartTimeRelativeMSec,
                        ThreadEndMSec = thread.EndTimeRelativeMSec,
                        CallStackIndex = (int)data.CallStackIndex(),
                        PhysicalFrames = physical,
                        Activations = callStackComputer.GetCallStacks(data).Select(a => ActivationInfo(log, a, qpc)).ToArray()
                    });
                }
                if (kernelCpu)
                {
                    events.Kernel.PerfInfoSample += OnSample;
                }
                else
                {
                    new SampleProfilerTraceEventParser(events).ThreadSample += OnSample;
                }
                events.Process();
            }
            if (targets.Count == 0)
            {
                throw new InvalidDataException("No exact sample matched the PID/thread/window or QPC; no nearest-sample substitution was made.");
            }

            foreach (var view in s_views)
            {
                using var log = new TraceLog(etlx);
                using var symbols = new SymbolReader(Console.Error);
                if (kernelCpu) { InputQuality.ResolveRuntimeSymbols(log, symbols, pid); }
                using var transforms = new StreamWriter(Path.Combine(output, view.Name + ".transforms.jsonl"));
                using var emitted = new StreamWriter(Path.Combine(output, view.Name + ".emitted.jsonl"));
                var source = new MutableTraceEventStackSource(log) { OnlyManagedCodeStacks = !kernelCpu };
                var computer = CreateComputer(log, symbols, view, enableParentContexts, kernelCpu);
                computer.TargetPid = kernelCpu ? pid : 0;
                computer.TraceAsyncProfilerCallStackSteps = true;
                int callbackCount = 0;
                computer.AsyncProfilerCallStackTransformPipeline.Add(context =>
                {
                    if (context.Segments.Count == 0)
                    {
                        return;
                    }
                    AsyncProfilerThreadKey owner = context.Segments[0].Thread;
                    if (!targets.TryGetValue((context.TimestampQpc, owner.ProcessIndex, owner.OsThreadId), out var indices))
                    {
                        return;
                    }
                    callbackCount++;
                    // Serialize now: the computer reuses activation objects, frames, and diagnostics.
                    WriteLine(transforms, new
                    {
                        CandidateEventIndices = indices,
                        context.TimestampQpc,
                        Segments = context.Segments.Select(a => ActivationIdentity(a)).ToArray(),
                        Frames = context.Frames.Select(frame => new
                        {
                            Origin = frame.Origin.ToString(),
                            ContextKind = frame.ContextKind.ToString(),
                            Activation = ActivationIdentity(frame.Context),
                            frame.SegmentFrameIndex,
                            Address = AddressInfo(log, frame.CodeAddress),
                            MethodIndex = (int)frame.Method,
                            BoxedStateMachineMethod = (int)frame.BoxedStateMachineMethod,
                            BoxedStateMachineMethodName = frame.BoxedStateMachineMethod == MethodIndex.Invalid
                                ? null : log.CodeAddresses.Methods.FullMethodName(frame.BoxedStateMachineMethod),
                            SyncFrameKind = frame.SyncFrameKind.ToString(),
                            Presentation = frame.Presentation.ToString(),
                            Boundary = frame.CodeAddress == CodeAddressIndex.Invalid
                                ? AsyncProfilerCallStackStitchBoundaryInfo.None : context.ClassifyBoundary(frame.CodeAddress)
                        }).ToArray(),
                        Diagnostics = context.Diagnostics
                    });
                });
                computer.GenerateThreadTimeStacks(source);
                int count = 0;
                int ordinal = 0;
                source.ForEach(sample =>
                {
                    int sampleOrdinal = ordinal++;
                    if (!times.TryGetValue(sample.TimeRelativeMSec, out var candidates))
                    {
                        return;
                    }
                    var frames = new List<object>();
                    for (var stack = sample.StackIndex; stack != StackSourceCallStackIndex.Invalid; stack = source.GetCallerIndex(stack))
                    {
                        var frame = source.GetFrameIndex(stack);
                        frames.Add(new
                        {
                            FrameIndex = (int)frame,
                            Name = source.GetFrameName(frame, false),
                            Address = AddressInfo(log, source.GetFrameCodeAddress(frame)),
                            Async = source.TryGetAsyncProfilerCallStackFrameInfo(frame, out var info) ? (object)info : null
                        });
                    }
                    count++;
                    WriteLine(emitted, new
                    {
                        Ordinal = sampleOrdinal,
                        sample.TimeRelativeMSec,
                        sample.Metric,
                        CandidateAnchorEventIndices = candidates,
                        Frames = frames
                    });
                });
                WriteJson(Path.Combine(output, view.Name + ".summary.json"), new
                {
                    view.Name,
                    EmittedTimestampCandidates = count,
                    TransformCallbacks = callbackCount,
                    computer.AsyncCallStacksActive,
                    computer.AsyncCallStackTransforms.EnableParentContexts,
                    computer.UseTasks,
                    computer.GroupByStartStopActivity,
                    SymbolPath = symbols.SymbolPath,
                    Diagnostics = computer.AsyncProfilerCallStackDiagnostics
                });
            }
            WriteJson(Path.Combine(output, "probe.summary.json"), new
            {
                SelectedPhysicalSamples = targets.Values.Sum(indices => indices.Count),
                TargetInstants = targets.Count,
                ProcessID = pid,
                OsThreadId = tid,
                StartMSec = exactQpc.HasValue ? (double?)null : start,
                EndMSec = exactQpc.HasValue ? (double?)null : end,
                ExactQpc = exactQpc,
                EmittedSelection = "Timestamp candidates across all threads; match Ordinal and full roots to the export, not a thread name alone."
            });
            Console.WriteLine($"Probe: {targets.Values.Sum(indices => indices.Count)} physical samples; evidence in {output}");
        }

        private static CpuComputer CreateComputer(
            TraceLog log, SymbolReader symbols,
            (string Name, bool EnableAsyncCallStacks, bool EnableNormalization, bool EnableInfrastructureCleanup) view,
            bool enableParentContexts, bool kernelCpu)
        {
            var computer = new CpuComputer(log, symbols, kernelCpu, view.EnableAsyncCallStacks);
            computer.AsyncCallStackTransforms.EnableNormalization = view.EnableNormalization;
            computer.AsyncCallStackTransforms.EnableInfrastructureCleanup = view.EnableInfrastructureCleanup;
            computer.AsyncCallStackTransforms.EnableFrameAnnotations = true;
            computer.AsyncCallStackTransforms.EnableParentContexts =
                view.EnableAsyncCallStacks && enableParentContexts;
            return computer;
        }

        // Keep collection-specific controls here; the production hosts own all reconstruction logic.
#pragma warning disable CS0618 // Deliberately validate the existing experimental kernel CPU host.
        private sealed class CpuComputer
        {
            public CpuComputer(TraceLog log, SymbolReader symbols, bool kernelCpu, bool enableAsync)
            {
                m_log = log;
                if (kernelCpu)
                {
                    m_kernel = new ThreadTimeStackComputer(log, symbols)
                    {
                        EnableAsyncCallStacks = enableAsync,
                        IncludeEventSourceEvents = false,
                        NoAwaitTime = true,
                        ExcludeReadyThread = true
                    };
                }
                else
                {
                    m_eventPipe = new SampleProfilerThreadTimeComputer(log, symbols) { EnableAsyncCallStacks = enableAsync };
                }
            }

            public int TargetPid { get; set; }
            public bool AsyncCallStacksActive => m_kernel?.AsyncCallStacksActive ?? m_eventPipe.AsyncCallStacksActive;
            public bool UseTasks => m_kernel?.UseTasks ?? m_eventPipe.UseTasks;
            public bool GroupByStartStopActivity => m_kernel?.GroupByStartStopActivity ?? m_eventPipe.GroupByStartStopActivity;
            public AsyncCallStackTransforms AsyncCallStackTransforms => m_kernel?.AsyncCallStackTransforms ?? m_eventPipe.AsyncCallStackTransforms;
            public AsyncProfilerCallStackTransformPipeline AsyncProfilerCallStackTransformPipeline =>
                m_kernel?.AsyncProfilerCallStackTransformPipeline ?? m_eventPipe.AsyncProfilerCallStackTransformPipeline;
            public AsyncProfilerCallStackDiagnostics AsyncProfilerCallStackDiagnostics =>
                m_kernel != null ? m_kernel.AsyncProfilerCallStackDiagnostics : m_eventPipe.AsyncProfilerCallStackDiagnostics;
            public bool TraceAsyncProfilerCallStackSteps
            {
                set
                {
                    if (m_kernel != null) { m_kernel.TraceAsyncProfilerCallStackSteps = value; }
                    else { m_eventPipe.TraceAsyncProfilerCallStackSteps = value; }
                }
            }

            public void GenerateThreadTimeStacks(MutableTraceEventStackSource source)
            {
                if (m_kernel != null)
                {
                    // Exclude allocation/task/context-switch samples: all four views represent kernel CPU only.
                    m_kernel.GenerateThreadTimeStacks(source, m_log.Events.Filter(data =>
                        data is SampledProfileTraceData && (TargetPid == 0 || data.ProcessID == TargetPid)));
                }
                else
                {
                    m_eventPipe.GenerateThreadTimeStacks(source,
                        TargetPid == 0 ? null : m_log.Events.Filter(data => data.ProcessID == TargetPid));
                }
            }

            #region private
            private readonly TraceLog m_log;
            private readonly ThreadTimeStackComputer m_kernel;
            private readonly SampleProfilerThreadTimeComputer m_eventPipe;
            #endregion
        }
#pragma warning restore CS0618

        private static object AddressInfo(TraceLog log, CodeAddressIndex index)
        {
            TraceCodeAddress address = index == CodeAddressIndex.Invalid ? null : log.CodeAddresses[index];
            return new
            {
                CodeAddressIndex = (int)index,
                HasCodeAddress = address != null,
                NativeIP = address == null ? null : "0x" + address.Address.ToString("X", CultureInfo.InvariantCulture),
                MethodIndex = (int)(address == null ? MethodIndex.Invalid : log.CodeAddresses.MethodIndex(index)),
                FullMethodName = address?.FullMethodName,
                ModuleFileIndex = (int)(address == null ? ModuleFileIndex.Invalid : log.CodeAddresses.ModuleFileIndex(index)),
                ModuleFilePath = address?.ModuleFilePath,
                Unresolved = address != null && string.IsNullOrEmpty(address.FullMethodName)
            };
        }

        private static object ActivationIdentity(AsyncProfilerCallStack activation) =>
            activation == null ? null : new
            {
                ProcessIndex = (int)activation.Thread.ProcessIndex,
                activation.Thread.OsThreadId,
                DispatcherId = "0x" + activation.DispatcherId.ToString("X", CultureInfo.InvariantCulture),
                activation.Depth,
                activation.StartQpc,
                activation.EndQpc,
                FramesIndex = (int)activation.FramesIndex,
                Kind = activation.Frames.Kind.ToString()
            };

        private static object ActivationInfo(TraceLog log, AsyncProfilerCallStack activation, long qpc)
        {
            bool hasParent = log.AsyncProfilerCallStacks.TryGetParentCallStack(activation, out var parent, out long parentQpc);
            return new
            {
                Identity = ActivationIdentity(activation),
                activation.ContinuationIndexBase,
                activation.WrapperCount,
                MethodCompletionObserved = log.AsyncProfilerCallStacks.MethodCompletionObserved(activation),
                ExceptionCompletionObserved = log.AsyncProfilerCallStacks.ExceptionCompletionObserved(activation),
                MethodCompleted = activation.GetMethodCompletedFrameCount(qpc),
                ExceptionCompleted = activation.GetExceptionCompletedFrameCount(qpc),
                RecordedCompleted = activation.GetCompletedFrameCount(qpc),
                WrapperResets = activation.GetWrapperResetCount(qpc),
                DispatcherInfo = log.AsyncProfilerCallStacks.GetDispatcherInfo(activation),
                Parent = hasParent ? new { TimestampQpc = parentQpc, Identity = ActivationIdentity(parent) } : null,
                Children = log.AsyncProfilerCallStacks.GetChildDispatcherInfos(activation).ToArray(),
                Frames = Enumerable.Range(0, activation.Frames.FrameCount).Select(i => new
                {
                    Ordinal = i,
                    MethodId = "0x" + activation.Frames.MethodIdAt(i).ToString("X", CultureInfo.InvariantCulture),
                    CapturedState = activation.Frames.FrameStateAt(i),
                    Address = AddressInfo(log, activation.Frames.CodeAddressAt(i))
                }).ToArray()
            };
        }

        private static long GetQpc(TraceEvent data)
        {
#pragma warning disable CS0618 // A forensic probe deliberately records the exact event timestamp, not a rounded viewer time.
            return data.TimeStampQPC;
#pragma warning restore CS0618
        }

        private static string FileHash(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        private static void WriteJson(string path, object value) =>
            File.WriteAllText(path, JsonSerializer.Serialize(value, s_jsonOptions));

        private static void WriteLine(TextWriter writer, object value) =>
            writer.WriteLine(JsonSerializer.Serialize(value, s_jsonOptions));

        private static (int Profiles, int Frames, long Events) ValidateExport(string path, bool allowEmpty = false)
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            int frameCount = document.RootElement.GetProperty("shared").GetProperty("frames").GetArrayLength();
            var profiles = document.RootElement.GetProperty("profiles");
            if (profiles.GetArrayLength() == 0 && !allowEmpty)
            {
                throw new InvalidDataException("No exported profiles.");
            }
            long eventCount = 0;
            foreach (var profile in profiles.EnumerateArray())
            {
                var stack = new Stack<int>();
                double start = profile.GetProperty("startValue").GetDouble();
                double end = profile.GetProperty("endValue").GetDouble();
                double previous = start;
                if (!double.IsFinite(start) || !double.IsFinite(end) || end < start)
                {
                    throw new InvalidDataException("Invalid profile range.");
                }
                foreach (var item in profile.GetProperty("events").EnumerateArray())
                {
                    eventCount++;
                    int frame = item.GetProperty("frame").GetInt32();
                    double at = item.GetProperty("at").GetDouble();
                    if (frame < 0 || frame >= frameCount || !double.IsFinite(at) || at < previous || at > end)
                    {
                        throw new InvalidDataException("Invalid exported event.");
                    }
                    previous = at;
                    string type = item.GetProperty("type").GetString();
                    if (type == "O")
                    {
                        stack.Push(frame);
                    }
                    else if (type != "C" || stack.Count == 0 || stack.Pop() != frame)
                    {
                        throw new InvalidDataException("Unbalanced exported stack.");
                    }
                }
                if (stack.Count != 0)
                {
                    throw new InvalidDataException("Unclosed exported frames.");
                }
            }
            return (profiles.GetArrayLength(), frameCount, eventCount);
        }

        private static readonly JsonSerializerOptions s_jsonOptions = new JsonSerializerOptions { IncludeFields = true };
        private static readonly (string Name, bool EnableAsyncCallStacks, bool EnableNormalization, bool EnableInfrastructureCleanup)[] s_views =
        {
            ("raw", false, false, false),
            ("structural", true, false, false),
            ("default", true, true, false),
            ("machinery", true, true, true)
        };

        #endregion
    }
}
