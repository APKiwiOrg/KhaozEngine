using System;
using System.Collections.Generic;
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
    [InlineData("empty-mesh-root-with-rules", TileWorldRefusal.ClearanceMeshRootRequired)]
    [InlineData("blank-mesh-root-with-rules", TileWorldRefusal.ClearanceMeshRootRequired)]
    [InlineData("cell-size-below-range", TileWorldRefusal.ClearanceCellSizeOutsideRange)]
    [InlineData("cell-size-above-range", TileWorldRefusal.ClearanceCellSizeOutsideRange)]
    [InlineData("cell-size-nan", TileWorldRefusal.ClearanceCellSizeOutsideRange)]
    [InlineData("duplicate-rule-name", TileWorldRefusal.ClearanceRuleDuplicate)]
    [InlineData("empty-rule-name", TileWorldRefusal.ClearanceRuleNameBlank)]
    [InlineData("empty-subject-tags", TileWorldRefusal.ClearanceTagsEmpty)]
    [InlineData("empty-obstacle-tags", TileWorldRefusal.ClearanceTagsEmpty)]
    [InlineData("empty-subject-tag-string", TileWorldRefusal.ClearanceTagBlank)]
    [InlineData("empty-obstacle-tag-string", TileWorldRefusal.ClearanceTagBlank)]
    [InlineData("negative-tolerance", TileWorldRefusal.ClearanceToleranceInvalid)]
    [InlineData("nan-tolerance", TileWorldRefusal.ClearanceToleranceInvalid)]
    [InlineData("infinite-tolerance", TileWorldRefusal.ClearanceToleranceInvalid)]
    [InlineData("empty-waiver-tag", TileWorldRefusal.ClearanceWaiverTagBlank)]
    [InlineData("null-rule", TileWorldRefusal.ClearanceRuleNull)]
    [InlineData("null-subject-tags", TileWorldRefusal.ClearanceTagsEmpty)]
    [InlineData("null-obstacle-tag", TileWorldRefusal.ClearanceTagBlank)]
    public void SettingsRefuseInvalidValues(string refusal, TileWorldRefusal reason)
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
            "null-rule" => () => new TileClearanceSettings("m", rules: [TreesClearBuildings(), null!]),
            "null-subject-tags" => () => new TileClearanceSettings("m", rules: [new TileClearanceRule("r", null!, ["structure"])]),
            "null-obstacle-tag" => () => new TileClearanceSettings("m", rules: [new TileClearanceRule("r", ["tree"], [null!])]),
            _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal, "unknown refusal row"),
        };
        var ex = Assert.Throws<TileClearanceArgumentException>(build);
        Assert.IsAssignableFrom<ArgumentException>(ex);
        Assert.Null(ex.ParamName);
        Assert.Equal(reason, ex.Reason);
        switch (reason)
        {
            case TileWorldRefusal.ClearanceMeshRootRequired:
                Assert.Equal(refusal == "empty-mesh-root-with-rules" ? "" : "  ", ex.MeshRoot);
                Assert.Equal(1, ex.RuleCount);
                break;
            case TileWorldRefusal.ClearanceCellSizeOutsideRange:
                Assert.Equal(refusal switch
                {
                    "cell-size-below-range" => TileClearanceSettings.MinCellSize / 2f,
                    "cell-size-above-range" => TileClearanceSettings.MaxCellSize * 2f,
                    _ => float.NaN,
                }, ex.CellSize);
                Assert.Equal(TileClearanceSettings.MinCellSize, ex.Minimum);
                Assert.Equal(TileClearanceSettings.MaxCellSize, ex.Maximum);
                break;
            case TileWorldRefusal.ClearanceWaiverTagBlank:
                Assert.Equal("", ex.WaiverTag);
                break;
            case TileWorldRefusal.ClearanceRuleDuplicate:
                Assert.Equal("trees-clear-buildings", ex.RuleName);
                break;
            case TileWorldRefusal.ClearanceRuleNameBlank:
                Assert.Equal("", ex.RuleName);
                break;
            case TileWorldRefusal.ClearanceRuleNull:
                Assert.Equal(1, ex.RuleIndex);
                break;
            case TileWorldRefusal.ClearanceToleranceInvalid:
                Assert.Equal("trees-clear-buildings", ex.RuleName);
                Assert.Equal(refusal switch
                {
                    "negative-tolerance" => -0.01f,
                    "infinite-tolerance" => float.PositiveInfinity,
                    _ => float.NaN,
                }, ex.Tolerance);
                break;
            case TileWorldRefusal.ClearanceTagsEmpty:
                Assert.Equal("r", ex.RuleName);
                Assert.Equal(refusal == "empty-obstacle-tags" ? "obstacleTags" : "subjectTags", ex.Field);
                Assert.Equal(refusal == "null-subject-tags" ? (int?)null : 0, ex.TagCount);
                break;
            case TileWorldRefusal.ClearanceTagBlank:
                Assert.Equal("r", ex.RuleName);
                Assert.Equal(refusal == "empty-subject-tag-string" ? "subjectTags" : "obstacleTags", ex.Field);
                Assert.Equal(refusal == "empty-subject-tag-string" ? 1 : 0, ex.TagIndex);
                Assert.Equal(refusal == "null-obstacle-tag" ? null : "", ex.Tag);
                break;
        }
    }

    [Fact]
    public void NullParametersKeepTheirBclContext()
    {
        var meshRoot = Assert.Throws<ArgumentNullException>(() => new TileClearanceSettings(null!));
        Assert.Equal("meshRoot", meshRoot.ParamName);
        var rule = Assert.Throws<ArgumentNullException>(() => Settings().WithRule(null!));
        Assert.Equal("rule", rule.ParamName);
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

    // The same refusals on every OS, so a root accepted on one head never fails to load on another.
    [Theory]
    [InlineData(@"C:\kit", TileWorldRefusal.ClearanceMeshRootRooted)]
    [InlineData("C:/kit", TileWorldRefusal.ClearanceMeshRootRooted)]
    [InlineData("c:kit", TileWorldRefusal.ClearanceMeshRootRooted)]
    [InlineData(@"\kit", TileWorldRefusal.ClearanceMeshRootRooted)]
    [InlineData("/kit", TileWorldRefusal.ClearanceMeshRootRooted)]
    [InlineData(@"..\models", TileWorldRefusal.ClearanceMeshRootBackslash)]
    public void SettingsRefuseAMeshRootThatIsNotPortable(string meshRoot, TileWorldRefusal reason)
    {
        var ex = Assert.Throws<TileClearanceArgumentException>(() => new TileClearanceSettings(meshRoot));
        Assert.Equal(reason, ex.Reason);
        Assert.Equal(meshRoot, ex.MeshRoot);
        var withRules = Assert.Throws<TileClearanceArgumentException>(
            () => new TileClearanceSettings(meshRoot, rules: [TreesClearBuildings()]));
        Assert.Equal(reason, withRules.Reason);
        Assert.Equal(meshRoot, withRules.MeshRoot);
        var with = Assert.Throws<TileClearanceArgumentException>(() => Settings().With(meshRoot, 0.1f, null));
        Assert.Equal(reason, with.Reason);
        Assert.Equal(meshRoot, with.MeshRoot);
    }

    [Theory]
    [InlineData("../models")]
    [InlineData("models/kit")]
    [InlineData("./models")]
    public void SettingsAcceptAForwardSlashRelativeMeshRoot(string meshRoot)
    {
        Assert.Equal(meshRoot, new TileClearanceSettings(meshRoot, rules: [TreesClearBuildings()]).MeshRoot);
    }

    [Fact]
    public void RulesCompareByValue()
    {
        TileClearanceRule rule = TreesClearBuildings();
        TileClearanceSettings settings = Settings();
        TileClearanceRule stored = settings.Rules[0];

        Assert.Equal(rule, stored);
        Assert.True(rule == stored);
        Assert.Equal(rule.GetHashCode(), stored.GetHashCode());
        Assert.Contains(rule, settings.WithRule(rule).Rules);

        var forward = new TileClearanceRule("r", ["tree", "bush"], ["structure"]);
        var reordered = new TileClearanceRule("r", ["bush", "tree"], ["structure"]);
        Assert.NotEqual(forward, reordered);
        Assert.NotEqual(rule, TreesClearBuildings(0.06f));
        Assert.NotEqual(rule, rule with { Name = "Trees-clear-buildings" });
        Assert.NotEqual(rule, rule with { ObstacleTags = ["structure", "wall"] });
        Assert.False(rule.Equals(null));
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
        var missing = Assert.Throws<TileClearanceArgumentException>(() => removed.WithoutRule("trees-clear-buildings"));
        Assert.Equal(TileWorldRefusal.ClearanceRuleMissing, missing.Reason);
        Assert.Equal("trees-clear-buildings", missing.RuleName);

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
        Assert.Equal(TreesClearBuildings(), rule);

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
    public void RootedMeshRootFailsToLoad()
    {
        using var tmp = new TempDir();
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetClearance(Settings());
        string dir = tmp.Sub("world");
        TileWorldFile.Save(doc, dir);

        string path = TileWorldFile.ManifestPath(dir);
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["clearance"]!["meshRoot"] = "/abs/models";
        File.WriteAllText(path, root.ToJsonString());

        var ex = Assert.Throws<TileWorldException>(() => TileWorldFile.Load(dir));
        Assert.Equal(TileWorldRefusal.ClearanceMeshRootRooted, ex.Reason);
        Assert.Equal(path, ex.SourceName);
        Assert.Equal("/abs/models", ex.MeshRoot);
        var inner = Assert.IsType<TileClearanceArgumentException>(ex.InnerException);
        Assert.Equal(TileWorldRefusal.ClearanceMeshRootRooted, inner.Reason);
        Assert.Equal("/abs/models", inner.MeshRoot);
    }

    [Theory]
    [InlineData(TileWorldRefusal.ClearanceRuleDuplicate)]
    [InlineData(TileWorldRefusal.ClearanceMeshRootBackslash)]
    [InlineData(TileWorldRefusal.ClearanceCellSizeOutsideRange)]
    [InlineData(TileWorldRefusal.ClearanceWaiverTagBlank)]
    [InlineData(TileWorldRefusal.ClearanceMeshRootRequired)]
    [InlineData(TileWorldRefusal.ClearanceRuleNull)]
    [InlineData(TileWorldRefusal.ClearanceRuleNameBlank)]
    [InlineData(TileWorldRefusal.ClearanceToleranceInvalid)]
    [InlineData(TileWorldRefusal.ClearanceTagsEmpty)]
    [InlineData(TileWorldRefusal.ClearanceTagBlank)]
    [InlineData(TileWorldRefusal.ClearanceRulesNull)]
    [InlineData(TileWorldRefusal.ClearanceMeshRootNull)]
    public void BadBlockFailsToLoad(TileWorldRefusal reason)
    {
        using var tmp = new TempDir();
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetClearance(Settings());
        string dir = tmp.Sub("world");
        TileWorldFile.Save(doc, dir);

        string path = TileWorldFile.ManifestPath(dir);
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        JsonNode block = root["clearance"]!;
        JsonArray rules = block["rules"]!.AsArray();
        switch (reason)
        {
            case TileWorldRefusal.ClearanceRuleDuplicate: rules.Add(rules[0]!.DeepClone()); break;
            case TileWorldRefusal.ClearanceMeshRootBackslash: block["meshRoot"] = @"..\models"; break;
            case TileWorldRefusal.ClearanceCellSizeOutsideRange: block["cellSize"] = 2f; break;
            case TileWorldRefusal.ClearanceWaiverTagBlank: block["waiverTag"] = " "; break;
            case TileWorldRefusal.ClearanceMeshRootRequired: block["meshRoot"] = ""; break;
            case TileWorldRefusal.ClearanceRuleNull: rules.Add((JsonNode?)null); break;
            case TileWorldRefusal.ClearanceRuleNameBlank: rules[0]!["name"] = ""; break;
            case TileWorldRefusal.ClearanceToleranceInvalid: rules[0]!["tolerance"] = -1f; break;
            case TileWorldRefusal.ClearanceTagsEmpty: rules[0]!["subjectTags"] = new JsonArray(); break;
            case TileWorldRefusal.ClearanceTagBlank: rules[0]!["obstacleTags"] = new JsonArray("structure", ""); break;
            case TileWorldRefusal.ClearanceRulesNull: block["rules"] = null; break;
            case TileWorldRefusal.ClearanceMeshRootNull: block["meshRoot"] = null; break;
        }
        File.WriteAllText(path, root.ToJsonString());

        var ex = Assert.Throws<TileWorldException>(() => TileWorldFile.Load(dir));
        Assert.Equal(reason, ex.Reason);
        Assert.Equal(path, ex.SourceName);
        if (reason == TileWorldRefusal.ClearanceMeshRootNull)
        {
            Assert.Equal("meshRoot", ex.Field);
            var nullInput = Assert.IsType<ArgumentNullException>(ex.InnerException);
            Assert.Equal("meshRoot", nullInput.ParamName);
            return;
        }
        var inner = Assert.IsType<TileClearanceArgumentException>(ex.InnerException);
        Assert.Equal(reason, inner.Reason);
        Assert.Equal(inner.MeshRoot, ex.MeshRoot);
        Assert.Equal(inner.CellSize, ex.CellSize);
        Assert.Equal(inner.Minimum, ex.Minimum);
        Assert.Equal(inner.Maximum, ex.Maximum);
        Assert.Equal(inner.WaiverTag, ex.WaiverTag);
        Assert.Equal(inner.RuleName, ex.RuleName);
        Assert.Equal(inner.RuleCount, ex.RuleCount);
        Assert.Equal(inner.RuleIndex, ex.RuleIndex);
        Assert.Equal(inner.Field, ex.Field);
        Assert.Equal(inner.Tolerance, ex.Tolerance);
        Assert.Equal(inner.TagCount, ex.TagCount);
        Assert.Equal(inner.TagIndex, ex.TagIndex);
        Assert.Equal(inner.Tag, ex.Tag);
        switch (reason)
        {
            case TileWorldRefusal.ClearanceRuleDuplicate: Assert.Equal("trees-clear-buildings", ex.RuleName); break;
            case TileWorldRefusal.ClearanceMeshRootBackslash: Assert.Equal(@"..\models", ex.MeshRoot); break;
            case TileWorldRefusal.ClearanceCellSizeOutsideRange:
                Assert.Equal(2f, ex.CellSize);
                Assert.Equal(TileClearanceSettings.MinCellSize, ex.Minimum);
                Assert.Equal(TileClearanceSettings.MaxCellSize, ex.Maximum);
                break;
            case TileWorldRefusal.ClearanceWaiverTagBlank: Assert.Equal(" ", ex.WaiverTag); break;
            case TileWorldRefusal.ClearanceMeshRootRequired:
                Assert.Equal("", ex.MeshRoot);
                Assert.Equal(1, ex.RuleCount);
                break;
            case TileWorldRefusal.ClearanceRuleNull: Assert.Equal(1, ex.RuleIndex); break;
            case TileWorldRefusal.ClearanceRuleNameBlank: Assert.Equal("", ex.RuleName); break;
            case TileWorldRefusal.ClearanceToleranceInvalid:
                Assert.Equal("trees-clear-buildings", ex.RuleName);
                Assert.Equal(-1f, ex.Tolerance);
                break;
            case TileWorldRefusal.ClearanceTagsEmpty:
                Assert.Equal("trees-clear-buildings", ex.RuleName);
                Assert.Equal("subjectTags", ex.Field);
                Assert.Equal(0, ex.TagCount);
                break;
            case TileWorldRefusal.ClearanceTagBlank:
                Assert.Equal("trees-clear-buildings", ex.RuleName);
                Assert.Equal("obstacleTags", ex.Field);
                Assert.Equal(1, ex.TagIndex);
                Assert.Equal("", ex.Tag);
                break;
            case TileWorldRefusal.ClearanceRulesNull: Assert.Equal("rules", ex.Field); break;
        }
    }
}
