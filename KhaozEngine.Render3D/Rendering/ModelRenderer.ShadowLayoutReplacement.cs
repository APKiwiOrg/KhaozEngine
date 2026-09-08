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
            AddReplacement(_defaultSet, graph.ShadowTexture, replacements, replacementBindings);
            AddReplacement(_skinnedDefaultFragSet, graph.ShadowTexture, replacements, replacementBindings);
            foreach (IGpuResourceSet set in liveMaterialSets)
                AddReplacement(set, graph.ShadowTexture, replacements, replacementBindings);
        }
        catch
        {
            foreach (IGpuResourceSet set in replacements.Values) set.Dispose();
            graph?.Dispose();
            return false;
        }

        IGpuResourceSet oldDefaultSet = _defaultSet;
        IGpuResourceSet oldSkinnedDefaultFragSet = _skinnedDefaultFragSet;
        commitMaterialSets(old => replacements[old]);
        _defaultSet = replacements[oldDefaultSet];
        _skinnedDefaultFragSet = replacements[oldSkinnedDefaultFragSet];
        ShadowMapRenderer.ShadowLayoutReplacement oldGraph = _shadowMap.CommitReplacement(graph);
        settings.CommitAtlasReplacement(graph.Resolution, graph.CascadeCount);
        invalidateShadowDepth();

        foreach ((IGpuResourceSet oldSet, IGpuResourceSet newSet) in replacements)
        {
            _shadowSamplingBindings.Remove(oldSet);
            _shadowSamplingBindings.Add(newSet, replacementBindings[newSet]);
            oldSet.Dispose();
        }
        oldGraph.Dispose();
        return true;
    }

    void AddReplacement(IGpuResourceSet oldSet, IGpuTexture shadowTexture,
        Dictionary<IGpuResourceSet, IGpuResourceSet> replacements,
        Dictionary<IGpuResourceSet, ShadowSamplingBinding> replacementBindings)
    {
        if (replacements.ContainsKey(oldSet)) return;
        if (!_shadowSamplingBindings.TryGetValue(oldSet, out ShadowSamplingBinding? binding))
            throw new InvalidOperationException("a live shadow-sampling set has no rebuild description.");
        IGpuResourceSet replacement = BuildShadowSamplingSet(binding, shadowTexture);
        replacements.Add(oldSet, replacement);
        replacementBindings.Add(replacement, binding);
    }

    IGpuResourceSet CreateShadowSamplingSet(IGpuResourceLayout layout, IGpuTexture shadowTexture,
        params IGpuBindableResource[] materialResources)
    {
        var binding = new ShadowSamplingBinding(layout, materialResources);
        IGpuResourceSet set = BuildShadowSamplingSet(binding, shadowTexture);
        _shadowSamplingBindings.Add(set, binding);
        return set;
    }

    internal IGpuResourceSet CreateTrackedSplatMaterialSet(IGpuBuffer paramsUbo, IGpuTexture albedoArray,
        IGpuTexture normalArray, IGpuSampler? sampler = null) =>
        CreateShadowSamplingSet(_splatMaterialLayout, _shadowMap.ShadowTexture,
            paramsUbo, albedoArray, normalArray, sampler ?? _terrainSampler);

    IGpuResourceSet BuildShadowSamplingSet(ShadowSamplingBinding binding, IGpuTexture shadowTexture)
    {
        var resources = new IGpuBindableResource[binding.MaterialResources.Length + 2];
        binding.MaterialResources.CopyTo(resources, 0);
        resources[^2] = shadowTexture;
        resources[^1] = _shadowMap.ShadowSampler;
        return _gd.Factory.CreateResourceSet(new GpuResourceSetDescription(binding.Layout, resources));
    }

    sealed class ShadowSamplingBinding
    {
        internal ShadowSamplingBinding(IGpuResourceLayout layout, IGpuBindableResource[] materialResources)
        {
            Layout = layout;
            MaterialResources = materialResources;
        }

        internal IGpuResourceLayout Layout { get; }
        internal IGpuBindableResource[] MaterialResources { get; }
    }
}
