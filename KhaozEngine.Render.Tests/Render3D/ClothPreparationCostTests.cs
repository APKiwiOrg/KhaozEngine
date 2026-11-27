using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class ClothPreparationCostTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("FILTER")]
    [InlineData("GITHUB_WORKFLOW_REF")]
    [InlineData("RUNNER_ENVIRONMENT")]
    [InlineData("KE_GRAPHICS_BACKEND")]
    [InlineData("architecture")]
    public void Timing_requires_the_exact_hosted_job(string? wrong)
    {
        var environment = new Dictionary<string, string>
        {
            ["FILTER"] = ClothPreparationAdmission.Filter,
            ["GITHUB_WORKFLOW_REF"] = "APKiwiOrg/KhaozEngine/.github/workflows/gpu-hosted-bench.yml@refs/heads/main",
            ["RUNNER_ENVIRONMENT"] = "github-hosted",
            ["KE_GRAPHICS_BACKEND"] = "vulkan-native",
        };
        Assert.Equal(wrong is null, ClothPreparationAdmission.HasHostedContext(
            name => name == wrong ? "other" : environment.GetValueOrDefault(name), wrong != "architecture"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("ref", "feature/equipment-fit")]
    [InlineData("ref", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("legs", "all")]
    [InlineData("filter", "Category=Diagnostic")]
    public void Timing_requires_the_exact_dispatch_and_detached_checkout(string? input, string? replacement)
    {
        string sha = new('a', 40);
        var values = new Dictionary<string, string>
        {
            ["ref"] = sha,
            ["legs"] = "linux-vulkan",
            ["filter"] = ClothPreparationAdmission.Filter,
        };
        if (input is not null) values[input] = replacement!;
        JsonElement inputs = JsonSerializer.SerializeToElement(values);
        Assert.Equal(input is null, ClothPreparationAdmission.MatchesDispatch(inputs, sha));
    }

    [Fact]
    public void Durations_use_nearest_rank_and_smoke_has_no_samples()
    {
        long[] ticks = Enumerable.Range(1, 20).Reverse().Select(tick => (long)tick).ToArray();
        Assert.Equal(new ClothPreparationStatistics(5, 9.5, 10), ClothPreparationStatistics.Summarize(ticks, 2_000));
        Assert.Equal(new ClothPreparationStatistics(null, null, null), ClothPreparationStatistics.Summarize(null, 0));
    }
}
