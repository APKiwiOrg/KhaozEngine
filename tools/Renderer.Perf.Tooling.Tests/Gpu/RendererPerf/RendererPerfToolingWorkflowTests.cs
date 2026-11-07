using System;
using System.IO;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfToolingWorkflowTests
{
    [Fact]
    public void Paid_matrix_requires_successful_free_validation_of_both_immutable_implementations()
    {
        string workflow = File.ReadAllText(RendererPerfRepositoryPaths.File(".github/workflows/gpu-hosted-bench.yml"))
            .ReplaceLineEndings("\n");
        string paid = workflow.Split("  renderer-bench:\n", 2)[1];
        string header = paid.Split("    steps:\n", 2)[0];
        Assert.Contains("needs: [legs, renderer-tooling-validation]", header, StringComparison.Ordinal);
        Assert.Contains("if: inputs.renderer-perf-unit != '' && github.run_attempt == 1", header, StringComparison.Ordinal);
        Assert.DoesNotContain("always()", header, StringComparison.Ordinal);
        string gate = workflow.Split("  renderer-tooling-validation:\n", 2)[1].Split("  renderer-bench:\n", 2)[0];
        Assert.Contains("needs: legs", gate, StringComparison.Ordinal);
        Assert.Contains("uses: ./.github/workflows/tooling-validation.yml", gate, StringComparison.Ordinal);
        Assert.Contains("ref: ${{ needs.legs.outputs.authorization-sha }}", gate, StringComparison.Ordinal);
        Assert.Contains("trusted-ref: ${{ github.workflow_sha }}", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error", gate, StringComparison.Ordinal);
        string ordinary = workflow.Split("  bench:\n", 2)[1].Split("  renderer-tooling-validation:\n", 2)[0];
        Assert.Contains("needs: legs", ordinary, StringComparison.Ordinal);
        Assert.DoesNotContain("needs: [legs,", ordinary, StringComparison.Ordinal);
    }

    [Fact]
    public void Free_tooling_validation_cannot_execute_the_authorized_measurement_fact()
    {
        string workflow = File.ReadAllText(RendererPerfRepositoryPaths.File(".github/workflows/tooling-validation.yml"));
        Assert.DoesNotContain("dotnet test tools/Renderer.Perf.Tests/", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("self-hosted", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("gpu-t4-", workflow, StringComparison.Ordinal);
        Assert.Contains("Remove-Item Env:KE_GPU_TESTS, Env:KE_RENDERER_PERF_UNIT", workflow, StringComparison.Ordinal);
        Assert.Contains("RendererPerfWaterOpticsRecipeTests.Fft_surface_stays_within_its_bound", workflow, StringComparison.Ordinal);
        Assert.Contains("notExecuted -ne '0'", workflow, StringComparison.Ordinal);
        Assert.Contains("persist-credentials: false", workflow, StringComparison.Ordinal);
    }
}
