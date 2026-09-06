using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.TileWorld;

/// <summary>The tile world: header fields plus a sparse map of 64x64 regions. Mutable, no undo of its own
/// (undo belongs to the editor kernel), no events: every mutation marks its region dirty, and the tools
/// compute the touched rect themselves.</summary>
public sealed partial class TileWorldDocument
{
    /// <summary>Planes a new document stacks.</summary>
    public const int DefaultPlaneCount = 4;
    /// <summary>Metres per tile in a new document.</summary>
    public const float DefaultTileSize = 1f;
    /// <summary>Metres between planes in a new document.</summary>
    public const float DefaultPlaneHeight = 3f;

    /// <summary>Stable id of this world, used in save paths and references.</summary>
    public string Id { get; set; } = "";
    /// <summary>Human-readable name for the editor.</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>Metres per tile.</summary>
    public float TileSize { get; set; } = DefaultTileSize;
    /// <summary>Planes every region allocates. Fixed once regions exist.</summary>
    public int PlaneCount { get; set; } = DefaultPlaneCount;
    /// <summary>Metres a plane with no authored heights sits above the one below it.</summary>
    public float PlaneHeight { get; set; } = DefaultPlaneHeight;
    /// <summary>Catalog files this world was authored against, relative to <c>world.json</c>.</summary>
    public List<string> CatalogPaths { get; } = new();
    /// <summary>Document-wide object id allocator. Never reused.</summary>
    public long NextObjectId { get; set; } = 1;

    readonly Dictionary<RegionCoord, TileRegion> _regions = new();
    readonly Dictionary<long, TileRegion> _objectIndex = new();

    /// <summary>Every region currently in memory, keyed by coordinate.</summary>
    public IReadOnlyDictionary<RegionCoord, TileRegion> Regions => _regions;

    /// <summary>Regions the manifest knows about that are not materialised in memory (a lazily opened world),
    /// with their stored hashes, so a save carries them through untouched.</summary>
    internal Dictionary<RegionCoord, string> UnloadedRegionHashes { get; } = new();

    /// <summary>The streaming source this document was opened through, or null for one built in memory. Set by
    /// <see cref="TileWorldSource.Open"/>, which is also what <see cref="TileWorldFile.Load"/> goes through, so a
    /// whole-world load has one too (with every region loaded, which the checks below then never need). It is the
    /// document's only view of the regions it does NOT hold.</summary>
    public TileWorldSource? Source { get; internal set; }

    /// <summary>The region at c, or null when it is not in memory.</summary>
    public TileRegion? GetRegion(RegionCoord c) => _regions.TryGetValue(c, out TileRegion? r) ? r : null;

    /// <summary>The region at c, creating an empty one when it does not exist. Throws when the region exists
    /// on disk but has not been loaded, because blanking it here would let the next save overwrite authored
    /// terrain with nothing.</summary>
    public TileRegion GetOrCreateRegion(RegionCoord c)
    {
        if (_regions.TryGetValue(c, out TileRegion? r)) return r;
        if (UnloadedRegionHashes.ContainsKey(c)) throw new TileWorldException(UnloadedMessage(c));
        r = new TileRegion(c, PlaneCount) { Dirty = true };
        _regions.Add(c, r);
        return r;
    }

    static string UnloadedMessage(RegionCoord c) =>
        $"region {c} exists on disk but is not loaded, load it through TileWorldSource first.";

    /// <summary>Adds an already-built region (the file loader's path). Throws when the coordinate is taken.</summary>
    internal void AttachRegion(TileRegion region)
    {
        if (region.Planes.Length != PlaneCount)
            throw new TileWorldException($"region {region.Coord}: has {region.Planes.Length} planes, the document has {PlaneCount}");
        Attach(region);
    }

    /// <summary>Puts a region this document previously held back where it was, the undo of a region delete.
    /// Same semantics as <see cref="AttachRegion"/> (the coordinate must be free) with a message that says
    /// restore, because a plane-count mismatch here means the document's plane count moved under a command
    /// rather than that a file on disk disagrees with the manifest.</summary>
    internal void RestoreRegion(TileRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (region.Planes.Length != PlaneCount)
            throw new TileWorldException($"region {region.Coord}: cannot restore a region with {region.Planes.Length} planes into a document with {PlaneCount}");
        Attach(region);
    }

