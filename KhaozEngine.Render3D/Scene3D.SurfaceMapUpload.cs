namespace KhaozEngine.Render3D;

/// <summary>Transactional surface-map upload through the material texture residency, and the matching release.</summary>
public sealed partial class Scene3D
{
    /// <summary>Upload each present material map, or take a reference to the texture already resident for its
    /// image identity, so an image shared by many parts, glbs or archetypes is one texture in this scene (see
    /// <see cref="ResidentMaterialTextureCount"/>). A failed upload releases every reference it already took and
    /// retires any texture it created. Absent maps retain invalid handles and use scene defaults when bound to a
    /// mesh. The emissive colour rides the bundle as <see cref="SurfaceMaps.EmissiveFactor"/>.</summary>
    /// <remarks>Unload meshes before releasing their maps with UnloadSurfaceMaps, or retain the maps until scene disposal.</remarks>
    public SurfaceMaps LoadSurfaceMaps(GltfMaterialMaps maps)
    {
        TextureHandle albedo = default, normal = default, roughness = default, emissive = default;
        TextureHandle Acquire(DecodedImage? image) => image is { } value ? AcquireMaterialTexture(value) : default;
        try
        {
            albedo = Acquire(maps.Albedo);
            normal = Acquire(maps.Normal);
            roughness = Acquire(maps.Roughness);
            emissive = Acquire(maps.Emissive);
            return new SurfaceMaps(albedo, normal, roughness, maps.AlphaCutoff, emissive, maps.EmissiveFactor);
        }
        catch
        {
            UnloadSurfaceMaps(new SurfaceMaps(albedo, normal, roughness, 0f, emissive, default));
            throw;
        }
    }

    /// <summary>Release an owned map bundle. Unload its meshes first. A resident map (every map
    /// <see cref="LoadSurfaceMaps"/> returns) releases one reference per slot, and the last reference retires the
    /// texture. A handle the residency does not track, such as the caller's own
    /// <see cref="LoadTexture(byte[],int,int,TextureMipPolicy)"/> texture placed in a bundle by hand, is retired
    /// once per distinct handle as before. Invalid handles never release the scene's default textures.
    /// A no-op after <see cref="Dispose"/> has run.</summary>
    public void UnloadSurfaceMaps(in SurfaceMaps maps)
    {
        if (_disposalComplete) return;
        Release(maps.Albedo, default, default, default);
        Release(maps.Normal, maps.Albedo, default, default);
        Release(maps.Roughness, maps.Albedo, maps.Normal, default);
        Release(maps.Emissive, maps.Albedo, maps.Normal, maps.Roughness);

        void Release(TextureHandle slot, TextureHandle a, TextureHandle b, TextureHandle c)
        {
            if (ReleaseMaterialTexture(slot)) return;
            // A caller texture shared by two slots of one hand-built bundle retires once.
            if (slot.Index == a.Index || slot.Index == b.Index || slot.Index == c.Index) return;
            RetireTexture(slot);
        }
    }
}
