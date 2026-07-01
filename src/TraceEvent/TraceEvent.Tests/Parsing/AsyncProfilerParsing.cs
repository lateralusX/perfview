// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

using Xunit;

namespace TraceEventTests
{
    /// <summary>
    /// Unit tests for <see cref="AsyncProfilerTraceEventParser.ParseBuffer(byte[], IAsyncProfilerSubEventSink)"/>. Each test synthesizes an
    /// <c>AsyncEvents</c> buffer in the exact runtime wire format (see <see cref="AsyncProfilerBufferBuilder"/>)
    /// and asserts the decoded sub-events. These exercise the pure decoder without a live TraceEventSource.
    /// </summary>
    public class AsyncProfilerParsing
    {
        private const uint AsyncThreadContextId = AsyncProfilerTestData.DefaultAsyncThreadContextId;
        private const ulong OsThreadId = AsyncProfilerTestData.DefaultOsThreadId;
        private const long StartQpc = AsyncProfilerTestData.DefaultStartQpc;

        [Fact]
        public void Header_TooShort_ReportsParseError()
        {
            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(new byte[8], sink);

            Assert.Single(sink.Errors);
            Assert.Empty(sink.Order);
        }

        [Fact]
        public void Header_UnsupportedVersion_ReportsParseError()
        {
            var buffer = new AsyncProfilerBufferBuilder().Build();
            buffer[0] = 2; // corrupt the version

            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(buffer, sink);

            Assert.Single(sink.Errors);
        }

        [Fact]
        public void EmptyBuffer_NoSubEvents_NoErrors()
        {
            var buffer = new AsyncProfilerBufferBuilder().Build();

            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(buffer, sink);

            Assert.Empty(sink.Order);
            Assert.Empty(sink.Errors);
        }

        [Theory]
        [InlineData(false)] // V2 RuntimeAsync
        [InlineData(true)]  // V1 StateMachineAsync
        public void ContextLifecycle_DecodesAllTransitions(bool stateMachine)
        {
            AsyncEventID create = stateMachine ? AsyncEventID.CreateStateMachineAsyncContext : AsyncEventID.CreateRuntimeAsyncContext;
            AsyncEventID resume = stateMachine ? AsyncEventID.ResumeStateMachineAsyncContext : AsyncEventID.ResumeRuntimeAsyncContext;
            AsyncEventID suspend = stateMachine ? AsyncEventID.SuspendStateMachineAsyncContext : AsyncEventID.SuspendRuntimeAsyncContext;
            AsyncEventID complete = stateMachine ? AsyncEventID.CompleteStateMachineAsyncContext : AsyncEventID.CompleteRuntimeAsyncContext;

            const ulong parent = 0x10, dispatcher = 0x20;

            var buffer = new AsyncProfilerBufferBuilder()
                .CreateContext(create, StartQpc + 10, parent, dispatcher)
                .ResumeContext(resume, StartQpc + 20, dispatcher)
                .ContextNoPayload(suspend, StartQpc + 30)
                .ContextNoPayload(complete, StartQpc + 40)
                .Build();

            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(buffer, sink);

            Assert.Empty(sink.Errors);
            Assert.Equal(new[] { create, resume, suspend, complete }, sink.Order);

            AsyncContextEvent created = Assert.Single(sink.ContextCreates);
            Assert.Equal(parent, created.ParentDispatcherId);
            Assert.Equal(dispatcher, created.DispatcherId);
            Assert.Equal(stateMachine, created.IsStateMachine);
            Assert.Equal(OsThreadId, created.OsThreadId);
            Assert.Equal(AsyncThreadContextId, created.AsyncThreadContextId);

            Assert.Equal(dispatcher, Assert.Single(sink.ContextResumes).DispatcherId);
            Assert.Single(sink.ContextSuspends);
            Assert.Single(sink.ContextCompletes);
        }

        [Fact]
        public void TimestampDeltas_AccumulateFromStart()
        {
            var buffer = new AsyncProfilerBufferBuilder()
                .Method(AsyncEventID.ResumeRuntimeAsyncMethod, StartQpc + 5)
                .Method(AsyncEventID.CompleteRuntimeAsyncMethod, StartQpc + 12)
                .Method(AsyncEventID.ResumeStateMachineAsyncMethod, StartQpc + 100)
                .Build();

            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(buffer, sink);

            Assert.Empty(sink.Errors);
            Assert.Equal(new long[] { StartQpc + 5, StartQpc + 12, StartQpc + 100 },
                sink.Methods.ConvertAll(m => m.TimestampQpc).ToArray());
        }

