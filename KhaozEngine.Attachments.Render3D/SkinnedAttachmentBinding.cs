using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>An immutable garment-skin to host-skin index table. Frame palettes belong to the caller.</summary>
internal sealed class SkinnedAttachmentBinding
{
    const float BindTolerance = 1e-4f;
    readonly uint _partId;
    readonly int[] _hostBones;

    SkinnedAttachmentBinding(SkinnedAttachmentPartDefinition part, ContractJointMap map,
        SkinnedGltfMesh hostMesh, IReadOnlyList<string> names, IReadOnlyList<Matrix4x4> inverseBind, int restCount)
    {
        string context = Context(part);
        CheckCount(names.Count, part);
        if (inverseBind.Count != names.Count || restCount != names.Count)
            throw new AttachmentBindingDataException(AttachmentBindingRefusal.GarmentBindCountsMismatch,
                $"{context}: garment inverse binds and rest poses must have one entry per skin joint.")
            { PartId = part.Id, MeshKey = part.Mesh.Key, JointCount = names.Count, InverseBindCount = inverseBind.Count, RestCount = restCount };
        _partId = part.Id;
        _hostBones = new int[names.Count];
        for (int garmentBone = 0; garmentBone < names.Count; garmentBone++)
        {
            string name = names[garmentBone];
            int hostBone;
            try { hostBone = map.Bone(name); }
            catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or ArgumentException)
            {
                throw new AttachmentBindingDataException(AttachmentBindingRefusal.GarmentJointUnresolved,
                    $"{context}: joint '{name}' cannot bind to the host skin.", error)
                { PartId = part.Id, MeshKey = part.Mesh.Key, JointName = name };
            }
            Matrix4x4 garmentBind = inverseBind[garmentBone], hostBind = hostMesh.InverseBind[hostBone];
            if (!MatchingBind(garmentBind, hostBind))
                throw new AttachmentBindingDataException(AttachmentBindingRefusal.GarmentBindMismatch,
                    $"{context}: joint '{name}' inverse bind must be finite and match the host within {BindTolerance} per element.")
                { PartId = part.Id, MeshKey = part.Mesh.Key, JointName = name, BindTolerance = BindTolerance };
            _hostBones[garmentBone] = hostBone;
        }
    }

    internal int JointCount => _hostBones.Length;

    internal static void ValidateHost(ContractJointMap map, SkinnedGltfMesh hostMesh)
    {
        if (!ReferenceEquals(hostMesh.Skeleton, map.Skeleton))
            throw new AttachmentBindingArgumentException(AttachmentBindingRefusal.HostSkeletonMismatch,
                "The host mesh and contract map must use the exact same skeleton instance.", nameof(hostMesh));
        if (hostMesh.InverseBind.Length != map.Skeleton.BoneCount || hostMesh.RestPose.Length != map.Skeleton.BoneCount)
            throw new AttachmentBindingDataException(AttachmentBindingRefusal.HostBindCountsMismatch,
                "The attachment host mesh needs one inverse bind and rest pose per host skin bone.")
            { JointCount = map.Skeleton.BoneCount, InverseBindCount = hostMesh.InverseBind.Length, RestCount = hostMesh.RestPose.Length };
        for (int bone = 0; bone < hostMesh.InverseBind.Length; bone++)
            if (!AttachmentSetBinding.Finite(hostMesh.InverseBind[bone]))
                throw new AttachmentBindingDataException(AttachmentBindingRefusal.HostInverseBindNonFinite,
                    $"Attachment host mesh: skin bone {bone} has a non-finite inverse bind.")
                { BoneIndex = bone };
    }

    internal static SkinnedAttachmentBinding FromMesh(SkinnedAttachmentPartDefinition part, ContractJointMap map,
        SkinnedGltfMesh hostMesh, IReadOnlyDictionary<AttachmentMeshRef, SkinnedGltfMesh> meshes)
    {
        if (!meshes.TryGetValue(part.Mesh, out SkinnedGltfMesh? mesh) || mesh is null)
            throw new AttachmentBindingDataException(AttachmentBindingRefusal.GarmentMetadataMissing,
                $"{Context(part)}: garment mesh metadata is missing.")
            { PartId = part.Id, MeshKey = part.Mesh.Key };
        if (mesh.Skeleton is not { } skeleton)
            throw new AttachmentBindingDataException(AttachmentBindingRefusal.GarmentSkeletonMissing,
                $"{Context(part)}: garment skin metadata needs a skeleton.")
            { PartId = part.Id, MeshKey = part.Mesh.Key };
        CheckCount(skeleton.BoneCount, part);
        var names = new string[skeleton.BoneCount];
        for (int bone = 0; bone < names.Length; bone++)
        {
            int node = skeleton.JointToNode[bone];
            if (node < 0 || node >= skeleton.NodeCount)
                throw new AttachmentBindingDataException(AttachmentBindingRefusal.GarmentNodeMissing,
                    $"{Context(part)}: garment skin joint {bone} refers to missing node {node}.")
                { PartId = part.Id, MeshKey = part.Mesh.Key, BoneIndex = bone, NodeIndex = node, NodeCount = skeleton.NodeCount };
            names[bone] = skeleton.NodeNames[node];
        }
        return new SkinnedAttachmentBinding(part, map, hostMesh, names, mesh.InverseBind, mesh.RestPose.Length);
    }

    internal static SkinnedAttachmentBinding FromResources(SkinnedAttachmentPartDefinition part, ContractJointMap map,
        SkinnedGltfMesh hostMesh, AttachmentPartResources resources)
    {
        if (!resources.IsSkinned)
            throw new AttachmentBindingDataException(AttachmentBindingRefusal.GarmentMetadataMissing,
                $"{Context(part)}: garment skin metadata is missing.")
            { PartId = part.Id, MeshKey = part.Mesh.Key };
        return new SkinnedAttachmentBinding(part, map, hostMesh, resources.JointNames, resources.InverseBind, resources.RestPose.Count);
    }

    internal void ValidatePalette(ReadOnlySpan<Matrix4x4> hostPalette)
    {
        foreach (int bone in _hostBones)
            if (!AttachmentSetBinding.ValidMatrix(hostPalette[bone]))
                throw new AttachmentBindingArgumentException(AttachmentBindingRefusal.HostJointPoseInvalid,
                    $"Attachment part {_partId}: host skin bone {bone} has an invalid joint model matrix.", nameof(hostPalette))
                { PartId = _partId, BoneIndex = bone };
    }

    internal void CopyPalette(ReadOnlySpan<Matrix4x4> hostPalette, Span<Matrix4x4> destination)
    {
        if (destination.Length < JointCount)
            throw new AttachmentBindingArgumentException(AttachmentBindingRefusal.GarmentPaletteOutputTooSmall,
                $"Output capacity {destination.Length} must hold {JointCount} garment joints for attachment part {_partId}.", nameof(destination))
            { PartId = _partId, ActualCapacity = destination.Length, RequiredCapacity = JointCount };
        ValidatePalette(hostPalette);
        for (int bone = 0; bone < _hostBones.Length; bone++) destination[bone] = hostPalette[_hostBones[bone]];
    }

    static void CheckCount(int count, SkinnedAttachmentPartDefinition part)
    {
        if (count < 1 || count > SkinningMath.MaxBonesPerDraw)
            throw new AttachmentBindingDataException(AttachmentBindingRefusal.GarmentJointCountOutsideRange,
                $"{Context(part)}: garment skin joint count {count} must be between 1 and {SkinningMath.MaxBonesPerDraw}.")
            { PartId = part.Id, MeshKey = part.Mesh.Key, JointCount = count, MinimumJointCount = 1, MaximumJointCount = SkinningMath.MaxBonesPerDraw };
    }

    static bool MatchingBind(in Matrix4x4 garment, in Matrix4x4 host)
    {
        if (!AttachmentSetBinding.Finite(garment) || !AttachmentSetBinding.Finite(host)) return false;
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
                if (MathF.Abs(garment[row, column] - host[row, column]) > BindTolerance) return false;
        return true;
    }

    static string Context(SkinnedAttachmentPartDefinition part) => $"Attachment part {part.Id}, mesh '{part.Mesh.Key}'";
}
