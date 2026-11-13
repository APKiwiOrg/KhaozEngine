using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

/// <summary>Applies <see cref="RenderQuality.MaterialAnisotropy"/> at the frame boundary.</summary>
public sealed partial class Scene3D
{
    // A request that failed to apply. It is held back while it stays the request, not retried every frame, and any
    // other request clears it, the active value included, so going back to the active value and then to the failed
    // one again retries it.
    int _failedMaterialAnisotropy;

    // Called by Begin, beside the shadow layout requests, so a menu change rebuilds the material bindings before
    // anything of the frame is recorded against the old sampler.
    void ApplyMaterialAnisotropy()
    {
        int requested = ModelRenderer.ClampMaterialAnisotropy(Post.Quality.MaterialAnisotropy);
        if (requested != _failedMaterialAnisotropy) _failedMaterialAnisotropy = 0;
        if (requested == _model.MaterialAnisotropy || requested == _failedMaterialAnisotropy) return;

        var liveSets = new List<IGpuResourceSet>();
        CollectLiveMaterialSets(liveSets);
        if (_model.ReplaceMaterialSampler(requested, liveSets, CommitMaterialSets) == ShadowLayoutReplacementResult.Failed)
        {
            _failedMaterialAnisotropy = requested;
            _shadowReconfigureLogger.Error(
                $"Material anisotropy {requested} could not be applied. Retaining {_model.MaterialAnisotropy}.");
            return;
        }
        _failedMaterialAnisotropy = 0;
    }
}
