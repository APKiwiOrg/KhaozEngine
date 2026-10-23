using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    readonly List<Matrix4x4> _outlineBoneMatrices = new();

    /// <summary>Adds one posed skinned mesh part to a target outline group from this frame.</summary>
    public void DrawSkinnedOutline(
        MeshOutlineGroup group,
        SkinnedMeshHandle mesh,
        ReadOnlySpan<Matrix4x4> boneMatrices,
        Matrix4x4 world) =>
        AddSkinnedOutlinePart(group, mesh, boneMatrices, world, 0f, false);

    /// <summary>Adds one posed and dissolving skinned mesh part to a target outline group from this frame.</summary>
    public void DrawSkinnedOutlineDissolved(
        MeshOutlineGroup group,
        SkinnedMeshHandle mesh,
        ReadOnlySpan<Matrix4x4> boneMatrices,
        Matrix4x4 world,
        float dissolve,
        bool dissolveComplement) =>
        AddSkinnedOutlinePart(group, mesh, boneMatrices, world, dissolve, dissolveComplement);

    /// <summary>Queues a one-part posed skinned target outline with scene-depth occlusion.</summary>
    public void DrawSkinnedOutline(
        SkinnedMeshHandle mesh,
        ReadOnlySpan<Matrix4x4> boneMatrices,
        Matrix4x4 world,
        Color color,
        float widthPixels) =>
        DrawSkinnedOutline(mesh, boneMatrices, world, color, widthPixels, MeshOutlineOcclusion.SceneDepth);

    /// <summary>Queues a one-part posed skinned target outline with the requested occlusion.</summary>
    public void DrawSkinnedOutline(
        SkinnedMeshHandle mesh,
        ReadOnlySpan<Matrix4x4> boneMatrices,
        Matrix4x4 world,
        Color color,
        float widthPixels,
        MeshOutlineOcclusion occlusion)
    {
        ValidateMeshOutlineStyle(widthPixels, occlusion);
        if (!_skinnedSlots.IsValid(mesh.Index, mesh.Generation)) return;
        if (_skinnedMeshes[mesh.Index] is not { } entry) return;
        PreparedSkinningPose prepared = PrepareSkinning(entry, boneMatrices, "independent target outline", world);
        MeshOutlineGroup group = BeginMeshOutline(color, widthPixels, occlusion);
        AppendSkinnedOutlinePart(RequireMeshOutlineGroup(group), mesh, world, 0f, false, prepared);
    }

    void AddSkinnedOutlinePart(
        MeshOutlineGroup group,
        SkinnedMeshHandle mesh,
        ReadOnlySpan<Matrix4x4> boneMatrices,
        Matrix4x4 world,
        float dissolve,
        bool dissolveComplement)
    {
        MeshOutlineDrawGroup drawGroup = RequireMeshOutlineGroup(group);
        if (!_skinnedSlots.IsValid(mesh.Index, mesh.Generation)) return;
        if (_skinnedMeshes[mesh.Index] is not { } entry) return;

        PreparedSkinningPose prepared = PrepareSkinning(entry, boneMatrices, "independent target outline", world);
        AppendSkinnedOutlinePart(drawGroup, mesh, world, dissolve, dissolveComplement, prepared);
    }

    void AppendSkinnedOutlinePart(MeshOutlineDrawGroup group, SkinnedMeshHandle mesh, Matrix4x4 world,
        float dissolve, bool dissolveComplement, in PreparedSkinningPose prepared)
    {
        int start = _outlineBoneMatrices.Count;
        if (prepared.Profile.Method == SkinningMethod.LinearBlend)
            foreach (Matrix4x4 bone in prepared.Owner.Lbs(prepared)) _outlineBoneMatrices.Add(bone);
        group.Parts.Add(MeshOutlinePart.Skinned(mesh, world, Math.Clamp(dissolve, 0f, 1f),
            dissolveComplement, start, prepared.BoneCount) with
        { Pose = prepared });
        _meshOutlinePartCount++;
    }

    bool EnqueueSkinnedOutlinePart(in MeshOutlinePart part, int drawIndex)
    {
        if (!_skinnedSlots.IsValid(part.SkinnedMesh.Index, part.SkinnedMesh.Generation)) return false;
        if (_skinnedMeshes[part.SkinnedMesh.Index] is not { } skinned) return false;
        if (EnqueueCoverageOutline(part, skinned, drawIndex)) return true;
        if (UseCoverageVertexSkinning(part.SkinnedMesh, part.Coverage.Preparation is not null))
            _targetOutlines.EnqueueSkinnedGpu(skinned.Vb, skinned.Ib, skinned.IndexCount,
                skinned.IndexFormat, skinned.OutlineMaterialSet, part.Pose, drawIndex, ToRender(part.World),
                skinned.AlphaCutoff, part.Dissolve, part.DissolveComplement, _frameOrigin);
        else
        {
            if (_skinnedCpuVerts[part.SkinnedMesh.Index] is not { } source) return false;
            _targetOutlines.EnqueueSkinnedCpu(source, skinned.Ib, skinned.IndexCount,
                skinned.IndexFormat, skinned.OutlineMaterialSet, part.Pose, drawIndex, ToRender(part.World),
                skinned.AlphaCutoff, part.Dissolve, part.DissolveComplement, _frameOrigin);
        }
        return true;
    }

    internal int OutlinePoseMatrixCount => _outlineBoneMatrices.Count;
    internal Matrix4x4 OutlinePoseMatrixAt(int index) => _outlineBoneMatrices[index];
}
