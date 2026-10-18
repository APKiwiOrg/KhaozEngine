using System;
using System.IO;
using System.Linq;
using System.Numerics;
using KhaozEngine.TileEdit;
using KhaozEngine.TileEdit.Tools;
using KhaozEngine.TileWorld;
using ModelContextProtocol;
using Xunit;

namespace KhaozEngine.Tests.TileEdit;

/// <summary>The clearance verbs and the save refusal they back. Called on the tool classes directly, so the guard's
/// error mapping is in the path. The world sits in <c>world</c> and its glb meshes in the sibling <c>models</c>, the
/// shape a game ships, so the mesh root is the relative <c>../models</c>.</summary>
public class ClearanceToolsTests
{
    const string RuleName = "trees-clear-buildings";
    const string Waiver = "clearance-waived";

    const string Catalog = """
        {
          "materials": [ { "id": 1, "name": "grass", "color": "#4d8a3a" } ],
          "archetypes": [
            { "id": "tree", "name": "Tree", "meshRef": "kit/tree.glb", "collisionKind": "None", "tags": ["tree"] },
            { "id": "wall", "name": "Wall", "meshRef": "kit/wall.glb", "collisionKind": "None", "tags": ["structure"] }
          ]
        }
        """;

    sealed class Fixture : IDisposable
    {
        public TempDir Temp { get; } = new();
        public string World => Temp.Sub("world");
        public string Manifest => TileWorldFile.ManifestPath(World);
        public TileEditSession Session { get; } = new();
        public MutationService Mutate { get; }
        public ClearanceTools Clearance { get; }
        public WorldTools Worlds { get; }
        public ObjectTools Objects { get; }

        /// <summary>A one-plane grass world over the tree and wall catalog, with both meshes written under
        /// <c>models</c>. Each mesh is centred on its footprint with its base at y 0, and the tree box sits inside
        /// the wall slab when both stand on one tile.</summary>
        public Fixture()
        {
            Directory.CreateDirectory(World);
            File.WriteAllText(Path.Combine(World, "props.json"), Catalog);
            Session.Create(World, "clearance", "Clearance", 1, 1f, new[] { "props.json" });
            var query = new QueryService(Session);
            Mutate = new MutationService(Session);
            Clearance = new ClearanceTools(query, Mutate);
            Worlds = new WorldTools(Session, query, Mutate);
            Objects = new ObjectTools(query, Mutate);
            WriteMeshes("models");
            Mutate.TilesFill(new TileRect(0, 0, TileRegion.Size, TileRegion.Size), 0, underlay: 1);
        }

        public void WriteMeshes(string directory)
        {
            ClearanceGlbFixtures.WriteBox(Temp.Sub(Path.Combine(directory, "kit", "tree.glb")),
                new Vector3(-0.3f, 0f, -0.3f), new Vector3(0.3f, 1f, 0.3f));
            ClearanceGlbFixtures.WriteBox(Temp.Sub(Path.Combine(directory, "kit", "wall.glb")),
                new Vector3(-0.5f, 0f, -0.15f), new Vector3(0.5f, 2f, 0.15f));
        }

        public void DeclareRule(string meshRoot = "../models")
        {
            Clearance.ClearanceSet(meshRoot, waiverTag: Waiver);
            Clearance.ClearanceRuleSet(RuleName, new[] { "tree" }, new[] { "structure" });
        }

        public (long Tree, long Wall) PlaceOverlap(int x = 5, int z = 5) =>
            (Mutate.ObjectPlace("tree", x, z, 0).ObjectId, Mutate.ObjectPlace("wall", x, z, 0).ObjectId);

        public void Dispose() => Temp.Dispose();
    }

    [Fact]
    public void ClearanceVerbsEditAndUndo()
    {
        using var f = new Fixture();
        Assert.Null(f.Clearance.ClearanceGet());

        MutationResult set = f.Clearance.ClearanceSet("../models", 0.2f, Waiver);
        Assert.Equal("Set clearance", set.Label);
        AssertBlock(f.Clearance.ClearanceGet(), "../models", 0.2f, Waiver);

        f.Clearance.ClearanceRuleSet(RuleName, new[] { "tree" }, new[] { "structure" }, 0.05f);
        AssertBlock(f.Clearance.ClearanceGet(), "../models", 0.2f, Waiver, (RuleName, "tree", "structure", 0.05f));

        f.Clearance.ClearanceRuleSet(RuleName, new[] { "tree" }, new[] { "structure", "rock" });
        AssertBlock(f.Clearance.ClearanceGet(), "../models", 0.2f, Waiver, (RuleName, "tree", "structure,rock", 0f));

        f.Clearance.ClearanceSet("../kit");
        AssertBlock(f.Clearance.ClearanceGet(), "../kit", 0.1f, null, (RuleName, "tree", "structure,rock", 0f));

        f.Clearance.ClearanceRuleRemove(RuleName);
        AssertBlock(f.Clearance.ClearanceGet(), "../kit", 0.1f, null);

        f.Worlds.Undo();
        AssertBlock(f.Clearance.ClearanceGet(), "../kit", 0.1f, null, (RuleName, "tree", "structure,rock", 0f));
        f.Worlds.Undo();
        AssertBlock(f.Clearance.ClearanceGet(), "../models", 0.2f, Waiver, (RuleName, "tree", "structure,rock", 0f));
        f.Worlds.Undo();
        AssertBlock(f.Clearance.ClearanceGet(), "../models", 0.2f, Waiver, (RuleName, "tree", "structure", 0.05f));
        f.Worlds.Undo();
        AssertBlock(f.Clearance.ClearanceGet(), "../models", 0.2f, Waiver);
        UndoResult last = f.Worlds.Undo();
        Assert.Equal(1, last.Steps);
        Assert.Null(f.Clearance.ClearanceGet());
    }

