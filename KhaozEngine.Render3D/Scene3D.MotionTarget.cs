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

    // One motion key per grouped rigid slot, filled by GroupInstances while the target is temporal.
    readonly List<MotionKey> _instanceMotionKeys = new();
    // One motion slot per grouped rigid slot, and the compact previous transforms they index. Grow-only.
    float[] _motionSlots = Array.Empty<float>();
    readonly List<Matrix4x4> _previousInstanceTransforms = new();

    /// <summary>The model renderer's temporal resources, null while the model framebuffer has no motion attachment.
    /// For tests.</summary>
    internal ModelMotionResources? MotionResourcesForTests => _model.Motion;

    /// <summary>The model framebuffer's sample count this frame. For tests.</summary>
    internal int ModelSampleCountForTests => _res.SampleCount;

    /// <summary>Whether any transparent pass that draws into the model framebuffer holds its temporal program. For
    /// tests.</summary>
    internal bool TransparentMotionShadersHeldForTests => _texBillboards.HoldsMotionShadersForTests
        || _beams.HoldsMotionShadersForTests || _trails.HoldsMotionShadersForTests
        || _overlayMeshes.HoldsMotionShadersForTests || _silhouettes.HoldsMotionShadersForTests;

    /// <summary>Upload this frame's motion block and the rigid motion slots, before the model pass. The block carries
    /// this frame's unjittered view-projection and last frame's, which group B has already rebased to this frame's
    /// origin, or this frame's again with the flag at zero when there is no valid history, which makes every variant
    /// write exactly zero. Both count toward the rigid instance stream's upload.</summary>
    internal void PrepareMotionFrame(IGpuCommandList cl)
    {
        if (!_res.MotionAllocated) return;
        FrameView current = CurrentFrameView;
        FrameView? previous = PreviousFrameView;
        _frameStats.AddInstanceUpload(_model.UploadMotionFrame(cl, new MotionFrameUbo
        {
            CurViewProj = current.ViewProjection,
            PrevViewProj = previous?.ViewProjection ?? current.ViewProjection,
            Params = new Vector4(previous is null ? 0f : 1f, 0f, 0f, 0f),
        }));

        int count = _instanceData.Count;
        if (count == 0) return;
        if (_instanceMotionKeys.Count != count)
            throw new InvalidOperationException("The motion keys were not grouped with this frame's instances.");
        if (_motionSlots.Length < count) _motionSlots = new float[Math.Max(count, _motionSlots.Length * 2)];
        Span<float> slots = _motionSlots.AsSpan(0, count);
        RigidMotionSlots.Build(CollectionsMarshal.AsSpan(_instanceMotionKeys), PreviousMotion, current.RenderOrigin,
            slots, _previousInstanceTransforms);
        _frameStats.AddInstanceUpload(
            _model.UploadRigidMotion(cl, slots, CollectionsMarshal.AsSpan(_previousInstanceTransforms)));
    }

    /// <summary>Read the motion target back: one UV motion per internal pixel, row 0 at the top, the sentinel where no
    /// opaque geometry drew. Drains the device. For tests.</summary>
    /// <exception cref="InvalidOperationException">No motion target exists, because temporal rendering was off for the
    /// last rendered frame.</exception>
    internal MotionTargetReadback ReadMotionTargetForTests()
    {
        IGpuTexture target = _res.MotionTex ?? throw new InvalidOperationException(
            "No motion target is allocated. Temporal rendering must be active for the frame that is read.");
        int width = (int)target.Width, height = (int)target.Height;
        IGpuResourceFactory f = _gd.Factory;
        using IGpuTexture staging = f.CreateTexture(GpuTextureDescription.Texture2D(
            target.Width, target.Height, MotionMath.Format, GpuTextureUsage.Staging));
        using (IGpuCommandList cl = f.CreateCommandList())
        {
            using (GpuRecording.Open(_gd, cl, "Scene3D.ReadMotionTargetForTests")) cl.CopyTexture(target, staging);
            _gd.Submit(cl);
            _gd.WaitForIdle();
        }
        var motion = new Vector2[width * height];
        var map = _gd.Map(staging, GpuMapMode.Read);
        unsafe
        {
            byte* data = (byte*)map.Data;
            for (int y = 0; y < height; y++)
            {
                ushort* row = (ushort*)(data + y * (int)map.RowPitch);
                for (int x = 0; x < width; x++)
                    motion[y * width + x] = new Vector2((float)BitConverter.UInt16BitsToHalf(row[2 * x]),
                        (float)BitConverter.UInt16BitsToHalf(row[2 * x + 1]));
            }
        }
        _gd.Unmap(staging);
        return new MotionTargetReadback(motion, width, height);
    }

    /// <summary>Pack every GPU-skinned caster's last frame into its <c>SkinnedMotionPalette</c> slot and upload them in
    /// one write. A draw with no key, a key with no last frame, a previous palette of another length (the key moved to
    /// another mesh, group C amendment 1) or a frame with no valid history packs this frame's own, which is camera-only
    /// motion. The upload counts toward the GPU-skinning uniforms.</summary>
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
        _frameStats.AddSkinnedUniformUpload(_model.UploadSkinnedMotion(cl));
    }

    // Last frame's position of every deformed CPU-skinned vertex, parallel to _cpuSkinnedVerts. Grow-only.
    readonly List<Vector3> _cpuSkinnedPrevious = new();

    /// <summary>Upload this frame's CPU-skinned geometry, and while the target is temporal each vertex's last-frame
    /// position: re-skinned from the key's previous palette and world when it has a usable one, this frame's own
    /// otherwise. Both count toward the CPU-skinned stream's upload.</summary>
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
        System.Diagnostics.Debug.Assert(_cpuSkinnedPrevious.Count == _cpuSkinnedVerts.Count,
            "The CPU-skinned previous positions must stay parallel to the deformed vertices.");
        _frameStats.AddSkinnedUpload(_model.UploadCpuSkinnedPrevious(cl, CollectionsMarshal.AsSpan(_cpuSkinnedPrevious)));
    }
}
