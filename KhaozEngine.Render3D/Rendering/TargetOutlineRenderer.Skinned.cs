using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class TargetOutlineRenderer
{
    readonly IGpuShaderSet _skinnedFullShaders;
    readonly IGpuShaderSet _skinnedVisibleShaders;
    readonly TargetOutlineSkinningStore _skinning;
    readonly List<Matrix4x4> _gpuPaletteBones = new();
    readonly List<ModelVertex> _cpuVertices = new();
    int _nextGpuPaletteSlot;
    int _groupPaletteSlotStart;

    public void EnqueueSkinnedGpu(
        IGpuBuffer restVertexBuffer,
        IGpuBuffer indexBuffer,
        int indexCount,
        GpuIndexFormat indexFormat,
        IGpuResourceSet? materialSet,
        ReadOnlySpan<Matrix4x4> composedBones,
        int drawIndex,
        Matrix4x4 world,
        float alphaCutoff,
        float dissolve,
        bool dissolveComplement,
        Vector3 renderOrigin)
    {
        int poseStart = _gpuPaletteBones.Count;
        for (int i = 0; i < composedBones.Length; i++) _gpuPaletteBones.Add(composedBones[i]);
        int paletteSlot = _nextGpuPaletteSlot++;
        WriteDrawPayload(drawIndex, world, alphaCutoff, dissolve, dissolveComplement, renderOrigin);
        _queue.Add(new QueuedDraw(QueuedGeometry.GpuSkinned, restVertexBuffer, indexBuffer, indexCount,
            indexFormat, materialSet ?? _defaultMaterialSet, drawIndex, paletteSlot, 0,
            poseStart, composedBones.Length));
    }

    public void EnqueueSkinnedCpu(
        ReadOnlySpan<SkinnedVertex> sourceVertices,
        IGpuBuffer indexBuffer,
        int indexCount,
        GpuIndexFormat indexFormat,
        IGpuResourceSet? materialSet,
        ReadOnlySpan<Matrix4x4> composedBones,
        int drawIndex,
        Matrix4x4 world,
        float alphaCutoff,
        float dissolve,
        bool dissolveComplement,
        Vector3 renderOrigin)
    {
        int baseVertex = _cpuVertices.Count;
        for (int i = 0; i < sourceVertices.Length; i++)
            _cpuVertices.Add(SkinningMath.SkinVertex(sourceVertices[i], composedBones));
        WriteDrawPayload(drawIndex, world, alphaCutoff, dissolve, dissolveComplement, renderOrigin);
        _queue.Add(new QueuedDraw(QueuedGeometry.CpuSkinned, null, indexBuffer, indexCount,
            indexFormat, materialSet ?? _defaultMaterialSet, drawIndex, -1, baseVertex, 0, 0));
    }

    void BeginSkinnedFrame()
    {
        _nextGpuPaletteSlot = 0;
        _groupPaletteSlotStart = 0;
        _gpuPaletteBones.Clear();
        _cpuVertices.Clear();
    }

    void BeginSkinnedGroup()
    {
        _gpuPaletteBones.Clear();
        _cpuVertices.Clear();
        _groupPaletteSlotStart = _nextGpuPaletteSlot;
    }

    void PrepareGpuPalette(IGpuCommandList commands)
    {
        int groupPaletteSlotCount = _nextGpuPaletteSlot - _groupPaletteSlotStart;
        if (groupPaletteSlotCount > 0) _skinning.EnsurePaletteCapacity(_nextGpuPaletteSlot);
        IGpuBuffer? cpuVertexBuffer = _cpuVertices.Count > 0
            ? _skinning.EnsureCpuVertexCapacity(_cpuVertices.Count)
            : null;

        if (groupPaletteSlotCount > 0)
        {
            Span<Matrix4x4> bones = CollectionsMarshal.AsSpan(_gpuPaletteBones);
            foreach (QueuedDraw draw in _queue)
            {
                if (draw.Geometry != QueuedGeometry.GpuSkinned) continue;
                _skinning.PackPalette(draw.PaletteSlot, bones.Slice(draw.PoseStart, draw.PoseCount));
            }
            _skinning.UploadPalette(commands, _groupPaletteSlotStart, groupPaletteSlotCount);
        }

        if (cpuVertexBuffer is not null)
        {
            _skinning.UploadCpuVertices(commands, CollectionsMarshal.AsSpan(_cpuVertices));
            for (int i = 0; i < _queue.Count; i++)
            {
                QueuedDraw draw = _queue[i];
                if (draw.Geometry == QueuedGeometry.CpuSkinned)
                    _queue[i] = draw with { VertexBuffer = cpuVertexBuffer };
            }
        }

        foreach (QueuedDraw draw in _queue)
        {
            if (draw.Geometry == QueuedGeometry.CpuSkinned && draw.VertexBuffer is null)
                throw new InvalidOperationException(
                    "CPU-skinned outline vertices were not prepared before mask rendering.");
        }
    }

    void WriteDrawPayload(int drawIndex, Matrix4x4 world, float alphaCutoff,
        float dissolve, bool dissolveComplement, Vector3 renderOrigin)
    {
        var payload = new DrawUbo
        {
            ViewProj = _viewProj,
            World = world,
            Params = new Vector4(alphaCutoff, dissolve, dissolveComplement ? 1f : 0f, 0f),
            RenderOrigin = new Vector4(renderOrigin, 0f),
        };
        MemoryMarshal.Write(_drawImage.AsSpan(drawIndex * DrawSlotBytes, DrawSlotBytes), in payload);
    }

    IGpuPipeline BuildSkinnedMaskPipeline(IGpuResourceFactory factory, IGpuShaderSet shaders,
        GpuOutputDescription outputs, bool full)
    {
        var vertexLayout = new GpuVertexLayoutDescription(
            new GpuVertexElement("Position", GpuVertexElementFormat.Float3),
            new GpuVertexElement("Normal", GpuVertexElementFormat.Float3),
            new GpuVertexElement("Color", GpuVertexElementFormat.Float4),
            new GpuVertexElement("TexCoord", GpuVertexElementFormat.Float2),
            new GpuVertexElement("BoneIndices", GpuVertexElementFormat.Float4),
            new GpuVertexElement("BoneWeights", GpuVertexElementFormat.Float4),
            new GpuVertexElement("Tangent", GpuVertexElementFormat.Float4));
        return factory.CreateGraphicsPipeline(new GpuPipelineDescription
        {
            BlendFactor = Vector4.Zero,
            BlendAttachments = full
                ? new[] { GpuBlendAttachment.OverrideBlend }
                : new[] { GpuBlendAttachment.OverrideBlend, GpuBlendAttachment.OverrideBlend },
            DepthStencil = full ? GpuDepthStencilState.DepthOnlyLessEqual
                : GpuDepthStencilState.DepthTestLessEqualNoWrite,
            Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid,
                GpuFrontFace.Clockwise, depthClipEnabled: true, scissorTestEnabled: false),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = new[] { _drawLayout, _materialLayout, _skinning.PaletteLayout },
            ShaderSet = shaders,
            VertexLayouts = new List<GpuVertexLayoutDescription> { vertexLayout },
            Outputs = outputs,
        });
    }
}
