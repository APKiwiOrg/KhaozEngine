using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>The finite checked and certified programs, built together before steady frames. Each uses its matching
/// layout and cached sets. The renderer that owns them drains before disposal, as it does for its other pipelines.</summary>
internal sealed class TemporalVisibilityPipelinePair : IDisposable
{
    readonly IGpuShaderSet _checkedShaders, _certifiedShaders;
    readonly IGpuPipeline _checkedPipeline, _certifiedPipeline;
    readonly bool _checkedUsesVisibility, _certifiedUsesVisibility;

    internal TemporalVisibilityPipelinePair(IGpuDevice device, string source, IGpuResourceLayout[] layouts,
        IGpuResourceLayout[] certifiedLayouts, GpuOutputDescription outputs, Func<string, string>? rewrite = null)
    {
        string Fragment(string text) => TemporalVisibilityGatherPolicy.ForBackend(rewrite?.Invoke(text) ?? text,
            device.Backend);
        IGpuResourceFactory factory = device.Factory;
        string checkedSource = Fragment(source), certifiedSource = Fragment(TemporalVisibilityPrograms.WithoutDecision(source));
        // Test rewrites can restore the check in the specialization or remove it from the control. Select the
        // matching layout after the rewrite. Production always has the checked/full and certified/compact pair.
        _checkedUsesVisibility = !checkedSource.Contains("bool visibilityReleased = false;", StringComparison.Ordinal);
        _certifiedUsesVisibility = !certifiedSource.Contains("bool visibilityReleased = false;", StringComparison.Ordinal);
        IGpuShaderSet? checkedShaders = null, certifiedShaders = null;
        IGpuPipeline? checkedPipeline = null;
        try
        {
            checkedShaders = factory.CreateShadersFromSpirv(ShaderSources.FullscreenVert, checkedSource);
            certifiedShaders = factory.CreateShadersFromSpirv(ShaderSources.FullscreenVert, certifiedSource);
            checkedPipeline = TemporalResolveRenderer.Fullscreen(factory, checkedShaders,
                _checkedUsesVisibility ? layouts : certifiedLayouts, outputs);
            _certifiedPipeline = TemporalResolveRenderer.Fullscreen(factory, certifiedShaders,
                _certifiedUsesVisibility ? layouts : certifiedLayouts, outputs);
            _checkedPipeline = checkedPipeline;
            _checkedShaders = checkedShaders;
            _certifiedShaders = certifiedShaders;
        }
        catch
        {
            checkedPipeline?.Dispose();
            certifiedShaders?.Dispose();
            checkedShaders?.Dispose();
            throw;
        }
    }

    internal bool UsesVisibility(bool certified) => certified ? _certifiedUsesVisibility : _checkedUsesVisibility;

    internal IGpuPipeline Select(bool certified) => certified ? _certifiedPipeline : _checkedPipeline;

    public void Dispose()
    {
        _checkedPipeline.Dispose();
        _certifiedPipeline.Dispose();
        _checkedShaders.Dispose();
        _certifiedShaders.Dispose();
    }
}
