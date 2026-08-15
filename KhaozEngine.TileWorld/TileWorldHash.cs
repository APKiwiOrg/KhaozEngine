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

    /// <summary>The hash a save of this region would record. Hashing TRIMS the region, because it hashes the
    /// canonical write, which drops layers that are all-default. That is content-preserving and exactly what a
    /// save does, but a layer array a caller took from an <c>OrAlloc</c> accessor before the hash can be left
    /// orphaned by it, so re-take the reference afterwards.</summary>
    public static string OfRegion(TileRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        return OfRegionBytes(TileRegionFile.WriteCanonical(region));
    }

    /// <summary>The world identity of a document, from loaded regions' canonical bytes plus stored hashes for
    /// regions known but not materialised. Trims every loaded region, with the caveat on
    /// <see cref="OfRegion"/>.</summary>
    public static string OfWorld(TileWorldDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        IEnumerable<(RegionCoord, string)> regions = doc.Regions.Values.Select(r => (r.Coord, OfRegion(r)))
            .Concat(doc.UnloadedRegionHashes.Select(k => (k.Key, k.Value)));
        return OfManifestRegions(doc.TileSize, doc.PlaneCount, doc.PlaneHeight, regions);
    }

    /// <summary>The same composition from a manifest's stored region hashes. Every number in the digest is
    /// formatted through the invariant culture, so one world has one identity on every machine whatever the
    /// ambient culture is.</summary>
    public static string OfManifestRegions(float tileSize, int planeCount, float planeHeight, IEnumerable<(RegionCoord Coord, string Hash)> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        var seen = new HashSet<RegionCoord>();
        var sb = new StringBuilder();
        sb.Append(Domain).Append(Inv(SchemeVersion)).Append('\n');
        sb.Append(tileSize.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append(Inv(planeCount)).Append('\n');
        sb.Append(planeHeight.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        foreach ((RegionCoord c, string h) in regions.OrderBy(r => r.Coord.Rz).ThenBy(r => r.Coord.Rx))
        {
            // Both of these are a caller handing over a region list that cannot describe one world. Digesting
            // it anyway would mint an identity for a world that does not exist, and the mismatch would then
            // surface as a client/server desync with no way back to the malformed list that caused it.
            if (h is null) throw new TileWorldException($"region {c}: hash is null");
            if (!seen.Add(c)) throw new TileWorldException($"region {c} is listed twice");
            sb.Append(Inv(c.Rx)).Append(' ').Append(Inv(c.Rz)).Append(' ').Append(h.ToLowerInvariant()).Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    // Every integer in the digest goes through the invariant culture. StringBuilder.Append(int) formats with
    // the CURRENT culture, so a negative region coordinate on a machine whose culture has its own minus sign
    // would digest different bytes for the same world, and an identity that depends on the thread culture is
    // worse than no identity at all. Same reason the region file names are written invariant.
    static string Inv(int value) => value.ToString(CultureInfo.InvariantCulture);
}
