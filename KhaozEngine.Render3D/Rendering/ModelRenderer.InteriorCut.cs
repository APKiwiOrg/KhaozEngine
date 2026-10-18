using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class ModelRenderer
{
    internal const uint InteriorCutWindowOffset = ClusterTailOffset + ClusterTailBytes;
    internal const uint InteriorCutHeightsOffset = InteriorCutWindowOffset + 16;
    internal const uint InteriorCutBytes = 32;
    internal InteriorCutResources InteriorResources { get; }
    IGpuTexture _interiorTexture;
    InteriorCutUniforms _interiorCutUniforms;

    static GpuResourceLayoutElement[] AppendInteriorCutLayout(GpuResourceLayoutElement[] prefix) =>
        [.. prefix, new("InteriorCut", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment)];

    internal void SetInteriorCut(IGpuTexture texture, in InteriorCutUniforms uniforms) =>
        SetInteriorCut(texture, uniforms, Array.Empty<IGpuResourceSet>(), static _ => { });

    /// <summary>Replace the rigid sets through the atlas transaction, then publish the matching frame tail.</summary>
    internal void SetInteriorCut(IGpuTexture texture, in InteriorCutUniforms uniforms,
        IReadOnlyList<IGpuResourceSet> liveMaterialSets,
        Action<Func<IGpuResourceSet, IGpuResourceSet>> commitMaterialSets)
    {
        if (!ReferenceEquals(texture, _interiorTexture))
        {
            var replacements = new Dictionary<IGpuResourceSet, IGpuResourceSet>(ReferenceEqualityComparer.Instance);
            var bindings = new Dictionary<IGpuResourceSet, ShadowSamplingBinding>(ReferenceEqualityComparer.Instance);
            try
            {
                _gd.WaitForIdle();
                AddReplacement(_defaultSet, _shadowMap.ShadowTexture, _pointShadowTexture,
                    _pointShadowTransientTexture, _pointLightBuffer, replacements, bindings, texture);
                foreach (IGpuResourceSet set in liveMaterialSets)
                    if (_shadowSamplingBindings.TryGetValue(set, out ShadowSamplingBinding? binding)
                        && binding.IncludesAtmosphere)
                        AddReplacement(set, _shadowMap.ShadowTexture, _pointShadowTexture,
                            _pointShadowTransientTexture, _pointLightBuffer, replacements, bindings, texture);
            }
            catch
            {
                foreach (IGpuResourceSet set in replacements.Values) set.Dispose();
                throw;
            }
            CommitShadowSamplingSets(replacements, bindings, commitMaterialSets);
            _interiorTexture = texture;
        }
        if (_interiorCutUniforms == uniforms) return;
        _interiorCutUniforms = uniforms;
        _frameImageDirty = true;
    }
}
