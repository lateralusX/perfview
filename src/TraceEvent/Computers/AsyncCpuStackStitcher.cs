// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>Where a <see cref="StitchedFrame"/> in a stitched call stack came from.</summary>
    public enum StitchedFrameOrigin
    {
        /// <summary>A real frame taken verbatim from the native (CPU-sample) sync stack.</summary>
        Sync = 0,

        /// <summary>The physically present current V1 state-machine frame. It retains the native code address
        /// for identity/source correlation, but the emitter may present it as the logical async method.</summary>
        AsyncCurrent,

        /// <summary>A suspended-ancestry frame spliced in from an async call stack segment (an async frame that
        /// is <b>not</b> physically present on the native sync stack because it is awaiting).</summary>
        AsyncRemaining,
    }

    /// <summary>
    /// One frame of a native (CPU-sample) call stack, as consumed by <see cref="AsyncCpuStackStitcher"/>.
    /// Carries both the <see cref="CodeAddressIndex"/> (for interning the frame into the output stack source)
    /// and the <see cref="MethodIndex"/> (for the V1 completed-frame identity match) so the stitcher does no
    /// symbol lookups of its own.
    /// </summary>
    public readonly struct StitchSyncFrame
    {
        /// <summary>The frame's code address (used to intern it and to classify it as an async boundary).</summary>
        public readonly CodeAddressIndex CodeAddress;

        /// <summary>The frame's method (used for the V1 inline-<c>MoveNext</c> identity match).</summary>
        public readonly MethodIndex Method;

        public StitchSyncFrame(CodeAddressIndex codeAddress, MethodIndex method)
        {
            CodeAddress = codeAddress;
            Method = method;
        }
    }

    /// <summary>
    /// One frame of a stitched call stack: either a real sync frame or an async-ancestry frame spliced in for a
    /// suspended await. For an async frame, <see cref="CodeAddress"/> may be
    /// <see cref="CodeAddressIndex.Invalid"/> (unsymbolized or a synthetic <c>0</c>-IP async frame), in which
    /// case the emitter applies the async leaf policy using <see cref="Segment"/> / <see cref="SegmentFrameIndex"/>.
    /// </summary>
    public readonly struct StitchedFrame
    {
        /// <summary>Where this frame came from.</summary>
        public readonly StitchedFrameOrigin Origin;

        /// <summary>The frame's code address, or <see cref="CodeAddressIndex.Invalid"/> for an unsymbolized async frame.</summary>
        public readonly CodeAddressIndex CodeAddress;

        /// <summary>For an async-origin frame, the source async segment; otherwise null.</summary>
        public readonly AsyncCallStackFrames Segment;

        /// <summary>For an async-origin frame, its index within <see cref="Segment"/>; otherwise -1.</summary>
        public readonly int SegmentFrameIndex;

        private StitchedFrame(StitchedFrameOrigin origin, CodeAddressIndex codeAddress, AsyncCallStackFrames segment, int segmentFrameIndex)
        {
            Origin = origin;
            CodeAddress = codeAddress;
            Segment = segment;
            SegmentFrameIndex = segmentFrameIndex;
        }

        internal static StitchedFrame Sync(CodeAddressIndex codeAddress) =>
            new StitchedFrame(StitchedFrameOrigin.Sync, codeAddress, null, -1);

        internal static StitchedFrame AsyncCurrent(
            CodeAddressIndex codeAddress, AsyncCallStackFrames segment, int segmentFrameIndex) =>
            new StitchedFrame(StitchedFrameOrigin.AsyncCurrent, codeAddress, segment, segmentFrameIndex);

        internal static StitchedFrame Async(AsyncCallStackFrames segment, int segmentFrameIndex) =>
            new StitchedFrame(StitchedFrameOrigin.AsyncRemaining, segment.CodeAddressAt(segmentFrameIndex), segment, segmentFrameIndex);
    }

    /// <summary>
    /// Soft diagnostics gathered while stitching. None of these conditions throw; they record anomalies that a
    /// caller (or a test) can assert on. A clean stitch has all counters at 0.
    /// </summary>
    public sealed class StitchDiagnostics
    {
        private const int MaximumMessageCount = 1_024;

        /// <summary>Number of async segments consumed.</summary>
        public int SegmentsProcessed;

        /// <summary>Segments whose expected sync-stack dispatch boundary was not found (stitch degraded to append-only).</summary>
        public int BoundariesNotFound;

        /// <summary>V2 segments where the current async method did not match the sync frame directly leaf-ward of the
        /// boundary (the next lower leaf-&gt;root index, boundaryPos-1).</summary>
        public int AdjacencyMismatches;

        /// <summary>Segments whose completed-frame count came from the continuation-wrapper slot (no completion events).</summary>
        public int V2WrapperFallbackUsed;

        /// <summary>V1 segments whose completed-frame count was derived from the inline sync frames (no completion events).</summary>
        public int V1InlineFallbackUsed;

        /// <summary>V2 samples emitted verbatim as the sync layout (not stitched) because no continuation-wrapper
        /// sat root-ward of the sampled leaf (at a higher leaf-&gt;root index): either the wrapper was the leaf itself,
        /// or there was no wrapper but the sample sat in dispatch-continuation plumbing. These frames are dispatch
        /// machinery, not a resumed continuation body, so there is no suspended ancestry to splice.</summary>
        public int V2SyncLayoutUsed;

        /// <summary>V2 samples (a subset of <see cref="V2SyncLayoutUsed"/>) where the sampled leaf frame was a
        /// continuation-wrapper that was dropped, leaving the dispatch-continuation frame root-ward of it (the next
        /// leaf-&gt;root index) as the leaf.</summary>
        public int V2LeafWrapperDropped;

        /// <summary>V2 dispatch-continuation plumbing frames (<c>InstrumentedDispatchContinuations</c> /
        /// <c>DispatchContinuations</c>) collapsed root-ward of a stitched continuation-wrapper, since the spliced
        /// logical ancestry already represents that machinery.</summary>
        public int V2PlumbingFramesCollapsed;

        /// <summary>V1 CoreLib async-state-machine-box frames collapsed contiguously root-ward of a consumed
        /// dispatcher boundary.</summary>
        public int V1InfrastructureFramesCollapsed;

        /// <summary>Human-readable notes: the anomalies above, plus per-segment happy-path trace steps when
        /// stitching is run with tracing enabled.</summary>
        public IReadOnlyList<string> Messages => m_messages ?? (IReadOnlyList<string>)Array.Empty<string>();

        /// <summary>Number of diagnostic messages omitted after the bounded message collection reached capacity.</summary>
        public int MessagesDropped;

        internal int MessageLimit { get; set; } = MaximumMessageCount;

        internal int RemainingMessageCapacity =>
            Math.Max(0, MessageLimit - (m_messages?.Count ?? 0));

        internal bool CanRetainMessage => RemainingMessageCapacity != 0;

        internal void Note(string message)
        {
            if (m_messages == null)
            {
                m_messages = new List<string>();
            }

            if (m_messages.Count < MessageLimit)
            {
                m_messages.Add(message);
            }
            else
            {
                MessagesDropped++;
            }
        }

        internal void Clear()
        {
            SegmentsProcessed = 0;
            BoundariesNotFound = 0;
            AdjacencyMismatches = 0;
            V2WrapperFallbackUsed = 0;
            V1InlineFallbackUsed = 0;
            V2SyncLayoutUsed = 0;
            V2LeafWrapperDropped = 0;
            V2PlumbingFramesCollapsed = 0;
            V1InfrastructureFramesCollapsed = 0;
            MessagesDropped = 0;
            m_messages?.Clear();
        }

        internal void AddTo(StitchDiagnostics target)
        {
            target.SegmentsProcessed += SegmentsProcessed;
            target.BoundariesNotFound += BoundariesNotFound;
            target.AdjacencyMismatches += AdjacencyMismatches;
            target.V2WrapperFallbackUsed += V2WrapperFallbackUsed;
            target.V1InlineFallbackUsed += V1InlineFallbackUsed;
            target.V2SyncLayoutUsed += V2SyncLayoutUsed;
            target.V2LeafWrapperDropped += V2LeafWrapperDropped;
            target.V2PlumbingFramesCollapsed += V2PlumbingFramesCollapsed;
            target.V1InfrastructureFramesCollapsed += V1InfrastructureFramesCollapsed;
            target.MessagesDropped += MessagesDropped;
            if (m_messages != null)
            {
                for (int i = 0; i < m_messages.Count; i++)
                {
                    target.Note(m_messages[i]);
                }
            }
        }

        #region private

        private List<string> m_messages;

        #endregion
    }

    /// <summary>The result of a stitch: the merged frames (leaf-&gt;root) and the diagnostics.</summary>
    public sealed class StitchResult
    {
        internal StitchResult(IReadOnlyList<StitchedFrame> frames, StitchDiagnostics diagnostics)
        {
            Frames = frames;
            Diagnostics = diagnostics;
        }

        /// <summary>The stitched frames, leaf-&gt;root (the native leaf first, the thread/process root last).</summary>
        public IReadOnlyList<StitchedFrame> Frames { get; }

        /// <summary>Soft anomalies observed while stitching.</summary>
        public StitchDiagnostics Diagnostics { get; }
    }

    /// <summary>
    /// The pure async-CPU stitch core: merges a native (CPU-sample) sync stack with the async call stack
    /// segments active at the same instant into a single logical call stack, splicing each suspended await's
    /// ancestry in place of its dispatch machinery.
    /// <para>
    /// This is an <b>interleaved, segment-lockstep, index-aware two-pointer replacement walk</b> (see the design
    /// note): one pointer down the sync stack (leaf-&gt;root), one down the async segment list (leaf-&gt;root),
    /// consumed in strict lockstep. For each segment the walk (a) finds the sync frame that is the segment's
    /// dispatch boundary, (b) emits the real sync frames leaf-ward of it (they are the segment's already-completed
    /// and currently-running frames, physically present on the native stack), then (c) splices the segment's
    /// root-ward remaining frames (the suspended ancestry) in place of the dispatch machinery.
    /// </para>
    /// <para>
    /// This type is intentionally free of any <c>StackSource</c> / interning dependency: it consumes materialized
    /// <see cref="StitchSyncFrame"/>s and produces a flat <see cref="StitchResult"/> so it can be unit-tested in
    /// isolation. A separate emitter turns the result into a <c>MutableTraceEventStackSource</c> call stack.
    /// </para>
    /// </summary>
    public static class AsyncCpuStackStitcher
    {
        /// <summary>
        /// Stitches <paramref name="syncLeafToRoot"/> (a native CPU-sample stack, leaf-&gt;root) with the async
        /// call stacks <paramref name="segmentsRootToLeaf"/> active at <paramref name="qpc"/> (as returned by
        /// <see cref="AsyncCallStacksIndex.GetAsyncCallStacks(AsyncThreadKey, long)"/>, i.e. depth-ascending =
        /// root-&gt;leaf). Convenience overload over
        /// <see cref="Stitch(IReadOnlyList{StitchSyncFrame}, IReadOnlyList{AsyncCallStack}, long, Func{CodeAddressIndex, AsyncStitchBoundaryInfo}, Func{AsyncCallstackKind, bool}, Func{CodeAddressIndex, MethodIndex}, bool)"/>
        /// that sources boundary classification from a <see cref="AsyncStitchBoundaryCache"/> and the
        /// completion-events-observed predicate from an <see cref="AsyncCallStacksIndex"/>.
        /// </summary>
        /// <param name="syncLeafToRoot">The native sync stack, leaf frame first, thread/process root last.</param>
        /// <param name="segmentsRootToLeaf">The async segments active at <paramref name="qpc"/>, root-&gt;leaf
        /// (they are reversed internally to leaf-&gt;root to walk in lockstep with the sync stack).</param>
        /// <param name="qpc">The sample time, in the trace's QPC domain.</param>
        /// <param name="boundaries">The per-trace boundary-method cache.</param>
        /// <param name="index">The async index, queried for whether completion events were emitted per process and kind.</param>
        /// <param name="processIndex">The process instance that emitted the sampled stack.</param>
        /// <param name="methodOf">Maps a <see cref="CodeAddressIndex"/> to its <see cref="MethodIndex"/>
        /// (typically <c>traceLog.CodeAddresses.MethodIndex</c>); used for the V1 identity match and V2 adjacency check.</param>
        /// <param name="trace">When true, a per-segment happy-path note (kind / boundary / completed count /
        /// spliced range) is appended to <see cref="StitchDiagnostics.Messages"/> in addition to the anomaly
        /// notes. Off by default; intended for debugging the stitch flow.</param>
        public static StitchResult Stitch(
            IReadOnlyList<StitchSyncFrame> syncLeafToRoot,
            IReadOnlyList<AsyncCallStack> segmentsRootToLeaf,
            long qpc,
            AsyncStitchBoundaryCache boundaries,
            AsyncCallStacksIndex index,
            ProcessIndex processIndex,
            Func<CodeAddressIndex, MethodIndex> methodOf,
            bool trace = false)
        {
            if (boundaries is null) throw new ArgumentNullException(nameof(boundaries));
            if (index is null) throw new ArgumentNullException(nameof(index));
            return Stitch(syncLeafToRoot, segmentsRootToLeaf, qpc, boundaries.Classify,
                kind => index.MethodCompletionObserved(processIndex, kind), methodOf, trace);
        }

        /// <summary>
        /// Stitches <paramref name="syncLeafToRoot"/> (a native CPU-sample stack, leaf-&gt;root) with the async
        /// call stacks <paramref name="segmentsRootToLeaf"/> active at <paramref name="qpc"/> (depth-ascending =
        /// root-&gt;leaf). This overload takes the boundary classifier and completion-events predicate as delegates
        /// so it has no dependency on <see cref="AsyncStitchBoundaryCache"/> / <see cref="AsyncCallStacksIndex"/>
        /// and can be unit-tested with synthetic code addresses.
        /// </summary>
        /// <param name="syncLeafToRoot">The native sync stack, leaf frame first, thread/process root last.</param>
        /// <param name="segmentsRootToLeaf">The async segments active at <paramref name="qpc"/>, root-&gt;leaf.</param>
        /// <param name="qpc">The sample time, in the trace's QPC domain.</param>
        /// <param name="classify">Classifies a sync frame's <see cref="CodeAddressIndex"/> as an async dispatch boundary.</param>
        /// <param name="methodCompletionObserved">True if <c>CompleteMethod</c> (normal completion) events were
        /// emitted for the given kind (see <see cref="AsyncCallStacksIndex.MethodCompletionObserved(ProcessIndex, AsyncCallstackKind)"/>). Exceptional
        /// unwinds are added unconditionally from the segment's <c>Unwind</c> deltas.</param>
        /// <param name="methodOf">Maps a <see cref="CodeAddressIndex"/> to its <see cref="MethodIndex"/>; used for
        /// the V1 identity match and V2 adjacency check.</param>
        /// <param name="trace">When true, appends a per-segment happy-path note (kind / boundary / completed count /
        /// spliced range) to <see cref="StitchDiagnostics.Messages"/>. Off by default; for debugging the flow.</param>
        public static StitchResult Stitch(
            IReadOnlyList<StitchSyncFrame> syncLeafToRoot,
            IReadOnlyList<AsyncCallStack> segmentsRootToLeaf,
            long qpc,
            Func<CodeAddressIndex, AsyncStitchBoundaryInfo> classify,
            Func<AsyncCallstackKind, bool> methodCompletionObserved,
            Func<CodeAddressIndex, MethodIndex> methodOf,
            bool trace = false)
        {
            if (syncLeafToRoot is null) throw new ArgumentNullException(nameof(syncLeafToRoot));
            if (classify is null) throw new ArgumentNullException(nameof(classify));
            if (methodCompletionObserved is null) throw new ArgumentNullException(nameof(methodCompletionObserved));
            if (methodOf is null) throw new ArgumentNullException(nameof(methodOf));

            var diagnostics = new StitchDiagnostics();
            var output = new List<StitchedFrame>(syncLeafToRoot.Count + 8);
            StitchInto(syncLeafToRoot, segmentsRootToLeaf, qpc, classify, methodCompletionObserved, null,
                ProcessIndex.Invalid, methodOf, trace, output, diagnostics);
            return new StitchResult(output, diagnostics);
        }

        internal static void StitchInto(
            IReadOnlyList<StitchSyncFrame> syncLeafToRoot,
            IReadOnlyList<AsyncCallStack> segmentsRootToLeaf,
            long qpc,
            Func<CodeAddressIndex, AsyncStitchBoundaryInfo> classify,
            Func<ProcessIndex, AsyncCallstackKind, bool> methodCompletionObserved,
            ProcessIndex processIndex,
            Func<CodeAddressIndex, MethodIndex> methodOf,
            bool trace,
            List<StitchedFrame> output,
            StitchDiagnostics diagnostics)
        {
            if (syncLeafToRoot is null) throw new ArgumentNullException(nameof(syncLeafToRoot));
            if (classify is null) throw new ArgumentNullException(nameof(classify));
            if (methodCompletionObserved is null) throw new ArgumentNullException(nameof(methodCompletionObserved));
            if (methodOf is null) throw new ArgumentNullException(nameof(methodOf));
            if (output is null) throw new ArgumentNullException(nameof(output));
            if (diagnostics is null) throw new ArgumentNullException(nameof(diagnostics));

            StitchInto(syncLeafToRoot, segmentsRootToLeaf, qpc, classify, null, methodCompletionObserved,
                processIndex, methodOf, trace, output, diagnostics);
        }

        private static void StitchInto(
            IReadOnlyList<StitchSyncFrame> syncLeafToRoot,
            IReadOnlyList<AsyncCallStack> segmentsRootToLeaf,
            long qpc,
            Func<CodeAddressIndex, AsyncStitchBoundaryInfo> classify,
            Func<AsyncCallstackKind, bool> methodCompletionObserved,
            Func<ProcessIndex, AsyncCallstackKind, bool> methodCompletionObservedByProcess,
            ProcessIndex processIndex,
            Func<CodeAddressIndex, MethodIndex> methodOf,
            bool trace,
            List<StitchedFrame> output,
            StitchDiagnostics diagnostics)
        {
            output.Clear();
            diagnostics.Clear();

            // With no segment there is no suspended ancestry to splice. An empty segment is also unusable: it may
            // identify an active async context, but it has no current frame with which to align a physical boundary.
            // In either case emit the sync stack verbatim so no continuation-wrapper or dispatcher frame is removed
            // without logical ancestry to replace it.
            if (segmentsRootToLeaf is null || segmentsRootToLeaf.Count == 0 || HasEmptySegment(segmentsRootToLeaf))
            {
                for (int i = 0; i < syncLeafToRoot.Count; i++)
                {
                    output.Add(StitchedFrame.Sync(syncLeafToRoot[i].CodeAddress));
                }
                return;
            }

            // V2 gate: a V2 (RuntimeAsync) segment is only stitched when the CPU is physically inside a resumed
            // continuation body, which is true exactly when a continuation-wrapper frame sits root-ward of the
            // sampled leaf (at a higher leaf->root index). Two shapes are dispatch machinery rather than a resumed
            // body, so they are not stitched - the sync layout is used and only the wrapper frame (when present) is
            // collapsed:
            //   * Case 2 - the wrapper is the sampled leaf: it has not yet entered the resumed async body. Drop the
            //     wrapper frame (dispatch machinery) so the dispatch-continuation frame root-ward of it becomes the leaf.
            //   * Case 3 - no wrapper anywhere, but the sample sits in dispatch-continuation plumbing
            //     (InstrumentedDispatchContinuations / DispatchContinuations): emit the sync layout unchanged.
            // A V2 segment whose sync stack carries no async-boundary frame at all is a different anomaly (e.g. a
            // truncated stack); it falls through to the append-only degradation in the loop below, unchanged.
            if (segmentsRootToLeaf[segmentsRootToLeaf.Count - 1].Frames.Kind == AsyncCallstackKind.RuntimeAsync)
            {
                int wrapperPos = FirstWrapperIndex(syncLeafToRoot, classify, out bool hasDispatchContinuation);
                bool wrapperFound = wrapperPos < syncLeafToRoot.Count;

                if (wrapperFound && wrapperPos == 0) // Case 2: drop the leaf wrapper, keep the rest as sync.
                {
                    diagnostics.V2SyncLayoutUsed++;
                    diagnostics.V2LeafWrapperDropped++;
                    for (int i = 1; i < syncLeafToRoot.Count; i++)
                    {
                        output.Add(StitchedFrame.Sync(syncLeafToRoot[i].CodeAddress));
                    }
                    if (trace)
                    {
                        diagnostics.Note("V2 not stitched: continuation-wrapper is the sampled leaf; dropped it and used the sync layout.");
                    }
                    return;
                }

                if (!wrapperFound && hasDispatchContinuation) // Case 3: sync layout unchanged.
                {
                    diagnostics.V2SyncLayoutUsed++;
                    for (int i = 0; i < syncLeafToRoot.Count; i++)
                    {
                        output.Add(StitchedFrame.Sync(syncLeafToRoot[i].CodeAddress));
                    }
                    if (trace)
                    {
                        diagnostics.Note("V2 not stitched: no continuation-wrapper root-ward of the leaf; used the sync layout.");
                    }
                    return;
                }
            }

            int pSync = 0;
            for (int segmentIndex = segmentsRootToLeaf.Count - 1; segmentIndex >= 0; segmentIndex--)
            {
                AsyncCallStack segment = segmentsRootToLeaf[segmentIndex];
                diagnostics.SegmentsProcessed++;
                AsyncCallStackFrames frames = segment.Frames;
                AsyncCallstackKind kind = frames.Kind;

                int boundaryPos = FindBoundary(syncLeafToRoot, pSync, kind, classify, out AsyncStitchBoundaryInfo boundaryInfo);
                bool boundaryFound = boundaryPos < syncLeafToRoot.Count;
                if (!boundaryFound)
                {
                    diagnostics.BoundariesNotFound++;
                    if (diagnostics.CanRetainMessage)
                    {
                        diagnostics.Note($"No {kind} dispatch boundary found at or root-ward of sync frame {pSync}; splicing remaining ancestry append-only.");
                    }
                    else
                    {
                        diagnostics.MessagesDropped++;
                    }
                }

                int completed = ComputeCompletedCount(segment, frames, kind, boundaryInfo, qpc,
                    syncLeafToRoot, pSync, boundaryPos, methodCompletionObserved,
                    methodCompletionObservedByProcess, processIndex, methodOf, diagnostics);

                int currentSyncPos = kind == AsyncCallstackKind.StateMachineAsync
                    ? FindCurrentV1SyncFrame(frames, completed, syncLeafToRoot, pSync, boundaryPos, methodOf)
                    : -1;

                // Emit the real sync frames leaf-ward of the boundary. The current V1 MoveNext frame remains tied
                // to its physical code address, but carries its async identity so the emitter can display the
                // logical source method rather than compiler-generated state-machine plumbing. Once that current
                // frame has been found, the remaining V1 frames through the dispatcher boundary are the physical
                // inline-transition chain represented by the logical ancestry and are not emitted.
                for (int i = pSync; i < boundaryPos; i++)
                {
                    if (kind == AsyncCallstackKind.StateMachineAsync && currentSyncPos >= 0 && i > currentSyncPos)
                    {
                        continue;
                    }

                    output.Add(i == currentSyncPos
                        ? StitchedFrame.AsyncCurrent(syncLeafToRoot[i].CodeAddress, frames, completed)
                        : StitchedFrame.Sync(syncLeafToRoot[i].CodeAddress));
                }

                // The current (running) frame is segment[completed]; it is physically present on the sync stack
                // (emitted above), so it is never re-emitted here. Splice only the root-ward suspended ancestry.
                if (boundaryFound)
                {
                    ValidateAdjacency(segment, frames, kind, completed, syncLeafToRoot, boundaryPos, methodOf, diagnostics);
                }

                for (int k = completed + 1; k < frames.FrameCount; k++)
                {
                    output.Add(StitchedFrame.Async(frames, k));
                }

                if (trace && diagnostics.CanRetainMessage)
                {
                    string boundaryDesc = boundaryFound
                        ? (boundaryInfo.Kind == AsyncStitchBoundaryKind.V2ContinuationWrapper
                            ? $"{boundaryInfo.Kind}[{boundaryInfo.WrapperIndex}]@sync{boundaryPos}"
                            : $"{boundaryInfo.Kind}@sync{boundaryPos}")
                        : "no-boundary";
                    int splicedCount = frames.FrameCount - (completed + 1);
                    if (splicedCount < 0) splicedCount = 0;
                    diagnostics.Note($"segment[{diagnostics.SegmentsProcessed - 1}] kind={kind} boundary={boundaryDesc} " +
                        $"emittedSync=[{pSync}..{boundaryPos}) completed={completed} spliced=[{completed + 1}..{frames.FrameCount}) ({splicedCount} frame(s)) frameCount={frames.FrameCount}");
                }
                else if (trace)
                {
                    diagnostics.MessagesDropped++;
                }

                // Skip the boundary frame itself (the dispatch machinery the ancestry replaces); keep other frames.
                pSync = boundaryFound ? boundaryPos + 1 : boundaryPos;
                if (boundaryFound && kind == AsyncCallstackKind.StateMachineAsync)
                {
                    while (pSync < syncLeafToRoot.Count &&
                           classify(syncLeafToRoot[pSync].CodeAddress).Kind ==
                               AsyncStitchBoundaryKind.V1DispatcherInfrastructure)
                    {
                        pSync++;
                        diagnostics.V1InfrastructureFramesCollapsed++;
                    }
                }
                else if (boundaryFound && kind == AsyncCallstackKind.RuntimeAsync)
                {
                    // Collapse the dispatch-continuation plumbing for this segment immediately, rather than waiting
                    // until all segments are exhausted. An inner V2 segment may be followed by a preserved bridge
                    // and then an outer V1/V2 segment; leaking these frames into that bridge breaks lockstep alignment.
                    while (pSync < syncLeafToRoot.Count &&
                           classify(syncLeafToRoot[pSync].CodeAddress).Kind ==
                               AsyncStitchBoundaryKind.V2DispatchContinuation)
                    {
                        pSync++;
                        diagnostics.V2PlumbingFramesCollapsed++;
                    }
                }
            }

            // Emit the clean sync tail (includes the thread/process root).
            for (int i = pSync; i < syncLeafToRoot.Count; i++)
            {
                output.Add(StitchedFrame.Sync(syncLeafToRoot[i].CodeAddress));
            }

        }

        private static bool HasEmptySegment(IReadOnlyList<AsyncCallStack> segments)
        {
            for (int i = 0; i < segments.Count; i++)
            {
                if (segments[i].Frames.FrameCount == 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Scans the sync stack (leaf-&gt;root) for the first V2 continuation-wrapper frame, used by the V2 gate to
        /// decide whether the CPU is physically inside a resumed continuation body (a wrapper root-ward of the leaf).
        /// Returns the wrapper's index, or <c>sync.Count</c> if none is present. <paramref name="hasDispatchContinuation"/>
        /// is set true when a V2 dispatch-continuation plumbing frame was seen before the first wrapper (only
        /// meaningful when no wrapper is found, distinguishing dispatch plumbing from a boundary-less stack).
        /// </summary>
        private static int FirstWrapperIndex(
            IReadOnlyList<StitchSyncFrame> sync, Func<CodeAddressIndex, AsyncStitchBoundaryInfo> classify,
            out bool hasDispatchContinuation)
        {
            hasDispatchContinuation = false;
            for (int i = 0; i < sync.Count; i++)
            {
                AsyncStitchBoundaryKind kind = classify(sync[i].CodeAddress).Kind;
                if (kind == AsyncStitchBoundaryKind.V2ContinuationWrapper)
                {
                    return i;
                }
                if (kind == AsyncStitchBoundaryKind.V2DispatchContinuation)
                {
                    hasDispatchContinuation = true;
                }
            }

            return sync.Count;
        }

        /// <summary>
        /// Finds the first sync frame at/root-ward of <paramref name="pSync"/> that is the dispatch boundary matching
        /// <paramref name="segmentKind"/>. Returns its position, or <c>sync.Count</c> if none is found.
        /// </summary>
        private static int FindBoundary(
            IReadOnlyList<StitchSyncFrame> sync, int pSync, AsyncCallstackKind segmentKind,
            Func<CodeAddressIndex, AsyncStitchBoundaryInfo> classify, out AsyncStitchBoundaryInfo boundaryInfo)
        {
            for (int i = pSync; i < sync.Count; i++)
            {
                AsyncStitchBoundaryInfo info = classify(sync[i].CodeAddress);
                if (MatchesKind(info.Kind, segmentKind))
                {
                    boundaryInfo = info;
                    return i;
                }
            }

            boundaryInfo = AsyncStitchBoundaryInfo.None;
            return sync.Count;
        }

        /// <summary>Whether a boundary kind matches an async segment's kind (V2 vs V1).</summary>
        private static bool MatchesKind(AsyncStitchBoundaryKind boundaryKind, AsyncCallstackKind segmentKind)
        {
            switch (segmentKind)
            {
                case AsyncCallstackKind.RuntimeAsync: // V2
                    // Only a continuation-wrapper is a valid V2 splice boundary: its presence proves the CPU is
                    // physically inside a resumed continuation body. A bare dispatch-continuation frame (no wrapper)
                    // is dispatch plumbing, handled by the sync-layout gate in Stitch, not a splice boundary.
                    return boundaryKind == AsyncStitchBoundaryKind.V2ContinuationWrapper;
                case AsyncCallstackKind.StateMachineAsync: // V1
                    return boundaryKind == AsyncStitchBoundaryKind.V1Dispatcher;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The number of leaf-ward frames of <paramref name="segment"/> that have completed by
        /// <paramref name="qpc"/>. Completions come from two independent mechanisms that are combined:
        /// <list type="bullet">
        ///   <item><b>Normal completions</b>: if <c>CompleteMethod</c> events were emitted for this kind
        ///   (<paramref name="methodCompletionObserved"/>), <see cref="AsyncCallStack.GetMethodCompletedFrameCount"/>
        ///   is authoritative; else V2 derives them from the continuation-wrapper slot (which already includes
        ///   exceptional unwinds), and V1 counts the inline-resumed <c>MoveNext</c> frames on the sync stack.</item>
        ///   <item><b>Exceptional completions</b>: unwound frames have left the sync stack, so for V1 they are always
        ///   added from <c>Unwind</c> events (<see cref="AsyncCallStack.GetExceptionCompletedFrameCount"/>). For V2
        ///   they are already folded into whichever normal source is used, so they are not added again.</item>
        /// </list>
        /// The result is clamped to <c>[0, FrameCount-1]</c> so a valid "current" frame always remains.
        /// </summary>
        private static int ComputeCompletedCount(
            AsyncCallStack segment, AsyncCallStackFrames frames, AsyncCallstackKind kind,
            AsyncStitchBoundaryInfo boundaryInfo, long qpc,
            IReadOnlyList<StitchSyncFrame> sync, int pSync, int boundaryPos,
            Func<AsyncCallstackKind, bool> methodCompletionObserved,
            Func<ProcessIndex, AsyncCallstackKind, bool> methodCompletionObservedByProcess,
            ProcessIndex processIndex,
            Func<CodeAddressIndex, MethodIndex> methodOf, StitchDiagnostics diagnostics)
        {
            bool completionObserved = methodCompletionObserved != null
                ? methodCompletionObserved(kind)
                : methodCompletionObservedByProcess(processIndex, kind);
            int completed;
            if (kind == AsyncCallstackKind.RuntimeAsync) // V2
            {
                if (completionObserved)
                {
                    // CompleteMethod events observed: the summed deltas (normal + any exceptional unwind) are the
                    // authoritative completed count.
                    completed = segment.GetCompletedFrameCount(qpc);
                }
                else
                {
                    // Derive from the continuation-wrapper slot; the wrapper index advances on every completion,
                    // including exceptional unwinds, so no separate exception term is added.
                    int slot = boundaryInfo.Kind == AsyncStitchBoundaryKind.V2ContinuationWrapper ? boundaryInfo.WrapperIndex : 0;
                    completed = segment.GetCompletedFrameCount(qpc, slot);
                    diagnostics.V2WrapperFallbackUsed++;
                }
            }
            else // V1 StateMachineAsync: normal completions + exceptional unwinds (the latter always from events).
            {
                int normal;
                if (completionObserved)
                {
                    normal = segment.GetMethodCompletedFrameCount(qpc);
                }
                else
                {
                    // No CompleteMethod events: the normal completions are the inline-resumed MoveNext frames
                    // physically on the sync stack.
                    normal = CountV1Completed(frames, sync, pSync, boundaryPos,
                        segment.GetExceptionCompletedFrameCount(qpc), methodOf);
                    diagnostics.V1InlineFallbackUsed++;
                }

                // Exceptional completions unwind off the sync stack, so they can only come from Unwind events; add
                // them regardless of how the normal count was derived (0 when none were emitted).
                completed = normal + segment.GetExceptionCompletedFrameCount(qpc);
            }

            int maxIndex = frames.FrameCount - 1;
            if (completed < 0)
            {
                completed = 0;
            }
            else if (completed > maxIndex)
            {
                completed = maxIndex;
            }
            return completed;
        }

        /// <summary>
        /// Counts the V1 completed frames by matching the segment's already-completed frames against the
        /// inline-resumed <c>MoveNext</c> frames on the sync stack. In a V1 inline-resume cascade the segment frames
        /// appear in order from the dispatch boundary toward the physical leaf. The final matched state-machine
        /// frame is current; every earlier match has completed. We therefore match <c>segment[0,1,2,...]</c>
        /// against the complete boundary slice (from <c>boundaryPos-1</c> through <c>pSync</c>), skipping
        /// synchronous calls and transition machinery; the completed count is <c>matchedCount - 1</c>. This ordered
        /// (positional) match is robust to a state machine legitimately recurring within one segment, unlike a
        /// set-membership count.
        /// <para>
        /// Exceptionally completed frames are absent from the physical stack. The known exception count is therefore
        /// used as a bounded number of async frames that may be skipped while aligning the next physical match.
        /// This supports exceptions before or between normally completed inline frames without allowing an
        /// unbounded search through unrelated async ancestry.
        /// </para>
        /// <para>
        /// If an async frame cannot be verified (for example a <c>0</c>-IP frame), matching stops conservatively,
        /// leaving the remaining frames as async ancestry.
        /// </para>
        /// </summary>
        private static int CountV1Completed(
            AsyncCallStackFrames frames, IReadOnlyList<StitchSyncFrame> sync, int pSync, int boundaryPos,
            int exceptionCompleted, Func<CodeAddressIndex, MethodIndex> methodOf)
        {
            int frameCount = frames.FrameCount;
            int nextSegmentFrame = 0;
            int matched = 0;
            int exceptionsSkipped = 0;

            for (int i = boundaryPos - 1; i >= pSync && nextSegmentFrame < frameCount; i--)
            {
                int candidate = nextSegmentFrame;
                int candidateExceptions = 0;
                int availableExceptions = exceptionCompleted - exceptionsSkipped;
                while (candidate < frameCount && candidateExceptions <= availableExceptions)
                {
                    MethodIndex expected = methodOf(frames.CodeAddressAt(candidate));
                    if (expected == MethodIndex.Invalid)
                    {
                        if (candidateExceptions < availableExceptions)
                        {
                            candidate++;
                            candidateExceptions++;
                            continue;
                        }

                        // The next verifiable physical frame cannot identify this async frame. Every previously
                        // matched frame completed; preserve the physical slice because the current frame is unknown.
                        return matched;
                    }

                    if (sync[i].Method == expected)
                    {
                        exceptionsSkipped += candidateExceptions;
                        nextSegmentFrame = candidate + 1;
                        matched++;
                        break;
                    }

                    candidate++;
                    candidateExceptions++;
                }
            }

            // The final matched physical state-machine frame is current; every earlier match completed normally.
            // Exceptionally completed frames were skipped only to align identities and are added separately.
            return Math.Max(0, matched - 1);
        }

        private static int FindCurrentV1SyncFrame(
            AsyncCallStackFrames frames, int completed, IReadOnlyList<StitchSyncFrame> sync,
            int pSync, int boundaryPos, Func<CodeAddressIndex, MethodIndex> methodOf)
        {
            if (completed < 0 || completed >= frames.FrameCount)
            {
                return -1;
            }

            MethodIndex current = methodOf(frames.CodeAddressAt(completed));
            if (current == MethodIndex.Invalid)
            {
                // Without a current-method identity we cannot distinguish synchronous leaf calls from the V1
                // transition chain. Preserve the complete physical slice rather than risk deleting user frames.
                return -1;
            }

            // Pick the leaf-most matching physical frame. Completed copies of the same state machine, if any,
            // occur root-ward during an inline-resume cascade.
            for (int i = pSync; i < boundaryPos; i++)
            {
                if (sync[i].Method == current)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Soft check that the segment's current frame (<c>segment[completed]</c>) matches the sync frame directly
        /// leaf-ward of the boundary — for V2 the continuation wrapper directly calls the resumed async method, so they
        /// are adjacent. A mismatch is recorded as a diagnostic, never thrown.
        /// </summary>
        private static void ValidateAdjacency(
            AsyncCallStack segment, AsyncCallStackFrames frames, AsyncCallstackKind kind, int completed,
            IReadOnlyList<StitchSyncFrame> sync, int boundaryPos,
            Func<CodeAddressIndex, MethodIndex> methodOf, StitchDiagnostics diagnostics)
        {
            if (kind != AsyncCallstackKind.RuntimeAsync)
            {
                return; // adjacency only holds for the V2 wrapper-direct-call shape.
            }

            int leafwardOfBoundary = boundaryPos - 1;
            if (leafwardOfBoundary < 0 || completed >= frames.FrameCount)
            {
                return;
            }

            MethodIndex expected = methodOf(frames.CodeAddressAt(completed));
            MethodIndex actual = sync[leafwardOfBoundary].Method;
            if (expected != MethodIndex.Invalid && actual != MethodIndex.Invalid && expected != actual)
            {
                diagnostics.AdjacencyMismatches++;
                if (diagnostics.CanRetainMessage)
                {
                    diagnostics.Note($"V2 current async method (segment frame {completed}) did not match the sync frame directly leaf-ward of the boundary (frame {leafwardOfBoundary}).");
                }
                else
                {
                    diagnostics.MessagesDropped++;
                }
            }
        }
    }
}
