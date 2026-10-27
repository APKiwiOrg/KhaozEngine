using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    readonly Stack<int> _freeUploadedGroundMaterials = new();

    internal long ReserveMeshOrder() => _slots.ReserveAllocationOrder();

    internal MeshHandle PublishUploadedMesh(SceneUploadPublication source, IGpuBuffer vertices, IGpuBuffer normals, IGpuBuffer indices,
        IGpuTexture? albedo, IGpuTexture? normal, IGpuTexture? roughness,
        IGpuTexture? groundAlbedo, IGpuBuffer? groundParams,
        List<IDisposable> owned, long meshOrder)
    {
        IGpuResourceSet? material = null, outline = null, cutout = null;
        int tileGround = -1;
        float alphaCutoff = source.AlphaCutoff;
        if (source.Ground)
        {
            var handle = LoadTileGroundMaterial(groundAlbedo!, groundParams!, source.Sampler, owned);
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
            tileGroundMaterial: tileGround, shadowCutout: cutout, meshOrder: meshOrder);
        _publishedResourceMeshes.Add(published);
        return published;
    }
    MeshHandle PublishMesh(GltfMesh source, IGpuBuffer vertices, IGpuBuffer normals, IGpuBuffer indices,
        IGpuResourceSet? material, IGpuResourceSet? outline, int splatMaterial = -1, float alphaCutoff = 0,
        int tileGroundMaterial = -1, IGpuResourceSet? shadowCutout = null, long meshOrder = 0)
        => PublishMesh(SceneUploadPublication.From(source), vertices, normals, indices, material, outline,
            splatMaterial, alphaCutoff, tileGroundMaterial, shadowCutout, meshOrder);

    MeshHandle PublishMesh(SceneUploadPublication source, IGpuBuffer vertices, IGpuBuffer normals, IGpuBuffer indices,
        IGpuResourceSet? material, IGpuResourceSet? outline, int splatMaterial = -1, float alphaCutoff = 0,
        int tileGroundMaterial = -1, IGpuResourceSet? shadowCutout = null, long meshOrder = 0)
    {
        int index = _slots.Alloc(out int generation, meshOrder);
        var mesh = new Mesh(vertices, normals, indices, source.IndexCount, source.IndexFormat, source.Bounds,
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
            if (value.TileGroundMaterial >= 0)
            {
                _tileGroundMaterials[value.TileGroundMaterial] = null;
                _freeUploadedGroundMaterials.Push(value.TileGroundMaterial);
            }
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
        // Only upload-owned slots are reusable. Legacy handles keep their historical indices, including
        // unloaded slots, so a stale public handle can never resolve a native resource's material.
        if (owned is not null && _freeUploadedGroundMaterials.TryPop(out int index))
        {
            _tileGroundMaterials[index] = new(albedo, ubo, set, selected);
            return new(index);
        }
        _tileGroundMaterials.Add(new(albedo, ubo, set, selected));
        return new(_tileGroundMaterials.Count - 1);
    }
}