    [Fact]
    public void BadClearanceEditsRefuseWithoutAnUndoStep()
    {
        using var f = new Fixture();
        f.DeclareRule();
        int depth = f.Worlds.Summary().UndoDepth;

        McpException unknown = Assert.Throws<McpException>(() => f.Clearance.ClearanceRuleRemove("no-such-rule"));
        Assert.Contains("no-such-rule", unknown.Message);
        McpException rooted = Assert.Throws<McpException>(
            () => f.Clearance.ClearanceSet(Path.GetFullPath(f.Temp.Sub("models"))));
        Assert.Contains("rooted", rooted.Message);
        Assert.Throws<McpException>(() => f.Clearance.ClearanceRuleSet("empty", Array.Empty<string>(), new[] { "x" }));

        Assert.Equal(depth, f.Worlds.Summary().UndoDepth);
        AssertBlock(f.Clearance.ClearanceGet(), "../models", 0.1f, Waiver, (RuleName, "tree", "structure", 0f));
    }

    [Fact]
    public void RuleSetWithoutBlockRefuses()
    {
        using var f = new Fixture();
        int depth = f.Worlds.Summary().UndoDepth;

        McpException ex = Assert.Throws<McpException>(
            () => f.Clearance.ClearanceRuleSet(RuleName, new[] { "tree" }, new[] { "structure" }));

        Assert.Contains("clearance_set", ex.Message);
        Assert.Throws<McpException>(() => f.Clearance.ClearanceRuleRemove(RuleName));
        Assert.Null(f.Clearance.ClearanceGet());
        Assert.Equal(depth, f.Worlds.Summary().UndoDepth);
    }

    [Fact]
    public void SaveRefusesAnOverlapAndWritesNothing()
    {
        using var f = new Fixture();
        f.DeclareRule();
        (long tree, long wall) = f.PlaceOverlap();
        (long tree2, long wall2) = f.PlaceOverlap(9, 9);
        byte[] before = File.ReadAllBytes(f.Manifest);

        McpException ex = Assert.Throws<McpException>(() => f.Worlds.WorldSave());

        Assert.StartsWith("world 'clearance' has 2 clearance issues:", ex.Message);
        string[] lines = ex.Message.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.All(lines.Skip(1), l => Assert.StartsWith($"[{TileWorldClearance.OverlapCode}] ", l));
        Assert.Contains($"tree #{tree} ", lines[1]);
        Assert.Contains($"wall #{wall} ", lines[1]);
        Assert.Contains($"tree #{tree2} ", lines[2]);
        Assert.Contains($"wall #{wall2} ", lines[2]);
        Assert.Equal(before, File.ReadAllBytes(f.Manifest));
        Assert.True(f.Worlds.Summary().Dirty);
    }

    [Fact]
    public void SaveSucceedsOnceWaived()
    {
        using var f = new Fixture();
        f.DeclareRule();
        (long tree, _) = f.PlaceOverlap();

        f.Objects.ObjectSetTags(tree, new[] { Waiver });
        SaveResult saved = f.Worlds.WorldSave();

        Assert.Equal(f.World, saved.Path);
        Assert.False(f.Worlds.Summary().Dirty);
        var reopened = new TileEditSession();
        reopened.Open(f.World);
        AssertBlock(new ClearanceTools(new QueryService(reopened), new MutationService(reopened)).ClearanceGet(),
            "../models", 0.1f, Waiver, (RuleName, "tree", "structure", 0f));
    }

    [Fact]
    public void MissingMeshRootRefusesSave()
    {
        using var f = new Fixture();
        f.DeclareRule("../absent");
        f.Mutate.ObjectPlace("tree", 5, 5, 0);
        byte[] before = File.ReadAllBytes(f.Manifest);

        McpException ex = Assert.Throws<McpException>(() => f.Worlds.WorldSave());

        Assert.StartsWith("world 'clearance' has 1 clearance issue:", ex.Message);
        Assert.Contains($"[{TileWorldClearance.MeshCode}] ", ex.Message);
        Assert.Contains(Path.GetFullPath(f.Temp.Sub("absent")), ex.Message);
        Assert.Equal(before, File.ReadAllBytes(f.Manifest));
    }

