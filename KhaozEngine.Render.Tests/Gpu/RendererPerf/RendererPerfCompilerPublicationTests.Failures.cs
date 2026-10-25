using System;
using System.IO;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfCompilerPublicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Actual_final_correction_and_recovery_failures_reach_unit_outcome(bool exhaustText)
    {
        using var fixture = new RendererPerfColdLifecycleFixture();
        var final = new InvalidOperationException(exhaustText ? new string('\u0001', 3000000) : "actual final write");
        var correction = new InvalidOperationException("actual correction write");
        var recovery = new InvalidOperationException("actual partial recovery write");
        string directory = Path.Combine(Path.GetTempPath(), "renderer-publication-failures-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var files = new RendererPerfPublicationFiles(directory);
        int writes = 0;
        var writer = new RendererPerfPublicationWriter((report, isFinal) =>
        {
            writes++;
            if (writes == 1) { Assert.False(isFinal); files.Write(report, false); return; }
            throw writes == 2 ? final : writes == 3 ? correction : recovery;
        }, _ => throw new InvalidOperationException("No stamp follows a failed final."), directory: directory);
        var outcome = fixture.Run(Assert.IsAssignableFrom<IRendererPerfUnitPublication>(writer));
        Assert.NotNull(outcome.Report);
        Assert.Equal(4, writes);
        Assert.Equal(new[] { "renderer.publication.final", "renderer.publication.correction", "renderer.publication.recovery" },
            outcome.Failures.Select(f => f.Stage));
        Assert.Same(final, outcome.Failures[0].Error);
        Assert.Same(correction, outcome.Failures[1].Error);
        Assert.Same(recovery, outcome.Failures[2].Error);
        Assert.False(outcome.Report.Publication.FinalUnitPublished);
        Assert.False(outcome.Report.Blocks.IsEmpty);
        Assert.NotEmpty(outcome.Report.Pipelines);
        Assert.True(File.Exists(Path.Combine(directory, fixture.Template.Manifest.UnitId + ".partial.json")));
        if (exhaustText)
        {
            Assert.Equal(RendererPerfFailureText.OmittedOriginal, outcome.Report.Publication.OriginalFailure);
            Assert.False(outcome.Report.Publication.FailureText.Complete);
            Assert.Equal(3, outcome.Report.Publication.FailureText.OmittedFailures);
            Assert.True(outcome.Report.Publication.FailureText.RequiredUtf8Bytes > 16777216);
        }
        else Assert.Equal(final.Message, outcome.Report.Publication.OriginalFailure);
        _output.WriteLine("actual final/correction/recovery events=3 partialExists=true textComplete={0} required={1}",
            outcome.Report.Publication.FailureText.Complete, outcome.Report.Publication.FailureText.RequiredUtf8Bytes);
        System.IO.Directory.Delete(directory, true);
    }
    [Fact]
    public void Previously_omitted_original_text_keeps_exact_required_bytes()
    {
        using var fixture = new RendererPerfColdLifecycleFixture();
        string directory = Path.Combine(Path.GetTempPath(), "renderer-omitted-cause-" + Guid.NewGuid().ToString("N"));
        var original = new InvalidOperationException(new string('\u0001', 3000000));
        fixture.Boundary = stage => { if (stage == "frame") throw original; };
        try
        {
            var outcome = fixture.Run(RendererPerfPublicationWriter.Files(directory));
            Assert.Single(outcome.Failures); Assert.Same(original, outcome.Failures[0].Error);
            Assert.NotNull(outcome.Report);
            Assert.False(outcome.Report.Publication.FailureText.Complete);
            Assert.Equal(1, outcome.Report.Publication.FailureText.OmittedFailures);
            Assert.Equal(18000004, outcome.Report.Publication.FailureText.RequiredUtf8Bytes);
            Assert.Equal(RendererPerfFailureText.OmittedOriginal, outcome.Report.Publication.OriginalFailure);
            Assert.Equal(0, fixture.Diagnostic.CompilerReads);
        }
        finally { if (System.IO.Directory.Exists(directory)) System.IO.Directory.Delete(directory, true); }
    }

}
