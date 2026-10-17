using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using KhaozEngine.Primitives;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileWorldClearanceTests
{
    const string RuleName = "trees-clear-buildings";
    const string Waiver = "clearance-waived";

    // Every mesh is authored centred on its own footprint with its base at y 0, as TileObjectPlacement expects. The
    // wall is a 0.3 m slab running east to west across its tile, so a quarter turn swings it north to south.
    static readonly IReadOnlyDictionary<string, ClearanceMesh> Meshes = new Dictionary<string, ClearanceMesh>
    {
        ["box-tree"] = Box(new(-0.3f, 0f, -0.3f), new(0.3f, 1f, 0.3f)),
        ["east-tree"] = Box(new(0.25f, 0f, -0.1f), new(0.45f, 1f, 0.1f)),
        ["cone-tree"] = Cone(baseY: 2.5f, apexY: 6f, radius: 1f),
        ["wall"] = Box(new(-0.5f, 0f, -0.15f), new(0.5f, 2f, 0.15f)),
        ["roof"] = Box(new(-3f, 0f, -3f), new(3f, 0.5f, 3f)),
        ["tower"] = Box(new(-0.4f, 0f, -0.4f), new(0.4f, 3f, 0.4f)),
        ["huge"] = Box(new(-150f, 0f, -150f), new(150f, 1f, 150f)),
    };

    static TileWorldCatalogs Catalogs() => TileWorldCatalogs.LoadJson(
        """
        {
          "archetypes": [
            { "id": "tree", "name": "tree", "meshRef": "box-tree", "tags": ["tree"] },
            { "id": "east_tree", "name": "east tree", "meshRef": "east-tree", "tags": ["tree"] },
            { "id": "pine", "name": "pine", "meshRef": "cone-tree", "tags": ["tree"] },
            { "id": "wall", "name": "wall", "meshRef": "wall", "tags": ["structure"] },
            { "id": "wall_yawed", "name": "wall yawed", "meshRef": "wall", "yawOffsetDegrees": 90, "tags": ["structure"] },
            { "id": "roof", "name": "roof", "meshRef": "roof", "sizeX": 6, "sizeZ": 6, "isRoof": true, "tags": ["structure"] },
            { "id": "tower", "name": "tower", "meshRef": "tower", "tags": ["tree", "structure"] },
            { "id": "broken", "name": "broken", "meshRef": "missing", "tags": ["structure"] },
            { "id": "huge", "name": "huge", "meshRef": "huge", "tags": ["structure"] }
          ]
        }
        """,
        "clearance-tests");

    static TileClearanceSettings Settings(float tolerance = 0f, string? waiverTag = Waiver, string obstacleTag = "structure") =>
        new("models", waiverTag: waiverTag,
            rules: new[] { new TileClearanceRule(RuleName, new[] { "tree" }, new[] { obstacleTag }, tolerance) });

    static TileWorldDocument World(TileClearanceSettings? settings, params RegionCoord[] regions)
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld(4, regions);
        doc.SetClearance(settings);
        return doc;
    }

    static IReadOnlyList<TileWorldIssue> Check(TileWorldDocument doc, TileWorldCatalogs? catalogs = null) =>
        TileWorldClearance.Check(doc, catalogs ?? Catalogs(), new FakeMeshes());

    [Fact]
    public void NoSettingsNoIssues()
    {
        TileWorldDocument doc = World(null);
        doc.AddObject("tree", 10, 10, 0, 0);
        doc.AddObject("wall", 10, 10, 0, 0);

        Assert.Empty(Check(doc));
    }

    [Fact]
    public void NoRulesNoIssues()
    {
        TileWorldDocument doc = World(new TileClearanceSettings("models"));
        doc.AddObject("tree", 10, 10, 0, 0);
        doc.AddObject("wall", 10, 10, 0, 0);

        Assert.Empty(Check(doc));
    }

    [Fact]
    public void TreeIntoWallIsReported()
    {
        TileWorldDocument doc = World(Settings());
        TileObject tree = doc.AddObject("tree", 12, 9, 0, 0);
        TileObject wall = doc.AddObject("wall", 12, 9, 0, 0);

        TileWorldIssue issue = Assert.Single(Check(doc));

        Assert.Equal(TileWorldClearance.OverlapCode, issue.Code);
        Assert.Equal("clearance.overlap", issue.Code);
        Assert.Matches(
            "^" + Regex.Escape($"rule '{RuleName}': tree #{tree.Id} at (12, 9) plane 0 is ")
                + @"\d+\.\d\d\d"
                + Regex.Escape($" m inside wall #{wall.Id} at (12, 9) plane 0") + "$",
            issue.Message);
        Assert.InRange(Depth(issue), 0.05f, 0.2f);
        Assert.Equal(new RegionCoord(0, 0), issue.Region);
        Assert.Equal(new TileCoord(12, 9, 0), issue.Tile);
    }

    [Fact]
    public void CrownOverWallTopIsClear()
    {
        TileWorldDocument doc = World(Settings());
        doc.AddObject("pine", 10, 10, 0, 0);
        doc.AddObject("wall", 10, 10, 0, 0);

        Assert.Empty(Check(doc));
    }

    // The second row puts the roof across broad-phase bucket edges at world x 16 and world z -16 while the crown sits
    // in one bucket, so the roof is only found through a bucket it was spread into.
    [Theory]
    [InlineData(10, 10, 8, 8)]
    [InlineData(17, 17, 13, 14)]
    public void RoofOnPlaneOneCatchesAPlaneZeroCrown(int pineX, int pineZ, int roofX, int roofZ)
    {
        TileWorldDocument doc = World(Settings());
        TileObject pine = doc.AddObject("pine", pineX, pineZ, 0, 0);
        TileObject roof = doc.AddObject("roof", roofX, roofZ, 1, 0);

        TileWorldIssue issue = Assert.Single(Check(doc));

        Assert.Equal(TileWorldClearance.OverlapCode, issue.Code);
        Assert.EndsWith($"inside roof #{roof.Id} at ({roofX}, {roofZ}) plane 1", issue.Message);
        Assert.Contains($"pine #{pine.Id} at ({pineX}, {pineZ}) plane 0", issue.Message);
        Assert.Equal(new TileCoord(pineX, pineZ, 0), issue.Tile);
    }

    [Fact]
    public void ToleranceIsHonoured()
    {
        float depth = Depth(Assert.Single(Check(TreeInWall(Settings()))));

        Assert.Empty(Check(TreeInWall(Settings(tolerance: depth + 0.02f))));
        Assert.Single(Check(TreeInWall(Settings(tolerance: depth - 0.02f))));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void WaiverOnEitherObjectSkipsThePair(bool waiveTree, bool waiveWall)
    {
        TileWorldDocument doc = World(Settings());
        doc.AddObject("tree", 10, 10, 0, 0, waiveTree ? new[] { "other", Waiver } : null);
        doc.AddObject("wall", 10, 10, 0, 0, waiveWall ? new[] { Waiver } : new[] { "other" });

        Assert.Empty(Check(doc));
    }

    [Fact]
    public void NoWaiverTagMeansNothingIsWaived()
    {
        TileWorldDocument doc = World(Settings(waiverTag: null));
        doc.AddObject("tree", 10, 10, 0, 0, new[] { Waiver });
        doc.AddObject("wall", 10, 10, 0, 0, new[] { Waiver });

        Assert.Single(Check(doc));
    }

    [Fact]
    public void EveryQuarterTurnGivesTheSameDepth()
    {
        float Measure(string wallArchetype, int rotation)
        {
            TileWorldDocument doc = World(Settings());
            doc.AddObject("tree", 10, 10, 0, 0);
            doc.AddObject(wallArchetype, 10, 10, 0, rotation);
            return Depth(Assert.Single(Check(doc)));
        }

        float unrotated = Measure("wall", 0);
        for (int rotation = 1; rotation < 4; rotation++)
            Assert.InRange(Measure("wall", rotation), unrotated - 0.02f, unrotated + 0.02f);
        Assert.InRange(Measure("wall_yawed", 0), unrotated - 0.02f, unrotated + 0.02f);
    }

    // The subject sits east of its tile centre, inside the east to west wall slab. A quarter turn swings it north or
    // south of the slab and a half turn back across it, so a check that ignored rotation would report every row.
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void RotationMovesAnOffCentreSubject(int rotation, bool inside)
    {
        TileWorldDocument doc = World(Settings());
        doc.AddObject("east_tree", 10, 10, 0, rotation);
        doc.AddObject("wall", 10, 10, 0, 0);

        IReadOnlyList<TileWorldIssue> issues = Check(doc);

        Assert.Equal(inside ? 1 : 0, issues.Count);
    }

    [Fact]
    public void MissingArchetypeIsSkipped()
    {
        TileWorldDocument doc = World(Settings());
        doc.AddObject("ghost", 10, 10, 0, 0);
        doc.AddObject("wall", 10, 10, 0, 0);
        doc.AddObject("tree", 20, 20, 0, 0);
        doc.AddObject("ghost", 20, 20, 0, 0);

        Assert.Empty(Check(doc));
    }

    [Fact]
    public void ObjectNeverChecksItself()
    {
        TileWorldDocument doc = World(Settings());
        doc.AddObject("tower", 10, 10, 0, 0);

        Assert.Empty(Check(doc));
    }

    [Theory]
    [InlineData("missing", "no such mesh")]
    [InlineData("nan", "not finite")]
    [InlineData("", "no meshRef")]
    [InlineData("null-mesh", "returned no mesh")]
    public void UnreadableMeshIsReported(string meshRef, string reason)
    {
        TileWorldCatalogs catalogs = Catalogs();
        catalogs.Archetype("broken")!.MeshRef = meshRef;
        TileWorldDocument doc = World(Settings());
        doc.AddObject("tree", 10, 10, 0, 0);
        doc.AddObject("broken", 10, 10, 0, 0);
        doc.AddObject("broken", 30, 30, 0, 0);

        TileWorldIssue issue = Assert.Single(Check(doc, catalogs));

        Assert.Equal(TileWorldClearance.MeshCode, issue.Code);
        Assert.Equal("clearance.mesh", issue.Code);
        Assert.Contains("'broken'", issue.Message);
        Assert.Contains($"'{meshRef}'", issue.Message);
        Assert.Contains(reason, issue.Message);
    }

    [Fact]
    public void TooLargeObstacleIsReported()
    {
        TileWorldDocument doc = World(Settings());
        doc.AddObject("tree", 10, 10, 0, 0);
        doc.AddObject("huge", 10, 10, 0, 0);

        TileWorldIssue issue = Assert.Single(Check(doc));

        Assert.Equal(TileWorldClearance.MeshCode, issue.Code);
        Assert.Contains("'huge'", issue.Message);
        Assert.Contains("9000000", issue.Message);
    }

    [Fact]
    public void RuleMatchingNothingIsReported()
    {
        TileWorldDocument doc = World(Settings(obstacleTag: "strucutre"));
        doc.AddObject("tree", 10, 10, 0, 0);
        doc.AddObject("wall", 10, 10, 0, 0);

        TileWorldIssue issue = Assert.Single(Check(doc));

        Assert.Equal(TileWorldClearance.RuleCode, issue.Code);
        Assert.Equal("clearance.rule", issue.Code);
        Assert.Contains($"'{RuleName}'", issue.Message);
        Assert.Contains("obstacle", issue.Message);
        Assert.Contains("strucutre", issue.Message);
    }

    [Fact]
    public void IssuesAreOrdered()
    {
        // Region (1, 0) is created first, so a plain walk of the loaded objects meets the higher ids first.
        TileWorldDocument doc = World(Settings(), new RegionCoord(1, 0), new RegionCoord(0, 0));
        TileObject treeA = doc.AddObject("tree", 10, 10, 0, 0);
        TileObject wallA1 = doc.AddObject("wall", 10, 10, 0, 0);
        TileObject wallA2 = doc.AddObject("wall", 10, 10, 0, 1);
        TileObject treeB = doc.AddObject("tree", 70, 10, 0, 0);
        TileObject wallB1 = doc.AddObject("wall", 70, 10, 0, 1);
        TileObject wallB2 = doc.AddObject("wall", 70, 10, 0, 0);

        string[] pairs = Check(doc)
            .Select(i => Regex.Match(i.Message, @"#(\d+) at .* inside \w+ #(\d+) at ").Groups)
            .Select(g => $"{g[1].Value}-{g[2].Value}")
            .ToArray();

        Assert.Equal(
            new[] { $"{treeA.Id}-{wallA1.Id}", $"{treeA.Id}-{wallA2.Id}", $"{treeB.Id}-{wallB1.Id}", $"{treeB.Id}-{wallB2.Id}" },
            pairs);
    }

    [Fact]
    public void MeshRootResolvesAgainstTheWorldDirectory()
    {
        string world = Path.Combine(Path.GetTempPath(), "worlds", "hollow");
        string resolved = TileWorldClearance.ResolveMeshRoot(world, new TileClearanceSettings("../../models"));

        Assert.Equal(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "models")), resolved);
    }

    static TileWorldDocument TreeInWall(TileClearanceSettings settings)
    {
        TileWorldDocument doc = World(settings);
        doc.AddObject("tree", 10, 10, 0, 0);
        doc.AddObject("wall", 10, 10, 0, 0);
        return doc;
    }

    static float Depth(TileWorldIssue issue) => float.Parse(
        Regex.Match(issue.Message, @" is (\d+\.\d+) m inside ").Groups[1].Value, CultureInfo.InvariantCulture);

    static ClearanceMesh Box(Vector3 min, Vector3 max)
    {
        var p = new List<Vector3>();
        for (int i = 0; i < 8; i++)
            p.Add(new((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z));
        int[] quads = { 0, 1, 3, 2, 4, 6, 7, 5, 0, 4, 5, 1, 2, 3, 7, 6, 0, 2, 6, 4, 1, 5, 7, 3 };
        var indices = new List<int>();
        for (int q = 0; q < quads.Length; q += 4)
            indices.AddRange(new[] { quads[q], quads[q + 1], quads[q + 2], quads[q], quads[q + 2], quads[q + 3] });
        return new ClearanceMesh(p, indices);
    }

    static ClearanceMesh Cone(float baseY, float apexY, float radius, int segments = 16)
    {
        var p = new List<Vector3> { new(0f, apexY, 0f), new(0f, baseY, 0f) };
        for (int i = 0; i < segments; i++)
        {
            float a = i * MathF.Tau / segments;
            p.Add(new(radius * MathF.Cos(a), baseY, radius * MathF.Sin(a)));
        }
        var indices = new List<int>();
        for (int i = 0; i < segments; i++)
        {
            int a = 2 + i, b = 2 + (i + 1) % segments;
            indices.AddRange(new[] { 0, a, b, 1, b, a });
        }
        return new ClearanceMesh(p, indices);
    }

    sealed class FakeMeshes : ITileMeshGeometry
    {
        public bool TryLoad(string meshRef, [NotNullWhen(true)] out ClearanceMesh? mesh, [NotNullWhen(false)] out string? error)
        {
            if (meshRef == "nan")
            {
                // A loader that hands the core a bad position, which ClearanceMesh refuses with ArgumentException.
                mesh = new ClearanceMesh(new[] { new Vector3(float.NaN, 0f, 0f), Vector3.UnitX, Vector3.UnitZ }, new[] { 0, 1, 2 });
                error = null;
                return true;
            }
            if (meshRef == "null-mesh")
            {
                // A loader that breaks its own contract by reporting success with no mesh.
                mesh = null!;
                error = null;
                return true;
            }
            if (Meshes.TryGetValue(meshRef, out mesh))
            {
                error = null;
                return true;
            }
            error = $"no such mesh '{meshRef}'";
            return false;
        }
    }
}
