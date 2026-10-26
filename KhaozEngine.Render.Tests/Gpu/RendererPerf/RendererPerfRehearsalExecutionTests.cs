using System;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("GpuBackendProvidersSerial")]
public sealed class RendererPerfRehearsalExecutionTests
{
    internal static RendererPerfReport Report()
    {
        var report = RendererPerfProtocolTests.CompleteReport();
        return report with
        {
            Manifest = RendererPerfRehearsalManifestTests.Parse(RendererPerfRehearsalManifestTests.ManifestJson()),
            Identity = report.Identity with
            {
                SoftwareAdapter = true,
                VendorId = 0x10005,
                DeviceId = 0,
                Adapter = "llvmpipe",
                Driver = "Mesa driver"
            },
            Assessment = report.Assessment with { ValidForPromotion = false, Reasons = [RendererPerfRehearsalPolicy.Reason] }
        };
    }

    [Fact]
    public void Rehearsal_admits_software_and_refuses_hardware_while_performance_refuses_software()
    {
        var report = Report();
        RendererPerfAdmission.Identity(report.Manifest, report.Identity);
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfAdmission.Identity(report.Manifest,
            RendererPerfProtocolTests.CompleteReport().Identity));
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfAdmission.Identity(
            RendererPerfProtocolTests.CompleteReport().Manifest, report.Identity));
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfAdmission.Identity(report.Manifest,
            report.Identity with { SoftwareAdapter = null }));
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfAdmission.Identity(report.Manifest,
            report.Identity with { TimestampSupported = false }));
    }

    [Fact]
    public void Rehearsal_drift_is_diagnostic_and_all_frames_are_retained()
    {
        var rig = new RendererPerfDriverTests.DriverRig(template: Report()) { Drift = true };
        var report = rig.Run();
        Assert.False(report.Assessment.ValidForPromotion);
        Assert.Contains(RendererPerfRehearsalPolicy.Reason, report.Assessment.Reasons);
        Assert.DoesNotContain(report.Assessment.Reasons, r => r.Contains("ClockOrWorkDrift", StringComparison.Ordinal));
        Assert.True(report.Assessment.Metrics["rehearsal.clockOrWorkDrift.blocks"].Value > 0);
        Assert.Equal(1260, rig.Frames.Count);
    }

    [Fact]
    public void Strict_reader_cannot_promote_a_rehearsal_even_with_forged_hardware_identity()
    {
        var report = Report();
        var forged = report with
        {
            Identity = RendererPerfProtocolTests.CompleteReport().Identity,
            Assessment = report.Assessment with { ValidForPromotion = true, Reasons = [] }
        };
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfReportJson.Serialize(forged));
    }

    [Fact]
    public void Concrete_unit_uses_all_native_sessions_and_never_proposes_promotion()
    {
        using var fixture = new RendererPerfColdLifecycleFixture(Report() with { Identity = Report().Identity with { TimestampPeriodNs = 2.5 } });
        fixture.Production.Software = true;
        fixture.Production.Features = new(false, true, "llvmpipe", supportsCompute: true, supportsCompletionFences: true) { SupportsTimestamps = true };
        var outcome = fixture.Run();
        Assert.Empty(outcome.Failures);
        Assert.Equal(1260, fixture.Frames);
        Assert.Equal(9, fixture.CreatedSessions);
        Assert.NotNull(fixture.Publication.Report);
        Assert.False(fixture.Publication.Report.Assessment.ValidForPromotion);
        Assert.Equal(new[] { RendererPerfRehearsalPolicy.Reason }, fixture.Publication.Report.Assessment.Reasons.ToArray());
        Assert.Equal(RendererPerfCollectionState.Complete, fixture.Publication.Report.Compiler.State);
    }
}
