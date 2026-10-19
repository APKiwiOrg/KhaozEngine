using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class ModelRenderer
{
    CoveragePulledResources _coveragePull = null!;
    IGpuShaderSet _coverageShaders = null!, _coverageDissolveShaders = null!;
    IGpuPipeline? _coveragePipeline, _coverageDissolvePipeline;
    bool[] _coverageDissolving = [];
    GpuOutputDescription _coverageOutputs;
    internal Action<string, CoveragePulledDraw>? CoveragePullObserverForTests { get; set; }

    internal void ReserveCoverage(CoverageGpuBatch batch, int count)
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
        _coveragePull.Reserve(batch, count);
        ReserveCoverageShadows(count);
        if (_coverageDissolving.Length < count) Array.Resize(ref _coverageDissolving, count);
    }

    internal void PackCoverage(in CoveragePulledDraw draw, in InstanceData instance)
    {
        PackSkinnedMainSlot(draw.InstanceSlot, instance.Model, instance.Tint, instance.Emissive, instance.SpecParams,
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
        commands.SetGraphicsResourceSet(0, _skinnedMainSet!, draw.InstanceSlot * SkinnedMainSlotBytes);
        commands.SetGraphicsResourceSet(1, material ?? _skinnedDefaultFragSet);
        _coveragePull.Bind(commands, 2, draw.InstanceSlot);
        CoveragePullObserverForTests?.Invoke("main", draw);
        commands.Draw((uint)draw.CornerCount, 1, 0, 0);
    }

    void BuildCoveragePipelines(IGpuResourceFactory factory, GpuOutputDescription outputs)
    {
        _coverageOutputs = outputs;
        if (_coveragePull is null) return;
        _coveragePipeline?.Dispose(); _coverageDissolvePipeline?.Dispose();
        _coveragePipeline = Create(_coverageShaders); _coverageDissolvePipeline = Create(_coverageDissolveShaders);
        IGpuPipeline Create(IGpuShaderSet shaders) => factory.CreateGraphicsPipeline(new()
        {
            BlendAttachments = ModelTargetBlends.Opaque(outputs),
            DepthStencil = GpuDepthStencilState.DepthOnlyLessEqual,
            Rasterizer = new(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise, true, false),
            Topology = GpuPrimitiveTopology.TriangleList,
            ResourceLayouts = [_skinnedMainLayout, _skinnedFragLayout, _coveragePull.Layout],
            ShaderSet = shaders,
            VertexLayouts = [],
            Outputs = outputs,
        });
    }

    void DisposeCoveragePulling()
    {
        DisposeCoverageShadows();
        _coveragePipeline?.Dispose(); _coverageDissolvePipeline?.Dispose();
        _coverageShaders?.Dispose(); _coverageDissolveShaders?.Dispose(); _coveragePull?.Dispose();
    }

    // CPU-skinned previous positions reserved before recording while the motion resources do not exist yet. The
    // renderer owns this buffer until EnsureMotionResources hands it to the motion resources it creates.
    IGpuBuffer? _cpuPreviousReserve;
    uint _cpuPreviousReserveCapacity;

    internal (uint Capacity, IGpuBuffer? Buffer) CpuPreviousReservationForTests =>
        _motion is { } motion ? (motion.CpuPreviousCapacity, motion.CpuPreviousCapacity == 0 ? null : motion.CpuPrevious)
            : (_cpuPreviousReserveCapacity, _cpuPreviousReserve);

    internal void ReserveCoveredSkinning(int vertices, int instances, int previous)
    {
        IGpuBuffer? nextVertices = null, nextInstances = null;
        uint vc = Math.Max((uint)vertices, _skinnedVertexCapacity), ic = Math.Max((uint)instances, _skinnedInstanceCapacity);
        try
        {
            if (_skinnedVertexBuffer is null || vc > _skinnedVertexCapacity)
                nextVertices = _gd.Factory.CreateBuffer(new(vc * ModelVertex.SizeInBytes, GpuBufferUsage.VertexBuffer));
            if (_skinnedInstanceBuffer is null || ic > _skinnedInstanceCapacity)
                nextInstances = _gd.Factory.CreateBuffer(new(ic * InstanceData.SizeInBytes, GpuBufferUsage.VertexBuffer));
        }
        catch { nextVertices?.Dispose(); nextInstances?.Dispose(); throw; }
        if (nextVertices is not null)
        { _retired.Retire(_skinnedVertexBuffer); _skinnedVertexBuffer = nextVertices; _skinnedVertexCapacity = vc; }
        if (nextInstances is not null)
        { _retired.Retire(_skinnedInstanceBuffer); _skinnedInstanceBuffer = nextInstances; _skinnedInstanceCapacity = ic; }
        if (previous <= 0) return;
        if (_motion is { } motion) { motion.ReserveCpuPrevious((uint)previous); return; }
        if (_cpuPreviousReserve is not null && _cpuPreviousReserveCapacity >= previous) return;
        IGpuBuffer next = ModelMotionResources.CreateCpuPrevious(_gd, (uint)previous);
        _retired.Retire(_cpuPreviousReserve);
        _cpuPreviousReserve = next;
        _cpuPreviousReserveCapacity = (uint)previous;
    }

    void ReleaseCpuPreviousReserve(bool retire)
    {
        if (retire) _retired.Retire(_cpuPreviousReserve);
        else _cpuPreviousReserve?.Dispose();
        _cpuPreviousReserve = null;
        _cpuPreviousReserveCapacity = 0;
    }
}
