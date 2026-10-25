using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.Primitives;

namespace KhaozEngine.TileWorld;

/// <summary>Detached terrain and material values for CPU region builds.</summary>
internal static class TileWorldBuildSnapshot
{
    internal static TileWorldDocument CaptureGround(TileWorldDocument document, RegionCoord region,
        bool captureOpticalProvenance = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        var snapshot = new TileWorldDocument
        {
            Id = document.Id,
            DisplayName = document.DisplayName,
            TileSize = document.TileSize,
            PlaneCount = document.PlaneCount,
            PlaneHeight = document.PlaneHeight,
        };
        for (int rz = region.Rz - 1; rz <= region.Rz + 1; rz++)
            for (int rx = region.Rx - 1; rx <= region.Rx + 1; rx++)
            {
                var coord = new RegionCoord(rx, rz);
                if (document.GetRegion(coord) is not { } source) continue;
                TileRegion copy = snapshot.GetOrCreateRegion(coord);
                for (int plane = 0; plane < document.PlaneCount; plane++)
                    CopyPlane(source.Planes[plane], copy.Planes[plane]);
                copy.Dirty = false;
            }
        // Opted-in strict reads must still see known-unloaded candidates before a loaded fallback.
        // Legacy workers do not need this optical manifest. No worker read can perform I/O.
        if (captureOpticalProvenance)
            foreach (var entry in document.UnloadedRegionHashes)
                snapshot.UnloadedRegionHashes.Add(entry.Key, entry.Value);
        return snapshot;
    }

    internal static TileWorldCatalogs CaptureMaterials(TileWorldCatalogs catalogs)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        return catalogs.CopyMaterials();
    }

    internal static TileWorldDocument CaptureInteriors(TileWorldDocument document, InteriorWindow window)
    {
        ArgumentNullException.ThrowIfNull(document);
        _ = window.NeedsRecentre(window.Min, 64f);
        var snapshot = new TileWorldDocument
        {
            Id = document.Id,
            DisplayName = document.DisplayName,
            TileSize = document.TileSize,
            PlaneCount = document.PlaneCount,
            PlaneHeight = document.PlaneHeight,
        };
        // H2 derives the encoding from the whole source. Keep all resolved height and indoor dependencies,
        // including distant storeys and roofs whose footprints overlap the window from anchors outside it.
        foreach (TileRegion source in document.Regions.Values)
        {
            TileRegion copy = snapshot.GetOrCreateRegion(source.Coord);
            for (int plane = 0; plane < document.PlaneCount; plane++) CopyPlane(source.Planes[plane], copy.Planes[plane]);
        }
        foreach (TileObject o in document.AllObjects())
            snapshot.AddObjectWithId(o.Id, o.ArchetypeId, o.X, o.Z, o.Plane, o.Rotation, o.Tags);
        foreach (TileRegion region in snapshot.Regions.Values) region.Dirty = false;
        return snapshot;
    }

    static readonly JsonSerializerOptions InteriorCatalogJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static TileWorldCatalogs CaptureInteriorCatalogs(TileWorldCatalogs catalogs)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        // The catalog loader owns construction and validation. Round-trip only the relevant archetypes to
        // detach their mutable fields and nested lists without exposing the catalog's mutable dictionaries.
        var archetypes = catalogs.Archetypes.Values.Where(a => a.IsRoof ||
            a.CollisionKind is TileCollisionKind.Wall or TileCollisionKind.WallCorner or TileCollisionKind.Diagonal);
        string json = JsonSerializer.Serialize(new { archetypes }, InteriorCatalogJson);
        return TileWorldCatalogs.Merge(catalogs.CopyMaterials(), TileWorldCatalogs.LoadJson(json, "interior build"));
    }

    static void CopyPlane(TilePlaneData source, TilePlaneData destination)
    {
        destination.Heights = (short[]?)source.Heights?.Clone();
        destination.Underlay = (ushort[]?)source.Underlay?.Clone();
        destination.Overlay = (ushort[]?)source.Overlay?.Clone();
        destination.OverlayShape = (byte[]?)source.OverlayShape?.Clone();
        destination.OverlayRotation = (byte[]?)source.OverlayRotation?.Clone();
        destination.Settings = (byte[]?)source.Settings?.Clone();
    }
}
