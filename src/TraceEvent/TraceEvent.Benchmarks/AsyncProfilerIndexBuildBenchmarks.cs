using System;
using System.IO;

using BenchmarkDotNet.Attributes;

using FastSerialization;

using Microsoft.Diagnostics.Tracing.Computers;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

using TraceEventTests;

namespace TraceEventBenchmarks
{
    [MemoryDiagnoser]
    public class AsyncProfilerIndexBuildBenchmarks
    {
        private byte[] _buffer;
        private AsyncProfilerManifest _manifest;
        private CountingSink _sink;
        private AsyncCallStacksIndex _index;

        [Params(1_000, 10_000)]
        public int EpisodeCount { get; set; }

        [Params(4, 16)]
        public int FrameCount { get; set; }

        [Params(AsyncCallstackKind.RuntimeAsync, AsyncCallstackKind.StateMachineAsync)]
        public AsyncCallstackKind Kind { get; set; }

        [Params(false, true)]
        public bool UniqueCallstacks { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _buffer = BuildBuffer(EpisodeCount, FrameCount, Kind, UniqueCallstacks);
            _manifest = new AsyncProfilerManifest();
            _sink = new CountingSink();

            var computer = new AsyncProfilerComputer();
            computer.Process(_buffer, (ProcessIndex)1);
            computer.Finish();
            _index = computer.Index;
        }

        [Benchmark]
        public int Decode()
        {
            _sink.Reset();
            AsyncProfilerTraceEventParser.ParseBuffer(_buffer, _manifest, _sink);
            return _sink.EventCount;
        }

        [Benchmark]
        public int BuildIndex()
        {
            var computer = new AsyncProfilerComputer();
            computer.Process(_buffer, (ProcessIndex)1);
            computer.Finish();
            return computer.DistinctFramesCount;
        }

        [Benchmark]
        public long SerializeIndex()
        {
            SerializationSettings settings = SerializationSettings.Default.WithStreamLabelWidth(StreamLabelWidth.EightBytes);
            using (var stream = new MemoryStream())
            {
                using (var serializer = new Serializer(new IOStreamStreamWriter(stream, settings, leaveOpen: true), _index))
                {
                }

                return stream.Length;
            }
        }

        private static byte[] BuildBuffer(int episodeCount, int frameCount, AsyncCallstackKind kind, bool uniqueCallstacks)
        {
            const long startQpc = 1_000_000;
            var builder = new AsyncProfilerBufferBuilder(startQpc: startQpc)
                .Metadata(startQpc, 10_000_000, startQpc, 1, 0, 32, Array.Empty<AsyncManifestEntry>())
                .ContextNoPayload(AsyncEventID.ResetAsyncThreadContext, startQpc);
            var methodIds = new ulong[frameCount];
            var states = kind == AsyncCallstackKind.StateMachineAsync ? new int[frameCount] : null;
            long timestampQpc = startQpc;

            for (int episode = 0; episode < episodeCount; episode++)
            {
                ulong methodBase = uniqueCallstacks ? 0x1000UL + (ulong)episode * 0x100UL : 0x1000UL;
                for (int frame = 0; frame < frameCount; frame++)
                {
                    methodIds[frame] = methodBase + (ulong)frame * 0x10UL;
                    if (states != null)
                    {
                        states[frame] = frame & 3;
                    }
                }

                timestampQpc++;
                builder.Callstack(
                    kind == AsyncCallstackKind.RuntimeAsync
                        ? AsyncEventID.ResumeRuntimeAsyncCallstack
                        : AsyncEventID.ResumeStateMachineAsyncCallstack,
                    timestampQpc,
                    continuationIndex: (byte)(episode & 31),
                    parentDispatcherId: 0,
                    dispatcherId: (ulong)episode + 1,
                    methodIds,
                    states);

                timestampQpc++;
                builder.ContextNoPayload(
                    kind == AsyncCallstackKind.RuntimeAsync
                        ? AsyncEventID.SuspendRuntimeAsyncContext
                        : AsyncEventID.SuspendStateMachineAsyncContext,
                    timestampQpc);
            }

            return builder.Build();
        }

        private sealed class CountingSink : IAsyncProfilerSubEventSink
        {
            public int EventCount { get; private set; }

            public void Reset() => EventCount = 0;

            public void OnContextCreate(in AsyncContextEvent e) => EventCount++;
            public void OnContextResume(in AsyncContextEvent e) => EventCount++;
            public void OnContextSuspend(in AsyncContextEvent e) => EventCount++;
            public void OnContextComplete(in AsyncContextEvent e) => EventCount++;
            public void OnException(in AsyncUnwindEvent e) => EventCount++;
            public void OnCallstack(in AsyncCallstackEvent e) => EventCount += e.FrameCount + 1;
            public void OnMethodResume(in AsyncMethodEvent e) => EventCount++;
            public void OnMethodComplete(in AsyncMethodEvent e) => EventCount++;
            public void OnResetThreadContext(in AsyncNeutralEvent e) => EventCount++;
            public void OnResetContinuationWrapperIndex(in AsyncNeutralEvent e) => EventCount++;
            public void OnMetadata(in AsyncMetadataEvent e) => EventCount++;
            public void OnSyncClock(in AsyncSyncClockEvent e) => EventCount++;
            public void OnUnknown(in AsyncUnknownEvent e) => EventCount++;
            public void OnParseError(in AsyncProfilerParseError e) => EventCount++;
        }
    }

    [MemoryDiagnoser]
    public class AsyncProfilerStateMachineAppendBenchmarks
    {
        private byte[] _buffer;

        [Params(1_000, 10_000)]
        public int EpisodeCount { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            const long startQpc = 1_000_000;
            var builder = new AsyncProfilerBufferBuilder(startQpc: startQpc)
                .Metadata(startQpc, 10_000_000, startQpc, 1, 0, 32, Array.Empty<AsyncManifestEntry>())
                .ContextNoPayload(AsyncEventID.ResetAsyncThreadContext, startQpc);
            long timestampQpc = startQpc;

            for (int episode = 0; episode < EpisodeCount; episode++)
            {
                ulong dispatcherId = (ulong)episode + 1;
                for (int chunk = 0; chunk < 4; chunk++)
                {
                    var methods = new ulong[4];
                    var states = new int[4];
                    for (int frame = 0; frame < methods.Length; frame++)
                    {
                        methods[frame] = 0x1000UL + (ulong)(chunk * methods.Length + frame) * 0x10UL;
                        states[frame] = frame & 3;
                    }

                    timestampQpc++;
                    builder.Callstack(
                        chunk == 0
                            ? AsyncEventID.ResumeStateMachineAsyncCallstack
                            : AsyncEventID.AppendStateMachineAsyncCallstack,
                        timestampQpc,
                        continuationIndex: (byte)(episode & 31),
                        parentDispatcherId: 0,
                        dispatcherId,
                        methods,
                        states);
                }

                timestampQpc++;
                builder.ContextNoPayload(AsyncEventID.SuspendStateMachineAsyncContext, timestampQpc);
            }

            _buffer = builder.Build();
        }

        [Benchmark]
        public int BuildIndex()
        {
            var computer = new AsyncProfilerComputer();
            computer.Process(_buffer, (ProcessIndex)1);
            computer.Finish();
            return computer.DistinctFramesCount;
        }
    }
}
