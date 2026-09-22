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

        /// <summary>glTF node name per skeleton node, in node order. An unnamed node is represented by an empty
        /// string.</summary>
        public IReadOnlyList<string> NodeNames { get; }

        public int NodeCount => ParentIndices.Length;
        public int BoneCount => JointToNode.Length;

        Dictionary<int, int>? _logicalToNode;
        Dictionary<string, int>? _nameToNode;
        int[]? _nodeToBone;

        public Skeleton(int[] parentIndices, JointPose[] restLocal, int[] nodeLogicalIndex, int[] jointToNode)
            : this(parentIndices, restLocal, nodeLogicalIndex, jointToNode, EmptyNodeNames(parentIndices))
        {
        }

        public Skeleton(int[] parentIndices, JointPose[] restLocal, int[] nodeLogicalIndex, int[] jointToNode,
            IReadOnlyList<string> nodeNames)
        {
            ParentIndices = parentIndices ?? throw new ArgumentNullException(nameof(parentIndices));
            RestLocal = restLocal ?? throw new ArgumentNullException(nameof(restLocal));
            NodeLogicalIndex = nodeLogicalIndex ?? throw new ArgumentNullException(nameof(nodeLogicalIndex));
            JointToNode = jointToNode ?? throw new ArgumentNullException(nameof(jointToNode));
            if (nodeNames is null) throw new ArgumentNullException(nameof(nodeNames));
            if (restLocal.Length != parentIndices.Length || nodeLogicalIndex.Length != parentIndices.Length)
                throw new ArgumentException("parentIndices, restLocal, and nodeLogicalIndex must have one entry per skeleton node.");
            if (nodeNames.Count != parentIndices.Length)
                throw new ArgumentException("nodeNames must have one entry per skeleton node.", nameof(nodeNames));

            var names = new string[nodeNames.Count];
            for (int i = 0; i < names.Length; i++) names[i] = nodeNames[i] ?? string.Empty;
            NodeNames = Array.AsReadOnly(names);
        }

        static string[] EmptyNodeNames(int[] parentIndices)
        {
            if (parentIndices is null) throw new ArgumentNullException(nameof(parentIndices));
            return new string[parentIndices.Length];
        }

        /// <summary>Find a skeleton node by its case-sensitive glTF name. Throws an <see cref="ArgumentException"/>
        /// naming the requested and known names when no node matches. Duplicate non-empty names are ambiguous and
        /// throw an <see cref="InvalidOperationException"/> when name lookup is first used.</summary>
        public int IndexOf(string nodeName)
        {
            if (TryIndexOf(nodeName, out int node)) return node;

            var known = new List<string>();
            for (int i = 0; i < NodeNames.Count; i++)
                if (NodeNames[i].Length > 0) known.Add(NodeNames[i]);
            string knownText = known.Count > 0 ? string.Join(", ", known) : "(none)";
            throw new ArgumentException(
                $"Skeleton joint '{nodeName}' was not found. Known names: {knownText}.", nameof(nodeName));
        }

        /// <summary>Try to find a skeleton node by its case-sensitive glTF name. Returns <c>false</c> and
        /// <c>node = -1</c> when no node matches. Duplicate non-empty names throw because the lookup is ambiguous.</summary>
        public bool TryIndexOf(string nodeName, out int node)
        {
            if (nodeName is null) throw new ArgumentNullException(nameof(nodeName));
            if (NameToNode().TryGetValue(nodeName, out node)) return true;
            node = -1;
            return false;
        }

        Dictionary<string, int> NameToNode()
        {
            if (_nameToNode is not null) return _nameToNode;

            var lookup = new Dictionary<string, int>(NodeNames.Count, StringComparer.Ordinal);
            for (int i = 0; i < NodeNames.Count; i++)
            {
                string name = NodeNames[i];
                if (name.Length == 0) continue;
                if (!lookup.TryAdd(name, i))
                    throw new InvalidOperationException($"Skeleton node name '{name}' is duplicated and cannot be resolved.");
            }
            _nameToNode = lookup;
            return lookup;
        }

        /// <summary>The skin bone index for a skeleton node, or <c>-1</c> when the node is not a skin joint.</summary>
        public int BoneIndexOfNode(int node)
        {
            if (node < 0 || node >= NodeCount) return -1;
            if (_nodeToBone is null)
            {
                var nodeToBone = new int[NodeCount];
                Array.Fill(nodeToBone, -1);
                for (int bone = 0; bone < JointToNode.Length; bone++) nodeToBone[JointToNode[bone]] = bone;
                _nodeToBone = nodeToBone;
            }
            return _nodeToBone[node];
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
