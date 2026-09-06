using System;
using System.IO;
using System.Linq;
using KeDungeon;
using KhaozEngine.Dungeon;
using KhaozEngine.MapDoc;
using Xunit;

namespace KeDungeon.Tests;

public class VerbTests
{
    static string NewTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ke-dungeon-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    static DungeonConfig MultiFloorNavConfig() => new()
    {
        MaxFloors = 3,
        RoomCountTarget = 16,
        LockCount = 0,
        BossRoom = false,
        LoopEdgeBudget = 0,
    };

    // First seed in 11..60 whose growth carves at least one stair edge, so a connectivity test exercises
    // NavSpace.Links (the stair joins between floors), not just per-layer grid adjacency. Mirrors the same
    // config/seed-search idiom KhaozEngine.Game.Tests/Navigation/DungeonNavTests.cs uses for the same
    // reason, and (like that fixture) is completable by construction, so it always bakes to exactly one
    // connected component.
    static DungeonLayout StairLayout(DungeonConfig? config = null)
    {
        config ??= MultiFloorNavConfig();
        for (ulong seed = 11; seed <= 60; seed++)
        {
            DungeonLayout layout = DungeonGenerator.Generate(config, seed);
            if (layout.Edges.Any(e => e.Kind == DungeonEdgeKind.Stair))
            {
                return layout;
            }
        }

        throw new InvalidOperationException("No stair edge was produced across seeds 11..60.");
    }

