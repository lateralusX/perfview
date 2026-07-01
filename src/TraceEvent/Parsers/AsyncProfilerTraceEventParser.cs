// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.Text;

using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

namespace Microsoft.Diagnostics.Tracing.Parsers
{
    /// <summary>
    /// Parser for the runtime's <c>System.Runtime.CompilerServices.AsyncProfilerEventSource</c>.
    /// <para>
    /// The EventSource emits a single <c>AsyncEvents</c> event (id = 1) whose payload is an opaque,
    /// densely packed binary buffer that batches many logical async-profiler sub-events. Each sub-event
    /// carries a delta-encoded timestamp and (for callstacks) delta-encoded method ids. This parser
    /// registers the raw <c>AsyncEvents</c> event so it round-trips through TraceLog/ETLX and shows up
    /// unaltered in the Events View, and additionally exposes <see cref="ParseBuffer(byte[], IAsyncProfilerSubEventSink)"/> which decodes the
    /// buffer into strongly typed sub-events for both the V2 (RuntimeAsync) and V1 (StateMachineAsync)
    /// instrumentation.
    /// </para>
    /// <para>
    /// The buffer/wire format is defined by <c>AsyncProfiler.cs</c> and
    /// <c>AsyncProfilerEventSource.cs</c> in dotnet/runtime. The framing is version tolerant: every
    /// sub-event is preceded by a payload-length prefix (0, 1, or 2 bytes, per the event manifest) so a
    /// decoder can skip sub-events (or trailing bytes of a sub-event) it does not understand.
    /// </para>
    /// </summary>
    public sealed class AsyncProfilerTraceEventParser : TraceEventParser
    {
        /// <summary>The EventSource provider name.</summary>
        public const string ProviderName = "System.Runtime.CompilerServices.AsyncProfilerEventSource";

        /// <summary>
        /// The provider GUID, derived from <see cref="ProviderName"/> using the standard EventSource
        /// name-to-GUID hashing algorithm (see TraceEventProviders.GetEventSourceGuidFromName).
        /// </summary>
        public static readonly Guid ProviderGuid = new Guid("7892f2f8-f81d-50d6-aea7-594d846c2274");

        /// <summary>The event id of the single raw <c>AsyncEvents</c> event.</summary>
        public const int AsyncEventsEventId = 1;

        /// <summary>The only buffer/header layout version this parser understands.</summary>
        public const byte SupportedBufferVersion = 1;

        private static volatile TraceEvent[] s_templates;

        public AsyncProfilerTraceEventParser(TraceEventSource source) : base(source)
        {
            ((ITraceParserServices)source).RegisterEventTemplate(AsyncEventsTemplate(OnRawAsyncEvents));
        }

        protected override string GetProviderName() => ProviderName;

        /// <summary>
        /// Subscribe to the raw <c>AsyncEvents</c> event (the undecoded buffer). Most consumers should
        /// instead use <see cref="ObserveSubEvents"/> to receive the decoded sub-events.
        /// </summary>
        public event Action<AsyncEventsTraceData> AsyncEvents
        {
            add
            {
                source.RegisterEventTemplate(AsyncEventsTemplate(value));
            }
            remove
            {
                source.UnregisterEventTemplate(value, AsyncEventsEventId, ProviderGuid);
            }
        }

        /// <summary>
        /// Registers <paramref name="sink"/> to receive every decoded sub-event carried by each raw
        /// <c>AsyncEvents</c> buffer. Multiple sinks may be registered; each is invoked in registration
        /// order for every sub-event.
        /// </summary>
        public void ObserveSubEvents(IAsyncProfilerSubEventSink sink)
        {
            if (sink == null)
            {
                throw new ArgumentNullException(nameof(sink));
            }

            _sinks = _sinks == null ? new[] { sink } : Append(_sinks, sink);
        }

