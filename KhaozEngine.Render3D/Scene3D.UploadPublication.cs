using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    internal MeshHandle PublishUploadedMesh(GltfMesh source, IGpuBuffer vertices, IGpuBuffer normals, IGpuBuffer indices,
        IGpuTexture? albedo, IGpuTexture? normal, IGpuTexture? roughness, float alphaCutoff,
        SceneGroundMaterialData? ground, IGpuTexture? groundAlbedo, IGpuBuffer? groundParams,
        List<IDisposable> owned)
    {
        IGpuResourceSet? material = null, outline = null, cutout = null;
        int tileGround = -1;
        if (ground is not null)
        {
            var handle = LoadTileGroundMaterial(groundAlbedo!, groundParams!, ground.Sampler, owned);
            tileGround = handle.ListIndex;
        }
        else
        {
            if (albedo is not null || normal is not null || roughness is not null)
            { material = _model.CreateMaterialSet(albedo, normal, roughness); owned.Add(material); }
            if (albedo is not null)
            { outline = _targetOutlines.CreateMaterialSet(albedo); owned.Add(outline); }
            if (albedo is not null && alphaCutoff > 0)
            { cutout = _model.ShadowMap.CreateCutoutMaterialSet(albedo); owned.Add(cutout); }
        }
        var published = PublishMesh(source, vertices, normals, indices, material, outline, alphaCutoff: alphaCutoff,
            tileGroundMaterial: tileGround, shadowCutout: cutout);
        _publishedResourceMeshes.Add(published);
        return published;
    }
    MeshHandle PublishMesh(GltfMesh source, IGpuBuffer vertices, IGpuBuffer normals, IGpuBuffer indices,
        IGpuResourceSet? material, IGpuResourceSet? outline, int splatMaterial = -1, float alphaCutoff = 0,
        int tileGroundMaterial = -1, IGpuResourceSet? shadowCutout = null)
    {
        var bounds = MeshBounds.FromVertices(source.Vertices);
        int index = _slots.Alloc(out int generation);
        var mesh = new Mesh(vertices, normals, indices, source.Indices32.Length, source.IndexFormat, bounds,
            material, outline, splatMaterial, alphaCutoff, tileGroundMaterial, shadowCutout);
        if (index < _meshes.Count) _meshes[index] = mesh;
        else _meshes.Add(mesh);
        return new(index, generation);
    }
    internal void PublishWaterDomain(MeshHandle mesh, IGpuBuffer clip) => _water.RegisterPublishedDomain(mesh, clip);
    internal void DetachUploadedResource(MeshHandle mesh, FoliageBatch? foliage)
    {
        if (mesh.Generation > 0 && _slots.IsValid(mesh.Index, mesh.Generation))
        {
            var value = _meshes[mesh.Index]!.Value;
            _publishedResourceMeshes.Remove(mesh);
            _slots.Free(mesh.Index, mesh.Generation);
            ForgetWaterAndInteriorRelevance(mesh); _silhouetteRelevance.Forget(mesh);
            if (value.TileGroundMaterial >= 0) _tileGroundMaterials[value.TileGroundMaterial] = null;
            _meshes[mesh.Index] = null;
        }
        if (foliage is not null)
        {
            foliage.IsDisposed = true; foliage.Buffer = null; foliage.Pending = null;
            _foliageBatches.Remove(foliage);
            _foliageDraws.RemoveAll(draw => ReferenceEquals(draw.Batch, foliage));
            _foliageShadowDraws.RemoveAll(draw => ReferenceEquals(draw.Batch, foliage));
            ForgetPreparedFoliagePointSources(foliage);
            foliage.PreparedPointStream = null;
        }
    }
    TileGroundMaterialHandle LoadTileGroundMaterial(IGpuTexture albedo, IGpuBuffer ubo,
        TerrainSamplerConfig? sampler, List<IDisposable>? owned)
    {
        IGpuSampler? selected = sampler.HasValue ? _model.CreateTerrainSampler(sampler.Value) : null;
        if (selected is not null) owned?.Add(selected);
        var set = selected is null ? _model.CreateTileGroundMaterialSet(ubo, albedo)
            : _model.CreateTileGroundMaterialSet(ubo, albedo, selected);
        owned?.Add(set);
        _tileGroundMaterials.Add(new(albedo, ubo, set, selected));
        return new(_tileGroundMaterials.Count - 1);
    }
}
