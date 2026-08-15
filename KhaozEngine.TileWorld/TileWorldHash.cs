using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace KhaozEngine.TileWorld;

/// <summary>World identity: SHA-256 per region over its canonical file bytes, composed with the three header
/// fields that shape the ground into one digest. Excludes id, display name, catalog paths, the object id
/// allocator and the format version, so renaming a world or re-pointing its catalogs never desyncs a live
/// server from its clients. This is what a game's client/server digest check compares.</summary>
public static class TileWorldHash
{
    /// <summary>Folded into every digest. Bump on any canonicalisation change, on purpose.</summary>
    public const int SchemeVersion = 1;
    const string Domain = "ketw/";

    /// <summary>Lower-hex SHA-256 of a region file's exact bytes.</summary>
    public static string OfRegionBytes(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The hash a save of this region would record.</summary>
    public static string OfRegion(TileRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        return OfRegionBytes(TileRegionFile.WriteCanonical(region));
    }

    /// <summary>The world identity of a document, from loaded regions' canonical bytes plus stored hashes for
    /// regions known but not materialised.</summary>
    public static string OfWorld(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        IEnumerable<(RegionCoord, string)> regions = doc.Regions.Values.Select(r => (r.Coord, OfRegion(r)))
            .Concat(doc.UnloadedRegionHashes.Select(k => (k.Key, k.Value)));
        return OfManifestRegions(doc.TileSize, doc.PlaneCount, doc.PlaneHeight, regions);
    }

    /// <summary>The same composition from a manifest's stored region hashes.</summary>
    public static string OfManifestRegions(float tileSize, int planeCount, float planeHeight, IEnumerable<(RegionCoord Coord, string Hash)> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        var sb = new StringBuilder();
        sb.Append(Domain).Append(Inv(SchemeVersion)).Append('\n');
        sb.Append(tileSize.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(Inv(planeCount)).Append('\n');
        sb.Append(planeHeight.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        foreach ((RegionCoord c, string h) in regions.OrderBy(r => r.Coord.Rz).ThenBy(r => r.Coord.Rx))
            sb.Append(Inv(c.Rx)).Append(' ').Append(Inv(c.Rz)).Append(' ').Append(h.ToLowerInvariant()).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    // Every integer in the digest goes through the invariant culture. StringBuilder.Append(int) formats with
    // the CURRENT culture, so a negative region coordinate on a machine whose culture has its own minus sign
    // would digest different bytes for the same world, and an identity that depends on the thread culture is
    // worse than no identity at all. Same reason the region file names are written invariant.
    static string Inv(int value) => value.ToString(CultureInfo.InvariantCulture);
}
