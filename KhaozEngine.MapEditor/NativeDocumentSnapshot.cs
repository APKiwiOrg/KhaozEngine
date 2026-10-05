using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEditor;

/// <summary>Detached native candidates and identity-preserving publication at transaction boundaries.</summary>
internal static class NativeDocumentSnapshot
{
    internal static MapDocument Clone(MapDocument document, MapDocRegistry registry)
    {
        var candidate = MapDocumentFile.LoadText(MapDocumentFile.SaveText(document, registry),
            new MapDocumentLoadOptions { Registry = registry });
        candidate.Tiles = document.Tiles;
        return candidate;
    }

    internal static void Publish(MapDocument target, MapDocument candidate)
    {
        target.Schema = candidate.Schema;
        target.FormatVersion = candidate.FormatVersion;
        target.Id = candidate.Id;
        target.DisplayName = candidate.DisplayName;
        target.Bounds = candidate.Bounds;
        target.TileSize = candidate.TileSize;
        target.Terrain = candidate.Terrain;
        target.ScatterLayers = candidate.ScatterLayers;
        target.CompanionLayers = candidate.CompanionLayers;
        target.Exclusions = candidate.Exclusions;
        target.ScatterOverrides = candidate.ScatterOverrides;
        target.Placements = candidate.Placements;
        target.Spawns = candidate.Spawns;
        target.PlayerSpawns = candidate.PlayerSpawns;
        target.Regions = candidate.Regions;
        target.TerrainOverrides = candidate.TerrainOverrides;
        target.Tiles = candidate.Tiles;
        target.PlayableBounds = candidate.PlayableBounds;
        target.NativeAssets = candidate.NativeAssets;
        target.NumericIdHighWaterMark = candidate.NumericIdHighWaterMark;
        target.ResolverIdentity = candidate.ResolverIdentity;
    }
}
