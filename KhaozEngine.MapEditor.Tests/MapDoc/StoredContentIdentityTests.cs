using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Storage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>The stored whole token reads pinned tile content, verified, and agrees across storage forms.</summary>
public sealed class StoredContentIdentityTests
{
    static readonly MapResolveOptions V2 = new("headless", 1, "options", ResolverVersion: 2);
    internal static string Token(MapDocument doc) => MapAuthoredIdentityV2.Compute(doc, SurfaceStorageFixtures.Assets(), V2);
    internal static string Token(string directory) =>
        MapAuthoredIdentityV2.Compute(MapStoredSurfaceSource.Open(directory), SurfaceStorageFixtures.Assets(), V2);

    /// <summary>Placements, a spawn, a player spawn and sculpt spread over document tiles 0, 1 and 2.</summary>
    internal static MapDocument Content()
    {
        MapDocument doc = SurfaceStorageFixtures.ThreeSurfaces();
        doc.Placements.Add(SurfaceStorageFixtures.ExplicitYPlacement("tree-1", 10f, 10f));
        doc.Placements.Add(SurfaceStorageFixtures.ExplicitYPlacement("tree-2", 100f, 12f));
        doc.Placements.Add(new MapPlacement
        {
            Id = "bound-1",
            Kind = "scenery",
            AssetId = "tree",
            X = 61.5f,
            Z = 1.5f,
            SupportBinding = new(MapSupportBindingKind.Surface, "ground", null, null, null, null),
        });
        doc.Spawns.Add(new MapSpawn { Id = "spawn-1", ArchetypeId = "wolf", X = 20f, Z = 20f });
        doc.PlayerSpawns.Add(new MapPlayerSpawn { Id = "start-1", X = 30f, Z = 30f, Yaw = 0.5f });
        doc.TerrainOverrides = new MapTerrainOverrides();
        doc.TerrainOverrides.SetDelta(4, 4, 0.5f);
        doc.TerrainOverrides.SetDelta(300, 4, -0.25f);
        return doc;
    }

    static string TileFileHolding(string directory, string text) => Directory
        .EnumerateFiles(Path.Combine(directory, "tiles"), "t_*.json", SearchOption.AllDirectories)
        .Single(path => File.ReadAllText(path).Contains(text, StringComparison.Ordinal));

