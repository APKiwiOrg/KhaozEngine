using System.Linq;
using static KhaozEngine.Tests.Gpu.RendererPerf.RendererPerfManifest;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal enum RendererPerfExecutionClass { Performance, Rehearsal }
internal enum RendererPerfRehearsalSchedule { Pilot, Full, Controls }
internal sealed record RendererPerfRehearsalClass(string Preset, RendererPerfRehearsalSchedule Schedule,
    RendererPerfExecutionPolicy Execution, RendererPerfLimits Limits);

// Correctness-only policy. Performance presets retain their independent hardware and cost checks.
internal static class RendererPerfRehearsalPolicy
{
    internal const string Reason = "SoftwareRehearsalNotPerformanceEvidence";

    static readonly RendererPerfExecutionPolicy Linux = new("vulkan", "mesa-lavapipe-software",
        "ubuntu-24.04", "gpu-hosted-bench.yml", "linux-vulkan", "retained-fence-batch-3/1");
    static readonly RendererPerfExecutionPolicy Windows = new("d3d11", "microsoft-warp-software",
        "windows-2025-vs2026", "gpu-hosted-bench.yml", "windows-d3d11", "retained-fence-batch-3/1");
    static readonly RendererPerfLimits PilotLimits = new(5400, 60000, 120000, 120);
    static readonly RendererPerfLimits FullLimits = new(19800, 60000, 120000, 350);
    static readonly RendererPerfLimits ControlsLimits = new(5400, 60000, 120000, 120);
    static readonly RendererPerfRehearsalClass[] Classes =
    [
        new("rehearsal", RendererPerfRehearsalSchedule.Pilot, Linux, PilotLimits),
        new("rehearsal", RendererPerfRehearsalSchedule.Pilot, Windows, PilotLimits),
        new("rehearsal-full", RendererPerfRehearsalSchedule.Full, Linux, FullLimits),
        new("rehearsal-full", RendererPerfRehearsalSchedule.Full, Windows, FullLimits),
        new("rehearsal-controls", RendererPerfRehearsalSchedule.Controls, Linux, ControlsLimits),
        new("rehearsal-controls", RendererPerfRehearsalSchedule.Controls, Windows, ControlsLimits)
    ];

    internal static RendererPerfRehearsalClass Class(RendererPerfManifest manifest)
        => Classes.SingleOrDefault(c => c.Preset == manifest.Preset && c.Execution.Leg == manifest.Execution.Leg)
            ?? throw new RendererPerfProtocolException("Preset", "No software rehearsal class admits this preset and leg.");

    internal static void Validate(RendererPerfManifest manifest)
    {
        var policy = Class(manifest);
        Require(manifest.Execution == policy.Execution,
            "Preset", "Rehearsal backend, software adapter, pinned standard runner or completion policy differs from its class.");
        Require(manifest.Limits == policy.Limits,
            "Preset", "Rehearsal unit, steady frame, cold frame or job deadline differs from the preset.");
        Require(manifest.Budget == new RendererPerfBudget(policy.Limits.JobMinutes, 0, "USD", 0),
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
                .All(RendererPerfReportJson.Promotable)
            && report.Controls.All(c => RendererPerfControlTiming.Promotable(c, report.Identity.TimestampPeriodNs))
            && report.Conditioning.Length + report.Blocks.Sum(b => b.Warmup.Length + b.Measured.Length)
                + report.Controls.Sum(c => c.Warmup.Length + c.Measured.Length) == report.Manifest.TotalFrameCount;

}
