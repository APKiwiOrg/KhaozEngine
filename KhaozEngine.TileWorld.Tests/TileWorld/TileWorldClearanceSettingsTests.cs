using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileWorldClearanceSettingsTests
{
    static TileClearanceRule TreesClearBuildings(float tolerance = 0.05f) =>
        new("trees-clear-buildings", ["tree"], ["structure"], tolerance);

    static TileClearanceSettings Settings() =>
        new("../../models", 0.1f, "clearance-waived", [TreesClearBuildings()]);

    [Theory]
    [InlineData("empty-mesh-root-with-rules")]
    [InlineData("blank-mesh-root-with-rules")]
    [InlineData("cell-size-below-range")]
    [InlineData("cell-size-above-range")]
    [InlineData("cell-size-nan")]
    [InlineData("duplicate-rule-name")]
    [InlineData("empty-rule-name")]
    [InlineData("empty-subject-tags")]
    [InlineData("empty-obstacle-tags")]
    [InlineData("empty-subject-tag-string")]
    [InlineData("empty-obstacle-tag-string")]
    [InlineData("negative-tolerance")]
    [InlineData("nan-tolerance")]
    [InlineData("infinite-tolerance")]
    [InlineData("empty-waiver-tag")]
    public void SettingsRefuseInvalidValues(string refusal)
    {
        Func<TileClearanceSettings> build = refusal switch
        {
            "empty-mesh-root-with-rules" => () => new TileClearanceSettings("", rules: [TreesClearBuildings()]),
            "blank-mesh-root-with-rules" => () => new TileClearanceSettings("  ", rules: [TreesClearBuildings()]),
            "cell-size-below-range" => () => new TileClearanceSettings("m", TileClearanceSettings.MinCellSize / 2f),
            "cell-size-above-range" => () => new TileClearanceSettings("m", TileClearanceSettings.MaxCellSize * 2f),
            "cell-size-nan" => () => new TileClearanceSettings("m", float.NaN),
            "duplicate-rule-name" => () => new TileClearanceSettings("m", rules: [TreesClearBuildings(), TreesClearBuildings(0.2f)]),
            "empty-rule-name" => () => new TileClearanceSettings("m", rules: [new TileClearanceRule("", ["tree"], ["structure"])]),
            "empty-subject-tags" => () => new TileClearanceSettings("m", rules: [new TileClearanceRule("r", [], ["structure"])]),
            "empty-obstacle-tags" => () => new TileClearanceSettings("m", rules: [new TileClearanceRule("r", ["tree"], [])]),
            "empty-subject-tag-string" => () => new TileClearanceSettings("m", rules: [new TileClearanceRule("r", ["tree", ""], ["structure"])]),
            "empty-obstacle-tag-string" => () => new TileClearanceSettings("m", rules: [new TileClearanceRule("r", ["tree"], [""])]),
            "negative-tolerance" => () => new TileClearanceSettings("m", rules: [TreesClearBuildings(-0.01f)]),
            "nan-tolerance" => () => new TileClearanceSettings("m", rules: [TreesClearBuildings(float.NaN)]),
            "infinite-tolerance" => () => new TileClearanceSettings("m", rules: [TreesClearBuildings(float.PositiveInfinity)]),
            "empty-waiver-tag" => () => new TileClearanceSettings("m", waiverTag: ""),
            _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal, "unknown refusal row"),
        };
        Assert.ThrowsAny<ArgumentException>(build);
    }

    [Fact]
    public void SettingsAcceptAnEmptyMeshRootWithoutRulesAndTheRangeBounds()
    {
        var empty = new TileClearanceSettings("");
        Assert.Empty(empty.Rules);
        Assert.Equal(TileClearanceSettings.DefaultCellSize, empty.CellSize);
        Assert.Null(empty.WaiverTag);
        Assert.Equal(TileClearanceSettings.MinCellSize, new TileClearanceSettings("m", TileClearanceSettings.MinCellSize).CellSize);
        Assert.Equal(TileClearanceSettings.MaxCellSize, new TileClearanceSettings("m", TileClearanceSettings.MaxCellSize).CellSize);
    }

    [Fact]
    public void WithRuleReplacesByNameAndWithoutRuleRemoves()
    {
        TileClearanceSettings settings = Settings();
        var rocks = new TileClearanceRule("rocks-clear-paths", ["rock"], ["path"]);

        TileClearanceSettings appended = settings.WithRule(rocks);
        Assert.Equal(["trees-clear-buildings", "rocks-clear-paths"], appended.Rules.Select(r => r.Name));
        Assert.Single(settings.Rules);

        TileClearanceSettings replaced = appended.WithRule(TreesClearBuildings(0.3f));
        Assert.Equal(["trees-clear-buildings", "rocks-clear-paths"], replaced.Rules.Select(r => r.Name));
        Assert.Equal(0.3f, replaced.Rule("trees-clear-buildings")!.Tolerance);
        Assert.Equal(0.05f, appended.Rule("trees-clear-buildings")!.Tolerance);

        TileClearanceSettings removed = replaced.WithoutRule("trees-clear-buildings");
        Assert.Equal(["rocks-clear-paths"], removed.Rules.Select(r => r.Name));
        Assert.Null(removed.Rule("trees-clear-buildings"));
        Assert.Throws<ArgumentException>(() => removed.WithoutRule("trees-clear-buildings"));

        TileClearanceSettings moved = replaced.With("../models", 0.25f, null);
        Assert.Equal("../models", moved.MeshRoot);
        Assert.Equal(0.25f, moved.CellSize);
        Assert.Null(moved.WaiverTag);
        Assert.Equal(["trees-clear-buildings", "rocks-clear-paths"], moved.Rules.Select(r => r.Name));
    }

    [Fact]
    public void ManifestRoundTripsTheBlock()
    {
        using var tmp = new TempDir();
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetClearance(Settings());
        string dir = tmp.Sub("world");
        TileWorldFile.Save(doc, dir);
        byte[] first = File.ReadAllBytes(TileWorldFile.ManifestPath(dir));

        TileWorldDocument back = TileWorldFile.Load(dir);
        TileClearanceSettings loaded = Assert.IsType<TileClearanceSettings>(back.Clearance);
        Assert.Equal("../../models", loaded.MeshRoot);
        Assert.Equal(0.1f, loaded.CellSize);
        Assert.Equal("clearance-waived", loaded.WaiverTag);
        TileClearanceRule rule = Assert.Single(loaded.Rules);
        Assert.Equal("trees-clear-buildings", rule.Name);
        Assert.Equal(["tree"], rule.SubjectTags);
        Assert.Equal(["structure"], rule.ObstacleTags);
        Assert.Equal(0.05f, rule.Tolerance);

        TileWorldFile.Save(back, dir, force: true);
        Assert.Equal(first, File.ReadAllBytes(TileWorldFile.ManifestPath(dir)));
    }

    [Fact]
    public void DefaultsAreWrittenOutExplicitly()
    {
        using var tmp = new TempDir();
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetClearance(new TileClearanceSettings("../models", rules: [new TileClearanceRule("r", ["tree"], ["structure"])]));
        string dir = tmp.Sub("world");
        TileWorldFile.Save(doc, dir);

        JsonNode block = JsonNode.Parse(File.ReadAllText(TileWorldFile.ManifestPath(dir)))!["clearance"]!;
        Assert.Equal(TileClearanceSettings.DefaultCellSize, block["cellSize"]!.GetValue<float>());
        Assert.Equal(0f, block["rules"]![0]!["tolerance"]!.GetValue<float>());
    }

    [Fact]
    public void AbsentBlockStaysAbsent()
    {
        using var tmp = new TempDir();
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        string dir = tmp.Sub("world");
        TileWorldFile.Save(doc, dir);

        JsonObject root = JsonNode.Parse(File.ReadAllText(TileWorldFile.ManifestPath(dir)))!.AsObject();
        Assert.False(root.ContainsKey("clearance"));
        Assert.Null(TileWorldFile.Load(dir).Clearance);
    }

    [Fact]
    public void WorldHashIgnoresTheBlock()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        string before = TileWorldHash.OfWorld(doc);

        doc.SetClearance(Settings());
        Assert.Equal(before, TileWorldHash.OfWorld(doc));

        doc.SetClearance(doc.Clearance!.WithRule(TreesClearBuildings(0.5f)));
        Assert.Equal(before, TileWorldHash.OfWorld(doc));
    }

    [Fact]
    public void BadBlockFailsToLoad()
    {
        using var tmp = new TempDir();
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetClearance(Settings());
        string dir = tmp.Sub("world");
        TileWorldFile.Save(doc, dir);

        string path = TileWorldFile.ManifestPath(dir);
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        JsonArray rules = root["clearance"]!["rules"]!.AsArray();
        rules.Add(rules[0]!.DeepClone());
        File.WriteAllText(path, root.ToJsonString());

        var ex = Assert.Throws<TileWorldException>(() => TileWorldFile.Load(dir));
        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
        Assert.Contains("trees-clear-buildings", ex.Message, StringComparison.Ordinal);
    }
}
