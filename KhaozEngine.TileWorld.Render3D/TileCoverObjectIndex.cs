using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld;

internal readonly record struct TileCoverObject(TileRect Footprint, int Plane, bool Solid, bool Roof, bool Door);

/// <summary>Spatial cover exclusions, with anchor ownership for replacing streamed or edited regions.</summary>
internal sealed class TileCoverObjectIndex
{
    readonly TileWorldDocument _document;
    readonly TileWorldCatalogs _catalogs;
    readonly Dictionary<RegionCoord, List<Entry>> _owned = new();
    readonly Dictionary<RegionCoord, HashSet<Entry>> _footprints = new();
    readonly Dictionary<RegionCoord, HashSet<Entry>> _doors = new();

    sealed class Entry(TileCoverObject value)
    {
        internal TileCoverObject Value { get; } = value;
    }

    internal TileCoverObjectIndex(TileWorldDocument document, TileWorldCatalogs catalogs)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
        foreach (RegionCoord region in document.Regions.Keys) Refresh(region);
    }

    internal void Refresh(RegionCoord anchorRegion)
    {
        if (_owned.Remove(anchorRegion, out List<Entry>? old))
            foreach (Entry entry in old)
            {
                if (entry.Value.Solid || entry.Value.Roof)
                    foreach (RegionCoord bucket in Regions(entry.Value.Footprint)) Remove(_footprints, bucket, entry);
                if (entry.Value.Door) Remove(_doors, DoorRegion(entry.Value.Footprint), entry);
            }
        if (_document.GetRegion(anchorRegion) is not { } region) return;
        var entries = new List<Entry>();
        foreach (TileObject obj in region.Objects)
        {
            if (_catalogs.Archetype(obj.ArchetypeId) is not { } archetype) continue;
            TileCoverObject value = Capture(obj, archetype);
            if (!value.Solid && !value.Roof && !value.Door) continue;
            var entry = new Entry(value);
            entries.Add(entry);
            if (value.Solid || value.Roof)
                foreach (RegionCoord bucket in Regions(value.Footprint)) Add(_footprints, bucket, entry);
            if (value.Door) Add(_doors, DoorRegion(value.Footprint), entry);
        }
        if (entries.Count > 0) _owned.Add(anchorRegion, entries);
    }

    internal IReadOnlyList<TileCoverObject> Query(RegionCoord region, IReadOnlyList<TileFoliageLayer> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);
        if (layers.Count == 0) return Array.Empty<TileCoverObject>();
        TileRect halo = region.Rect.Expand(1);
        var candidates = new HashSet<Entry>();
        foreach (RegionCoord bucket in Regions(halo))
        {
            if (!_footprints.TryGetValue(bucket, out HashSet<Entry>? entries)) continue;
            foreach (Entry entry in entries)
            {
                TileCoverObject value = entry.Value;
                if (!value.Footprint.Intersects(halo)) continue;
                foreach (TileFoliageLayer layer in layers)
                    if ((layer.ExcludeSolidObjects && value.Solid && value.Plane == layer.Plane) ||
                        (layer.ExcludeIndoors && value.Roof && value.Plane > layer.Plane))
                    {
                        candidates.Add(entry);
                        break;
                    }
            }
        }
        foreach (TileFoliageLayer layer in layers)
        {
            if (layer.DoorClearance <= 0f) continue;
            double reach = layer.DoorClearance / (double)_document.TileSize;
            int rx0 = Bucket(region.Rect.X - reach), rx1 = Bucket(region.Rect.X1 + reach);
            int rz0 = Bucket(region.Rect.Z - reach), rz1 = Bucket(region.Rect.Z1 + reach);
            for (int rz = rz0; rz <= rz1; rz++)
                for (int rx = rx0; rx <= rx1; rx++)
                {
                    if (!_doors.TryGetValue(new RegionCoord(rx, rz), out HashSet<Entry>? entries)) continue;
                    foreach (Entry entry in entries)
                        if (entry.Value.Plane == layer.Plane && DoorReaches(entry.Value.Footprint, region, layer.DoorClearance))
                            candidates.Add(entry);
                }
        }
        var result = new TileCoverObject[candidates.Count];
        int i = 0;
        foreach (Entry entry in candidates) result[i++] = entry.Value;
        return result;
    }

    bool DoorReaches(TileRect footprint, RegionCoord region, float clearance)
    {
        float size = _document.TileSize;
        float x = TileWorldSpace.WorldX(footprint.X + footprint.Width * 0.5f, size);
        float z = TileWorldSpace.WorldZ(footprint.Z + footprint.Height * 0.5f, size);
        float minX = TileWorldSpace.WorldX(region.Rect.X, size);
        float maxX = TileWorldSpace.WorldX(region.Rect.X1, size);
        float minZ = TileWorldSpace.WorldZ(region.Rect.Z1, size);
        float maxZ = TileWorldSpace.WorldZ(region.Rect.Z, size);
        double dx = x - Math.Clamp(x, minX, maxX);
        double dz = z - Math.Clamp(z, minZ, maxZ);
        return dx * dx + dz * dz < (double)clearance * clearance;
    }

    internal static TileCoverObject Capture(TileObject obj, TileObjectArchetype archetype) => new(
        TileFootprint.Of(archetype, obj.X, obj.Z, obj.Rotation), obj.Plane,
        archetype.CollisionKind == TileCollisionKind.Solid, archetype.IsRoof,
        HasTag(obj.Tags, "door") || HasTag(archetype.Tags, "door"));

    static bool HasTag(IReadOnlyList<string>? tags, string wanted)
    {
        for (int i = 0; i < (tags?.Count ?? 0); i++)
            if (string.Equals(tags![i], wanted, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static RegionCoord DoorRegion(TileRect footprint) => new(
        Bucket(footprint.X + footprint.Width * 0.5f), Bucket(footprint.Z + footprint.Height * 0.5f));

    static int Bucket(double tile) => (int)Math.Floor(tile / TileRegion.Size);

    static IEnumerable<RegionCoord> Regions(TileRect footprint)
    {
        if (footprint.IsEmpty) yield break;
        RegionCoord lo = RegionCoord.Of(footprint.X, footprint.Z);
        RegionCoord hi = RegionCoord.Of(footprint.X1 - 1, footprint.Z1 - 1);
        for (int rz = lo.Rz; rz <= hi.Rz; rz++)
            for (int rx = lo.Rx; rx <= hi.Rx; rx++) yield return new RegionCoord(rx, rz);
    }

    static void Add(Dictionary<RegionCoord, HashSet<Entry>> buckets, RegionCoord region, Entry entry)
    {
        if (!buckets.TryGetValue(region, out HashSet<Entry>? entries)) buckets.Add(region, entries = new());
        entries.Add(entry);
    }

    static void Remove(Dictionary<RegionCoord, HashSet<Entry>> buckets, RegionCoord region, Entry entry)
    {
        if (!buckets.TryGetValue(region, out HashSet<Entry>? entries)) return;
        entries.Remove(entry);
        if (entries.Count == 0) buckets.Remove(region);
    }
}
