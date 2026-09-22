using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

using BenchmarkDotNet.Attributes;

using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;
using Microsoft.Diagnostics.Tracing.Stacks;

using TraceEventTests;

namespace TraceEventBenchmarks
{
    public enum AsyncProfilerKindMode
    {
        V2Only,
        V1Only,
        Mixed,
    }

    public enum AsyncProfilerWorkloadProfile
    {
        Realistic,
        High,
        Extreme,
    }

    [MemoryDiagnoser]
    public class AsyncProfilerEndToEndBenchmarks
    {
        private AsyncProfilerEndToEndFixture _fixture;
        private TraceLog _traceLog;
        private TraceLog _controlTraceLog;
        private SymbolReader _symbolReader;

        [Params(AsyncProfilerKindMode.V2Only, AsyncProfilerKindMode.V1Only, AsyncProfilerKindMode.Mixed)]
        public AsyncProfilerKindMode KindMode { get; set; }

        [Params(
            AsyncProfilerWorkloadProfile.Realistic,
            AsyncProfilerWorkloadProfile.High,
            AsyncProfilerWorkloadProfile.Extreme)]
        public AsyncProfilerWorkloadProfile WorkloadProfile { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _fixture = AsyncProfilerEndToEndFixture.Create(WorkloadProfile, KindMode);
            _fixture.WriteNettrace(_fixture.NetTracePath, includeAsyncProfilerData: true);
            _fixture.WriteNettrace(_fixture.ControlNetTracePath, includeAsyncProfilerData: false);
            TraceLog.CreateFromEventPipeDataFile(_fixture.NetTracePath, _fixture.EtlxPath);
            TraceLog.CreateFromEventPipeDataFile(_fixture.ControlNetTracePath, _fixture.ControlEtlxPath);
            TraceLog.CreateFromEventPipeDataFile(
                _fixture.NetTracePath,
                _fixture.PreservedBuffersEtlxPath,
                new TraceLogOptions { KeepAsyncProfilerEvents = true });

            _traceLog = new TraceLog(_fixture.EtlxPath);
            _controlTraceLog = new TraceLog(_fixture.ControlEtlxPath);
            _symbolReader = new SymbolReader(TextWriter.Null);
            AsyncIndexMemoryUsage memory = MeasureAsyncIndexMemory(_traceLog);
            ReportAsyncIndexMemory(memory, _fixture.ContextCount);
            _fixture.Validate(_traceLog, _controlTraceLog, _symbolReader);
            ReportAsyncIndexMemoryAfterUse(memory.ManagedHeapBefore, _fixture.ContextCount);
            ReportAsyncIndexMemoryAfterRelease(_traceLog, _fixture.ContextCount);
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _traceLog?.Dispose();
            _controlTraceLog?.Dispose();
            _symbolReader?.Dispose();
            _fixture?.Dispose();
        }

        [Benchmark(OperationsPerInvoke = 3)]
        public long NetTraceToEtlxWithoutAsync()
        {
            long length = 0;
            for (int i = 0; i < 3; i++)
            {
                TraceLog.CreateFromEventPipeDataFile(_fixture.ControlNetTracePath, _fixture.ControlConversionEtlxPath);
                length += new FileInfo(_fixture.ControlConversionEtlxPath).Length;
            }
            return length;
        }

        [Benchmark]
        public long NetTraceToEtlxWithAsync()
        {
            TraceLog.CreateFromEventPipeDataFile(_fixture.NetTracePath, _fixture.ConversionEtlxPath);
            return new FileInfo(_fixture.ConversionEtlxPath).Length;
        }

        [Benchmark]
        public long NetTraceToEtlxWithAsyncBuffers()
        {
            TraceLog.CreateFromEventPipeDataFile(
                _fixture.NetTracePath,
                _fixture.PreservedBuffersConversionEtlxPath,
                new TraceLogOptions { KeepAsyncProfilerEvents = true });
            return new FileInfo(_fixture.PreservedBuffersConversionEtlxPath).Length;
        }

        [Benchmark(OperationsPerInvoke = 128)]
        public int OpenEtlxWithoutAsync()
        {
            int result = 0;
            for (int i = 0; i < 128; i++)
            {
                using (var traceLog = new TraceLog(_fixture.ControlEtlxPath))
                {
                    result += traceLog.EventCount + (traceLog.AsyncCallStacks?.DistinctFramesCount ?? 0);
                }
            }
            return result;
        }

        [Benchmark(OperationsPerInvoke = 2)]
        public int OpenEtlxAndLoadAsyncIndex()
        {
            int result = 0;
            for (int i = 0; i < 2; i++)
            {
                using (var traceLog = new TraceLog(_fixture.EtlxPath))
                {
                    result += traceLog.EventCount + traceLog.AsyncCallStacks.DistinctFramesCount;
                }
            }
            return result;
        }

        [Benchmark(OperationsPerInvoke = 6)]
        public int GenerateCpuStacksWithoutAsync()
        {
            return GenerateCpuStacks(_controlTraceLog, stitchAsyncCallStacks: false, traceCount: 6);
        }

        [Benchmark(OperationsPerInvoke = 6)]
        public int GenerateCpuStacksWithAsyncIndex()
        {
            return GenerateCpuStacks(_traceLog, stitchAsyncCallStacks: false, traceCount: 6);
        }

        [Benchmark(OperationsPerInvoke = 6)]
        public int GenerateStitchedCpuStacks()
        {
            return GenerateCpuStacks(_traceLog, stitchAsyncCallStacks: true, traceCount: 6);
        }

        [Benchmark(OperationsPerInvoke = 6)]
        public int GenerateStitchedCpuStacksAndReleaseIndex()
        {
            return GenerateCpuStacks(
                _traceLog, stitchAsyncCallStacks: true, traceCount: 6, releaseAsyncCallStacks: true);
        }

