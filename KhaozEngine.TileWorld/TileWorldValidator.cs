using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.TileWorld;

/// <summary>One validation finding. <see cref="Code"/> is stable and greppable, <see cref="Message"/> is for humans.</summary>
public sealed record TileWorldIssue(string Code, string Message, RegionCoord? Region, TileCoord? Tile);

/// <summary>Semantic validation of a document against its catalogs: every id resolves, every plane is in
/// range, every footprint lands on existing regions, ids and marker names are unique. Runs at save in the
/// tools and at load on the heads, so a dangling id fails in the editor, not at boot. The codes are stable,
/// so callers may branch on them: <c>header.planeCount</c>, <c>header.tileSize</c>, <c>header.planeHeight</c>,
/// <c>region.planeCount</c>, <c>material.missing</c>, <c>overlay.shape</c>, <c>archetype.missing</c>,
/// <c>object.plane</c>, <c>object.footprint</c>, <c>object.duplicateId</c>, <c>object.region</c>,
/// <c>marker.plane</c>, <c>marker.duplicateName</c>, <c>marker.region</c>,
/// <c>foliage.archetype</c>, <c>foliage.underlay</c>.</summary>
public static class TileWorldValidator
{
    /// <summary>Every issue in the document, header first and then region by region in a stable order.</summary>
    public static IReadOnlyList<TileWorldIssue> Validate(TileWorldDocument doc, TileWorldCatalogs catalogs)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(catalogs);
        var issues = new List<TileWorldIssue>();
        if (doc.PlaneCount < 1) issues.Add(new("header.planeCount", $"planeCount {doc.PlaneCount} must be at least 1", null, null));
        if (!(doc.TileSize > 0f)) issues.Add(new("header.tileSize", $"tileSize {doc.TileSize} must be positive", null, null));
        if (!(doc.PlaneHeight >= 0f)) issues.Add(new("header.planeHeight", $"planeHeight {doc.PlaneHeight} must not be negative", null, null));

