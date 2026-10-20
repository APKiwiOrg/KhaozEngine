using System;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoveragePerfWorkflowTests
{
    static string Gpu => CoveragePerfDiscoveryTests.Read(".github/workflows/gpu-hosted-bench.yml");
    static string Stress => CoveragePerfDiscoveryTests.Read(".github/workflows/stress-test.yml");

    [Theory]
    [InlineData("stress-test.yml")]
    [InlineData("gpu-hosted-bench.yml")]
    public void Measurement_is_dispatch_only_and_forwards_all_three_identity_inputs(string file)
    {
        string text = CoveragePerfDiscoveryTests.Read(".github/workflows/" + file);
        string events = text.Split("on:\n", 2)[1].Split("\npermissions:", 2)[0];
        Assert.StartsWith("  workflow_dispatch:\n", events, StringComparison.Ordinal);
        foreach (string trigger in new[] { "\n  push:", "\n  pull_request:", "\n  schedule:", "\n  workflow_call:" })
            Assert.DoesNotContain(trigger, events, StringComparison.Ordinal);
        foreach (string input in new[] { "unit", "manifest-sha256", "workload-sha" })
        {
            Assert.Contains("      coverage-perf-" + input + ":", events, StringComparison.Ordinal);
            Assert.Contains("${{ inputs.coverage-perf-" + input + " }}", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Stress_preserves_the_existing_contract_and_pins_the_reviewed_shared_workflow()
    {
        Assert.Contains("stress-test.yml@a0f13d51ec0c4ba0ceaa65caf86b2875fca35004 #", Stress, StringComparison.Ordinal);
        foreach (string input in new[] { "project", "filter", "iterations", "load", "configuration", "support-cost", "build-target", "artifact-env",
            "coverage-perf-cpu", "coverage-perf-unit", "coverage-perf-manifest-sha256", "coverage-perf-workload-sha", "coverage-perf-preset" })
            Assert.Contains($"      {input}: ${{{{ inputs.{input} }}}}", Stress, StringComparison.Ordinal);
        Assert.Contains("packages-token: ${{ secrets.GITHUB_TOKEN }}", Stress, StringComparison.Ordinal);
        Assert.Contains("coverage-perf-cpu:\n", Stress, StringComparison.Ordinal);
    }

    [Fact]
    public void Gpu_refuses_bad_selection_before_allocating_a_paid_runner()
    {
        string pick = Gpu.Split("  bench:\n", 2)[0];
        foreach (string validation in new[] { "^[a-z0-9-]{1,64}$", "^[0-9a-f]{64}$", "^[0-9a-f]{40}$",
            "windows-d3d11|windows-vulkan|linux-vulkan", "FullyQualifiedName~GpuCapabilitiesDiagTests", "::error::" })
            Assert.Contains(validation, pick, StringComparison.Ordinal);
        Assert.Contains("COVERAGE_PERF_UNIT", pick, StringComparison.Ordinal);
        Assert.Contains("FILTER", pick, StringComparison.Ordinal);
        Assert.DoesNotContain("${{ inputs.", pick.Split("        run: |", 2)[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Gpu_keeps_legacy_steps_and_checks_native_identity_before_measurement()
    {
        string text = Gpu;
        int measure = text.IndexOf("      - name: Measure authorized coverage unit", StringComparison.Ordinal);
        Assert.True(measure > 0);
        foreach (string step in new[] { "GPU, driver model and the GRID driver (Windows)", "Load only the NVIDIA Vulkan driver (Linux)",
            "Confirm the NVIDIA adapter (Windows)", "Confirm the NVIDIA adapter (Linux)", "Record coverage native identity" })
        {
            int position = text.IndexOf("      - name: " + step, StringComparison.Ordinal);
            Assert.InRange(position, 0, measure - 1);
        }
        Assert.Contains("Start-Process -FilePath $exe", text, StringComparison.Ordinal);
        Assert.Contains("VK_DRIVER_FILES=$icd", text, StringComparison.Ordinal);
        foreach (string os in new[] { "Windows", "Linux" })
        {
            string legacy = text.Split("      - name: Test (" + os + ")", 2)[1].Split("      - name:", 2)[0];
            Assert.Contains("inputs.coverage-perf-unit == ''", legacy, StringComparison.Ordinal);
            Assert.Contains("dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj", legacy, StringComparison.Ordinal);
            Assert.Contains(os == "Windows" ? "$env:FILTER" : "$FILTER", legacy, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Gpu_measurement_binds_checkout_fixed_filter_and_finalized_evidence()
    {
        string text = Gpu;
        Assert.Contains("timeout-minutes: ${{ inputs.coverage-perf-unit != '' && 30 || 60 }}", text, StringComparison.Ordinal);
        Assert.Contains(CoveragePerfDiscoveryTests.Filter, text, StringComparison.Ordinal);
        Assert.Contains("git rev-parse HEAD", text, StringComparison.Ordinal);
        Assert.Contains("KE_COVERAGE_PERF_REPORTS:", text, StringComparison.Ordinal);
        foreach (string fact in new[] { "nvidia-smi", "device-report.log", "workload-sha.txt", "notExecuted", "executed", "passed", "failed",
            "khaozengine.coverage-perf-report/1", "unitId", "returnedByRun", "finalUnitPublished", ".json", ".md", ".trx" })
            Assert.Contains(fact, text, StringComparison.Ordinal);
        string upload = text.Split("      - name: Upload the output", 2)[1];
        Assert.Contains("if: always()", upload, StringComparison.Ordinal);
        Assert.Contains("${{ runner.temp }}/coverage-perf", upload, StringComparison.Ordinal);
    }
}
