using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>Pins one manifest. Files swept by another generation become Missing, never replacement data.</summary>
public sealed class MapStoredSurfaceSource : IMapSurfaceSource
{
    readonly string _directory;
    internal MapSurfaceStorageIndex Index { get; }
    MapStoredSurfaceSource(string directory, MapDocument manifest, MapTileIndex tiles)
    {
        _directory = Path.GetFullPath(directory);
        Index = tiles.Surfaces ?? new MapSurfaceStorageIndex(Array.Empty<MapDirectoryPageRef>(), manifest.Surfaces.Refs);
        Surfaces = Array.AsReadOnly(MapSurfaceStorageIndex.CopyRefs(manifest.Surfaces.Refs).OrderBy(s => s.Id, StringComparer.Ordinal).ToArray());
        SnapshotId = "manifest:" + tiles.ManifestSha256;
        RootSha256 = MapSurfaceSemantics.RootDigest(manifest);
    }
    public static MapStoredSurfaceSource Open(string tiledDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tiledDirectory);
        MapDocument manifest = MapTiledFile.ReadManifest(tiledDirectory, new MapDocumentLoadOptions(), out MapTileIndex tiles);
        manifest.Tiles = tiles;
        return new(tiledDirectory, manifest, tiles);
    }
    internal static MapStoredSurfaceSource FromManifest(string directory, MapDocument manifest, MapTileIndex tiles) => new(directory, manifest, tiles);
    public string SnapshotId { get; }
    public string RootSha256 { get; }
    public IReadOnlyList<MapSurfaceRef> Surfaces { get; }
    public MapPatchRead ReadPatch(MapPatchKey key)
    {
        var budget = new MapPageBudget(2);
        foreach (MapDirectoryPageRef dir in Index.Covering(key.SurfaceId, new(key.SlotX, key.SlotZ, checked(key.SlotX + 1), checked(key.SlotZ + 1))))
        {
            MapPatchStatus ds = Directory(dir, budget);
            if (ds != MapPatchStatus.Present) return new(key, ds, null, null, "directory unavailable", budget.Reads);
            foreach (MapIndexPageRef page in Index.DirectoryPages[dir.Sha256])
            {
                if (!page.Covers.Contains(key.SlotX, key.SlotZ)) continue;
                MapPatchStatus ps = Page(dir, page, budget);
                if (ps != MapPatchStatus.Present) return new(key, ps, null, null, "index unavailable", budget.Reads);
            }
        }
        if (!Index.ByKey.TryGetValue(key, out MapSurfaceIndexEntry? entry)) return new(key, MapPatchStatus.KnownEmpty, null, null, null, budget.Reads);
        return Payload(entry) with { PagesRead = budget.Reads };
    }
    public MapPatchFindResult FindPatches(MapSurfaceScope scope) => MapSurfaceQuery.Find(this, Index, scope, Directory, Page, Payload);
    internal MapPatchStatus Directory(MapDirectoryPageRef dir, MapPageBudget budget)
    {
        if (Index.DirectoryPages.ContainsKey(dir.Sha256)) return MapPatchStatus.Present;
        return ReadPage(dir.Sha256, budget, () => Index.AddDirectory(dir,
            MapSurfacePages.DecodeDirectory(MapSurfaceStorageLayout.Read(_directory, 'd', dir.Sha256), dir)));
    }
    internal MapPatchStatus Page(MapDirectoryPageRef dir, MapIndexPageRef page, MapPageBudget budget)
    {
        if (Index.IndexPages.ContainsKey(page.Sha256)) return MapPatchStatus.Present;
        return ReadPage(page.Sha256, budget, () => Index.AddIndex(page,
            MapSurfacePages.DecodeIndex(MapSurfaceStorageLayout.Read(_directory, 'i', page.Sha256), dir.SurfaceId, page)));
    }
    MapPatchStatus ReadPage(string digest, MapPageBudget budget, Action read)
    {
        budget.BeforeRead();
        try { read(); budget.Reads++; return MapPatchStatus.Present; }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            return ex is FileNotFoundException or DirectoryNotFoundException ? MapPatchStatus.Missing : MapPatchStatus.Corrupt;
        }
    }
    internal MapPatchRead Payload(MapSurfaceIndexEntry entry)
    {
        try
        {
            byte[] bytes = MapSurfaceStorageLayout.Read(_directory, 'p', entry.PayloadSha256);
            MapSurfacePatch patch = MapSurfacePatchCodec.Decode(bytes, entry.Key);
            if (MapSurfaceSemantics.PatchDigest(patch) != entry.SemanticSha256 ||
                MapSurfaceRanges.Rectangle(patch) != entry.Cells || patch.Heights.Min() != entry.MinHeightUnits ||
                patch.Heights.Max() != entry.MaxHeightUnits ||
                !patch.CornerDependencies.Select(d => d.Owner.Patch).Distinct().OrderBy(k => k).SequenceEqual(entry.Dependencies) ||
                !patch.Records.Select(r => r.Id).OrderBy(s => s, StringComparer.Ordinal).SequenceEqual(entry.RecordIds))
                throw new MapDocumentException("surface semantic or index digest mismatch");
            return new(entry.Key, MapPatchStatus.Present, patch, entry.SemanticSha256, null, 0);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            MapPatchStatus status = ex is FileNotFoundException or DirectoryNotFoundException ? MapPatchStatus.Missing : MapPatchStatus.Corrupt;
            return new(entry.Key, status, null, entry.SemanticSha256, ex.Message, 0);
        }
    }
    internal static bool IsStorageFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or
        MapDocumentException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException or ArithmeticException;
}