    void Attach(TileRegion region)
    {
        _regions.Add(region.Coord, region);
        UnloadedRegionHashes.Remove(region.Coord);
        foreach (TileObject o in region.Objects) _objectIndex[o.Id] = region;
    }

    /// <summary>Drops the region and its objects from the index. False when it was not there.
    /// <para>For a document opened through <see cref="TileWorldSource"/>, this follows the source's unload path so
    /// its saved hash and marker index remain valid. A dirty source-backed region is refused until it is
    /// saved.</para></summary>
    public bool RemoveRegion(RegionCoord c) => Source is null ? DetachRegion(c) : Source.Unload(c);

    /// <summary>Permanently deletes a loaded or unloaded region. Unlike <see cref="RemoveRegion"/>, this does
    /// not preserve source state for a later reload and does not refuse dirty regions. A source-backed delete
    /// drops the known hash and marker-index rows too, so the next <see cref="TileWorldFile.Save"/> removes the
    /// region file and manifest row.</summary>
    public bool DeleteRegion(RegionCoord c)
    {
        if (Source is not null) return Source.DeleteRegion(c);
        bool loaded = DetachRegion(c);
        bool unloaded = UnloadedRegionHashes.Remove(c);
        return loaded || unloaded;
    }

    // The source's final detach after it has captured the region state. Kept separate from the public path so
    // TileWorldSource.Unload does not recurse through itself.
    internal bool DetachRegion(RegionCoord c)
    {
        if (!_regions.Remove(c, out TileRegion? r)) return false;
        foreach (TileObject o in r.Objects) _objectIndex.Remove(o.Id);
        return true;
    }

    /// <summary>The region holding world tile (x, z), or null when it is not in memory.</summary>
    public TileRegion? RegionAt(int x, int z) => GetRegion(RegionCoord.Of(x, z));

    /// <summary>The region holding world tile (x, z), or a <see cref="TileWorldException"/> naming it. The
    /// message distinguishes a region that was never created from one still waiting to be loaded.</summary>
    public TileRegion RequireRegion(int x, int z)
    {
        RegionCoord c = RegionCoord.Of(x, z);
        TileRegion? r = GetRegion(c);
        if (r is not null) return r;
        string why = UnloadedRegionHashes.ContainsKey(c) ? UnloadedMessage(c) : $"region {c} does not exist. Create it first.";
        throw new TileWorldException($"tile ({x}, {z}): {why}");
    }

    void RequirePlane(int plane)
    {
        if ((uint)plane >= (uint)PlaneCount) throw new ArgumentOutOfRangeException(nameof(plane), $"plane {plane} is outside 0..{PlaneCount - 1}");
    }

    /// <summary>Takes the next free object id and advances the allocator.</summary>
    public long AllocateObjectId() => NextObjectId++;

