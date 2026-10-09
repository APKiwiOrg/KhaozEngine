using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapEditor;

/// <summary>Detached native candidates and identity-preserving publication at transaction boundaries.</summary>
internal static class NativeDocumentSnapshot
{
    internal static MapDocument Clone(MapDocument document, MapDocRegistry registry)
    {
        if (document.Tiles is { IsPartial: true })
            throw new MapDocumentException("Native snapshots require a complete document, not a windowed document.");
        // Snapshotting a complete tiled world is independent of the small-file embedding limit.
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
        };
        var metadata = new MapSurfaceSet();
        // Metadata has no payload references to resolve during the temporary root round trip.
        foreach (MapSurfaceRef surface in document.Surfaces.Refs)
            metadata.Refs.Add(surface with { IndoorSpan = null });
        root.Surfaces = metadata;
        var candidate = MapDocumentFile.LoadText(MapDocumentFile.SaveText(root, registry),
            new MapDocumentLoadOptions { Registry = registry });
        candidate.Surfaces = document.Surfaces.Clone();
        candidate.Tiles = document.Tiles;
        return candidate;
    }

    internal static void PublishWriteSet(MapDocument target, MapDocument candidate, MapNativeWriteSet writeSet)
    {
        // Records, spaces, owner dependencies and material uses live in their named patch payloads.
        foreach (MapPatchKey key in writeSet.Patches)
        {
            if (candidate.Surfaces.Patches.TryGetValue(key, out MapSurfacePatch? patch))
                target.Surfaces.Patches[key] = patch;
            else target.Surfaces.Patches.Remove(key);
        }
        string[] order = candidate.Surfaces.Refs.Select(s => s.Id).ToArray();
        foreach (string id in writeSet.SurfaceIds)
        {
            MapSurfaceRef? surface = candidate.Surfaces.Refs.Find(s => s.Id == id);
            if (surface is not null) PlaceRef(target.Surfaces.Refs, surface, order);
            else if (target.Surfaces.Refs.FindIndex(s => s.Id == id) is var oldIndex and >= 0)
                target.Surfaces.Refs.RemoveAt(oldIndex);
        }
        if (writeSet.Placements)
        {
            target.Placements = candidate.Placements;
            target.NumericIdHighWaterMark = candidate.NumericIdHighWaterMark;
        }
    }

    /// <summary>Replaces a ref in place, or inserts it before the first existing ref that follows it in
    /// <paramref name="order"/>, so a restored ref returns to its original position.</summary>
    internal static void PlaceRef(List<MapSurfaceRef> refs, MapSurfaceRef surface, IReadOnlyList<string> order)
    {
        int index = refs.FindIndex(s => s.Id == surface.Id);
        if (index >= 0)
        {
            refs[index] = surface;
            return;
        }
        int rank = IndexOf(order, surface.Id);
        int next = rank < 0 ? -1 : refs.FindIndex(s => IndexOf(order, s.Id) > rank);
        if (next < 0) refs.Add(surface);
        else refs.Insert(next, surface);
    }

    static int IndexOf(IReadOnlyList<string> order, string id)
    {
        for (int i = 0; i < order.Count; i++)
            if (string.Equals(order[i], id, StringComparison.Ordinal)) return i;
        return -1;
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
        target.SupportRecipe = candidate.SupportRecipe;
        target.Surfaces = candidate.Surfaces;
    }
}
