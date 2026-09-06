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
/// server from its clients. The catalogs' CONTENT is a separate digest, <see cref="OfCatalogs"/>, and
/// <see cref="OfWorldAndCatalogs"/> composes the two: that is the one a client/server connect gate should
/// compare, because the world digest alone cannot see an archetype gaining a collision kind.</summary>
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
        string terrain = OfManifestRegions(doc.TileSize, doc.PlaneCount, doc.PlaneHeight, regions);
        if (doc.FoliageLayers.Count == 0) return terrain;

        var sb = new StringBuilder();
        sb.Append(Domain).Append("foliage/").Append(Inv(SchemeVersion)).Append('\n');
        sb.Append(terrain).Append('\n');
        foreach (TileFoliageLayer layer in doc.FoliageLayers.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            Text(sb, layer.Id);
            sb.Append(Inv(layer.Plane)).Append(' ')
                .Append(Float(layer.OriginX)).Append(' ').Append(Float(layer.OriginZ)).Append(' ')
                .Append(Float(layer.CellSize)).Append(' ').Append(Inv(layer.Width)).Append(' ')
                .Append(Inv(layer.Height)).Append(' ').Append(Inv(layer.Seed)).Append(' ')
                .Append(Float(layer.Spacing)).Append(' ').Append(Float(layer.ScaleMin)).Append(' ')
                .Append(Float(layer.ScaleMax)).Append(' ').Append(Float(layer.RootOffset)).Append(' ')
                .Append(layer.ExcludeIndoors ? '1' : '0').Append(layer.ExcludeSolidObjects ? '1' : '0').Append(' ')
                .Append(Float(layer.DoorClearance)).Append(' ').Append(Float(layer.EdgeFade)).Append('\n');
            foreach (TileFoliageArchetype archetype in layer.Archetypes)
            {
                Text(sb, archetype.Id);
                sb.Append(Float(archetype.Weight)).Append('\n');
            }
            sb.Append("materials ");
            foreach (ushort material in layer.AllowedUnderlays) sb.Append(Inv(material)).Append(' ');
            sb.Append('\n').Append(Convert.ToBase64String(layer.CopyDensity())).Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>
    /// The CATALOGS' identity: every ground material and every object archetype, each field of each, composed
    /// canonically. Independent of file formatting, of comments, and of the order the catalog files were merged in,
    /// because it digests the loaded CONTENT rather than the bytes on disk.
    /// <para>Separate from <see cref="OfWorld"/> on purpose. A world's regions and its catalogs live in different
    /// files with different lifetimes, and <see cref="OfWorld"/> has always excluded the catalog PATHS so that
    /// re-pointing them never desyncs a live server. What it also excluded, and should not have, is the catalogs'
    /// CONTENT: an archetype that gains a <see cref="TileObjectArchetype.CollisionKind"/> bakes a different
    /// collision map, so two heads over one world directory with independently updated catalogs agreed on the world
    /// digest and then disagreed on every wall. <see cref="OfWorldAndCatalogs"/> is the composed digest a netcode
    /// gate should use.</para>
    /// <para>EVERY authored field is in, cosmetic ones included, rather than only the fields the collision baker
    /// reads. The engine does not know which fields a given game treats as decoration, a mesh reference points at
    /// content the client has to ship anyway, and a digest that blessed art drift would be silently wrong for the
    /// first game that dispatched on a tag.</para>
    /// </summary>
    /// <param name="catalogs">The loaded catalogs.</param>
    /// <exception cref="ArgumentNullException"><paramref name="catalogs"/> is null.</exception>
    public static string OfCatalogs(TileWorldCatalogs catalogs)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        var sb = new StringBuilder();
        sb.Append(Domain).Append("catalogs/").Append(Inv(SchemeVersion)).Append('\n');
        // Sorted by id in both tables, because the dictionaries behind them are hash-ordered and a hash layout must
        // never reach a decision. Ordinal for the archetype ids, so the order does not move with the culture either.
        foreach (GroundMaterial m in catalogs.Materials.Values.OrderBy(m => m.Id))
        {
            sb.Append("m ").Append(Inv(m.Id)).Append(' ');
            Text(sb, m.Name);
            Text(sb, m.Color);
            Text(sb, m.Texture);
            sb.Append(m.Kind).Append(' ');
            sb.Append(m.TilesPerMetre?.ToString("R", CultureInfo.InvariantCulture) ?? "-").Append('\n');
        }
        foreach (TileObjectArchetype a in catalogs.Archetypes.Values.OrderBy(a => a.Id, StringComparer.Ordinal))
        {
            sb.Append("a ");
            Text(sb, a.Id);
            Text(sb, a.Name);
            Text(sb, a.MeshRef);
            sb.Append(Inv(a.SizeX)).Append(' ').Append(Inv(a.SizeZ)).Append(' ');
            sb.Append(a.CollisionKind).Append(' ');
            sb.Append(a.IsRoof ? '1' : '0').Append(a.Interactive ? '1' : '0').Append(' ');
            sb.Append(a.YawOffsetDegrees.ToString("R", CultureInfo.InvariantCulture)).Append(' ');
            // Tags keep their AUTHORED order, which is the order the catalog file carries and the order a game
            // reading them sees. Sorting them here would call two different files one archetype.
            sb.Append(Inv(a.Tags?.Count ?? 0)).Append(' ');
            for (int i = 0; i < (a.Tags?.Count ?? 0); i++) Text(sb, a.Tags![i]);
            sb.Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>
    /// The digest a netcode connect gate should compare: <see cref="OfWorld"/> and <see cref="OfCatalogs"/>
    /// composed into one identity, so archetype drift is refused at the door instead of surfacing as a per-step
    /// correction on every wall.
    /// <para>Deliberately NOT either half's own digest, so a head gating on the world alone and a head gating on
    /// this can never accidentally agree. Adopting it is a change on BOTH heads at once: the string every deployed
    /// client computes today is <see cref="OfWorld"/>'s, and that one is unchanged.</para>
    /// </summary>
    /// <param name="doc">The world.</param>
    /// <param name="catalogs">The catalogs its objects reference by id.</param>
    /// <exception cref="ArgumentNullException"><paramref name="doc"/> or <paramref name="catalogs"/> is null.</exception>
    public static string OfWorldAndCatalogs(TileWorldDocument doc, TileWorldCatalogs catalogs)
    {
        string world = OfWorld(doc);
        string content = OfCatalogs(catalogs);
        var sb = new StringBuilder();
        sb.Append(Domain).Append("worldcat/").Append(Inv(SchemeVersion)).Append('\n');
        sb.Append(world).Append('\n').Append(content).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
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
    static string Float(float value) => value.ToString("R", CultureInfo.InvariantCulture);

    // A LENGTH-PREFIXED string, because catalog text is authored and a delimiter that appears inside a name would
    // otherwise let two different catalogs digest the same bytes. Null and empty are distinguished, so an archetype
    // that loses its texture reference is not the same content as one that never had one.
    static void Text(StringBuilder sb, string? value)
    {
        if (value is null) { sb.Append("- "); return; }
        sb.Append(Inv(value.Length)).Append(':').Append(value).Append(' ');
    }
}
