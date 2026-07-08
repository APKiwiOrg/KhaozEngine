using System;
using System.Collections.Generic;

namespace KhaozEngine.Render3D
{
    /// <summary>Per-skeleton-node blend weights in [0,1], the spatial gate a <see cref="AnimationLayer"/> composites
    /// through: weight 1 lets a node take the layer fully, 0 leaves it on the base, a fraction blends. A mask is a
    /// skeleton-shaped array (one weight per <see cref="Skeleton.NodeCount"/> node, in node order), reusable and
    /// allocation-free to apply (<see cref="Weight"/> is a plain array read). Build one for an upper-body action with
    /// <see cref="Subtree(Skeleton, int, float)"/> / <see cref="Subtree(Skeleton, string, IReadOnlyList{string}, float)"/>
    /// ("this bone and all its descendants at weight w, the rest at 0"). Pure presentation; GPU-free.</summary>
    public sealed class BoneMask
    {
        readonly float[] _weights;

        /// <summary>Number of skeleton nodes this mask covers (its weight-array length).</summary>
        public int NodeCount => _weights.Length;

        /// <summary>The per-node weight (0..1) at node index <paramref name="node"/>.</summary>
        public float Weight(int node) => _weights[node];

        /// <summary>Wrap an existing per-node weight array (one entry per skeleton node, each clamped to [0,1]). The
        /// array is COPIED, so the caller may reuse its buffer. Prefer <see cref="Full"/> / <see cref="Empty"/> /
        /// <see cref="Subtree(Skeleton, int, float)"/> for the common shapes.</summary>
        public BoneMask(ReadOnlySpan<float> weights)
        {
            _weights = new float[weights.Length];
            for (int i = 0; i < weights.Length; i++) _weights[i] = Math.Clamp(weights[i], 0f, 1f);
        }

        BoneMask(float[] owned) => _weights = owned;   // internal: takes ownership, values already clamped

        /// <summary>A mask that is weight 1 on every node (the layer takes the whole skeleton). Compositing a full
        /// mask is the single-full-layer fast path: it reduces to the layer pose itself.</summary>
        public static BoneMask Full(Skeleton skel)
        {
            if (skel is null) throw new ArgumentNullException(nameof(skel));
            var w = new float[skel.NodeCount];
            for (int i = 0; i < w.Length; i++) w[i] = 1f;
            return new BoneMask(w);
        }

        /// <summary>A mask that is weight 0 on every node (the layer affects nothing). Compositing an empty mask is a
        /// no-op that leaves the base pose unchanged.</summary>
        public static BoneMask Empty(Skeleton skel)
        {
            if (skel is null) throw new ArgumentNullException(nameof(skel));
            return new BoneMask(new float[skel.NodeCount]);   // default 0
        }

        /// <summary>A subtree mask: the node <paramref name="root"/> AND all of its descendants get
        /// <paramref name="weight"/>; every other node gets 0. This is the upper-body-action shape (mask the spine
        /// root so the torso + arms + head follow the action while the legs stay on locomotion). Relies on the
        /// skeleton's topological order (<c>ParentIndices[i] &lt; i</c>): a single forward pass propagates the flag
        /// from a marked parent to its children.</summary>
        public static BoneMask Subtree(Skeleton skel, int root, float weight)
        {
            if (skel is null) throw new ArgumentNullException(nameof(skel));
            if (root < 0 || root >= skel.NodeCount)
                throw new ArgumentOutOfRangeException(nameof(root), root, $"root must be a node index in [0, {skel.NodeCount}).");
            float w = Math.Clamp(weight, 0f, 1f);
            var weights = new float[skel.NodeCount];
            var inSubtree = new bool[skel.NodeCount];
            inSubtree[root] = true;
            weights[root] = w;
            int[] parents = skel.ParentIndices;
            for (int i = root + 1; i < skel.NodeCount; i++)
            {
                int p = parents[i];
                if (p >= 0 && inSubtree[p]) { inSubtree[i] = true; weights[i] = w; }
            }
            return new BoneMask(weights);
        }

        /// <summary>A subtree mask keyed by bone NAME: resolves <paramref name="rootBoneName"/> to a node via
        /// <paramref name="boneNames"/> (one name per skeleton node, in node order) then defers to
        /// <see cref="Subtree(Skeleton, int, float)"/>. Throws if the name is not found. Use the index overload in a
        /// hot path; this is the authoring convenience.</summary>
        public static BoneMask Subtree(Skeleton skel, string rootBoneName, IReadOnlyList<string> boneNames, float weight)
        {
            if (skel is null) throw new ArgumentNullException(nameof(skel));
            if (rootBoneName is null) throw new ArgumentNullException(nameof(rootBoneName));
            if (boneNames is null) throw new ArgumentNullException(nameof(boneNames));
            if (boneNames.Count != skel.NodeCount)
                throw new ArgumentException($"boneNames length {boneNames.Count} must equal node count {skel.NodeCount}.", nameof(boneNames));
            int node = -1;
            for (int i = 0; i < boneNames.Count; i++)
                if (string.Equals(boneNames[i], rootBoneName, StringComparison.Ordinal)) { node = i; break; }
            if (node < 0) throw new ArgumentException($"bone '{rootBoneName}' was not found in the skeleton.", nameof(rootBoneName));
            return Subtree(skel, node, weight);
        }
    }
}
