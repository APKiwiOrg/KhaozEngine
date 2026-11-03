using System;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

[Collection("NativeDeviceLifecycle")]
public sealed partial class RendererPerfCompilerPublicationTests(ITestOutputHelper output)
{
    readonly ITestOutputHelper _output = output;
    [Fact]
    public void Production_publication_implements_the_typed_account()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "renderer-publication-" + Guid.NewGuid().ToString("N"));
        try { Assert.IsAssignableFrom<IRendererPerfUnitPublication>(RendererPerfPublicationWriter.Files(path)); }
        finally { if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true); }
    }

    [Fact]
    public void Parse_is_not_directory_verification()
    {
        var report = RendererPerfAnalysisFixture.Report();
        var parsed = RendererPerfReportJson.Parse(RendererPerfReportJson.Serialize(report));
        var summary = RendererPerfAnalysis.Analyze(parsed);
        Assert.False(summary.PromotionEligible);
        Assert.Contains(summary.Reasons, r => r.Contains("unverified", StringComparison.OrdinalIgnoreCase));
        var comparison = RendererPerfComparison.Compare(parsed, parsed);
        Assert.False(comparison.Compatible);
        Assert.Contains(comparison.Reasons, r => r.Contains("unverified", StringComparison.OrdinalIgnoreCase));
    }
}
