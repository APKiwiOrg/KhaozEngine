using System;
using System.Collections.Generic;
using System.IO;
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
        SkinnedGltfMesh hostMesh, SkinningProfile profile, IReadOnlyList<string> names,
        IReadOnlyList<Matrix4x4> inverseBind, int restCount)
    {
        string context = Context(part);
        if (profile != hostMesh.Profile)
            throw new InvalidDataException($"{context}: garment profile {profile.Method} version {profile.AlgorithmVersion} " +
                $"must match host skin profile {hostMesh.Profile.Method} version {hostMesh.Profile.AlgorithmVersion}.");
        Profile = profile;
        CheckCount(names.Count, context);
        if (inverseBind.Count != names.Count || restCount != names.Count)
            throw new InvalidDataException($"{context}: garment inverse binds and rest poses must have one entry per skin joint.");
        _partId = part.Id;
        _hostBones = new int[names.Count];
        for (int garmentBone = 0; garmentBone < names.Count; garmentBone++)
        {
            string name = names[garmentBone];
            int hostBone;
            try { hostBone = map.Bone(name); }
            catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or ArgumentException)
            {
                throw new InvalidDataException($"{context}: joint '{name}' cannot bind to the host skin.", error);
            }
            Matrix4x4 garmentBind = inverseBind[garmentBone], hostBind = hostMesh.InverseBind[hostBone];
            if (!MatchingBind(garmentBind, hostBind))
                throw new InvalidDataException($"{context}: joint '{name}' inverse bind must be finite and match the host within {BindTolerance} per element.");
            _hostBones[garmentBone] = hostBone;
        }
    }

    internal int JointCount => _hostBones.Length;
    internal SkinningProfile Profile { get; }

    internal static void ValidateHost(ContractJointMap map, SkinnedGltfMesh hostMesh)
    {
        if (!ReferenceEquals(hostMesh.Skeleton, map.Skeleton))
            throw new ArgumentException("The host mesh and contract map must use the exact same skeleton instance.", nameof(hostMesh));
        if (hostMesh.InverseBind.Length != map.Skeleton.BoneCount || hostMesh.RestPose.Length != map.Skeleton.BoneCount)
            throw new InvalidDataException("The attachment host mesh needs one inverse bind and rest pose per host skin bone.");
        for (int bone = 0; bone < hostMesh.InverseBind.Length; bone++)
            if (!AttachmentSetBinding.Finite(hostMesh.InverseBind[bone]))
                throw new InvalidDataException($"Attachment host mesh: skin bone {bone} has a non-finite inverse bind.");
    }

    internal static SkinnedAttachmentBinding FromMesh(SkinnedAttachmentPartDefinition part, ContractJointMap map,
        SkinnedGltfMesh hostMesh, IReadOnlyDictionary<AttachmentMeshRef, SkinnedGltfMesh> meshes)
    {
        if (!meshes.TryGetValue(part.Mesh, out SkinnedGltfMesh? mesh) || mesh is null)
            throw new InvalidDataException($"{Context(part)}: garment mesh metadata is missing.");
        if (mesh.Skeleton is not { } skeleton)
            throw new InvalidDataException($"{Context(part)}: garment skin metadata needs a skeleton.");
        CheckCount(skeleton.BoneCount, Context(part));
        var names = new string[skeleton.BoneCount];
        for (int bone = 0; bone < names.Length; bone++)
        {
            int node = skeleton.JointToNode[bone];
            if (node < 0 || node >= skeleton.NodeCount)
                throw new InvalidDataException($"{Context(part)}: garment skin joint {bone} refers to missing node {node}.");
            names[bone] = skeleton.NodeNames[node];
        }
        return new SkinnedAttachmentBinding(part, map, hostMesh, mesh.Profile, names, mesh.InverseBind, mesh.RestPose.Length);
    }

    internal static SkinnedAttachmentBinding FromResources(SkinnedAttachmentPartDefinition part, ContractJointMap map,
        SkinnedGltfMesh hostMesh, AttachmentPartResources resources)
    {
        if (!resources.IsSkinned)
            throw new InvalidDataException($"{Context(part)}: garment skin metadata is missing.");
        return new SkinnedAttachmentBinding(part, map, hostMesh, resources.Profile,
            resources.JointNames, resources.InverseBind, resources.RestPose.Count);
    }

    internal void ValidatePalette(ReadOnlySpan<Matrix4x4> hostPalette)
    {
        foreach (int bone in _hostBones)
            if (!AttachmentSetBinding.ValidMatrix(hostPalette[bone]))
                throw new ArgumentException($"Attachment part {_partId}: host skin bone {bone} has an invalid joint model matrix.", nameof(hostPalette));
    }

    internal void CopyPalette(ReadOnlySpan<Matrix4x4> hostPalette, Span<Matrix4x4> destination)
    {
        if (destination.Length < JointCount)
            throw new ArgumentException($"Output capacity {destination.Length} must hold {JointCount} garment joints for attachment part {_partId}.", nameof(destination));
        ValidatePalette(hostPalette);
        for (int bone = 0; bone < _hostBones.Length; bone++) destination[bone] = hostPalette[_hostBones[bone]];
    }

    static void CheckCount(int count, string context)
    {
        if (count < 1 || count > SkinningMath.MaxBonesPerDraw)
            throw new InvalidDataException($"{context}: garment skin joint count {count} must be between 1 and {SkinningMath.MaxBonesPerDraw}.");
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
