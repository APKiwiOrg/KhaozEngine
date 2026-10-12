using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

/// <summary>Persisted identities pinned to literals derived outside the engine, from their documented canonical forms
/// and the fixture definitions. A test that takes an input from the engine says so. A change here is an identity
/// migration.</summary>
public class MapPersistedIdentityTests
{
    static readonly MapWorldGrids Grids = new(64f, 1, 4, Vector2.Zero);
    static readonly MapNavTileOptions Options = new(2f, "profile-a", "legacy-stepper");

    [Fact]
    public void DoorwayPlacementDigest_IsPinned()
    {
        // Over the doorway's id, numeric id 1, asset id, the SHA-256 of its collider bytes, no selection, the float bits
        // of (0.23, 0, 0.17), yaw 0.371, scale 1.137 and raise 0, and the interaction policy hash.
        var g = MapPlacementShapes.Resolve(NativeWorldFixtures.Doorway(0.371f, 1.137f).Resolved)
            .Single(p => p.PlacementId == "doorway");
        Assert.Equal("217765858ec4d9176bdb2704cf5a744802fa2ad2b9c8ef0f93ae2359295534a1", g.Digest);
    }

    [Fact]
    public void StackedCaveUpperCeilingChunkDigest_IsPinned()
    {
        // 72 faces of 36 flat cells at y 6.5, split south-west to north-east, emitted as (A, C, B) about anchor
        // (3, 7, 3), each owned by its slot cell and parent triangle with the ceiling role.
        var set = MapTerrainPhysics.Compile(NativeWorldFixtures.StackedCave().View, new MapTerrainChunkPolicy());
        Assert.Equal("1414db9235eb9409c416ce533d4b2041e41350006d0ff83e49cc60d2fae7237f",
            set.Chunks.Single(c => c.ChunkId == "cave-upper-ceiling/0,0/0,0,64").Digest);
    }

    [Fact]
    public void LegacyTerrainIdentity_IsPinned()
    {
        // Composed from the terrain block and sculpt tile digests MapLegacyTerrainDigestTests pins.
        Assert.Equal("0cad1729a54d81cdcd40981d6217a4f3c8973dfa3f79f7f9db8cd5ef6048d403",
            NativeWorldFixtures.BuildLegacyPinnedTerrain().LegacyTerrainIdentity);
    }

    [Fact]
    public void NavGeometryDigestOverAStaticAndASculptTile_AndItsSeam_ArePinned()
    {
        var world = NativeWorldFixtures.BuildLegacyPinnedTerrain();
        var tiles = MapNavTiling.Partition(world, Grids, Options);
        Assert.Equal(new[] { new MapNavTileCoord(0, -1), new MapNavTileCoord(1, -1) }, tiles.Select(t => t.Coord));
        // Tile (0, -1) holds the crate's placement digest, the pinned terrain block and sculpt tile (1, -1).
        Assert.Equal("4f3c9ed5ebac482552989d05ed2bd63204514bde73c03d2f3d29f1daf06ad311", tiles[0].GeometryDigest);
        // Tile (1, -1) holds the terrain block alone. Their seam is the edge x 64 from z -64 to 0, with a 2 m margin.
        Assert.Equal("e253ae086cdc8ba1d83694e1b073a25ff831af4a0b4293320e7225abb149e601", tiles[1].GeometryDigest);
        var seam = Assert.Single(MapNavTiling.Seams(tiles, world, Options));
        Assert.Equal("52f9a46adcd544b1ddb62b6375c8bb2c22f0ba4014435bb589dfc275142746b3", seam.Digest);
    }

