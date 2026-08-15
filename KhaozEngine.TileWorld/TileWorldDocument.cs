using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.TileWorld;

/// <summary>The tile world: header fields plus a sparse map of 64x64 regions. Mutable, no undo of its own
/// (undo belongs to the editor kernel), no events: every mutation marks its region dirty, and the tools
/// compute the touched rect themselves.</summary>
public sealed partial class TileWorldDocument
{
    public const int DefaultPlaneCount = 4;
    public const float DefaultTileSize = 1f;
    public const float DefaultPlaneHeight = 3f;

    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>Metres per tile.</summary>
    public float TileSize { get; set; } = DefaultTileSize;
    public int PlaneCount { get; set; } = DefaultPlaneCount;
    /// <summary>Metres a plane with no authored heights sits above the one below it.</summary>
    public float PlaneHeight { get; set; } = DefaultPlaneHeight;
    /// <summary>Catalog files this world was authored against, relative to <c>world.json</c>.</summary>
    public List<string> CatalogPaths { get; } = new();
    /// <summary>Document-wide object id allocator. Never reused.</summary>
    public long NextObjectId { get; set; } = 1;

    readonly Dictionary<RegionCoord, TileRegion> _regions = new();
    readonly Dictionary<long, TileRegion> _objectIndex = new();

    public IReadOnlyDictionary<RegionCoord, TileRegion> Regions => _regions;

    /// <summary>Regions the manifest knows about that are not materialised in memory (a lazily opened world),
    /// with their stored hashes, so a save carries them through untouched.</summary>
    internal Dictionary<RegionCoord, string> UnloadedRegionHashes { get; } = new();

    public TileRegion? GetRegion(RegionCoord c) => _regions.TryGetValue(c, out TileRegion? r) ? r : null;

    public TileRegion GetOrCreateRegion(RegionCoord c)
    {
        if (_regions.TryGetValue(c, out TileRegion? r)) return r;
        r = new TileRegion(c, PlaneCount) { Dirty = true };
        _regions.Add(c, r);
        UnloadedRegionHashes.Remove(c);
        return r;
    }

    /// <summary>Adds an already-built region (the file loader's path). Throws when the coordinate is taken.</summary>
    internal void AttachRegion(TileRegion region)
    {
        if (region.Planes.Length != PlaneCount)
            throw new TileWorldException($"region {region.Coord}: has {region.Planes.Length} planes, the document has {PlaneCount}");
        _regions.Add(region.Coord, region);
        UnloadedRegionHashes.Remove(region.Coord);
        foreach (TileObject o in region.Objects) _objectIndex[o.Id] = region;
    }

    public bool RemoveRegion(RegionCoord c)
    {
        if (!_regions.Remove(c, out TileRegion? r)) return false;
        foreach (TileObject o in r.Objects) _objectIndex.Remove(o.Id);
        return true;
    }

    public TileRegion? RegionAt(int x, int z) => GetRegion(RegionCoord.Of(x, z));

    /// <summary>The region holding world tile (x, z), or a <see cref="TileWorldException"/> naming it.</summary>
    public TileRegion RequireRegion(int x, int z)
    {
        RegionCoord c = RegionCoord.Of(x, z);
        return GetRegion(c) ?? throw new TileWorldException($"tile ({x}, {z}) is in region {c}, which does not exist. Create it first.");
    }

    void RequirePlane(int plane)
    {
        if ((uint)plane >= (uint)PlaneCount) throw new ArgumentOutOfRangeException(nameof(plane), $"plane {plane} is outside 0..{PlaneCount - 1}");
    }

    public long AllocateObjectId() => NextObjectId++;

