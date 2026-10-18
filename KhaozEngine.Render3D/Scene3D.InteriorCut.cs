using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    /// <summary>The interior spans requested for the next render, or null to disable cutting.</summary>
    public InteriorHeightField? InteriorCut { get; set; }

    long _interiorTextureGeneration;

    void PrepareInteriorCut()
    {
        InteriorCutResources resources = _model.InteriorResources;
        resources.Apply(InteriorCut);
        InteriorCutUniforms uniforms = InteriorCutUniforms.From(InteriorCut, _currentFrameView.RenderOrigin);
        if (_interiorTextureGeneration != resources.TextureGeneration)
        {
            var liveSets = new List<IGpuResourceSet>();
            CollectLiveMaterialSets(liveSets);
            _model.SetInteriorCut(resources.Texture, uniforms, liveSets, CommitMaterialSets);
            _interiorTextureGeneration = resources.TextureGeneration;
        }
        else _model.SetInteriorCut(resources.Texture, uniforms);
        resources.CommitBindings();
    }
}