    [Fact]
    public void StackedCaveBuildHash_IsPinned()
    {
        // Format 7 changes one native root member, formatVersion 6 to 7, and nothing else this fixture hashes. The test
        // derives the root from the persisted canonical manifest bytes under the documented scheme-one normalization,
        // the content and patch digests from their canonical forms, and both composites from their documented member
        // order. The recorded chunk and placement digests are geometry inputs read from the engine once. At format 6 the
        // derivation reproduces the released pins below, which checks every input. The format 7 pins are that same
        // derivation at format 7, never a value printed by the changed product.
        var fixture = NativeWorldFixtures.StackedCave();
        var world = NativeWorldFixtures.BuildStackedCave();
        Assert.Equal(RecordedPlacements, world.Placements.Select(p => (p.PlacementId, p.Digest)));
        Assert.Equal(RecordedChunks, world.Terrain.Chunks.Select(c => (c.ChunkId, c.Digest)));
        JsonObject manifest = CanonicalManifest(fixture.Document);
        var detached = MapDocumentFile.LoadText(MapDocumentFile.SaveText(fixture.Document));
        detached.FormatVersion = 6;
        Assert.Equal(MapSurfaceSemantics.RootDigest(detached), RootDigest(manifest, 6));

        string authoredSix = Authored(RootDigest(manifest, 6), fixture);
        Assert.Equal("436a2731a92851e05bc667b3d02bb89e2980922d894348a704f950df55b78235", authoredSix);
        Assert.Equal("655026f34e7bc1dd9a93157d406c28547b8b3d832fec1dc041a7e87ad1b0efc3", Build(authoredSix));

        // The only changed input is the root's formatVersion member, 7 in place of 6. Content, patches, closure, options
        // and every geometry digest are the inputs that reproduced the format 6 pins above.
        string authoredSeven = Authored(RootDigest(manifest, 7), fixture);
        Assert.Equal("a7c7fe280fdc1d23513313a52723514622c92e46da3edc8e80ac1322e7b28d1b", authoredSeven);
        Assert.Equal("4ce2461c8ddf5e72524ec367fd4c21662c60b1a86cb5cf044b0953c152a5f271", Build(authoredSeven));
        Assert.Equal(authoredSeven, world.AuthoredHash);
        Assert.Equal(Build(authoredSeven), world.BuildHash);
    }

    // Geometry inputs, unchanged by the format advance and checked above against the current build.
    static readonly (string, string)[] RecordedPlacements =
    [
        ("cave-crate", "e38fa49ce04062ebe829a2ccd1c6d3110f1566da7455b24a5e65d5e458d2955c"),
    ];
    static readonly (string, string)[] RecordedChunks =
    [
        ("cave-lower-ceiling/0,0/0,0,64", "df98d2eefe78e50a35fab7df1a71f9ee7639dcc5e6f638b44af4b4fe9b483eca"),
        ("cave-lower-floor/0,0/0,0,64", "acf9ab3e9e9657ba18c8d69828677f056109d0a9f659ac601a6a9903a09cbc94"),
        ("cave-upper-ceiling/0,0/0,0,64", "1414db9235eb9409c416ce533d4b2041e41350006d0ff83e49cc60d2fae7237f"),
        ("cave-upper-floor/0,0/0,0,64", "d73019366d6ef1910a7529f2e5c9882fb5f4529ab3bcbf87c4ca4411d7d3259c"),
        ("shaft-east/0/Back", "c5495194b808527c63b7ad70e798566ced43f3625e8c4fddbba1a7e95e8317df"),
        ("shaft-north/0/Front", "a7b87b077e343ffe090791e9f8e6fbb0dbb77b92d7153bc7703814908a255141"),
        ("shaft-south/0/Back", "d101c4edc8f48c2d11d10d8d5a9058ee8818dd8b93aa69ae2980b4bfec973fb8"),
        ("shaft-west/0/Front", "0fe4136e3462673ed7df69c0c9bf2892705ec15b76e9f79ea1e5124ae850d293"),
    ];

