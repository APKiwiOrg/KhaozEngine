using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapEditor;

public sealed class MapDocumentWindowingSpawnTests
{
    [Fact]
    public void Window_opens_at_an_enabled_player_spawn_instead_of_empty_bounds_center()
    {
        WithWorld(doc => doc.PlayerSpawns.Add(Spawn("start", 3)), directory =>
        {
            MapDocument loaded = Load(directory, out MapTileRect? window);
            Assert.Equal(new MapTileCoord(3, 0), window!.Value.Min);
            Assert.Equal("start", Assert.Single(loaded.PlayerSpawns).Id);
            Assert.True(loaded.Tiles!.IsPartial);
        });
    }

    [Fact]
    public void Search_ignores_disabled_spawns()
    {
        WithWorld(doc =>
        {
            doc.PlayerSpawns.Add(Spawn("disabled", 1, false));
            doc.PlayerSpawns.Add(Spawn("enabled", 2));
        }, directory =>
        {
            MapDocument loaded = Load(directory, out MapTileRect? window);
            Assert.Equal(new MapTileCoord(2, 0), window!.Value.Min);
            Assert.Equal("enabled", Assert.Single(loaded.PlayerSpawns).Id);
        });
    }

    [Fact]
    public void Equidistant_spawn_tiles_have_a_stable_coordinate_order()
    {
        WithWorld(doc =>
        {
            doc.PlayerSpawns.Add(Spawn("right", 1));
            doc.PlayerSpawns.Add(Spawn("left", -1));
        }, directory =>
        {
            MapDocument loaded = Load(directory, out MapTileRect? window);
            Assert.Equal(new MapTileCoord(-1, 0), window!.Value.Min);
            Assert.Equal("left", Assert.Single(loaded.PlayerSpawns).Id);
        });
    }

    [Fact]
    public void No_enabled_spawn_keeps_the_bounds_center_fallback()
    {
        WithWorld(doc => doc.PlayerSpawns.Add(Spawn("disabled", 2, false)), directory =>
        {
            Load(directory, out MapTileRect? window);
            Assert.Equal(new MapTileCoord(0, 0), window!.Value.Min);
        });
    }

    [Fact]
    public void Zero_budget_keeps_the_bounds_center_even_with_an_enabled_spawn()
    {
        WithWorld(doc => doc.PlayerSpawns.Add(Spawn("start", 3)), directory =>
        {
            MapDocumentWindowing.Load(directory, new MapDocumentLoadOptions(), 1, 0, 0,
                out bool windowed, out MapTileRect? window);
            Assert.True(windowed);
            Assert.Equal(new MapTileCoord(0, 0), window!.Value.Min);
        });
    }

    [Fact]
    public void Search_never_reads_the_tile_after_its_budget()
    {
        WithWorld(doc => doc.PlayerSpawns.Add(Spawn("start", 3)), directory =>
        {
            string outsideBudget = Directory.GetFiles(directory, "t_3_0.*.json", SearchOption.AllDirectories).Single();
            File.Delete(outsideBudget);

            MapDocumentWindowing.Load(directory, new MapDocumentLoadOptions(), 1, 0, 1,
                out _, out MapTileRect? window);
            Assert.Equal(new MapTileCoord(0, 0), window!.Value.Min);

            // A second allowed search read reaches the deliberately missing file.
            Assert.Throws<MapDocumentException>(() => MapDocumentWindowing.Load(directory,
                new MapDocumentLoadOptions(), 1, 0, 2, out _, out _));
        });
    }

    [Fact]
    public void Default_budget_does_not_turn_into_a_whole_world_spawn_scan()
    {
        WithWorld(doc =>
        {
            for (int tile = 1; tile < MapDocumentWindowing.DefaultPlayerSpawnSearchTileLimit; tile++)
                doc.Placements.Add(new MapPlacement
                    { Id = "tile-" + tile, Kind = "rock", X = tile * MapDocumentFile.DefaultTileSize + 5, Z = 5 });
            doc.PlayerSpawns.Add(Spawn("beyond-budget", MapDocumentWindowing.DefaultPlayerSpawnSearchTileLimit));
        }, directory =>
        {
            MapDocument loaded = Load(directory, out MapTileRect? window);
            Assert.Equal(new MapTileCoord(0, 0), window!.Value.Min);
            Assert.Empty(loaded.PlayerSpawns);
        });
    }

