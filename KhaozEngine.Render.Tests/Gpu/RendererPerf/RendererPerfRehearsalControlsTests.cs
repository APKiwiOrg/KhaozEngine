using System;
using System.Linq;
using System.Text.Json.Nodes;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("GpuBackendProvidersSerial")]
public sealed class RendererPerfRehearsalControlsTests
{
    internal static JsonObject ManifestJson(string leg)
    {
        var json = RendererPerfRehearsalClassesTests.ManifestJson("rehearsal-full", leg);
        json["preset"] = "rehearsal-controls";
        json["conditioningFrames"] = 3;
        json["recipes"] = RendererPerfProtocolTests.ManifestJson()["recipes"]!.DeepClone();
        json["blocks"] = new JsonArray();
        json["limits"]!["unitSeconds"] = 5400;
        json["limits"]!["jobMinutes"] = 120;
        json["budget"]!["maximumBilledMinutes"] = 120;
        return json;
    }

    internal static RendererPerfReport Report(string leg)
    {
        var report = RendererPerfRehearsalLegExecutionTests.Report("rehearsal-full", leg);
        report = report with
        {
            Manifest = RendererPerfRehearsalManifestTests.Parse(ManifestJson(leg)),
            Recipes = [.. report.Recipes.Where(r => r.Identity.SceneId == "S3")],
            Blocks = []
        };
        return RendererPerfExecutionEvidenceTests.WithTrace(report);
    }

    [Theory]
    [InlineData("linux-vulkan")]
    [InlineData("windows-d3d11")]
    public void Controls_class_keeps_all_full_control_counts_and_one_short_conditioning_batch(string leg)
    {
        var manifest = RendererPerfRehearsalManifestTests.Parse(ManifestJson(leg));
        Assert.Equal(RendererPerfExecutionClass.Rehearsal, manifest.ExecutionClass);
        Assert.Equal(3, manifest.ConditioningFrames);
        Assert.Equal("S3", Assert.Single(manifest.Recipes).SceneId);
        Assert.Empty(manifest.Blocks);
        Assert.Equal(Enumerable.Range(1, 12), manifest.Controls.Select(c => c.Id));
        Assert.All(manifest.Controls, c => { Assert.Equal(64, c.WarmupFrames); Assert.Equal(128, c.MeasuredFrames); });
        Assert.Equal(2307, manifest.TotalFrameCount);
        Assert.Equal(new RendererPerfLimits(5400, 60000, 120), manifest.Limits);
        Assert.Equal(new RendererPerfBudget(120, 0, "USD", 0), manifest.Budget);
    }

    [Theory]
    [InlineData("linux-vulkan")]
    [InlineData("windows-d3d11")]
    public void Controls_class_refuses_extra_scene_work_schedule_drift_and_paid_or_foreign_mappings(string leg)
    {
        JsonObject original = ManifestJson(leg);
        foreach (string fault in new[] { "conditioning", "recipe", "block", "missing-control", "control-order", "warmup", "measured",
            "paid-runner", "other-runner", "hardware", "other-software", "backend", "leg", "full-unit", "full-job", "paid-frame",
            "paid-billing", "rate", "cost", "currency", "campaign", "pilot", "full", "rehearsal", "rehearsal-full" })
        {
            JsonObject json = original.DeepClone().AsObject();
            switch (fault)
            {
                case "conditioning": json["conditioningFrames"] = 4; break;
                case "recipe": json["recipes"] = new JsonArray(); break;
                case "block": json["blocks"] = RendererPerfProtocolTests.ManifestJson()["blocks"]!.DeepClone(); break;
                case "missing-control": json["controls"]!.AsArray().RemoveAt(11); break;
                case "control-order": json["controls"]!.AsArray().ReverseInPlace(); break;
                case "warmup": json["controls"]![0]!["warmupFrames"] = 32; break;
                case "measured": json["controls"]![0]!["measuredFrames"] = 64; break;
                case "paid-runner": json["execution"]!["runner"] = leg == "linux-vulkan" ? "gpu-t4-linux" : "gpu-t4-windows"; break;
                case "other-runner": json["execution"]!["runner"] = leg == "linux-vulkan" ? "windows-2025-vs2026" : "ubuntu-24.04"; break;
                case "hardware": json["execution"]!["adapterPolicy"] = "nvidia-t4-hardware"; break;
                case "other-software": json["execution"]!["adapterPolicy"] = leg == "linux-vulkan" ? "microsoft-warp-software" : "mesa-lavapipe-software"; break;
                case "backend": json["execution"]!["backend"] = leg == "linux-vulkan" ? "d3d11" : "vulkan"; break;
                case "leg": json["execution"]!["leg"] = "windows-vulkan"; break;
                case "full-unit": json["limits"]!["unitSeconds"] = 19800; break;
                case "full-job": json["limits"]!["jobMinutes"] = 350; break;
                case "paid-frame": json["limits"]!["frameMilliseconds"] = 10000; break;
                case "paid-billing": json["budget"]!["maximumBilledMinutes"] = 23; break;
                case "rate": json["budget"]!["ratePerMinute"] = 0.102m; break;
                case "cost": json["budget"]!["maximumCostUsd"] = 1; break;
                case "currency": json["budget"]!["currency"] = "NZD"; break;
                case "campaign": json["approval"]!["campaignMaximumUsd"] = 12; break;
                default: json["preset"] = fault; break;
            }
            Assert.Throws<RendererPerfProtocolException>(() => RendererPerfRehearsalManifestTests.Parse(json));
        }
    }