    // The tiled manifest is the persisted root: every native global plus file and storage bookkeeping.
    static JsonObject CanonicalManifest(MapDocument document)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ke-identity-pin-" + Guid.NewGuid().ToString("N"));
        try
        {
            MapDocumentFile.SaveTiled(document, directory);
            return JsonNode.Parse(File.ReadAllBytes(Path.Combine(directory, "map.json")))!.AsObject();
        }
        finally { Directory.Delete(directory, true); }
    }

    static string RootDigest(JsonObject manifest, int formatVersion)
    {
        var root = (JsonObject)manifest.DeepClone();
        foreach (string bookkeeping in new[] { "$schema", "displayName", "schemeVersion", "surfaceStorage", "tiles" }) root.Remove(bookkeeping);
        root["formatVersion"] = formatVersion;
        root["nativeAssets"] = new JsonArray(root["nativeAssets"]!.AsArray()
            .OrderBy(a => a!["id"]!.GetValue<string>(), StringComparer.Ordinal).Select(a => a!.DeepClone()).ToArray());
        return Sha256("kemap/native-root/2\0", w => Normalized(w, root));
    }

    static string Authored(string root, StackedCaveFixture fixture)
    {
        var whole = JsonNode.Parse(MapDocumentFile.SaveText(fixture.Document))!.AsObject();
        Assert.Null(fixture.Document.TerrainOverrides?.Tiles.FirstOrDefault());
        var entries = new List<(string Kind, string Id, string Digest)>();
        foreach (var (member, kind) in new[] { ("placements", "placement"), ("spawns", "spawn"), ("playerSpawns", "playerSpawn") })
            foreach (JsonNode? item in whole[member]?.AsArray() ?? new JsonArray())
            {
                var node = (JsonObject)item!.DeepClone();
                if (kind == "placement") node.Remove("displayName");
                entries.Add((kind, node["id"]!.GetValue<string>(), Sha256("", w =>
                {
                    w.WriteStartArray(); w.WriteStringValue("kemap/native-content/1"); w.WriteStringValue(kind); Normalized(w, node); w.WriteEndArray();
                })));
            }
        string content = Sha256("", w =>
        {
            w.WriteStartObject(); w.WriteString("domain", "kemap/native-content/1"); w.WriteStartArray("entries");
            foreach (var kind in new[] { "placement", "spawn", "playerSpawn" })
                foreach (var entry in entries.Where(e => e.Kind == kind).OrderBy(e => e.Id, StringComparer.Ordinal))
                {
                    w.WriteStartArray(); w.WriteStringValue(entry.Kind); w.WriteStringValue(entry.Id); w.WriteStringValue(entry.Digest); w.WriteEndArray();
                }
            w.WriteEndArray(); w.WriteEndObject();
        });
        MapResolveOptions options = NativeWorldFixtures.ResolveOptions;
        return Sha256("", w =>
        {
            w.WriteStartObject();
            w.WriteString("domain", "kemap/native-authored/2");
            w.WriteString("rootDigest", root);
            w.WriteString("contentDigest", content);
            w.WriteStartArray("patches");
            foreach (var patch in fixture.Document.Surfaces.Patches.OrderBy(p => p.Key))
            {
                w.WriteStartArray(); w.WriteStringValue(patch.Key.SurfaceId); w.WriteNumberValue(patch.Key.SlotX); w.WriteNumberValue(patch.Key.SlotZ);
                w.WriteStringValue(Convert.ToHexStringLower(SHA256.HashData(
                    Encoding.UTF8.GetBytes("kemap/surface-patch/1\0").Concat(MapSurfacePatchCodec.Encode(patch.Value)).ToArray())));
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteString("closure", fixture.Assets.Hash);
            w.WriteString("builderId", options.BuilderId);
            w.WriteNumber("builderVersion", options.BuilderVersion);
            w.WriteString("optionsHash", options.OptionsHash);
            w.WriteNumber("resolverVersion", options.ResolverVersion);
            w.WriteEndObject();
        });
    }

    static string Build(string authored)
    {
        MapWorldBuildOptions options = NativeWorldFixtures.Options();
        return Sha256("", w =>
        {
            w.WriteStartObject();
            w.WriteString("domain", "kemap/built-world/1");
            w.WriteString("authoredHash", authored);
            w.WriteNumber("builderVersion", options.BuilderVersion);
            w.WriteString("consumerPolicyIdentity", options.ConsumerPolicyIdentity);
            w.WriteString("interactionPolicyHash", MapInteractionPolicy.Hash);
            w.WriteNumber("maxTrianglesPerChunk", options.Chunks.MaxTrianglesPerChunk);
            w.WriteString("legacyTerrainIdentity", "");
            foreach (var (name, pairs) in new[] { ("placements", RecordedPlacements), ("terrain", RecordedChunks) })
            {
                w.WriteStartArray(name);
                foreach (var (id, digest) in pairs) { w.WriteStartArray(); w.WriteStringValue(id); w.WriteStringValue(digest); w.WriteEndArray(); }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        });
    }

    // Members in ordinal order at every depth, arrays in stored order, compact.
    static void Normalized(Utf8JsonWriter w, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                w.WriteStartObject();
                foreach (var member in obj.OrderBy(p => p.Key, StringComparer.Ordinal)) { w.WritePropertyName(member.Key); Normalized(w, member.Value); }
                w.WriteEndObject();
                break;
            case JsonArray array:
                w.WriteStartArray();
                foreach (JsonNode? item in array) Normalized(w, item);
                w.WriteEndArray();
                break;
            case null: w.WriteNullValue(); break;
            default: node.WriteTo(w); break;
        }
    }

    static string Sha256(string prefix, Action<Utf8JsonWriter> body)
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.UTF8.GetBytes(prefix));
        using (var w = new Utf8JsonWriter(stream)) body(w);
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }
}