        [Fact]
        public void RuntimeCallstack_MultiFrame_ReconstructsDeltaIps_NoState()
        {
            ulong[] ips = { 0x7FFF_0000_1000, 0x7FFF_0000_1800, 0x7FFF_0000_0900 };

            var buffer = new AsyncProfilerBufferBuilder()
                .Callstack(AsyncEventID.CreateRuntimeAsyncCallstack, StartQpc + 1, continuationIndex: 3,
                    parentDispatcherId: 0x99, dispatcherId: 0xAA, ips, states: null)
                .Build();

            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(buffer, sink);

            Assert.Empty(sink.Errors);
            AsyncCallstackEvent cs = Assert.Single(sink.Callstacks);
            Assert.Equal(AsyncCallstackKind.RuntimeAsync, cs.Kind);
            Assert.False(cs.IsCached);
            Assert.Equal(3, cs.ContinuationIndex);
            Assert.Equal((byte)ips.Length, cs.FrameCount);
            Assert.Equal(0x99UL, cs.ParentDispatcherId);
            Assert.Equal(0xAAUL, cs.DispatcherId);
            Assert.Equal(ips, cs.MethodIds);
            Assert.Null(cs.FrameStates);
        }

        [Fact]
        public void RuntimeResumeCallstack_HasNoParentDispatcher()
        {
            ulong[] ips = { 0x4000, 0x4100 };

            var buffer = new AsyncProfilerBufferBuilder()
                .Callstack(AsyncEventID.ResumeRuntimeAsyncCallstack, StartQpc + 1, continuationIndex: 0,
                    parentDispatcherId: 0 /* not emitted for non-Create */, dispatcherId: 0x55, ips, states: null)
                .Build();

            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(buffer, sink);

            Assert.Empty(sink.Errors);
            AsyncCallstackEvent cs = Assert.Single(sink.Callstacks);
            Assert.Equal(0UL, cs.ParentDispatcherId);
            Assert.Equal(0x55UL, cs.DispatcherId);
            Assert.Equal(ips, cs.MethodIds);
        }

        [Fact]
        public void StateMachineCallstack_CarriesPerFrameState()
        {
            ulong[] handles = { 0x8000, 0x8200, 0x8100 };
            int[] states = { 0, 2, -1 };

            var buffer = new AsyncProfilerBufferBuilder()
                .Callstack(AsyncEventID.ResumeStateMachineAsyncCallstack, StartQpc + 1, continuationIndex: 7,
                    parentDispatcherId: 0, dispatcherId: 0x77, handles, states)
                .Build();

            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(buffer, sink);

            Assert.Empty(sink.Errors);
            AsyncCallstackEvent cs = Assert.Single(sink.Callstacks);
            Assert.Equal(AsyncCallstackKind.StateMachineAsync, cs.Kind);
            Assert.Equal(handles, cs.MethodIds);
            Assert.Equal(states, cs.FrameStates);
        }

        [Fact]
        public void CachedCallstack_HasNoFrames()
        {
            var buffer = new AsyncProfilerBufferBuilder()
                .Callstack(AsyncEventID.ResumeRuntimeAsyncCallstack, StartQpc + 1, continuationIndex: 1,
                    parentDispatcherId: 0, dispatcherId: 0xC0FFEE, methodIds: Array.Empty<ulong>(), states: null)
                .Build();

            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(buffer, sink);

            Assert.Empty(sink.Errors);
            AsyncCallstackEvent cs = Assert.Single(sink.Callstacks);
            Assert.True(cs.IsCached);
            Assert.Equal(0, cs.FrameCount);
            Assert.Empty(cs.MethodIds);
            Assert.Equal(0xC0FFEEUL, cs.DispatcherId);
        }

        [Fact]
        public void Unwind_DecodesFrameCount()
        {
            var buffer = new AsyncProfilerBufferBuilder()
                .Unwind(AsyncEventID.UnwindStateMachineAsyncException, StartQpc + 2, unwoundFrames: 42)
                .Build();

            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(buffer, sink);

            Assert.Empty(sink.Errors);
            Assert.Equal(42u, Assert.Single(sink.Unwinds).UnwoundFrameCount);
        }

