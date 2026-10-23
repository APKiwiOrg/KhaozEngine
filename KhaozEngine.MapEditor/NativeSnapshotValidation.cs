using System;
using System.IO;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Storage;

namespace KhaozEngine.MapEditor;

/// <summary>Validates snapshot roots while leaving effective relationships to the publication admission gate.</summary>
internal static class NativeSnapshotValidation
{
    internal static void Validate(MapDocument document, MapDocRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Tiles is { IsPartial: true })
            throw new MapDocumentException("Native snapshots require a complete document, not a windowed document.");
        var root = new MapDocument
        {
            Schema = document.Schema,
            FormatVersion = document.FormatVersion,
            Id = document.Id,
            DisplayName = document.DisplayName,
            Bounds = document.Bounds,
            TileSize = document.TileSize,
            Terrain = document.Terrain,
            ScatterLayers = document.ScatterLayers,
            CompanionLayers = document.CompanionLayers,
            Exclusions = document.Exclusions,
            ScatterOverrides = document.ScatterOverrides,
            Placements = document.Placements,
            Spawns = document.Spawns,
            PlayerSpawns = document.PlayerSpawns,
            Regions = document.Regions,
            TerrainOverrides = document.TerrainOverrides,
            PlayableBounds = document.PlayableBounds,
            NativeAssets = document.NativeAssets,
            NumericIdHighWaterMark = document.NumericIdHighWaterMark,
            ResolverIdentity = document.ResolverIdentity,
            SupportRecipe = document.SupportRecipe,
            Prefabs = document.Prefabs,
            BuildingIntent = document.BuildingIntent,
            NativeResourceTransforms = document.NativeResourceTransforms,
        };
        // A root view has no resident payloads. Their relationships are checked when publishing the candidate.
        foreach (var surface in document.Surfaces.Refs)
            root.Surfaces.Refs.Add(surface with { IndoorSpan = null });
        var errors = MapDocumentValidator.Validate(root, registry);
        if (errors.Count != 0)
            throw new MapDocumentException("refusing to snapshot an invalid map document:\n  " + string.Join("\n  ", errors));
        MapSurfaceSaveGuard.Whole(root.Surfaces);
        // Retain the root DTO codecs' finite-value and resource checks without a whole-write preparation or reload.
        JsonSerializer.Serialize(Stream.Null, root, MapDocumentFile.CreateOptions(registry, write: true));
    }
}