    /// <summary>Places a new object in the region holding (x, z), which must exist. The destination is checked
    /// BEFORE an id is taken, so a placement that lands outside the authored world does not burn one.</summary>
    public TileObject AddObject(string archetypeId, int x, int z, int plane, int rotation, IEnumerable<string>? tags = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archetypeId);
        RequirePlane(plane);
        RequireRegion(x, z);
        return AddObjectWithId(AllocateObjectId(), archetypeId, x, z, plane, rotation, tags);
    }

    /// <summary>Inserts an object at a GIVEN id, the re-add half of an undoable place or delete: an object has
    /// to come back with the id it left with, or every reference to it (a marker's tag, a quest hook, a saved
    /// region file) points at nothing after one undo. Throws when the id is already in use, and pulls
    /// <see cref="NextObjectId"/> past the id so a later fresh allocation cannot collide with it. The allocator
    /// is never pulled BACK, which is why a redo can re-add the same id safely.</summary>
    internal TileObject AddObjectWithId(long id, string archetypeId, int x, int z, int plane, int rotation, IEnumerable<string>? tags)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archetypeId);
        RequirePlane(plane);
        if (_objectIndex.ContainsKey(id)) throw new TileWorldException($"object {id} already exists");
        TileRegion region = RequireRegion(x, z);
        var o = new TileObject
        {
            Id = id, ArchetypeId = archetypeId, X = x, Z = z, Plane = plane, Rotation = rotation & 3,
            Tags = tags is null ? null : tags.ToList(),
        };
        region.Objects.Add(o);
        region.Dirty = true;
        _objectIndex[o.Id] = region;
        if (id >= NextObjectId) NextObjectId = id + 1;
        return o;
    }

    /// <summary>The object with this id, or null when no such object is indexed.</summary>
    public TileObject? FindObject(long id)
    {
        // Indexed loop rather than List.Find with a lambda: the predicate captures id, so the old form allocated
        // a closure and a delegate on EVERY lookup. Harmless in the editor, and the tile netcode resolves an
        // interaction target through here on each command and on each reconcile replay of an unacked one.
        if (!_objectIndex.TryGetValue(id, out TileRegion? r)) return null;
        List<TileObject> objects = r.Objects;
        for (int i = 0; i < objects.Count; i++)
            if (objects[i].Id == id) return objects[i];
        return null;
    }

    /// <summary>Deletes the object. False when no such object is indexed.</summary>
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
        TileObject o = from.Objects.Find(candidate => candidate.Id == id)
            ?? throw new TileWorldException($"object {id} is indexed to region {from.Coord} but is not in it, the object index is stale. Call RebuildObjectIndex.");
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

    /// <summary>Every object in every loaded region, in no particular order.</summary>
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
    /// old one, so a throw leaves the existing marker where it was rather than deleting it.
    /// <para>Marker names are unique across the WHOLE world, not just the part of it in memory. When this
    /// document came from a <see cref="TileWorldSource"/>, a name an unloaded region already carries is refused
    /// rather than authored into a second region that would collide the moment both were loaded (#788). A
    /// document with no source has no view of anything it does not hold, so it checks the loaded set alone.</para></summary>
    /// <exception cref="TileWorldException">The name belongs to a marker in a region this document does not
    /// hold, or the destination region does not exist.</exception>
    public TileMarker SetMarker(string name, int x, int z, int plane, IEnumerable<string>? tags = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        RequirePlane(plane);
        TileRegion region = RequireRegion(x, z);
        RequireNameFreeInUnloadedRegions(name);
        RemoveMarker(name);
        var m = new TileMarker { Name = name, X = x, Z = z, Plane = plane, Tags = tags?.ToList() };
        region.Markers.Add(m);
        region.Dirty = true;
        return m;
    }

    // The manifest's marker index covers every region, loaded or not, and a LOADED region outranks its row
    // there (the region in memory is the live truth, the row is a copy of what a file said). So a row only
    // blocks the name when the region holding it is one this document cannot see: that marker is real, it is
    // just not in memory, and RemoveMarker below would walk straight past it and author the duplicate.
    void RequireNameFreeInUnloadedRegions(string name)
    {
        if (Source is not TileWorldSource source) return;
        if (source.FindMarker(name) is not TileMarker carried) return;
        RegionCoord home = RegionCoord.Of(carried.X, carried.Z);
        if (GetRegion(home) is not null) return;
        throw new TileWorldException(
            $"marker '{name}' already exists at ({carried.X}, {carried.Z}) in region {home}, which is not loaded. " +
            "Marker names are unique across the whole world. Load that region to move it, or pick another name.");
    }

    /// <summary>The marker with this name, or null when there is none. Walks the LOADED regions only, so a
    /// streaming client asking before it has streamed anything wants <see cref="TileWorldSource.FindMarker"/>,
    /// which reads the manifest's index instead.</summary>
    public TileMarker? FindMarker(string name) => AllMarkers().FirstOrDefault(m => m.Name == name);

    /// <summary>Deletes the named marker. False when there was none.</summary>
    public bool RemoveMarker(string name)
    {
        foreach (TileRegion r in _regions.Values)
        {
            int n = r.Markers.RemoveAll(m => m.Name == name);
            if (n > 0) { r.Dirty = true; return true; }
        }
        return false;
    }

    /// <summary>Every marker in every loaded region, in no particular order.</summary>
    public IEnumerable<TileMarker> AllMarkers() => _regions.Values.SelectMany(r => r.Markers);

    /// <summary>Recomputes the id index from the regions (after a bulk load or an external edit of the lists).</summary>
    public void RebuildObjectIndex()
    {
        _objectIndex.Clear();
        foreach (TileRegion r in _regions.Values)
            foreach (TileObject o in r.Objects) _objectIndex[o.Id] = r;
    }
}