        [Benchmark(OperationsPerInvoke = 2)]
        public int ReloadReleasedAsyncIndex()
        {
            int result = 0;
            for (int i = 0; i < 2; i++)
            {
                if (!_traceLog.ReleaseAsyncCallStacks())
                {
                    throw new InvalidOperationException("The async-callstack index was not loaded or reloadable.");
                }

                result += _traceLog.AsyncCallStacks.DistinctFramesCount;
            }
            return result;
        }

        private SampleProfilerThreadTimeComputer CreateComputer(
            TraceLog traceLog,
            bool stitchAsyncCallStacks)
        {
            return new SampleProfilerThreadTimeComputer(traceLog, _symbolReader, stitchAsyncCallStacks)
            {
                IncludeEventSourceEvents = false,
                GroupByStartStopActivity = false,
            };
        }

        private int GenerateCpuStacks(
            TraceLog traceLog,
            bool stitchAsyncCallStacks,
            int traceCount,
            bool releaseAsyncCallStacks = false)
        {
            int sampleCount = 0;
            for (int i = 0; i < traceCount; i++)
            {
                var stackSource = new MutableTraceEventStackSource(traceLog);
                var computer = CreateComputer(traceLog, stitchAsyncCallStacks);
                computer.GenerateThreadTimeStacks(stackSource);
                sampleCount += CountSamples(stackSource);
                if (releaseAsyncCallStacks && !traceLog.ReleaseAsyncCallStacks())
                {
                    throw new InvalidOperationException("The async-callstack index was not loaded or reloadable.");
                }
            }
            return sampleCount;
        }

        private static int CountSamples(MutableTraceEventStackSource stackSource)
        {
            int count = 0;
            stackSource.ForEach(_ => count++);
            return count;
        }

        private static AsyncIndexMemoryUsage MeasureAsyncIndexMemory(TraceLog traceLog)
        {
            using (Process process = Process.GetCurrentProcess())
            {
                CollectGarbage();
                long managedHeapBefore = GC.GetTotalMemory(forceFullCollection: false);
                process.Refresh();
                long privateBytesBefore = process.PrivateMemorySize64;
                long workingSetBefore = process.WorkingSet64;

                AsyncCallStacksIndex index = traceLog.AsyncCallStacks;
                if (index == null)
                {
                    throw new InvalidOperationException("The generated ETLX does not contain an async callstack index.");
                }

                CollectGarbage();
                long managedHeapAfter = GC.GetTotalMemory(forceFullCollection: false);
                process.Refresh();
                long privateBytesAfter = process.PrivateMemorySize64;
                long workingSetAfter = process.WorkingSet64;
                GC.KeepAlive(index);

                return new AsyncIndexMemoryUsage(
                    managedHeapBefore,
                    managedHeapAfter,
                    privateBytesBefore,
                    privateBytesAfter,
                    workingSetBefore,
                    workingSetAfter);
            }
        }

        private static void CollectGarbage()
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        private static void ReportAsyncIndexMemory(AsyncIndexMemoryUsage memory, int contextCount)
        {
            Console.WriteLine(
                $"Async index retained memory after full GC: " +
                $"managedHeap={memory.ManagedHeapBefore:N0}->{memory.ManagedHeapAfter:N0} bytes, " +
                $"delta={memory.ManagedHeapDelta:N0} ({memory.ManagedHeapDelta / (double)contextCount:F2}/context); " +
                $"processAfterLoad: privateBytes={memory.PrivateBytesAfter:N0} bytes, " +
                $"workingSet={memory.WorkingSetAfter:N0} bytes " +
                $"(before load: privateBytes={memory.PrivateBytesBefore:N0}, workingSet={memory.WorkingSetBefore:N0}).");
        }

        private static void ReportAsyncIndexMemoryAfterUse(long managedHeapBefore, int contextCount)
        {
            CollectGarbage();
            long managedHeapAfterUse = GC.GetTotalMemory(forceFullCollection: false);
            long delta = managedHeapAfterUse - managedHeapBefore;
            Console.WriteLine(
                $"Async index retained memory after full validation: managedHeapDelta={delta:N0} bytes " +
                $"({delta / (double)contextCount:F2}/context).");
        }

        private static void ReportAsyncIndexMemoryAfterRelease(TraceLog traceLog, int contextCount)
        {
            CollectGarbage();
            long managedHeapBefore = GC.GetTotalMemory(forceFullCollection: false);

            if (!traceLog.ReleaseAsyncCallStacks())
            {
                throw new InvalidOperationException("The async-callstack index was not loaded or reloadable.");
            }

            CollectGarbage();
            long managedHeapAfter = GC.GetTotalMemory(forceFullCollection: false);
            long reclaimed = managedHeapBefore - managedHeapAfter;
            Console.WriteLine(
                $"Async index memory after release: managedHeap={managedHeapBefore:N0}->{managedHeapAfter:N0} bytes, " +
                $"reclaimed={reclaimed:N0} ({reclaimed / (double)contextCount:F2}/context).");

            AsyncCallStacksIndex reloaded = traceLog.AsyncCallStacks;
            GC.KeepAlive(reloaded);
        }

        private readonly struct AsyncIndexMemoryUsage
        {
            public AsyncIndexMemoryUsage(
                long managedHeapBefore,
                long managedHeapAfter,
                long privateBytesBefore,
                long privateBytesAfter,
                long workingSetBefore,
                long workingSetAfter)
            {
                ManagedHeapBefore = managedHeapBefore;
                ManagedHeapAfter = managedHeapAfter;
                PrivateBytesBefore = privateBytesBefore;
                PrivateBytesAfter = privateBytesAfter;
                WorkingSetBefore = workingSetBefore;
                WorkingSetAfter = workingSetAfter;
            }

            public long ManagedHeapBefore { get; }
            public long ManagedHeapAfter { get; }
            public long ManagedHeapDelta => ManagedHeapAfter - ManagedHeapBefore;
            public long PrivateBytesBefore { get; }
            public long PrivateBytesAfter { get; }
            public long WorkingSetBefore { get; }
            public long WorkingSetAfter { get; }
        }
    }

