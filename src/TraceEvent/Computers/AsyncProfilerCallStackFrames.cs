// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using FastSerialization;

using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>
    /// A compact, deduplicated handle to an <see cref="AsyncProfilerCallStackFrames"/> interned in an
    /// <see cref="AsyncProfilerCallStacksIndex"/>.
    /// </summary>
    internal enum AsyncProfilerCallStackFramesIndex
    {
        /// <summary>No interned frame list.</summary>
        Invalid = -1,
    }

    /// <summary>
    /// The frames of an async call stack, ordered leaf-first, interned (deduplicated) across the trace.
    /// <see cref="MethodIdAt"/> returns native code IPs for both instrumentation families: runtime-async
    /// continuation IPs for V2 and state-machine <c>MoveNext</c> body IPs for V1. V1 frames also carry raw signed
    /// compiler states. Method ids are not CLR MethodLoad identifiers or MethodDesc handles.
    /// </summary>
    internal sealed class AsyncProfilerCallStackFrames
    {
        /// <summary>The V1 or V2 instrumentation family that emitted these frames.</summary>
        public AsyncCallstackKind Kind { get; }
        /// <summary>The number of frames, ordered leaf-to-root.</summary>
        public int FrameCount => _methodIds.Length;
        /// <summary>The native code IP emitted for a frame, or 0 for a runtime-emitted sentinel.</summary>
        public ulong MethodIdAt(int index) => _methodIds[index];
        /// <summary>The raw signed V1 compiler state for a frame, not an IL/native offset; 0 for V2.</summary>
        public int FrameStateAt(int index) => _frameStates != null ? _frameStates[index] : 0;

        /// <summary>
        /// The registered <see cref="CodeAddressIndex"/> for frame <paramref name="index"/> in the originating
        /// <see cref="TraceLog.CodeAddresses"/> table, or <see cref="CodeAddressIndex.Invalid"/> if no address was
        /// registered or the index is out of range. A valid code-address index does not guarantee a resolved
        /// method or module; use <see cref="TraceCodeAddresses.MethodIndex"/> to check method resolution.
        /// Names and source information remain owned by the originating TraceLog, not this frame list.
        /// </summary>
        public CodeAddressIndex CodeAddressAt(int index) =>
            _codeAddresses != null && (uint)index < (uint)_codeAddresses.Length ? _codeAddresses[index] : CodeAddressIndex.Invalid;

        #region private

        internal AsyncProfilerCallStackFrames(AsyncCallstackKind kind, ulong[] methodIds, int[] frameStates, ProcessIndex processIndex = 0)
        {
            Kind = kind;
            _methodIds = methodIds;
            _frameStates = frameStates;
            ProcessIndex = processIndex;
        }

        /// <summary>The process these frames were captured in. Build-time only (used to resolve addresses); not serialized.</summary>
        internal ProcessIndex ProcessIndex { get; }

        /// <summary>
        /// Assigns the resolved code address for frame <paramref name="index"/> (called by <see cref="TraceLog"/> at
        /// build time as methods are discovered). Lazily allocates the per-frame array, defaulting unset frames to
        /// <see cref="CodeAddressIndex.Invalid"/>.
        /// </summary>
        internal void SetCodeAddressAt(int index, CodeAddressIndex codeAddress)
        {
            if (_codeAddresses == null)
            {
                _codeAddresses = new CodeAddressIndex[_methodIds.Length];
                for (int i = 0; i < _codeAddresses.Length; i++)
                {
                    _codeAddresses[i] = CodeAddressIndex.Invalid;
                }
            }
            _codeAddresses[index] = codeAddress;
        }

        internal void Write(Serializer serializer)
        {
            serializer.Write((byte)Kind);
            serializer.Write(_methodIds.Length);
            for (int i = 0; i < _methodIds.Length; i++)
            {
                serializer.Write((long)_methodIds[i]);
            }
            if (_frameStates == null)
            {
                serializer.Write(-1);
            }
            else
            {
                serializer.Write(_frameStates.Length);
                for (int i = 0; i < _frameStates.Length; i++)
                {
                    serializer.Write(_frameStates[i]);
                }
            }
            if (_codeAddresses == null)
            {
                serializer.Write(-1);
            }
            else
            {
                serializer.Write(_codeAddresses.Length);
                for (int i = 0; i < _codeAddresses.Length; i++)
                {
                    serializer.Write((int)_codeAddresses[i]);
                }
            }
        }

        internal static AsyncProfilerCallStackFrames Read(Deserializer deserializer)
        {
            var kind = (AsyncCallstackKind)deserializer.ReadByte();
            int frameCount = deserializer.ReadInt();
            var methodIds = new ulong[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                methodIds[i] = (ulong)deserializer.ReadInt64();
            }
            int stateCount = deserializer.ReadInt();
            int[] frameStates = null;
            if (stateCount >= 0)
            {
                frameStates = new int[stateCount];
                for (int i = 0; i < stateCount; i++)
                {
                    frameStates[i] = deserializer.ReadInt();
                }
            }
            var frames = new AsyncProfilerCallStackFrames(kind, methodIds, frameStates);
            int codeAddrCount = deserializer.ReadInt();
            if (codeAddrCount >= 0)
            {
                var codeAddresses = new CodeAddressIndex[codeAddrCount];
                for (int i = 0; i < codeAddrCount; i++)
                {
                    codeAddresses[i] = (CodeAddressIndex)deserializer.ReadInt();
                }
                frames._codeAddresses = codeAddresses;
            }
            return frames;
        }

        private readonly ulong[] _methodIds;
        private readonly int[] _frameStates; // null for runtime callstacks
        private CodeAddressIndex[] _codeAddresses; // per-frame resolved code address; null until symbolized

        #endregion
    }
}
