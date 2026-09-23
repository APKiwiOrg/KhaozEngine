using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    static int s_nextOutlineOwner;

    readonly int _outlineOwner = Interlocked.Increment(ref s_nextOutlineOwner);
    readonly List<MeshOutlineDrawGroup> _meshOutlineGroups = new();
    int _outlineFrame;
    int _meshOutlinePartCount;

    /// <summary>
    /// Starts one frame-local target outline group. Every mesh part added to the returned handle forms one
    /// projected union with <paramref name="color"/> and a width measured in final framebuffer pixels. Scene depth
    /// hides the border, as <see cref="MeshOutlineOcclusion.SceneDepth"/> describes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="widthPixels"/> is not finite or is outside the supported 0.5 to 8 pixel range.
    /// </exception>
    public MeshOutlineGroup BeginMeshOutline(Color color, float widthPixels)
        => BeginMeshOutline(color, widthPixels, MeshOutlineOcclusion.SceneDepth);

    /// <summary>
    /// Starts one frame-local target outline group whose border <paramref name="occlusion"/> may hide. Every mesh
    /// part added to the returned handle forms one projected union with <paramref name="color"/> and a width
    /// measured in final framebuffer pixels.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="widthPixels"/> is not finite or is outside the supported 0.5 to 8 pixel range, or
    /// <paramref name="occlusion"/> is not a defined <see cref="MeshOutlineOcclusion"/>.
    /// </exception>
    public MeshOutlineGroup BeginMeshOutline(Color color, float widthPixels, MeshOutlineOcclusion occlusion)
    {
        if (!float.IsFinite(widthPixels) || widthPixels < 0.5f || widthPixels > 8f)
            throw new ArgumentOutOfRangeException(nameof(widthPixels), widthPixels,
                "outline width must be finite and between 0.5 and 8 pixels.");
        if (!Enum.IsDefined(occlusion))
            throw new ArgumentOutOfRangeException(nameof(occlusion), occlusion, "unknown outline occlusion.");

        int index = _meshOutlineGroups.Count;
        _meshOutlineGroups.Add(new MeshOutlineDrawGroup(color, widthPixels, occlusion));
        return new MeshOutlineGroup(_outlineOwner, _outlineFrame, index);
    }

    /// <summary>Adds one mesh part to a group returned by <see cref="BeginMeshOutline(Color, float, MeshOutlineOcclusion)"/> this frame.</summary>
    public void DrawMeshOutline(MeshOutlineGroup group, MeshHandle mesh, Matrix4x4 world)
        => AddMeshOutlinePart(group, mesh, world, 0f, false);

    public void DrawMeshOutlineDissolved(MeshOutlineGroup group, MeshHandle mesh, Matrix4x4 world,
        float dissolve, bool dissolveComplement)
    {
        AddMeshOutlinePart(group, mesh, world, dissolve, dissolveComplement);
    }

    void AddMeshOutlinePart(MeshOutlineGroup group, MeshHandle mesh, Matrix4x4 world,
        float dissolve, bool dissolveComplement)
    {
        MeshOutlineDrawGroup drawGroup = RequireMeshOutlineGroup(group);
        drawGroup.Parts.Add(MeshOutlinePart.Rigid(mesh, world,
            Math.Clamp(dissolve, 0f, 1f), dissolveComplement));
        _meshOutlinePartCount++;
    }

    MeshOutlineDrawGroup RequireMeshOutlineGroup(MeshOutlineGroup group)
    {
        if (group.Owner != _outlineOwner || group.Frame != _outlineFrame
            || group.Index < 0 || group.Index >= _meshOutlineGroups.Count)
            throw new ArgumentException("outline group does not belong to this scene and frame.", nameof(group));
        return _meshOutlineGroups[group.Index];
    }

    /// <summary>Queues a one-part target outline with a width measured in final framebuffer pixels.</summary>
    public void DrawMeshOutline(MeshHandle mesh, Matrix4x4 world, Color color, float widthPixels)
        => DrawMeshOutline(mesh, world, color, widthPixels, MeshOutlineOcclusion.SceneDepth);

    /// <summary>Queues a one-part target outline whose border <paramref name="occlusion"/> may hide.</summary>
    public void DrawMeshOutline(MeshHandle mesh, Matrix4x4 world, Color color, float widthPixels,
        MeshOutlineOcclusion occlusion)
    {
        MeshOutlineGroup group = BeginMeshOutline(color, widthPixels, occlusion);
        DrawMeshOutline(group, mesh, world);
    }

    internal int MeshOutlineGroupCount => _meshOutlineGroups.Count;
    internal int MeshOutlinePartCount => _meshOutlinePartCount;
    internal MeshOutlineOcclusion MeshOutlineOcclusionAt(int groupIndex) => _meshOutlineGroups[groupIndex].Occlusion;
    internal float MeshOutlineDissolveAt(int groupIndex, int partIndex) =>
        _meshOutlineGroups[groupIndex].Parts[partIndex].Dissolve;

    void BeginMeshOutlineFrame()
    {
        _outlineFrame = _outlineFrame == int.MaxValue ? 1 : _outlineFrame + 1;
        _meshOutlineGroups.Clear();
        _outlineBoneMatrices.Clear();
        _meshOutlinePartCount = 0;
    }

    sealed class MeshOutlineDrawGroup
    {
        public Color Color { get; }
        public float WidthPixels { get; }
        public MeshOutlineOcclusion Occlusion { get; }
        public List<MeshOutlinePart> Parts { get; } = new();

        public MeshOutlineDrawGroup(Color color, float widthPixels, MeshOutlineOcclusion occlusion)
        {
            Color = color;
            WidthPixels = widthPixels;
            Occlusion = occlusion;
        }
    }

    enum MeshOutlinePartKind
    {
        Rigid,
        Skinned,
    }

    readonly record struct MeshOutlinePart(
        MeshOutlinePartKind Kind,
        MeshHandle Mesh,
        SkinnedMeshHandle SkinnedMesh,
        Matrix4x4 World,
        float Dissolve,
        bool DissolveComplement,
        int BoneStart,
        int BoneCount)
    {
        public static MeshOutlinePart Rigid(
            MeshHandle mesh, Matrix4x4 world, float dissolve, bool complement) =>
            new(MeshOutlinePartKind.Rigid, mesh, default, world, dissolve, complement, 0, 0);

        public static MeshOutlinePart Skinned(
            SkinnedMeshHandle mesh, Matrix4x4 world, float dissolve, bool complement,
            int boneStart, int boneCount) =>
            new(MeshOutlinePartKind.Skinned, default, mesh, world, dissolve, complement,
                boneStart, boneCount);
    }

    void DrawTargetOutlines(IGpuCommandList cl, Matrix4x4 viewProjection, IGpuFramebuffer target)
    {
        if (_meshOutlinePartCount == 0) return;
        Matrix4x4 clipViewProjection = GpuClip.Correct(viewProjection, _gd.Capabilities);
        _targetOutlines.EnsureCapacity(_meshOutlinePartCount);
        int drawIndex = 0;
        for (int groupIndex = 0; groupIndex < _meshOutlineGroups.Count; groupIndex++)
        {
            MeshOutlineDrawGroup group = _meshOutlineGroups[groupIndex];
            if (group.Parts.Count == 0) continue;
            _targetOutlines.BeginGroup(clipViewProjection);
            int groupDraws = 0;
            foreach (MeshOutlinePart part in group.Parts)
            {
                if ((!part.DissolveComplement && part.Dissolve >= 1f)
                    || (part.DissolveComplement && part.Dissolve <= 0f)) continue;
                if (!_slots.IsValid(part.Mesh.Index, part.Mesh.Generation)) continue;
                if (_meshes[part.Mesh.Index] is not { } mesh) continue;
                _targetOutlines.Enqueue(mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat,
                    mesh.OutlineMaterialSet, drawIndex++, ToRender(part.World), mesh.AlphaCutoff,
                    part.Dissolve, part.DissolveComplement, _frameOrigin);
                groupDraws++;
            }
            bool occluded = group.Occlusion == MeshOutlineOcclusion.SceneDepth;
            _targetOutlines.Render(cl, _res, target, group.Color, group.WidthPixels, Post.BackgroundColor.R,
                Post.Pixelated, groupIndex, occluded);
            _frameStats.DrawCalls += groupDraws * (occluded ? 2 : 1) + 1;
        }
    }
}
