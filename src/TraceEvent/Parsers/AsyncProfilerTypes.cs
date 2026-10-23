// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

namespace Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler
{
    /// <summary>
    /// Identifies a logical async-profiler sub-event carried inside an <c>AsyncEvents</c> buffer.
    /// The numeric values match the runtime's internal <c>AsyncProfiler.AsyncEventID</c> enum exactly.
    /// Ids 1-10 are the V2 (RuntimeAsync) instrumentation, ids 11-19 are the V1 (StateMachineAsync)
    /// instrumentation, and ids 20-23 are neutral profiler book-keeping events.
    /// </summary>
    internal enum AsyncEventID : byte
    {
        // V2 (RuntimeAsync) events.
        CreateRuntimeAsyncContext = 1,
        ResumeRuntimeAsyncContext = 2,
        SuspendRuntimeAsyncContext = 3,
        CompleteRuntimeAsyncContext = 4,
        UnwindRuntimeAsyncException = 5,
        CreateRuntimeAsyncCallstack = 6,
        ResumeRuntimeAsyncCallstack = 7,
        SuspendRuntimeAsyncCallstack = 8,
        ResumeRuntimeAsyncMethod = 9,
        CompleteRuntimeAsyncMethod = 10,

        // V1 (StateMachineAsync) events.
        CreateStateMachineAsyncContext = 11,
        ResumeStateMachineAsyncContext = 12,
        SuspendStateMachineAsyncContext = 13,
        CompleteStateMachineAsyncContext = 14,
        UnwindStateMachineAsyncException = 15,
        ResumeStateMachineAsyncCallstack = 16,
        ResumeStateMachineAsyncMethod = 17,
        CompleteStateMachineAsyncMethod = 18,
        AppendStateMachineAsyncCallstack = 19,

        // Neutral profiler events.
        ResetAsyncThreadContext = 20,
        ResetAsyncContinuationWrapperIndex = 21,
        AsyncProfilerMetadata = 22,
        AsyncProfilerSyncClock = 23,
    }

    /// <summary>
    /// The width of the length prefix that precedes a sub-event payload in the buffer. Matches the
    /// runtime's <c>EventManifestEntry.PayloadLengthFieldSize</c>.
    /// </summary>
    internal enum PayloadLengthFieldSize : byte
    {
        None = 0,
        Byte = 1,
        UShort = 2,
    }

    /// <summary>
    /// The instrumentation family that produced an async callstack. Both families encode native code
    /// instruction pointers: V2 captures runtime-async continuation IPs; V1 captures state-machine
    /// <c>MoveNext</c> body IPs plus raw signed compiler states. V1/V2 instrumentation is independent of
    /// the buffer layout version and the per-event schema versions in <see cref="Manifest"/>.
    /// </summary>
    internal enum AsyncCallstackKind : byte
    {
        /// <summary>V2 runtime-async instrumentation.</summary>
        RuntimeAsync = 0,
        /// <summary>V1 compiler-generated state-machine instrumentation.</summary>
        StateMachineAsync = 1,
    }