    [Fact]
    public void Generate_Then_Verify_Exits0()
    {
        string dir = NewTempDir();
        try
        {
            string layoutPath = Path.Combine(dir, "layout.json");

            int generateExit = Program.Main(new[] { "generate", "--seed", "42", "--out", layoutPath });
            Assert.Equal(0, generateExit);
            Assert.True(File.Exists(layoutPath));

            int verifyExit = Program.Main(new[] { "verify", "--layout", layoutPath });
            Assert.Equal(0, verifyExit);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Generate_Then_Preview_WritesFloor0Png()
    {
        string dir = NewTempDir();
        try
        {
            string layoutPath = Path.Combine(dir, "layout.json");
            int generateExit = Program.Main(new[] { "generate", "--seed", "7", "--out", layoutPath });
            Assert.Equal(0, generateExit);

            string outDir = Path.Combine(dir, "preview");
            int previewExit = Program.Main(new[] { "preview", "--layout", layoutPath, "--out-dir", outDir });
            Assert.Equal(0, previewExit);

            string floor0 = Path.Combine(outDir, "floor-0.png");
            Assert.True(File.Exists(floor0));

            byte[] bytes = File.ReadAllBytes(floor0);
            Assert.True(bytes.Length > 8);
            Assert.Equal(0x89, bytes[0]);
            Assert.Equal(0x50, bytes[1]);
            Assert.Equal(0x4E, bytes[2]);
            Assert.Equal(0x47, bytes[3]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Bake_ProducesMapJson_ThatMapDocumentFileAccepts()
    {
        string dir = NewTempDir();
        try
        {
            string layoutPath = Path.Combine(dir, "layout.json");
            int generateExit = Program.Main(new[] { "generate", "--seed", "3", "--out", layoutPath });
            Assert.Equal(0, generateExit);

            string mapPath = Path.Combine(dir, "map.json");
            int bakeExit = Program.Main(new[]
            {
                "bake",
                "--layout", layoutPath,
                "--map", mapPath,
                "--origin-x", "0",
                "--origin-z", "0",
                "--base-y", "0",
                "--yaw", "0",
            });
            Assert.Equal(0, bakeExit);
            Assert.True(File.Exists(mapPath));

            MapDocument doc = MapDocumentFile.Load(mapPath);
            Assert.NotEmpty(doc.Placements);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Bake_TwiceIntoSameMap_Accumulates()
    {
        string dir = NewTempDir();
        try
        {
            string firstLayout = Path.Combine(dir, "layout-a.json");
            string secondLayout = Path.Combine(dir, "layout-b.json");
            Assert.Equal(0, Program.Main(new[] { "generate", "--seed", "3", "--out", firstLayout }));
            Assert.Equal(0, Program.Main(new[] { "generate", "--seed", "4", "--out", secondLayout }));

            string mapPath = Path.Combine(dir, "map.json");
            int firstBake = Program.Main(new[]
            {
                "bake", "--layout", firstLayout, "--map", mapPath,
                "--origin-x", "0", "--origin-z", "0", "--base-y", "0",
            });
            Assert.Equal(0, firstBake);

            int placementsAfterFirst = MapDocumentFile.Load(mapPath).Placements.Count;

            int secondBake = Program.Main(new[]
            {
                "bake", "--layout", secondLayout, "--map", mapPath,
                "--origin-x", "300", "--origin-z", "0", "--base-y", "0",
            });
            Assert.Equal(0, secondBake);

            MapDocument doc = MapDocumentFile.Load(mapPath);
            Assert.True(doc.Placements.Count > placementsAfterFirst,
                "second bake must append placements to the existing document");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MalformedConfig_Exits3_WithMessageNotStackTrace()
    {
        string dir = NewTempDir();
        TextWriter originalError = Console.Error;
        try
        {
            string configPath = Path.Combine(dir, "config.json");
            File.WriteAllText(configPath, "{ not valid json !");
            string layoutPath = Path.Combine(dir, "layout.json");

            using var stderr = new StringWriter();
            Console.SetError(stderr);

            int exit = Program.Main(new[]
            {
                "generate", "--seed", "1", "--config", configPath, "--out", layoutPath,
            });

            Assert.Equal(3, exit);
            string message = stderr.ToString();
            Assert.Contains("config", message, StringComparison.Ordinal);
            Assert.DoesNotContain("   at ", message, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void UnknownVerb_Exits2()
    {
        int exit = Program.Main(new[] { "frobnicate" });

        Assert.Equal(2, exit);
    }

    [Fact]
    public void MissingRequiredArg_Exits2()
    {
        string dir = NewTempDir();
        try
        {
            string layoutPath = Path.Combine(dir, "layout.json");

            int exit = Program.Main(new[] { "generate", "--out", layoutPath });

            Assert.Equal(2, exit);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Nav_ConnectedMultiFloorLayout_ReportsOneComponent_Exits0()
    {
        string dir = NewTempDir();
        TextWriter originalOut = Console.Out;
        try
        {
            DungeonLayout layout = StairLayout();
            Assert.True(layout.Floors >= 2, "fixture must span multiple floors to exercise stair links");

            string layoutPath = Path.Combine(dir, "layout.json");
            File.WriteAllText(layoutPath, DungeonJson.SaveLayout(layout));

            using var stdout = new StringWriter();
            Console.SetOut(stdout);

            int exit = Program.Main(new[]
            {
                "nav", "--layout", layoutPath, "--origin-x", "0", "--origin-z", "0", "--base-y", "0",
            });

            Assert.Equal(0, exit);
            Assert.Contains("components: 1", stdout.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalOut);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Nav_DisconnectedLayout_ReportsMultipleComponents_ExitsNonZeroOnlyWhenRequired()
    {
        string dir = NewTempDir();
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        try
        {
            // Two 2x3 walkable islands on one floor, split by three full columns of wall: even 8-connected
            // with diagonals allowed, the islands are three cells apart, nowhere near touching. rooms/
            // edges/keys/markers/stats are all omitted: DungeonJson's LayoutDto defaults every one of them
            // to a non-null empty value (see DungeonJson.cs), and nothing here references a room or lock
            // id, so the hand-authored raster alone is already a valid layout document.
            const string layoutJson = """
            {
              "cellSizeMeters": 2,
              "floorHeightMeters": 4,
              "width": 6,
              "depth": 3,
              "floors": 1,
              "grid": [
                ["RRWWRR", "RRWWRR", "RRWWRR"]
              ]
            }
            """;
            string layoutPath = Path.Combine(dir, "layout.json");
            File.WriteAllText(layoutPath, layoutJson);

            using var stdout = new StringWriter();
            Console.SetOut(stdout);

            int exit = Program.Main(new[]
            {
                "nav", "--layout", layoutPath, "--origin-x", "0", "--origin-z", "0", "--base-y", "0",
            });

            Assert.Equal(0, exit);
            Assert.Contains("components: 2", stdout.ToString(), StringComparison.Ordinal);

            using var requiredStdout = new StringWriter();
            using var requiredStderr = new StringWriter();
            Console.SetOut(requiredStdout);
            Console.SetError(requiredStderr);

            int requiredExit = Program.Main(new[]
            {
                "nav", "--layout", layoutPath, "--origin-x", "0", "--origin-z", "0", "--base-y", "0",
                "--require-connected",
            });

            Assert.Equal(1, requiredExit);
            Assert.Contains("components: 2", requiredStdout.ToString(), StringComparison.Ordinal);
            Assert.Contains("not fully connected", requiredStderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Nav_NonZeroYaw_IsRejected_WithUnsupportedMessage()
    {
        TextWriter originalError = Console.Error;
        try
        {
            using var stderr = new StringWriter();
            Console.SetError(stderr);

            // --layout points at a file that does not exist: RunNav validates --yaw before ever touching
            // the file, so a missing path still proves the rejection is about yaw, not about the file.
            int exit = Program.Main(new[]
            {
                "nav", "--layout", "does-not-exist.json", "--origin-x", "0", "--origin-z", "0",
                "--base-y", "0", "--yaw", "0.5",
            });

            Assert.Equal(2, exit);
            string message = stderr.ToString();
            Assert.Contains("not supported", message, StringComparison.Ordinal);
            Assert.Contains("140", message, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(originalError);
        }
    }

    [Fact]
    public void Nav_AgentHeight_ChangesRoofedLayoutReportAfterJsonRoundTrip()
    {
        string dir = NewTempDir();
        TextWriter originalOut = Console.Out;
        try
        {
            DungeonConfig config = MultiFloorNavConfig();
            config.CeilingMode = DungeonCeilingMode.Roofed;
            config.CeilingHeightMeters = 1f;
            DungeonLayout layout = StairLayout(config);
            string layoutPath = Path.Combine(dir, "layout.json");
            File.WriteAllText(layoutPath, DungeonJson.SaveLayout(layout));

            string[] BaseArgs(params string[] extra) => new[]
            {
                "nav", "--layout", layoutPath, "--origin-x", "0", "--origin-z", "0", "--base-y", "0",
            }.Concat(extra).ToArray();

            using var defaultStdout = new StringWriter();
            Console.SetOut(defaultStdout);
            int defaultExit = Program.Main(BaseArgs());

            using var shortStdout = new StringWriter();
            Console.SetOut(shortStdout);
            int shortExit = Program.Main(BaseArgs("--agent-height", "0.1"));

            using var tallStdout = new StringWriter();
            Console.SetOut(tallStdout);
            int tallExit = Program.Main(BaseArgs("--agent-height", "1000"));

            Assert.Equal(0, defaultExit);
            Assert.Equal(0, shortExit);
            Assert.Equal(0, tallExit);
            Assert.NotEqual(defaultStdout.ToString(), shortStdout.ToString());
            Assert.Equal(defaultStdout.ToString(), tallStdout.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Directory.Delete(dir, recursive: true);
        }
    }
}
