using System.Linq;
using static KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfManifest;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal enum RendererPerfExecutionClass { Performance, Rehearsal }

// Correctness-only policy. Performance presets retain their independent hardware and cost checks.
internal static class RendererPerfRehearsalPolicy
{
    internal const string Reason = "SoftwareRehearsalNotPerformanceEvidence";

    internal static void Validate(RendererPerfManifest manifest)
    {
        Require(manifest.Execution == new RendererPerfExecutionPolicy("vulkan", "mesa-lavapipe-software",
            "ubuntu-24.04", "gpu-hosted-bench.yml", "linux-vulkan", "retained-fence-batch-3/1"),
            "Preset", "Rehearsal requires the standard Ubuntu lavapipe Linux Vulkan leg.");
        Require(manifest.Limits == new RendererPerfLimits(5400, 60000, 120),
            "Preset", "Rehearsal unit, frame or job deadline differs from the preset.");
        Require(manifest.Budget == new RendererPerfBudget(120, 0, "USD", 0),
            "Preset", "Rehearsal requires the standard hosted zero-cost budget.");
        Require(Text(manifest.Approval.Reference) && Text(manifest.Approval.Purpose)
            && Text(manifest.Approval.CampaignReference) && manifest.Approval.CampaignMaximumUsd == 0,
            "Approval", "Rehearsal requires exact owner approval references and a zero-cost campaign. A manifest does not grant consent.");
    }

    internal static void ValidateReport(RendererPerfReport report)
    {
        Require(!report.Assessment.ValidForPromotion && report.Assessment.Reasons.Contains(Reason),
            "Publication", "A software rehearsal always retains its fixed non-promotion reason.");
        if (report.Publication.State == RendererPerfPublicationState.Completed)
        {
            RendererPerfAdmission.Identity(report.Manifest, report.Identity);
            Require(FramesPassed(report), "Publication", "Completed rehearsal requires every frame guard and timestamp.");
        }
    }

    internal static bool FramesPassed(RendererPerfReport report)
        => report.Blocks.All(b => b.Errors.IsEmpty)
            && report.Conditioning.Concat(report.Blocks.SelectMany(b => b.Warmup.Concat(b.Measured)))
                .Concat(report.Controls.SelectMany(c => c.Warmup.Concat(c.Measured))).All(RendererPerfReportJson.Promotable)
            && report.Conditioning.Length + report.Blocks.Sum(b => b.Warmup.Length + b.Measured.Length)
                + report.Controls.Sum(c => c.Warmup.Length + c.Measured.Length) == report.Manifest.TotalFrameCount;

}
