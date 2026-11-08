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
    bool _coverageRefused;
    internal void RefuseCoverage() => _coverageRefused = true;
    internal Action<string, CoverageDrawEvidence>? CoveragePullObserverForTests { get; set; }

    internal void ReserveCoverage(int count)
    {
        if (_coverageRefused)
            throw new GpuShaderPolicyUnsupportedException(GpuShaderPolicyRefusal.PreciseMathUnavailable, GpuShaderStages.Vertex);
        if (_coveragePull is null)
        {
            var resources = new CoveragePulledResources(_gd, _retired.Add);
            IGpuShaderSet? full = null, visible = null;
            IGpuPipeline? fullPipeline = null, visiblePipeline = null;
            try
            {
                var factory = _gd.Factory;
                full = factory.CreateShadersFromSpirv(ShaderSources.CoverageOutlineVert,
                    ShaderSources.TargetOutlineFullMaskFrag, new(PreciseMath: true));
                visible = factory.CreateShadersFromSpirv(ShaderSources.CoverageOutlineVert,
                    ShaderSources.TargetOutlineVisibleMaskFrag, new(PreciseMath: true));
                if (_fullFramebuffer is not null && _visibleFramebuffer is not null)
                {
                    fullPipeline = BuildCoverageMaskPipeline(factory, full, _fullFramebuffer.Outputs, true, resources.Layout);
                    visiblePipeline = BuildCoverageMaskPipeline(factory, visible, _visibleFramebuffer.Outputs, false, resources.Layout);
                }
                resources.Reserve(count);
            }
            catch
            {
                fullPipeline?.Dispose(); visiblePipeline?.Dispose();
                full?.Dispose(); visible?.Dispose(); resources.Dispose();
                throw;
            }
            _coveragePull = resources; _coverageFullShaders = full; _coverageVisibleShaders = visible;
            _coverageFullPipeline = fullPipeline; _coverageVisiblePipeline = visiblePipeline;
        }
        _coveragePull.Reserve(count);
        if (_coverageMaterials.Length < count) Array.Resize(ref _coverageMaterials, count);
    }

    internal void SetCoverageMaterial(int drawIndex, IGpuResourceSet? material) => _coverageMaterials[drawIndex] = material;

    internal void EnqueueCoverage(in CoveragePulledDraw draw, int drawIndex, Matrix4x4 world,
        float alphaCutoff, float dissolve, bool complement, Vector3 origin)
    {
        WriteDrawPayload(drawIndex, world, alphaCutoff, dissolve, complement, origin, false);
        _coveragePull.Pack(draw.BindingSlot, draw);
        _queue.Add(new(QueuedGeometry.Coverage, null, null!, draw.CornerCount, GpuIndexFormat.UInt32,
            _coverageMaterials[drawIndex] ?? _defaultMaterialSet, drawIndex, -1, 0, 0, 0, draw));
    }

    void UploadCoverageMasks(IGpuCommandList commands)
    {
        if (_queue.Exists(static draw => draw.Geometry == QueuedGeometry.Coverage)) _coveragePull.Upload(commands);
    }

    void BuildCoverageMaskPipelines(IGpuResourceFactory factory)
    {
        if (_coverageRefused || _coveragePull is null || _fullFramebuffer is null || _visibleFramebuffer is null) return;
        _coverageFullPipeline = BuildCoverageMaskPipeline(factory, _coverageFullShaders, _fullFramebuffer.Outputs, true, _coveragePull.Layout);
        _coverageVisiblePipeline = BuildCoverageMaskPipeline(factory, _coverageVisibleShaders, _visibleFramebuffer.Outputs, false, _coveragePull.Layout);
    }

    IGpuPipeline BuildCoverageMaskPipeline(IGpuResourceFactory factory, IGpuShaderSet shaders,
        GpuOutputDescription outputs, bool full, IGpuResourceLayout pullingLayout) => factory.CreateGraphicsPipeline(new()
        {
            BlendAttachments = full ? [GpuBlendAttachment.OverrideBlend]
                : [GpuBlendAttachment.OverrideBlend, GpuBlendAttachment.OverrideBlend],
            DepthStencil = full ? GpuDepthStencilState.DepthOnlyLessEqual : GpuDepthStencilState.DepthTestLessEqualNoWrite,
            Rasterizer = new(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, true, false),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = [_drawLayout, _materialLayout, pullingLayout],
            ShaderSet = shaders,
            VertexLayouts = [],
            Outputs = outputs,
        });

    void DrawCoverageMask(IGpuCommandList commands, in QueuedDraw draw, bool full)
    {
        commands.SetPipeline(full ? _coverageFullPipeline! : _coverageVisiblePipeline!);
        commands.SetGraphicsResourceSet(0, _drawSet!, (uint)draw.DrawIndex * DrawSlotBytes);
        commands.SetGraphicsResourceSet(1, draw.MaterialSet);
        _coveragePull.Bind(commands, 2, draw.Coverage.BindingSlot);
        commands.Draw((uint)draw.IndexCount, 1, 0, 0);
        CoveragePullObserverForTests?.Invoke(full ? "full" : "visible", new(draw.Coverage));
    }

    internal void ReserveCoverageSurface(int slot, in CoverageSurfaceRange surface) => _coveragePull.ReserveSurface(slot, surface);

    void DisposeCoveragePulling()
    { _coverageFullShaders?.Dispose(); _coverageVisibleShaders?.Dispose(); _coveragePull?.Dispose(); }

}
