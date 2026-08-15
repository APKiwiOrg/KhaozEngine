using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld;

/// <summary>A world opened from disk with regions materialised on demand: the manifest is read once, and each
/// region file is read, hash-checked and attached to <see cref="Document"/> the first time something asks for
/// it. The streaming client's entry point. <see cref="TileWorldFile.Load"/> is this plus load-everything.</summary>
public sealed class TileWorldSource
{
    readonly Dictionary<RegionCoord, string> _known;

    /// <summary>The world directory this source reads from.</summary>
    public string Directory { get; }
    /// <summary>The document being filled in, header fields already populated from the manifest.</summary>
    public TileWorldDocument Document { get; }
    /// <summary>Every region the manifest lists, loaded or not.</summary>
    public IReadOnlyCollection<RegionCoord> KnownRegions => _known.Keys;

    TileWorldSource(string directory, TileWorldDocument document, Dictionary<RegionCoord, string> known)
    {
        Directory = directory; Document = document; _known = known;
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
        return new TileWorldSource(directory, doc, known);
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
