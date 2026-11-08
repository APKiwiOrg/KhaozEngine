using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering;

/// <summary>The caustic map bound in every lit set: a one-texel placeholder until a real map replaces it.</summary>
internal sealed partial class ModelRenderer
{
    IGpuTexture _causticPlaceholder = null!;
    IGpuTexture _causticMap = null!;
    IGpuSampler _causticSampler = null!;

    internal IGpuTexture CausticMap => _causticMap;

    void CreateCausticPlaceholder(IGpuResourceFactory factory)
    {
        _causticPlaceholder = factory.CreateTexture(GpuTextureDescription.Texture2D(
            1, 1, GpuPixelFormat.R16G16B16A16Float, GpuTextureUsage.Sampled));
        byte[] one = BitConverter.GetBytes((Half)1f);
        _gd.UpdateTexture(_causticPlaceholder, [.. one, .. one, .. one, .. one], 0, 0, 1, 1);
        _causticSampler = factory.CreateSampler(GpuSamplerDescription.Linear);
        _causticMap = _causticPlaceholder;
    }

    void DisposeCausticPlaceholder()
    {
        _causticSampler.Dispose();
        _causticPlaceholder.Dispose();
    }

    /// <summary>Replace every set that captured the map, or none of them when one fails to build. The caller owns
    /// the map. A frame in flight may still read the sets naming the previous map, so they go through the retire
    /// queue rather than a drain, which would stall the frame that first meets caustic water and each layer growth.
    /// The opaque foliage subset rebuilds on its next draw and retires its own set the same way.</summary>
    internal void SetCausticMap(IGpuTexture map, IReadOnlyList<IGpuResourceSet> liveMaterialSets,
        Action<Func<IGpuResourceSet, IGpuResourceSet>> commitMaterialSets)
    {
        if (ReferenceEquals(map, _causticMap)) return;

        var replacements = new Dictionary<IGpuResourceSet, IGpuResourceSet>(ReferenceEqualityComparer.Instance);
        var bindings = new Dictionary<IGpuResourceSet, ShadowSamplingBinding>(ReferenceEqualityComparer.Instance);
        IGpuResourceSet? splatFrameSet = null;
        IGpuResourceSet? tileGroundFrameSet = null;
        IGpuResourceSet? skinnedMainSet = null;
        try
        {
            AddReplacement(_defaultSet, _shadowMap.ShadowTexture, _pointShadowTexture, _pointShadowTransientTexture,
                _pointLightBuffer, replacements, bindings, causticMap: map);
            foreach (IGpuResourceSet set in liveMaterialSets)
                if (_shadowSamplingBindings.TryGetValue(set, out ShadowSamplingBinding? binding)
                    && binding.IncludesAtmosphere)
                    AddReplacement(set, _shadowMap.ShadowTexture, _pointShadowTexture, _pointShadowTransientTexture,
                        _pointLightBuffer, replacements, bindings, causticMap: map);

            splatFrameSet = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_splatFrameLayout,
                WaterVolumeResources(map, _atmosphere.AppendResources(_ubo, _pointLightBuffer, _pointLightClusterBuffer))));
            tileGroundFrameSet = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_tileGroundFrameLayout,
                WaterVolumeResources(map, _atmosphere.AppendResources(_ubo, _pointLightBuffer, _pointLightClusterBuffer))));
            if (_skinnedMainSet is not null)
            {
                skinnedMainSet = _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(_skinnedMainLayout,
                    WaterVolumeResources(map, _atmosphere.AppendResources(_ubo, _pointLightBuffer, _pointLightClusterBuffer,
                        new GpuBufferRange(_skinnedMainUbo!, 0, SkinnedMainSlotBytes)))));
            }
        }
        catch
        {
            foreach (IGpuResourceSet replacement in replacements.Values) replacement.Dispose();
            splatFrameSet?.Dispose();
            tileGroundFrameSet?.Dispose();
            skinnedMainSet?.Dispose();
            throw;
        }

        IGpuResourceSet oldSplatFrameSet = _splatFrameSet;
        IGpuResourceSet oldTileGroundFrameSet = _tileGroundFrameSet;
        IGpuResourceSet? oldSkinnedMainSet = _skinnedMainSet;

        _causticMap = map;
        _splatFrameSet = splatFrameSet;
        _tileGroundFrameSet = tileGroundFrameSet;
        if (oldSkinnedMainSet is not null) _skinnedMainSet = skinnedMainSet;
        CommitShadowSamplingSets(replacements, bindings, commitMaterialSets, _retired);
        _retired.Retire(oldSkinnedMainSet, oldSplatFrameSet, oldTileGroundFrameSet);
    }
}
