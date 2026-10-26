using System;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("GpuBackendProvidersSerial")]
public sealed class RendererPerfRehearsalWorkflowTests
{
    internal static RendererPerfWorkflowContractTests.WorkflowFixture Fixture(bool rehearsal = true)
    {
        var fixture = new RendererPerfWorkflowContractTests.WorkflowFixture();
        if (rehearsal)
        {
            fixture.Manifest = RendererPerfRehearsalManifestTests.ManifestJson();
            fixture.Manifest["workloadSha"] = fixture.Workload;
            fixture.SaveManifest();
            fixture.Git("add", "--", "tools/Renderer.Perf.Tests/authorized/pilot-linux.json");
            fixture.Git("commit", "--amend", "--no-edit");
            fixture.Environment["TESTED_REF"] = fixture.Git("rev-parse", "HEAD").Trim();
        }
        return fixture;
    }

    [Fact]
    public void Trusted_selector_and_authorization_admit_only_the_free_rehearsal_mapping()
    {
        using var fixture = Fixture();
        fixture.Accept("Authorize", "authorizationSha");
        var result = fixture.Run("Select");
        Assert.True(result.Code == 0, result.Output);
        string line = result.Output.Split('\n').Single(s => s.StartsWith("matrix=", StringComparison.Ordinal));
        var row = Assert.Single(JsonNode.Parse(line[7..])!["include"]!.AsArray())!;
        Assert.Equal("ubuntu-24.04", row["runner"]!.GetValue<string>());
        Assert.Equal("rehearsal", row["executionClass"]!.GetValue<string>());
        Assert.Equal("llvmpipe", row["vulkanDevice"]!.GetValue<string>());
        Assert.Equal("", row["d3dAdapter"]!.GetValue<string>());
        Assert.Equal(120, row["jobMinutes"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(true, "runner", "gpu-t4-linux")]
    [InlineData(true, "runner", "gpu-t4-windows")]
    [InlineData(true, "adapterPolicy", "nvidia-t4-hardware")]
    [InlineData(true, "backend", "d3d11")]
    [InlineData(true, "leg", "windows-d3d11")]
    [InlineData(true, "leg", "windows-vulkan")]
    [InlineData(false, "runner", "ubuntu-24.04")]
    [InlineData(false, "adapterPolicy", "mesa-lavapipe-software")]
    public void Trusted_allocation_refuses_cross_class_execution(bool rehearsal, string key, string value)
    {
        using var fixture = Fixture(rehearsal);
        fixture.Manifest["execution"]![key] = value;
        fixture.SaveManifest();
        Assert.NotEqual(0, fixture.Run("Select").Code);
        Assert.NotEqual(0, fixture.Run("Authorize").Code);
    }

    [Theory]
    [InlineData("limits", "jobMinutes", 12)]
    [InlineData("limits", "unitSeconds", 600)]
    [InlineData("limits", "frameMilliseconds", 10000)]
    [InlineData("budget", "ratePerMinute", 1)]
    [InlineData("budget", "maximumCostUsd", 1)]
    public void Trusted_rehearsal_refuses_paid_limits_and_nonzero_cost(string section, string key, int value)
    {
        using var fixture = Fixture();
        fixture.Manifest[section]![key] = value;
        fixture.SaveManifest();
        Assert.NotEqual(0, fixture.Run("Select").Code);
    }

    [Fact]
    public void Trusted_verifiers_refuse_the_other_execution_class()
    {
        using var rehearsal = Fixture();
        rehearsal.WriteEvidence(source: RendererPerfRehearsalExecutionTests.Report());
        rehearsal.Accept("VerifyRehearsal", "correctness evidence accepted");
        Assert.NotEqual(0, rehearsal.Run("Verify").Code);
        rehearsal.Report["assessment"]!["validForPromotion"] = true;
        rehearsal.SaveReport();
        Assert.NotEqual(0, rehearsal.Run("VerifyRehearsal").Code);
        using var paid = Fixture(false);
        paid.WriteEvidence();
        paid.Accept("Verify", "evidence accepted");
        Assert.NotEqual(0, paid.Run("VerifyRehearsal").Code);
    }

    [Fact]
    public void Workflow_bootstrap_and_verification_are_class_isolated()
    {
        string job = RendererPerfWorkflowText.Read().Job("renderer-bench");
        string Step(string name) => job.Split("      - ").Single(s => s.StartsWith("name: " + name + "\n", StringComparison.Ordinal));
        string lava = Step("Install renderer rehearsal lavapipe");
        Assert.Contains("matrix.executionClass == 'rehearsal'", lava, StringComparison.Ordinal);
        Assert.Contains("dpkg -L mesa-vulkan-drivers", lava, StringComparison.Ordinal);
        Assert.Contains("/lvp_icd[^/]*\\.json$", lava, StringComparison.Ordinal);
        Assert.Contains("matrix.executionClass != 'rehearsal'", Step("Load only the NVIDIA Vulkan driver (Linux)"), StringComparison.Ordinal);
        Assert.Contains("KE_VULKAN_DEVICE: ${{ matrix.vulkanDevice }}", job, StringComparison.Ordinal);
        Assert.Contains("KE_D3D11_ADAPTER: ${{ matrix.d3dAdapter }}", job, StringComparison.Ordinal);
        Assert.Contains("matrix.executionClass == 'rehearsal'", Step("Verify renderer rehearsal correctness"), StringComparison.Ordinal);
        Assert.Contains("-Mode VerifyRehearsal", job, StringComparison.Ordinal);
        Assert.Contains("if: always()", Step("Upload renderer evidence including partial failures"), StringComparison.Ordinal);
    }
}