    [Fact]
    public void ValidateListsClearanceIssues()
    {
        using var f = new Fixture();
        f.DeclareRule();
        (long tree, long wall) = f.PlaceOverlap();

        ValidateResult result = f.Worlds.WorldValidate();

        Assert.False(result.Valid);
        string issue = Assert.Single(result.Issues);
        Assert.StartsWith($"[{TileWorldClearance.OverlapCode}] ", issue);
        Assert.Contains($"tree #{tree} ", issue);
        Assert.Contains($"wall #{wall} ", issue);
    }

    [Fact]
    public void CheckReportsWithoutSaving()
    {
        using var f = new Fixture();
        Assert.True(f.Clearance.ClearanceCheck().Clear);
        f.DeclareRule();
        (long tree, long wall) = f.PlaceOverlap();
        byte[] before = File.ReadAllBytes(f.Manifest);

        ClearanceCheckResult result = f.Clearance.ClearanceCheck();

        Assert.False(result.Clear);
        string issue = Assert.Single(result.Issues);
        Assert.StartsWith($"[{TileWorldClearance.OverlapCode}] ", issue);
        Assert.Contains($"tree #{tree} ", issue);
        Assert.Contains($"wall #{wall} ", issue);
        Assert.True(f.Worlds.Summary().Dirty);
        Assert.Equal(before, File.ReadAllBytes(f.Manifest));

        f.Objects.ObjectSetTags(wall, new[] { Waiver });
        ClearanceCheckResult waived = f.Clearance.ClearanceCheck();
        Assert.True(waived.Clear);
        Assert.Empty(waived.Issues);
    }

    [Fact]
    public void SavesReuseOneMeshSourceUntilTheRootChanges()
    {
        using var f = new Fixture();
        f.DeclareRule();
        f.Mutate.ObjectPlace("tree", 5, 5, 0);
        Assert.Null(f.Session.ClearanceMeshes);

        f.Worlds.WorldSave();
        object? first = f.Session.ClearanceMeshes;
        Assert.NotNull(first);
        f.Mutate.ObjectPlace("wall", 9, 9, 0);
        f.Worlds.WorldSave();
        Assert.Same(first, f.Session.ClearanceMeshes);

        f.WriteMeshes("models2");
        f.Clearance.ClearanceSet("../models2", waiverTag: Waiver);
        f.Worlds.WorldSave();
        object? second = f.Session.ClearanceMeshes;
        Assert.NotNull(second);
        Assert.NotSame(first, second);

        // Undo moves the root back, and the next check replaces the source again without a world_open.
        f.Worlds.Undo();
        f.Clearance.ClearanceCheck();
        object? third = f.Session.ClearanceMeshes;
        Assert.NotSame(second, third);

        f.Worlds.WorldOpen(f.World);
        Assert.Null(f.Session.ClearanceMeshes);
        f.Clearance.ClearanceCheck();
        Assert.NotSame(third, f.Session.ClearanceMeshes);
    }

    [Fact]
    public void SaveSeesAGlbExportedAfterACheck()
    {
        using var f = new Fixture();
        f.DeclareRule();
        string tree = f.Temp.Sub(Path.Combine("models", "kit", "tree.glb"));
        File.Delete(tree);
        f.Mutate.ObjectPlace("tree", 5, 5, 0);

        string issue = Assert.Single(f.Clearance.ClearanceCheck().Issues);
        Assert.StartsWith($"[{TileWorldClearance.MeshCode}] ", issue);
        object? source = f.Session.ClearanceMeshes;
        f.WriteMeshes("models");

        f.Worlds.WorldSave();
        Assert.False(f.Worlds.Summary().Dirty);
        Assert.Same(source, f.Session.ClearanceMeshes);
    }

    [Fact]
    public void SaveSeesAMeshReExportedClearOfItsOverlap()
    {
        using var f = new Fixture();
        f.DeclareRule();
        f.PlaceOverlap();
        string wall = f.Temp.Sub(Path.Combine("models", "kit", "wall.glb"));
        File.SetLastWriteTimeUtc(wall, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Throws<McpException>(() => f.Worlds.WorldSave());

        // The slab moved to the north edge of its tile, clear of the tree box at the centre.
        ClearanceGlbFixtures.WriteBox(wall, new Vector3(-0.5f, 0f, -0.5f), new Vector3(0.5f, 2f, -0.4f));
        File.SetLastWriteTimeUtc(wall, new DateTime(2026, 1, 1, 0, 5, 0, DateTimeKind.Utc));

        f.Worlds.WorldSave();
        Assert.False(f.Worlds.Summary().Dirty);
    }

    static void AssertBlock(ClearanceInfo? info, string meshRoot, float cellSize, string? waiverTag,
        params (string Name, string Subjects, string Obstacles, float Tolerance)[] rules)
    {
        Assert.NotNull(info);
        Assert.Equal(meshRoot, info.MeshRoot);
        Assert.Equal(cellSize, info.CellSize);
        Assert.Equal(waiverTag, info.WaiverTag);
        Assert.Equal(rules, info.Rules.Select(r =>
            (r.Name, string.Join(',', r.SubjectTags), string.Join(',', r.ObstacleTags), r.Tolerance)));
    }
}
