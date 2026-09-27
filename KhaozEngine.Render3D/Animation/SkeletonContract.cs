using System;
using System.Collections.Generic;

namespace KhaozEngine.Render3D
{
    /// <summary>One joint a skinned body promises to carry: its glTF node name, its parent's name (<c>null</c> for
    /// the root), and whether it deforms the skin. A joint that does not deform is the root or a socket.</summary>
    public readonly record struct ContractJoint(string Name, string? Parent, bool Deforms);

    /// <summary>The named joints a family of skinned bodies carries, each with its parent: the table a game's rig
    /// scripts, clips and runtime agree on, checked against a loaded skeleton by <see cref="ContractJointMap"/>.
    /// </summary>
    /// <remarks>The table is validated once on construction. It is not empty, every name is unique, exactly one
    /// joint has no parent and it comes first, and every other joint's parent is declared before it. So one forward
    /// pass over <see cref="Joints"/> meets each parent before its children, as a <see cref="Skeleton"/>'s node
    /// order does. Names compare ordinally. Pure data. GPU-free.</remarks>
    public sealed class SkeletonContract
    {
        readonly HashSet<string> _names = new(StringComparer.Ordinal);

        /// <summary>Validates and copies a joint table.</summary>
        /// <param name="joints">The joints, the root first and each parent before its children. The list is copied,
        /// so the caller may reuse it.</param>
        /// <exception cref="ArgumentException">The table is empty, or a joint is unnamed, declared twice, a second
        /// root, or parented to a joint not declared before it. The message names the joint.</exception>
        public SkeletonContract(IReadOnlyList<ContractJoint> joints)
        {
            ArgumentNullException.ThrowIfNull(joints);
            if (joints.Count == 0)
                throw new ArgumentException("A skeleton contract needs at least its root joint.", nameof(joints));

            var copy = new ContractJoint[joints.Count];
            for (int index = 0; index < copy.Length; index++)
            {
                ContractJoint joint = joints[index];
                if (string.IsNullOrEmpty(joint.Name))
                    throw new ArgumentException($"Contract joint {index} has no name.", nameof(joints));
                if (index == 0 && joint.Parent is not null)
                    throw new ArgumentException($"The first contract joint '{joint.Name}' has parent '{joint.Parent}'."
                        + " The first joint must be the root, with no parent.", nameof(joints));
                if (index > 0 && joint.Parent is null)
                    throw new ArgumentException($"Contract joint '{joint.Name}' is a second root. Only the first joint"
                        + $" '{copy[0].Name}' has no parent.", nameof(joints));
                if (joint.Parent is not null && !_names.Contains(joint.Parent))
                    throw new ArgumentException($"Contract joint '{joint.Name}' has parent '{joint.Parent}', which is"
                        + " not declared before it.", nameof(joints));
                if (!_names.Add(joint.Name))
                    throw new ArgumentException($"Contract joint '{joint.Name}' is declared twice.", nameof(joints));
                copy[index] = joint;
            }

            Joints = Array.AsReadOnly(copy);
            Root = copy[0].Name;
        }

        /// <summary>Every joint, in declaration order: the root first, each parent before its children.</summary>
        public IReadOnlyList<ContractJoint> Joints { get; }

        /// <summary>The root joint's name, the one joint with no parent.</summary>
        public string Root { get; }

        /// <summary>Whether the contract declares a joint of this name (ordinal).</summary>
        public bool Contains(string name)
        {
            ArgumentNullException.ThrowIfNull(name);
            return _names.Contains(name);
        }
    }
}
