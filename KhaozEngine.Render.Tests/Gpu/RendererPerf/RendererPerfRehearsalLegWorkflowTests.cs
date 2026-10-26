using System;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("GpuBackendProvidersSerial")]
public sealed class RendererPerfRehearsalLegWorkflowTests
{
    static RendererPerfWorkflowContractTests.WorkflowFixture Fixture(string preset, string leg)
    {
        var fixture = new RendererPerfWorkflowContractTests.WorkflowFixture();
        fixture.Manifest = RendererPerfRehearsalClassesTests.ManifestJson(preset, leg);
        fixture.Manifest["workloadSha"] = fixture.Workload;
        fixture.Environment["LEGS"] = leg;
        fixture.SaveManifest();
        fixture.Git("add", "--", "tools/Renderer.Perf.Tests/authorized/pilot-linux.json");
        fixture.Git("commit", "--amend", "--no-edit");
        fixture.Environment["TESTED_REF"] = fixture.Git("rev-parse", "HEAD").Trim();
        return fixture;
    }

    [Theory]
    [MemberData(nameof(RendererPerfRehearsalClassesTests.Classes), MemberType = typeof(RendererPerfRehearsalClassesTests))]
    public void Trusted_selector_binds_each_class_to_its_standard_runner_and_cap(string preset, string leg)
    {
        using var fixture = Fixture(preset, leg);
        fixture.Accept("Authorize", "authorizationSha");
        var result = fixture.Run("Select");
        Assert.True(result.Code == 0, result.Output);
        var line = result.Output.Split('\n').Single(s => s.StartsWith("matrix=", StringComparison.Ordinal));
        var row = Assert.Single(JsonNode.Parse(line[7..])!["include"]!.AsArray())!;
        Assert.Equal(leg == "linux-vulkan" ? "ubuntu-24.04" : "windows-2025-vs2026", row["runner"]!.GetValue<string>());
        Assert.Equal("rehearsal", row["executionClass"]!.GetValue<string>());
        Assert.Equal(leg == "linux-vulkan" ? "vulkan-native" : "direct3d11-native", row["backend"]!.GetValue<string>());
        Assert.Equal(leg == "linux-vulkan" ? "llvmpipe" : "", row["vulkanDevice"]!.GetValue<string>());
        Assert.Equal(leg == "windows-d3d11" ? "warp" : "", row["d3dAdapter"]!.GetValue<string>());
        Assert.Equal(preset == "rehearsal-full" ? 350 : 120, row["jobMinutes"]!.GetValue<int>());
    }

    [Theory]
    [MemberData(nameof(RendererPerfRehearsalClassesTests.Classes), MemberType = typeof(RendererPerfRehearsalClassesTests))]
    public void Trusted_selector_refuses_cross_leg_and_schedule_values(string preset, string leg)
    {
        using var fixture = Fixture(preset, leg);
        var original = fixture.Manifest.DeepClone().AsObject();
        foreach (string runner in new[] { "gpu-t4-linux", "gpu-t4-windows", "windows-latest", leg == "linux-vulkan" ? "windows-2025-vs2026" : "ubuntu-24.04" })
        {
            fixture.Manifest = original.DeepClone().AsObject();
            fixture.Manifest["execution"]!["runner"] = runner;
            fixture.SaveManifest();
            Assert.NotEqual(0, fixture.Run("Select").Code);
            Assert.NotEqual(0, fixture.Run("Authorize").Code);
        }
        fixture.Manifest = original.DeepClone().AsObject();
        fixture.Manifest["execution"]!["adapterPolicy"] = leg == "linux-vulkan" ? "microsoft-warp-software" : "mesa-lavapipe-software";
        fixture.SaveManifest();
        Assert.NotEqual(0, fixture.Run("Select").Code);
        fixture.Manifest = original.DeepClone().AsObject();
        fixture.Manifest["limits"]!["unitSeconds"] = preset == "rehearsal-full" ? 5400 : 19800;
        fixture.SaveManifest();
        Assert.NotEqual(0, fixture.Run("Select").Code);
        fixture.Manifest = original.DeepClone().AsObject();
        fixture.Manifest["limits"]!["jobMinutes"] = preset == "rehearsal-full" ? 120 : 350;
        fixture.SaveManifest();
        Assert.NotEqual(0, fixture.Run("Select").Code);
    }

    [Theory]
    [MemberData(nameof(RendererPerfRehearsalClassesTests.Classes), MemberType = typeof(RendererPerfRehearsalClassesTests))]
    public void Trusted_rehearsal_verifier_accepts_software_and_refuses_hardware_or_paid_verification(string preset, string leg)
    {
        using var fixture = Fixture(preset, leg);
        fixture.WriteEvidence(source: RendererPerfRehearsalLegExecutionTests.Report(preset, leg));
        fixture.Accept("VerifyRehearsal", "correctness evidence accepted");
        Assert.NotEqual(0, fixture.Run("Verify").Code);
        fixture.Report["identity"]!["softwareAdapter"] = false;
        fixture.SaveReport();
        Assert.NotEqual(0, fixture.Run("VerifyRehearsal").Code);
    }

    [Fact]
    public void Windows_rehearsal_omits_GRID_and_keeps_the_pinned_SDK_and_fixed_fact()
    {
        string job = RendererPerfWorkflowText.Read().Job("renderer-bench");
        string Step(string name) => job.Split("      - ").Single(s => s.StartsWith("name: " + name + "\n", StringComparison.Ordinal));
        Assert.Contains("runner.os == 'Windows' && matrix.executionClass != 'rehearsal'", Step("GPU, driver model and the GRID driver (Windows)"), StringComparison.Ordinal);
        Assert.Contains("dotnet-sdk-10.0.402-win-x64.zip", Step("Install isolated renderer SDK (Windows)"), StringComparison.Ordinal);
        Assert.Contains("KE_D3D11_ADAPTER: ${{ matrix.d3dAdapter }}", job, StringComparison.Ordinal);
        Assert.Contains("FullyQualifiedName=Renderer.Perf.Tests.RendererPerfTests.Measure_authorized_unit&Category=RendererPerformance", job, StringComparison.Ordinal);
        Assert.DoesNotContain("KE_D3D11_DEBUG_LAYER:", job, StringComparison.Ordinal);
        Assert.DoesNotContain("KE_VULKAN_VALIDATION:", job, StringComparison.Ordinal);
    }
}
