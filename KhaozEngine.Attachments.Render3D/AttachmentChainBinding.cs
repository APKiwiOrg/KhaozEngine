using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>Resolved chains and links for one skinned garment part.</summary>
internal sealed class AttachmentChainBinding
{
    internal AttachmentChainBinding(SkinnedAttachmentPartDefinition part, ChainMotionDefinition motion, ContractJointMap map,
        ReadOnlySpan<Matrix4x4> hostBinds, IReadOnlyList<string> names, IReadOnlyList<GarmentChainJoint> joints,
        IReadOnlyList<Matrix4x4> garmentBinds)
    {
        var bonesByName = new Dictionary<string, int>(names.Count, StringComparer.Ordinal);
        for (int bone = 0; bone < names.Count; bone++) bonesByName.TryAdd(names[bone], bone);
        Chains = new BoundAttachmentChain[motion.Chains.Count];
        Links = motion.Links;
        for (int chainIndex = 0; chainIndex < Chains.Length; chainIndex++)
        {
            ChainDefinition chain = motion.Chains[chainIndex];
            int count = chain.Joints.Count;
            var paletteBones = new int[count];
            var restLocals = new ChainJointRest[count];
            var restFromAnchor = new Matrix4x4[count];
            int anchor = -1, previousNode = -1;
            Matrix4x4 restWorld = Matrix4x4.Identity, relative = Matrix4x4.Identity;
            for (int index = 0; index < count; index++)
            {
                string name = chain.Joints[index];
                if (!bonesByName.TryGetValue(name, out int bone))
                    throw Refuse(AttachmentBindingRefusal.ChainJointMissing, part, chainIndex, name,
                        "the joint is absent from the garment skin.");
                if (map.Skeleton.TryIndexOf(name, out int hostNode) && map.Skeleton.BoneIndexOfNode(hostNode) >= 0)
                    throw Refuse(AttachmentBindingRefusal.ChainJointIsHostJoint, part, chainIndex, name,
                        "the joint is also on the host skin.");

                GarmentChainJoint joint = joints[bone];
                if (index == 0)
                {
                    anchor = ResolveAnchor(map, part, chainIndex, name, joint.ParentName);
                    if (!Matrix4x4.Invert(hostBinds[anchor], out restWorld))
                        throw Refuse(AttachmentBindingRefusal.ChainBindMismatch, part, chainIndex, name,
                            "its anchor has a singular inverse bind.");
                }
                else if (joint.Parent != previousNode)
                    throw Refuse(AttachmentBindingRefusal.ChainNotParentLine, part, chainIndex, name,
                        "its parent is not the previous chain joint.");

                JointPose local = joint.RestLocal;
                Matrix4x4 localMatrix = local.ToMatrix();
                relative = localMatrix * relative;
                restWorld = localMatrix * restWorld;
                if (local.Rotation == default || !AttachmentSetBinding.ValidMatrix(relative)
                    || !Matrix4x4.Invert(restWorld, out Matrix4x4 expected)
                    || !SkinnedAttachmentBinding.MatchingBind(garmentBinds[bone], expected))
                    throw Refuse(AttachmentBindingRefusal.ChainBindMismatch, part, chainIndex, name,
                        "its inverse bind does not match its rest frame on the host anchor.");

                previousNode = joint.Node;
                paletteBones[index] = bone;
                restLocals[index] = new ChainJointRest(local.Translation, local.Rotation, local.Scale);
                restFromAnchor[index] = relative;
            }
            Chains[chainIndex] = new BoundAttachmentChain(anchor, paletteBones, restLocals, restFromAnchor, chain.Tip, chain.Settings);
        }
    }

    internal BoundAttachmentChain[] Chains { get; }
    internal IReadOnlyList<ChainLink> Links { get; }

    static int ResolveAnchor(ContractJointMap map, SkinnedAttachmentPartDefinition part, int chain, string joint, string? parentName)
    {
        if (string.IsNullOrEmpty(parentName))
            throw Refuse(AttachmentBindingRefusal.ChainAnchorNotHostJoint, part, chain, joint,
                "the first chain joint has no named parent.");
        try
        {
            return map.Bone(parentName);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new AttachmentBindingDataException(AttachmentBindingRefusal.ChainAnchorNotHostJoint,
                $"Attachment part {part.Id}, mesh '{part.Mesh.Key}', chain {chain}: parent '{parentName}' is outside the host skin.", error)
            { PartId = part.Id, MeshKey = part.Mesh.Key, ChainIndex = chain, JointName = parentName };
        }
    }

    static AttachmentBindingDataException Refuse(AttachmentBindingRefusal reason, SkinnedAttachmentPartDefinition part,
        int chain, string joint, string detail)
    {
        var message = $"Attachment part {part.Id}, mesh '{part.Mesh.Key}', chain {chain}, joint '{joint}': {detail}";
        return new AttachmentBindingDataException(reason, message)
        {
            PartId = part.Id, MeshKey = part.Mesh.Key, ChainIndex = chain, JointName = joint,
            BindTolerance = reason == AttachmentBindingRefusal.ChainBindMismatch ? SkinnedAttachmentBinding.BindTolerance : null,
        };
    }
}

/// <summary>A garment joint's imported location in the node tree and its local rest pose.</summary>
internal readonly record struct GarmentChainJoint(int Node, int Parent, string? ParentName, JointPose RestLocal)
{
    internal static GarmentChainJoint Capture(Skeleton skeleton, int node)
    {
        int parent = skeleton.ParentIndices[node];
        string? parentName = (uint)parent < (uint)skeleton.NodeCount ? skeleton.NodeNames[parent] : null;
        return new GarmentChainJoint(node, parent, parentName, skeleton.RestLocal[node]);
    }
}

/// <summary>A chain's host anchor and garment palette slots, indexed from its anchor toward its tip.</summary>
internal sealed record BoundAttachmentChain(int AnchorBone, int[] PaletteBones, ChainJointRest[] RestLocals,
    Matrix4x4[] RestFromAnchor, Vector3 Tip, ChainSettings Settings)
{
    internal void CopyPalette(ReadOnlySpan<Matrix4x4> frames, in Matrix4x4 anchor, Span<Matrix4x4> destination)
    {
        for (int joint = 0; joint < PaletteBones.Length; joint++)
            destination[PaletteBones[joint]] = frames[joint] * anchor;
    }
}