    /// <summary>
    /// Static per-<see cref="AsyncEventID"/> metadata: the payload-length prefix width and callstack/kind
    /// classification used while decoding a buffer. These are the default (version 1) values baked into
    /// the runtime; a buffer's <c>AsyncProfilerMetadata</c> sub-event carries the live manifest for
    /// forward-version negotiation.
    /// </summary>
    internal static class AsyncEventInfo
    {
        // Indexed by (byte)AsyncEventID - 1; dense and ordered by ascending id.
        private static readonly PayloadLengthFieldSize[] s_payloadLengthFieldSizes =
        {
            PayloadLengthFieldSize.Byte,    // 1  CreateRuntimeAsyncContext
            PayloadLengthFieldSize.Byte,    // 2  ResumeRuntimeAsyncContext
            PayloadLengthFieldSize.None,    // 3  SuspendRuntimeAsyncContext
            PayloadLengthFieldSize.None,    // 4  CompleteRuntimeAsyncContext
            PayloadLengthFieldSize.Byte,    // 5  UnwindRuntimeAsyncException
            PayloadLengthFieldSize.UShort,  // 6  CreateRuntimeAsyncCallstack
            PayloadLengthFieldSize.UShort,  // 7  ResumeRuntimeAsyncCallstack
            PayloadLengthFieldSize.UShort,  // 8  SuspendRuntimeAsyncCallstack
            PayloadLengthFieldSize.None,    // 9  ResumeRuntimeAsyncMethod
            PayloadLengthFieldSize.None,    // 10 CompleteRuntimeAsyncMethod
            PayloadLengthFieldSize.Byte,    // 11 CreateStateMachineAsyncContext
            PayloadLengthFieldSize.Byte,    // 12 ResumeStateMachineAsyncContext
            PayloadLengthFieldSize.None,    // 13 SuspendStateMachineAsyncContext
            PayloadLengthFieldSize.None,    // 14 CompleteStateMachineAsyncContext
            PayloadLengthFieldSize.Byte,    // 15 UnwindStateMachineAsyncException
            PayloadLengthFieldSize.UShort,  // 16 ResumeStateMachineAsyncCallstack
            PayloadLengthFieldSize.None,    // 17 ResumeStateMachineAsyncMethod
            PayloadLengthFieldSize.None,    // 18 CompleteStateMachineAsyncMethod
            PayloadLengthFieldSize.UShort,  // 19 AppendStateMachineAsyncCallstack
            PayloadLengthFieldSize.None,    // 20 ResetAsyncThreadContext
            PayloadLengthFieldSize.None,    // 21 ResetAsyncContinuationWrapperIndex
            PayloadLengthFieldSize.UShort,  // 22 AsyncProfilerMetadata
            PayloadLengthFieldSize.Byte,    // 23 AsyncProfilerSyncClock
        };

        /// <summary>Returns the payload-length prefix width for <paramref name="eventId"/> (None for unknown ids).</summary>
        public static PayloadLengthFieldSize GetPayloadLengthFieldSize(AsyncEventID eventId)
        {
            int i = (byte)eventId - 1;
            return (uint)i < (uint)s_payloadLengthFieldSizes.Length ? s_payloadLengthFieldSizes[i] : PayloadLengthFieldSize.None;
        }

        /// <summary>True if <paramref name="eventId"/> is a V1 (StateMachineAsync) event (ids 11-19).</summary>
        public static bool IsStateMachine(AsyncEventID eventId) =>
            eventId >= AsyncEventID.CreateStateMachineAsyncContext && eventId <= AsyncEventID.AppendStateMachineAsyncCallstack;

        /// <summary>True if <paramref name="eventId"/> is a V2 (RuntimeAsync) event (ids 1-10).</summary>
        public static bool IsRuntime(AsyncEventID eventId) =>
            eventId >= AsyncEventID.CreateRuntimeAsyncContext && eventId <= AsyncEventID.CompleteRuntimeAsyncMethod;

        /// <summary>True if <paramref name="eventId"/> carries a callstack payload (ids 6, 7, 8, 16, 19).</summary>
        public static bool IsCallstack(AsyncEventID eventId) =>
            eventId == AsyncEventID.CreateRuntimeAsyncCallstack ||
            eventId == AsyncEventID.ResumeRuntimeAsyncCallstack ||
            eventId == AsyncEventID.SuspendRuntimeAsyncCallstack ||
            eventId == AsyncEventID.ResumeStateMachineAsyncCallstack ||
            eventId == AsyncEventID.AppendStateMachineAsyncCallstack;

        /// <summary>
        /// Classifies a callstack event's frame family. StateMachineAsync callstacks (ids 16, 19) encode a
        /// raw signed compiler state alongside each <c>MoveNext</c> body IP; RuntimeAsync callstacks
        /// encode native continuation IPs without per-frame states.
        /// </summary>
        public static AsyncCallstackKind GetCallstackKind(AsyncEventID eventId) =>
            eventId == AsyncEventID.ResumeStateMachineAsyncCallstack || eventId == AsyncEventID.AppendStateMachineAsyncCallstack
                ? AsyncCallstackKind.StateMachineAsync
                : AsyncCallstackKind.RuntimeAsync;
    }

