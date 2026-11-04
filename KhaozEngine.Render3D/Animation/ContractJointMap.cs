using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>A loaded skeleton checked against a <see cref="SkeletonContract"/>, with each contract joint's
    /// immutable BASE frames.</summary>
    /// <remarks>The base is a body's zero: the pose every procedural channel or clip layer is measured from. It is
    /// the skin's one-frame stance clip when the skin ships one, and the bind rest otherwise, so a code-built skeleton
    /// or a rigged body with no stance keeps the bind pose as its zero. Every frame derived from the zero follows it:
    /// the base world frames, the parent-base inverses, the body alignment and <see cref="SkinAtBase"/>.
    /// Construction refuses a skeleton with more nodes than a skinned draw takes, two nodes of one name, and a
    /// contract joint that is missing, misparented or (other than the root) outside the skin. Unnamed nodes are
    /// left alone, as <see cref="Skeleton"/> leaves them. Pure presentation. GPU-free.</remarks>
    public sealed class ContractJointMap
    {
        readonly Dictionary<string, int> _nodes = new(StringComparer.Ordinal);
        readonly JointPose[] _baseLocal;
        readonly Matrix4x4[] _baseWorld;
        readonly Matrix4x4[] _parentBaseInverse;
        readonly Matrix4x4[] _bodyAlignment;
        readonly bool[] _isContract;

        /// <summary>Resolves the contract joints over a skeleton and computes their base frames.</summary>
        /// <param name="skeleton">The loaded skeleton, on <paramref name="contract"/>.</param>
        /// <param name="contract">The joints the skeleton must carry, each under its declared parent.</param>
        /// <param name="stance">The skin's stance clip, sampled once as the base, or null to keep the bind rest. A
        /// clip that could mislead a caller is refused: it must be named <paramref name="stanceClipName"/>, key each
        /// track exactly once, animate contract joints only, and carry no scale track.</param>
        /// <param name="stanceClipName">The name a stance clip must carry.</param>
        /// <exception cref="ContractJointMapArgumentException">The skeleton or stance breaks the contract.
        /// <see cref="ContractJointMapArgumentException.Reason"/> identifies the rule, with structured node, joint,
        /// parent or clip identifiers on the exception.</exception>
        public ContractJointMap(Skeleton skeleton, SkeletonContract contract, AnimationClip? stance = null,
            string stanceClipName = "stance")
        {
            ArgumentNullException.ThrowIfNull(skeleton);
            ArgumentNullException.ThrowIfNull(contract);
            ArgumentNullException.ThrowIfNull(stanceClipName);
            if (skeleton.NodeCount > SkinningMath.MaxBonesPerDraw)
                throw new ContractJointMapArgumentException(ContractJointMapRefusal.NodeLimitExceeded,
                    $"The skeleton has {skeleton.NodeCount} nodes, more than the"
                    + $" {SkinningMath.MaxBonesPerDraw} a skinned draw takes.", nameof(skeleton))
                { NodeCount = skeleton.NodeCount, MaximumNodeCount = SkinningMath.MaxBonesPerDraw };

            Skeleton = skeleton;
            Contract = contract;
            _baseWorld = new Matrix4x4[skeleton.NodeCount];
            _parentBaseInverse = new Matrix4x4[skeleton.NodeCount];
            _bodyAlignment = new Matrix4x4[skeleton.NodeCount];
            _isContract = new bool[skeleton.NodeCount];
            for (int node = 0; node < skeleton.NodeCount; node++)
            {
                string nodeName = skeleton.NodeNames[node];
                if (nodeName.Length > 0 && !_nodes.TryAdd(nodeName, node))
                    throw new ContractJointMapArgumentException(ContractJointMapRefusal.DuplicateNodeName,
                        $"Duplicate skeleton node name '{nodeName}'.", nameof(skeleton))
                    { NodeName = nodeName, NodeIndex = node };
            }

            _baseLocal = stance is null
                ? (JointPose[])skeleton.RestLocal.Clone()
                : SampleStance(skeleton, contract, stance, stanceClipName);

            for (int index = 0; index < contract.Joints.Count; index++)
            {
                (string name, string? expectedParent, _) = contract.Joints[index];
                if (!_nodes.TryGetValue(name, out int node))
                    throw new ContractJointMapArgumentException(ContractJointMapRefusal.ContractJointMissing,
                        $"Missing contract joint '{name}'.", nameof(skeleton)) { JointName = name };

                int parent = skeleton.ParentIndices[node];
                if (parent < -1 || parent >= skeleton.NodeCount)
                    throw new ContractJointMapArgumentException(ContractJointMapRefusal.ParentIndexInvalid,
                        $"Joint '{name}' has an invalid parent index {parent}.", nameof(skeleton))
                    { JointName = name, ParentIndex = parent, NodeCount = skeleton.NodeCount };
                string? actualParent = parent < 0 ? null : skeleton.NodeNames[parent];
                if (!string.Equals(actualParent, expectedParent, StringComparison.Ordinal))
                    throw new ContractJointMapArgumentException(ContractJointMapRefusal.ParentNameMismatch,
                        $"Joint '{name}' has parent '{actualParent ?? "none"}',"
                        + $" expected '{expectedParent ?? "none"}'.", nameof(skeleton))
                    { JointName = name, ActualParentName = actualParent, ExpectedParentName = expectedParent };
                if (name != contract.Root && skeleton.BoneIndexOfNode(node) < 0)
                    throw new ContractJointMapArgumentException(ContractJointMapRefusal.ContractJointOutsideSkin,
                        $"Joint '{name}' is not in the skin.", nameof(skeleton)) { JointName = name, NodeIndex = node };

                Matrix4x4 parentWorld = parent < 0 ? Matrix4x4.Identity : _baseWorld[parent];
                _baseWorld[node] = _baseLocal[node].ToMatrix() * parentWorld;
                if (!Matrix4x4.Invert(parentWorld, out _parentBaseInverse[node]))
                    throw new ContractJointMapArgumentException(ContractJointMapRefusal.ParentBaseFrameSingular,
                        $"Joint '{name}' has a singular parent base frame.", nameof(skeleton))
                    { JointName = name, ParentNodeIndex = parent };
                Matrix4x4 rigidBase = BoneSocket.ComposeRigid(Matrix4x4.Identity, _baseWorld[node], Matrix4x4.Identity);
                if (!Matrix4x4.Invert(rigidBase, out Matrix4x4 inverseRigidBase))
                    throw new ContractJointMapArgumentException(ContractJointMapRefusal.BaseFrameSingular,
                        $"Joint '{name}' has a singular base frame.", nameof(skeleton)) { JointName = name, NodeIndex = node };
                _bodyAlignment[node] = Matrix4x4.CreateTranslation(rigidBase.Translation) * inverseRigidBase;
                // General inversion can round the homogeneous one of this engine-owned rigid affine frame.
                _bodyAlignment[node].M44 = 1f;
                _isContract[node] = true;
            }
        }

        /// <summary>The skeleton the map was built over.</summary>
        public Skeleton Skeleton { get; }

        /// <summary>The contract the skeleton was checked against.</summary>
        public SkeletonContract Contract { get; }

        /// <summary>The skeleton node of a name, a contract joint or any other named node.</summary>
        /// <exception cref="ContractJointMapKeyNotFoundException">The skeleton has no node of that name,
        /// with <see cref="ContractJointMapRefusal.NodeNameMissing"/> and the requested JointName.</exception>
        public int Node(string name) =>
            _nodes.TryGetValue(name, out int node)
                ? node
                : throw new ContractJointMapKeyNotFoundException(ContractJointMapRefusal.NodeNameMissing,
                    $"No skeleton node named '{name}'.") { JointName = name };

        /// <summary>The skin bone of a named joint.</summary>
        /// <exception cref="ContractJointMapKeyNotFoundException">The skeleton has no node of that name,
        /// with <see cref="ContractJointMapRefusal.NodeNameMissing"/> and the requested JointName.</exception>
        /// <exception cref="ContractJointMapStateException">The joint is not in the skin, as the root may not be,
        /// with <see cref="ContractJointMapRefusal.NamedNodeOutsideSkin"/> and JointName.</exception>
        public int Bone(string name)
        {
            int bone = Skeleton.BoneIndexOfNode(Node(name));
            return bone >= 0
                ? bone
                : throw new ContractJointMapStateException(ContractJointMapRefusal.NamedNodeOutsideSkin,
                    $"Joint '{name}' is not in the skin.") { JointName = name };
        }

        /// <summary>The zero, one local pose per node: the stance when the skin ships one, the bind rest otherwise.
        /// </summary>
        public ReadOnlySpan<JointPose> BaseLocal => _baseLocal;

        /// <summary>A contract joint's model frame at the base (the stance, or the bind rest without one).</summary>
        /// <param name="node">A contract joint's node.</param>
        /// <exception cref="ContractJointMapArgumentException">The node is not a contract joint, with
        /// <see cref="ContractJointMapRefusal.NodeOutsideContract"/>, NodeIndex and NodeName.</exception>
        public Matrix4x4 BaseWorld(int node) => _baseWorld[ContractNode(node)];

        /// <summary>The inverse of a contract joint's parent model frame at the base, which turns a model frame back
        /// into the joint's local pose.</summary>
        /// <param name="node">A contract joint's node.</param>
        /// <exception cref="ContractJointMapArgumentException">The node is not a contract joint, with
        /// <see cref="ContractJointMapRefusal.NodeOutsideContract"/>, NodeIndex and NodeName.</exception>
        public Matrix4x4 ParentBaseInverse(int node) => _parentBaseInverse[ContractNode(node)];

        /// <summary>Takes a joint's base ORIENTATION back out, for a rigid piece authored in the body's own axes.
        /// </summary>
        /// <remarks>A composed joint carries its base orientation (the authoring tool's bone axes, turned by the
        /// stance if there is one), but a grip, a hair piece or a wrap is authored in the frame the rigid body holds
        /// it in: at the joint, with the body's axes. So
        /// <c>piece * BodyAlignment(node) * BoneSocket.ComposeRigid(Identity, jointModel, Identity)</c> sits the
        /// piece at the joint with the body's axes at the base and carries it through the joint's motion from there.
        /// This is <c>T(baseOffset) * inverse(jointBaseModel)</c>, taken off the rigid part of the base frame so it
        /// cancels exactly what <see cref="BoneSocket.ComposeRigid"/> keeps. A pure rotation, and the identity on a
        /// joint that sits at the body's axes at the base. The homogeneous component is exactly one even when
        /// general inversion rounds it for oblique base axes.</remarks>
        /// <param name="node">A contract joint's node.</param>
        /// <exception cref="ContractJointMapArgumentException">The node is not a contract joint, with
        /// <see cref="ContractJointMapRefusal.NodeOutsideContract"/>, NodeIndex and NodeName.</exception>
        public Matrix4x4 BodyAlignment(int node) => _bodyAlignment[ContractNode(node)];

        /// <summary>Every vertex of a skin on this map's skeleton, deformed to the base, in model space.</summary>
        /// <remarks>The same deform the shader applies: the base composed into joint model matrices, each after its
        /// bone's inverse bind, blended per vertex. Without a stance this is the bind pose, so the positions are the
        /// skin's own to within a rounding. Allocates the result and two palettes, so call it at load, not per frame.
        /// </remarks>
        /// <param name="mesh">A skin whose skeleton is this map's.</param>
        /// <exception cref="ContractJointMapArgumentException">The mesh poses through another skeleton, with
        /// <see cref="ContractJointMapRefusal.MeshSkeletonMismatch"/> and ParamName mesh.</exception>
        public Vector3[] SkinAtBase(SkinnedGltfMesh mesh)
        {
            ArgumentNullException.ThrowIfNull(mesh);
            if (!ReferenceEquals(mesh.Skeleton, Skeleton))
                throw new ContractJointMapArgumentException(ContractJointMapRefusal.MeshSkeletonMismatch,
                    "The mesh and joint map must use the same skeleton.", nameof(mesh));

            var joints = new Matrix4x4[Skeleton.BoneCount];
            Skeleton.ComposeInto(_baseLocal, joints);
            var skin = new Matrix4x4[joints.Length];
            for (int bone = 0; bone < skin.Length; bone++)
                skin[bone] = SkinningMath.Compose(joints[bone], mesh.InverseBind[bone]);
            var positions = new Vector3[mesh.Vertices.Length];
            for (int i = 0; i < positions.Length; i++)
                positions[i] = SkinningMath.SkinVertex(mesh.Vertices[i], skin).Position;
            return positions;
        }

        // A node the frame accessors may read. The base frames exist for contract joints only, so any other node is
        // refused rather than read as a zero matrix.
        int ContractNode(int node)
        {
            if (node < 0 || node >= _isContract.Length)
                throw new ArgumentOutOfRangeException(nameof(node), node,
                    $"The skeleton has no node {node}. It has {_isContract.Length}.");
            if (!_isContract[node])
                throw new ContractJointMapArgumentException(ContractJointMapRefusal.NodeOutsideContract,
                    $"Node {node} '{Skeleton.NodeNames[node]}' is not a contract joint.", nameof(node))
                { NodeIndex = node, NodeName = Skeleton.NodeNames[node] };
            return node;
        }

        // One local pose per node with the stance laid over the rest. A clip that could mislead a caller is refused
        // before it is sampled. Tracks name glTF logical nodes, which are not the skeleton's node order.
        static JointPose[] SampleStance(Skeleton skeleton, SkeletonContract contract, AnimationClip stance,
            string stanceClipName)
        {
            if (!string.Equals(stance.Name, stanceClipName, StringComparison.Ordinal))
                throw new ContractJointMapArgumentException(ContractJointMapRefusal.StanceNameMismatch,
                    $"The base pose clip is '{stance.Name}', expected '{stanceClipName}'.", nameof(stance))
                { ClipName = stance.Name, ExpectedClipName = stanceClipName };
            foreach (JointTrack track in stance.Tracks)
            {
                int node = skeleton.NodeForLogicalIndex(track.TargetNode);
                if (node < 0)
                    throw new ContractJointMapArgumentException(ContractJointMapRefusal.StanceNodeMissing,
                        $"The stance animates glTF node {track.TargetNode}, which is not a"
                        + " contract joint of this skeleton.", nameof(stance))
                    { TargetNode = track.TargetNode, ClipName = stance.Name };
                string joint = skeleton.NodeNames[node];
                if (!contract.Contains(joint))
                    throw new ContractJointMapArgumentException(ContractJointMapRefusal.StanceNodeOutsideContract,
                        $"The stance animates glTF node {track.TargetNode} '{joint}', which is"
                        + " not a contract joint.", nameof(stance))
                    { TargetNode = track.TargetNode, JointName = joint, ClipName = stance.Name };
                if (track.Scale is not null)
                    throw new ContractJointMapArgumentException(ContractJointMapRefusal.StanceScaleTrack,
                        $"The stance scales '{joint}'. A stance may only turn and move joints.", nameof(stance))
                    { JointName = joint, ClipName = stance.Name };
                int keys = track.Rotation is { Times.Length: not 1 } rotation ? rotation.Times.Length
                    : track.Translation is { Times.Length: not 1 } translation ? translation.Times.Length
                    : 1;
                if (keys != 1)
                    throw new ContractJointMapArgumentException(ContractJointMapRefusal.StanceTrackKeyCountMismatch,
                        $"The stance keys '{joint}' {keys} times, expected once. A stance is"
                        + " one pose.", nameof(stance))
                    { JointName = joint, KeyCount = keys, ExpectedKeyCount = 1, ClipName = stance.Name };
            }
            return AnimationSampler.SamplePose(stance, skeleton, 0f);
        }
    }
}
