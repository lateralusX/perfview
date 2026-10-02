// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.AsyncProfiler;

namespace Microsoft.Diagnostics.Tracing.Computers
{
    /// <summary>
    /// Adds dispatcher creation-parent contexts to an already stitched execution stack. Every inserted frame keeps
    /// its parent activation identity; the instance owns reusable scratch buffers because it runs on the CPU-sample
    /// hot path.
    /// </summary>
    internal sealed class AsyncContextAncestryAugmenter
    {
        internal const int DefaultMaximumDepth = 64;
        private const int ParentCacheSize = 256;

        internal bool Prepare(
            AsyncCallStacksIndex index,
            IReadOnlyList<AsyncCallStack> activeSegments)
        {
            for (int i = activeSegments.Count - 1; i >= 0; i--)
            {
                AsyncCallStack child = activeSegments[i];
                if (!TryGetParent(
                    index, child, out AsyncCallStack parent, out _))
                {
                    continue;
                }

                var parentIdentity = new ActivationIdentity(parent);
                if (Contains(activeSegments, parentIdentity))
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        internal void Augment(
            AsyncCallStacksIndex index,
            IReadOnlyList<AsyncCallStack> activeSegments,
            IReadOnlyList<AsyncSegmentPlacement> placements,
            List<StitchedFrame> frames,
            int maximumDepth)
        {
            if (index == null) throw new ArgumentNullException(nameof(index));
            if (activeSegments == null) throw new ArgumentNullException(nameof(activeSegments));
            if (placements == null) throw new ArgumentNullException(nameof(placements));
            if (frames == null) throw new ArgumentNullException(nameof(frames));
            if (maximumDepth <= 0) throw new ArgumentOutOfRangeException(nameof(maximumDepth));

            m_active.Clear();
            for (int i = 0; i < activeSegments.Count; i++)
            {
                m_active.Add(new ActivationIdentity(activeSegments[i]));
            }

            // Placements are recorded leaf-to-root. Process them in reverse so root-ward insertions do not
            // invalidate the still-pending lower insertion indices.
            for (int placementIndex = placements.Count - 1; placementIndex >= 0; placementIndex--)
            {
                AsyncSegmentPlacement placement = placements[placementIndex];
                CollectMissingParents(index, placement.Activation, maximumDepth);
                if (m_chain.Count == 0)
                {
                    continue;
                }

                m_insertion.Clear();
                for (int i = 0; i < m_chain.Count; i++)
                {
                    CollectParentFrames(index, m_chain[i]);
                    m_insertion.AddRange(m_parentFrames);
                }

                if (m_insertion.Count != 0)
                {
                    frames.InsertRange(placement.RootIndexExclusive, m_insertion);
                }
            }
        }

        internal void Clear()
        {
            m_active.Clear();
            m_visited.Clear();
            m_chain.Clear();
            m_parentCandidates.Clear();
            m_insertion.Clear();
            m_parentFrames.Clear();
            m_parentCache = null;
        }

        private void CollectMissingParents(
            AsyncCallStacksIndex index,
            AsyncCallStack activation,
            int maximumDepth)
        {
            m_chain.Clear();
            m_visited.Clear();
            m_visited.Add(new ActivationIdentity(activation));

            AsyncCallStack child = activation;
            for (int depth = 0; depth < maximumDepth; depth++)
            {
                if (!TryGetParent(
                    index, child, out AsyncCallStack parent, out long childCreationQpc))
                {
                    break;
                }

                var identity = new ActivationIdentity(parent);
                if (Contains(m_active, identity) || Contains(m_visited, identity))
                {
                    break;
                }

                m_visited.Add(identity);
                m_chain.Add(new ParentAtCreation(parent, childCreationQpc));
                child = parent;
            }
        }

        private bool TryGetParent(
            AsyncCallStacksIndex index,
            AsyncCallStack child,
            out AsyncCallStack parent,
            out long parentQpc)
        {
            var identity = new ActivationIdentity(child);
            int cacheIndex = GetParentCacheIndex(index, identity);
            if (m_parentCache != null)
            {
                ParentCacheEntry cached = m_parentCache[cacheIndex];
                if (cached.Matches(index, identity))
                {
                    parent = cached.Parent;
                    parentQpc = cached.ParentQpc;
                    return true;
                }
            }

            bool found = index.TryGetParentAsyncCallStack(
                child, m_parentCandidates, out parent, out parentQpc);
            if (found)
            {
                if (m_parentCache == null)
                {
                    m_parentCache = new ParentCacheEntry[ParentCacheSize];
                }
                m_parentCache[cacheIndex] =
                    new ParentCacheEntry(index, identity, parent, parentQpc);
            }
            return found;
        }

        private static int GetParentCacheIndex(
            AsyncCallStacksIndex index,
            ActivationIdentity identity) =>
            (identity.GetHashCode() ^ RuntimeHelpers.GetHashCode(index)) &
            (ParentCacheSize - 1);

        private void CollectParentFrames(
            AsyncCallStacksIndex index,
            ParentAtCreation parentAtCreation)
        {
            m_parentFrames.Clear();
            AsyncCallStack parent = parentAtCreation.Parent;
            int completed = parent.GetExceptionCompletedFrameCount(parentAtCreation.CreationQpc);
            if (index.MethodCompletionObserved(parent))
            {
                completed += parent.GetMethodCompletedFrameCount(parentAtCreation.CreationQpc);
            }

            int frameCount = parent.Frames.FrameCount;
            if (completed < 0)
            {
                completed = 0;
            }
            else if (completed > frameCount)
            {
                completed = frameCount;
            }

            // The historical parent's current frame has no physical counterpart in the sampled stack.
            for (int frameIndex = completed; frameIndex < frameCount; frameIndex++)
            {
                m_parentFrames.Add(
                    StitchedFrame.CreateAsyncContextParent(parent, frameIndex));
            }
        }

        private static bool Contains(
            List<ActivationIdentity> identities,
            ActivationIdentity candidate)
        {
            for (int i = 0; i < identities.Count; i++)
            {
                if (identities[i].Equals(candidate))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool Contains(
            IReadOnlyList<AsyncCallStack> activations,
            ActivationIdentity candidate)
        {
            for (int i = 0; i < activations.Count; i++)
            {
                if (new ActivationIdentity(activations[i]).Equals(candidate))
                {
                    return true;
                }
            }
            return false;
        }

        private readonly List<ActivationIdentity> m_active = new List<ActivationIdentity>();
        private readonly List<ActivationIdentity> m_visited = new List<ActivationIdentity>();
        private readonly List<ParentAtCreation> m_chain = new List<ParentAtCreation>();
        private readonly List<AsyncCallStack> m_parentCandidates = new List<AsyncCallStack>();
        private readonly List<StitchedFrame> m_insertion = new List<StitchedFrame>();
        private readonly List<StitchedFrame> m_parentFrames = new List<StitchedFrame>();
        private ParentCacheEntry[] m_parentCache;

        private readonly struct ParentAtCreation
        {
            internal ParentAtCreation(AsyncCallStack parent, long creationQpc)
            {
                Parent = parent;
                CreationQpc = creationQpc;
            }

            internal AsyncCallStack Parent { get; }
            internal long CreationQpc { get; }
        }

        private readonly struct ActivationIdentity : IEquatable<ActivationIdentity>
        {
            internal ActivationIdentity(AsyncCallStack activation)
            {
                Thread = activation.Thread;
                DispatcherId = activation.DispatcherId;
                StartQpc = activation.StartQpc;
            }

            public bool Equals(ActivationIdentity other) =>
                Thread.Equals(other.Thread) &&
                DispatcherId == other.DispatcherId &&
                StartQpc == other.StartQpc;

            public override bool Equals(object obj) =>
                obj is ActivationIdentity other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Thread.GetHashCode();
                    hash = (hash * 397) ^ DispatcherId.GetHashCode();
                    return (hash * 397) ^ StartQpc.GetHashCode();
                }
            }

            private readonly AsyncThreadKey Thread;
            private readonly ulong DispatcherId;
            private readonly long StartQpc;
        }

        private readonly struct ParentCacheEntry
        {
            internal ParentCacheEntry(
                AsyncCallStacksIndex index,
                ActivationIdentity identity,
                AsyncCallStack parent,
                long parentQpc)
            {
                Index = index;
                Identity = identity;
                Parent = parent;
                ParentQpc = parentQpc;
            }

            internal bool Matches(
                AsyncCallStacksIndex index,
                ActivationIdentity identity) =>
                ReferenceEquals(Index, index) && Identity.Equals(identity);

            internal AsyncCallStack Parent { get; }
            internal long ParentQpc { get; }

            private AsyncCallStacksIndex Index { get; }
            private ActivationIdentity Identity { get; }
        }
    }
}
