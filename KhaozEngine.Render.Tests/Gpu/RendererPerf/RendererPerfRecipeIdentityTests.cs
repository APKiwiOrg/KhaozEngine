using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfRecipeIdentityTests
{
    static readonly RendererPerfDerivedDigests Actual = new(new string('a', 64), new string('b', 64), new string('c', 64));

    [Theory]
    [InlineData("empty")]
    [InlineData("village")]
    [InlineData("crowd")]
    [InlineData("outdoors")]
    [InlineData("effects-ui")]
    [InlineData("water-optics-v2/village/off")]
    [InlineData("water-optics-v2/village/m2-on")]
    [InlineData("water-optics-v2/village/m5-on")]
    [InlineData("water-optics-v2/outdoors/off")]
    [InlineData("water-optics-v2/outdoors/m2-on")]
    [InlineData("water-optics-v2/outdoors/m5-on")]
    public void Frozen_recipe_matches_the_running_platform_family(string id)
    {
        using var recipe = RendererPerfRecipe.Create(id);
    }

    [Fact]
    public void Explicit_geometry_inputs_preserve_the_existing_public_workload()
    {
        var placements = RendererPerfPropPlacement.Create(1024, 64f, RendererPerfRecipe.Seed);
        Assert.Equal(StreamedWorldSceneContent.PropWorlds(1024, 64f, RendererPerfRecipe.Seed), placements.Select(p => p.World));
        var tube = RendererPerfTubeInputs.Character.Build();
        var original = StreamedWorldSceneContent.CharacterTube();
        Assert.Equal(original.Vertices, tube.Vertices);
        Assert.Equal(original.Indices32, tube.Indices32);
        Assert.Equal(original.RestPose, tube.RestPose);
        Assert.Equal(original.InverseBind, tube.InverseBind);
    }

    [Fact]
    public void Seeded_prop_rotation_matrices_are_derived_outputs()
    {
        using var recipe = RendererPerfRecipe.Create("village", verifyFrozen: false);
        byte[] original = recipe.Describe();
        JsonObject live = JsonNode.Parse(original)!.AsObject();
        live["Geometry"]!["PropWorlds"]![0]!["M11"] = 123f;
        live["Geometry"]!["Draws"]![64]!["World"]!["M11"] = 123f;
        var (authored, derived) = RendererPerfFrozenRecipe.Capture(original);
        var (changedAuthored, changedDerived) = RendererPerfFrozenRecipe.Capture(JsonSerializer.SerializeToUtf8Bytes(live));
        Assert.Equal(RendererPerfRecipe.Digest(authored), RendererPerfRecipe.Digest(changedAuthored));
        Assert.NotEqual(derived.ConstructionSha256, changedDerived.ConstructionSha256);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("village")]
    [InlineData("crowd")]
    [InlineData("outdoors")]
    [InlineData("effects-ui")]
    public void Live_derived_outputs_are_separate_from_portable_inputs(string id)
    {
        using var recipe = RendererPerfRecipe.Create(id, verifyFrozen: false);
        JsonObject live = JsonNode.Parse(recipe.Describe())!.AsObject();
        var (authored, derived) = RendererPerfFrozenRecipe.Capture(recipe.Describe());
        Assert.Equal(recipe.Facts.RecipeSha256, RendererPerfRecipe.Digest(authored));
        Assert.Equal(recipe.Facts.GeometrySha256, RendererPerfRecipe.Digest(authored["Geometry"]));
        Assert.Equal(recipe.Facts.Derived, derived);
        Assert.Equal(recipe.Facts.PlatformFamily, RendererPerfFrozenRecipe.PlatformFamily);
        Assert.Equal(live["Camera"]!["Azimuth"]!.GetValue<float>(), authored["Camera"]!["Azimuth"]!.GetValue<float>());
        Assert.Equal(live["BlockFrames"]!.ToJsonString(), authored["BlockFrames"]!.ToJsonString());
        Assert.Equal(live["Schedule"]!.ToJsonString(), authored["Schedule"]!.ToJsonString());
        Assert.Null(authored["Camera"]!["View"]);
        Assert.Null(authored["Frames"]![0]!["PoseSha256"]);
        live["Camera"]!["View"]!["M11"] = 123f;
        live["Frames"]![0]!["PoseSha256"] = new string('d', 64);
        if (id is "crowd" or "effects-ui")
        {
            live["Geometry"]!["Actor"]!["InverseBind"]![0]!["M11"] = 456f;
            string palette = id == "crowd" ? "PaletteAtZero" : "Palettes";
            Assert.Null(authored["Geometry"]![palette]);
            Assert.Equal(256, authored["Geometry"]!["PoseAngles"]!.AsArray().Count);
            live["Geometry"]![palette] = new JsonArray();
        }
        var (changedAuthored, changedDerived) = RendererPerfFrozenRecipe.Capture(JsonSerializer.SerializeToUtf8Bytes(live));
        Assert.Equal(RendererPerfRecipe.Digest(authored), RendererPerfRecipe.Digest(changedAuthored));
        Assert.NotEqual(derived.ConstructionSha256, changedDerived.ConstructionSha256);
    }

    [Theory]
    [InlineData("Camera")]
    [InlineData("PoseAngles")]
    [InlineData("Geometry")]
    [InlineData("Assets")]
    [InlineData("Settings")]
    [InlineData("BlockFrames")]
    [InlineData("Schedule")]
    [InlineData("Frames")]
    [InlineData("PropPlacements")]
    [InlineData("TubeInputs")]
    public void Live_authored_mutations_remain_portable_failures(string input)
    {
        using var recipe = RendererPerfRecipe.Create("crowd", verifyFrozen: false);
        byte[] original = recipe.Describe();
        JsonObject live = JsonNode.Parse(original)!.AsObject();
        switch (input)
        {
            case "Camera": live["Camera"]!["Distance"] = 151f; break;
            case "PoseAngles": live["Geometry"]!["PoseAngles"]![0] = .2f; break;
            case "Geometry": live["Geometry"]!["ActorDraws"]![0]!["CastsShadows"] = false; break;
            case "Assets": live["Assets"]!["TextureWidth"] = 8; break;
            case "Settings": live["Settings"]!["Outline"] = true; break;
            case "BlockFrames": live["BlockFrames"]![0]!["EffectTimeSeconds"] = 99f; break;
            case "Schedule": live["Schedule"]!["StaticRebuildBudget"] = 2; break;
            case "Frames": live["Frames"]![0]!["Azimuth"] = .7f; break;
            case "PropPlacements": live["Geometry"]!["Rigid"]!["PropPlacements"]![0]!["Azimuth"] = 2f; break;
            case "TubeInputs": live["Geometry"]!["Actor"]!["Inputs"]!["Radius"] = .6f; break;
        }
        var (authored, derived) = RendererPerfFrozenRecipe.Capture(JsonSerializer.SerializeToUtf8Bytes(live));
        JsonObject frozen = JsonNode.Parse(RendererPerfFrozenRecipe.Freeze(original))!.AsObject();
        foreach (string family in new[] { "osx-arm64", "linux-x64", "win-x64" })
        {
            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                RendererPerfFrozenRecipe.Verify(frozen, authored, derived, family));
            Assert.Contains("authored", error.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("osx-arm64")]
    [InlineData("linux-x64")]
    [InlineData("win-x64")]
    [InlineData("unknown-arm64")]
    public void Mutated_authored_input_is_rejected_on_every_family(string family)
    {
        JsonObject authored = Authored();
        authored["Camera"]!["Azimuth"] = .7f;
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            RendererPerfFrozenRecipe.Verify(Frozen(), authored, Actual, family));
        Assert.Contains("authored", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("osx-arm64", "GeometrySha256")]
    [InlineData("osx-arm64", "SequenceSha256")]
    [InlineData("osx-arm64", "ConstructionSha256")]
    [InlineData("linux-x64", "GeometrySha256")]
    [InlineData("linux-x64", "SequenceSha256")]
    [InlineData("linux-x64", "ConstructionSha256")]
    [InlineData("win-x64", "GeometrySha256")]
    [InlineData("win-x64", "SequenceSha256")]
    [InlineData("win-x64", "ConstructionSha256")]
    public void Mutated_derived_digest_is_rejected_only_for_its_family(string family, string digest)
    {
        JsonObject frozen = Frozen();
        frozen["DerivedByPlatform"]![family]![digest] = new string('d', 64);
        foreach (string other in new[] { "osx-arm64", "linux-x64", "win-x64" })
        {
            if (other == family)
                Assert.Throws<InvalidOperationException>(() => RendererPerfFrozenRecipe.Verify(frozen, Authored(), Actual, other));
            else
                RendererPerfFrozenRecipe.Verify(frozen, Authored(), Actual, other);
        }
    }

    [Theory]
    [InlineData("osx-arm64")]
    [InlineData("linux-x64")]
    [InlineData("win-x64")]
    [InlineData("freebsd-arm64")]
    public void Missing_family_is_rejected_with_the_actual_digests(string family)
    {
        JsonObject frozen = Frozen();
        ((JsonObject)frozen["DerivedByPlatform"]!).Remove(family);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            RendererPerfFrozenRecipe.Verify(frozen, Authored(), Actual, family));
        Assert.Contains(family, error.Message, StringComparison.Ordinal);
        Assert.Contains(Actual.GeometrySha256, error.Message, StringComparison.Ordinal);
        Assert.Contains(Actual.SequenceSha256, error.Message, StringComparison.Ordinal);
        Assert.Contains(Actual.ConstructionSha256, error.Message, StringComparison.Ordinal);
    }

    static JsonObject Authored() => JsonNode.Parse("""
        {"Id":"fixture","Camera":{"Azimuth":0.6},"BlockFrames":[{"Ordinal":64,"SequenceIndex":64}],"Schedule":{"StaticPointRows":1}}
        """)!.AsObject();

    static JsonObject Frozen() => new()
    {
        ["Schema"] = "khaozengine.renderer-perf-frozen-recipe/2",
        ["Authored"] = Authored(),
        ["DerivedByPlatform"] = new JsonObject
        {
            ["osx-arm64"] = JsonSerializer.SerializeToNode(Actual),
            ["linux-x64"] = JsonSerializer.SerializeToNode(Actual),
            ["win-x64"] = JsonSerializer.SerializeToNode(Actual),
        },
    };
}