    [Fact]
    public void Negative_search_budget_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MapDocumentWindowing.Load("unused",
            new MapDocumentLoadOptions(), 1, 0, -1, out _, out _));
    }

    [Fact]
    public void A_small_document_still_loads_whole()
    {
        WithWorld(doc => doc.PlayerSpawns.Add(Spawn("start", 3)), directory =>
        {
            MapDocument loaded = MapDocumentWindowing.Load(directory, new MapDocumentLoadOptions(), 2, 0,
                out bool windowed, out MapTileRect? window);
            Assert.False(windowed);
            Assert.Null(window);
            Assert.False(loaded.Tiles!.IsPartial);
            Assert.Single(loaded.Placements);
            Assert.Single(loaded.PlayerSpawns);
        });
    }

    [Fact]
    public void Editor_starts_its_camera_at_the_loaded_player_spawn()
    {
        WithWorld(doc => doc.PlayerSpawns.Add(Spawn("start", 3)), directory =>
        {
            var scene = new HeadlessScene();
            scene.Init(null!, null!, null!, new MapEditorOptions
                { DocumentPath = directory, WholeWorldTileLimit = 1, EditorWindowRadius = 0 });
            try
            {
                scene.OnEnter();
                Assert.Equal(new MapTileCoord(3, 0), scene.Window!.Value.Min);
                Assert.Equal(Spawn("start", 3).X, scene.Camera.Position.X);
                Assert.Equal(5 - 32, scene.Camera.Position.Z);
                Assert.True(scene.Camera.WorldToScreen(new Vector3(Spawn("start", 3).X, 0, 5),
                    800, 600, out Vector2 screen));
                Assert.InRange(Vector2.Distance(screen, new Vector2(400, 300)), 0, .1f);
            }
            finally { scene.OnExit(); }
        });
    }

    [Fact]
    public void Editor_honors_the_configured_search_budget()
    {
        WithWorld(doc => doc.PlayerSpawns.Add(Spawn("start", 3)), directory =>
        {
            var scene = new HeadlessScene();
            scene.Init(null!, null!, null!, new MapEditorOptions
            {
                DocumentPath = directory, WholeWorldTileLimit = 1,
                EditorWindowRadius = 0, PlayerSpawnSearchTileLimit = 0,
            });
            try
            {
                scene.OnEnter();
                Assert.Equal(new MapTileCoord(0, 0), scene.Window!.Value.Min);
                Assert.Equal(0, scene.Camera.Position.X);
                Assert.True(scene.Camera.WorldToScreen(Vector3.Zero, 800, 600, out Vector2 screen));
                Assert.InRange(Vector2.Distance(screen, new Vector2(400, 300)), 0, .1f);
            }
            finally { scene.OnExit(); }
        });
    }

    sealed class HeadlessScene : MapEditorScene
    {
        protected override void BuildWorld() { }
        protected override void TeardownWorld() { }
    }

    static MapDocument Load(string directory, out MapTileRect? window)
    {
        MapDocument result = MapDocumentWindowing.Load(directory, new MapDocumentLoadOptions(),
            wholeWorldTileLimit: 1, windowRadius: 0, out bool windowed, out window);
        Assert.True(windowed);
        return result;
    }

    static MapPlayerSpawn Spawn(string id, int tileX, bool enabled = true) => new()
    {
        Id = id, X = tileX * MapDocumentFile.DefaultTileSize + 5, Z = 5, Enabled = enabled,
    };

    static void WithWorld(Action<MapDocument> configure, Action<string> assertion)
    {
        string directory = Path.Combine(Path.GetTempPath(), "ke-spawn-window-" + Guid.NewGuid().ToString("N"));
        try
        {
            var doc = new MapDocument
            {
                Id = "spawn-window",
                Bounds = new MapBounds { MinX = -4096, MinZ = -4096, MaxX = 4096, MaxZ = 4096 },
            };
            doc.Placements.Add(new MapPlacement { Id = "center", Kind = "rock", X = 5, Z = 5 });
            configure(doc);
            MapDocumentFile.SaveTiled(doc, directory);
            assertion(directory);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
