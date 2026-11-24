using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class ModelRenderer
{
    internal bool AmbientOutputActive { get; private set; }

    // Keep the working opaque graph until the replacement pipelines, including foliage, are usable.
    // An AA layout change can replace motion resources as part of the same transaction.
    internal void ReplaceAmbientOutput(in GpuOutputDescription outputs, bool enabled)
    {
        var old = new OpaqueOutputState(this);
        ModelMotionResources? oldMotion = _motion;
        if (!MotionMath.IsTemporal(outputs)) _motion = null;
        new OpaqueOutputState().Restore(this);
        try
        {
            _coverageReady = old.CoverageReady;
            _coverageRebuildPending = old.CoveragePending;
            BuildPipelines(_gd.Factory, outputs, failOnCoverageRefusal: true);
            if (enabled || old.Foliage is not null) EnsureFoliagePipeline();
            if (enabled || old.OpaqueFoliage is not null) OpaqueFoliagePipeline();
            if (enabled || old.PreparedFoliage is not null) EnsurePreparedFoliagePipeline();
            if (enabled || old.PreparedOpaqueFoliage is not null) EnsurePreparedFoliagePipeline(opaqueMaterial: true);
            if (enabled)
            {
                if (_tileGroundReliefPipeline is null) BuildTileGroundReliefPipeline(_gd.Factory, hex: false);
                if (_tileGroundReliefHexPipeline is null) BuildTileGroundReliefPipeline(_gd.Factory, hex: true);
            }

            new OpaqueOutputState(this).EnsureDeviceReady();
        }
        catch
        {
            new OpaqueOutputState(this).Retire(_retired);
            if (!ReferenceEquals(_motion, oldMotion)) _retired.Retire(_motion);
            _motion = oldMotion;
            old.Restore(this);
            if (enabled) ReleaseAmbientShaders();
            throw;
        }
        old.Retire(_retired);
        if (!ReferenceEquals(_motion, oldMotion)) _retired.Retire(oldMotion);
        AmbientOutputActive = enabled;
        if (!enabled)
        {
            if (ShadowVisibilityTargets.HasVisibility(outputs)) ReleaseAmbientShaders();
            else ReleaseOutputShaders();
        }
    }

    void ReleaseAmbientShaders()
    {
        _visibilityShaders.ReleaseAmbientVariants(_retired);
        _coverageVisibility.ReleaseAmbientVariants(_retired);
    }

    void ReleaseOutputShaders()
    {
        _visibilityShaders.Release(_retired);
        _coverageVisibility.Release(_retired);
    }

    internal void EnsureCoverageDeviceReady()
    {
        _coveragePipeline?.EnsureDeviceReady();
        _coverageDissolvePipeline?.EnsureDeviceReady();
        _coverageMotionPipeline?.EnsureDeviceReady();
        _coverageDissolveMotionPipeline?.EnsureDeviceReady();
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

        internal void EnsureDeviceReady()
        {
            _rigid?.EnsureDeviceReady(); _dissolve?.EnsureDeviceReady(); _cpuSkinnedMotion?.EnsureDeviceReady();
            _skinned?.EnsureDeviceReady(); _skinnedDissolve?.EnsureDeviceReady(); _splat?.EnsureDeviceReady();
            _tile?.EnsureDeviceReady(); _tileRelief?.EnsureDeviceReady(); _tileHex?.EnsureDeviceReady();
            _coverage?.EnsureDeviceReady(); _coverageDissolve?.EnsureDeviceReady();
            _coverageMotion?.EnsureDeviceReady(); _coverageDissolveMotion?.EnsureDeviceReady();
            Foliage?.EnsureDeviceReady(); OpaqueFoliage?.EnsureDeviceReady();
            PreparedFoliage?.EnsureDeviceReady(); PreparedOpaqueFoliage?.EnsureDeviceReady();
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
