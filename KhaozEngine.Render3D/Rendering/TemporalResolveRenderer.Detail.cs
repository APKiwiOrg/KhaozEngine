using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Internal;
using static KhaozEngine.Render3D.Internal.FragmentPassResources;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class TemporalResolveRenderer
{
    /// <summary>Try creating the optional Fine pass before binding ordinary resolve inputs.</summary>
    internal Exception? TryBindDetail(TemporalHistory history)
    {
        try { BindDetail(history); return null; }
        catch (Exception failure) when (TemporalDetailStore.CanFallbackFromCreationFailure(failure))
        {
            return failure;
        }
    }

    void BindDetail(TemporalHistory history)
    {
        if (history.DetailStore is not { } detail) return;
        IGpuResourceLayout? candidate = null;
        try
        {
            var layout = _detailLayout ?? (candidate = _gd.Factory.CreateResourceLayout(
                new GpuResourceLayoutDescription(T("DetailColor"), T("DetailClassification"))));
            detail.BindResolve(history, _resolveLayout, layout, _fragmentSourceForTests);
            _detailLayout = layout;
        }
        catch
        {
            // A newly created layout has not been recorded. A reused layout still belongs to the renderer.
            candidate?.Dispose();
            throw;
        }
    }
}
