using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    /// <summary>The interior spans requested for the next render, or null to disable cutting.</summary>
    public InteriorHeightField? InteriorCut
    {
        get => _interiorCut;
        set
        {
            if (ReferenceEquals(_interiorCut, value)) return;
            _interiorCut = value;
            PrepareInteriorRelevanceContext();
            _interiorRelevanceDirty = true;
        }
    }

    InteriorHeightField? _interiorCut;

    long _interiorTextureGeneration;

    void DisposeModelWithInteriorCutBorrowers(Internal.ResourceDisposal disposal, bool shadowReceiversReleased)
    {
        // The point renderer borrows the model owner's cut image. Its atlas stays alive for model receivers.
        if (!disposal.Release("pointShadowBorrower", () => _pointShadows?.Dispose())) return;
        _pointShadows = null;
        // External material sets also name model-owned layouts, buffers, maps and samplers.
        _pointShadowOwnersReleased = shadowReceiversReleased && disposal.Release("model", _model.Dispose);
    }

    void PrepareInteriorCut()
    {
        RefreshInteriorRelevance();
        InteriorCutResources resources = _model.InteriorResources;
        resources.Apply(InteriorCut);
        InteriorCutUniforms uniforms = InteriorCutUniforms.From(InteriorCut, _currentFrameView.RenderOrigin);
        bool textureChanged = _interiorTextureGeneration != resources.TextureGeneration;
        if (textureChanged)
        {
            var liveSets = new List<IGpuResourceSet>();
            CollectLiveMaterialSets(liveSets);
            _model.SetInteriorCut(resources.Texture, uniforms, liveSets, CommitMaterialSets);
        }
        else _model.SetInteriorCut(resources.Texture, uniforms);
        // Empty frames need no cut borrowers. A texture change must still rebind every borrower before retirement.
        if (textureChanged || InteriorCut is not null || _instances.Items.Count > 0 || _skinnedInstances.Items.Count > 0
            || _meshOutlinePartCount > 0 || _silhouetteDraws.Count > 0)
        {
            _model.ShadowMap.SetInteriorCut(resources.Texture, uniforms);
            _pointShadows?.SetInteriorCut(resources.Texture, uniforms);
            _targetOutlines.SetInteriorCut(resources.Texture, uniforms);
            _silhouettes.SetInteriorCut(resources.Texture, uniforms);
        }
        resources.CommitBindings();
        _interiorTextureGeneration = resources.TextureGeneration;
    }
}
