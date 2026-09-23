using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;

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
        MeshOutlineGroup group = BeginMeshOutline(color, widthPixels, occlusion);
        DrawSkinnedOutline(group, mesh, boneMatrices, world);
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

        (int start, int count) = AppendOutlinePose(_outlineBoneMatrices, boneMatrices, entry.InverseBind);
        drawGroup.Parts.Add(MeshOutlinePart.Skinned(mesh, world, Math.Clamp(dissolve, 0f, 1f),
            dissolveComplement, start, count));
        _meshOutlinePartCount++;
    }

    static (int Start, int Count) AppendOutlinePose(
        List<Matrix4x4> destination,
        ReadOnlySpan<Matrix4x4> boneMatrices,
        Matrix4x4[] inverseBind)
    {
        if (boneMatrices.Length != inverseBind.Length)
            throw new ArgumentException(
                $"boneMatrices length {boneMatrices.Length} must equal the mesh bone count {inverseBind.Length}.",
                nameof(boneMatrices));
        if (boneMatrices.Length > SkinningMath.MaxBonesPerDraw)
            throw new ArgumentException(
                $"a skinned mesh has {boneMatrices.Length} bones, over the {SkinningMath.MaxBonesPerDraw}-bone per-draw cap.",
                nameof(boneMatrices));

        int start = destination.Count;
        for (int i = 0; i < boneMatrices.Length; i++)
            destination.Add(SkinningMath.Compose(boneMatrices[i], inverseBind[i]));
        return (start, boneMatrices.Length);
    }

    internal int OutlinePoseMatrixCount => _outlineBoneMatrices.Count;
    internal Matrix4x4 OutlinePoseMatrixAt(int index) => _outlineBoneMatrices[index];
}