    public TileObject AddObject(string archetypeId, int x, int z, int plane, int rotation, IEnumerable<string>? tags = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archetypeId);
        RequirePlane(plane);
        TileRegion region = RequireRegion(x, z);
        var o = new TileObject
        {
            Id = AllocateObjectId(), ArchetypeId = archetypeId, X = x, Z = z, Plane = plane, Rotation = rotation & 3,
            Tags = tags is null ? null : tags.ToList(),
        };
        region.Objects.Add(o);
        region.Dirty = true;
        _objectIndex[o.Id] = region;
        return o;
    }

    public TileObject? FindObject(long id) =>
        _objectIndex.TryGetValue(id, out TileRegion? r) ? r.Objects.Find(o => o.Id == id) : null;

    public bool RemoveObject(long id)
    {
        if (!_objectIndex.Remove(id, out TileRegion? r)) return false;
        r.Objects.RemoveAll(o => o.Id == id);
        r.Dirty = true;
        return true;
    }

    /// <summary>Moves an object, re-homing it when the anchor crosses into another region (which must exist).</summary>
    public void MoveObject(long id, int x, int z, int plane)
    {
        RequirePlane(plane);
        if (!_objectIndex.TryGetValue(id, out TileRegion? from))
            throw new TileWorldException($"object {id} does not exist");
        TileRegion to = RequireRegion(x, z);
        TileObject o = from.Objects.First(o => o.Id == id);
        o.X = x; o.Z = z; o.Plane = plane;
        from.Dirty = true;
        if (!ReferenceEquals(from, to))
        {
            from.Objects.Remove(o);
            to.Objects.Add(o);
            to.Dirty = true;
            _objectIndex[id] = to;
        }
    }

    /// <summary>Objects whose ANCHOR tile lies in the rect (footprints are the caller's business).</summary>
    public IEnumerable<TileObject> ObjectsIn(TileRect rect, int? plane = null)
    {
        foreach (TileRegion r in RegionsTouching(rect))
            foreach (TileObject o in r.Objects)
                if (rect.Contains(o.X, o.Z) && (plane is null || o.Plane == plane.Value)) yield return o;
    }

    public IEnumerable<TileObject> AllObjects() => _regions.Values.SelectMany(r => r.Objects);

    /// <summary>Every existing region whose rect intersects the given rect.</summary>
    public IEnumerable<TileRegion> RegionsTouching(TileRect rect)
    {
        if (rect.IsEmpty) yield break;
        RegionCoord lo = RegionCoord.Of(rect.X, rect.Z);
        RegionCoord hi = RegionCoord.Of(rect.X1 - 1, rect.Z1 - 1);
        for (int rz = lo.Rz; rz <= hi.Rz; rz++)
            for (int rx = lo.Rx; rx <= hi.Rx; rx++)
                if (_regions.TryGetValue(new RegionCoord(rx, rz), out TileRegion? r)) yield return r;
    }

    /// <summary>Places (or re-homes) the uniquely named marker. Validates the destination BEFORE dropping the
    /// old one, so a throw leaves the existing marker where it was rather than deleting it.</summary>
    public TileMarker SetMarker(string name, int x, int z, int plane, IEnumerable<string>? tags = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        RequirePlane(plane);
        TileRegion region = RequireRegion(x, z);
        RemoveMarker(name);
        var m = new TileMarker { Name = name, X = x, Z = z, Plane = plane, Tags = tags?.ToList() };
        region.Markers.Add(m);
        region.Dirty = true;
        return m;
    }

    public TileMarker? FindMarker(string name) => AllMarkers().FirstOrDefault(m => m.Name == name);

    public bool RemoveMarker(string name)
    {
        foreach (TileRegion r in _regions.Values)
        {
            int n = r.Markers.RemoveAll(m => m.Name == name);
            if (n > 0) { r.Dirty = true; return true; }
        }
        return false;
    }

    public IEnumerable<TileMarker> AllMarkers() => _regions.Values.SelectMany(r => r.Markers);

    /// <summary>Recomputes the id index from the regions (after a bulk load or an external edit of the lists).</summary>
    public void RebuildObjectIndex()
    {
        _objectIndex.Clear();
        foreach (TileRegion r in _regions.Values)
            foreach (TileObject o in r.Objects) _objectIndex[o.Id] = r;
    }
}
