using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>Sparse knowledge from one manifest. An unread covering page is never evidence of emptiness.</summary>
public sealed class MapSurfaceStorageIndex
{
    internal readonly Dictionary<string, IReadOnlyList<MapIndexPageRef>> DirectoryPages = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, IReadOnlyList<MapSurfaceIndexEntry>> IndexPages = new(StringComparer.Ordinal);
    internal readonly SortedDictionary<MapPatchKey, MapSurfaceIndexEntry> ByKey = new();
    internal readonly Dictionary<MapPatchKey, MapSurfacePatch> Baselines = new();
    internal readonly List<MapSurfaceRef> OriginalRefs;
    readonly MapSurfacePageLookup _lookup;

    internal MapSurfaceStorageIndex(IEnumerable<MapDirectoryPageRef> directory, IEnumerable<MapSurfaceRef> refs)
    {
        Directory = Array.AsReadOnly(directory.OrderBy(d => d.SurfaceId, StringComparer.Ordinal)
            .ThenBy(d => d.Covers.MinZ).ThenBy(d => d.Covers.MinX).ToArray());
        OriginalRefs = CopyRefs(refs);
        _lookup = new MapSurfacePageLookup(Directory);
        foreach (MapDirectoryPageRef page in Directory)
        {
            MapSurfacePages.Validate(page);
            if (!OriginalRefs.Any(s => s.Id == page.SurfaceId)) throw new MapDocumentException("unknown storage surface");
        }
        foreach (var surface in Directory.GroupBy(d => d.SurfaceId))
        {
            var pages = surface.ToArray();
            for (int i = 0; i < pages.Length; i++)
                for (int j = i + 1; j < pages.Length; j++)
                    if (pages[i].Covers.Overlaps(pages[j].Covers)) throw new MapDocumentException("overlapping directory pages");
        }
    }
    public IReadOnlyList<MapDirectoryPageRef> Directory { get; }
    internal IEnumerable<MapDirectoryPageRef> Covering(string surface, MapSlotRect slots, MapPageBudget? budget = null) =>
        _lookup.Covering(surface, slots, budget);
    public IReadOnlyList<MapDirectoryPageRef> ReadDirectoryPages => Array.AsReadOnly(Directory.Where(d => DirectoryPages.ContainsKey(d.Sha256)).ToArray());
    public IReadOnlyList<MapIndexPageRef> ReadIndexPages => Array.AsReadOnly(DirectoryPages.Values.SelectMany(p => p)
        .Where(p => IndexPages.ContainsKey(p.Sha256)).ToArray());
    public int UnreadIndexPages => DirectoryPages.Values.SelectMany(p => p).Count(p => !IndexPages.ContainsKey(p.Sha256));
    public IReadOnlyList<MapSurfaceIndexEntry> Entries => Array.AsReadOnly(ByKey.Values.ToArray());
    public bool IsPartial => ReadDirectoryPages.Count != Directory.Count || UnreadIndexPages != 0 || ByKey.Values.Any(e => !e.Loaded);

    public MapPatchStatus StatusOf(MapPatchKey key)
    {
        if (ByKey.TryGetValue(key, out MapSurfaceIndexEntry? entry)) return entry.Loaded ? MapPatchStatus.Present : MapPatchStatus.Unloaded;
        foreach (MapDirectoryPageRef directory in Directory)
        {
            if (directory.SurfaceId != key.SurfaceId || !directory.Covers.Contains(key.SlotX, key.SlotZ)) continue;
            if (!DirectoryPages.TryGetValue(directory.Sha256, out var pages)) return MapPatchStatus.Unloaded;
            foreach (MapIndexPageRef page in pages)
                if (page.Covers.Contains(key.SlotX, key.SlotZ) && !IndexPages.ContainsKey(page.Sha256)) return MapPatchStatus.Unloaded;
        }
        return MapPatchStatus.KnownEmpty;
    }
    internal void AddDirectory(MapDirectoryPageRef page, IReadOnlyList<MapIndexPageRef> refs) => DirectoryPages.Add(page.Sha256, refs);
    internal void AddIndex(MapIndexPageRef page, IReadOnlyList<MapSurfaceIndexEntry> entries)
    {
        IndexPages.Add(page.Sha256, entries);
        foreach (MapSurfaceIndexEntry entry in entries)
            if (!ByKey.TryAdd(entry.Key, entry)) throw new MapDocumentException("duplicate surface index key");
    }
    internal void Loaded(MapSurfacePatch patch)
    {
        ByKey[patch.Key] = ByKey[patch.Key] with { Loaded = true };
        Baselines[patch.Key] = patch.Clone();
    }
    internal MapSurfaceStorageIndex Clone()
    {
        var copy = new MapSurfaceStorageIndex(Directory, OriginalRefs);
        foreach (var page in DirectoryPages) copy.DirectoryPages.Add(page.Key, page.Value);
        foreach (var page in IndexPages) copy.IndexPages.Add(page.Key, page.Value);
        foreach (var entry in ByKey) copy.ByKey.Add(entry.Key, entry.Value);
        foreach (var patch in Baselines) copy.Baselines.Add(patch.Key, patch.Value.Clone());
        return copy;
    }
    internal static List<MapSurfaceRef> CopyRefs(IEnumerable<MapSurfaceRef> refs) => refs.Select(s => s with
    {
        IndoorSpan = s.IndoorSpan is { } span ? span with { DomainTags = Array.AsReadOnly(span.DomainTags.ToArray()) } : null,
    }).ToList();
}
