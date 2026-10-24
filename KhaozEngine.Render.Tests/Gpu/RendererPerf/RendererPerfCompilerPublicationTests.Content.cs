using System;
using System.IO;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfCompilerPublicationTests
{
    [Theory]
    [InlineData("root", false)]
    [InlineData("inventory", false)]
    [InlineData("producer", false)]
    [InlineData("root", true)]
    [InlineData("inventory", true)]
    [InlineData("producer", true)]
    public void Same_identity_overwrite_after_decode_refuses_new_and_existing_writers(string target, bool alreadyOpen)
    {
        using var f = new RendererPerfCompilerPublicationFixture();
        var publisher = RendererPerfPublicationWriter.Files(f.Directory);
        Assert.Empty(f.Publish(publisher).Failures);
        string name = f.Report.Manifest.UnitId + ".json";
        string relative = target == "root" ? name : target == "inventory" ? f.Report.Manifest.UnitId + "-compiler-inventory.json" : "work-models.json";
        string path = Path.Combine(f.Directory, relative);
        var before = RendererPerfArtifactIdentity.Platform.Entry(null, path);
        long length = new FileInfo(path).Length;
        using var existing = alreadyOpen ? OpenWriter() : null;
        bool reached = false, flushed = false;
        var budget = f.Budget with
        {
            Boundary = stage =>
            {
                if (stage != "renderer.verify.complete") return;
                Assert.False(reached);
                reached = true;
                using var opened = alreadyOpen ? null : OpenWriter();
                var writer = existing ?? opened!;
                writer.Position = 0;
                writer.WriteByte((byte)'!');
                writer.Flush(true);
                flushed = true;
                Assert.Equal(length, writer.Length);
                Assert.Equal(before, RendererPerfArtifactIdentity.Platform.Entry(null, path));
            }
        };
        var error = Record.Exception(() => RendererPerfArtifactVerification.Verify(f.Directory, name, budget));
        Assert.True(reached);
        Assert.True(flushed);
        Assert.NotNull(error);
        Assert.Contains("content changed", Assert.IsType<IOException>(error).Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(length, new FileInfo(path).Length);
        Assert.Equal(before, RendererPerfArtifactIdentity.Platform.Entry(null, path));
        Assert.NotEqual(0, RunConsole(f.Directory, name).Exit);
        _output.WriteLine("content target={0} writerAlreadyOpen={1} boundary=true flushed=true length={2} sameIdentity=true refusal={3}",
            target, alreadyOpen, length, error.Message);

        FileStream OpenWriter() => new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Content_validation_uses_original_budget_and_retains_failed_publication(bool cancel, bool afterRead)
    {
        using var f = new RendererPerfCompilerPublicationFixture();
        bool reached = false;
        string boundary = afterRead ? "renderer.verify.content.chunk.after" : "renderer.verify.content.chunk.before";
        var budget = f.Budget with
        {
            Boundary = stage =>
        {
            if (reached || stage != boundary) return;
            reached = true;
            if (cancel) f.Cancellation.Cancel(); else f.Now = 1500100;
        }
        };
        var publisher = RendererPerfPublicationWriter.Files(f.Directory);
        Assert.True(publisher.PrepareCollection(f.Report, f.Inventory, budget).Ready);
        var cold = f.Collect(publisher, budget);
        Assert.Empty(cold.Failures);
        var result = publisher.Publish(f.Report with { Compiler = cold.Compiler, Pipelines = cold.Pipelines }, f.Inventory, budget);
        Assert.True(reached);
        var failure = Assert.Single(result.Failures);
        if (cancel) Assert.Equal(f.Cancellation.Token, Assert.IsType<OperationCanceledException>(failure.Error).CancellationToken);
        else Assert.Contains(boundary, Assert.IsType<TimeoutException>(failure.Error).Message, StringComparison.Ordinal);
        Assert.False(result.Report.Publication.FinalUnitPublished);
        Assert.False(result.Report.Assessment.ValidForPromotion);
        Assert.Equal(f.Report.Blocks, result.Report.Blocks);
        Assert.Equal(cold.Pipelines, result.Report.Pipelines);
        string name = f.Report.Manifest.UnitId;
        var stored = RendererPerfReportJson.Parse(File.ReadAllBytes(Path.Combine(f.Directory, name + ".json")));
        Assert.Equal(RendererPerfPublicationState.Failed, stored.Publication.State);
        Assert.False(stored.Assessment.ValidForPromotion);
        Assert.True(File.Exists(Path.Combine(f.Directory, name + ".partial.json")));
        _output.WriteLine("content budget boundary={0} cancel={1} reached=true failure={2} elapsed={3}",
            boundary, cancel, failure.Error, result.Report.Publication.ElapsedMilliseconds.Value);
    }

}