    internal sealed class AsyncProfilerEndToEndFixture : IDisposable
    {
        private const int ProcessId = 42;
        private const int OsThreadId = 43;
        private const long ThreadStreamIndex = 1;
        private const long QpcFrequency = 10_000_000;
        private const long StartQpc = 1_000_000;
        private const int ContextsPerBuffer = 1_000;
        private const int UnmatchedSamplePeriod = 20;
        private const int ManagedMethodCount = 1_000;
        private const int AsyncFrameCount = 8;
        private const int DistinctAsyncStackCount = 17;
        private const int V1MethodIndexOffset = 200;
        private const int UnmatchedMethodIndex = 900;
        private const int V1StackIdOffset = DistinctAsyncStackCount;
        private const int UnmatchedStackId = (DistinctAsyncStackCount * 2) + 1;
        private const ulong AppMappingStart = 0x100000;
        private const ulong RuntimeMappingStart = 0x200000;
        private const ulong AddressStride = 0x100;
        private const ulong AppMappingId = 1;
        private const ulong RuntimeMappingId = 2;
        private const ulong WrapperAddress = RuntimeMappingStart;
        private const ulong DispatchAddress = RuntimeMappingStart + AddressStride;
        private const ulong V1DispatcherAddress = RuntimeMappingStart + (2 * AddressStride);
        private const ulong V1InfrastructureAddress = RuntimeMappingStart + (3 * AddressStride);
        private const string WrapperName =
            "System.Runtime.CompilerServices.AsyncProfiler+ContinuationWrapper.Continuation_Wrapper_0";
        private const string DispatchName =
            "System.Runtime.CompilerServices.RuntimeAsyncTask.DispatchContinuations";

        private static readonly Guid s_universalSystemProviderGuid =
            new Guid("8c107b6c-79f8-5231-4de6-2a0e20a3f562");

        private readonly int _contextRatePerSecond;
        private readonly int _durationSeconds;

        private AsyncProfilerEndToEndFixture(
            string directory,
            AsyncProfilerWorkloadProfile workloadProfile,
            AsyncProfilerKindMode kindMode,
            int contextRatePerSecond,
            int durationSeconds)
        {
            WorkloadProfile = workloadProfile;
            KindMode = kindMode;
            _contextRatePerSecond = contextRatePerSecond;
            _durationSeconds = durationSeconds;
            Directory = directory;
            string profile = workloadProfile.ToString().ToLowerInvariant();
            string mode = kindMode.ToString().ToLowerInvariant();
            string name = "async-" + profile + "-" + mode;
            NetTracePath = Path.Combine(directory, name + ".nettrace");
            ControlNetTracePath = Path.Combine(directory, name + "-control.nettrace");
            EtlxPath = Path.Combine(directory, name + ".etlx");
            ControlEtlxPath = Path.Combine(directory, name + "-control.etlx");
            PreservedBuffersEtlxPath = Path.Combine(directory, name + "-preserved.etlx");
            ConversionEtlxPath = Path.Combine(directory, name + "-conversion.etlx");
            ControlConversionEtlxPath = Path.Combine(directory, name + "-control-conversion.etlx");
            PreservedBuffersConversionEtlxPath =
                Path.Combine(directory, name + "-preserved-conversion.etlx");
        }

        public AsyncProfilerWorkloadProfile WorkloadProfile { get; }
        public AsyncProfilerKindMode KindMode { get; }
        public string Directory { get; }
        public string NetTracePath { get; }
        public string ControlNetTracePath { get; }
        public string EtlxPath { get; }
        public string ControlEtlxPath { get; }
        public string PreservedBuffersEtlxPath { get; }
        public string ConversionEtlxPath { get; }
        public string ControlConversionEtlxPath { get; }
        public string PreservedBuffersConversionEtlxPath { get; }

        public int ContextCount => _contextRatePerSecond * _durationSeconds;
        private int CpuSampleCount => _durationSeconds * 1_000;
        private int UnmatchedSampleCount => CpuSampleCount / UnmatchedSamplePeriod;
        private int MatchedSampleCount => CpuSampleCount - UnmatchedSampleCount;
        private int ContextsPerSample => _contextRatePerSecond / 1_000;

        public static AsyncProfilerEndToEndFixture Create(
            AsyncProfilerWorkloadProfile workloadProfile,
            AsyncProfilerKindMode kindMode)
        {
            int contextRatePerSecond;
            int durationSeconds;
            switch (workloadProfile)
            {
                case AsyncProfilerWorkloadProfile.Realistic:
                    contextRatePerSecond = 10_000;
                    durationSeconds = 30;
                    break;
                case AsyncProfilerWorkloadProfile.High:
                    contextRatePerSecond = 100_000;
                    durationSeconds = 30;
                    break;
                case AsyncProfilerWorkloadProfile.Extreme:
                    contextRatePerSecond = 1_000_000;
                    durationSeconds = 10;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(workloadProfile));
            }
            string directory = Path.Combine(Path.GetTempPath(), "TraceEventAsyncBenchmark_" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            return new AsyncProfilerEndToEndFixture(
                directory,
                workloadProfile,
                kindMode,
                contextRatePerSecond,
                durationSeconds);
        }

        public void WriteNettrace(string path, bool includeAsyncProfilerData)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var writer = new BenchmarkEventPipeWriterV6(stream, QpcFrequency);
                writer.WriteHeaders();
                writer.WriteMetadataBlock(
                new EventMetadata(
                    1,
                    AsyncProfilerTraceEventParser.ProviderName,
                    "AsyncEvents",
                    AsyncProfilerTraceEventParser.AsyncEventsEventId)
                {
                    ProviderId = AsyncProfilerTraceEventParser.ProviderGuid,
                },
                new EventMetadata(
                    2,
                    SampleProfilerTraceEventParser.ProviderName,
                    "Sample",
                    0,
                    new MetadataParameter("Type", MetadataTypeCode.Int32))
                {
                    ProviderId = SampleProfilerTraceEventParser.ProviderGuid,
                },
                new EventMetadata(3, "Universal.System", "ProcessMapping", 3)
                {
                    ProviderId = s_universalSystemProviderGuid,
                },
                new EventMetadata(4, "Universal.System", "ProcessSymbol", 4)
                {
                    ProviderId = s_universalSystemProviderGuid,
                });

