using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace KhaozEngine.MapDoc;

/// <summary>The world identity of a map document: SHA-256 over canonical bytes, per tile and for the global
/// half, composed into one digest. Replaces "serialize the whole document and hash the text", which cost a
/// full canonical serialize plus several full-size buffer copies per call.
/// <para>The two forms of the same world produce the same identity, so a game can convert a monolithic
/// document to the tiled form without a coordinated client and server release.</para>
/// <para>What is in the hash: everything that shapes terrain, scatter, or authored content, plus
/// <c>tileSize</c>. What is NOT: <c>displayName</c> and <c>$schema</c>. The hash answers "is the ground under
/// this player the same ground", and renaming a zone must not desync a live server from its clients.</para>
/// <para>The claim is scoped, not flat. The four bucketed lists (placements, spawns, player spawns, sculpt)
/// are sorted before hashing, so their hash depends only on content. <c>exclusions</c>,
/// <c>scatterOverrides</c>, <c>regions</c> and the various tag lists are hashed in DOCUMENT ORDER:
/// <c>scatterOverrides</c> has to be, because <see cref="MapRuntime.BuildScatterConfig"/> walks it in
/// document order and the resulting config is order-sensitive, so reordering it changes the world and the
/// hash is right to notice.</para></summary>
public static class MapDocumentHash
{
    /// <summary>Hash scheme version, folded into every composed digest. Bumping it invalidates every stored
    /// hash on purpose, which is what a canonicalization change must do. Changing the DECLARATION ORDER of a
    /// feature DTO's properties counts: <see cref="MapFeatureConverter"/> serializes a feature through
    /// reflection, so the manifest hash inherits System.Text.Json's member ordering for those types, which is
    /// stable for a given assembly build but is not a contractual guarantee.</summary>
    public const int SchemeVersion = 1;

    /// <summary>Domain separator, so a digest computed under one scheme can never be mistaken for one
    /// computed under another and a value can never slide from one field into the next.</summary>
    const string Domain = "kemap/";

    /// <summary>The canonical hash of one document tile's four content lists, lower hex.</summary>
    public static string OfTile(MapSpatialIndex index, MapTileCoord tile, MapDocRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        return OfLists(MapTileLists.Of(index, tile), registry ?? MapDocRegistry.CreateDefault());
    }

    /// <summary>The canonical hash of four content lists that did not come from a spatial index (a parsed tile
    /// file, on the verification path).</summary>
    internal static string OfLists(in MapTileLists lists, MapDocRegistry registry)
    {
        JsonSerializerOptions options = MapDocumentFile.CreateCompactOptions(registry);
        MapTileLists local = lists;
        return MapCanonical.HashHex(w => MapCanonical.WriteTileBody(w, local, options, schemaRef: null));
    }

    /// <summary>The canonical hash of the global half: format version, id, bounds, tileSize, sculptCellSize,
    /// terrain, scatter and companion layers, exclusions, scatter overrides and regions. Excludes
    /// <c>displayName</c>, <c>$schema</c> and the tile index itself.</summary>
    public static string OfManifest(MapDocument doc, MapDocRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        JsonSerializerOptions options = MapDocumentFile.CreateCompactOptions(registry ?? MapDocRegistry.CreateDefault());
        return MapCanonical.HashHex(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("formatVersion", doc.FormatVersion);
            w.WriteString("id", doc.Id);
            MapCanonical.WriteGlobals(w, doc, options);
            w.WriteEndObject();
        });
    }

    /// <summary>Composes a world identity from the manifest hash and the per-tile hashes, ascending (Z, X).
    /// The input is <c>"kemap/&lt;SchemeVersion&gt;\n"</c>, the manifest hash and a newline, then one
    /// <c>"{x},{z}={hash}\n"</c> line per occupied tile, every integer formatted with
    /// <see cref="CultureInfo.InvariantCulture"/>.</summary>
    /// <exception cref="MapDocumentException">The entries are not strictly ascending in (Z, X), so an
    /// unordered caller fails loudly instead of minting a second identity for the same world.</exception>
    public static string Compose(string manifestHash, IEnumerable<MapTileEntry> tiles)
    {
        ArgumentException.ThrowIfNullOrEmpty(manifestHash);
        ArgumentNullException.ThrowIfNull(tiles);

        using var sink = new MapCanonical.HashingBufferWriter();
        Append(sink, Domain + SchemeVersion.ToString(CultureInfo.InvariantCulture) + "\n");
        Append(sink, manifestHash);
        Append(sink, "\n");

        bool first = true;
        MapTileCoord previous = default;
        foreach (MapTileEntry e in tiles)
        {
            if (!first && !IsAscending(previous, e.Coord))
                throw new MapDocumentException(
                    $"world hash composition needs tiles strictly ascending in (Z, X): ({previous.X}, {previous.Z}) then ({e.Coord.X}, {e.Coord.Z}).");
            first = false;
            previous = e.Coord;
            Append(sink, string.Create(CultureInfo.InvariantCulture, $"{e.Coord.X},{e.Coord.Z}={e.Hash}\n"));
        }
        return Convert.ToHexStringLower(sink.GetHashAndReset());
    }

    /// <summary>The world identity of a document. On a tiled document this reads the hashes out of the
    /// manifest index and never opens a tile file, so it costs one small composition regardless of world
    /// size.
    /// <para>The consequence, stated because it is a trap: on a tiled document the per-tile half comes from
    /// the INDEX, not from the in-memory content, so an edited-but-unsaved document still reports its
    /// on-disk identity. <see cref="MapDocumentFile.SaveTiled"/> refreshes
    /// <see cref="MapDocument.Tiles"/> as it commits, so the value is current again the moment the edit is
    /// durable, which is the only moment a world identity is worth quoting anyway.</para></summary>
    public static string OfWorld(MapDocument doc, MapDocRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        registry ??= MapDocRegistry.CreateDefault();
        string manifest = OfManifest(doc, registry);
        if (doc.Tiles is { } index) return Compose(manifest, index.Entries);

        MapSpatialIndex spatial = MapSpatialIndex.Build(doc);
        var entries = new List<MapTileEntry>(spatial.OccupiedTiles.Count);
        foreach (MapTileCoord tile in spatial.OccupiedTiles)
            entries.Add(new MapTileEntry(tile, OfTile(spatial, tile, registry), Loaded: true));
        return Compose(manifest, entries);
    }

    static bool IsAscending(MapTileCoord a, MapTileCoord b) =>
        b.Z > a.Z || (b.Z == a.Z && b.X > a.X);

    static void Append(MapCanonical.HashingBufferWriter sink, string text) =>
        sink.Append(Encoding.UTF8.GetBytes(text));
}