        /// <summary>
        /// Decodes an <c>AsyncEvents</c> buffer, dispatching each contained sub-event to <paramref name="sink"/>.
        /// This is a pure function of the buffer bytes (it does not require a live <see cref="TraceEventSource"/>),
        /// which makes it directly testable and reusable by higher level computers.
        /// </summary>
        /// <param name="buffer">The raw async-profiler buffer (the <c>Buffer</c> payload of <see cref="AsyncEventsTraceData"/>).</param>
        /// <param name="sink">Receives the decoded sub-events and any parse errors.</param>
        /// <remarks>
        /// This overload frames sub-events using a fresh manifest seeded with the built-in v1 defaults (and
        /// any <c>AsyncProfilerMetadata</c> found within <paramref name="buffer"/> itself). To honor a
        /// manifest that was advertised in an earlier buffer, use the
        /// <see cref="ParseBuffer(byte[], AsyncProfilerManifest, IAsyncProfilerSubEventSink)"/> overload with
        /// a persistent <see cref="AsyncProfilerManifest"/>.
        /// </remarks>
        public static void ParseBuffer(byte[] buffer, IAsyncProfilerSubEventSink sink)
        {
            ParseBuffer(buffer, new AsyncProfilerManifest(), sink);
        }

        /// <summary>
        /// Decodes an <c>AsyncEvents</c> buffer, dispatching each contained sub-event to <paramref name="sink"/>,
        /// using <paramref name="manifest"/> to frame sub-events. The manifest supplies the payload-length
        /// prefix width for each event id (so events whose id or width this parser does not statically know can
        /// still be skipped) and is refreshed in place whenever an <c>AsyncProfilerMetadata</c> sub-event is
        /// decoded. Reuse a single <paramref name="manifest"/> instance across all buffers of a session, because
        /// the runtime advertises the manifest only once per configuration revision.
        /// </summary>
        /// <param name="buffer">The raw async-profiler buffer (the <c>Buffer</c> payload of <see cref="AsyncEventsTraceData"/>).</param>
        /// <param name="manifest">The live per-event manifest used to frame sub-events; updated in place from metadata.</param>
        /// <param name="sink">Receives the decoded sub-events and any parse errors.</param>
        public static void ParseBuffer(byte[] buffer, AsyncProfilerManifest manifest, IAsyncProfilerSubEventSink sink)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }
            if (manifest == null)
            {
                throw new ArgumentNullException(nameof(manifest));
            }
            if (sink == null)
            {
                throw new ArgumentNullException(nameof(sink));
            }

            if (!AsyncProfilerBufferHeader.TryRead(buffer, out AsyncProfilerBufferHeader header))
            {
                sink.OnParseError(new AsyncProfilerParseError("Invalid or truncated async-profiler buffer header", 0));
                return;
            }

            // Bound the decode by the header's declared total size so trailing padding past the logical
            // end of the buffer is never mis-decoded as a (bogus) sub-event. The physical buffer length
            // is the ultimate guard.
            int limit = header.TotalSize > 0 && header.TotalSize <= (uint)buffer.Length
                ? (int)header.TotalSize
                : buffer.Length;

            int index = AsyncProfilerBufferHeader.Size;
            long timestampQpc = header.StartTimestampQpc;

