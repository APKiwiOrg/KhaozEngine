using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    internal bool ReplaceShadowLayout(int resolution, int cascadeCount)
    {
        if (_model.ShadowMap.MatchesLayout(resolution, cascadeCount)) return false;

        var liveSets = new List<IGpuResourceSet>();
        foreach (Mesh? mesh in _meshes)
            if (mesh is { MaterialSet: { } set }) liveSets.Add(set);
        foreach (SkinnedMeshEntry? mesh in _skinnedMeshes)
        {
            if (mesh?.MaterialSet is { } materialSet) liveSets.Add(materialSet);
            if (mesh?.SkinnedMaterialSet is { } skinnedMaterialSet) liveSets.Add(skinnedMaterialSet);
        }
        foreach (SplatMaterialEntry? material in _splatMaterials)
            if (material is not null) liveSets.Add(material.Set);
        foreach (TileGroundMaterialEntry? material in _tileGroundMaterials)
            if (material is not null) liveSets.Add(material.Set);

        return _model.ReplaceShadowLayout(
            resolution,
            cascadeCount,
            Post.Quality.Shadows,
            liveSets,
            CommitMaterialSets,
            () => _shadowPassRendered = false);
    }

    void CommitMaterialSets(Func<IGpuResourceSet, IGpuResourceSet> replacementFor)
    {
        for (int i = 0; i < _meshes.Count; i++)
        {
            if (_meshes[i] is not { MaterialSet: { } oldSet } mesh) continue;
            _meshes[i] = new Mesh(mesh.Vb, mesh.Ib, mesh.IndexCount, mesh.IndexFormat, in mesh.Bounds,
                replacementFor(oldSet), mesh.SplatMaterial, mesh.AlphaCutoff, mesh.TileGroundMaterial);
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
