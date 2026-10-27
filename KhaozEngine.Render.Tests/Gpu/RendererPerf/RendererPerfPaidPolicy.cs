using static KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfManifest;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal static class RendererPerfPaidPolicy
{
    // Run 40435928569 lasted 14m17s after a 12-minute cap. Round its 2m17s overrun up.
    internal const int CleanupAllowanceMinutes = 3;

    internal static void Validate(RendererPerfManifest manifest)
    {
        var execution = manifest.Execution;
        bool linux = execution.Leg == "linux-vulkan", pilot = manifest.Preset == "pilot";
        Require(execution.Backend == (execution.Leg == "windows-d3d11" ? "d3d11" : "vulkan")
            && execution.Runner == (linux ? "gpu-t4-linux" : "gpu-t4-windows")
            && execution.AdapterPolicy == "nvidia-t4-hardware" && execution.Workflow == "gpu-hosted-bench.yml"
            && execution.CompletionPolicy == "retained-fence-batch-3/1", "Preset", "Backend, hardware, runner, workflow or completion policy differs from the preset.");
        int minutes = pilot ? (linux ? 12 : 20) : 35;
        Require(manifest.Limits == new RendererPerfLimits(pilot ? 600 : 1500, 10000, 60000, minutes),
            "Preset", "Unit, steady frame, cold frame or job deadline differs from the preset.");
        int billedMinutes = minutes + CleanupAllowanceMinutes;
        decimal rate = linux ? 0.052m : 0.102m;
        Require(manifest.Budget == new RendererPerfBudget(billedMinutes, rate, "USD", billedMinutes * rate),
            "Preset", "Billed minutes, rate, currency or cost cap differs from the preset.");
        Require(Text(manifest.Approval.Reference) && Text(manifest.Approval.Purpose) && Text(manifest.Approval.CampaignReference)
            && manifest.Approval.CampaignMaximumUsd >= manifest.Budget.MaximumCostUsd
            && manifest.Approval.CampaignMaximumUsd <= 12m, "Approval",
            "Exact owner approval and campaign references with a sufficient cap of at most USD 12 are required. A manifest does not grant consent.");
    }
}
