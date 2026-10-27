using System;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("GpuBackendProvidersSerial")]
public sealed class RendererPerfRehearsalControlsWorkflowTests
{
    static RendererPerfWorkflowContractTests.WorkflowFixture Fixture(string leg)
    {
        var fixture = new RendererPerfWorkflowContractTests.WorkflowFixture();
        fixture.Manifest = RendererPerfRehearsalControlsTests.ManifestJson(leg);
        fixture.Manifest["workloadSha"] = fixture.Workload;
        fixture.Environment["LEGS"] = leg;
        fixture.SaveManifest();
        fixture.Git("add", "--", "tools/Renderer.Perf.Tests/authorized/pilot-linux.json");
        fixture.Git("commit", "--amend", "--no-edit");
        fixture.Environment["TESTED_REF"] = fixture.Git("rev-parse", "HEAD").Trim();
        return fixture;
    }

    [Theory]
    [InlineData("linux-vulkan")]
    [InlineData("windows-d3d11")]
    public void Controls_selector_and_verifier_share_the_existing_software_job_and_certification(string leg)
    {
        using var fixture = Fixture(leg);
        fixture.Accept("Authorize", "authorizationSha");
        var result = fixture.Run("Select");
        Assert.True(result.Code == 0, result.Output);
        string line = result.Output.Split('\n').Single(s => s.StartsWith("matrix=", StringComparison.Ordinal));
        var row = Assert.Single(JsonNode.Parse(line[7..])!["include"]!.AsArray())!;
        Assert.Equal(leg == "linux-vulkan" ? "ubuntu-24.04" : "windows-2025-vs2026", row["runner"]!.GetValue<string>());
        Assert.Equal(120, row["jobMinutes"]!.GetValue<int>());
        Assert.Equal("rehearsal", row["executionClass"]!.GetValue<string>());
        Assert.Equal(leg == "linux-vulkan" ? "vulkan-native" : "direct3d11-native", row["backend"]!.GetValue<string>());
        Assert.Equal(leg == "linux-vulkan" ? "llvmpipe" : "", row["vulkanDevice"]!.GetValue<string>());
        Assert.Equal(leg == "windows-d3d11" ? "warp" : "", row["d3dAdapter"]!.GetValue<string>());
        fixture.WriteEvidence(source: RendererPerfRehearsalControlsTests.Report(leg));
        fixture.Accept("VerifyRehearsal", "correctness evidence accepted");
        Assert.NotEqual(0, fixture.Run("Verify").Code);
        fixture.Report["identity"]!["softwareAdapter"] = false;
        fixture.SaveReport();
        Assert.NotEqual(0, fixture.Run("VerifyRehearsal").Code);
    }

    [Theory]
    [InlineData("linux-vulkan")]
    [InlineData("windows-d3d11")]
    public void Controls_selector_refuses_paid_labels_foreign_software_and_full_limits(string leg)
    {
        using var fixture = Fixture(leg);
        var original = fixture.Manifest.DeepClone().AsObject();
        foreach ((string section, string key, JsonNode? value) in new (string, string, JsonNode?)[]
        {
            ("execution", "runner", JsonValue.Create("gpu-t4-linux")),
            ("execution", "runner", JsonValue.Create("gpu-t4-windows")),
            ("execution", "adapterPolicy", JsonValue.Create("nvidia-t4-hardware")),
            ("execution", "adapterPolicy", JsonValue.Create(leg == "linux-vulkan" ? "microsoft-warp-software" : "mesa-lavapipe-software")),
            ("execution", "leg", JsonValue.Create("windows-vulkan")),
            ("limits", "unitSeconds", JsonValue.Create(19800)),
            ("limits", "jobMinutes", JsonValue.Create(350)),
            ("budget", "maximumCostUsd", JsonValue.Create(1)),
            ("preset", "", JsonValue.Create("full"))
        })
        {
            fixture.Manifest = original.DeepClone().AsObject();
            if (section == "preset") fixture.Manifest[section] = value;
            else fixture.Manifest[section]![key] = value;
            fixture.SaveManifest();
            Assert.NotEqual(0, fixture.Run("Select").Code);
        }
    }

    [Fact]
    public void Controls_reuses_the_fixed_fact_shared_job_cap_and_separate_correctness_verifier()
    {
        string job = RendererPerfWorkflowText.Read().Job("renderer-bench");
        Assert.Contains("timeout-minutes: ${{ matrix.jobMinutes }}", job, StringComparison.Ordinal);
        Assert.Contains("matrix.executionClass == 'rehearsal'", job, StringComparison.Ordinal);
        Assert.Contains("-Mode VerifyRehearsal", job, StringComparison.Ordinal);
        Assert.Contains("FullyQualifiedName=Renderer.Perf.Tests.RendererPerfTests.Measure_authorized_unit&Category=RendererPerformance", job, StringComparison.Ordinal);
        Assert.Contains("matrix.executionClass != 'rehearsal'", job, StringComparison.Ordinal);
    }
}
