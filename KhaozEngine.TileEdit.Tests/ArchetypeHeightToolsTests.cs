using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using KhaozEngine.TileEdit;
using KhaozEngine.TileEdit.Tools;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileEdit;

/// <summary>The two archetype height verbs: <c>archetype_measure_heights</c> reads each archetype's model top out
/// of its glb, and <c>archetype_set_collision_heights</c> writes heights into the catalog file that defines each
/// archetype. Called on the tool class directly, so the guard's error mapping is in the path.</summary>
public class ArchetypeHeightToolsTests
{
    sealed class Fixture : IDisposable
    {
        public TempDir Temp { get; } = new();
        public string World => Temp.Sub("world");
        public string Kit => Temp.Sub("kit");
        public TileEditSession Session { get; } = new();
        public ArchetypeHeightTools Tools { get; }

        /// <summary>A world over the given catalog files, each written inside the world directory.</summary>
        public Fixture(params (string Name, string Json)[] catalogs)
        {
            Directory.CreateDirectory(World);
            foreach ((string name, string json) in catalogs)
            {
                string path = Path.Combine(World, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, json);
            }
            Session.Create(World, "heights", "Heights", 1, 1f, catalogs.Select(c => c.Name).ToArray());
            Tools = new ArchetypeHeightTools(new ArchetypeHeightService(Session));
        }

        public string CatalogPath(string name) => Path.Combine(World, name);

        public void Dispose() => Temp.Dispose();
    }

    const string Props = """
        {
          "archetypes": [
            { "id": "wall", "name": "Wall", "meshRef": "kit/wall.glb", "collisionKind": "Wall" },
            { "id": "tree", "name": "Tree", "meshRef": "kit/tree.glb", "collisionKind": "Solid" },
            { "id": "rug", "name": "Rug", "meshRef": "kit/rug.glb", "collisionKind": "None" },
            { "id": "pit", "name": "Pit", "meshRef": "kit/pit.glb", "collisionKind": "Solid" },
            { "id": "pillar", "name": "Pillar", "meshRef": "kit/pillar.glb", "collisionKind": "Solid", "collisionHeight": 2.5 }
          ]
        }
        """;

    const string Ground = """
        {
          "materials": [ { "id": 1, "name": "grass", "color": "#4d8a3a" } ],
          "archetypes": [ { "id": "rock", "name": "Rock", "meshRef": "kit/rock.glb", "collisionKind": "Solid" } ]
        }
        """;

    [Fact]
    public void MeasureReadsTheModelTopAsTheHeight()
    {
        using var f = new Fixture(("props.json", Props));
        WriteTriangleGlb(Path.Combine(f.Kit, "kit", "wall.glb"), minY: 0f, maxY: 2.75f);
        WriteTriangleGlb(Path.Combine(f.Kit, "kit", "pillar.glb"), minY: -0.2f, maxY: 3.1f);

        MeasureHeightsResult result = f.Tools.MeasureHeights(f.Kit);

        Assert.Equal(f.Kit, result.KitRoot);
        Assert.Equal(new[] { "pillar", "pit", "rug", "tree", "wall" }, result.Archetypes.Select(a => a.Id));
        MeasuredArchetypeHeight wall = result.Archetypes.Single(a => a.Id == "wall");
        Assert.Equal(2.75f, wall.Height);
        Assert.Null(wall.Error);
        Assert.Equal("Wall", wall.CollisionKind);
        Assert.Null(wall.Recorded);
        MeasuredArchetypeHeight pillar = result.Archetypes.Single(a => a.Id == "pillar");
        Assert.Equal(3.1f, pillar.Height);
        Assert.Equal(2.5f, pillar.Recorded);

        // Read-only: measuring writes nothing and leaves the session's catalogs as they were.
        Assert.Equal(Props, File.ReadAllText(f.CatalogPath("props.json")));
        Assert.Null(f.Session.Editing!.Catalogs.Archetype("wall")!.CollisionHeight);
    }