        var seenIds = new HashSet<long>();
        var seenMarkers = new HashSet<string>(StringComparer.Ordinal);
        foreach (TileFoliageLayer layer in doc.FoliageLayers)
        {
            foreach (TileFoliageArchetype archetype in layer.Archetypes)
                if (catalogs.Archetype(archetype.Id) is null)
                    issues.Add(new("foliage.archetype",
                        $"foliage layer '{layer.Id}' references archetype '{archetype.Id}', which is not in the catalog",
                        null, null));
            foreach (ushort underlay in layer.AllowedUnderlays)
                if (catalogs.Material(underlay) is null)
                    issues.Add(new("foliage.underlay",
                        $"foliage layer '{layer.Id}' allows underlay {underlay}, which is not in the catalog",
                        null, null));
        }
        foreach (TileRegion region in doc.Regions.Values.OrderBy(r => r.Coord.Rz).ThenBy(r => r.Coord.Rx))
        {
            // A region allocates its planes once, at construction. A later edit of the document's plane count
            // leaves the already-loaded regions behind, and every plane-indexed read past this point would
            // then be reading a shorter array than the header advertises.
            if (region.Planes.Length != doc.PlaneCount)
                issues.Add(new("region.planeCount", $"region {region.Coord} has {region.Planes.Length} planes, the world has {doc.PlaneCount}", region.Coord, null));
            ValidateLayers(catalogs, region, issues);
            foreach (TileObject o in region.Objects) ValidateObject(doc, catalogs, region, o, seenIds, issues);
            foreach (TileMarker m in region.Markers)
            {
                if ((uint)m.Plane >= (uint)doc.PlaneCount)
                    issues.Add(new("marker.plane", $"marker '{m.Name}' is on plane {m.Plane}, the world has {doc.PlaneCount}", region.Coord, m.Coord));
                if (!region.Coord.Rect.Contains(m.X, m.Z))
                    issues.Add(new("marker.region", $"marker '{m.Name}' at ({m.X}, {m.Z}) is stored in region {region.Coord} but lies in {RegionCoord.Of(m.X, m.Z)}", region.Coord, m.Coord));
                if (!seenMarkers.Add(m.Name))
                    issues.Add(new("marker.duplicateName", $"marker name '{m.Name}' is used more than once", region.Coord, m.Coord));
            }
        }
        return issues;
    }

    /// <summary>Validates the whole document and, when anything is wrong, throws once quoting the first five
    /// issues and the total count.</summary>
    public static void ValidateOrThrow(TileWorldDocument doc, TileWorldCatalogs catalogs)
    {
        IReadOnlyList<TileWorldIssue> issues = Validate(doc, catalogs);
        if (issues.Count == 0) return;
        string head = string.Join(" | ", issues.Take(5).Select(i => $"[{i.Code}] {i.Message}"));
        throw new TileWorldException($"world '{doc.Id}' has {issues.Count} validation issues: {head}");
    }

    static void ValidateLayers(TileWorldCatalogs catalogs, TileRegion region, List<TileWorldIssue> issues)
    {
        for (int p = 0; p < region.Planes.Length; p++)
        {
            TilePlaneData d = region.Planes[p];
            var reported = new HashSet<ushort>();
            void Check(ushort id, string layer, int index)
            {
                if (id == 0 || catalogs.Material(id) is not null || !reported.Add(id)) return;
                TileCoord t = TileAt(region.Coord, index, p);
                issues.Add(new("material.missing", $"{layer} material {id} is not in the catalog (first at local ({t.LocalX}, {t.LocalZ}) plane {p})", region.Coord, t));
            }
            if (d.Underlay is not null) for (int i = 0; i < d.Underlay.Length; i++) Check(d.Underlay[i], "underlay", i);
            if (d.Overlay is not null) for (int i = 0; i < d.Overlay.Length; i++) Check(d.Overlay[i], "overlay", i);
            if (d.OverlayShape is not null)
            {
                for (int i = 0; i < d.OverlayShape.Length; i++)
                {
                    if (d.OverlayShape[i] > (byte)TileOverlayShape.CornerThreeQuarter)
                    {
                        TileCoord t = TileAt(region.Coord, i, p);
                        issues.Add(new("overlay.shape", $"overlay shape {d.OverlayShape[i]} at local ({t.LocalX}, {t.LocalZ}) plane {p} is not a known shape", region.Coord, t));
                        break;
                    }
                }
            }
        }
    }

    /// <summary>The world tile a flat layer index addresses, so an issue can point an editor at the offending
    /// tile rather than at a region and a number the caller would have to decode.</summary>
    static TileCoord TileAt(RegionCoord c, int index, int plane) =>
        new(c.OriginX + (index % TileRegion.Size), c.OriginZ + (index / TileRegion.Size), plane);

    static void ValidateObject(TileWorldDocument doc, TileWorldCatalogs catalogs, TileRegion region, TileObject o, HashSet<long> seenIds, List<TileWorldIssue> issues)
    {
        if (!seenIds.Add(o.Id))
            issues.Add(new("object.duplicateId", $"object id {o.Id} is used more than once", region.Coord, o.Coord));
        if ((uint)o.Plane >= (uint)doc.PlaneCount)
            issues.Add(new("object.plane", $"object {o.Id} ('{o.ArchetypeId}') is on plane {o.Plane}, the world has {doc.PlaneCount}", region.Coord, o.Coord));
        if (!region.Coord.Rect.Contains(o.X, o.Z))
            issues.Add(new("object.region", $"object {o.Id} at ({o.X}, {o.Z}) is stored in region {region.Coord} but lies in {RegionCoord.Of(o.X, o.Z)}", region.Coord, o.Coord));
        // Content reaches here straight off disk, so a region file carrying "archetypeId": null lands as a
        // null string on a non-nullable property. Validate reports bad content, it never throws on it.
        if (string.IsNullOrWhiteSpace(o.ArchetypeId))
        {
            issues.Add(new("archetype.missing", $"object {o.Id} has no archetype id", region.Coord, o.Coord));
            return;
        }
        TileObjectArchetype? a = catalogs.Archetype(o.ArchetypeId);
        if (a is null)
        {
            issues.Add(new("archetype.missing", $"object {o.Id} references archetype '{o.ArchetypeId}', which is not in the catalog", region.Coord, o.Coord));
            return;
        }
        TileRect fp = TileFootprint.Of(a, o.X, o.Z, o.Rotation);
        for (int z = fp.Z; z < fp.Z1; z++)
            for (int x = fp.X; x < fp.X1; x++)
                if (doc.RegionAt(x, z) is null)
                {
                    issues.Add(new("object.footprint", $"object {o.Id} ('{o.ArchetypeId}') footprint tile ({x}, {z}) lies in region {RegionCoord.Of(x, z)}, which does not exist", region.Coord, o.Coord));
                    return;
                }
    }
}
