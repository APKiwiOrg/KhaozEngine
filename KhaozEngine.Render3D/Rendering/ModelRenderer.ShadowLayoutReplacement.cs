using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering;

internal sealed partial class ModelRenderer
{
    readonly ConditionalWeakTable<IGpuResourceSet, ShadowSamplingBinding> _shadowSamplingBindings = new();

    internal bool ReplaceShadowLayout(int resolution, int cascadeCount, ShadowSettings settings,
        IReadOnlyList<IGpuResourceSet> liveMaterialSets,
        Action<Func<IGpuResourceSet, IGpuResourceSet>> commitMaterialSets,
        Action invalidateShadowDepth)
    {
        if (_shadowMap.MatchesLayout(resolution, cascadeCount)) return false;

        ShadowMapRenderer.ShadowLayoutReplacement? graph = null;
        var replacements = new Dictionary<IGpuResourceSet, IGpuResourceSet>(ReferenceEqualityComparer.Instance);
        var replacementBindings = new Dictionary<IGpuResourceSet, ShadowSamplingBinding>(ReferenceEqualityComparer.Instance);
        try
        {
            _gd.WaitForIdle();
            graph = _shadowMap.BuildReplacement(resolution, cascadeCount);
            AddReplacement(_defaultSet, graph.ShadowTexture, _pointShadowTexture, replacements, replacementBindings);
            AddReplacement(_skinnedDefaultFragSet, graph.ShadowTexture, _pointShadowTexture, replacements, replacementBindings);
            foreach (IGpuResourceSet set in liveMaterialSets)
                AddReplacement(set, graph.ShadowTexture, _pointShadowTexture, replacements, replacementBindings);
        }
        catch
        {
            foreach (IGpuResourceSet set in replacements.Values) set.Dispose();
            graph?.Dispose();
            return false;
        }

        ShadowMapRenderer.ShadowLayoutReplacement oldGraph = _shadowMap.CommitReplacement(graph);
        settings.CommitAtlasReplacement(graph.Resolution, graph.CascadeCount);
        invalidateShadowDepth();
        CommitShadowSamplingSets(replacements, replacementBindings, commitMaterialSets);
        oldGraph.Dispose();
        return true;
    }

    // Swap every rebuilt set in, re-key the rebuild descriptions onto the new sets, and free the old ones. Shared
    // with BindPointShadowAtlas (ModelRenderer.PointShadowUniforms.cs), because a set carrying the cascade atlas carries
    // the point atlas beside it and both swaps are the same transaction shape.
    void CommitShadowSamplingSets(Dictionary<IGpuResourceSet, IGpuResourceSet> replacements,
        Dictionary<IGpuResourceSet, ShadowSamplingBinding> replacementBindings,
        Action<Func<IGpuResourceSet, IGpuResourceSet>> commitMaterialSets)
    {
        IGpuResourceSet oldDefaultSet = _defaultSet;
        IGpuResourceSet oldSkinnedDefaultFragSet = _skinnedDefaultFragSet;
        commitMaterialSets(old => replacements[old]);
        _defaultSet = replacements[oldDefaultSet];
        _skinnedDefaultFragSet = replacements[oldSkinnedDefaultFragSet];

        foreach ((IGpuResourceSet oldSet, IGpuResourceSet newSet) in replacements)
        {
            _shadowSamplingBindings.Remove(oldSet);
            _shadowSamplingBindings.Add(newSet, replacementBindings[newSet]);
            oldSet.Dispose();
        }
    }

    void AddReplacement(IGpuResourceSet oldSet, IGpuTexture shadowTexture, IGpuTexture pointShadowTexture,
        Dictionary<IGpuResourceSet, IGpuResourceSet> replacements,
        Dictionary<IGpuResourceSet, ShadowSamplingBinding> replacementBindings) =>
        AddReplacement(oldSet, shadowTexture, pointShadowTexture, _pointShadowTransientTexture, _pointLightBuffer,
            replacements, replacementBindings);

    void AddReplacement(IGpuResourceSet oldSet, IGpuTexture shadowTexture, IGpuTexture pointShadowTexture,
        IGpuBuffer pointLightBuffer, Dictionary<IGpuResourceSet, IGpuResourceSet> replacements,
        Dictionary<IGpuResourceSet, ShadowSamplingBinding> replacementBindings) =>
        AddReplacement(oldSet, shadowTexture, pointShadowTexture, _pointShadowTransientTexture, pointLightBuffer,
            replacements, replacementBindings);

