using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class TargetOutlineRenderer
{
    CoveragePulledResources _coveragePull = null!;
    IGpuShaderSet _coverageFullShaders = null!, _coverageVisibleShaders = null!;
    IGpuPipeline? _coverageFullPipeline, _coverageVisiblePipeline;
    IGpuResourceSet?[] _coverageMaterials = [];
    internal Action<string, CoveragePulledDraw>? CoveragePullObserverForTests { get; set; }

    internal void ReserveCoverage(CoverageGpuBatch batch, int count)
    {
        if (_coveragePull is null)
        {
            _coveragePull = InitializeCoveragePulling(_gd.Factory);
            BuildCoverageMaskPipelines(_gd.Factory);
        }
        _coveragePull.Reserve(batch, count);
        if (_coverageMaterials.Length < count) Array.Resize(ref _coverageMaterials, count);
    }

    internal void SetCoverageMaterial(int drawIndex, IGpuResourceSet? material) => _coverageMaterials[drawIndex] = material;

    internal void EnqueueCoverage(in CoveragePulledDraw draw, int drawIndex, Matrix4x4 world,
        float alphaCutoff, float dissolve, bool complement, Vector3 origin)
    {
        WriteDrawPayload(drawIndex, world, alphaCutoff, dissolve, complement, origin, false);
        _coveragePull.Pack((uint)drawIndex, draw);
        _queue.Add(new(QueuedGeometry.Coverage, null, null!, draw.CornerCount, GpuIndexFormat.UInt32,
            _coverageMaterials[drawIndex] ?? _defaultMaterialSet, drawIndex, -1, 0, 0, 0, draw));
    }

    CoveragePulledResources InitializeCoveragePulling(IGpuResourceFactory factory)
    {
        var resources = new CoveragePulledResources(_gd, _retired.Add);
        _coverageFullShaders = factory.CreateShadersFromSpirv(ShaderSources.CoverageOutlineVert,
            ShaderSources.TargetOutlineFullMaskFrag, new(PreciseMath: true));
        _coverageVisibleShaders = factory.CreateShadersFromSpirv(ShaderSources.CoverageOutlineVert,
            ShaderSources.TargetOutlineVisibleMaskFrag, new(PreciseMath: true));
        return resources;
    }

    void UploadCoverageMasks(IGpuCommandList commands)
    {
        if (_queue.Exists(static draw => draw.Geometry == QueuedGeometry.Coverage)) _coveragePull.Upload(commands);
    }

    void BuildCoverageMaskPipelines(IGpuResourceFactory factory)
    {
        if (_coveragePull is null || _fullFramebuffer is null || _visibleFramebuffer is null) return;
        _coverageFullPipeline = BuildCoverageMaskPipeline(factory, _coverageFullShaders, _fullFramebuffer.Outputs, true);
        _coverageVisiblePipeline = BuildCoverageMaskPipeline(factory, _coverageVisibleShaders, _visibleFramebuffer.Outputs, false);
    }

    IGpuPipeline BuildCoverageMaskPipeline(IGpuResourceFactory factory, IGpuShaderSet shaders,
        GpuOutputDescription outputs, bool full) => factory.CreateGraphicsPipeline(new()
        {
            BlendAttachments = full ? [GpuBlendAttachment.OverrideBlend]
                : [GpuBlendAttachment.OverrideBlend, GpuBlendAttachment.OverrideBlend],
            DepthStencil = full ? GpuDepthStencilState.DepthOnlyLessEqual : GpuDepthStencilState.DepthTestLessEqualNoWrite,
            Rasterizer = new(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, true, false),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = [_drawLayout, _materialLayout, _coveragePull.Layout],
            ShaderSet = shaders,
            VertexLayouts = [],
            Outputs = outputs,
        });

    void DrawCoverageMask(IGpuCommandList commands, in QueuedDraw draw, bool full)
    {
        commands.SetPipeline(full ? _coverageFullPipeline! : _coverageVisiblePipeline!);
        commands.SetGraphicsResourceSet(0, _drawSet!, (uint)draw.DrawIndex * DrawSlotBytes);
        commands.SetGraphicsResourceSet(1, draw.MaterialSet);
        _coveragePull.Bind(commands, 2, (uint)draw.DrawIndex);
        CoveragePullObserverForTests?.Invoke(full ? "full" : "visible", draw.Coverage);
        commands.Draw((uint)draw.IndexCount, 1, 0, 0);
    }

    void DisposeCoveragePulling()
    { _coverageFullShaders?.Dispose(); _coverageVisibleShaders?.Dispose(); _coveragePull?.Dispose(); }

    internal (IGpuTexture Full, IGpuTexture Visible) CoverageTargetsForTests =>
        (_fullCoverage ?? throw new System.InvalidOperationException("No full outline target."),
         _visibleCoverage ?? throw new System.InvalidOperationException("No visible outline target."));

    internal void EnqueuePrepared(IGpuBuffer vertices, IGpuBuffer indices, int indexCount,
        GpuIndexFormat format, IGpuResourceSet? material, int drawIndex, Matrix4x4 world,
        float alphaCutoff, float dissolve, bool complement, Vector3 origin, int baseVertex)
    {
        WriteDrawPayload(drawIndex, world, alphaCutoff, dissolve, complement, origin, false);
        _queue.Add(new QueuedDraw(QueuedGeometry.Rigid, vertices, indices, indexCount, format,
            material ?? _defaultMaterialSet, drawIndex, -1, baseVertex, 0, 0));
    }
}