        [Fact]
        public void Metadata_DecodesClockAndManifest()
        {
            var manifest = new[]
            {
                new AsyncManifestEntry(AsyncEventID.CreateRuntimeAsyncContext, 1, PayloadLengthFieldSize.Byte),
                new AsyncManifestEntry(AsyncEventID.AsyncProfilerMetadata, 1, PayloadLengthFieldSize.UShort),
            };
            long utcFileTime = new DateTime(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();

            var buffer = new AsyncProfilerBufferBuilder()
                .Metadata(StartQpc + 3, qpcFrequency: 10_000_000, qpcSync: 5_000, utcSync: (ulong)utcFileTime,
                    eventBufferSize: 4096, wrapperCount: 32, manifest)
                .Build();

            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(buffer, sink);

            Assert.Empty(sink.Errors);
            AsyncMetadataEvent md = Assert.Single(sink.Metadatas);
            Assert.Equal(10_000_000UL, md.QpcFrequency);
            Assert.Equal(5_000UL, md.QpcSync);
            Assert.Equal((ulong)utcFileTime, md.UtcSync);
            Assert.Equal(4096u, md.EventBufferSize);
            Assert.Equal((byte)32, md.WrapperCount);
            Assert.Equal(2, md.Manifest.Length);
            Assert.Equal(AsyncEventID.AsyncProfilerMetadata, md.Manifest[1].EventId);
            Assert.Equal(PayloadLengthFieldSize.UShort, md.Manifest[1].PayloadLengthFieldSize);
            Assert.Equal(new DateTime(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc), md.SyncUtc);
        }

        [Fact]
        public void SyncClock_DecodesQpcAndUtc()
        {
            var buffer = new AsyncProfilerBufferBuilder()
                .SyncClock(StartQpc + 4, qpcSync: 777, utcSync: 888)
                .Build();

            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(buffer, sink);

            Assert.Empty(sink.Errors);
            AsyncSyncClockEvent sc = Assert.Single(sink.SyncClocks);
            Assert.Equal(777UL, sc.QpcSync);
            Assert.Equal(888UL, sc.UtcSync);
        }

        [Fact]
        public void ResetEvents_Decode()
        {
            var buffer = new AsyncProfilerBufferBuilder()
                .ContextNoPayload(AsyncEventID.ResetAsyncThreadContext, StartQpc + 1)
                .ContextNoPayload(AsyncEventID.ResetAsyncContinuationWrapperIndex, StartQpc + 2)
                .Build();

            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(buffer, sink);

            Assert.Empty(sink.Errors);
            Assert.Single(sink.ResetThreadContexts);
            Assert.Single(sink.ResetWrapperIndexes);
        }

        [Fact]
        public void ForwardCompat_ExtraTrailingPayloadBytes_AreSkippedViaLengthPrefix()
        {
            // Simulate a newer runtime that appended fields to ResumeRuntimeAsyncContext: the known
            // dispatcher id is still readable, and the decoder resyncs to the next sub-event using the
            // authoritative payload-length prefix.
            var buffer = new AsyncProfilerBufferBuilder()
                .ResumeContextWithExtra(AsyncEventID.ResumeRuntimeAsyncContext, StartQpc + 1, dispatcher: 0x42, extraBytes: 3)
                .Method(AsyncEventID.CompleteRuntimeAsyncMethod, StartQpc + 2)
                .Build();

            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(buffer, sink);

            Assert.Empty(sink.Errors);
            Assert.Equal(new[] { AsyncEventID.ResumeRuntimeAsyncContext, AsyncEventID.CompleteRuntimeAsyncMethod }, sink.Order);
            Assert.Equal(0x42UL, Assert.Single(sink.ContextResumes).DispatcherId);
        }

        [Fact]
        public void UnknownEventIdWithoutPayload_ReportedAsUnknown()
        {
            var buffer = new AsyncProfilerBufferBuilder()
                .UnknownNoPayload(eventId: 250, StartQpc + 1)
                .Method(AsyncEventID.ResumeRuntimeAsyncMethod, StartQpc + 2)
                .Build();

            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(buffer, sink);

            Assert.Empty(sink.Errors);
            Assert.Single(sink.Unknowns);
            Assert.Equal((AsyncEventID)250, sink.Unknowns[0].EventId);
            Assert.Single(sink.Methods);
        }

        [Fact]
        public void Manifest_DrivesFramingForUnknownFutureEvent()
        {
            // Metadata advertises a future event id 24 with a UShort length prefix. The parser must use the
            // manifest to frame (and skip) the unknown sub-event, then continue to the following known one.
            // Without the manifest, id 24 defaults to no prefix and its payload would be mis-decoded.
            var manifestEntries = new[]
            {
                new AsyncManifestEntry((AsyncEventID)24, 1, PayloadLengthFieldSize.UShort),
            };
            var futurePayload = new byte[] { 1, 2, 3, 4, 5 };

            var buffer = new AsyncProfilerBufferBuilder()
                .Metadata(StartQpc + 1, qpcFrequency: 1, qpcSync: 1, utcSync: 1, eventBufferSize: 0, wrapperCount: 0, manifestEntries)
                .RawEvent(24, StartQpc + 2, PayloadLengthFieldSize.UShort, futurePayload)
                .Method(AsyncEventID.ResumeRuntimeAsyncMethod, StartQpc + 3)
                .Build();

            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(buffer, sink);

            Assert.Empty(sink.Errors);
            Assert.Single(sink.Metadatas);
            AsyncUnknownEvent unknown = Assert.Single(sink.Unknowns);
            Assert.Equal((AsyncEventID)24, unknown.EventId);
            Assert.Equal(futurePayload.Length, unknown.PayloadLength);
            Assert.Single(sink.Methods);
        }

        [Fact]
        public void Manifest_UpgradesKnownEventFieldSize_ReadsV1FieldsAndSkipsAppended()
        {
            // A newer runtime advertises ResumeRuntimeAsyncContext at version 2 with a UShort prefix (payload
            // grew past a byte). The parser reads the v1 dispatcher id it understands and skips the appended
            // bytes via the length prefix, then continues to the next sub-event.
            var entries = new[]
            {
                new AsyncManifestEntry(AsyncEventID.ResumeRuntimeAsyncContext, 2, PayloadLengthFieldSize.UShort),
            };
            // payload = cu64(dispatcher=0x42) + two appended (v2) bytes.
            var payload = new byte[] { 0x42, 0xAA, 0xBB };

            var buffer = new AsyncProfilerBufferBuilder()
                .Metadata(StartQpc + 1, qpcFrequency: 1, qpcSync: 1, utcSync: 1, eventBufferSize: 0, wrapperCount: 0, entries)
                .RawEvent((byte)AsyncEventID.ResumeRuntimeAsyncContext, StartQpc + 2, PayloadLengthFieldSize.UShort, payload)
                .Method(AsyncEventID.CompleteRuntimeAsyncMethod, StartQpc + 3)
                .Build();

            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(buffer, sink);

            Assert.Empty(sink.Errors);
            Assert.Equal(0x42UL, Assert.Single(sink.ContextResumes).DispatcherId);
            Assert.Single(sink.Methods);
        }

        [Fact]
        public void Manifest_PersistsAcrossBuffers()
        {
            // The runtime advertises the manifest once (its own buffer); a later data buffer must still be
            // framed with that manifest. A shared AsyncProfilerManifest carries the state across ParseBuffer calls.
            var manifest = new AsyncProfilerManifest();
            var entries = new[]
            {
                new AsyncManifestEntry((AsyncEventID)24, 3, PayloadLengthFieldSize.UShort),
            };

            var metadataBuffer = new AsyncProfilerBufferBuilder()
                .Metadata(StartQpc + 1, qpcFrequency: 1, qpcSync: 1, utcSync: 1, eventBufferSize: 0, wrapperCount: 0, entries)
                .Build();

            var dataBuffer = new AsyncProfilerBufferBuilder()
                .RawEvent(24, StartQpc + 2, PayloadLengthFieldSize.UShort, new byte[] { 9, 9, 9 })
                .Method(AsyncEventID.ResumeRuntimeAsyncMethod, StartQpc + 3)
                .Build();

            var sink = new CollectingSink();
            AsyncProfilerTraceEventParser.ParseBuffer(metadataBuffer, manifest, sink);
            AsyncProfilerTraceEventParser.ParseBuffer(dataBuffer, manifest, sink);

            Assert.Empty(sink.Errors);
            Assert.Equal(PayloadLengthFieldSize.UShort, manifest.GetPayloadLengthFieldSize((AsyncEventID)24));
            Assert.Equal((byte)3, manifest.GetVersion((AsyncEventID)24));
            Assert.Single(sink.Unknowns);   // id 24 in the data buffer was framed & skipped via the shared manifest
            Assert.Single(sink.Methods);
        }
    }
}
