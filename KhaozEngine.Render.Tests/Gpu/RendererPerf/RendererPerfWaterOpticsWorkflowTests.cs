using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfWaterOpticsWorkflowTests
{
    [Theory]
    [InlineData("linux-vulkan", "gpu-t4-linux", "vulkan-native", 35)]
    [InlineData("windows-d3d11", "gpu-t4-windows", "direct3d11-native", 50)]
    public void Select_routes_the_paid_v2_legs(string leg, string runner, string backend, int minutes)
    {
        using var workspace = new RendererPerfWorkflowTrustTests.AdmissionWorkspace();
        Prepare(workspace.Fixture, "water-optics-v2", leg);
        var result = workspace.Select();
        Assert.True(result.Code == 0, result.Output);
        JsonNode row = MatrixRow(File.ReadAllText(workspace.Output));
        Assert.Equal(leg, row["leg"]!.GetValue<string>());
        Assert.Equal(runner, row["runner"]!.GetValue<string>());
        Assert.Equal(backend, row["backend"]!.GetValue<string>());
        Assert.Equal(minutes, row["jobMinutes"]!.GetValue<int>());
        Assert.Equal("performance", row["executionClass"]!.GetValue<string>());
        Assert.Equal(1095, row["lockedGraphicsClockMHz"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("rehearsal-water-optics-v2", "linux-vulkan", "ubuntu-24.04", "mesa-lavapipe-software")]
    [InlineData("rehearsal-water-optics-v2-full", "windows-d3d11", "windows-2025-vs2026", "microsoft-warp-software")]
    public void Select_routes_the_v2_rehearsals(string preset, string leg, string runner, string adapter)
    {
        using var workspace = new RendererPerfWorkflowTrustTests.AdmissionWorkspace();
        Prepare(workspace.Fixture, preset, leg);
        var result = workspace.Select();
        Assert.True(result.Code == 0, result.Output);
        JsonNode row = MatrixRow(File.ReadAllText(workspace.Output));
        Assert.Equal(leg, row["leg"]!.GetValue<string>());
        Assert.Equal(runner, row["runner"]!.GetValue<string>());
        Assert.Equal(350, row["jobMinutes"]!.GetValue<int>());
        Assert.Equal("rehearsal", row["executionClass"]!.GetValue<string>());
        Assert.True(row.AsObject().ContainsKey("lockedGraphicsClockMHz"));
        Assert.Null(row["lockedGraphicsClockMHz"]);
        Assert.Equal(leg == "linux-vulkan" ? "llvmpipe" : "", row["vulkanDevice"]!.GetValue<string>());
        Assert.Equal(leg == "windows-d3d11" ? "warp" : "", row["d3dAdapter"]!.GetValue<string>());
        workspace.Fixture.Manifest["execution"]!["adapterPolicy"] = "nvidia-t4-hardware";
        workspace.Fixture.SaveManifest();
        Assert.NotEqual(0, workspace.Fixture.Run("Select").Code);
        workspace.Fixture.Manifest["execution"]!["adapterPolicy"] = adapter;
        workspace.Fixture.Manifest["limits"]!["unitSeconds"] = 5400;
        workspace.Fixture.SaveManifest();
        Assert.NotEqual(0, workspace.Fixture.Run("Select").Code);
    }

    [Theory]
    [InlineData("rehearsal-water-optics-v2", "windows-d3d11")]
    [InlineData("rehearsal-water-optics-v2", "windows-vulkan")]
    [InlineData("rehearsal-water-optics-v2-full", "linux-vulkan")]
    [InlineData("rehearsal-water-optics-v2-full", "windows-vulkan")]
    public void Select_refuses_other_v2_rehearsal_legs(string preset, string leg)
    {
        using var fixture = new RendererPerfWorkflowContractTests.WorkflowFixture();
        Prepare(fixture, preset, leg);
        var result = fixture.Run("Select");
        Assert.NotEqual(0, result.Code);
        Assert.Contains("No software rehearsal class admits this preset and leg", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("matrix=", result.Output, StringComparison.Ordinal);
    }

    // The last three caps differ from a boundary only past double precision, so the selector must compare exact decimals.
    [Theory]
    [InlineData("water-optics-v2", "linux-vulkan", "7.383")]
    [InlineData("water-optics-v2", "windows-d3d11", "7.383")]
    [InlineData("water-optics-v2", "linux-vulkan", "1.975")]
    [InlineData("water-optics-v2", "windows-d3d11", "5.405")]
    [InlineData("full", "linux-vulkan", "22.001")]
    [InlineData("full", "windows-d3d11", "22.001")]
    [InlineData("pilot", "linux-vulkan", "22.001")]
    [InlineData("pilot", "windows-d3d11", "22.001")]
    [InlineData("water-optics-v2", "linux-vulkan", "7.382000000000001")]
    [InlineData("full", "windows-d3d11", "22.000000000000001")]
    [InlineData("water-optics-v2", "linux-vulkan", "1.9759999999999999")]
    public void Select_refuses_campaign_overruns(string preset, string leg, string cap)
    {
        using var workspace = new RendererPerfWorkflowTrustTests.AdmissionWorkspace();
        Prepare(workspace.Fixture, preset, leg, decimal.Parse(cap, CultureInfo.InvariantCulture));
        Assert.Matches("\"campaignMaximumUsd\":" + Regex.Escape(cap) + "[,}]", File.ReadAllText(workspace.Fixture.ManifestPath));
        var result = workspace.Select();
        Assert.NotEqual(0, result.Code);
        Assert.Contains("Paid campaign cap must cover the unit budget", result.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(workspace.Output) && File.ReadAllText(workspace.Output).Contains("matrix=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("approval", "campaignMaximumUsd")]
    [InlineData("budget", "maximumCostUsd")]
    public void Select_refuses_a_paid_amount_that_is_not_a_json_number(string section, string name)
    {
        using var fixture = new RendererPerfWorkflowContractTests.WorkflowFixture();
        Prepare(fixture, "water-optics-v2", "linux-vulkan");
        fixture.Manifest[section]![name] = fixture.Manifest[section]![name]!.ToJsonString();
        fixture.SaveManifest();
        var result = fixture.Run("Select");
        Assert.NotEqual(0, result.Code);
        Assert.Contains($"Manifest {section}.{name} must be an exact USD decimal", result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("matrix=", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pilot", "linux-vulkan")]
    [InlineData("pilot", "windows-d3d11")]
    [InlineData("full", "linux-vulkan")]
    [InlineData("full", "windows-d3d11")]
    [InlineData("full", "windows-vulkan")]
    [InlineData("water-optics-v2", "linux-vulkan")]
    [InlineData("water-optics-v2", "windows-d3d11")]
    public void Select_accepts_paid_campaign_boundaries(string preset, string leg)
    {
        using var fixture = new RendererPerfWorkflowContractTests.WorkflowFixture();
        Prepare(fixture, preset, leg);
        fixture.Manifest["approval"]!["campaignMaximumUsd"] = fixture.Manifest["budget"]!["maximumCostUsd"]!.DeepClone();
        fixture.SaveManifest();
        fixture.Accept("Select", "matrix=");
        fixture.Manifest["approval"]!["campaignMaximumUsd"] = preset == "water-optics-v2" ? 7.382m : 22m;
        fixture.SaveManifest();
        fixture.Accept("Select", "matrix=");
    }

    [Theory]
    [InlineData("water-optics-v2", "Windows Vulkan is not an approved leg for the water-optics-v2 campaign.")]
    [InlineData("pilot", "Windows Vulkan has no initial pilot")]
    public void Select_refuses_windows_vulkan_for_v2_and_preserves_the_v1_reason(string preset, string reason)
    {
        using var fixture = new RendererPerfWorkflowContractTests.WorkflowFixture();
        Prepare(fixture, preset, "windows-vulkan");
        var result = fixture.Run("Select");
        Assert.NotEqual(0, result.Code);
        Assert.Contains(reason, result.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("matrix=", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Selector_maximums_match_the_policy()
    {
        string selector = RendererPerfWorkflowText.Selector();
        decimal Constant(string name)
        {
            Match match = Regex.Match(selector, @"(?m)^\$" + name + @" = ([0-9.]+)d$");
            Assert.True(match.Success, "Missing selector campaign maximum " + name);
            return decimal.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        Assert.Equal(RendererPerfPaidPolicy.CampaignMaximumUsd, Constant("campaignMaximumUsd"));
        Assert.Equal(RendererPerfPaidPolicy.WaterOpticsV2CampaignMaximumUsd, Constant("waterOpticsV2CampaignMaximumUsd"));
    }

    [Fact]
    public void Shared_class_inventory_covers_every_admitted_schedule_leg()
    {
        var expected = RendererPerfSchedules.Presets.SelectMany(preset =>
        {
            var schedule = RendererPerfSchedules.For(preset);
            return schedule.Legs.Where(leg => schedule.ExecutionClass == RendererPerfExecutionClass.Performance || leg != "windows-vulkan")
                .Select(leg => (preset, leg));
        }).OrderBy(row => row);
        var actual = RendererPerfColdLimitPolicyTests.Classes().Select(row => ((string)row[0], (string)row[1])).ToArray();
        Assert.Equal(expected, actual.OrderBy(row => row));
        Assert.Equal(actual.Length, actual.Distinct().Count());
    }

    static JsonNode MatrixRow(string output)
        => Assert.Single(JsonNode.Parse(output.Split('\n').Single(line => line.StartsWith("matrix=", StringComparison.Ordinal))[7..])!["include"]!.AsArray())!;

    static void Prepare(RendererPerfWorkflowContractTests.WorkflowFixture fixture, string preset, string leg, decimal? cap = null)
    {
        fixture.Manifest = RendererPerfColdLimitPolicyTests.ManifestJson(preset, leg);
        fixture.Manifest["workloadSha"] = fixture.Workload;
        if (cap is not null) fixture.Manifest["approval"]!["campaignMaximumUsd"] = cap.Value;
        fixture.Environment["LEGS"] = leg;
        fixture.SaveManifest();
        fixture.Git("add", "--", "tools/Renderer.Perf.Tests/authorized/pilot-linux.json");
        fixture.Git("commit", "--amend", "--no-edit");
        fixture.Environment["TESTED_REF"] = fixture.Git("rev-parse", "HEAD").Trim();
    }
}