            try
            {
                while (index < limit)
                {
                    var eventId = (AsyncEventID)buffer[index++];

                    if (!AsyncProfilerReader.TryReadCompressedUInt64(buffer, ref index, out ulong deltaTicks))
                    {
                        sink.OnParseError(new AsyncProfilerParseError("Truncated sub-event timestamp delta", index));
                        return;
                    }
                    timestampQpc += (long)deltaTicks;

                    int payloadLength = ReadPayloadLengthPrefix(buffer, eventId, manifest, ref index);
                    int payloadStart = index;

                    DispatchSubEvent(eventId, timestampQpc, header, buffer, index, payloadLength, manifest, sink);

                    // The payload-length prefix is authoritative: always advance exactly past the payload,
                    // regardless of how many bytes the specific decoder consumed. This is what lets a parser
                    // that only understands version 1 of an event read the v1 fields it knows and then skip
                    // any fields a newer runtime appended (v2+), and skip whole sub-events it does not know.
                    index = payloadStart + payloadLength;
                }
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException || ex is ArgumentOutOfRangeException)
            {
                sink.OnParseError(new AsyncProfilerParseError("Buffer truncated: " + ex.Message, index));
            }
        }

        #region private

        private readonly AsyncProfilerManifest _manifest = new AsyncProfilerManifest();
        private IAsyncProfilerSubEventSink[] _sinks;
        private DispatchSink _dispatchSink;

        /// <summary>
        /// The live manifest this parser maintains across buffers (payload-length widths and schema versions
        /// per event id). It reflects the most recent <c>AsyncProfilerMetadata</c> seen so far.
        /// </summary>
        public AsyncProfilerManifest Manifest => _manifest;

        private void OnRawAsyncEvents(AsyncEventsTraceData data)
        {
            IAsyncProfilerSubEventSink[] sinks = _sinks;
            if (sinks == null || sinks.Length == 0)
            {
                return;
            }

            if (_dispatchSink == null)
            {
                _dispatchSink = new DispatchSink();
            }

            _dispatchSink.Targets = sinks;
            ParseBuffer(data.Buffer, _manifest, _dispatchSink);
        }

        private static IAsyncProfilerSubEventSink[] Append(IAsyncProfilerSubEventSink[] existing, IAsyncProfilerSubEventSink added)
        {
            var result = new IAsyncProfilerSubEventSink[existing.Length + 1];
            Array.Copy(existing, result, existing.Length);
            result[existing.Length] = added;
            return result;
        }

        protected internal override void EnumerateTemplates(Func<string, string, EventFilterResponse> eventsToObserve, Action<TraceEvent> callback)
        {
            if (s_templates == null)
            {
                var templates = new TraceEvent[1];
                templates[0] = AsyncEventsTemplate(null);
                s_templates = templates;
            }

            foreach (var template in s_templates)
            {
                if (eventsToObserve == null || eventsToObserve(template.ProviderName, template.EventName) == EventFilterResponse.AcceptEvent)
                {
                    callback(template);
                }
            }
        }

        private static AsyncEventsTraceData AsyncEventsTemplate(Action<AsyncEventsTraceData> action)
        {
            // eventID=1, task=1, opcode=Info (0); the single EventSource "AsyncEvents" event.
            return new AsyncEventsTraceData(action, AsyncEventsEventId, 1, "AsyncEvents", Guid.Empty, 0, "Info", ProviderGuid, ProviderName);
        }

        // Reads the per-sub-event payload-length prefix (0, 1, or 2 little-endian bytes, per the live
        // manifest) and advances past it, returning the declared payload length.
        private static int ReadPayloadLengthPrefix(byte[] buffer, AsyncEventID eventId, AsyncProfilerManifest manifest, ref int index)
        {
            switch (manifest.GetPayloadLengthFieldSize(eventId))
            {
                case PayloadLengthFieldSize.None:
                    return 0;
                case PayloadLengthFieldSize.Byte:
                    return buffer[index++];
                default: // UShort
                    int value = buffer[index] | (buffer[index + 1] << 8);
                    index += 2;
                    return value;
            }
        }

        private static void DispatchSubEvent(AsyncEventID eventId, long timestampQpc, in AsyncProfilerBufferHeader header,
            byte[] buffer, int index, int payloadLength, AsyncProfilerManifest manifest, IAsyncProfilerSubEventSink sink)
        {
            switch (eventId)
            {
                case AsyncEventID.CreateRuntimeAsyncContext:
                case AsyncEventID.CreateStateMachineAsyncContext:
                {
                    AsyncProfilerReader.TryReadCompressedUInt64(buffer, ref index, out ulong parentDispatcherId);
                    AsyncProfilerReader.TryReadCompressedUInt64(buffer, ref index, out ulong dispatcherId);
                    sink.OnContextCreate(new AsyncContextEvent(eventId, timestampQpc, header, parentDispatcherId, dispatcherId));
                    return;
                }
                case AsyncEventID.ResumeRuntimeAsyncContext:
                case AsyncEventID.ResumeStateMachineAsyncContext:
                {
                    AsyncProfilerReader.TryReadCompressedUInt64(buffer, ref index, out ulong dispatcherId);
                    sink.OnContextResume(new AsyncContextEvent(eventId, timestampQpc, header, 0, dispatcherId));
                    return;
                }
                case AsyncEventID.SuspendRuntimeAsyncContext:
                case AsyncEventID.SuspendStateMachineAsyncContext:
                {
                    sink.OnContextSuspend(new AsyncContextEvent(eventId, timestampQpc, header, 0, 0));
                    return;
                }
                case AsyncEventID.CompleteRuntimeAsyncContext:
                case AsyncEventID.CompleteStateMachineAsyncContext:
                {
                    sink.OnContextComplete(new AsyncContextEvent(eventId, timestampQpc, header, 0, 0));
                    return;
                }
                case AsyncEventID.UnwindRuntimeAsyncException:
                case AsyncEventID.UnwindStateMachineAsyncException:
                {
                    AsyncProfilerReader.TryReadCompressedUInt32(buffer, ref index, out uint unwoundFrames);
                    sink.OnException(new AsyncUnwindEvent(eventId, timestampQpc, header, unwoundFrames));
                    return;
                }
                case AsyncEventID.CreateRuntimeAsyncCallstack:
                case AsyncEventID.ResumeRuntimeAsyncCallstack:
                case AsyncEventID.SuspendRuntimeAsyncCallstack:
                case AsyncEventID.ResumeStateMachineAsyncCallstack:
                case AsyncEventID.AppendStateMachineAsyncCallstack:
                {
                    if (AsyncCallstackEvent.TryRead(eventId, timestampQpc, header, buffer, ref index, out AsyncCallstackEvent callstack))
                    {
                        sink.OnCallstack(callstack);
                    }
                    else
                    {
                        sink.OnParseError(new AsyncProfilerParseError("Truncated callstack payload for " + eventId, index));
                    }
                    return;
                }
                case AsyncEventID.ResumeRuntimeAsyncMethod:
                case AsyncEventID.ResumeStateMachineAsyncMethod:
                {
                    sink.OnMethodResume(new AsyncMethodEvent(eventId, timestampQpc, header));
                    return;
                }
                case AsyncEventID.CompleteRuntimeAsyncMethod:
                case AsyncEventID.CompleteStateMachineAsyncMethod:
                {
                    sink.OnMethodComplete(new AsyncMethodEvent(eventId, timestampQpc, header));
                    return;
                }
                case AsyncEventID.ResetAsyncThreadContext:
                {
                    sink.OnResetThreadContext(new AsyncNeutralEvent(eventId, timestampQpc, header));
                    return;
                }
                case AsyncEventID.ResetAsyncContinuationWrapperIndex:
                {
                    sink.OnResetContinuationWrapperIndex(new AsyncNeutralEvent(eventId, timestampQpc, header));
                    return;
                }
                case AsyncEventID.AsyncProfilerMetadata:
                {
                    if (AsyncMetadataEvent.TryRead(timestampQpc, header, buffer, index, payloadLength, out AsyncMetadataEvent metadata))
                    {
                        // Adopt the advertised manifest so subsequent sub-events (in this and later buffers)
                        // are framed with the runtime's authoritative payload-length widths and versions.
                        manifest.Apply(metadata);
                        sink.OnMetadata(metadata);
                    }
                    else
                    {
                        sink.OnParseError(new AsyncProfilerParseError("Truncated AsyncProfilerMetadata payload", index));
                    }
                    return;
                }
                case AsyncEventID.AsyncProfilerSyncClock:
                {
                    AsyncProfilerReader.TryReadCompressedUInt64(buffer, ref index, out ulong qpcSync);
                    AsyncProfilerReader.TryReadCompressedUInt64(buffer, ref index, out ulong utcSync);
                    sink.OnSyncClock(new AsyncSyncClockEvent(timestampQpc, header, qpcSync, utcSync));
                    return;
                }
                default:
                {
                    // Unknown/future sub-event. The payload-length prefix lets ParseBuffer skip it safely,
                    // so simply report it and let the caller decide whether to care.
                    sink.OnUnknown(new AsyncUnknownEvent(eventId, timestampQpc, header, payloadLength));
                    return;
                }
            }
        }

        // Fans a single decode out to a snapshot of the registered sinks.
        private sealed class DispatchSink : IAsyncProfilerSubEventSink
        {
            public IAsyncProfilerSubEventSink[] Targets;

            public void OnContextCreate(in AsyncContextEvent e) { foreach (var t in Targets) t.OnContextCreate(e); }
            public void OnContextResume(in AsyncContextEvent e) { foreach (var t in Targets) t.OnContextResume(e); }
            public void OnContextSuspend(in AsyncContextEvent e) { foreach (var t in Targets) t.OnContextSuspend(e); }
            public void OnContextComplete(in AsyncContextEvent e) { foreach (var t in Targets) t.OnContextComplete(e); }
            public void OnException(in AsyncUnwindEvent e) { foreach (var t in Targets) t.OnException(e); }
            public void OnCallstack(in AsyncCallstackEvent e) { foreach (var t in Targets) t.OnCallstack(e); }
            public void OnMethodResume(in AsyncMethodEvent e) { foreach (var t in Targets) t.OnMethodResume(e); }
            public void OnMethodComplete(in AsyncMethodEvent e) { foreach (var t in Targets) t.OnMethodComplete(e); }
            public void OnResetThreadContext(in AsyncNeutralEvent e) { foreach (var t in Targets) t.OnResetThreadContext(e); }
            public void OnResetContinuationWrapperIndex(in AsyncNeutralEvent e) { foreach (var t in Targets) t.OnResetContinuationWrapperIndex(e); }
            public void OnMetadata(in AsyncMetadataEvent e) { foreach (var t in Targets) t.OnMetadata(e); }
            public void OnSyncClock(in AsyncSyncClockEvent e) { foreach (var t in Targets) t.OnSyncClock(e); }
            public void OnUnknown(in AsyncUnknownEvent e) { foreach (var t in Targets) t.OnUnknown(e); }
            public void OnParseError(in AsyncProfilerParseError e) { foreach (var t in Targets) t.OnParseError(e); }
        }

        #endregion
    }

    /// <summary>
    /// Raw <c>AsyncEvents</c> ETW/EventPipe event template. Exposes the encoded buffer untouched so that
    /// filters / Events View / TraceLog round-trip work unchanged. Use
    /// <see cref="AsyncProfilerTraceEventParser.ParseBuffer(byte[], IAsyncProfilerSubEventSink)"/> to decode <see cref="Buffer"/>.
    /// </summary>
    public sealed class AsyncEventsTraceData : TraceEvent
    {
        private static readonly string[] s_payloadNames = new[] { "Length", "Buffer" };

        /// <summary>The length (in bytes) of the encoded buffer (an EventSource int32 length prefix).</summary>
        public int Length { get { return GetInt32At(0); } }

        /// <summary>The encoded async-profiler buffer. Decode with <see cref="AsyncProfilerTraceEventParser.ParseBuffer(byte[], IAsyncProfilerSubEventSink)"/>.</summary>
        public byte[] Buffer { get { return GetByteArrayAt(4, Length); } }

        #region Private
        internal AsyncEventsTraceData(Action<AsyncEventsTraceData> action, int eventID, int task, string taskName, Guid taskGuid, int opcode, string opcodeName, Guid providerGuid, string providerName)
            : base(eventID, task, taskName, taskGuid, opcode, opcodeName, providerGuid, providerName)
        {
            m_target = action;
        }

        protected internal override void Dispatch()
        {
            m_target?.Invoke(this);
        }

        protected internal override void Validate()
        {
            Debug.Assert(4 <= EventDataLength);
            Debug.Assert(4 + Length <= EventDataLength);
        }

        protected internal override Delegate Target
        {
            get { return m_target; }
            set { m_target = (Action<AsyncEventsTraceData>)value; }
        }

        public override StringBuilder ToXml(StringBuilder sb)
        {
            Prefix(sb);
            XmlAttrib(sb, "Length", Length);
            sb.Append("/>");
            return sb;
        }

        public override string[] PayloadNames { get { return s_payloadNames; } }

        public override object PayloadValue(int index)
        {
            switch (index)
            {
                case 0:
                    return Length;
                case 1:
                    return Buffer;
                default:
                    Debug.Assert(false, "Bad field index");
                    return null;
            }
        }

        private event Action<AsyncEventsTraceData> m_target;
        #endregion
    }
}
