using System;
using System.Linq;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("GpuBackendProvidersSerial")]
public sealed class RendererPerfRehearsalVerdictTests
{
    [Fact]
    public void Real_publication_certifies_correctness_but_analysis_and_comparison_cannot_promote_it()
    {
        using var fixture = new RendererPerfCompilerPublicationFixture(RendererPerfRehearsalExecutionTests.Report());
        var published = fixture.Publish(RendererPerfPublicationWriter.Files(fixture.Directory));
        Assert.Empty(published.Failures);
        Assert.True(published.Certified);
        var outcome = new RendererPerfUnitOutcome(published.Report, fixture.Inventory, null, []) { Certified = published.Certified };
        RendererPerfVerdict.RequireSuccess(outcome);
        var artifact = RendererPerfArtifactVerification.Verify(fixture.Directory, published.Report.Manifest.UnitId + ".json");
        Assert.False(RendererPerfAnalysis.Analyze(artifact).PromotionEligible);
        Assert.False(RendererPerfComparison.Compare(artifact, artifact).Compatible);
        Assert.False(artifact.Report.Assessment.ValidForPromotion);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("compiler")]
    [InlineData("certification")]
    [InlineData("frame")]
    [InlineData("missing-frame")]
    [InlineData("reason")]
    [InlineData("promotion")]
    [InlineData("publication")]
    public void Rehearsal_verdict_refuses_incomplete_or_failed_evidence(string fault)
    {
        var report = RendererPerfRehearsalExecutionTests.Report();
        var failures = fault == "failure" ? System.Collections.Immutable.ImmutableArray.Create(
            new GpuDiagnosticFailure("test", new InvalidOperationException("retained failure"))) : [];
        if (fault == "compiler") report = report with { Compiler = RendererPerfCompilerEvidence.Pending };
        if (fault == "frame") report = report with { Conditioning = report.Conditioning.SetItem(0, report.Conditioning[0] with { Errors = ["guard failed"] }) };
        if (fault == "missing-frame") report = report with { Conditioning = report.Conditioning.RemoveAt(0) };
        if (fault == "reason") report = report with { Assessment = report.Assessment with { Reasons = [RendererPerfRehearsalPolicy.Reason, "unexpected failure"] } };
        if (fault == "promotion") report = report with { Assessment = report.Assessment with { ValidForPromotion = true } };
        if (fault == "publication") report = report with { Publication = report.Publication with { FinalUnitPublished = false } };
        var outcome = new RendererPerfUnitOutcome(report, null, null, failures) { Certified = fault != "certification" };
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfVerdict.RequireSuccess(outcome));
    }

    [Fact]
    public void Paid_verdict_still_requires_promotion()
    {
        var report = RendererPerfProtocolTests.CompleteReport();
        RendererPerfVerdict.RequireSuccess(new(report, null, null, []));
        Assert.Throws<RendererPerfProtocolException>(() => RendererPerfVerdict.RequireSuccess(new(
            report with { Assessment = report.Assessment with { ValidForPromotion = false } }, null, null, [])));
    }
}
