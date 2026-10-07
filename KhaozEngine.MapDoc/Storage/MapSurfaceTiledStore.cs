using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>Prepares a surface closure before writes and shares the existing manifest-last transaction.</summary>
internal static class MapSurfaceTiledStore
{
    internal sealed record Prepared(MapSurfaceStorageIndex Index, IReadOnlyList<(char Kind, byte[] Bytes)> Files,
        IReadOnlyList<(MapSurfacePatch Patch, string Digest)> Payloads, IReadOnlyList<MapSurfaceRef> Refs, IReadOnlyCollection<string> Keep);

    internal static IReadOnlyList<MapDirectoryPageRef> ReadDirectory(JsonObject root)
    {
        if (!MapDocumentMembers.TryGetProperty(root, "surfaceStorage", out JsonNode? node)) return Array.Empty<MapDirectoryPageRef>();
        if (node is not JsonArray array) throw new MapDocumentException("invalid surfaceStorage");
        var pages = new List<MapDirectoryPageRef>(array.Count);
        JsonSerializerOptions options = MapDocumentFile.CreateCompactOptions(MapDocRegistry.CreateDefault());
        foreach (JsonNode? item in array)
        {
            if (item is not JsonObject value || value.Count != 3 || value["surfaceId"] is null || value["sha256"] is null ||
                value["covers"] is not JsonObject covers || covers.Count != 4 ||
                new[] { "minX", "minZ", "maxXExclusive", "maxZExclusive" }.Any(n => covers[n] is null))
                throw new MapDocumentException("invalid surface directory reference members");
            MapDirectoryPageRef page = item?.Deserialize<MapDirectoryPageRef>(options) ?? throw new MapDocumentException("invalid surface directory reference");
            MapSurfacePages.Validate(page);
            pages.Add(page);
        }
        return pages;
    }
    internal static Prepared Prepare(MapDocument doc, string root, MapSurfacePacking? packing)
    {
        try { return PrepareCore(doc, root, packing); }
        catch (Exception ex) when (ex is MapExactOverflowException or OverflowException)
        {
            throw new MapDocumentException("surface storage not representable", ex);
        }
    }
    static Prepared PrepareCore(MapDocument doc, string root, MapSurfacePacking? packing)
    {
        MapSurfaceStorageIndex? prior = doc.Tiles?.Surfaces;
        bool partial = doc.Tiles?.IsPartial == true;
        if (partial && prior is not null) MapSurfaceSaveGuard.Partial(doc, prior);
        else MapSurfaceSaveGuard.Whole(doc.Surfaces);
        MapSurfaceStorageIndex? closure = partial && prior is not null ? ReadClosure(root, prior) : null;
        var entries = new SortedDictionary<MapPatchKey, MapSurfaceIndexEntry>();
        if (closure is not null)
            foreach (MapSurfaceIndexEntry entry in closure.Entries)
                if (!prior!.Baselines.ContainsKey(entry.Key)) entries.Add(entry.Key, entry);
        var files = new List<(char Kind, byte[] Bytes)>();
        var payloads = new List<(MapSurfacePatch Patch, string Digest)>();
        var incidence = partial && prior is not null ? null : new MapSurfaceIncidence(doc.Surfaces);
        foreach (var pair in doc.Surfaces.Patches)
        {
            MapSurfacePatch patch = pair.Value;
            if (pair.Key != patch.Key) throw new MapDocumentException("surface dictionary key does not match patch key");
            IReadOnlyList<MapRecordRef> incidents = partial && prior is not null && prior.ByKey.TryGetValue(patch.Key, out var old)
                ? old.IncidentRecords : incidence!.For(patch.Key);
            IReadOnlyList<string> spaces = partial && prior is not null && prior.ByKey.TryGetValue(patch.Key, out var oldSpace)
                ? oldSpace.SpaceIds : MapSurfaceReferences.Spaces(doc.Surfaces, patch, incidents);
            MapSurfaceIndexEntry entry = Entry(patch, incidents, spaces);
            entries.Add(patch.Key, entry);
            payloads.Add((patch, entry.PayloadSha256));
        }
        var refs = doc.Surfaces.Refs.OrderBy(s => s.Id, StringComparer.Ordinal).Select(s => s with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(s, entries.Values.Where(e => e.Key.SurfaceId == s.Id)
                .Select(e => new KeyValuePair<MapPatchKey, string>(e.Key, e.SemanticSha256))),
        }).ToArray();
        MapSurfaceStorageIndex all = partial && closure is not null
            ? CarryPages(closure, entries, refs, files) : Pack(entries.Values, refs, packing ?? new(), files);
        var keep = new HashSet<string>(StringComparer.Ordinal);
        foreach (MapDirectoryPageRef dir in all.Directory)
        {
            keep.Add(MapSurfaceStorageLayout.PathOf(root, 'd', dir.Sha256));
            foreach (MapIndexPageRef page in all.DirectoryPages[dir.Sha256]) keep.Add(MapSurfaceStorageLayout.PathOf(root, 'i', page.Sha256));
        }
        foreach (MapSurfaceIndexEntry entry in entries.Values) keep.Add(MapSurfaceStorageLayout.PathOf(root, 'p', entry.PayloadSha256));
        MapSurfaceStorageIndex resident = partial && prior is not null ? Residency(all, prior, doc.Surfaces) : all;
        foreach (MapSurfacePatch patch in doc.Surfaces.Patches.Values) resident.Loaded(patch);
        return new(resident, files, payloads, refs, keep);
    }
    internal static MapSurfaceIndexEntry Entry(MapSurfacePatch patch, IReadOnlyList<MapRecordRef> incident, IReadOnlyList<string> spaces)
    {
        byte[] bytes = MapSurfacePatchCodec.Encode(patch);
        return new(patch.Key, MapSurfaceRanges.Rectangle(patch), patch.Heights.Min(), patch.Heights.Max(),
            MapSurfaceStorageLayout.Digest(bytes), MapSurfaceSemantics.PatchDigest(patch),
            Array.AsReadOnly(patch.CornerDependencies.Select(d => d.Owner.Patch).Distinct().OrderBy(k => k).ToArray()),
            Array.AsReadOnly(patch.Records.Select(r => r.Id).OrderBy(s => s, StringComparer.Ordinal).ToArray()),
            Array.AsReadOnly(incident.ToArray()), Array.AsReadOnly(spaces.ToArray()), true);
    }
    internal static MapSurfaceStorageIndex BuildResidentIndex(MapSurfaceSet set)
    {
        var incidence = new MapSurfaceIncidence(set);
        var entries = set.Patches.Values.Select(p =>
        {
            var incident = incidence.For(p.Key);
            return Entry(p, incident, MapSurfaceReferences.Spaces(set, p, incident));
        }).ToArray();
        return Pack(entries, set.Refs, new(), new());
    }
    static MapSurfaceStorageIndex Pack(IEnumerable<MapSurfaceIndexEntry> entries, IEnumerable<MapSurfaceRef> refs,
        MapSurfacePacking packing, List<(char Kind, byte[] Bytes)> files)
    {
        if (packing.IndexBlockSlots is < 1 or > 16 || packing.DirectoryBlockPages is < 1 or > 16)
            throw new MapDocumentException("invalid surface packing");
        var directories = new List<MapDirectoryPageRef>();
        var directoryPages = new Dictionary<string, IReadOnlyList<MapIndexPageRef>>();
        var indexPages = new Dictionary<string, IReadOnlyList<MapSurfaceIndexEntry>>();
        int block = packing.IndexBlockSlots, directoryBlock = checked(block * packing.DirectoryBlockPages);
        foreach (var surface in entries.GroupBy(e => e.Key.SurfaceId).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var pageRefs = new List<MapIndexPageRef>();
            foreach (var group in surface.GroupBy(e => (X: MapSurfaceRanges.Floor(e.Key.SlotX, block), Z: MapSurfaceRanges.Floor(e.Key.SlotZ, block)))
                .OrderBy(g => g.Key.Z).ThenBy(g => g.Key.X))
            {
                MapSlotRect covers = Block(group.Key.X, group.Key.Z, block);
                MapSurfaceIndexEntry[] items = group.OrderBy(e => e.Key).ToArray();
                byte[] bytes = MapSurfacePages.EncodeIndex(surface.Key, covers, items);
                string digest = MapSurfaceStorageLayout.Digest(bytes);
                pageRefs.Add(new(covers, digest, items.Length));
                indexPages.Add(digest, Array.AsReadOnly(items)); files.Add(('i', bytes));
            }
            foreach (var group in pageRefs.GroupBy(p => (X: MapSurfaceRanges.Floor(p.Covers.MinX, directoryBlock), Z: MapSurfaceRanges.Floor(p.Covers.MinZ, directoryBlock)))
                .OrderBy(g => g.Key.Z).ThenBy(g => g.Key.X))
            {
                MapSlotRect covers = Block(group.Key.X, group.Key.Z, directoryBlock);
                MapIndexPageRef[] items = group.OrderBy(p => p.Covers.MinZ).ThenBy(p => p.Covers.MinX).ToArray();
                byte[] bytes = MapSurfacePages.EncodeDirectory(surface.Key, covers, items);
                string digest = MapSurfaceStorageLayout.Digest(bytes);
                directories.Add(new(surface.Key, covers, digest));
                directoryPages.Add(digest, Array.AsReadOnly(items)); files.Add(('d', bytes));
            }
        }
        var result = new MapSurfaceStorageIndex(directories, refs);
        foreach (var d in directoryPages) result.DirectoryPages.Add(d.Key, d.Value);
        foreach (MapIndexPageRef page in directoryPages.Values.SelectMany(p => p)) result.AddIndex(page, indexPages[page.Sha256]);
        return result;
    }
    static MapSlotRect Block(long x, long z, int size)
    {
        try { return new(checked(x * size), checked(z * size), checked((x + 1) * size), checked((z + 1) * size)); }
        catch (OverflowException) { throw new MapDocumentException("surface page bounds not representable"); }
    }
    static MapSurfaceStorageIndex CarryPages(MapSurfaceStorageIndex closure, SortedDictionary<MapPatchKey, MapSurfaceIndexEntry> entries,
        IEnumerable<MapSurfaceRef> refs, List<(char Kind, byte[] Bytes)> files)
    {
        var dirs = new List<MapDirectoryPageRef>();
        var dp = new Dictionary<string, IReadOnlyList<MapIndexPageRef>>();
        var ip = new Dictionary<string, IReadOnlyList<MapSurfaceIndexEntry>>();
        foreach (MapDirectoryPageRef dir in closure.Directory)
        {
            var pages = new List<MapIndexPageRef>();
            foreach (MapIndexPageRef old in closure.DirectoryPages[dir.Sha256])
            {
                var items = closure.IndexPages[old.Sha256].Select(e => entries[e.Key]).ToArray();
                string digest = old.Sha256;
                if (!items.Zip(closure.IndexPages[old.Sha256]).All(pair => Equivalent(pair.First, pair.Second)))
                {
                    byte[] bytes = MapSurfacePages.EncodeIndex(dir.SurfaceId, old.Covers, items);
                    digest = MapSurfaceStorageLayout.Digest(bytes); files.Add(('i', bytes));
                }
                pages.Add(old with { Sha256 = digest }); ip.Add(digest, Array.AsReadOnly(items));
            }
            string hash = dir.Sha256;
            if (!pages.SequenceEqual(closure.DirectoryPages[dir.Sha256]))
            {
                byte[] directory = MapSurfacePages.EncodeDirectory(dir.SurfaceId, dir.Covers, pages);
                hash = MapSurfaceStorageLayout.Digest(directory); files.Add(('d', directory));
            }
            dirs.Add(dir with { Sha256 = hash }); dp.Add(hash, Array.AsReadOnly(pages.ToArray()));
        }
        var result = new MapSurfaceStorageIndex(dirs, refs);
        foreach (var page in dp) result.DirectoryPages.Add(page.Key, page.Value);
        foreach (MapIndexPageRef page in dp.Values.SelectMany(p => p)) result.AddIndex(page, ip[page.Sha256]);
        return result;
    }
    static bool Equivalent(MapSurfaceIndexEntry a, MapSurfaceIndexEntry b) => a.Key == b.Key && a.Cells == b.Cells &&
        a.MinHeightUnits == b.MinHeightUnits && a.MaxHeightUnits == b.MaxHeightUnits && a.PayloadSha256 == b.PayloadSha256 &&
        a.SemanticSha256 == b.SemanticSha256 && a.Dependencies.SequenceEqual(b.Dependencies) && a.RecordIds.SequenceEqual(b.RecordIds) &&
        a.IncidentRecords.SequenceEqual(b.IncidentRecords) && a.SpaceIds.SequenceEqual(b.SpaceIds);
    static MapSurfaceStorageIndex Residency(MapSurfaceStorageIndex all, MapSurfaceStorageIndex old, MapSurfaceSet set)
    {
        var result = new MapSurfaceStorageIndex(all.Directory, all.OriginalRefs);
        foreach (MapDirectoryPageRef dir in all.Directory)
        {
            bool read = old.ReadDirectoryPages.Any(p => p.SurfaceId == dir.SurfaceId && p.Covers == dir.Covers);
            if (!read) continue;
            result.AddDirectory(dir, all.DirectoryPages[dir.Sha256]);
            foreach (MapIndexPageRef page in all.DirectoryPages[dir.Sha256])
                if (old.ReadIndexPages.Any(p => p.Covers == page.Covers && old.DirectoryPages.Values.SelectMany(v => v)
                    .Any(v => v.Sha256 == p.Sha256 && old.IndexPages[p.Sha256].Any(e => e.Key.SurfaceId == dir.SurfaceId))))
                    result.AddIndex(page, Array.AsReadOnly(all.IndexPages[page.Sha256].Select(e => e with { Loaded = set.Patches.ContainsKey(e.Key) }).ToArray()));
        }
        return result;
    }
    static MapSurfaceStorageIndex ReadClosure(string root, MapSurfaceStorageIndex old)
    {
        var result = new MapSurfaceStorageIndex(old.Directory, old.OriginalRefs);
        foreach (MapDirectoryPageRef dir in result.Directory)
        {
            var pages = MapSurfacePages.DecodeDirectory(MapSurfaceStorageLayout.Read(root, 'd', dir.Sha256), dir);
            result.AddDirectory(dir, pages);
            foreach (MapIndexPageRef page in pages)
                result.AddIndex(page, MapSurfacePages.DecodeIndex(MapSurfaceStorageLayout.Read(root, 'i', page.Sha256), dir.SurfaceId, page));
        }
        return result;
    }
    internal static void Write(string root, Prepared prepared, MapDocumentSaveOptions save, HashSet<string> touched)
    {
        foreach (var payload in prepared.Payloads)
        {
            byte[] bytes = MapSurfacePatchCodec.Encode(payload.Patch);
            if (MapSurfaceStorageLayout.Digest(bytes) != payload.Digest)
                throw new MapDocumentException("document changed during surface save");
            WriteFile(root, 'p', bytes, save, touched);
        }
        foreach (var file in prepared.Files.OrderBy(f => f.Kind == 'i' ? 0 : 1))
            WriteFile(root, file.Kind, file.Bytes, save, touched);
    }
    static void WriteFile(string root, char kind, byte[] bytes, MapDocumentSaveOptions save, HashSet<string> touched)
    {
        MapSurfaceStorageLayout.Write(root, kind, bytes, save);
        string? directory = Path.GetDirectoryName(MapSurfaceStorageLayout.PathOf(root, kind, MapSurfaceStorageLayout.Digest(bytes)));
        while (directory is not null && directory != root)
        {
            touched.Add(directory); directory = Path.GetDirectoryName(directory);
        }
    }
    internal static void Load(string root, MapDocument doc, MapTileIndex tiles, MapTileRect? window)
    {
        try { LoadCore(root, doc, tiles, window); }
        catch (Exception ex) when (ex is MapExactOverflowException or OverflowException)
        {
            throw new MapDocumentException("surface window not representable", ex);
        }
    }
    static void LoadCore(string root, MapDocument doc, MapTileIndex tiles, MapTileRect? window)
    {
        MapStoredSurfaceSource source = MapStoredSurfaceSource.FromManifest(root, doc, tiles);
        var budget = new MapPageBudget(int.MaxValue);
        foreach (MapSurfaceRef surface in source.Surfaces)
        {
            MapSlotRect? slots = null; MapCellRect? cells = null;
            if (window is { } rect)
            {
                MapExactValue size = MapExactValue.FromSingle(tiles.TileSize);
                MapExactRect world = new(new MapExactValue(rect.Min.X, 1).Multiply(size), new MapExactValue(rect.Min.Z, 1).Multiply(size),
                    new MapExactValue((long)rect.Max.X + 1, 1).Multiply(size), new MapExactValue((long)rect.Max.Z + 1, 1).Multiply(size));
                cells = MapSurfaceRanges.Cells(world, surface.Frame); slots = MapSurfaceRanges.Slots(cells.Value);
            }
            IEnumerable<MapDirectoryPageRef> dirs = slots is { } range ? source.Index.Covering(surface.Id, range) : source.Index.Directory.Where(d => d.SurfaceId == surface.Id);
            foreach (MapDirectoryPageRef dir in dirs)
            {
                Require(source.Directory(dir, budget));
                foreach (MapIndexPageRef page in source.Index.DirectoryPages[dir.Sha256])
                {
                    if (slots is { } slotRange && !page.Covers.Overlaps(slotRange)) continue;
                    Require(source.Page(dir, page, budget));
                    foreach (MapSurfaceIndexEntry entry in source.Index.IndexPages[page.Sha256])
                    {
                        if (cells is { } cellRange && !entry.Cells.Overlaps(cellRange)) continue;
                        MapPatchRead read = source.Payload(entry); Require(read.Status);
                        doc.Surfaces.Patches.Add(entry.Key, read.Patch!); source.Index.Loaded(read.Patch!);
                    }
                }
            }
        }
        if (window is null)
            foreach (MapSurfaceRef surface in source.Surfaces)
                if (MapSurfaceSemantics.SurfaceDigest(surface, source.Index.Entries.Where(e => e.Key.SurfaceId == surface.Id)
                    .Select(e => new KeyValuePair<MapPatchKey, string>(e.Key, e.SemanticSha256))) != surface.SemanticSha256)
                    throw new MapDocumentException("surface semantic digest mismatch");
    }
    static void Require(MapPatchStatus status)
    {
        if (status != MapPatchStatus.Present) throw new MapDocumentException($"surface storage {status}");
    }

    internal static void Verify(string root, MapDocument doc, MapTileIndex tiles, HashSet<string> named, List<string> report)
    {
        MapStoredSurfaceSource source = MapStoredSurfaceSource.FromManifest(root, doc, tiles);
        var budget = new MapPageBudget(int.MaxValue);
        foreach (MapDirectoryPageRef dir in source.Index.Directory)
        {
            named.Add(MapTiledFile.Normalize(MapSurfaceStorageLayout.PathOf(root, 'd', dir.Sha256)));
            MapPatchStatus ds = source.Directory(dir, budget);
            if (ds != MapPatchStatus.Present) { report.Add($"surface directory {ds}: {dir.Sha256}"); continue; }
            foreach (MapIndexPageRef page in source.Index.DirectoryPages[dir.Sha256])
            {
                named.Add(MapTiledFile.Normalize(MapSurfaceStorageLayout.PathOf(root, 'i', page.Sha256)));
                MapPatchStatus ps = source.Page(dir, page, budget);
                if (ps != MapPatchStatus.Present) { report.Add($"surface index {ps}: {page.Sha256}"); continue; }
                foreach (MapSurfaceIndexEntry entry in source.Index.IndexPages[page.Sha256])
                {
                    named.Add(MapTiledFile.Normalize(MapSurfaceStorageLayout.PathOf(root, 'p', entry.PayloadSha256)));
                    MapPatchRead read = source.Payload(entry);
                    if (read.Status == MapPatchStatus.Present) doc.Surfaces.Patches.Add(entry.Key, read.Patch!);
                    else report.Add($"surface payload {read.Status}: {entry.Key}");
                }
            }
        }
        if (report.Any(r => r.StartsWith("surface", StringComparison.Ordinal))) return;
        try { MapSurfaceSaveGuard.Whole(doc.Surfaces); }
        catch (MapDocumentException ex) { report.Add(ex.Message); return; }
        var incidence = new MapSurfaceIncidence(doc.Surfaces);
        foreach (MapSurfaceIndexEntry entry in source.Index.Entries)
            if (!incidence.For(entry.Key).SequenceEqual(entry.IncidentRecords))
                report.Add($"incident list mismatch: {entry.Key}");
        foreach (MapSurfaceRef surface in doc.Surfaces.Refs)
            if (MapSurfaceSemantics.SurfaceDigest(surface, source.Index.Entries.Where(e => e.Key.SurfaceId == surface.Id)
                .Select(e => new KeyValuePair<MapPatchKey, string>(e.Key, e.SemanticSha256))) != surface.SemanticSha256)
                report.Add($"surface semantic digest mismatch: {surface.Id}");
    }
}