    [Fact]
    public void MeasureReportsAMissingMeshAsAnError()
    {
        using var f = new Fixture(("props.json", Props));
        WriteTriangleGlb(Path.Combine(f.Kit, "kit", "wall.glb"), minY: 0f, maxY: 2.75f);

        MeasureHeightsResult result = f.Tools.MeasureHeights(f.Kit);

        // No greybox fallback: a missing glb is an error naming the mesh, never a guessed box.
        MeasuredArchetypeHeight tree = result.Archetypes.Single(a => a.Id == "tree");
        Assert.Null(tree.Height);
        Assert.NotNull(tree.Error);
        Assert.Contains("kit/tree.glb", tree.Error, StringComparison.Ordinal);
        Assert.Equal(2.75f, result.Archetypes.Single(a => a.Id == "wall").Height);
    }

    // The resolver reports a broken glb once per path, so two archetypes sharing one must both get the real reason.
    [Fact]
    public void MeasureGivesEveryArchetypeSharingABrokenMeshTheSameReason()
    {
        using var f = new Fixture(("props.json", """
            {
              "archetypes": [
                { "id": "fence_a", "name": "Fence", "meshRef": "kit/fence.glb", "collisionKind": "Wall" },
                { "id": "fence_b", "name": "Fence", "meshRef": "kit/fence.glb", "collisionKind": "Wall" }
              ]
            }
            """));
        Directory.CreateDirectory(Path.Combine(f.Kit, "kit"));
        File.WriteAllBytes(Path.Combine(f.Kit, "kit", "fence.glb"), Encoding.ASCII.GetBytes("not a glb at all"));

        MeasureHeightsResult result = f.Tools.MeasureHeights(f.Kit);

        string?[] errors = result.Archetypes.Select(a => a.Error).ToArray();
        Assert.Equal(2, errors.Length);
        Assert.All(errors, e => Assert.Contains("did not load", e, StringComparison.Ordinal));
        Assert.DoesNotContain("no geometry", errors[0], StringComparison.Ordinal);
        Assert.Equal(errors[0], errors[1]);
        Assert.All(result.Archetypes, a => Assert.Null(a.Height));
    }