    [Theory]
    [InlineData("linux-vulkan")]
    [InlineData("windows-d3d11")]
    public void Driver_executes_short_conditioning_and_twelve_control_sessions_with_correctness_verdict(string leg)
    {
        var template = Report(leg);
        var rig = new RendererPerfDriverTests.DriverRig(template: template);
        var report = rig.Run();
        Assert.Equal(13, rig.Created.Count);
        Assert.Equal("conditioning:S3", rig.Created[0].SceneId);
        Assert.All(rig.Created.Skip(1), b => Assert.StartsWith("control:", b.SceneId, StringComparison.Ordinal));
        Assert.Equal(2307, rig.Frames.Count);
        Assert.Equal(769, rig.BatchSizes.Count);
        Assert.All(rig.BatchSizes, count => Assert.Equal(3, count));
        foreach (int id in Enumerable.Range(1, 12))
            Assert.Equal(Enumerable.Range(-64, 192), rig.Frames.Where(f => f.Block.SceneId == $"control:{id}").Select(f => f.Index));
        Assert.Equal(3, report.Conditioning.Length);
        Assert.Empty(report.Blocks);
        Assert.Null(report.Publication.OriginalFailure);
        Assert.Equal("S3", Assert.Single(report.Recipes).Identity.SceneId);
        Assert.All(rig.Writes, write => Assert.False(write.Report.Assessment.ValidForPromotion));
        RendererPerfVerdict.RequireSuccess(new(template, null, null, []) { Certified = true });
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfVerdict.RequireSuccess(new(template, null, null, [])));
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfAdmission.Identity(template.Manifest,
            template.Identity with { SoftwareAdapter = false, VendorId = 0x10de, DeviceId = 0x1eb8 }));
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Serialize(template with
        { Assessment = template.Assessment with { ValidForPromotion = true, Reasons = [] } }));
    }

    [Fact]
    public void Controls_only_unit_retains_production_cleanup_replay_and_publication()
    {
        var report = Report("linux-vulkan");
        using var fixture = new RendererPerfColdLifecycleFixture(report with { Identity = report.Identity with { TimestampPeriodNs = 2.5 } });
        fixture.Production.Software = true;
        fixture.Production.Features = new(false, true, "llvmpipe", supportsCompute: true, supportsCompletionFences: true)
        { SupportsTimestamps = true, MaxStorageBufferRangeBytes = 134217728 };
        var outcome = fixture.Run();
        Assert.Empty(outcome.Failures);
        Assert.Equal(2307, fixture.Frames);
        Assert.Equal(13, fixture.CreatedSessions);
        Assert.Equal(RendererPerfCollectionState.Complete, outcome.Report!.Compiler.State);
        Assert.Equal(new[] { RendererPerfRehearsalPolicy.Reason }, outcome.Report.Assessment.Reasons.ToArray());
        Assert.Equal(1, fixture.Publication.Prepared);
        Assert.Equal(1, fixture.Publication.Published);
        Assert.Contains("production.context.dispose", fixture.Trace);
        Assert.Contains("create.diagnostic", fixture.Trace);
        Assert.True(fixture.Trace.IndexOf("production.context.dispose") < fixture.Trace.IndexOf("create.diagnostic"));
    }
}
