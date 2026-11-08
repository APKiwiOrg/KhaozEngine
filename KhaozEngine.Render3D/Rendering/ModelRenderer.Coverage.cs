using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class ModelRenderer
{
    CoveragePulledResources _coveragePull = null!;
    readonly ModelVisibilityShaders _coverageVisibility = new();
    IGpuShaderSet _coverageShaders = null!, _coverageDissolveShaders = null!;
    IGpuPipeline? _coveragePipeline, _coverageDissolvePipeline;
    bool[] _coverageDissolving = [];
    uint _coverageMainSlotOffset;
    internal void SetCoverageMainSlotOffset(uint offset) => _coverageMainSlotOffset = offset;
    GpuOutputDescription _coverageOutputs;
    internal Action<string, CoverageDrawEvidence>? CoveragePullObserverForTests { get; set; }

    internal void ReserveCoverage(int count)
    {
        if (_coveragePull is null)
        {
            _coveragePull = new(_gd, resource => _retired.Retire(resource));
            _coverageShaders = _gd.Factory.CreateShadersFromSpirv(ShaderSources.CoverageModelVert,
                ShaderSources.SkinnedModelFrag, new(PreciseMath: true));
            _coverageDissolveShaders = _gd.Factory.CreateShadersFromSpirv(ShaderSources.CoverageModelVert,
                ShaderSources.SkinnedModelDissolveFrag, new(PreciseMath: true));
            BuildCoveragePipelines(_gd.Factory, _coverageOutputs);
        }
        _coveragePull.Reserve(count);
        ReserveCoverageShadows(count);
        if (_coverageDissolving.Length < count) Array.Resize(ref _coverageDissolving, count);
    }

    internal void ReserveCoverageSurface(int slot, in CoverageSurfaceRange surface) => _coveragePull.ReserveSurface(slot, surface);

    internal void PackCoverage(in CoveragePulledDraw draw, in InstanceData instance)
    {
        PackSkinnedMainSlot(_coverageMainSlotOffset + draw.InstanceSlot, instance.Model, instance.Tint, instance.Emissive, instance.SpecParams,
            instance.Dissolve, instance.IsDynamic, instance.DissolveComplement);
        _coverageDissolving[draw.InstanceSlot] = instance.Dissolve.X > 0 || instance.DissolveComplement > 0;
        _coveragePull.Pack(draw.InstanceSlot, draw);
    }

    internal void PackCoverageSurface(in CoveragePulledDraw draw) => _coveragePull.Pack(draw.InstanceSlot, draw);
    internal void UploadCoverageSurfaces(IGpuCommandList commands) => _coveragePull.Upload(commands);
    internal void UploadCoverage(IGpuCommandList commands) => UploadSkinnedMainSlots(commands);

    internal void DrawCoverage(IGpuCommandList commands, in CoveragePulledDraw draw, IGpuResourceSet? material)
    {
        commands.SetPipeline(_coverageDissolving[draw.InstanceSlot] ? _coverageDissolvePipeline! : _coveragePipeline!);
        commands.SetGraphicsResourceSet(0, _skinnedMainSet!, (_coverageMainSlotOffset + draw.InstanceSlot) * SkinnedMainSlotBytes);
        commands.SetGraphicsResourceSet(1, material ?? _skinnedDefaultFragSet);
        _coveragePull.Bind(commands, 2, draw.InstanceSlot);
        commands.Draw((uint)draw.CornerCount, 1, 0, 0);
        CoveragePullObserverForTests?.Invoke("main", new(draw));
    }

    void BuildCoveragePipelines(IGpuResourceFactory factory, GpuOutputDescription outputs)
    {
        _coverageOutputs = outputs;
        if (_coveragePull is null) return;
        _retired.Retire(_coveragePipeline); _retired.Retire(_coverageDissolvePipeline);
        BuildCoverageMotionPipelines(factory, outputs);
        _coveragePipeline = Create(_coverageShaders); _coverageDissolvePipeline = Create(_coverageDissolveShaders);
        IGpuPipeline Create(IGpuShaderSet shaders) => factory.CreateGraphicsPipeline(new()
        {
            BlendAttachments = ModelTargetBlends.Opaque(outputs),
            DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
            Rasterizer = new(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, true, false),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = [_skinnedMainLayout, _skinnedFragLayout, _coveragePull.Layout],
            ShaderSet = _coverageVisibility.TrySelect(factory, outputs, ShaderSources.CoverageModelVert,
                ReferenceEquals(shaders, _coverageDissolveShaders) ? ShaderSources.SkinnedModelDissolveFrag : ShaderSources.SkinnedModelFrag,
                false, out var visibility, new(PreciseMath: true)) ? visibility : shaders,
            VertexLayouts = [],
            Outputs = outputs,
        });
    }

    void DisposeCoveragePulling()
    {
        _coverageVisibility.Dispose();
        DisposeCoverageShadows();
        DisposeCoverageMotion();
        _coveragePipeline?.Dispose(); _coverageDissolvePipeline?.Dispose();
        _coverageShaders?.Dispose(); _coverageDissolveShaders?.Dispose(); _coveragePull?.Dispose();
    }

}
