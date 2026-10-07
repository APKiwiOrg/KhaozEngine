using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>Resident editing snapshot. Both captured and returned payloads are detached.</summary>
public sealed class MapDocumentSurfaceSource : IMapSurfaceSource
{
    readonly MapSurfaceSet _surfaces;
    readonly MapSurfaceStorageIndex _index;
    readonly MapSurfaceStorageIndex _residentIndex;
    MapDocumentSurfaceSource(MapDocument doc)
    {
        _surfaces = doc.Surfaces.Clone();
        Surfaces = Array.AsReadOnly(MapSurfaceStorageIndex.CopyRefs(_surfaces.Refs).OrderBy(s => s.Id, StringComparer.Ordinal).ToArray());
        _residentIndex = MapSurfaceTiledStore.BuildResidentIndex(_surfaces);
        _index = doc.Tiles is { IsPartial: true, Surfaces: { } stored } ? stored.Clone() : _residentIndex;
        MapDocument capturedRoot = MapTiledFile.GlobalsOnly(doc);
        capturedRoot.Surfaces = _surfaces;
        capturedRoot.Tiles = doc.Tiles is { } tiles ? new MapTileIndex(tiles.TileSize, tiles.SchemeVersion,
            tiles.SourceDirectory, tiles.Entries, tiles.ManifestSha256, _index) : null;
        RootSha256 = MapSurfaceSemantics.RootDigest(capturedRoot);
        foreach (MapSurfacePatch patch in _surfaces.Patches.Values)
        {
            if (_index.ByKey.TryGetValue(patch.Key, out MapSurfaceIndexEntry? entry))
                _index.ByKey[patch.Key] = MapSurfaceTiledStore.Entry(patch, entry.IncidentRecords, entry.SpaceIds);
        }
        using var hash = new MapCanonical.HashingBufferWriter();
        hash.Append(Encoding.UTF8.GetBytes(RootSha256 + "\n"));
        foreach (var patch in _surfaces.Patches) hash.Append(Encoding.UTF8.GetBytes(MapSurfaceSemantics.PatchDigest(patch.Value) + "\n"));
        SnapshotId = "doc:" + Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    public static MapDocumentSurfaceSource Capture(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new(document);
    }
    public string SnapshotId { get; }
    public string RootSha256 { get; }
    public IReadOnlyList<MapSurfaceRef> Surfaces { get; }
    public MapPatchRead ReadPatch(MapPatchKey key) => _surfaces.Patches.TryGetValue(key, out MapSurfacePatch? patch)
        ? new(key, MapPatchStatus.Present, patch.Clone(), MapSurfaceSemantics.PatchDigest(patch), null, 0)
        : new(key, _index.StatusOf(key) == MapPatchStatus.Present ? MapPatchStatus.KnownEmpty : _index.StatusOf(key), null, null, null, 0);
    public MapPatchFindResult FindPatches(MapSurfaceScope scope)
    {
        MapPatchFindResult known = Find(_index, scope);
        if (ReferenceEquals(_index, _residentIndex) || known.Status == MapFindStatus.CapacityExceeded) return known;
        MapPatchFindResult resident = Find(_residentIndex, scope);
        if (resident.Status == MapFindStatus.CapacityExceeded) return resident;
        MapPatchRead[] patches = known.Patches.Concat(resident.Patches).DistinctBy(p => p.Key).OrderBy(p => p.Key).ToArray();
        if (patches.Length > scope.Limits.MaxCandidatePatches)
            return new(MapFindStatus.CapacityExceeded, scope, SnapshotId, Array.Empty<MapPatchRead>(),
                Array.Empty<MapCoveredRange>(), Array.Empty<MapPatchRead>(), 0);
        var empty = new List<MapCoveredRange>();
        foreach (MapCoveredRange range in known.KnownEmpty)
        {
            var rects = new List<MapSlotRect> { range.Slots };
            foreach (MapPatchRead patch in patches.Where(p => p.Key.SurfaceId == range.SurfaceId))
                MapSurfaceQuery.Subtract(rects, new(patch.Key.SlotX, patch.Key.SlotZ, checked(patch.Key.SlotX + 1), checked(patch.Key.SlotZ + 1)));
            empty.AddRange(rects.Select(r => new MapCoveredRange(range.SurfaceId, r)));
        }
        return known with { Patches = Array.AsReadOnly(patches), KnownEmpty = Array.AsReadOnly(empty.ToArray()) };
    }
    MapPatchFindResult Find(MapSurfaceStorageIndex index, MapSurfaceScope scope) => MapSurfaceQuery.Find(this, index, scope,
        (dir, _) => index.DirectoryPages.ContainsKey(dir.Sha256) ? MapPatchStatus.Present : MapPatchStatus.Unloaded,
        (_, page, _) => index.IndexPages.ContainsKey(page.Sha256) ? MapPatchStatus.Present : MapPatchStatus.Unloaded,
        entry => ReadPatch(entry.Key));
}
