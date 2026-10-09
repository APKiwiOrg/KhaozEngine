using System;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Storage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>The stored whole token reads pinned tile content, verified, and agrees across storage forms.</summary>
public sealed class StoredContentIdentityTests
{
    static readonly MapResolveOptions V2 = new("headless", 1, "options", ResolverVersion: 2);
    static string Token(MapDocument doc) => MapAuthoredIdentityV2.Compute(doc, SurfaceStorageFixtures.Assets(), V2);
    static string Token(string directory) =>
        MapAuthoredIdentityV2.Compute(MapStoredSurfaceSource.Open(directory), SurfaceStorageFixtures.Assets(), V2);

    /// <summary>Placements, a spawn, a player spawn and sculpt spread over document tiles 0, 1 and 2.</summary>
    static MapDocument Content()
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
        string path = TileFileHolding(directory, "\"tree-2\"");
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"tree-2\"", "\"tree-3\"", StringComparison.Ordinal));

        string message = Assert.Throws<MapDocumentException>(() =>
            MapAuthoredIdentityV2.Compute(source, SurfaceStorageFixtures.Assets(), V2)).Message;
        Assert.Contains("Corrupt", message);
        AssertNamesNoOuterPath(message, directory);
    });

    [Fact]
    public void StoredContent_RemovedTileFileRefusesMissing() => TiledDocFixture.InDirectory(directory =>
    {
        MapDocumentFile.SaveTiled(Content(), directory);
        MapStoredSurfaceSource source = MapStoredSurfaceSource.Open(directory);
        File.Delete(TileFileHolding(directory, "\"tree-2\""));

        string message = Assert.Throws<MapDocumentException>(() =>
            MapAuthoredIdentityV2.Compute(source, SurfaceStorageFixtures.Assets(), V2)).Message;
        Assert.Contains("Missing", message);
        AssertNamesNoOuterPath(message, directory);
    });
}
