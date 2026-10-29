// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>
    /// A custom presentation callback's view after structural reconstruction and enabled built-in transforms.
    /// </summary>
    /// <remarks>
    /// The context, frame collection, segment collection, and activation objects are temporary callback views.
    /// Do not retain them or access them after the callback returns. Copy needed scalar identities instead.
    /// The context and its frame-collection adapter are reused across invocations.
    /// Edits change presentation, not the persisted index or recorded execution evidence.
    /// Copying a frame struct or segment list does not detach reused activation references.
    /// </remarks>
    public sealed class AsyncProfilerCallStackTransformContext
    {
        /// <summary>The TraceLog owning the frame identities.</summary>
        public TraceLog TraceLog => m_state.TraceLog;

        /// <summary>The exact reconstruction timestamp in the trace's QPC domain.</summary>
        public long TimestampQpc => m_state.TimestampQpc;

        /// <summary>The active captured segments on the physical stack's thread, outermost-to-innermost.
        /// Historical parents inserted into Frames are not added to this list; use each frame's Context.</summary>
        public IReadOnlyList<AsyncProfilerCallStack> Segments => m_state.Segments;

        /// <summary>The mutable stitched frame sequence, leaf-to-root, after enabled parent-context expansion
        /// and built-in transforms, but before frame-name annotations, generated context-transition markers,
        /// and grouping roots. Frames may be removed, reordered, or replaced; additional labels can be created
        /// with new AsyncProfilerCallStackFrame("label"). Reused identities must belong to this TraceLog.
        /// Inserting or replacing with a default frame throws ArgumentException.</summary>
        public IList<AsyncProfilerCallStackFrame> Frames { get; }

        #region private

        internal AsyncProfilerCallStackTransformContext(Action<AsyncProfilerCallStackTransformContext> transform)
        {
            m_transform = transform;
            Frames = new FrameList(this);
        }

        internal void Invoke(AsyncProfilerCallStackTransformState state)
        {
            m_state = state;
            try
            {
                m_transform(this);
            }
            finally
            {
                m_state = default;
            }
        }

        private sealed class FrameList : IList<AsyncProfilerCallStackFrame>
        {
            internal FrameList(AsyncProfilerCallStackTransformContext context) { m_context = context; }

            public int Count => CurrentFrames.Count;
            public bool IsReadOnly => false;
            public AsyncProfilerCallStackFrame this[int index]
            {
                get => new AsyncProfilerCallStackFrame(CurrentFrames[index]);
                set => CurrentFrames[index] = value.GetFrame();
            }
            public void Add(AsyncProfilerCallStackFrame item) => CurrentFrames.Add(item.GetFrame());
            public void Clear() => CurrentFrames.Clear();
            public void Insert(int index, AsyncProfilerCallStackFrame item) => CurrentFrames.Insert(index, item.GetFrame());
            public void RemoveAt(int index) => CurrentFrames.RemoveAt(index);
            public bool Contains(AsyncProfilerCallStackFrame item) => IndexOf(item) >= 0;
            public int IndexOf(AsyncProfilerCallStackFrame item) => CurrentFrames.IndexOf(item.GetFrame());
            public bool Remove(AsyncProfilerCallStackFrame item) => CurrentFrames.Remove(item.GetFrame());
            public void CopyTo(AsyncProfilerCallStackFrame[] array, int arrayIndex)
            {
                if (array == null)
                {
                    throw new ArgumentNullException(nameof(array));
                }
                if (arrayIndex < 0 || arrayIndex > array.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(arrayIndex));
                }
                if (array.Length - arrayIndex < Count)
                {
                    throw new ArgumentException("The destination array is too small.", nameof(array));
                }
                for (int i = 0; i < Count; i++)
                {
                    array[arrayIndex + i] = this[i];
                }
            }
            public IEnumerator<AsyncProfilerCallStackFrame> GetEnumerator()
            {
                foreach (AsyncProfilerStitchedFrame frame in CurrentFrames)
                {
                    yield return new AsyncProfilerCallStackFrame(frame);
                }
            }
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            private List<AsyncProfilerStitchedFrame> CurrentFrames => m_context.m_state.Frames ??
                throw new InvalidOperationException("The frame collection is only available during its callback.");

            private readonly AsyncProfilerCallStackTransformContext m_context;
        }

        private readonly Action<AsyncProfilerCallStackTransformContext> m_transform;
        private AsyncProfilerCallStackTransformState m_state;

        #endregion
    }
}
