using System.Linq;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfScheduleTests
{
    [Theory]
    [InlineData("full")]
    [InlineData("rehearsal-full")]
    public void Full_blocks_match_the_frozen_v1_order(string preset)
    {
        var blocks = RendererPerfSchedules.For(preset).Blocks();
        Assert.Equal(RendererPerfProtocolTests.FullOrder, blocks.Select(b => $"{b.SceneId}/{b.Iteration}/{b.Layout}"));
        Assert.All(blocks, b => Assert.Equal((192, 256), (b.WarmupFrames, b.MeasuredFrames)));
    }

    [Theory]
    [InlineData("pilot")]
    [InlineData("rehearsal")]
    public void Pilot_blocks_match_the_frozen_v1_order(string preset)
    {
        RendererPerfBlock[] expected =
            [new("S3", RendererPerfLayout.TotalOnly, 0, 64, 128), new("S3", RendererPerfLayout.PerPass, 0, 64, 128)];
        Assert.Equal(expected, RendererPerfSchedules.For(preset).Blocks().AsEnumerable());
    }

    [Fact]
    public void Controls_rehearsal_has_no_scene_blocks() => Assert.Empty(RendererPerfSchedules.For("rehearsal-controls").Blocks());

    [Theory]
    [InlineData("pilot", 1260)]
    [InlineData("rehearsal", 1260)]
    [InlineData("full", 16944)]
    [InlineData("rehearsal-full", 16944)]
    [InlineData("rehearsal-controls", 2307)]
    public void V1_totals_and_conditioning_are_unchanged(string preset, int total)
    {
        var schedule = RendererPerfSchedules.For(preset);
        Assert.Equal(total, schedule.TotalFrames);
        Assert.Equal(total, schedule.ConditioningFrames + schedule.Blocks().Sum(b => b.WarmupFrames + b.MeasuredFrames)
            + schedule.ControlIds.Length * (schedule.ControlWarmupFrames + schedule.ControlMeasuredFrames));
        Assert.Equal("S3", schedule.ConditioningSceneId);
        Assert.Equal("conditioning:S3", schedule.ConditioningBlockId);
    }

    [Theory]
    [InlineData("pilot", false, new[] { 1, 4, 5, 7, 9, 11 }, false)]
    [InlineData("rehearsal", true, new[] { 1, 4, 5, 7, 9, 11 }, false)]
    [InlineData("full", false, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, true)]
    [InlineData("rehearsal-full", true, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, true)]
    [InlineData("rehearsal-controls", true, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, true)]
    public void V1_classes_controls_and_legs_are_unchanged(string preset, bool rehearsal,
        int[] controls, bool windowsVulkan)
    {
        var schedule = RendererPerfSchedules.For(preset);
        Assert.Equal(rehearsal ? RendererPerfExecutionClass.Rehearsal : RendererPerfExecutionClass.Performance, schedule.ExecutionClass);
        Assert.Equal(controls, schedule.ControlIds);
        string[] legs = windowsVulkan ? ["linux-vulkan", "windows-d3d11", "windows-vulkan"] : ["linux-vulkan", "windows-d3d11"];
        Assert.Equal(legs, schedule.Legs);
        Assert.All(schedule.Recipes, r => Assert.Equal(r.Scene == 0 ? "empty-native-1080/1" : "renderer-full-native-1080/1", r.Profile));
    }

    [Fact]
    public void Manifest_schedule_reads_the_table_for_its_preset()
    {
        var manifest = RendererPerfManifest.Parse(System.Text.Encoding.UTF8.GetBytes(
            RendererPerfProtocolTests.ManifestJson("full").ToJsonString()));
        Assert.Same(RendererPerfSchedules.For("full"), manifest.Schedule);
        Assert.Equal(manifest.Schedule.Blocks().AsEnumerable(), manifest.Blocks);
    }

    [Fact]
    public void Presets_keep_v1_order_and_append_the_v2_campaign()
        => Assert.Equal(["pilot", "full", "rehearsal", "rehearsal-full", "rehearsal-controls", "water-optics-v2",
            "rehearsal-water-optics-v2", "rehearsal-water-optics-v2-single"], RendererPerfSchedules.Presets.AsEnumerable());

    [Fact]
    public void Every_total_literal_reconciles_with_its_expanded_schedule()
    {
        foreach (string preset in RendererPerfSchedules.Presets)
        {
            var schedule = RendererPerfSchedules.For(preset);
            Assert.Equal(schedule.TotalFrames, schedule.ConditioningFrames
                + schedule.Blocks().Sum(b => b.WarmupFrames + b.MeasuredFrames)
                + schedule.ControlIds.Length * (schedule.ControlWarmupFrames + schedule.ControlMeasuredFrames));
        }
    }

    [Fact]
    public void Unknown_preset_refuses()
    {
        var error = Assert.Throws<RendererPerfProtocolException>(() => RendererPerfSchedules.For("smoke"));
        Assert.Equal("Preset", error.Code);
        Assert.Equal("Unknown renderer preset.", error.Message);
    }

    [Fact]
    public void Preset_is_case_sensitive()
        => Assert.Equal("Preset", Assert.Throws<RendererPerfProtocolException>(() => RendererPerfSchedules.For("Full")).Code);

    [Fact]
    public void Recipe_id_strips_only_the_version()
    {
        Assert.Equal("village", RendererPerfRecipe.IdFromVersion("village/1"));
        Assert.Equal("water-optics-v2/village/off", RendererPerfRecipe.IdFromVersion("water-optics-v2/village/off/1"));
    }

    [Theory]
    [InlineData("village")]
    [InlineData("/1")]
    [InlineData("village/")]
    public void Recipe_version_requires_an_id_and_revision(string version)
        => Assert.Equal("Identity", Assert.Throws<RendererPerfProtocolException>(() => RendererPerfRecipe.IdFromVersion(version)).Code);

    [Theory]
    [InlineData("full", 43)]
    [InlineData("rehearsal-full", 43)]
    [InlineData("pilot", 9)]
    [InlineData("rehearsal", 9)]
    [InlineData("rehearsal-controls", 13)]
    public void V1_operation_counts_are_unchanged(string preset, int operations)
        => Assert.Equal(operations, RendererPerfSchedules.For(preset).Operations);
}
