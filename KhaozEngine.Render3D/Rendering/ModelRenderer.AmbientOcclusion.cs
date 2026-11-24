using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class ModelRenderer
{
    // Adding the diffuse attachment preserves all layouts, materials and motion resources. Keep the
    // working pipelines until the extended output graph is usable, including every already-used variant.
    internal void EnableAmbientOutput(in GpuOutputDescription outputs)
    {
        var old = new OpaqueOutputState(this);
        new OpaqueOutputState().Restore(this);
        try
        {
            _coverageReady = old.CoverageReady;
            _coverageRebuildPending = old.CoveragePending;
            BuildPipelines(_gd.Factory, outputs, failOnCoverageRefusal: true);
            if (old.Foliage is not null) EnsureFoliagePipeline();
            if (old.OpaqueFoliage is not null) OpaqueFoliagePipeline();
            if (old.PreparedFoliage is not null) EnsurePreparedFoliagePipeline();
            if (old.PreparedOpaqueFoliage is not null) EnsurePreparedFoliagePipeline(opaqueMaterial: true);
            _visibilityShaders.EnsureCompilationReady();
            _coverageVisibility.EnsureCompilationReady();
        }
        catch
        {
            new OpaqueOutputState(this).Retire(_retired);
            old.Restore(this);
            _visibilityShaders.ReleaseAmbientVariants(_retired);
            _coverageVisibility.ReleaseAmbientVariants(_retired);
            throw;
        }
        old.Retire(_retired);
    }

    readonly struct OpaqueOutputState
    {
        readonly IGpuPipeline? _rigid, _dissolve, _cpuSkinnedMotion, _skinned, _skinnedDissolve, _splat;
        readonly IGpuPipeline? _tile, _tileRelief, _tileHex;
        readonly IGpuPipeline? _coverage, _coverageDissolve, _coverageMotion, _coverageDissolveMotion;
        internal readonly IGpuPipeline? Foliage, OpaqueFoliage, PreparedFoliage, PreparedOpaqueFoliage;
        readonly IGpuShaderSet? _preparedFoliageShaders, _preparedOpaqueFoliageShaders;
        readonly GpuOutputDescription _tileOutputs, _foliageOutputs, _preparedOutputs, _coverageOutputs;
        readonly GpuVertexLayoutDescription _tileVertex, _tileInstance;
        internal readonly bool CoverageReady, CoveragePending;

        internal OpaqueOutputState(ModelRenderer model)
        {
            (_rigid, _dissolve, _cpuSkinnedMotion) = (model._pipeline, model._dissolvePipeline, model._cpuSkinnedMotionPipeline);
            (_skinned, _skinnedDissolve, _splat) = (model._skinnedPipeline, model._skinnedDissolvePipeline, model._splatPipeline);
            (_tile, _tileRelief, _tileHex) = (model._tileGroundPipeline, model._tileGroundReliefPipeline, model._tileGroundReliefHexPipeline);
            (_tileOutputs, _tileVertex, _tileInstance) = (model._tileGroundOutputs, model._tileGroundVertexLayout, model._tileGroundInstanceLayout);
            (_coverage, _coverageDissolve) = (model._coveragePipeline, model._coverageDissolvePipeline);
            (_coverageMotion, _coverageDissolveMotion) = (model._coverageMotionPipeline, model._coverageDissolveMotionPipeline);
            (_coverageOutputs, CoverageReady, CoveragePending) = (model._coverageOutputs, model._coverageReady, model._coverageRebuildPending);
            (Foliage, OpaqueFoliage, _foliageOutputs) = (model._foliagePipeline, model._foliageOpaquePipeline, model._foliageOutputs);
            (PreparedFoliage, PreparedOpaqueFoliage, _preparedOutputs) =
                (model._preparedFoliagePipeline, model._preparedFoliageOpaquePipeline, model._preparedFoliageOutputs);
            (_preparedFoliageShaders, _preparedOpaqueFoliageShaders) =
                (model._preparedFoliageShaders, model._preparedFoliageOpaqueShaders);
        }

        internal void Restore(ModelRenderer model)
        {
            (model._pipeline, model._dissolvePipeline, model._cpuSkinnedMotionPipeline) = (_rigid!, _dissolve!, _cpuSkinnedMotion);
            (model._skinnedPipeline, model._skinnedDissolvePipeline, model._splatPipeline) = (_skinned!, _skinnedDissolve!, _splat!);
            (model._tileGroundPipeline, model._tileGroundReliefPipeline, model._tileGroundReliefHexPipeline) = (_tile!, _tileRelief, _tileHex);
            (model._tileGroundOutputs, model._tileGroundVertexLayout, model._tileGroundInstanceLayout) = (_tileOutputs, _tileVertex, _tileInstance);
            (model._coveragePipeline, model._coverageDissolvePipeline) = (_coverage, _coverageDissolve);
            (model._coverageMotionPipeline, model._coverageDissolveMotionPipeline) = (_coverageMotion, _coverageDissolveMotion);
            (model._coverageOutputs, model._coverageReady, model._coverageRebuildPending) = (_coverageOutputs, CoverageReady, CoveragePending);
            (model._foliagePipeline, model._foliageOpaquePipeline, model._foliageOutputs) = (Foliage, OpaqueFoliage, _foliageOutputs);
            (model._preparedFoliagePipeline, model._preparedFoliageOpaquePipeline, model._preparedFoliageOutputs) =
                (PreparedFoliage, PreparedOpaqueFoliage, _preparedOutputs);
            (model._preparedFoliageShaders, model._preparedFoliageOpaqueShaders) =
                (_preparedFoliageShaders, _preparedOpaqueFoliageShaders);
        }

        internal void Retire(GpuRetireQueue retired)
        {
            retired.Retire(_rigid, _dissolve, _cpuSkinnedMotion);
            retired.Retire(_skinned, _skinnedDissolve, _splat);
            retired.Retire(_tile, _tileRelief, _tileHex);
            retired.Retire(_coverage, _coverageDissolve, _coverageMotion);
            retired.Retire(_coverageDissolveMotion, Foliage, OpaqueFoliage);
            retired.Retire(PreparedFoliage, PreparedOpaqueFoliage, _preparedFoliageShaders);
            retired.Retire(_preparedOpaqueFoliageShaders);
        }
    }
}
