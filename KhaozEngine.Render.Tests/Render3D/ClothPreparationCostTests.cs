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

    [Theory]
    [InlineData(null, null, null, false, false)]
    [InlineData("1", null, null, true, false)]
    [InlineData(null, "1", null, false, true)]
    [InlineData(null, null, ClothPreparationAdmission.Filter, true, false)]
    [InlineData(null, null, ClothPreparationAdmission.ComparisonFilter, false, true)]
    [InlineData(null, "1", "Category=Diagnostic", false, false)]
    [InlineData(null, "1", ClothPreparationAdmission.Filter, true, false)]
    public void Discovery_arms_only_the_requested_workload(string? legacySmoke, string? comparisonSmoke, string? filter,
        bool legacyExpected, bool comparisonExpected)
    {
        string? Read(string name) => name switch
        {
            "FILTER" => filter,
            "KE_CLOTH_PREPARATION_SMOKE" => legacySmoke,
            "KE_CLOTH_FACTOR_COMPARISON_SMOKE" => comparisonSmoke,
            "KE_GPU_TESTS" => "0",
            _ => null,
        };
        Assert.Equal(legacyExpected, ClothPreparationAdmission.IsSelected(ClothPreparationAdmission.Filter, Read));
        Assert.Equal(comparisonExpected, ClothPreparationAdmission.IsSelected(ClothPreparationAdmission.ComparisonFilter, Read));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("FILTER", ClothPreparationAdmission.Filter)]
    [InlineData("GITHUB_RUN_ATTEMPT", "2")]
    [InlineData("GITHUB_RUN_ATTEMPT", null)]
    [InlineData("GITHUB_EVENT_NAME", "push")]
    [InlineData("RUNNER_ENVIRONMENT", "self-hosted")]
    [InlineData("GITHUB_WORKFLOW_REF", "APKiwiOrg/KhaozEngine/.github/workflows/gpu-hosted-bench.yml@refs/heads/other")]
    public void Comparison_admits_only_the_first_exact_hosted_dispatch(string? variable, string? replacement)
    {
        var environment = new Dictionary<string, string>
        {
            ["FILTER"] = ClothPreparationAdmission.ComparisonFilter,
            ["GITHUB_WORKFLOW_REF"] = "APKiwiOrg/KhaozEngine/.github/workflows/gpu-hosted-bench.yml@refs/heads/main",
            ["RUNNER_ENVIRONMENT"] = "github-hosted",
            ["KE_GRAPHICS_BACKEND"] = "vulkan-native",
            ["GITHUB_EVENT_NAME"] = "workflow_dispatch",
            ["GITHUB_RUN_ATTEMPT"] = "1",
        };
        string? Read(string name) => name == variable ? replacement : environment.GetValueOrDefault(name);
        Assert.Equal(variable is null, ClothPreparationAdmission.HasHostedContext(Read, true, ClothPreparationAdmission.ComparisonFilter));
        Assert.False(ClothPreparationAdmission.HasHostedContext(Read, false, ClothPreparationAdmission.ComparisonFilter));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("ref", "feature/equipment-fit")]
    [InlineData("ref", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("legs", "all")]
    [InlineData("filter", ClothPreparationAdmission.Filter)]
    [InlineData("coverage-cost-unit", "prior-unit")]
    [InlineData("renderer-perf-unit", "other-unit")]
    public void Comparison_refuses_a_different_checkout_or_unit(string? input, string? replacement)
    {
        string sha = new('a', 40);
        var values = new Dictionary<string, string>
        {
            ["ref"] = sha,
            ["legs"] = "linux-vulkan",
            ["filter"] = ClothPreparationAdmission.ComparisonFilter,
        };
        if (input is not null) values[input] = replacement!;
        JsonElement inputs = JsonSerializer.SerializeToElement(values);
        Assert.Equal(input is null, ClothPreparationAdmission.MatchesDispatch(inputs, sha, ClothPreparationAdmission.ComparisonFilter));
        Assert.False(ClothPreparationAdmission.MatchesDispatch(inputs, "ref: refs/heads/main", ClothPreparationAdmission.ComparisonFilter));
    }

    [Fact]
    public void Durations_use_nearest_rank_and_smoke_has_no_samples()
    {
        long[] ticks = Enumerable.Range(1, 20).Reverse().Select(tick => (long)tick).ToArray();
        Assert.Equal(new ClothPreparationStatistics(5, 9.5, 10), ClothPreparationStatistics.Summarize(ticks, 2_000));
        Assert.Equal(new ClothPreparationStatistics(null, null, null), ClothPreparationStatistics.Summarize(null, 0));
    }
}
