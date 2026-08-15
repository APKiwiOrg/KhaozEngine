using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld;

/// <summary>An opened world directory: the manifest is read up front, the regions are materialised on demand
/// and hash-checked as they land.</summary>
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
}
