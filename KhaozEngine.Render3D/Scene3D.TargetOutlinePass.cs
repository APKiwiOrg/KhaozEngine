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
    /// projected union with <paramref name="color"/> and a width measured in final framebuffer pixels.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="widthPixels"/> is not finite or is outside the supported 0.5 to 8 pixel range.
    /// </exception>
    public MeshOutlineGroup BeginMeshOutline(Color color, float widthPixels)
    {
        if (!float.IsFinite(widthPixels) || widthPixels < 0.5f || widthPixels > 8f)
            throw new ArgumentOutOfRangeException(nameof(widthPixels), widthPixels,
                "outline width must be finite and between 0.5 and 8 pixels.");

        int index = _meshOutlineGroups.Count;
        _meshOutlineGroups.Add(new MeshOutlineDrawGroup(color, widthPixels));
        return new MeshOutlineGroup(_outlineOwner, _outlineFrame, index);
    }

    /// <summary>Adds one mesh part to a group returned by <see cref="BeginMeshOutline"/> this frame.</summary>
    public void DrawMeshOutline(MeshOutlineGroup group, MeshHandle mesh, Matrix4x4 world)
    {
        if (group.Owner != _outlineOwner || group.Frame != _outlineFrame
            || group.Index < 0 || group.Index >= _meshOutlineGroups.Count)
            throw new ArgumentException("outline group does not belong to this scene and frame.", nameof(group));

        _meshOutlineGroups[group.Index].Parts.Add(new MeshOutlinePart(mesh, world));
        _meshOutlinePartCount++;
    }

    /// <summary>Queues a one-part target outline with a width measured in final framebuffer pixels.</summary>
    public void DrawMeshOutline(MeshHandle mesh, Matrix4x4 world, Color color, float widthPixels)
    {
        MeshOutlineGroup group = BeginMeshOutline(color, widthPixels);
        DrawMeshOutline(group, mesh, world);
    }

    internal int MeshOutlineGroupCount => _meshOutlineGroups.Count;
    internal int MeshOutlinePartCount => _meshOutlinePartCount;

    void BeginMeshOutlineFrame()
    {
        _outlineFrame = _outlineFrame == int.MaxValue ? 1 : _outlineFrame + 1;
        _meshOutlineGroups.Clear();
        _meshOutlinePartCount = 0;
    }

    sealed class MeshOutlineDrawGroup
    {
        public Color Color { get; }
        public float WidthPixels { get; }
        public List<MeshOutlinePart> Parts { get; } = new();

        public MeshOutlineDrawGroup(Color color, float widthPixels)
        {
            Color = color;
            WidthPixels = widthPixels;
        }
    }

    readonly record struct MeshOutlinePart(MeshHandle Mesh, Matrix4x4 World);

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
                if (!_slots.IsValid(part.Mesh.Index, part.Mesh.Generation)) continue;
                if (_meshes[part.Mesh.Index] is not { } mesh) continue;
                _targetOutlines.Enqueue(mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat,
                    mesh.OutlineMaterialSet, drawIndex++, ToRender(part.World), mesh.AlphaCutoff);
                groupDraws++;
            }
            _targetOutlines.Render(cl, _res, target, group.Color, group.WidthPixels, Post.BackgroundColor.R,
                groupIndex);
            _frameStats.DrawCalls += groupDraws * 2 + 1;
        }
    }
}
