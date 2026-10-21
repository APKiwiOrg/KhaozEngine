using System;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoveragePerfWorkflowTests
{
    const string SharedPin = "b177e97c34666048e5ce13c8b6c6e8729ada5c4a";
    static string Gpu => CoveragePerfDiscoveryTests.Read(".github/workflows/gpu-hosted-bench.yml");
    static string Stress => CoveragePerfDiscoveryTests.Read(".github/workflows/stress-test.yml");

    [Theory]
    [InlineData("stress-test.yml")]
    [InlineData("gpu-hosted-bench.yml")]
    public void Measurement_is_dispatch_only_and_forwards_all_three_identity_inputs(string file)
    {
        string text = CoveragePerfDiscoveryTests.Read(".github/workflows/" + file);
        string events = CoveragePerfWorkflowText.Events(text);
        Assert.StartsWith("  workflow_dispatch:\n", events, StringComparison.Ordinal);
        foreach (string trigger in new[] { "\n  push:", "\n  pull_request:", "\n  schedule:", "\n  workflow_call:" })
            Assert.DoesNotContain(trigger, events, StringComparison.Ordinal);
        foreach (string input in new[] { "unit", "manifest-sha256", "workload-sha" })
        {
            Assert.Contains("      coverage-perf-" + input + ":", events, StringComparison.Ordinal);
            Assert.Contains("${{ inputs.coverage-perf-" + input + " }}", text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Structural_helpers_read_lf_and_crlf_workflows_alike(string newline)
    {
        string[] lines = { "name: sample", "on:", "  workflow_dispatch:", "    inputs:", "      coverage-perf-unit:", "permissions:",
            "  contents: read", "jobs:", "  bench:", "    steps:", "      - name: First", "        run: |", "          echo one",
            "      - name: Second", "        if: inputs.coverage-perf-unit != ''", "        run: echo two", "" };
        string workflow = string.Join(newline, lines);
        Assert.Equal(string.Join("\n", lines), CoveragePerfWorkflowText.Lf(workflow));
        Assert.Equal("  workflow_dispatch:\n    inputs:\n      coverage-perf-unit:", CoveragePerfWorkflowText.Events(workflow));
        Assert.Equal("name: sample\n", CoveragePerfWorkflowText.Before(workflow, "on:\n"));
        Assert.Equal("\n        run: |\n          echo one", CoveragePerfWorkflowText.Step(workflow, "First"));
        Assert.Equal("\n        if: inputs.coverage-perf-unit != ''\n        run: echo two\n", CoveragePerfWorkflowText.Step(workflow, "Second"));
        Assert.Equal("  bench:\n", CoveragePerfWorkflowText.Before(CoveragePerfWorkflowText.After(workflow, "jobs:\n"), "    steps:"));
        string[] steps = CoveragePerfWorkflowText.Steps(workflow);
        Assert.Equal(3, steps.Length);
        Assert.StartsWith("name: Second\n        if: inputs.coverage-perf-unit != ''\n", steps[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Stress_preserves_the_existing_contract_and_pins_the_reviewed_shared_workflow()
    {
        Assert.Contains("stress-test.yml@" + SharedPin + " #", Stress, StringComparison.Ordinal);
        Assert.DoesNotContain("a0f13d51e", Stress, StringComparison.Ordinal);
        Assert.DoesNotContain("b44c61e03", Stress, StringComparison.Ordinal);
        // GitHub refuses an empty value for a required dispatch input, and coverage mode needs an empty filter.
        Assert.Contains("      filter:\n        description: dotnet test --filter expression selecting the tests to repeat, "
            + "empty only for coverage-perf-cpu\n        required: false\n        default: ''\n", Stress, StringComparison.Ordinal);
        foreach (string input in new[] { "project", "filter", "iterations", "load", "configuration", "support-cost", "build-target", "artifact-env",
            "coverage-perf-cpu", "coverage-perf-unit", "coverage-perf-manifest-sha256", "coverage-perf-workload-sha", "coverage-perf-preset" })
            Assert.Contains($"      {input}: ${{{{ inputs.{input} }}}}", Stress, StringComparison.Ordinal);
        Assert.Contains("packages-token: ${{ secrets.GITHUB_TOKEN }}", Stress, StringComparison.Ordinal);
        Assert.Contains("coverage-perf-cpu:\n", Stress, StringComparison.Ordinal);
    }

    [Fact]
    public void Gpu_refuses_bad_selection_before_allocating_a_paid_runner()
    {
        string pick = CoveragePerfWorkflowText.Before(Gpu, "  bench:\n");
        foreach (string validation in new[] { "^[a-z0-9-]{1,64}$", "^[0-9a-f]{64}$", "^[0-9a-f]{40}$",
            "windows-d3d11|windows-vulkan|linux-vulkan", "FullyQualifiedName~GpuCapabilitiesDiagTests", "::error::" })
            Assert.Contains(validation, pick, StringComparison.Ordinal);
        Assert.Contains("COVERAGE_PERF_UNIT", pick, StringComparison.Ordinal);
        Assert.Contains("FILTER", pick, StringComparison.Ordinal);
        Assert.DoesNotContain("${{", CoveragePerfWorkflowText.After(pick, "        run: |"), StringComparison.Ordinal);
    }

    [Fact]
    public void Gpu_refuses_re_runs_in_the_pick_job_only_for_measurement()
    {
        string pick = CoveragePerfWorkflowText.Before(Gpu, "  bench:\n");
        Assert.Contains("          WORKFLOW_RUN_ATTEMPT: ${{ github.run_attempt }}", pick, StringComparison.Ordinal);
        string measurement = CoveragePerfWorkflowText.Before(
            CoveragePerfWorkflowText.After(pick, "          if [ -n \"$COVERAGE_PERF_UNIT\" ]; then\n"), "          elif ");
        Assert.Contains("if [ \"$WORKFLOW_RUN_ATTEMPT\" != '1' ]; then\n" +
            "              echo \"::error::coverage performance requires github.run_attempt 1, a re-run cannot reuse an approval\"; exit 1\n" +
            "            fi", measurement, StringComparison.Ordinal);
        string legacy = CoveragePerfWorkflowText.After(pick, "          elif ");
        Assert.DoesNotContain("WORKFLOW_RUN_ATTEMPT", legacy, StringComparison.Ordinal);
        Assert.Contains("\n    needs: legs\n", CoveragePerfWorkflowText.After(Gpu, "  bench:"), StringComparison.Ordinal);
        // Re-running only failed jobs skips the pick job, so the bench job itself must refuse before a runner.
        Assert.Contains("\n    if: inputs.coverage-perf-unit == '' || github.run_attempt == 1\n",
            CoveragePerfWorkflowText.Before(CoveragePerfWorkflowText.After(Gpu, "  bench:"), "    steps:\n"),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Step_chunks_end_before_an_unnamed_step(string newline)
    {
        string workflow = string.Join(newline, "    steps:", "      - name: Verify coverage authorization commit",
            "        if: inputs.coverage-perf-unit != ''", "        run: echo verified",
            "      - uses: actions/setup-dotnet@pinned", "        with:", "          dotnet-version: 10.0.x", "");
        string[] steps = CoveragePerfWorkflowText.Steps(workflow);
        Assert.Equal(3, steps.Length);
        Assert.Equal("name: Verify coverage authorization commit\n        if: inputs.coverage-perf-unit != ''\n        run: echo verified", steps[1]);
        Assert.Equal("uses: actions/setup-dotnet@pinned\n        with:\n          dotnet-version: 10.0.x\n", steps[2]);
    }

    [Fact]
    public void Gpu_keeps_legacy_steps_and_checks_native_identity_before_measurement()
    {
        string text = Gpu;
        int measure = text.IndexOf("      - name: Measure authorized coverage unit", StringComparison.Ordinal);
        Assert.True(measure > 0);
        foreach (string step in new[] { "Verify coverage authorization commit", "GPU, driver model and the GRID driver (Windows)",
            "Load only the NVIDIA Vulkan driver (Linux)", "Confirm the NVIDIA adapter (Windows)", "Confirm the NVIDIA adapter (Linux)",
            "Record coverage native identity" })
        {
            int position = text.IndexOf("      - name: " + step, StringComparison.Ordinal);
            Assert.InRange(position, 0, measure - 1);
        }
        Assert.Contains("Start-Process -FilePath $exe", text, StringComparison.Ordinal);
        Assert.Contains("VK_DRIVER_FILES=$icd", text, StringComparison.Ordinal);
        foreach (string os in new[] { "Windows", "Linux" })
        {
            string legacy = CoveragePerfWorkflowText.Step(text, "Test (" + os + ")");
            Assert.Contains("inputs.coverage-perf-unit == ''", legacy, StringComparison.Ordinal);
            Assert.Contains("dotnet test KhaozEngine.Render.Tests/KhaozEngine.Render.Tests.csproj", legacy, StringComparison.Ordinal);
            Assert.Contains(os == "Windows" ? "$env:FILTER" : "$FILTER", legacy, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Gpu_measurement_checks_out_the_authorization_commit_and_its_single_workload_parent()
    {
        string text = Gpu;
        string checkout = CoveragePerfWorkflowText.Before(CoveragePerfWorkflowText.After(text, "      - uses: actions/checkout@"), "\n      - ");
        Assert.Contains("ref: ${{ inputs.ref }}", checkout, StringComparison.Ordinal);
        Assert.Contains("fetch-depth: ${{ inputs.coverage-perf-unit != '' && 2 || 1 }}", checkout, StringComparison.Ordinal);
        string verify = CoveragePerfWorkflowText.Step(text, "Verify coverage authorization commit");
        Assert.Contains("if: inputs.coverage-perf-unit != ''", verify, StringComparison.Ordinal);
        foreach (string fact in new[] { "git rev-parse HEAD", "git rev-list --parents -n 1 HEAD", "git diff --name-status --no-renames",
            "$env:KE_COVERAGE_PERF_WORKLOAD_SHA", "tools/EquipmentCoverage.Perf.Tests/authorized/$($env:KE_COVERAGE_PERF_UNIT).json",
            "workload-sha.txt", "authorization-sha.txt", "$parents.Count -ne 2", "$changes.Count -ne 1" })
            Assert.Contains(fact, verify, StringComparison.Ordinal);
        Assert.DoesNotContain("Checked-out workload SHA differs", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Gpu_measurement_binds_checkout_fixed_filter_and_finalized_evidence()
    {
        string text = Gpu;
        Assert.Contains("timeout-minutes: ${{ inputs.coverage-perf-unit != '' && 30 || 60 }}", text, StringComparison.Ordinal);
        Assert.Contains(CoveragePerfDiscoveryTests.Filter, text, StringComparison.Ordinal);
        Assert.Contains("KE_COVERAGE_PERF_REPORTS:", text, StringComparison.Ordinal);
        foreach (string fact in new[] { "nvidia-smi", "device-report.log", "workload-sha.txt", "notExecuted", "executed", "passed", "failed",
            "khaozengine.coverage-perf-report/1", "unitId", "returnedByRun", "finalUnitPublished", "authorizationCommitSha", ".json", ".md", ".trx" })
            Assert.Contains(fact, text, StringComparison.Ordinal);
        string upload = CoveragePerfWorkflowText.After(text, "      - name: Upload the output");
        Assert.Contains("if: always()", upload, StringComparison.Ordinal);
        Assert.Contains("${{ runner.temp }}/coverage-perf", upload, StringComparison.Ordinal);
    }
}
