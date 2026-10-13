namespace KhaozEngine.Render3D;

/// <summary>Transactional surface-map upload and owning release through the scene's retirement queue.</summary>
public sealed partial class Scene3D
{
    /// <summary>Upload each present material map. A failed upload retires every map it already created.
    /// Absent maps retain invalid handles and use scene defaults when bound to a mesh.</summary>
    /// <remarks>Unload meshes before releasing their maps with UnloadSurfaceMaps, or retain the maps until scene disposal.</remarks>
    public SurfaceMaps LoadSurfaceMaps(GltfMaterialMaps maps)
    {
        TextureHandle albedo = default, normal = default, roughness = default;
        TextureHandle Upload(DecodedImage? image) =>
            image is { } value ? LoadTexture(value.Rgba, value.Width, value.Height) : default;
        try
        {
            albedo = Upload(maps.Albedo);
            normal = Upload(maps.Normal);
            roughness = Upload(maps.Roughness);
            return new SurfaceMaps(albedo, normal, roughness, maps.AlphaCutoff);
        }
        catch
        {
            UnloadSurfaceMaps(new SurfaceMaps(albedo, normal, roughness));
            throw;
        }
    }

    /// <summary>Retire each distinct valid texture in an owned map bundle. Unload its meshes first.
    /// Invalid handles never release the scene's default textures.</summary>
    public void UnloadSurfaceMaps(in SurfaceMaps maps)
    {
        UnloadTexture(maps.Albedo);
        if (maps.Normal.Index != maps.Albedo.Index) UnloadTexture(maps.Normal);
        if (maps.Roughness.Index != maps.Albedo.Index && maps.Roughness.Index != maps.Normal.Index)
            UnloadTexture(maps.Roughness);
    }
}
