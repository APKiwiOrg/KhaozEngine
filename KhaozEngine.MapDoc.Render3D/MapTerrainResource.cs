using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

public enum MapTerrainFindingKind { NormalMapNotConsumed, RoughnessMapNotConsumed }
public sealed record MapTerrainFinding(MapTerrainFindingKind Kind, ushort MaterialId, string ResourceId);

/// <summary>A prepared-input-owned albedo layer. Terrain deliberately consumes no other map channel.</summary>
public sealed class MapTerrainLayer
{
    readonly MapRenderMaterial _material;
    public ushort MaterialId { get; }
    public string ResourceId => _material.ResourceId;
    public Vector4 Tint => _material.BaseColour;
    public float TilesPerMetre => .5f;
    public DecodedImage? Albedo => BorrowAlbedo is { } image ? new(image.Rgba.ToArray(), image.Width, image.Height) : null;
    internal DecodedImage? BorrowAlbedo => _material.BorrowMaps.Albedo;
    internal MapTerrainLayer(ushort id, MapRenderMaterial material) { MaterialId = id; _material = material; }
}

/// <summary>Ordered numeric material IDs and albedo layers for Render3D's ground array. A null albedo is a solid
/// white layer at this set's dimensions, tinted by Tint. Pixel ownership stays with the batch's retained input.</summary>
public sealed class MapTerrainMaterialSet
{
    public IReadOnlyList<MapTerrainLayer> Layers { get; }
    public int Width { get; }
    public int Height { get; }
    public float BaseSpecularStrength => .15f;
    internal MapTerrainMaterialSet(IEnumerable<MapTerrainLayer> layers)
    {
        var rows = layers.ToArray();
        if (rows.Length is < 1 or > TileGroundMaterialConfig.MaxMaterials || rows.Select(l => l.MaterialId).Distinct().Count() != rows.Length)
            throw new ArgumentException("Terrain material sets require distinct admitted layers.", nameof(layers));
        Layers = Array.AsReadOnly(rows);
        var images = rows.Select(l => l.BorrowAlbedo).Where(i => i.HasValue).Select(i => i!.Value).ToArray();
        Width = images.Length == 0 ? 1 : images[0].Width;
        Height = images.Length == 0 ? 1 : images[0].Height;
        if (images.Any(i => i.Width != Width || i.Height != Height))
            throw new MapRenderAssetRefusal(MapRenderAssetStatus.Unsupported, "Terrain albedo layers require equal dimensions.");
    }
}

/// <summary>A canonical-surface mesh for the format-neutral Render3D ground pipeline. Color holds four underlay
/// weights, Uv.xy and Tangent.xy their ordered material slots, Tangent.z is brightness (one), and Tangent.w is
/// overlay slot plus one. Zero Tangent.w selects hard coverage. Otherwise the overlay owns one minus weight sum.
/// FaceKeys is triangle-aligned provenance, including every render-only feather subdivision.</summary>
public sealed class MapTerrainResource : MapResourcePayload
{
    readonly GltfMesh _mesh;
    public GltfMesh Mesh => MapRenderCopies.Mesh(_mesh);
    public MapTerrainMaterialSet Materials { get; }
    public IReadOnlyList<MapFaceKey> FaceKeys { get; }
    internal GltfMesh BorrowMesh => _mesh;
    internal MapTerrainResource(GltfMesh mesh, MapTerrainMaterialSet materials, IReadOnlyList<MapFaceKey> faceKeys)
    {
        _mesh = mesh; Materials = materials; FaceKeys = Array.AsReadOnly(faceKeys.ToArray());
        if (mesh.TriangleCount != FaceKeys.Count) throw new ArgumentException("Terrain provenance must cover every render triangle.", nameof(faceKeys));
    }
}
