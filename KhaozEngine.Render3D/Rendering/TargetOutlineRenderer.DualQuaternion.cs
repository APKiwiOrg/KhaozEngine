using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>Independent DQ outline poses and lazy full/visible mask variants. Neither execution path
/// borrows a colour draw, converts a prepared palette again or reinterprets a matrix buffer.</summary>
internal sealed partial class TargetOutlineRenderer
{
    readonly List<RigidDualQuaternion> _gpuDqPaletteBones = new();
    int _nextDqPaletteSlot;
    int _groupDqPaletteSlotStart;
    readonly IGpuShaderSet?[] _dqMaskShaders = new IGpuShaderSet[2];
    readonly IGpuPipeline?[] _dqMaskPipelines = new IGpuPipeline[2];

    internal void EnqueueSkinnedGpu(IGpuBuffer restVertexBuffer, IGpuBuffer indexBuffer, int indexCount,
        GpuIndexFormat indexFormat, IGpuResourceSet? materialSet, in PreparedSkinningPose pose,
        int drawIndex, Matrix4x4 world, float alphaCutoff, float dissolve, bool dissolveComplement,
        Vector3 renderOrigin)
    {
        if (pose.Profile.Method == SkinningMethod.LinearBlend)
        {
            EnqueueSkinnedGpu(restVertexBuffer, indexBuffer, indexCount, indexFormat, materialSet,
                pose.Owner.Lbs(pose), drawIndex, world, alphaCutoff, dissolve, dissolveComplement, renderOrigin);
            return;
        }
        ReadOnlySpan<RigidDualQuaternion> bones = pose.Owner.Dq(pose);
        int start = _gpuDqPaletteBones.Count;
        foreach (RigidDualQuaternion bone in bones) _gpuDqPaletteBones.Add(bone);
        WriteDrawPayload(drawIndex, world, alphaCutoff, dissolve, dissolveComplement, renderOrigin, false);
        _queue.Add(new QueuedDraw(QueuedGeometry.DqGpuSkinned, restVertexBuffer, indexBuffer, indexCount,
            indexFormat, materialSet ?? _defaultMaterialSet, drawIndex, _nextDqPaletteSlot++, 0, start, bones.Length));
    }

    internal void EnqueueSkinnedCpu(ReadOnlySpan<SkinnedVertex> sourceVertices, IGpuBuffer indexBuffer,
        int indexCount, GpuIndexFormat indexFormat, IGpuResourceSet? materialSet, in PreparedSkinningPose pose,
        int drawIndex, Matrix4x4 world, float alphaCutoff, float dissolve, bool dissolveComplement,
        Vector3 renderOrigin)
    {
        if (pose.Profile.Method == SkinningMethod.LinearBlend)
        {
            EnqueueSkinnedCpu(sourceVertices, indexBuffer, indexCount, indexFormat, materialSet,
                pose.Owner.Lbs(pose), drawIndex, world, alphaCutoff, dissolve, dissolveComplement, renderOrigin);
            return;
        }
        ReadOnlySpan<RigidDualQuaternion> bones = pose.Owner.Dq(pose);
        int baseVertex = _cpuVertices.Count;
        foreach (SkinnedVertex vertex in sourceVertices)
            _cpuVertices.Add(SkinningEvaluation.Vertex(vertex, pose.Profile, default, bones));
        WriteDrawPayload(drawIndex, world, alphaCutoff, dissolve, dissolveComplement, renderOrigin, false);
        _queue.Add(new QueuedDraw(QueuedGeometry.CpuSkinned, null, indexBuffer, indexCount,
            indexFormat, materialSet ?? _defaultMaterialSet, drawIndex, -1, baseVertex, 0, 0));
    }

    void PrepareDqPalette(IGpuCommandList commands)
    {
        if (_nextDqPaletteSlot == _groupDqPaletteSlotStart) return;
        _skinning.EnsurePaletteCapacity(_nextDqPaletteSlot, SkinningMethod.DualQuaternionRigid);
        Span<RigidDualQuaternion> bones = CollectionsMarshal.AsSpan(_gpuDqPaletteBones);
        foreach (QueuedDraw draw in _queue)
            if (draw.Geometry == QueuedGeometry.DqGpuSkinned)
                _skinning.PackPalette(draw.PaletteSlot, bones.Slice(draw.PoseStart, draw.PoseCount));
        _skinning.UploadDqPalette(commands);
    }

    IGpuPipeline DqMaskPipeline(bool full)
    {
        int variant = full ? 0 : 1;
        if (_dqMaskPipelines[variant] is { } pipeline) return pipeline;
        IGpuShaderSet shaders = _dqMaskShaders[variant] ??= _gd.Factory.CreateShadersFromSpirv(
            ShaderSources.DqTargetOutlineSkinnedMaskVert,
            full ? ShaderSources.TargetOutlineFullMaskFrag : ShaderSources.TargetOutlineVisibleMaskFrag);
        return _dqMaskPipelines[variant] = BuildSkinnedMaskPipeline(_gd.Factory, shaders,
            full ? _fullFramebuffer!.Outputs : _visibleFramebuffer!.Outputs, full,
            _skinning.PaletteLayoutFor(SkinningMethod.DualQuaternionRigid));
    }

    void DisposeDqMaskPipelines()
    {
        for (int i = 0; i < _dqMaskPipelines.Length; i++)
        {
            _dqMaskPipelines[i]?.Dispose();
            _dqMaskPipelines[i] = null;
        }
    }
}
