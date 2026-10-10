using System;
using System.Text.Json;
using KhaozEngine.MapDoc.Storage;

namespace KhaozEngine.MapDoc.Identity;

/// <summary>Identity of a resolver-1 document's analytic terrain: the terrain block and each sculpt tile, digested
/// separately so a consumer can cover only the sculpt tiles a region touches. Both values are persisted identities
/// that consumers store, so their canonical forms must stay stable. A change is an identity migration.</summary>
public static class MapLegacyTerrainDigest
{
    /// <summary>The lowercase hex SHA-256 of the terrain without sculpt, over the compact canonical JSON
    /// <c>{"domain":"kemap/legacy-terrain-block/1","sculptCellSize":n,"terrain":{...}}</c>. The terrain block is the
    /// <see cref="MapDocument.Terrain"/> object as the whole writer serializes it, members in ordinal order at every
    /// level. The sculpt cell size is the document's, or the default when it has no sculpt block.</summary>
    public static string TerrainBlock(MapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        JsonSerializerOptions options = MapDocumentFile.CreateCompactOptions(MapDocRegistry.CreateDefault());
        return MapCanonical.HashHex(w =>
        {
            w.WriteStartObject();
            w.WriteString("domain", "kemap/legacy-terrain-block/1");
            w.WriteNumber("sculptCellSize", MapCanonical.SculptCellSizeOf(document));
            w.WritePropertyName("terrain");
            MapSurfaceRootProjection.WriteNormalized(w, JsonSerializer.SerializeToNode(document.Terrain, options));
            w.WriteEndObject();
        });
    }

    /// <summary>The lowercase hex SHA-256 of one sculpt tile, exactly the sculpt entry digest of the resolver-2
    /// authored content digest: the compact canonical JSON
    /// <c>["kemap/native-content/1","sculpt",{"deltas":[...],"tileX":x,"tileZ":z}]</c>.</summary>
    public static string SculptTile(MapSculptTile tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        return MapAuthoredContentDigest.SculptDigest(tile);
    }
}