    /// <summary>
    /// The live per-event manifest a parser uses to frame sub-events: the payload-length prefix width and
    /// schema version for each <see cref="AsyncEventID"/>. It starts from the built-in v1 defaults (see
    /// <see cref="AsyncEventInfo"/>) and is refreshed whenever an <c>AsyncProfilerMetadata</c> sub-event is
    /// decoded, so a parser can (a) frame (and therefore skip) events whose id or payload-length width it
    /// does not statically know, and (b) track each event's emitted schema version.
    /// <para>
    /// The runtime emits the metadata only once per configuration revision (not per buffer), so a single
    /// manifest instance must persist across buffers from the same process instance. The caller owns this
    /// state: <see cref="Computers.AsyncProfilerComputer"/> maintains one manifest per process instance,
    /// while direct decoder callers must pass their retained manifest to
    /// <see cref="AsyncProfilerTraceEventParser.ParseBuffer(byte[], Manifest, ISubEventSink)"/>.
    /// The overload without a manifest creates fresh defaults for each call. Do not share a manifest between
    /// different process instances.
    /// </para>
    /// </summary>
    internal sealed class Manifest
    {
        // Indexed by the raw byte event id (0..255) so the manifest can describe framing for future ids the
        // parser does not statically know about.
        private readonly PayloadLengthFieldSize[] _fieldSize = new PayloadLengthFieldSize[256];
        private readonly byte[] _version = new byte[256];

        /// <summary>Creates a manifest seeded with the known version-1 event layouts.</summary>
        public Manifest()
        {
            // Seed with the built-in v1 layout for the ids we statically know (1..23). Unknown ids default
            // to no payload-length prefix and version 0 until a metadata manifest describes them.
            for (int id = 1; id <= 23; id++)
            {
                _fieldSize[id] = AsyncEventInfo.GetPayloadLengthFieldSize((AsyncEventID)id);
                _version[id] = 1;
            }
        }

        /// <summary>The payload-length prefix width to use when framing <paramref name="eventId"/>.</summary>
        public PayloadLengthFieldSize GetPayloadLengthFieldSize(AsyncEventID eventId) => _fieldSize[(byte)eventId];

        /// <summary>
        /// The most recently advertised schema version of <paramref name="eventId"/>, or the built-in version 1
        /// for a known event before metadata is seen. Unknown events default to 0.
        /// </summary>
        public byte GetVersion(AsyncEventID eventId) => _version[(byte)eventId];

        /// <summary>Applies the manifest carried by an <c>AsyncProfilerMetadata</c> sub-event.</summary>
        public void Apply(in AsyncMetadataEvent metadata) => Apply(metadata.Manifest);

        /// <summary>
        /// Applies a set of manifest entries, overriding the framing width and version for each listed id.
        /// Unlisted ids retain their previous entries; null or empty input leaves the manifest unchanged.
        /// </summary>
        public void Apply(AsyncManifestEntry[] entries)
        {
            if (entries == null)
            {
                return;
            }

            foreach (AsyncManifestEntry entry in entries)
            {
                byte id = (byte)entry.EventId;
                _fieldSize[id] = entry.PayloadLengthFieldSize;
                _version[id] = entry.Version;
            }
        }
    }

    /// <summary>
    /// The fixed-size header at the start of every <c>AsyncEvents</c> buffer (37 bytes, little-endian).
    /// Identifies the emitting OS thread and the timestamp window the batched sub-events fall within.
    /// </summary>
    internal readonly struct BufferHeader
    {
        /// <summary>The size (in bytes) of the serialized header.</summary>
        public const int Size = 37;

        public readonly byte Version;
        public readonly uint TotalSize;
        public readonly uint AsyncThreadContextId;
        public readonly ulong OsThreadId;
        public readonly uint EventCount;
        public readonly long StartTimestampQpc;
        public readonly long EndTimestampQpc;

        public BufferHeader(byte version, uint totalSize, uint asyncThreadContextId, ulong osThreadId, uint eventCount, long startTimestampQpc, long endTimestampQpc)
        {
            Version = version;
            TotalSize = totalSize;
            AsyncThreadContextId = asyncThreadContextId;
            OsThreadId = osThreadId;
            EventCount = eventCount;
            StartTimestampQpc = startTimestampQpc;
            EndTimestampQpc = endTimestampQpc;
        }

        /// <summary>Reads and validates the buffer header. Returns false for a truncated or unsupported buffer.</summary>
        public static bool TryRead(byte[] buffer, out BufferHeader header)
        {
            header = default;
            if (buffer == null || buffer.Length < Size || buffer[0] != 1)
            {
                return false;
            }

            int i = 1;
            uint totalSize = BufferReader.ReadUInt32LE(buffer, ref i);
            uint asyncThreadContextId = BufferReader.ReadUInt32LE(buffer, ref i);
            ulong osThreadId = BufferReader.ReadUInt64LE(buffer, ref i);
            uint eventCount = BufferReader.ReadUInt32LE(buffer, ref i);
            ulong startTs = BufferReader.ReadUInt64LE(buffer, ref i);
            ulong endTs = BufferReader.ReadUInt64LE(buffer, ref i);

            header = new BufferHeader(buffer[0], totalSize, asyncThreadContextId, osThreadId, eventCount, (long)startTs, (long)endTs);
            return true;
        }
    }

