// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

using Xunit;

namespace TraceEventTests
{
    /// <summary>Default header values used by <see cref="AsyncProfilerBufferBuilder"/> and the parser tests.</summary>
    internal static class AsyncProfilerTestData
    {
        public const uint DefaultAsyncThreadContextId = 0x0BADF00D;
        public const ulong DefaultOsThreadId = 0x1234;
        public const long DefaultStartQpc = 1_000_000;
    }

    /// <summary>
    /// Builds an <c>AsyncEvents</c> buffer byte-for-byte in the runtime wire format (37-byte header,
    /// per-sub-event framing of id + compressed delta timestamp + length prefix + payload). Shared by the
    /// parser and computer tests. Parameterized by the emitting OS thread and start timestamp so multiple
    /// threads / buffers can be synthesized.
    /// </summary>
    internal sealed class AsyncProfilerBufferBuilder
    {
        private readonly List<byte> _bytes = new List<byte>();
        private readonly ulong _osThreadId;
        private readonly uint _asyncThreadContextId;
        private readonly long _startQpc;
        private long _lastTs;
        private uint _eventCount;

        public AsyncProfilerBufferBuilder(
            ulong osThreadId = AsyncProfilerTestData.DefaultOsThreadId,
            uint asyncThreadContextId = AsyncProfilerTestData.DefaultAsyncThreadContextId,
            long startQpc = AsyncProfilerTestData.DefaultStartQpc)
        {
            _osThreadId = osThreadId;
            _asyncThreadContextId = asyncThreadContextId;
            _startQpc = startQpc;
            _lastTs = startQpc;

            // 37-byte header; totalSize/eventCount/endTs are patched in Build().
            _bytes.Add(1);                              // version
            WriteUInt32(0);                             // totalSize (patched)
            WriteUInt32(asyncThreadContextId);
            WriteUInt64(osThreadId);
            WriteUInt32(0);                             // eventCount (patched)
            WriteUInt64((ulong)startQpc);               // startTs
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

    /// <summary>A sink that records every decoded sub-event so tests can assert on them.</summary>
    internal sealed class CollectingSink : IAsyncProfilerSubEventSink
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
}
