using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    void EnsurePointLightCapacity()
    {
        int required = _lights.Count;
        EnsurePointSlotUniformCapacity(required);
        _model.EnsurePointShadowSlotCapacity(required);
        if (!_model.RequiresPointLightGrowth(required)) return;
        var liveSets = new List<IGpuResourceSet>();
        CollectLiveMaterialSets(liveSets);
        _model.EnsurePointLightCapacity(required, liveSets, CommitMaterialSets);
    }

    internal bool ReplaceShadowLayout(int resolution, int cascadeCount) =>
        ReplaceShadowLayoutWithResult(resolution, cascadeCount) == ShadowLayoutReplacementResult.Replaced;

    internal ShadowLayoutReplacementResult ReplaceShadowLayoutWithResult(int resolution, int cascadeCount)
    {
        if (_model.ShadowMap.MatchesLayout(resolution, cascadeCount))
            return ShadowLayoutReplacementResult.Unchanged;

        var liveSets = new List<IGpuResourceSet>();
        CollectLiveMaterialSets(liveSets);

        bool replaced = _model.ReplaceShadowLayout(
            resolution,
            cascadeCount,
            Post.Quality.Shadows,
            liveSets,
            CommitMaterialSets,
            () => _shadowPassRendered = false);
        return replaced ? ShadowLayoutReplacementResult.Replaced : ShadowLayoutReplacementResult.Failed;
    }

    /// <summary>Every live resource set that carries a shadow binding, which is what a texture swap has to rebuild
    /// and hand back. One definition, because the cascade atlas replacement and the point-shadow atlas bind change
    /// the same sets for the same reason, and a set missing from one of the two lists would keep binding a freed
    /// texture.</summary>
    void CollectLiveMaterialSets(List<IGpuResourceSet> into)
    {
        foreach (Mesh? mesh in _meshes)
            if (mesh is { MaterialSet: { } set }) into.Add(set);
        foreach (SkinnedMeshEntry? mesh in _skinnedMeshes)
        {
            if (mesh?.MaterialSet is { } materialSet) into.Add(materialSet);
            if (mesh?.SkinnedMaterialSet is { } skinnedMaterialSet) into.Add(skinnedMaterialSet);
        }
        foreach (SplatMaterialEntry? material in _splatMaterials)
            if (material is not null) into.Add(material.Set);
        foreach (TileGroundMaterialEntry? material in _tileGroundMaterials)
            if (material is not null) into.Add(material.Set);
    }

    void CommitMaterialSets(Func<IGpuResourceSet, IGpuResourceSet> replacementFor)
    {
        for (int i = 0; i < _meshes.Count; i++)
        {
            if (_meshes[i] is not { MaterialSet: { } oldSet } mesh) continue;
            _meshes[i] = new Mesh(mesh.Vb, mesh.OutlineNormalVb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat,
                in mesh.Bounds, replacementFor(oldSet), mesh.OutlineMaterialSet, mesh.SplatMaterial, mesh.AlphaCutoff,
                mesh.TileGroundMaterial);
        }

        for (int i = 0; i < _skinnedMeshes.Count; i++)
        {
            if (_skinnedMeshes[i] is not { } mesh) continue;
            IGpuResourceSet? materialSet = mesh.MaterialSet is null ? null : replacementFor(mesh.MaterialSet);
            IGpuResourceSet? skinnedMaterialSet = mesh.SkinnedMaterialSet is null
                ? null
                : replacementFor(mesh.SkinnedMaterialSet);
            _skinnedMeshes[i] = new SkinnedMeshEntry(mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat,
                materialSet, skinnedMaterialSet, mesh.InverseBind, in mesh.Bounds);
        }

        foreach (SplatMaterialEntry? material in _splatMaterials)
            if (material is not null) material.ReplaceSet(replacementFor(material.Set));
        foreach (TileGroundMaterialEntry? material in _tileGroundMaterials)
            if (material is not null) material.ReplaceSet(replacementFor(material.Set));
    }
}

internal enum ShadowLayoutReplacementResult
{
    Unchanged,
    Replaced,
    Failed,
}

public sealed partial class ShadowSettings
{
    internal void CommitAtlasReplacement(int resolution, int cascadeCount)
    {
        if (!_atlasCommitted)
            throw new InvalidOperationException("a shadow atlas must be committed before its layout can be replaced.");
        _shadowMapResolution = resolution;
        _shadowCascadeCount = cascadeCount;
    }
}
