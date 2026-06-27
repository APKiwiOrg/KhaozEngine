using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>The joint hierarchy a skinned mesh poses through: a flat, topologically-ordered list of skeleton
    /// nodes (each node's parent index is strictly less than its own, <c>-1</c> for a root), each node's rest-pose
    /// local TRS, the glTF logical node index per skeleton node (so an <see cref="AnimationClip"/> keyed by logical
    /// node index resolves to a skeleton node), and the skin's bone-to-node map. Composing the rest locals (or a
    /// sampled set of locals) up the hierarchy yields the joint-WORLD bone palette
    /// <see cref="Scene3D.DrawSkinned(SkinnedMeshHandle, ReadOnlySpan{Matrix4x4}, Matrix4x4, Primitives.Color)"/>
    /// consumes (it multiplies by the mesh inverse-bind itself). Pure presentation; GPU-free.</summary>
    public sealed class Skeleton
    {
        /// <summary>Parent skeleton-node index per node (<c>-1</c> for a root). Topologically ordered:
        /// <c>ParentIndices[i] &lt; i</c> always, so one forward pass composes the whole hierarchy.</summary>
        public int[] ParentIndices { get; }

        /// <summary>Rest-pose local TRS per skeleton node (the pose used when a clip does not animate that node).</summary>
        public JointPose[] RestLocal { get; }

        /// <summary>The glTF logical node index per skeleton node (the key an <see cref="AnimationClip"/> channel
        /// targets).</summary>
        public int[] NodeLogicalIndex { get; }

        /// <summary>Skeleton-node index per skin bone, in skin-joint order (so a composed world array aligns with the
        /// mesh inverse-bind / vertex JOINTS_0 indices).</summary>
        public int[] JointToNode { get; }

        public int NodeCount => ParentIndices.Length;
        public int BoneCount => JointToNode.Length;

        Dictionary<int, int>? _logicalToNode;

        public Skeleton(int[] parentIndices, JointPose[] restLocal, int[] nodeLogicalIndex, int[] jointToNode)
        {
            ParentIndices = parentIndices ?? throw new ArgumentNullException(nameof(parentIndices));
            RestLocal = restLocal ?? throw new ArgumentNullException(nameof(restLocal));
            NodeLogicalIndex = nodeLogicalIndex ?? throw new ArgumentNullException(nameof(nodeLogicalIndex));
            JointToNode = jointToNode ?? throw new ArgumentNullException(nameof(jointToNode));
            if (restLocal.Length != parentIndices.Length || nodeLogicalIndex.Length != parentIndices.Length)
                throw new ArgumentException("parentIndices, restLocal, and nodeLogicalIndex must have one entry per skeleton node.");
        }

        /// <summary>The skeleton node a glTF logical node index maps to, or <c>-1</c> if that node is not in this
        /// skeleton.</summary>
        public int NodeForLogicalIndex(int logical)
        {
            if (_logicalToNode is null)
            {
                _logicalToNode = new Dictionary<int, int>(NodeLogicalIndex.Length);
                for (int i = 0; i < NodeLogicalIndex.Length; i++) _logicalToNode[NodeLogicalIndex[i]] = i;
            }
            return _logicalToNode.TryGetValue(logical, out int node) ? node : -1;
        }

        /// <summary>The joint-WORLD bone palette at rest (composing the rest locals up the hierarchy, gathered into
        /// skin-bone order). Passing this to <c>DrawSkinned</c> yields the identity deform.</summary>
        public Matrix4x4[] ComposeRestPose()
        {
            var palette = new Matrix4x4[BoneCount];
            ComposeInto(RestLocal, palette);
            return palette;
        }

        /// <summary>Compose <paramref name="localByNode"/> (one local TRS per skeleton node, in node order) up the
        /// hierarchy and gather the per-bone joint-WORLD matrices into <paramref name="bonePaletteOut"/> (length
        /// <see cref="BoneCount"/>).</summary>
        public void ComposeInto(ReadOnlySpan<JointPose> localByNode, Matrix4x4[] bonePaletteOut)
        {
            if (localByNode.Length != NodeCount)
                throw new ArgumentException($"localByNode length {localByNode.Length} must equal node count {NodeCount}.");
            if (bonePaletteOut.Length != BoneCount)
                throw new ArgumentException($"bonePaletteOut length {bonePaletteOut.Length} must equal bone count {BoneCount}.");
            Span<Matrix4x4> world = NodeCount <= 128 ? stackalloc Matrix4x4[NodeCount] : new Matrix4x4[NodeCount];
            for (int i = 0; i < NodeCount; i++)
            {
                Matrix4x4 local = localByNode[i].ToMatrix();
                int parent = ParentIndices[i];
                world[i] = parent < 0 ? local : local * world[parent];
            }
            for (int b = 0; b < BoneCount; b++)
                bonePaletteOut[b] = world[JointToNode[b]];
        }
    }
}
