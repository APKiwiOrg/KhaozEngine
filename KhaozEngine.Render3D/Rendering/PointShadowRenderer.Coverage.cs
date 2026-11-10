using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class PointShadowRenderer
{
    CoveragePulledResources? _coveragePull;
    IGpuShaderSet[] _coverageShaders = [];
    IGpuPipeline[] _coveragePipelines = [];
    bool _coverageCompilationReady;
    bool _coverageRefused;
    internal void RefuseCoverage(GpuRetireQueue? retired = null)
    { _coverageRefused = true; DisposeCoverage(retired); }

    internal void ReserveCoverage(int count)
    {
        if (_coverageRefused)
            throw new GpuShaderPolicyUnsupportedException(GpuShaderPolicyRefusal.PreciseMathUnavailable, GpuShaderStages.Vertex);
        if (_coveragePull is null)
        {
            var pull = new CoveragePulledResources(_gd, resource => _retired.Add(resource));
            var factory = _gd.Factory;
            // Coverage consumes the skinned caster bindings, which exclude the rigid interior cut.
            var shaders = new IGpuShaderSet[3];
            var pipelines = new IGpuPipeline[3];
            try
            {
                shaders[0] = factory.CreateShadersFromSpirv(ShaderSources.CoveragePointVert, ShaderSources.PointShadowRigidFrag, new(PreciseMath: true));
                shaders[1] = factory.CreateShadersFromSpirv(ShaderSources.CoveragePointDissolveVert, ShaderSources.PointShadowSkinnedDissolveFrag, new(PreciseMath: true));
                shaders[2] = factory.CreateShadersFromSpirv(ShaderSources.CoveragePointDissolveVert, ShaderSources.PointShadowSkinnedDissolveInvertedFrag, new(PreciseMath: true));
                for (int i = 0; i < 3; i++) pipelines[i] = factory.CreateGraphicsPipeline(new()
                {
                    BlendAttachments = [GpuBlendAttachment.OverrideBlend],
                    DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
                    Rasterizer = new(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, true, true),
                    Topology = GpuPrimitiveTopology.TriangleList,
                    ResourceLayouts = [_layout, _skinnedCasterLayout, pull.Layout],
                    ShaderSet = shaders[i],
                    VertexLayouts = [],
                    Outputs = new(GpuPixelFormat.D32FloatS8UInt, GpuPixelFormat.R32Float),
                });
                pull.Reserve(count);
            }
            catch
            {
                foreach (var pipeline in pipelines) pipeline?.Dispose();
                foreach (var shader in shaders) shader?.Dispose();
                pull.Dispose();
                throw;
            }
            _coveragePull = pull; _coverageShaders = shaders; _coveragePipelines = pipelines;
        }
        _coveragePull.Reserve(count);
    }

    internal void CompleteCoverageCompilation()
    {
        if (_coverageCompilationReady) return;
        try
        {
            foreach (var shaders in _coverageShaders) shaders.EnsureCompilationReady();
            _coverageCompilationReady = true;
        }
        catch { DisposeCoverage(); throw; }
    }

    internal void ReserveCoverageSurface(int slot, in CoverageSurfaceRange surface) => _coveragePull!.ReserveSurface(slot, surface);
    internal Action<string, CoverageDrawEvidence>? CoveragePullObserverForTests { get; set; }

    internal void PackCoverage(in CoveragePulledDraw draw) => _coveragePull!.Pack(draw.BindingSlot, draw);
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
        _coveragePull!.Bind(commands, 2, draw.BindingSlot);
        commands.Draw((uint)draw.CornerCount, 1, 0, 0);
        CoveragePullObserverForTests?.Invoke("point", new(draw));
    }

    void DisposeCoverage(GpuRetireQueue? retired = null)
    {
        foreach (var pipeline in _coveragePipelines) ResourceRetirement.Free(pipeline, retired);
        foreach (var shaders in _coverageShaders) ResourceRetirement.Free(shaders, retired);
        ResourceRetirement.Free(_coveragePull, retired);
        _coveragePipelines = []; _coverageShaders = []; _coveragePull = null;
        _coverageCompilationReady = false;
    }
}
