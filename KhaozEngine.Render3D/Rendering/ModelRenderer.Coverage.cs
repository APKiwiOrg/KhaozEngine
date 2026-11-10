using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class ModelRenderer
{
    internal GpuShaderCompileOptions CoverageVertexOptions { get; set; } = new(PreciseMath: true);
    CoveragePulledResources _coveragePull = null!;
    readonly ModelVisibilityShaders _coverageVisibility = new();
    bool _coverageReady, _coverageRefused, _coverageRebuildPending;
    internal bool CoverageRefused => _coverageRefused;
    IGpuShaderSet _coverageShaders = null!, _coverageDissolveShaders = null!;
    IGpuPipeline? _coveragePipeline, _coverageDissolvePipeline;
    bool[] _coverageDissolving = [];
    uint _coverageMainSlotOffset;
    internal void SetCoverageMainSlotOffset(uint offset) => _coverageMainSlotOffset = offset;
    GpuOutputDescription _coverageOutputs;
    internal Action<string, CoverageDrawEvidence>? CoveragePullObserverForTests { get; set; }

    internal void RefuseCoverage()
    {
        _coverageRefused = true;
        DisposeCoveragePulling(_retired);
    }

    internal void ReserveCoverage(int count)
    {
        if (_coverageRefused)
            throw new GpuShaderPolicyUnsupportedException(GpuShaderPolicyRefusal.PreciseMathUnavailable, GpuShaderStages.Vertex);
        if (_coveragePull is null)
        {
            // All resources remain an unpublished transaction until every graphics owner is usable.
            try
            {
                _coveragePull = new(_gd, resource => _retired.Retire(resource));
                _coverageShaders = _gd.Factory.CreateShadersFromSpirv(ShaderSources.CoverageModelVert,
                    ShaderSources.SkinnedModelFrag, CoverageVertexOptions);
                _coverageDissolveShaders = _gd.Factory.CreateShadersFromSpirv(ShaderSources.CoverageModelVert,
                    ShaderSources.SkinnedModelDissolveFrag, CoverageVertexOptions);
                CreateCoveragePipelines(_gd.Factory, _coverageOutputs);
                _coveragePull.Reserve(count);
                ReserveCoverageShadows(count);
            }
            catch
            {
                DisposeCoveragePulling();
                _coverageRefused = true;
                throw;
            }
        }
        _coveragePull.Reserve(count);
        ReserveCoverageShadows(count);
        if (_coverageDissolving.Length < count) Array.Resize(ref _coverageDissolving, count);
    }

    internal void CompleteCoverageCompilation()
    {
        if (_coverageReady) return;
        _coverageShaders.EnsureCompilationReady();
        _coverageDissolveShaders.EnsureCompilationReady();
        _coverageMotionShaders?.EnsureCompilationReady();
        _coverageDissolveMotionShaders?.EnsureCompilationReady();
        _coverageVisibility.EnsureCompilationReady();
        foreach (var shaders in _coverageCascadeShaders) shaders.EnsureCompilationReady();
        _coverageReady = true;
        _coverageRebuildPending = false;
    }

    /// <summary>Joins output-rebuilt variants once every renderer has queued its programs for that rebuild.</summary>
    internal void CompleteCoverageOutputRebuild()
    {
        if (!_coverageRebuildPending || _coverageRefused) return;
        try { CompleteCoverageCompilation(); }
        catch (Exception error) when (error is NotSupportedException or ShaderValidationException) { RefuseCoverage(); }
    }

    internal void ReserveCoverageSurface(int slot, in CoverageSurfaceRange surface) => _coveragePull.ReserveSurface(slot, surface);

    internal void PackCoverage(in CoveragePulledDraw draw, in InstanceData instance)
    {
        PackSkinnedMainSlot(_coverageMainSlotOffset + draw.InstanceSlot, instance.Model, instance.Tint, instance.Emissive, instance.SpecParams,
            instance.Dissolve, instance.IsDynamic, instance.DissolveComplement);
        _coverageDissolving[draw.InstanceSlot] = instance.Dissolve.X > 0 || instance.DissolveComplement > 0;
        _coveragePull.Pack(draw.BindingSlot, draw);
    }

    internal void PackCoverageSurface(in CoveragePulledDraw draw) => _coveragePull.Pack(draw.BindingSlot, draw);
    internal void UploadCoverageSurfaces(IGpuCommandList commands) => _coveragePull.Upload(commands);
    internal void UploadCoverage(IGpuCommandList commands) => UploadSkinnedMainSlots(commands);

    internal void DrawCoverage(IGpuCommandList commands, in CoveragePulledDraw draw, IGpuResourceSet? material)
    {
        commands.SetPipeline(_coverageDissolving[draw.InstanceSlot] ? _coverageDissolvePipeline! : _coveragePipeline!);
        commands.SetGraphicsResourceSet(0, _skinnedMainSet!, (_coverageMainSlotOffset + draw.InstanceSlot) * SkinnedMainSlotBytes);
        commands.SetGraphicsResourceSet(1, material ?? _skinnedDefaultFragSet);
        _coveragePull.Bind(commands, 2, draw.BindingSlot);
        commands.Draw((uint)draw.CornerCount, 1, 0, 0);
        CoveragePullObserverForTests?.Invoke("main", new(draw));
    }

    void BuildCoveragePipelines(IGpuResourceFactory factory, GpuOutputDescription outputs)
    {
        _coverageOutputs = outputs;
        // Ordinary output rebuilds must never resurrect a refused or incomplete coverage owner.
        if ((!_coverageReady && !_coverageRebuildPending) || _coverageRefused) return;
        _coverageReady = false;
        _coverageRebuildPending = true;
        // Compilation joins after the scene finishes this batch, so these variants cannot delay later renderers.
        try { CreateCoveragePipelines(factory, outputs); }
        catch (Exception error) when (error is NotSupportedException or ShaderValidationException)
        {
            // Earlier frames may still reference shared coverage owners. Retire the whole refused family
            // behind completion rather than destroying an in-flight program or retaining a failed variant.
            RefuseCoverage();
        }
    }

    void CreateCoveragePipelines(IGpuResourceFactory factory, GpuOutputDescription outputs)
    {
        _retired.Retire(_coveragePipeline); _retired.Retire(_coverageDissolvePipeline);
        _coveragePipeline = _coverageDissolvePipeline = null;
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
                false, out var visibility, CoverageVertexOptions) ? visibility : shaders,
            VertexLayouts = [],
            Outputs = outputs,
        });
    }

    void DisposeCoveragePulling(GpuRetireQueue? retired = null)
    {
        _coverageVisibility.Release(retired);
        DisposeCoverageShadows(retired);
        DisposeCoverageMotion(retired);
        ResourceRetirement.Free(_coveragePipeline, retired); ResourceRetirement.Free(_coverageDissolvePipeline, retired);
        ResourceRetirement.Free(_coverageShaders, retired); ResourceRetirement.Free(_coverageDissolveShaders, retired);
        ResourceRetirement.Free(_coveragePull, retired);
        _coveragePipeline = _coverageDissolvePipeline = null;
        _coverageShaders = _coverageDissolveShaders = null!;
        _coveragePull = null!; _coverageReady = false;
    }

}
