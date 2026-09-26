using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

/// <summary>
/// The screen-space motion target's per-frame work (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24 sections 3 and 4): the
/// previous state each opaque path reads, and the seams the tests read it through. Nothing here runs while the model
/// framebuffer has no motion attachment.
/// </summary>
public sealed partial class Scene3D
{
    /// <summary>The key of this frame's GPU-skinned draw record <paramref name="draw"/>. For tests.</summary>
    internal MotionKey GpuSkinnedMotionForTests(int draw) => _gpuSkinnedDraws[draw].Motion;

    /// <summary>The key, mesh slot and vertex count of this frame's CPU-skinned draw record <paramref name="draw"/>.
    /// For tests.</summary>
    internal (MotionKey Motion, int MeshIndex, int VertexCount) CpuSkinnedMotionForTests(int draw)
    {
        CpuSkinnedDraw record = _cpuSkinnedDraws[draw];
        return (record.Motion, record.MeshIndex, record.VertexCount);
    }

    /// <summary>Whether this frame has a previous frame to measure motion against. Group B's
    /// <see cref="PreviousFrameView"/> is null on the first temporal frame and after every reset, and every temporal
    /// variant then writes exactly zero.</summary>
    bool MotionHistoryValid => PreviousFrameView is not null;

    /// <summary>The history previous object state is read from this frame, or null when there is none to read.</summary>
    MotionHistory? PreviousMotion => MotionHistoryValid ? ActiveMotionHistory : null;

    /// <summary>Pack every GPU-skinned caster's last frame into its <c>SkinnedMotionPalette</c> slot and upload them in
    /// one write. A draw with no key, a key with no last frame, a previous palette of another length (the key moved to
    /// another mesh, group C amendment 1) or a frame with no valid history packs this frame's own, which is camera-only
    /// motion.</summary>
    void PrepareGpuSkinnedMotion(IGpuCommandList cl, ReadOnlySpan<Matrix4x4> boneSpan)
    {
        _model.EnsureSkinnedMotionCapacity((uint)_gpuSkinnedDraws.Count);
        MotionHistory? history = PreviousMotion;
        for (int d = 0; d < _gpuSkinnedDraws.Count; d++)
        {
            GpuSkinnedDraw dr = _gpuSkinnedDraws[d];
            if (history is not null && !dr.Motion.IsNone
                && history.TryGetPreviousSkinned(dr.Motion, out Matrix4x4 world, out ReadOnlySpan<Matrix4x4> palette)
                && palette.Length == dr.BoneCount)
                _model.PackSkinnedMotion(dr.Slot, ToRender(world), palette);
            else
                _model.PackSkinnedMotion(dr.Slot, dr.World, boneSpan.Slice(dr.BoneSpanStart, dr.BoneCount));
        }
        _model.UploadSkinnedMotion(cl);
    }

    // Last frame's position of every deformed CPU-skinned vertex, parallel to _cpuSkinnedVerts. Grow-only.
    readonly List<Vector3> _cpuSkinnedPrevious = new();

    /// <summary>Upload this frame's CPU-skinned geometry, and while the target is temporal each vertex's last-frame
    /// position: re-skinned from the key's previous palette and world when it has a usable one, this frame's own
    /// otherwise.</summary>
    void UploadCpuSkinnedFrame(IGpuCommandList cl)
    {
        _model.UploadCpuSkinned(cl, CollectionsMarshal.AsSpan(_cpuSkinnedVerts), CollectionsMarshal.AsSpan(_cpuSkinnedInstances));
        _frameStats.AddSkinnedUpload((long)_cpuSkinnedVerts.Count * Unsafe.SizeOf<ModelVertex>()
            + (long)_cpuSkinnedInstances.Count * Unsafe.SizeOf<ModelRenderer.InstanceData>());
        if (!_res.MotionAllocated) return;

        _cpuSkinnedPrevious.Clear();
        MotionHistory? history = PreviousMotion;
        ReadOnlySpan<ModelVertex> skinned = CollectionsMarshal.AsSpan(_cpuSkinnedVerts);
        for (int d = 0; d < _cpuSkinnedDraws.Count; d++)
        {
            CpuSkinnedDraw dr = _cpuSkinnedDraws[d];
            SkinnedVertex[]? source = _skinnedCpuVerts[dr.MeshIndex];
            int bones = _skinnedMeshes[dr.MeshIndex]?.InverseBind.Length ?? -1;
            if (history is not null && source is not null && !dr.Motion.IsNone
                && history.TryGetPreviousSkinned(dr.Motion, out Matrix4x4 world, out ReadOnlySpan<Matrix4x4> palette)
                && palette.Length == bones)
                CpuSkinnedMotion.AppendPrevious(source, palette, ToRender(world), _cpuSkinnedPrevious);
            else
                CpuSkinnedMotion.AppendCurrent(skinned.Slice(dr.BaseVertex, dr.VertexCount), _cpuSkinnedInstances[d].Model,
                    _cpuSkinnedPrevious);
        }
        _model.UploadCpuSkinnedPrevious(cl, CollectionsMarshal.AsSpan(_cpuSkinnedPrevious));
    }
}