                writer.WriteThreadBlock(w => w.WriteThreadEntry(ThreadStreamIndex, OsThreadId, ProcessId));
                writer.WriteStackBlock(1, (DistinctAsyncStackCount * 2) + 1, w =>
                {
                    for (int stackIndex = 0; stackIndex < DistinctAsyncStackCount; stackIndex++)
                    {
                        WriteStack(
                            w,
                            MethodAddress(FirstMethodIndex(AsyncCallstackKind.RuntimeAsync, stackIndex)),
                            WrapperAddress,
                            DispatchAddress);
                    }
                    for (int stackIndex = 0; stackIndex < DistinctAsyncStackCount; stackIndex++)
                    {
                        WriteStack(
                            w,
                            MethodAddress(FirstMethodIndex(AsyncCallstackKind.StateMachineAsync, stackIndex)),
                            V1DispatcherAddress,
                            V1InfrastructureAddress);
                    }
                    WriteStack(w, MethodAddress(UnmatchedMethodIndex), WrapperAddress, DispatchAddress);
                });

                int sequence = WriteSymbols(writer);
                WriteWorkload(writer, sequence, includeAsyncProfilerData);
                writer.WriteEndBlock();
                writer.Flush();
            }
        }

        public void Validate(TraceLog traceLog, TraceLog controlTraceLog, SymbolReader symbolReader)
        {
            ValidateRawInput();

            Require(controlTraceLog.AsyncCallStacks == null,
                "The no-async control ETLX unexpectedly contains an async callstack index.");
            ValidatePreservedBuffersEtlx();
            Require(CountAsyncBuffers(traceLog) == 0,
                "The default ETLX unexpectedly retained raw async-profiler buffers.");

            AsyncCallStacksIndex index = traceLog.AsyncCallStacks;
            if (index == null)
            {
                throw new InvalidOperationException("The generated ETLX does not contain an async callstack index.");
            }

            int intervalCount = 0;
            int threadCount = 0;
            foreach (AsyncThreadKey thread in index.Threads)
            {
                threadCount++;
                intervalCount += index.GetAsyncCallStacks(thread).Count;
            }
            Require(threadCount == 1, $"Expected one async thread, found {threadCount}.");
            Require(intervalCount == ContextCount, $"Expected {ContextCount} indexed contexts, found {intervalCount}.");
            int expectedDistinctFrames = KindMode == AsyncProfilerKindMode.Mixed
                ? DistinctAsyncStackCount * 2
                : DistinctAsyncStackCount;
            Require(index.DistinctFramesCount == expectedDistinctFrames,
                $"Expected {expectedDistinctFrames} shared async frame sequences, found {index.DistinctFramesCount}.");

            var controlStackSource = new MutableTraceEventStackSource(controlTraceLog);
            CreateComputer(controlTraceLog, symbolReader, stitchAsyncCallStacks: false)
                .GenerateThreadTimeStacks(controlStackSource);
            ValidateStacks(controlStackSource, StackValidationMode.Sync);

            var syncStackSource = new MutableTraceEventStackSource(traceLog);
            CreateComputer(traceLog, symbolReader, stitchAsyncCallStacks: false)
                .GenerateThreadTimeStacks(syncStackSource);
            ValidateStacks(syncStackSource, StackValidationMode.Sync);

            var stitchedStackSource = new MutableTraceEventStackSource(traceLog);
            SampleProfilerThreadTimeComputer stitchedComputer =
                CreateComputer(traceLog, symbolReader, stitchAsyncCallStacks: true);
            stitchedComputer.GenerateThreadTimeStacks(stitchedStackSource);
            ValidateStacks(stitchedStackSource, StackValidationMode.Stitched);
            Require(stitchedComputer.AsyncStitchDiagnostics.SegmentsProcessed == MatchedSampleCount,
                $"Expected {MatchedSampleCount} matched samples, found " +
                $"{stitchedComputer.AsyncStitchDiagnostics.SegmentsProcessed}.");
            Require(stitchedComputer.AsyncStitchDiagnostics.BoundariesNotFound == 0,
                "A generated CPU sample did not contain the expected V2 continuation wrapper.");
            Require(stitchedComputer.AsyncStitchDiagnostics.AdjacencyMismatches == 0,
                "A generated CPU sample did not align with the expected current async method.");
            Require(stitchedComputer.AsyncStitchDiagnostics.V2SyncLayoutUsed == 0,
                "A matched generated CPU sample unexpectedly used the synchronous layout.");

            long asyncNettraceBytes = new FileInfo(NetTracePath).Length;
            long controlNettraceBytes = new FileInfo(ControlNetTracePath).Length;
            long asyncEtlxBytes = new FileInfo(EtlxPath).Length;
            long controlEtlxBytes = new FileInfo(ControlEtlxPath).Length;
            long preservedEtlxBytes = new FileInfo(PreservedBuffersEtlxPath).Length;
            Console.WriteLine(
                $"Async benchmark fixture: contexts={ContextCount:N0}, subEvents={(ContextCount * 2) + 2:N0}, " +
                $"samples={CpuSampleCount:N0}, matched={MatchedSampleCount:N0}, unmatched={UnmatchedSampleCount:N0}, " +
                $"nettraceDelta={asyncNettraceBytes - controlNettraceBytes:N0} bytes " +
                $"({(asyncNettraceBytes - controlNettraceBytes) / (double)ContextCount:F2}/context), " +
                $"indexOnlyEtlxDelta={asyncEtlxBytes - controlEtlxBytes:N0} bytes " +
                $"({(asyncEtlxBytes - controlEtlxBytes) / (double)ContextCount:F2}/context), " +
                $"preservedBufferEtlxDelta={preservedEtlxBytes - controlEtlxBytes:N0} bytes " +
                $"({(preservedEtlxBytes - controlEtlxBytes) / (double)ContextCount:F2}/context).");
        }

        public void Dispose()
        {
            DeleteIfExists(NetTracePath);
            DeleteIfExists(ControlNetTracePath);
            DeleteIfExists(EtlxPath);
            DeleteIfExists(ControlEtlxPath);
            DeleteIfExists(PreservedBuffersEtlxPath);
            DeleteIfExists(ConversionEtlxPath);
            DeleteIfExists(ControlConversionEtlxPath);
            DeleteIfExists(PreservedBuffersConversionEtlxPath);
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory);
            }
        }

        private static int WriteSymbols(EventPipeWriterV6 writer)
        {
            int sequence = 1;
            writer.WriteEventBlock(block =>
            {
                block.WriteEventBlob(EventOptions(3, sequence++, StartQpc - 3), w =>
                    WriteProcessMappingPayload(
                        w,
                        AppMappingId,
                        AppMappingStart,
                        AppMappingStart + ((ulong)ManagedMethodCount * AddressStride),
                        "TestApp"));
                block.WriteEventBlob(EventOptions(3, sequence++, StartQpc - 2), w =>
                    WriteProcessMappingPayload(
                        w,
                        RuntimeMappingId,
                        RuntimeMappingStart,
                        RuntimeMappingStart + (4 * AddressStride),
                        AsyncStitchBoundary.HostModuleName));

                ulong symbolId = 1;
                for (int methodIndex = 0; methodIndex < ManagedMethodCount; methodIndex++)
                {
                    int capturedMethodIndex = methodIndex;
                    ulong capturedSymbolId = symbolId++;
                    block.WriteEventBlob(EventOptions(4, sequence++, StartQpc - 1), w =>
                        WriteProcessSymbolPayload(
                            w,
                            capturedSymbolId,
                            AppMappingId,
                            MethodAddress(capturedMethodIndex),
                            SymbolName(capturedMethodIndex)));
                }

                block.WriteEventBlob(EventOptions(4, sequence++, StartQpc - 1), w =>
                    WriteProcessSymbolPayload(w, symbolId++, RuntimeMappingId, WrapperAddress, WrapperName));
                block.WriteEventBlob(EventOptions(4, sequence++, StartQpc - 1), w =>
                    WriteProcessSymbolPayload(w, symbolId++, RuntimeMappingId, DispatchAddress, DispatchName));
                block.WriteEventBlob(EventOptions(4, sequence++, StartQpc - 1), w =>
                    WriteProcessSymbolPayload(
                        w,
                        symbolId,
                        RuntimeMappingId,
                        V1DispatcherAddress,
                        "System.Runtime.CompilerServices.AsyncProfilerAsyncStateMachineBox.MoveNextAsDispatcher"));
                block.WriteEventBlob(EventOptions(4, sequence++, StartQpc - 1), w =>
                    WriteProcessSymbolPayload(
                        w,
                        symbolId,
                        RuntimeMappingId,
                        V1InfrastructureAddress,
                        "System.Runtime.CompilerServices.AsyncProfilerAsyncStateMachineBox.InstrumentedMoveNext"));
            });
            return sequence;
        }

        private void WriteWorkload(EventPipeWriterV6 writer, int sequence, bool includeAsyncProfilerData)
        {
            ulong[][] v2Frames = CreateAsyncFrames(AsyncCallstackKind.RuntimeAsync);
            ulong[][] v1Frames = CreateAsyncFrames(AsyncCallstackKind.StateMachineAsync);
            int[] v1States = new int[AsyncFrameCount];

            int contextIndex = 0;
            int sampleIndex = 0;
            int bufferCount = ContextCount / ContextsPerBuffer;
            for (int bufferIndex = 0; bufferIndex < bufferCount; bufferIndex++)
            {
                int firstContext = contextIndex;
                long bufferStartQpc = ContextStartQpc(firstContext);
                var asyncBuffer = new AsyncProfilerBufferBuilder(OsThreadId, 0x0BADF00D, bufferStartQpc);
                if (bufferIndex == 0)
                {
                    asyncBuffer.Metadata(
                        bufferStartQpc,
                        (ulong)QpcFrequency,
                        qpcSync: 1,
                        utcSync: 1,
                        eventBufferSize: 0,
                        wrapperCount: 32,
                        new AsyncManifestEntry[0]);
                    asyncBuffer.ContextNoPayload(AsyncEventID.ResetAsyncThreadContext, bufferStartQpc);
                }

                for (int i = 0; i < ContextsPerBuffer; i++, contextIndex++)
                {
                    long contextStartQpc = ContextStartQpc(contextIndex) + 1;
                    long contextEndQpc = ContextStartQpc(contextIndex + 1);
                    int stackIndex = AsyncStackIndex(contextIndex);
                    AsyncCallstackKind kind = KindForContext(contextIndex);
                    if (kind == AsyncCallstackKind.RuntimeAsync)
                    {
                        asyncBuffer.Callstack(
                            AsyncEventID.ResumeRuntimeAsyncCallstack,
                            contextStartQpc,
                            continuationIndex: 0,
                            parentDispatcherId: 0,
                            dispatcherId: (ulong)contextIndex + 1,
                            v2Frames[stackIndex],
                            states: null);
                        asyncBuffer.ContextNoPayload(AsyncEventID.SuspendRuntimeAsyncContext, contextEndQpc);
                    }
                    else
                    {
                        asyncBuffer.Callstack(
                            AsyncEventID.ResumeStateMachineAsyncCallstack,
                            contextStartQpc,
                            continuationIndex: 0,
                            parentDispatcherId: 0,
                            dispatcherId: (ulong)contextIndex + 1,
                            v1Frames[stackIndex],
                            v1States);
                        asyncBuffer.ContextNoPayload(AsyncEventID.SuspendStateMachineAsyncContext, contextEndQpc);
                    }
                }

                byte[] outerPayload = AsyncEventsPayload(asyncBuffer.Build());
                long bufferEndQpc = ContextStartQpc(contextIndex);
                writer.WriteEventBlock(block =>
                {
                    long nextBufferStartQpc = bufferEndQpc;
                    while (sampleIndex < CpuSampleCount)
                    {
                        bool unmatched = IsUnmatchedSample(sampleIndex);
                        long sampleQpc = StartQpc + ((long)sampleIndex * QpcFrequency / 1_000);
                        if (!unmatched)
                        {
                            sampleQpc++;
                        }
                        if (sampleQpc >= nextBufferStartQpc)
                        {
                            break;
                        }

                        int contextAtSample = sampleIndex * ContextsPerSample;
                        int stackId = unmatched
                            ? UnmatchedStackId
                            : StackId(KindForContext(contextAtSample), AsyncStackIndex(contextAtSample));
                        block.WriteEventBlob(EventOptions(2, sequence++, sampleQpc, stackId),
                            w => w.Write((int)ClrThreadSampleType.Managed));
                        sampleIndex++;
                    }
                    if (includeAsyncProfilerData)
                    {
                        block.WriteEventBlob(EventOptions(1, sequence++, bufferEndQpc), w => w.Write(outerPayload));
                    }
                });
            }

            writer.WriteEventBlock(block =>
            {
                long sentinelQpc = StartQpc + ((long)_durationSeconds * QpcFrequency);
                block.WriteEventBlob(EventOptions(2, sequence, sentinelQpc, stackId: 1),
                    w => w.Write((int)ClrThreadSampleType.Managed));
            });
        }

        private void ValidateRawInput()
        {
            var sink = new CountingSink();
            int outerBufferCount = 0;
            using (var source = new EventPipeEventSource(NetTracePath))
            {
                var parser = new AsyncProfilerTraceEventParser(source);
                var manifest = new AsyncProfilerManifest();
                parser.AsyncEvents += data =>
                {
                    outerBufferCount++;
                    AsyncProfilerTraceEventParser.ParseBuffer(data.Buffer, manifest, sink);
                };
                source.Process();
            }

            Require(outerBufferCount == ContextCount / ContextsPerBuffer,
                $"Expected {ContextCount / ContextsPerBuffer} outer async buffers, found {outerBufferCount}.");
            Require(sink.SubEventCount == (ContextCount * 2) + 2,
                $"Expected {(ContextCount * 2) + 2} logical async sub-events, found {sink.SubEventCount}.");
            int expectedRuntimeCount = KindMode == AsyncProfilerKindMode.V2Only
                ? ContextCount
                : KindMode == AsyncProfilerKindMode.V1Only ? 0 : ContextCount / 2;
            int expectedStateMachineCount = ContextCount - expectedRuntimeCount;
            Require(sink.RuntimeResumeCount == expectedRuntimeCount,
                $"Expected {expectedRuntimeCount} V2 resume callstacks, found {sink.RuntimeResumeCount}.");
            Require(sink.RuntimeSuspendCount == expectedRuntimeCount,
                $"Expected {expectedRuntimeCount} V2 suspends, found {sink.RuntimeSuspendCount}.");
            Require(sink.StateMachineResumeCount == expectedStateMachineCount,
                $"Expected {expectedStateMachineCount} V1 resume callstacks, found {sink.StateMachineResumeCount}.");
            Require(sink.StateMachineSuspendCount == expectedStateMachineCount,
                $"Expected {expectedStateMachineCount} V1 suspends, found {sink.StateMachineSuspendCount}.");
            Require(sink.ParseErrorCount == 0, $"The generated async buffers produced {sink.ParseErrorCount} parse errors.");
        }

        private void ValidatePreservedBuffersEtlx()
        {
            using (var traceLog = new TraceLog(PreservedBuffersEtlxPath))
            {
                int outerBufferCount = CountAsyncBuffers(traceLog);

                Require(outerBufferCount == ContextCount / ContextsPerBuffer,
                    $"Expected {ContextCount / ContextsPerBuffer} preserved async buffers, found {outerBufferCount}.");
                Require(traceLog.AsyncCallStacks != null,
                    "Preserving raw async buffers unexpectedly removed the derived async index.");
            }
        }

        private static int CountAsyncBuffers(TraceLog traceLog)
        {
            int count = 0;
            foreach (TraceEvent e in traceLog.Events)
            {
                if (e.ProviderGuid == AsyncProfilerTraceEventParser.ProviderGuid &&
                    e.ID == (TraceEventID)AsyncProfilerTraceEventParser.AsyncEventsEventId)
                {
                    count++;
                }
            }
            return count;
        }

        private static SampleProfilerThreadTimeComputer CreateComputer(
            TraceLog traceLog,
            SymbolReader symbolReader,
            bool stitchAsyncCallStacks)
        {
            return new SampleProfilerThreadTimeComputer(traceLog, symbolReader, stitchAsyncCallStacks)
            {
                IncludeEventSourceEvents = false,
                GroupByStartStopActivity = false,
            };
        }

        private void ValidateStacks(
            MutableTraceEventStackSource stackSource,
            StackValidationMode mode)
        {
            int sampleIndex = 0;
            int matchedCount = 0;
            int unmatchedCount = 0;
            stackSource.ForEach(sample =>
            {
                bool unmatched = IsUnmatchedSample(sampleIndex);
                int contextAtSample = sampleIndex * ContextsPerSample;
                int asyncStackIndex = AsyncStackIndex(contextAtSample);
                AsyncCallstackKind kind = KindForContext(contextAtSample);
                var actual = new List<string>();
                for (StackSourceCallStackIndex stack = sample.StackIndex;
                    stack != StackSourceCallStackIndex.Invalid;
                    stack = stackSource.GetCallerIndex(stack))
                {
                    string name = stackSource.GetFrameName(stackSource.GetFrameIndex(stack), false);
                    string scenarioFrame = GetScenarioFrame(name);
                    if (scenarioFrame != null)
                    {
                        actual.Add(scenarioFrame);
                    }
                }

                string[] expected;
                if (unmatched || mode == StackValidationMode.Sync)
                {
                    int methodIndex = unmatched
                        ? UnmatchedMethodIndex
                        : FirstMethodIndex(kind, asyncStackIndex);
                    expected = new[]
                    {
                        MethodName(methodIndex),
                        unmatched || kind == AsyncCallstackKind.RuntimeAsync
                            ? "Wrapper"
                            : AsyncStitchBoundary.MoveNextAsDispatcherName,
                        unmatched || kind == AsyncCallstackKind.RuntimeAsync
                            ? "DispatchContinuations"
                            : "V1Infrastructure",
                    };
                }
                else
                {
                    expected = new string[AsyncFrameCount];
                    int firstMethodIndex = FirstMethodIndex(kind, asyncStackIndex);
                    for (int frameIndex = 0; frameIndex < expected.Length; frameIndex++)
                    {
                        expected[frameIndex] = MethodName(firstMethodIndex + frameIndex);
                    }
                }

                Require(actual.Count == expected.Length,
                    $"Sample {sampleIndex} produced {actual.Count} scenario frames; expected {expected.Length}. " +
                    $"Actual: {string.Join(" -> ", actual)}.");
                for (int frameIndex = 0; frameIndex < expected.Length; frameIndex++)
                {
                    Require(actual[frameIndex] == expected[frameIndex],
                        $"Sample {sampleIndex}, frame {frameIndex}: expected '{expected[frameIndex]}', " +
                        $"found '{actual[frameIndex]}'. Full scenario stack: {string.Join(" -> ", actual)}.");
                }

                if (unmatched)
                {
                    unmatchedCount++;
                }
                else
                {
                    matchedCount++;
                }
                sampleIndex++;
            });

            Require(sampleIndex == CpuSampleCount,
                $"Expected {CpuSampleCount} CPU samples, found {sampleIndex}.");
            Require(matchedCount == MatchedSampleCount,
                $"Expected {MatchedSampleCount} matched samples, found {matchedCount}.");
            Require(unmatchedCount == UnmatchedSampleCount,
                $"Expected {UnmatchedSampleCount} unmatched samples, found {unmatchedCount}.");
        }

        private static string GetScenarioFrame(string frameName)
        {
            const string methodPrefix = "TestApp.Workload.Method";
            int methodStart = frameName.IndexOf(methodPrefix, StringComparison.Ordinal);
            if (methodStart >= 0)
            {
                int methodEnd = methodStart + methodPrefix.Length + 4;
                return frameName.Substring(methodStart, methodEnd - methodStart);
            }
            const string stateMachineMethodPrefix = "<Method";
            methodStart = frameName.IndexOf(stateMachineMethodPrefix, StringComparison.Ordinal);
            if (methodStart >= 0)
            {
                int digitsStart = methodStart + stateMachineMethodPrefix.Length;
                return "TestApp.Workload.Method" + frameName.Substring(digitsStart, 4);
            }
            if (frameName.IndexOf(AsyncStitchBoundary.ContinuationWrapperPrefix, StringComparison.Ordinal) >= 0)
            {
                return "Wrapper";
            }
            if (frameName.IndexOf(AsyncStitchBoundary.DispatchContinuationsName, StringComparison.Ordinal) >= 0)
            {
                return "DispatchContinuations";
            }
            if (frameName.IndexOf(AsyncStitchBoundary.MoveNextAsDispatcherName, StringComparison.Ordinal) >= 0)
            {
                return AsyncStitchBoundary.MoveNextAsDispatcherName;
            }
            if (frameName.IndexOf("InstrumentedMoveNext", StringComparison.Ordinal) >= 0)
            {
                return "V1Infrastructure";
            }
            return null;
        }

        private static WriteEventOptions EventOptions(int metadataId, int sequence, long timestamp, int stackId = 0)
        {
            return new WriteEventOptions
            {
                MetadataId = metadataId,
                ThreadIndexOrId = ThreadStreamIndex,
                CaptureThreadIndexOrId = ThreadStreamIndex,
                SequenceNumber = sequence,
                StackId = stackId,
                Timestamp = timestamp,
                IsSorted = true,
            };
        }

        private long ContextStartQpc(int contextIndex) =>
            StartQpc + ((long)contextIndex * QpcFrequency / _contextRatePerSecond);

        private static int AsyncStackIndex(int contextIndex) => contextIndex % DistinctAsyncStackCount;

        private static ulong[][] CreateAsyncFrames(AsyncCallstackKind kind)
        {
            var result = new ulong[DistinctAsyncStackCount][];
            for (int stackIndex = 0; stackIndex < result.Length; stackIndex++)
            {
                result[stackIndex] = new ulong[AsyncFrameCount];
                int firstMethodIndex = FirstMethodIndex(kind, stackIndex);
                for (int frameIndex = 0; frameIndex < AsyncFrameCount; frameIndex++)
                {
                    result[stackIndex][frameIndex] = MethodAddress(firstMethodIndex + frameIndex);
                }
            }
            return result;
        }

        private AsyncCallstackKind KindForContext(int contextIndex)
        {
            switch (KindMode)
            {
                case AsyncProfilerKindMode.V1Only:
                    return AsyncCallstackKind.StateMachineAsync;
                case AsyncProfilerKindMode.Mixed:
                    return ((contextIndex / ContextsPerSample) & 1) == 0
                        ? AsyncCallstackKind.RuntimeAsync
                        : AsyncCallstackKind.StateMachineAsync;
                default:
                    return AsyncCallstackKind.RuntimeAsync;
            }
        }

        private static int FirstMethodIndex(AsyncCallstackKind kind, int asyncStackIndex) =>
            (kind == AsyncCallstackKind.StateMachineAsync ? V1MethodIndexOffset : 0) +
            (asyncStackIndex * AsyncFrameCount);

        private static int StackId(AsyncCallstackKind kind, int asyncStackIndex) =>
            (kind == AsyncCallstackKind.StateMachineAsync ? V1StackIdOffset : 0) + asyncStackIndex + 1;

        private static bool IsUnmatchedSample(int sampleIndex) =>
            (sampleIndex + 1) % UnmatchedSamplePeriod == 0;

        private static string MethodName(int methodIndex) =>
            "TestApp.Workload.Method" + methodIndex.ToString("D4");

        private static string SymbolName(int methodIndex)
        {
            if (methodIndex >= V1MethodIndexOffset &&
                methodIndex < V1MethodIndexOffset + (DistinctAsyncStackCount * AsyncFrameCount))
            {
                return "TestApp.Workload+<Method" + methodIndex.ToString("D4") + ">d__" +
                    methodIndex.ToString() + ".MoveNext";
            }
            return MethodName(methodIndex);
        }

        private static ulong MethodAddress(int methodIndex) => AppMappingStart + ((ulong)methodIndex * AddressStride);

        private static byte[] AsyncEventsPayload(byte[] buffer)
        {
            byte[] payload = new byte[sizeof(int) + buffer.Length];
            Array.Copy(BitConverter.GetBytes(buffer.Length), 0, payload, 0, sizeof(int));
            Array.Copy(buffer, 0, payload, sizeof(int), buffer.Length);
            return payload;
        }

        private static void WriteStack(BinaryWriter writer, params ulong[] addresses)
        {
            writer.Write(addresses.Length * sizeof(ulong));
            for (int i = 0; i < addresses.Length; i++)
            {
                writer.Write(addresses[i]);
            }
        }

        private static void WriteProcessMappingPayload(
            BinaryWriter writer,
            ulong id,
            ulong startAddress,
            ulong endAddress,
            string fileName)
        {
            writer.WriteVarUInt(id);
            writer.WriteVarUInt(startAddress);
            writer.WriteVarUInt(endAddress);
            writer.WriteVarUInt(0);
            WriteShortUtf8String(writer, fileName);
            writer.WriteVarUInt(0);
        }

        private static void WriteProcessSymbolPayload(
            BinaryWriter writer,
            ulong id,
            ulong mappingId,
            ulong address,
            string name)
        {
            writer.WriteVarUInt(id);
            writer.WriteVarUInt(mappingId);
            writer.WriteVarUInt(address);
            writer.WriteVarUInt(address + 0xF);
            WriteShortUtf8String(writer, name);
        }

        private static void WriteShortUtf8String(BinaryWriter writer, string value)
        {
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(value);
            writer.Write((ushort)utf8.Length);
            writer.Write(utf8);
        }

        private static void DeleteIfExists(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private enum StackValidationMode
        {
            Sync,
            Stitched,
        }

        private sealed class BenchmarkEventPipeWriterV6 : EventPipeWriterV6
        {
            private readonly long _qpcFrequency;

            public BenchmarkEventPipeWriterV6(Stream stream, long qpcFrequency)
                : base(stream)
            {
                _qpcFrequency = qpcFrequency;
            }

            public override void WriteHeaders()
            {
                _writer.WriteNetTraceHeaderV6OrGreater(6, 0);
                _writer.WriteTraceBlockV6OrGreater(
                    new DateTime(2025, 2, 3, 4, 5, 6),
                    syncTimeQpc: 1,
                    qpcFrequency: _qpcFrequency,
                    pointerSize: 8,
                    new Dictionary<string, string>());
            }

            public void WriteStackBlock(int firstStackId, int count, Action<BinaryWriter> writeStacks)
            {
                WriteBlock(5, w =>
                {
                    w.Write(firstStackId);
                    w.Write(count);
                    writeStacks(w);
                });
            }
        }

        private sealed class CountingSink : IAsyncProfilerSubEventSink
        {
            public int SubEventCount { get; private set; }
            public int RuntimeResumeCount { get; private set; }
            public int RuntimeSuspendCount { get; private set; }
            public int StateMachineResumeCount { get; private set; }
            public int StateMachineSuspendCount { get; private set; }
            public int ParseErrorCount { get; private set; }

            public void OnContextCreate(in AsyncContextEvent e) => SubEventCount++;
            public void OnContextResume(in AsyncContextEvent e) => SubEventCount++;

            public void OnContextSuspend(in AsyncContextEvent e)
            {
                SubEventCount++;
                if (e.EventId == AsyncEventID.SuspendRuntimeAsyncContext)
                {
                    RuntimeSuspendCount++;
                }
                else if (e.EventId == AsyncEventID.SuspendStateMachineAsyncContext)
                {
                    StateMachineSuspendCount++;
                }
            }

            public void OnContextComplete(in AsyncContextEvent e) => SubEventCount++;
            public void OnException(in AsyncUnwindEvent e) => SubEventCount++;

            public void OnCallstack(in AsyncCallstackEvent e)
            {
                SubEventCount++;
                if (e.EventId == AsyncEventID.ResumeRuntimeAsyncCallstack)
                {
                    RuntimeResumeCount++;
                }
                else if (e.EventId == AsyncEventID.ResumeStateMachineAsyncCallstack)
                {
                    StateMachineResumeCount++;
                }
            }

            public void OnMethodResume(in AsyncMethodEvent e) => SubEventCount++;
            public void OnMethodComplete(in AsyncMethodEvent e) => SubEventCount++;
            public void OnResetThreadContext(in AsyncNeutralEvent e) => SubEventCount++;
            public void OnResetContinuationWrapperIndex(in AsyncNeutralEvent e) => SubEventCount++;
            public void OnMetadata(in AsyncMetadataEvent e) => SubEventCount++;
            public void OnSyncClock(in AsyncSyncClockEvent e) => SubEventCount++;
            public void OnUnknown(in AsyncUnknownEvent e) => SubEventCount++;

            public void OnParseError(in AsyncProfilerParseError e)
            {
                ParseErrorCount++;
            }
        }
    }
}
