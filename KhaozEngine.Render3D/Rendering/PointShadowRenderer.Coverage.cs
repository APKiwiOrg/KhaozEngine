using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class PointShadowRenderer
{
    CoveragePulledResources? _coveragePull;
    IGpuShaderSet[] _coverageShaders = [];
    IGpuPipeline[] _coveragePipelines = [];

    internal void ReserveCoverage(CoverageGpuBatch batch, int count)
    {
        if (_coveragePull is null)
        {
            _coveragePull = new(_gd, resource => _retired.Add(resource));
            var factory = _gd.Factory;
            _coverageShaders = [
                factory.CreateShadersFromSpirv(ShaderSources.CoveragePointVert, ShaderSources.PointShadowRigidFrag, new(PreciseMath: true)),
                factory.CreateShadersFromSpirv(ShaderSources.CoveragePointDissolveVert, ShaderSources.PointShadowRigidDissolveFrag, new(PreciseMath: true)),
                factory.CreateShadersFromSpirv(ShaderSources.CoveragePointDissolveVert, ShaderSources.PointShadowRigidDissolveInvertedFrag, new(PreciseMath: true))];
            _coveragePipelines = new IGpuPipeline[3];
            for (int i = 0; i < 3; i++) _coveragePipelines[i] = factory.CreateGraphicsPipeline(new()
            {
                BlendAttachments = [GpuBlendAttachment.OverrideBlend],
                DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
                Rasterizer = new(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, true, true),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = [_layout, _skinnedCasterLayout, _coveragePull.Layout],
                ShaderSet = _coverageShaders[i],
                VertexLayouts = [],
                Outputs = new(GpuPixelFormat.D32FloatS8UInt, GpuPixelFormat.R32Float),
            });
        }
        _coveragePull.Reserve(batch, count);
    }

    internal void PackCoverage(in CoveragePulledDraw draw) => _coveragePull!.Pack(draw.InstanceSlot, draw);
    internal void UploadCoverage(IGpuCommandList commands) => _coveragePull!.Upload(commands);

    internal void BeginCoverageFace(IGpuCommandList commands, PointShadowAtlas atlas, int packedFaceIndex,
        int face, int row, ShadowCastKind kind)
    {
        commands.SetPipeline(_coveragePipelines[kind switch
        {
            ShadowCastKind.Opaque => 0,
            ShadowCastKind.Dissolving => 1,
            ShadowCastKind.DissolvingInverted => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        }]);
        uint size = (uint)atlas.FaceResolution;
        commands.SetScissorRect(0, (uint)face * size, (uint)row * size, size, size);
        commands.SetGraphicsResourceSet(0, _set, (uint)packedFaceIndex * FaceSlotBytes);
    }

    internal void DrawCoverageCaster(IGpuCommandList commands, in CoveragePulledDraw draw, uint casterSlot)
    {
        commands.SetGraphicsResourceSet(1, _skinnedCasterSet, casterSlot * SkinnedCasterSlotBytes);
        _coveragePull!.Bind(commands, 2, draw.InstanceSlot);
        commands.Draw((uint)draw.CornerCount, 1, 0, 0);
    }

    void DisposeCoverage()
    {
        foreach (var pipeline in _coveragePipelines) pipeline.Dispose();
        foreach (var shaders in _coverageShaders) shaders.Dispose();
        _coveragePull?.Dispose();
    }
}