    // Ruling B2: a model whose top is at or below its base measures to a height the loader would refuse, so it is
    // an error entry and the author sets that archetype's height by hand.
    [Fact]
    public void MeasureReportsANonPositiveTopAsAnError()
    {
        using var f = new Fixture(("props.json", Props));
        WriteTriangleGlb(Path.Combine(f.Kit, "kit", "rug.glb"), minY: 0f, maxY: 0f);
        WriteTriangleGlb(Path.Combine(f.Kit, "kit", "pit.glb"), minY: -1f, maxY: -0.25f);

        MeasureHeightsResult result = f.Tools.MeasureHeights(f.Kit);

        foreach (string id in new[] { "rug", "pit" })
        {
            MeasuredArchetypeHeight entry = result.Archetypes.Single(a => a.Id == id);
            Assert.Null(entry.Height);
            Assert.NotNull(entry.Error);
            Assert.Contains("not above 0", entry.Error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AnExistingHeightIsKeptWithoutOverwrite()
    {
        using var f = new Fixture(("props.json", Props));
        string path = f.CatalogPath("props.json");

        CollisionHeightsResult kept = f.Tools.SetCollisionHeights(
            new[] { new ArchetypeHeight("pillar", 3f), new ArchetypeHeight("wall", 2f) });

        CollisionHeightChange wall = Assert.Single(kept.Changed);
        Assert.Equal("wall", wall.Id);
        Assert.Null(wall.Previous);
        Assert.Equal(2f, wall.Height);
        CollisionHeightSkip skip = Assert.Single(kept.Skipped);
        Assert.Equal("pillar", skip.Id);
        Assert.Equal(2.5f, skip.Recorded);
        Assert.Empty(kept.Errors);
        Assert.Contains("\"collisionHeight\": 2.5 }", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal(2.5f, f.Session.Editing!.Catalogs.Archetype("pillar")!.CollisionHeight);

        string beforeOverwrite = File.ReadAllText(path);
        CollisionHeightsResult replaced = f.Tools.SetCollisionHeights(
            new[] { new ArchetypeHeight("pillar", 3f) }, overwrite: true);

        CollisionHeightChange pillar = Assert.Single(replaced.Changed);
        Assert.Equal(2.5f, pillar.Previous);
        Assert.Equal(3f, pillar.Height);
        Assert.Equal(beforeOverwrite.Replace("\"collisionHeight\": 2.5 }", "\"collisionHeight\": 3 }",
            StringComparison.Ordinal), File.ReadAllText(path));
        Assert.Equal(3f, f.Session.Editing!.Catalogs.Archetype("pillar")!.CollisionHeight);
    }

    // Ruling B2: a value the loader would reject is an error entry and never reaches the file. Ruling B12: an id the
    // session's catalogs do not define is an error entry too, never a guess at a file.
    [Fact]
    public void SetRefusesANonFiniteOrNonPositiveHeightAndAnUnknownId()
    {
        using var f = new Fixture(("props.json", Props));
        byte[] before = File.ReadAllBytes(f.CatalogPath("props.json"));

        CollisionHeightsResult result = f.Tools.SetCollisionHeights(new[]
        {
            new ArchetypeHeight("wall", float.NaN),
            new ArchetypeHeight("tree", 0f),
            new ArchetypeHeight("rug", -1f),
            new ArchetypeHeight("pit", float.PositiveInfinity),
            new ArchetypeHeight("ghost", 1f),
        });

        Assert.Empty(result.Changed);
        Assert.Empty(result.Skipped);
        Assert.Equal(new[] { "wall", "tree", "rug", "pit", "ghost" }, result.Errors.Select(e => e.Id));
        Assert.Contains("not defined", result.Errors.Single(e => e.Id == "ghost").Error, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(f.CatalogPath("props.json")));
        Assert.Null(f.Session.Editing!.Catalogs.Archetype("wall")!.CollisionHeight);
    }

    [Fact]
    public void AMixedBatchWritesTheGoodEntryAndReportsTheBadOne()
    {
        using var f = new Fixture(("props.json", Props));

        CollisionHeightsResult result = f.Tools.SetCollisionHeights(
            new[] { new ArchetypeHeight("wall", 2f), new ArchetypeHeight("tree", -1f) });

        CollisionHeightChange wall = Assert.Single(result.Changed);
        Assert.Equal("wall", wall.Id);
        Assert.Equal("tree", Assert.Single(result.Errors).Id);
        Assert.Empty(result.Skipped);
        string text = File.ReadAllText(f.CatalogPath("props.json"));
        Assert.Contains("\"collisionKind\": \"Wall\", \"collisionHeight\": 2 }", text, StringComparison.Ordinal);
        Assert.Contains("\"collisionKind\": \"Solid\" }", text, StringComparison.Ordinal);
        Assert.Equal(2f, f.Session.Editing!.Catalogs.Archetype("wall")!.CollisionHeight);
        Assert.Null(f.Session.Editing!.Catalogs.Archetype("tree")!.CollisionHeight);
    }

    // A later duplicate is ignored whatever became of the first entry, and the message says only that, so it stays
    // true when the first entry was itself refused.
    [Fact]
    public void ALaterDuplicateEntryIsIgnoredEvenWhenTheFirstWasRefused()
    {
        using var f = new Fixture(("props.json", Props));

        CollisionHeightsResult result = f.Tools.SetCollisionHeights(
            new[] { new ArchetypeHeight("wall", float.NaN), new ArchetypeHeight("wall", 2f) });

        Assert.Empty(result.Changed);
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains("not a finite number", result.Errors[0].Error, StringComparison.Ordinal);
        Assert.Equal("listed more than once in this call, so this later entry is ignored.", result.Errors[1].Error);
        Assert.Null(f.Session.Editing!.Catalogs.Archetype("wall")!.CollisionHeight);
    }

    // When the write lands but the session refresh fails (another catalog file broke after world_open), the change
    // is still reported and SessionRefreshError says the session needs world_open, with the loader's reason.
    [Fact]
    public void AFailedSessionRefreshStillReportsTheWrittenHeights()
    {
        using var f = new Fixture(("ground.json", Ground), ("props.json", Props));
        File.WriteAllText(f.CatalogPath("ground.json"), "{ oops");

        CollisionHeightsResult result = f.Tools.SetCollisionHeights(new[] { new ArchetypeHeight("tree", 4.5f) });

        CollisionHeightChange tree = Assert.Single(result.Changed);
        Assert.Equal(f.CatalogPath("props.json"), tree.File);
        Assert.Contains("\"collisionHeight\": 4.5 }", File.ReadAllText(f.CatalogPath("props.json")), StringComparison.Ordinal);
        Assert.Empty(result.Errors);
        Assert.NotNull(result.SessionRefreshError);
        Assert.Contains("world_open", result.SessionRefreshError, StringComparison.Ordinal);
        Assert.Contains("ground.json", result.SessionRefreshError, StringComparison.Ordinal);
        // The refresh did not run, which is exactly what the error tells the client.
        Assert.Null(f.Session.Editing!.Catalogs.Archetype("tree")!.CollisionHeight);
    }

    // A call that writes nothing does not refresh the session, so a catalog that broke after world_open cannot
    // produce a refresh error claiming files were written.
    [Fact]
    public void ACallThatWritesNothingNeverReportsARefreshError()
    {
        using var f = new Fixture(("ground.json", Ground), ("props.json", Props));
        File.WriteAllText(f.CatalogPath("ground.json"), "{ oops");
        byte[] props = File.ReadAllBytes(f.CatalogPath("props.json"));

        CollisionHeightsResult result = f.Tools.SetCollisionHeights(new[] { new ArchetypeHeight("tree", -1f) });

        Assert.Null(result.SessionRefreshError);
        Assert.Empty(result.Changed);
        Assert.Equal("tree", Assert.Single(result.Errors).Id);
        Assert.Equal(props, File.ReadAllBytes(f.CatalogPath("props.json")));
        Assert.Equal("{ oops", File.ReadAllText(f.CatalogPath("ground.json")));
    }

    // Ruling B12: each height lands in the one catalog file that defines its archetype, a file with nothing to
    // change keeps every byte, and the open session sees the new heights without a world_open.
    [Fact]
    public void AHeightLandsInTheFileThatDefinesTheArchetype()
    {
        using var f = new Fixture(("ground.json", Ground), ("props.json", Props));
        byte[] ground = File.ReadAllBytes(f.CatalogPath("ground.json"));
        int undoDepth = f.Session.Summary().UndoDepth;

        CollisionHeightsResult result = f.Tools.SetCollisionHeights(
            new[] { new ArchetypeHeight("tree", 4.5f), new ArchetypeHeight("wall", 2.25f) });

        Assert.Empty(result.Errors);
        Assert.Null(result.SessionRefreshError);
        Assert.Equal(new[] { "tree", "wall" }, result.Changed.Select(c => c.Id));
        Assert.All(result.Changed, c => Assert.Equal(f.CatalogPath("props.json"), c.File));
        Assert.Equal(ground, File.ReadAllBytes(f.CatalogPath("ground.json")));
        Assert.Contains("\"collisionKind\": \"Solid\", \"collisionHeight\": 4.5 }",
            File.ReadAllText(f.CatalogPath("props.json")), StringComparison.Ordinal);

        TileWorldCatalogs live = f.Session.Editing!.Catalogs;
        Assert.Equal(4.5f, live.Archetype("tree")!.CollisionHeight);
        Assert.Equal(2.25f, live.Archetype("wall")!.CollisionHeight);
        Assert.Null(live.Archetype("rock")!.CollisionHeight);
        Assert.Equal(undoDepth, f.Session.Summary().UndoDepth);

        // The other file is the one written when the archetype lives there.
        CollisionHeightsResult rock = f.Tools.SetCollisionHeights(new[] { new ArchetypeHeight("rock", 1.5f) });
        Assert.Equal(f.CatalogPath("ground.json"), Assert.Single(rock.Changed).File);
        Assert.Equal(1.5f, f.Session.Editing!.Catalogs.Archetype("rock")!.CollisionHeight);
        Assert.Equal(1.5f, TileWorldCatalogs.Load(new[] { f.CatalogPath("ground.json") }).Archetype("rock")!.CollisionHeight);
    }

    // A file the disk refuses fails alone: the file written before it stays reported as changed and refreshes the
    // session, and only the refused file's entries become errors. The refused file's directory is made read-only
    // after the world opens, so it still loads and prepares but its temporary file cannot be created.
    [Fact]
    public void AFailedWriteKeepsTheFilesThatLandedBeforeIt()
    {
        // Windows governs a directory through its ACL, not a POSIX mode.
        if (OperatingSystem.IsWindows()) return;
        using var f = new Fixture(("ground.json", Ground), ("locked/props.json", Props));
        string locked = Path.GetDirectoryName(f.CatalogPath("locked/props.json"))!;
        byte[] props = File.ReadAllBytes(f.CatalogPath("locked/props.json"));
        UnixFileMode mode = File.GetUnixFileMode(locked);
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            // A user that ignores directory modes (root) can write anyway, so there is no failure to observe.
            if (CanCreateFileIn(locked)) return;

            CollisionHeightsResult result = f.Tools.SetCollisionHeights(
                new[] { new ArchetypeHeight("rock", 1.5f), new ArchetypeHeight("tree", 4.5f) });

            CollisionHeightChange rock = Assert.Single(result.Changed);
            Assert.Equal("rock", rock.Id);
            Assert.Equal(f.CatalogPath("ground.json"), rock.File);
            ArchetypeHeightError tree = Assert.Single(result.Errors);
            Assert.Equal("tree", tree.Id);
            Assert.Contains("could not be written", tree.Error, StringComparison.Ordinal);
            Assert.Null(result.SessionRefreshError);

            Assert.Equal(1.5f, TileWorldCatalogs.Load(new[] { f.CatalogPath("ground.json") }).Archetype("rock")!.CollisionHeight);
            Assert.Equal(props, File.ReadAllBytes(f.CatalogPath("locked/props.json")));
            Assert.Equal(new[] { "props.json" }, Directory.GetFiles(locked).Select(Path.GetFileName));
            Assert.Equal(1.5f, f.Session.Editing!.Catalogs.Archetype("rock")!.CollisionHeight);
            Assert.Null(f.Session.Editing!.Catalogs.Archetype("tree")!.CollisionHeight);
        }
        finally
        {
            File.SetUnixFileMode(locked, mode);
        }
    }

    static bool CanCreateFileIn(string directory)
    {
        string probe = Path.Combine(directory, "probe");
        try
        {
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Writes a one-triangle binary glTF whose vertices span y from <paramref name="minY"/> to
    /// <paramref name="maxY"/>, positions only, which is all the loader needs to measure it.</summary>
    static void WriteTriangleGlb(string path, float minY, float maxY)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        float[] positions = { 0f, minY, 0f, 1f, minY, 0f, 0f, maxY, 1f };
        byte[] bin = new byte[positions.Length * sizeof(float)];
        for (int i = 0; i < positions.Length; i++)
            BitConverter.TryWriteBytes(bin.AsSpan(i * sizeof(float)), positions[i]);

        string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);
        string json = "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":0}]," +
            "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}}]}]," +
            $"\"buffers\":[{{\"byteLength\":{bin.Length}}}],\"bufferViews\":[{{\"buffer\":0,\"byteLength\":{bin.Length}}}]," +
            $"\"accessors\":[{{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\"," +
            $"\"min\":[0,{F(minY)},0],\"max\":[1,{F(maxY)},1]}}]}}";
        byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
        int jsonLength = (jsonBytes.Length + 3) & ~3;

        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);
        w.Write(0x46546C67u);
        w.Write(2u);
        w.Write((uint)(12 + 8 + jsonLength + 8 + bin.Length));
        w.Write((uint)jsonLength);
        w.Write(0x4E4F534Au);
        w.Write(jsonBytes);
        for (int i = jsonBytes.Length; i < jsonLength; i++) w.Write((byte)' ');
        w.Write((uint)bin.Length);
        w.Write(0x004E4942u);
        w.Write(bin);
    }
}