    void AddReplacement(IGpuResourceSet oldSet, IGpuTexture shadowTexture, IGpuTexture pointShadowTexture,
        IGpuTexture pointShadowTransientTexture, IGpuBuffer pointLightBuffer,
        Dictionary<IGpuResourceSet, IGpuResourceSet> replacements,
        Dictionary<IGpuResourceSet, ShadowSamplingBinding> replacementBindings)
    {
        if (replacements.ContainsKey(oldSet)) return;
        if (!_shadowSamplingBindings.TryGetValue(oldSet, out ShadowSamplingBinding? binding))
            throw new InvalidOperationException("a live shadow-sampling set has no rebuild description.");
        IGpuResourceSet replacement = BuildShadowSamplingSet(
            binding, shadowTexture, pointShadowTexture, pointShadowTransientTexture, pointLightBuffer);
        replacements.Add(oldSet, replacement);
        replacementBindings.Add(replacement, binding);
    }

    IGpuResourceSet CreateShadowSamplingSet(IGpuResourceLayout layout, IGpuTexture shadowTexture,
        params IGpuBindableResource[] materialResources) =>
        CreateShadowSamplingSet(layout, shadowTexture, includesPointLights: false, materialResources);

    IGpuResourceSet CreateShadowSamplingSet(IGpuResourceLayout layout, IGpuTexture shadowTexture,
        bool includesPointLights, params IGpuBindableResource[] materialResources)
    {
        var binding = new ShadowSamplingBinding(layout, materialResources, includesPointLights);
        IGpuResourceSet set = BuildShadowSamplingSet(
            binding, shadowTexture, _pointShadowTexture, _pointShadowTransientTexture, _pointLightBuffer);
        _shadowSamplingBindings.Add(set, binding);
        return set;
    }

    /// <summary>Build a splat-terrain material resource set (set 1): the material's params UBO + the two 5-layer
    /// texture arrays (albedo, tangent-space normal) + a terrain (wrap/anisotropic) sampler + the two shadow
    /// atlases. Shared across every chunk using this material and owned by Scene3D, NOT per mesh.
    /// <paramref name="sampler"/> is the shared default one unless the material overrides its
    /// <see cref="TerrainSamplerConfig"/>, in which case the caller owns the one it passes. Tracked, which is the
    /// only way a splat set may be built: see the note in ModelRenderer.Splat.cs.</summary>
    internal IGpuResourceSet CreateTrackedSplatMaterialSet(IGpuBuffer paramsUbo, IGpuTexture albedoArray,
        IGpuTexture normalArray, IGpuSampler? sampler = null) =>
        CreateShadowSamplingSet(_splatMaterialLayout, _shadowMap.ShadowTexture,
            paramsUbo, albedoArray, normalArray, sampler ?? _terrainSampler);

    // Every receiver layout ends in the cascade atlas, shared sampler, base point atlas, and transient point atlas.
    IGpuResourceSet BuildShadowSamplingSet(ShadowSamplingBinding binding, IGpuTexture shadowTexture,
        IGpuTexture pointShadowTexture, IGpuTexture pointShadowTransientTexture, IGpuBuffer pointLightBuffer)
    {
        int pointLightOffset = binding.IncludesPointLights ? 2 : 0;
        var resources = new IGpuBindableResource[binding.MaterialResources.Length + pointLightOffset + 4];
        if (binding.IncludesPointLights)
        {
            resources[0] = binding.MaterialResources[0];
            resources[1] = pointLightBuffer;
            resources[2] = _pointLightClusterBuffer;
            binding.MaterialResources.AsSpan(1).CopyTo(resources.AsSpan(3));
        }
        else
        {
            binding.MaterialResources.CopyTo(resources, 0);
        }
        resources[^4] = shadowTexture;
        resources[^3] = _shadowMap.ShadowSampler;
        resources[^2] = pointShadowTexture;
        resources[^1] = pointShadowTransientTexture;
        return _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(binding.Layout, resources));
    }

    sealed class ShadowSamplingBinding
    {
        internal ShadowSamplingBinding(IGpuResourceLayout layout, IGpuBindableResource[] materialResources,
            bool includesPointLights)
        {
            Layout = layout;
            MaterialResources = materialResources;
            IncludesPointLights = includesPointLights;
        }

        internal IGpuResourceLayout Layout { get; }
        internal IGpuBindableResource[] MaterialResources { get; }
        internal bool IncludesPointLights { get; }
    }
}