    static void AssertNamesNoOuterPath(string message, string directory)
    {
        string outside = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory))!;
        Assert.DoesNotContain(outside, message.Replace(directory, "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    static void AssertNamesNoPathOrFile(string message, string directory)
    {
        Assert.DoesNotContain(Path.TrimEndingDirectorySeparator(directory), message, StringComparison.Ordinal);
        Assert.DoesNotContain(MapTiledFile.ManifestName, message, StringComparison.Ordinal);
        Assert.DoesNotContain(".json", message, StringComparison.Ordinal);
        Assert.DoesNotContain("t_", message, StringComparison.Ordinal);
    }

    static string Refusal(string directory) =>
        Assert.Throws<MapDocumentException>(() => Token(directory)).Message;

    /// <summary>The pinned tile entry whose file holds <paramref name="text"/>.</summary>
    internal static MapTileEntry PinnedTileHolding(string directory, string text)
    {
        MapTiledFile.ReadManifest(directory, new MapDocumentLoadOptions(), out MapTileIndex index);
        return index.Entries.Single(e => File.ReadAllText(MapTileFile.PathOf(directory, e.Coord, e.Hash))
            .Contains(text, StringComparison.Ordinal));
    }

    internal static void EditManifest(string directory, Action<JsonObject> edit)
    {
        string path = Path.Combine(directory, MapTiledFile.ManifestName);
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(root);
        File.WriteAllText(path, root.ToJsonString());
    }

    static void SetPinnedHash(string directory, MapTileCoord coord, string hash) => EditManifest(directory, root =>
    {
        JsonObject tile = root["tiles"]!.AsArray().Select(n => n!.AsObject())
            .Single(t => (int)t["x"]! == coord.X && (int)t["z"]! == coord.Z);
        tile["hash"] = hash;
    });

    /// <summary>Hand edits one placement in its tile file, renames the file to the canonical hash of the edited
    /// content and pins that hash in the manifest, the way a consistent save would.</summary>
    internal static void RewritePlacement(string directory, string id, Action<JsonObject> edit)
    {
        MapTileEntry entry = PinnedTileHolding(directory, $"\"{id}\"");
        string path = MapTileFile.PathOf(directory, entry.Coord, entry.Hash);
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(root["placements"]!.AsArray().Select(n => n!.AsObject()).Single(p => (string)p["id"]! == id));
        string json = root.ToJsonString();
        MapDocRegistry registry = MapDocRegistry.CreateDefault();
        string hash = MapDocumentHash.OfLists(MapTileFile.Parse(json, "rewrite", entry.Coord, entry.Hash, registry).Lists, registry);
        File.Delete(path);
        File.WriteAllText(MapTileFile.PathOf(directory, entry.Coord, hash), json);
        SetPinnedHash(directory, entry.Coord, hash);
    }

    [Fact]
    public void StoredContent_MonolithicTiledAndRepackedShareOneToken() => TiledDocFixture.InDirectory(tiled =>
        TiledDocFixture.InDirectory(repacked => TiledDocFixture.InDirectory(scratch =>
    {
        MapDocument doc = Content();
        string expected = Token(doc);
        string monolithic = Path.Combine(scratch, "zone.json");
        MapDocumentFile.Save(doc, monolithic);
        MapDocumentFile.SaveTiled(doc, tiled);
        MapTiledFile.Save(doc, repacked, MapDocRegistry.CreateDefault(), null,
            new MapSurfacePacking(IndexBlockSlots: 1, DirectoryBlockPages: 2));
        Assert.True(MapDocumentFile.LoadTiled(tiled).Tiles!.Entries.Count >= 2);

        Assert.Equal(expected, Token(MapDocumentFile.Load(monolithic)));
        Assert.Equal(expected, Token(MapDocumentFile.LoadTiled(tiled)));
        Assert.Equal(expected, Token(tiled));
        Assert.Equal(expected, Token(repacked));
    })));

    [Fact]
    public void StoredContent_MovingOnePlacementInTheTiledCopyChangesTheToken() => TiledDocFixture.InDirectory(directory =>
    {
        MapDocumentFile.SaveTiled(Content(), directory);
        string before = Token(directory);
        MapDocument loaded = MapDocumentFile.LoadTiled(directory);
        loaded.Placements.Single(p => p.Id == "tree-2").X += 1f;
        MapDocumentFile.SaveTiled(loaded, directory);

        string after = Token(directory);
        Assert.NotEqual(before, after);
        Assert.Equal(Token(loaded), after);
    });

    [Fact]
    public void StoredContent_AlteredTileBytesRefuseCorrupt() => TiledDocFixture.InDirectory(directory =>
    {
        MapDocumentFile.SaveTiled(Content(), directory);
        MapStoredSurfaceSource source = MapStoredSurfaceSource.Open(directory);
        MapTileCoord coord = PinnedTileHolding(directory, "\"tree-2\"").Coord;
        string path = TileFileHolding(directory, "\"tree-2\"");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"tree-2\"", "\"tree-3\"", StringComparison.Ordinal));

        string message = Assert.Throws<MapDocumentException>(() =>
            MapAuthoredIdentityV2.Compute(source, SurfaceStorageFixtures.Assets(), V2)).Message;
        Assert.Contains("Corrupt", message);
        AssertNamesNoOuterPath(message, directory);
        Assert.Equal($"Corrupt pinned tile ({coord.X}, {coord.Z}).", message);
        AssertNamesNoPathOrFile(message, directory);
    });

    [Fact]
    public void StoredContent_RemovedTileFileRefusesMissing() => TiledDocFixture.InDirectory(directory =>
    {
        MapDocumentFile.SaveTiled(Content(), directory);
        MapStoredSurfaceSource source = MapStoredSurfaceSource.Open(directory);
        MapTileCoord coord = PinnedTileHolding(directory, "\"tree-2\"").Coord;
        File.Delete(TileFileHolding(directory, "\"tree-2\""));

        string message = Assert.Throws<MapDocumentException>(() =>
            MapAuthoredIdentityV2.Compute(source, SurfaceStorageFixtures.Assets(), V2)).Message;
        Assert.Contains("Missing", message);
        AssertNamesNoOuterPath(message, directory);
        Assert.Equal($"Missing pinned tile ({coord.X}, {coord.Z}).", message);
        AssertNamesNoPathOrFile(message, directory);
    });

    [Fact]
    public void StoredContent_DuplicatePlacementIdAcrossTilesRefusesCorrupt() => TiledDocFixture.InDirectory(directory =>
    {
        MapDocumentFile.SaveTiled(Content(), directory);
        MapTileCoord first = PinnedTileHolding(directory, "\"tree-1\"").Coord;
        MapTileCoord second = PinnedTileHolding(directory, "\"tree-2\"").Coord;
        Assert.NotEqual(first, second);
        RewritePlacement(directory, "tree-2", p => p["id"] = "tree-1");

        MapTiledFile.ReadManifest(directory, new MapDocumentLoadOptions(), out MapTileIndex index);
        MapTileCoord later = index.Entries.Last(e => e.Coord == first || e.Coord == second).Coord;
        string message = Refusal(directory);
        Assert.Equal($"Corrupt pinned tile ({later.X}, {later.Z}).", message);
        AssertNamesNoPathOrFile(message, directory);
    });

    [Theory]
    [InlineData("upper")]
    [InlineData("short")]
    [InlineData("../x")]
    [InlineData("../../../../../x")]
    public void StoredContent_PinnedTileHashThatIsNotLowercaseHexRefusesCorruptWithoutReading(string shape) =>
        TiledDocFixture.InDirectory(directory =>
    {
        MapDocumentFile.SaveTiled(Content(), directory);
        MapTileEntry entry = PinnedTileHolding(directory, "\"tree-2\"");
        string hash = shape switch
        {
            "upper" => entry.Hash.ToUpperInvariant(),
            "short" => entry.Hash[..63],
            _ => shape,
        };
        if (hash.Contains('/'))
        {
            // A read of the path a traversal hash names finds no file and would refuse Missing, never Corrupt.
            string named = Path.GetFullPath(MapTileFile.PathOf(directory, entry.Coord, hash));
            Assert.False(File.Exists(named));
            if (shape.Length > 4) Assert.False(named.StartsWith(Path.GetFullPath(directory), StringComparison.Ordinal));
        }
        SetPinnedHash(directory, entry.Coord, hash);

        string message = Refusal(directory);
        Assert.Equal($"Corrupt pinned tile ({entry.Coord.X}, {entry.Coord.Z}).", message);
        AssertNamesNoPathOrFile(message, directory);
    });

    [Fact]
    public void StoredContent_PinnedTileHashSchemeMismatchRefusesWithItsMessage() => TiledDocFixture.InDirectory(directory =>
    {
        MapDocumentFile.SaveTiled(Content(), directory);
        int other = MapDocumentHash.SchemeVersion + 1;
        EditManifest(directory, root => root["schemeVersion"] = other);

        string message = Refusal(directory);
        Assert.Equal($"Whole authored identity requires tile hash scheme {MapDocumentHash.SchemeVersion}, " +
            $"the pinned manifest uses {other}.", message);
        AssertNamesNoPathOrFile(message, directory);
    });
}