    /// <summary>
    /// Low-level readers for the buffer's little-endian fixed-width fields and its LEB128/zigzag
    /// compressed integers. All compressed readers advance the caller's <c>index</c> and return false on truncation.
    /// </summary>
    internal static class BufferReader
    {
        public static ushort ReadUInt16LE(byte[] buffer, ref int index)
        {
            ushort value = (ushort)(buffer[index] | (buffer[index + 1] << 8));
            index += 2;
            return value;
        }

        public static uint ReadUInt32LE(byte[] buffer, ref int index)
        {
            uint value = (uint)(buffer[index] | (buffer[index + 1] << 8) | (buffer[index + 2] << 16) | (buffer[index + 3] << 24));
            index += 4;
            return value;
        }

        public static ulong ReadUInt64LE(byte[] buffer, ref int index)
        {
            ulong lo = ReadUInt32LE(buffer, ref index);
            ulong hi = ReadUInt32LE(buffer, ref index);
            return lo | (hi << 32);
        }

        /// <summary>
        /// Reads an unsigned LEB128 (7-bit, least-significant group first) value without advancing beyond
        /// <paramref name="limit"/>.
        /// </summary>
        public static bool TryReadCompressedUInt64(byte[] buffer, ref int index, int limit, out ulong value)
        {
            value = 0;
            for (int byteIndex = 0; byteIndex < 10; byteIndex++)
            {
                if (index >= limit)
                {
                    return false;
                }

                byte b = buffer[index++];
                if (byteIndex == 9 && (b & 0xFE) != 0)
                {
                    return false;
                }

                value |= (ulong)(b & 0x7F) << (byteIndex * 7);
                if ((b & 0x80) == 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Reads an unsigned LEB128 value into a 32-bit result.</summary>
        public static bool TryReadCompressedUInt32(byte[] buffer, ref int index, int limit, out uint value)
        {
            if (TryReadCompressedUInt64(buffer, ref index, limit, out ulong wide) && wide <= uint.MaxValue)
            {
                value = (uint)wide;
                return true;
            }
            value = 0;
            return false;
        }

        /// <summary>Reads a zigzag-encoded signed LEB128 value (64-bit).</summary>
        public static bool TryReadCompressedInt64(byte[] buffer, ref int index, int limit, out long value)
        {
            if (TryReadCompressedUInt64(buffer, ref index, limit, out ulong u))
            {
                value = (long)(u >> 1) ^ -(long)(u & 1);
                return true;
            }
            value = 0;
            return false;
        }

        /// <summary>Reads a zigzag-encoded signed LEB128 value (32-bit).</summary>
        public static bool TryReadCompressedInt32(byte[] buffer, ref int index, int limit, out int value)
        {
            if (TryReadCompressedUInt32(buffer, ref index, limit, out uint u))
            {
                value = (int)(u >> 1) ^ -(int)(u & 1);
                return true;
            }
            value = 0;
            return false;
        }
    }

    /// <summary>A sub-event describing an async-context lifecycle transition (Create/Resume/Suspend/Complete).</summary>
    internal readonly struct AsyncContextEvent
    {
        public readonly AsyncEventID EventId;
        public readonly long TimestampQpc;
        public readonly BufferHeader Header;

        /// <summary>The parent dispatcher id (only meaningful for Create events; 0 otherwise).</summary>
        public readonly ulong ParentDispatcherId;

        /// <summary>The dispatcher id (meaningful for Create/Resume; 0 for Suspend/Complete which are implicit on the current context).</summary>
        public readonly ulong DispatcherId;

        public AsyncContextEvent(AsyncEventID eventId, long timestampQpc, in BufferHeader header, ulong parentDispatcherId, ulong dispatcherId)
        {
            EventId = eventId;
            TimestampQpc = timestampQpc;
            Header = header;
            ParentDispatcherId = parentDispatcherId;
            DispatcherId = dispatcherId;
        }

        public ulong OsThreadId => Header.OsThreadId;
        public uint AsyncThreadContextId => Header.AsyncThreadContextId;
        public bool IsStateMachine => AsyncEventInfo.IsStateMachine(EventId);
    }

    /// <summary>A sub-event marking that an async method began (Resume) or finished (Complete) running.</summary>
    internal readonly struct AsyncMethodEvent
    {
        public readonly AsyncEventID EventId;
        public readonly long TimestampQpc;
        public readonly BufferHeader Header;

        public AsyncMethodEvent(AsyncEventID eventId, long timestampQpc, in BufferHeader header)
        {
            EventId = eventId;
            TimestampQpc = timestampQpc;
            Header = header;
        }

        public ulong OsThreadId => Header.OsThreadId;
        public uint AsyncThreadContextId => Header.AsyncThreadContextId;
        public bool IsStateMachine => AsyncEventInfo.IsStateMachine(EventId);
    }

    /// <summary>A sub-event recording that an exception unwound some number of async frames.</summary>
    internal readonly struct AsyncUnwindEvent
    {
        public readonly AsyncEventID EventId;
        public readonly long TimestampQpc;
        public readonly BufferHeader Header;
        public readonly uint UnwoundFrameCount;

        public AsyncUnwindEvent(AsyncEventID eventId, long timestampQpc, in BufferHeader header, uint unwoundFrameCount)
        {
            EventId = eventId;
            TimestampQpc = timestampQpc;
            Header = header;
            UnwoundFrameCount = unwoundFrameCount;
        }

        public ulong OsThreadId => Header.OsThreadId;
        public bool IsStateMachine => AsyncEventInfo.IsStateMachine(EventId);
    }

    /// <summary>A neutral profiler book-keeping sub-event (thread-context / wrapper-index reset).</summary>
    internal readonly struct AsyncNeutralEvent
    {
        public readonly AsyncEventID EventId;
        public readonly long TimestampQpc;
        public readonly BufferHeader Header;

        public AsyncNeutralEvent(AsyncEventID eventId, long timestampQpc, in BufferHeader header)
        {
            EventId = eventId;
            TimestampQpc = timestampQpc;
            Header = header;
        }

        public ulong OsThreadId => Header.OsThreadId;
    }

    /// <summary>An unrecognized/future sub-event; its payload was skipped using the length prefix.</summary>
    internal readonly struct AsyncUnknownEvent
    {
        public readonly AsyncEventID EventId;
        public readonly long TimestampQpc;
        public readonly BufferHeader Header;
        public readonly int PayloadLength;

        public AsyncUnknownEvent(AsyncEventID eventId, long timestampQpc, in BufferHeader header, int payloadLength)
        {
            EventId = eventId;
            TimestampQpc = timestampQpc;
            Header = header;
            PayloadLength = payloadLength;
        }
    }

    /// <summary>
    /// An async callstack sub-event. Frames are ordered leaf-first; <see cref="MethodIds"/> holds native code IPs
    /// for both families, not CLR MethodLoad identifiers or MethodDesc handles. <see cref="FrameStates"/>
    /// carries raw signed compiler states for StateMachineAsync callstacks (null for RuntimeAsync).
    /// A <see cref="IsCached"/> callstack (frame count 0) references a previously emitted callstack by
    /// dispatcher id and carries no frame data.
    /// </summary>
    internal readonly struct AsyncCallstackEvent
    {
        public readonly AsyncEventID EventId;
        public readonly long TimestampQpc;
        public readonly BufferHeader Header;
        public readonly byte ContinuationIndex;
        public readonly byte FrameCount;
        public readonly ulong ParentDispatcherId;
        public readonly ulong DispatcherId;
        /// <summary>Native continuation IPs for V2, or state-machine <c>MoveNext</c> body IPs for V1.</summary>
        public readonly ulong[] MethodIds;
        /// <summary>Raw V1 compiler states, not IL/native offsets; null for V2.</summary>
        public readonly int[] FrameStates;

        private AsyncCallstackEvent(AsyncEventID eventId, long timestampQpc, in BufferHeader header,
            byte continuationIndex, byte frameCount, ulong parentDispatcherId, ulong dispatcherId, ulong[] methodIds, int[] frameStates)
        {
            EventId = eventId;
            TimestampQpc = timestampQpc;
            Header = header;
            ContinuationIndex = continuationIndex;
            FrameCount = frameCount;
            ParentDispatcherId = parentDispatcherId;
            DispatcherId = dispatcherId;
            MethodIds = methodIds;
            FrameStates = frameStates;
        }

        public ulong OsThreadId => Header.OsThreadId;
        public uint AsyncThreadContextId => Header.AsyncThreadContextId;
        public AsyncCallstackKind Kind => AsyncEventInfo.GetCallstackKind(EventId);

        /// <summary>True when the callstack references a previously emitted callstack by dispatcher id (no frames inline).</summary>
        public bool IsCached => FrameCount == 0;

        internal static bool TryRead(AsyncEventID eventId, long timestampQpc, in BufferHeader header,
            byte[] buffer, ref int index, int payloadEnd, out AsyncCallstackEvent result)
        {
            result = default;
            if (!TryReadHeader(eventId, buffer, ref index, payloadEnd, out byte continuationIndex,
                out byte frameCount, out ulong parentDispatcherId, out ulong dispatcherId))
            {
                return false;
            }

            if (frameCount == 0)
            {
                // Cached callstack reference; frame data is resolved out of band by dispatcher id.
                result = new AsyncCallstackEvent(eventId, timestampQpc, header, continuationIndex, 0, parentDispatcherId, dispatcherId, Array.Empty<ulong>(), null);
                return true;
            }

            bool readState = AsyncEventInfo.GetCallstackKind(eventId) == AsyncCallstackKind.StateMachineAsync;
            var methodIds = new ulong[frameCount];
            int[] frameStates = readState ? new int[frameCount] : null;

            if (!TryReadFrames(buffer, ref index, payloadEnd, frameCount, methodIds, frameStates))
            {
                return false;
            }

            result = new AsyncCallstackEvent(eventId, timestampQpc, header, continuationIndex, frameCount, parentDispatcherId, dispatcherId, methodIds, frameStates);
            return true;
        }

        internal static bool TryReadInto(AsyncEventID eventId, long timestampQpc, in BufferHeader header,
            byte[] buffer, ref int index, int payloadEnd, ulong[] methodIds, int[] frameStates, out AsyncCallstackEvent result)
        {
            result = default;
            if (!TryReadHeader(eventId, buffer, ref index, payloadEnd, out byte continuationIndex,
                out byte frameCount, out ulong parentDispatcherId, out ulong dispatcherId))
            {
                return false;
            }

            if (frameCount == 0)
            {
                result = new AsyncCallstackEvent(eventId, timestampQpc, header, continuationIndex, 0, parentDispatcherId, dispatcherId, Array.Empty<ulong>(), null);
                return true;
            }

            bool readState = AsyncEventInfo.GetCallstackKind(eventId) == AsyncCallstackKind.StateMachineAsync;
            if (methodIds == null || methodIds.Length < frameCount ||
                (readState && (frameStates == null || frameStates.Length < frameCount)))
            {
                return false;
            }

            int[] states = readState ? frameStates : null;
            if (!TryReadFrames(buffer, ref index, payloadEnd, frameCount, methodIds, states))
            {
                return false;
            }

            result = new AsyncCallstackEvent(eventId, timestampQpc, header, continuationIndex, frameCount, parentDispatcherId, dispatcherId, methodIds, states);
            return true;
        }

        private static bool TryReadHeader(AsyncEventID eventId, byte[] buffer, ref int index, int payloadEnd,
            out byte continuationIndex, out byte frameCount, out ulong parentDispatcherId, out ulong dispatcherId)
        {
            continuationIndex = 0;
            frameCount = 0;
            parentDispatcherId = 0;
            dispatcherId = 0;
            if (payloadEnd - index < 3)
            {
                return false;
            }

            index++; // Reserved callstack id (for future callstack interning).
            continuationIndex = buffer[index++];
            frameCount = buffer[index++];

            if (eventId == AsyncEventID.CreateRuntimeAsyncCallstack &&
                !BufferReader.TryReadCompressedUInt64(buffer, ref index, payloadEnd, out parentDispatcherId))
            {
                return false;
            }

            return BufferReader.TryReadCompressedUInt64(buffer, ref index, payloadEnd, out dispatcherId);
        }

        private static bool TryReadFrames(byte[] buffer, ref int index, int payloadEnd, int frameCount,
            ulong[] methodIds, int[] frameStates)
        {
            if (!BufferReader.TryReadCompressedUInt64(buffer, ref index, payloadEnd, out ulong currentMethodId))
            {
                return false;
            }
            methodIds[0] = currentMethodId;
            if (frameStates != null)
            {
                if (!BufferReader.TryReadCompressedInt32(buffer, ref index, payloadEnd, out int state0))
                {
                    return false;
                }
                frameStates[0] = state0;
            }

            for (int i = 1; i < frameCount; i++)
            {
                if (!BufferReader.TryReadCompressedInt64(buffer, ref index, payloadEnd, out long delta))
                {
                    return false;
                }
                currentMethodId = (ulong)((long)currentMethodId + delta);
                methodIds[i] = currentMethodId;

                if (frameStates != null)
                {
                    if (!BufferReader.TryReadCompressedInt32(buffer, ref index, payloadEnd, out int state))
                    {
                        return false;
                    }
                    frameStates[i] = state;
                }
            }

            return true;
        }
    }

    /// <summary>A single entry of the per-event manifest carried by an <see cref="AsyncMetadataEvent"/>.</summary>
    internal readonly struct AsyncManifestEntry
    {
        public readonly AsyncEventID EventId;
        public readonly byte Version;
        public readonly PayloadLengthFieldSize PayloadLengthFieldSize;

        public AsyncManifestEntry(AsyncEventID eventId, byte version, PayloadLengthFieldSize payloadLengthFieldSize)
        {
            EventId = eventId;
            Version = version;
            PayloadLengthFieldSize = payloadLengthFieldSize;
        }
    }

    /// <summary>
    /// The profiler metadata sub-event: the QPC frequency and a QPC/UTC synchronization point (so QPC
    /// timestamps can be mapped to wall-clock time), configuration, and the live per-event manifest.
    /// </summary>
    internal readonly struct AsyncMetadataEvent
    {
        public readonly long TimestampQpc;
        public readonly BufferHeader Header;
        public readonly ulong QpcFrequency;
        public readonly ulong QpcSync;
        public readonly ulong UtcSync;
        public readonly uint EventBufferSize;
        public readonly byte WrapperCount;
        public readonly AsyncManifestEntry[] Manifest;

        private AsyncMetadataEvent(long timestampQpc, in BufferHeader header, ulong qpcFrequency, ulong qpcSync, ulong utcSync, uint eventBufferSize, byte wrapperCount, AsyncManifestEntry[] manifest)
        {
            TimestampQpc = timestampQpc;
            Header = header;
            QpcFrequency = qpcFrequency;
            QpcSync = qpcSync;
            UtcSync = utcSync;
            EventBufferSize = eventBufferSize;
            WrapperCount = wrapperCount;
            Manifest = manifest;
        }

        public ulong OsThreadId => Header.OsThreadId;

        /// <summary>The synchronization point interpreted as a UTC wall-clock time (utcSync is a Windows FILETIME).</summary>
        public DateTime SyncUtc => DateTime.FromFileTimeUtc((long)UtcSync);

        internal static bool TryRead(long timestampQpc, in BufferHeader header, byte[] buffer, ref int index, int payloadEnd, out AsyncMetadataEvent result)
        {
            result = default;
            if (!BufferReader.TryReadCompressedUInt64(buffer, ref index, payloadEnd, out ulong qpcFrequency) ||
                !BufferReader.TryReadCompressedUInt64(buffer, ref index, payloadEnd, out ulong qpcSync) ||
                !BufferReader.TryReadCompressedUInt64(buffer, ref index, payloadEnd, out ulong utcSync) ||
                !BufferReader.TryReadCompressedUInt32(buffer, ref index, payloadEnd, out uint eventBufferSize) ||
                index >= payloadEnd)
            {
                return false;
            }

            byte wrapperCount = buffer[index++];
            if (index >= payloadEnd)
            {
                return false;
            }

            byte manifestCount = buffer[index++];
            AsyncManifestEntry[] manifest = manifestCount == 0 ? Array.Empty<AsyncManifestEntry>() : new AsyncManifestEntry[manifestCount];
            for (int i = 0; i < manifestCount; i++)
            {
                if (payloadEnd - index < 3)
                {
                    return false;
                }

                var payloadLengthFieldSize = (PayloadLengthFieldSize)buffer[index + 2];
                if (payloadLengthFieldSize != PayloadLengthFieldSize.None &&
                    payloadLengthFieldSize != PayloadLengthFieldSize.Byte &&
                    payloadLengthFieldSize != PayloadLengthFieldSize.UShort)
                {
                    return false;
                }

                manifest[i] = new AsyncManifestEntry((AsyncEventID)buffer[index], buffer[index + 1], payloadLengthFieldSize);
                index += 3;
            }

            result = new AsyncMetadataEvent(timestampQpc, header, qpcFrequency, qpcSync, utcSync, eventBufferSize, wrapperCount, manifest);
            return true;
        }
    }

    /// <summary>A lightweight QPC/UTC re-synchronization point emitted periodically between metadata events.</summary>
    internal readonly struct AsyncSyncClockEvent
    {
        public readonly long TimestampQpc;
        public readonly BufferHeader Header;
        public readonly ulong QpcSync;
        public readonly ulong UtcSync;

        public AsyncSyncClockEvent(long timestampQpc, in BufferHeader header, ulong qpcSync, ulong utcSync)
        {
            TimestampQpc = timestampQpc;
            Header = header;
            QpcSync = qpcSync;
            UtcSync = utcSync;
        }

        public ulong OsThreadId => Header.OsThreadId;
        public DateTime SyncUtc => DateTime.FromFileTimeUtc((long)UtcSync);
    }

    /// <summary>Reports a recoverable decode problem in a buffer (truncation, unexpected layout).</summary>
    internal readonly struct ParseError
    {
        /// <summary>The decoding failure.</summary>
        public readonly string Message;
        /// <summary>The byte offset of the failure within the buffer.</summary>
        public readonly int ByteOffset;
        /// <summary>The decoded header, or null when the header could not be read.</summary>
        public readonly BufferHeader? BufferHeader;
        /// <summary>
        /// The failing sub-event's QPC, or the last decoded QPC when its timestamp is unavailable.
        /// Null when the header could not be read.
        /// </summary>
        public readonly long? TimestampQpc;
        /// <summary>The failing sub-event's id, or null for a buffer-level failure.</summary>
        public readonly AsyncEventID? EventId;

        /// <summary>Creates a decode diagnostic, with any available buffer and sub-event context.</summary>
        public ParseError(string message, int byteOffset,
            BufferHeader? bufferHeader = null, long? timestampQpc = null, AsyncEventID? eventId = null)
        {
            Message = message;
            ByteOffset = byteOffset;
            BufferHeader = bufferHeader;
            TimestampQpc = timestampQpc;
            EventId = eventId;
        }

        public override string ToString() => "AsyncProfiler parse error @" + ByteOffset + ": " + Message;
    }

    /// <summary>
    /// Receives the strongly typed sub-events decoded from an <c>AsyncEvents</c> buffer by
    /// <see cref="AsyncProfilerTraceEventParser.ParseBuffer(byte[], ISubEventSink)"/>. A single sink handles both the V2
    /// (RuntimeAsync) and V1 (StateMachineAsync) instrumentation; the concrete
    /// <see cref="AsyncEventID"/> on each event distinguishes the family.
    /// </summary>
    internal interface ISubEventSink
    {
        void OnContextCreate(in AsyncContextEvent e);
        void OnContextResume(in AsyncContextEvent e);
        void OnContextSuspend(in AsyncContextEvent e);
        void OnContextComplete(in AsyncContextEvent e);
        void OnException(in AsyncUnwindEvent e);
        void OnCallstack(in AsyncCallstackEvent e);
        void OnMethodResume(in AsyncMethodEvent e);
        void OnMethodComplete(in AsyncMethodEvent e);
        void OnResetThreadContext(in AsyncNeutralEvent e);
        void OnResetContinuationWrapperIndex(in AsyncNeutralEvent e);
        void OnMetadata(in AsyncMetadataEvent e);
        void OnSyncClock(in AsyncSyncClockEvent e);
        void OnUnknown(in AsyncUnknownEvent e);
        void OnParseError(in ParseError e);
    }

    internal interface ICallstackPayloadSink
    {
        bool TryOnCallstack(AsyncEventID eventId, long timestampQpc, in BufferHeader header,
            byte[] buffer, ref int index, int payloadEnd);
    }
}
