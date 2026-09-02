using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld;

/// <summary>A world opened from disk with regions materialised on demand: the manifest is read once, and each
/// region file is read, hash-checked and attached to <see cref="Document"/> the first time something asks for
/// it. The streaming client's entry point. <see cref="TileWorldFile.Load"/> is this plus load-everything.</summary>
public sealed class TileWorldSource
{
    readonly Dictionary<RegionCoord, string> _known;
    // The manifest's marker index, read once at Open. Keyed by name, which is document-unique, and holding the
    // manifest's own copy rather than a region's, because none of the regions is materialised yet.
    readonly Dictionary<string, TileMarker> _markers;

    /// <summary>The world directory this source reads from.</summary>
    public string Directory { get; }
    /// <summary>The document being filled in, header fields already populated from the manifest.</summary>
    public TileWorldDocument Document { get; }
    /// <summary>Every region the manifest lists, loaded or not.</summary>
    public IReadOnlyCollection<RegionCoord> KnownRegions => _known.Keys;

    /// <summary>Every marker the manifest indexes, in name order, without a region read. Empty for a world saved
    /// by an engine older than the index.</summary>
    public IReadOnlyCollection<string> Markers => _markers.Keys;

    TileWorldSource(string directory, TileWorldDocument document, Dictionary<RegionCoord, string> known,
        Dictionary<string, TileMarker> markers)
    {
        Directory = directory; Document = document; _known = known; _markers = markers;
    }

    /// <summary>
    /// A marker by name, out of the manifest's index, with NO region read. What a client uses to find the spawn
    /// before it has streamed anything: <see cref="TileWorldDocument.FindMarker"/> walks LOADED regions only, so
    /// asking it first would mean materialising regions one at a time until one carried the marker and then
    /// unloading the ones that did not.
    /// <para>A COPY, so a caller that nudges the one it was handed changes nothing in the index and nothing in the
    /// region once that region is loaded. The index is derived from the regions, which stay the source of truth.</para>
    /// <para>Null for an unknown name, and for every name in a world saved by an engine older than the index. A
    /// world written by this engine always carries one, so the fallback a caller needs is the ordinary
    /// <see cref="TileWorldDocument.FindMarker"/> over loaded regions.</para>
    /// </summary>
    /// <param name="name">The marker's document-unique name.</param>
    public TileMarker? FindMarker(string name)
    {
        if (name is null || !_markers.TryGetValue(name, out TileMarker? m)) return null;
        return new TileMarker
        {
            Name = m.Name, X = m.X, Z = m.Z, Plane = m.Plane,
            Tags = m.Tags is null ? null : new List<string>(m.Tags),
        };
    }

    /// <summary>Reads the manifest (migrating it when needed) and returns a source with no region loaded yet.</summary>
    public static TileWorldSource Open(string directory, TileWorldLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        options ??= new TileWorldLoadOptions();
        TileWorldManifest m = TileWorldFile.ReadManifest(directory, options);
        var doc = new TileWorldDocument
        {
            Id = m.Id, DisplayName = m.DisplayName, TileSize = m.TileSize, PlaneCount = m.PlaneCount,
            PlaneHeight = m.PlaneHeight, NextObjectId = m.NextObjectId,
        };
        doc.CatalogPaths.AddRange(m.CatalogPaths);
        var known = new Dictionary<RegionCoord, string>();
        foreach (TileWorldManifestRegion r in m.Regions)
        {
            var c = new RegionCoord(r.Rx, r.Rz);
            if (!known.TryAdd(c, r.Hash))
                throw new TileWorldException($"{TileWorldFile.ManifestPath(directory)}: region {c} is listed twice");
            doc.UnloadedRegionHashes[c] = r.Hash;
        }
        var markers = new Dictionary<string, TileMarker>(StringComparer.Ordinal);
        foreach (TileWorldManifestMarker mk in m.Markers)
            markers[mk.Name] = new TileMarker
            {
                Name = mk.Name, X = mk.X, Z = mk.Z, Plane = mk.Plane,
                Tags = mk.Tags is null ? null : new List<string>(mk.Tags),
            };
        return new TileWorldSource(directory, doc, known, markers);
    }

    /// <summary>Materialises the region, checking its bytes against the manifest hash. Null when the manifest
    /// does not list it.</summary>
    public TileRegion? EnsureLoaded(RegionCoord c)
    {
        if (Document.GetRegion(c) is TileRegion loaded) return loaded;
        if (!_known.TryGetValue(c, out string? expectedHash)) return null;
        string path = TileWorldFile.RegionPath(Directory, c);
        byte[] bytes = TileWorldFile.ReadRegionBytes(Directory, c);
        string actual = TileWorldHash.OfRegionBytes(bytes);
        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new TileWorldException($"{path}: region {c} hash {actual} does not match the manifest's {expectedHash} (torn write, or a hand edit that skipped the manifest)");
        TileRegion region = TileRegionFile.Parse(bytes, c, Document.PlaneCount, path);
        Document.AttachRegion(region);
        return region;
    }

    /// <summary>True when the manifest lists this region, loaded or not.</summary>
    public bool IsKnown(RegionCoord c) => _known.ContainsKey(c);

    /// <summary>True when this region is materialised in memory right now.</summary>
    public bool IsLoaded(RegionCoord c) => Document.GetRegion(c) is not null;

    /// <summary>Loads every known region touching the rect.</summary>
    public IReadOnlyList<TileRegion> EnsureLoaded(TileRect rect)
    {
        var result = new List<TileRegion>();
        if (rect.IsEmpty) return result;
        RegionCoord lo = RegionCoord.Of(rect.X, rect.Z), hi = RegionCoord.Of(rect.X1 - 1, rect.Z1 - 1);
        for (int rz = lo.Rz; rz <= hi.Rz; rz++)
            for (int rx = lo.Rx; rx <= hi.Rx; rx++)
                if (EnsureLoaded(new RegionCoord(rx, rz)) is TileRegion r) result.Add(r);
        return result;
    }

    /// <summary>Drops a clean region from memory, keeping its hash so a later save carries it through untouched.
    /// Refuses a dirty region: save first, or the edit is lost.</summary>
    public bool Unload(RegionCoord c)
    {
        TileRegion? r = Document.GetRegion(c);
        if (r is null) return false;
        if (r.Dirty) throw new TileWorldException($"region {c} has unsaved changes and cannot be unloaded");
        // Hashed from the region, not read out of _known, which is the manifest as it stood at Open. A region
        // that was loaded, edited and saved has new bytes on disk and a new hash in the manifest, while _known
        // still holds the old one. Recording that stale hash here would put it back into the next save's
        // manifest over bytes it does not describe, and every later load would then refuse the world. Every
        // file this engine writes is canonical, and a clean region reloaded from one re-serialises to the
        // same bytes, so this is the hash of what is on disk.
        string hash = TileWorldHash.OfRegion(r);
        Document.RemoveRegion(c);
        Document.UnloadedRegionHashes[c] = hash;
        // _known moves with it, or the next EnsureLoaded checks the file against the Open-time hash and calls
        // a correctly saved region a torn write. This is also what makes a region CREATED after Open reachable:
        // it was never in the manifest we read, so without this it would be permanently unknown to this source
        // even though the save put it in the manifest on disk.
        _known[c] = hash;
        return true;
    }
}
