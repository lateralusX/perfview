// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>
    /// Read-only identity and provenance of a captured or reconstructed frame. Frame identities belong to the
    /// originating TraceLog. Custom transforms can remove/reorder frames or insert explicitly synthetic labels.
    /// </summary>
    /// <remarks>
    /// Captured-frame views from query results can be retained. Frames supplied to a custom transform may refer
    /// to reused activation objects; copying the struct does not detach those references. Do not retain them
    /// beyond the callback. A default value has no valid identity or label and cannot be inserted into a transform.
    /// </remarks>
    public readonly struct AsyncProfilerCallStackFrame
    {
        /// <summary>Creates a synthetic display label with no physical or captured async identity.</summary>
        /// <exception cref="ArgumentException"><paramref name="displayName"/> is null or empty.</exception>
        public AsyncProfilerCallStackFrame(string displayName)
        {
            if (string.IsNullOrEmpty(displayName))
            {
                throw new ArgumentException("A synthetic frame requires a nonempty display name.", nameof(displayName));
            }
            m_frame = AsyncProfilerStitchedFrame.CreateSynthetic(displayName);
            m_initialized = true;
        }

        /// <summary>The code address in the originating TraceLog, or Invalid when unavailable.
        /// A valid address need not have resolved symbols. Current/completing frames retain the sampled
        /// address; a box-carrier frame retains its physical runtime helper's address, not the logical target's IP.</summary>
        public CodeAddressIndex CodeAddress => m_initialized ? m_frame.CodeAddress : CodeAddressIndex.Invalid;

        /// <summary>The captured async IP, including a zero-IP sentinel; zero for non-async frames.
        /// This can differ from CodeAddress's physical IP for reconstructed current/completing frames and box carriers.</summary>
        public ulong CapturedInstructionPointer => m_frame.Segment == null
            ? 0 : m_frame.Segment.MethodIdAt(m_frame.SegmentFrameIndex);

        /// <summary>The raw captured V1 compiler state, or null for V2 and non-async frames.
        /// Inferred running/completed presentation states never replace this value.</summary>
        public int? CapturedState => m_frame.Segment?.Kind == AsyncCallstackKind.StateMachineAsync
            ? m_frame.Segment.FrameStateAt(m_frame.SegmentFrameIndex) : (int?)null;

        /// <summary>The associated activation, or null when none. Parent-context frames identify the historical
        /// parent activation, which need not appear in the callback's active Segments list.
        /// Activations supplied during transform callbacks must not be retained after the callback.</summary>
        public AsyncProfilerCallStack Context => m_frame.Context;

        /// <summary>True when the frame represents physically sampled execution, rather than a logical
        /// caller, parent context, dispatch target, or synthetic label.</summary>
        public bool IsPhysical => m_initialized &&
            (m_frame.Origin == AsyncProfilerStitchedFrameOrigin.Sync ||
             (m_frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent && !m_frame.IsStateMachineBoxCarrier) ||
             m_frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompleting);

        /// <summary>True for the physically matched current async method, not a dispatch or box target.</summary>
        public bool IsCurrent => m_frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCurrent;

        /// <summary>True for a historical parent resume/append context reached through a child's creation
        /// relationship. This is not a newly captured creation-time stack, physical execution at sample time,
        /// or an ordinary suspended async caller.</summary>
        public bool IsParentContext => m_frame.ContextKind == AsyncProfilerStitchedFrameContextKind.ParentContext;

        /// <summary>True when progress records identify this as the current logical parent frame at child
        /// creation. This does not establish physical execution or replace its captured state or IP.</summary>
        public bool IsParentContextCurrent => m_frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncContextParentCurrent;

        /// <summary>True for an indexed resumption target without proven application-body execution.</summary>
        public bool IsDispatchTarget => m_frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncDispatchTarget;

        /// <summary>True when a V1 dispatch target was identified through a physical runtime box carrier.</summary>
        public bool IsStateMachineBoxCarrier => m_frame.IsStateMachineBoxCarrier;

        /// <summary>True for a physically present invocation whose async operation has logically completed.</summary>
        public bool IsCompleting => m_frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompleting;

        /// <summary>True for verified physical return-unwind work attributed to a completed logical root.
        /// The root body need not be physically present, so this does not set IsPhysical.</summary>
        public bool IsCompletionUnwind => m_frame.Origin == AsyncProfilerStitchedFrameOrigin.AsyncCompletionUnwind;

        /// <summary>The explicit label for a synthetic frame; null for captured or reconstructed identities.
        /// This is not the formatted method name or the final annotated stack-source frame name.</summary>
        public string DisplayName => m_frame.DisplayName;

        #region private

        internal AsyncProfilerCallStackFrame(AsyncProfilerStitchedFrame frame)
        {
            m_frame = frame;
            m_initialized = true;
        }

        internal AsyncProfilerStitchedFrame GetFrame()
        {
            if (!m_initialized)
            {
                throw new ArgumentException("A default frame has no identity or display label.");
            }
            return m_frame;
        }

        private readonly AsyncProfilerStitchedFrame m_frame;
        private readonly bool m_initialized;

        #endregion
    }
}
