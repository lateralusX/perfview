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
        private const uint AsyncThreadContextId = 0x0BADF00D;
        private const ulong OsThreadId = 0x1234;
        private const long StartQpc = 1_000_000;

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

        #region helpers

        /// <summary>A sink that records every decoded sub-event so tests can assert on them.</summary>
        private sealed class CollectingSink : IAsyncProfilerSubEventSink
        {
            public readonly List<AsyncEventID> Order = new List<AsyncEventID>();
            public readonly List<AsyncContextEvent> ContextCreates = new List<AsyncContextEvent>();
            public readonly List<AsyncContextEvent> ContextResumes = new List<AsyncContextEvent>();
            public readonly List<AsyncContextEvent> ContextSuspends = new List<AsyncContextEvent>();
            public readonly List<AsyncContextEvent> ContextCompletes = new List<AsyncContextEvent>();
            public readonly List<AsyncUnwindEvent> Unwinds = new List<AsyncUnwindEvent>();
            public readonly List<AsyncCallstackEvent> Callstacks = new List<AsyncCallstackEvent>();
            public readonly List<AsyncMethodEvent> Methods = new List<AsyncMethodEvent>();
            public readonly List<AsyncNeutralEvent> ResetThreadContexts = new List<AsyncNeutralEvent>();
            public readonly List<AsyncNeutralEvent> ResetWrapperIndexes = new List<AsyncNeutralEvent>();
            public readonly List<AsyncMetadataEvent> Metadatas = new List<AsyncMetadataEvent>();
            public readonly List<AsyncSyncClockEvent> SyncClocks = new List<AsyncSyncClockEvent>();
            public readonly List<AsyncUnknownEvent> Unknowns = new List<AsyncUnknownEvent>();
            public readonly List<AsyncProfilerParseError> Errors = new List<AsyncProfilerParseError>();

            public void OnContextCreate(in AsyncContextEvent e) { Order.Add(e.EventId); ContextCreates.Add(e); }
            public void OnContextResume(in AsyncContextEvent e) { Order.Add(e.EventId); ContextResumes.Add(e); }
            public void OnContextSuspend(in AsyncContextEvent e) { Order.Add(e.EventId); ContextSuspends.Add(e); }
            public void OnContextComplete(in AsyncContextEvent e) { Order.Add(e.EventId); ContextCompletes.Add(e); }
            public void OnException(in AsyncUnwindEvent e) { Order.Add(e.EventId); Unwinds.Add(e); }
            public void OnCallstack(in AsyncCallstackEvent e) { Order.Add(e.EventId); Callstacks.Add(e); }
            public void OnMethodResume(in AsyncMethodEvent e) { Order.Add(e.EventId); Methods.Add(e); }
            public void OnMethodComplete(in AsyncMethodEvent e) { Order.Add(e.EventId); Methods.Add(e); }
            public void OnResetThreadContext(in AsyncNeutralEvent e) { Order.Add(e.EventId); ResetThreadContexts.Add(e); }
            public void OnResetContinuationWrapperIndex(in AsyncNeutralEvent e) { Order.Add(e.EventId); ResetWrapperIndexes.Add(e); }
            public void OnMetadata(in AsyncMetadataEvent e) { Order.Add(AsyncEventID.AsyncProfilerMetadata); Metadatas.Add(e); }
            public void OnSyncClock(in AsyncSyncClockEvent e) { Order.Add(AsyncEventID.AsyncProfilerSyncClock); SyncClocks.Add(e); }
            public void OnUnknown(in AsyncUnknownEvent e) { Unknowns.Add(e); }
            public void OnParseError(in AsyncProfilerParseError e) { Errors.Add(e); }
        }

        /// <summary>
        /// Builds an <c>AsyncEvents</c> buffer byte-for-byte in the runtime wire format (37-byte header,
        /// per-sub-event framing of id + compressed delta timestamp + length prefix + payload).
        /// </summary>
        private sealed class AsyncProfilerBufferBuilder
        {
            private readonly List<byte> _bytes = new List<byte>();
            private long _lastTs = StartQpc;
            private uint _eventCount;

            public AsyncProfilerBufferBuilder()
            {
                // 37-byte header; totalSize/eventCount/endTs are patched in Build().
                _bytes.Add(1);                              // version
                WriteUInt32(0);                             // totalSize (patched)
                WriteUInt32(AsyncThreadContextId);
                WriteUInt64(OsThreadId);
                WriteUInt32(0);                             // eventCount (patched)
                WriteUInt64((ulong)StartQpc);               // startTs
                WriteUInt64(0);                             // endTs (patched)
            }

            public AsyncProfilerBufferBuilder CreateContext(AsyncEventID id, long ts, ulong parent, ulong dispatcher)
            {
                var payload = new List<byte>();
                WriteCompressedUInt64(payload, parent);
                WriteCompressedUInt64(payload, dispatcher);
                return Emit(id, ts, payload);
            }

            public AsyncProfilerBufferBuilder ResumeContext(AsyncEventID id, long ts, ulong dispatcher)
            {
                var payload = new List<byte>();
                WriteCompressedUInt64(payload, dispatcher);
                return Emit(id, ts, payload);
            }

            public AsyncProfilerBufferBuilder ResumeContextWithExtra(AsyncEventID id, long ts, ulong dispatcher, int extraBytes)
            {
                var payload = new List<byte>();
                WriteCompressedUInt64(payload, dispatcher);
                for (int i = 0; i < extraBytes; i++)
                {
                    payload.Add((byte)(0xE0 + i));
                }
                return Emit(id, ts, payload);
            }

            public AsyncProfilerBufferBuilder ContextNoPayload(AsyncEventID id, long ts) => Emit(id, ts, new List<byte>());

            public AsyncProfilerBufferBuilder Method(AsyncEventID id, long ts) => Emit(id, ts, new List<byte>());

            public AsyncProfilerBufferBuilder Unwind(AsyncEventID id, long ts, uint unwoundFrames)
            {
                var payload = new List<byte>();
                WriteCompressedUInt32(payload, unwoundFrames);
                return Emit(id, ts, payload);
            }

            public AsyncProfilerBufferBuilder Callstack(AsyncEventID id, long ts, byte continuationIndex,
                ulong parentDispatcherId, ulong dispatcherId, ulong[] methodIds, int[] states)
            {
                var payload = new List<byte>();
                payload.Add(0);                             // reserved callstack id
                payload.Add(continuationIndex);
                payload.Add((byte)methodIds.Length);        // frame count
                if (id == AsyncEventID.CreateRuntimeAsyncCallstack)
                {
                    WriteCompressedUInt64(payload, parentDispatcherId);
                }
                WriteCompressedUInt64(payload, dispatcherId);

                if (methodIds.Length > 0)
                {
                    WriteCompressedUInt64(payload, methodIds[0]);
                    if (states != null)
                    {
                        WriteCompressedInt32(payload, states[0]);
                    }
                    for (int i = 1; i < methodIds.Length; i++)
                    {
                        WriteCompressedInt64(payload, (long)methodIds[i] - (long)methodIds[i - 1]);
                        if (states != null)
                        {
                            WriteCompressedInt32(payload, states[i]);
                        }
                    }
                }
                return Emit(id, ts, payload);
            }

            public AsyncProfilerBufferBuilder Metadata(long ts, ulong qpcFrequency, ulong qpcSync, ulong utcSync,
                uint eventBufferSize, byte wrapperCount, AsyncManifestEntry[] manifest)
            {
                var payload = new List<byte>();
                WriteCompressedUInt64(payload, qpcFrequency);
                WriteCompressedUInt64(payload, qpcSync);
                WriteCompressedUInt64(payload, utcSync);
                WriteCompressedUInt32(payload, eventBufferSize);
                payload.Add(wrapperCount);
                payload.Add((byte)manifest.Length);
                foreach (var entry in manifest)
                {
                    payload.Add((byte)entry.EventId);
                    payload.Add(entry.Version);
                    payload.Add((byte)entry.PayloadLengthFieldSize);
                }
                return Emit(AsyncEventID.AsyncProfilerMetadata, ts, payload);
            }

            public AsyncProfilerBufferBuilder SyncClock(long ts, ulong qpcSync, ulong utcSync)
            {
                var payload = new List<byte>();
                WriteCompressedUInt64(payload, qpcSync);
                WriteCompressedUInt64(payload, utcSync);
                return Emit(AsyncEventID.AsyncProfilerSyncClock, ts, payload);
            }

            public AsyncProfilerBufferBuilder UnknownNoPayload(byte eventId, long ts) => Emit((AsyncEventID)eventId, ts, new List<byte>());

            /// <summary>
            /// Emits a sub-event with an explicit payload-length prefix width, independent of the static
            /// default table. Used to simulate a newer runtime whose manifest advertises a different width
            /// (or a future event id the parser does not statically know).
            /// </summary>
            public AsyncProfilerBufferBuilder RawEvent(byte eventId, long ts, PayloadLengthFieldSize prefix, byte[] payload)
            {
                _bytes.Add(eventId);
                WriteCompressedUInt64(_bytes, (ulong)(ts - _lastTs));
                _lastTs = ts;

                switch (prefix)
                {
                    case PayloadLengthFieldSize.None:
                        break;
                    case PayloadLengthFieldSize.Byte:
                        _bytes.Add((byte)payload.Length);
                        break;
                    default: // UShort
                        _bytes.Add((byte)(payload.Length & 0xFF));
                        _bytes.Add((byte)((payload.Length >> 8) & 0xFF));
                        break;
                }

                _bytes.AddRange(payload);
                _eventCount++;
                return this;
            }

            private AsyncProfilerBufferBuilder Emit(AsyncEventID id, long ts, List<byte> payload)
            {
                _bytes.Add((byte)id);
                WriteCompressedUInt64(_bytes, (ulong)(ts - _lastTs));
                _lastTs = ts;

                switch (AsyncEventInfo.GetPayloadLengthFieldSize(id))
                {
                    case PayloadLengthFieldSize.None:
                        Assert.Empty(payload);
                        break;
                    case PayloadLengthFieldSize.Byte:
                        Assert.True(payload.Count <= byte.MaxValue);
                        _bytes.Add((byte)payload.Count);
                        break;
                    default: // UShort
                        _bytes.Add((byte)(payload.Count & 0xFF));
                        _bytes.Add((byte)((payload.Count >> 8) & 0xFF));
                        break;
                }

                _bytes.AddRange(payload);
                _eventCount++;
                return this;
            }

            public byte[] Build()
            {
                byte[] result = _bytes.ToArray();
                // Patch totalSize @1, eventCount @17, endTs @29.
                WriteUInt32At(result, 1, (uint)result.Length);
                WriteUInt32At(result, 17, _eventCount);
                WriteUInt64At(result, 29, (ulong)_lastTs);
                return result;
            }

            private void WriteUInt32(uint value)
            {
                _bytes.Add((byte)value);
                _bytes.Add((byte)(value >> 8));
                _bytes.Add((byte)(value >> 16));
                _bytes.Add((byte)(value >> 24));
            }

            private void WriteUInt64(ulong value)
            {
                WriteUInt32((uint)value);
                WriteUInt32((uint)(value >> 32));
            }

            private static void WriteUInt32At(byte[] buffer, int offset, uint value)
            {
                buffer[offset] = (byte)value;
                buffer[offset + 1] = (byte)(value >> 8);
                buffer[offset + 2] = (byte)(value >> 16);
                buffer[offset + 3] = (byte)(value >> 24);
            }

            private static void WriteUInt64At(byte[] buffer, int offset, ulong value)
            {
                WriteUInt32At(buffer, offset, (uint)value);
                WriteUInt32At(buffer, offset + 4, (uint)(value >> 32));
            }

            private static void WriteCompressedUInt64(List<byte> target, ulong value)
            {
                while (value > 0x7F)
                {
                    target.Add((byte)(value | 0x80));
                    value >>= 7;
                }
                target.Add((byte)value);
            }

            private static void WriteCompressedUInt32(List<byte> target, uint value) => WriteCompressedUInt64(target, value);

            private static void WriteCompressedInt64(List<byte> target, long value)
            {
                ulong zigzag = (ulong)((value << 1) ^ (value >> 63));
                WriteCompressedUInt64(target, zigzag);
            }

            private static void WriteCompressedInt32(List<byte> target, int value)
            {
                uint zigzag = (uint)((value << 1) ^ (value >> 31));
                WriteCompressedUInt64(target, zigzag);
            }
        }

        #endregion
    }
}
