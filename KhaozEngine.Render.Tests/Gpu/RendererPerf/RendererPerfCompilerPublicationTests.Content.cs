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
        AssertCompatibleWriterExclusion(target, alreadyOpen, "renderer.verify.complete", 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Earlier_registered_file_stays_excluded_during_later_final_rehash(bool alreadyOpen)
        => AssertCompatibleWriterExclusion("producer", alreadyOpen, "renderer.verify.content.before", 3);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Consumed_root_prefix_stays_excluded_during_multichunk_final_rehash(bool alreadyOpen)
        => AssertCompatibleWriterExclusion("root", alreadyOpen, "renderer.verify.content.chunk.after", 1);

    void AssertCompatibleWriterExclusion(string target, bool alreadyOpen, string boundary, int occurrence)
    {
        using var f = new RendererPerfCompilerPublicationFixture();
        Assert.Empty(f.Publish(RendererPerfPublicationWriter.Files(f.Directory)).Failures);
        string name = f.Report.Manifest.UnitId + ".json";
        string relative = target == "root" ? name : target == "inventory" ? f.Report.Manifest.UnitId + "-compiler-inventory.json" : "work-models.json";
        string path = Path.Combine(f.Directory, relative);
        var before = RendererPerfArtifactIdentity.Platform.Entry(null, path);
        byte[] original = File.ReadAllBytes(path);
        if (boundary.EndsWith("chunk.after", StringComparison.Ordinal)) Assert.True(original.Length > 65536);
        using var existing = alreadyOpen ? OpenWriter() : null;
        bool reached = false, flushed = false;
        int seen = 0, acquisitions = 0, acquired = 0;
        Exception? writeError = null;
        var budget = f.Budget with
        {
            Boundary = stage =>
            {
                if (stage == "renderer.verify.file.open.before") acquisitions++;
                if (stage == "renderer.verify.file.open.after") acquired++;
                if (stage != boundary || ++seen != occurrence) return;
                reached = true;
                writeError = Record.Exception(() =>
                {
                    using var opened = alreadyOpen ? null : OpenWriter();
                    var writer = existing ?? opened!;
                    writer.Position = 0;
                    writer.WriteByte((byte)'!');
                    writer.Flush(true);
                    flushed = true;
                    Assert.Equal(original.LongLength, writer.Length);
                });
                Assert.Equal(before, RendererPerfArtifactIdentity.Platform.Entry(null, path));
            }
        };
        RendererPerfVerifiedArtifact? verified = null;
        var error = Record.Exception(() => verified = RendererPerfArtifactVerification.Verify(f.Directory, name, budget));
        Assert.Equal(original.LongLength, new FileInfo(path).Length);
        Assert.Equal(before, RendererPerfArtifactIdentity.Platform.Entry(null, path));
        _output.WriteLine("writer observation target={0} existing={1} boundary={2}/{3} reached={4} flushed={5} length={6} sameIdentity=true verified={7} writerError={8} readerError={9}",
            target, alreadyOpen, boundary, occurrence, reached, flushed, original.Length, verified is not null, writeError, error);
        // A completed overwrite must never leave a verified wrapper. In the supported protocol an
        // existing writer prevents acquisition, while a new writer is refused at the held boundary.
        if (alreadyOpen)
        {
            Assert.False(reached);
            Assert.Equal(target == "root" ? 1 : target == "producer" ? 2 : 3, acquisitions);
            Assert.Equal(acquisitions - 1, acquired);
            AssertLeaseContention(error);
            Assert.Null(verified);
        }
        else
        {
            Assert.True(reached);
            if (flushed)
            {
                Assert.NotNull(error);
                Assert.Null(verified);
                Assert.Contains("content changed", Assert.IsType<IOException>(error).Message, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                AssertSharingContention(writeError);
                Assert.Null(error);
                Assert.NotNull(verified);
            }
        }
        existing?.Dispose();
        Assert.Equal(original.LongLength, new FileInfo(path).Length);
        Assert.Equal(before, RendererPerfArtifactIdentity.Platform.Entry(null, path));
        byte[] current = File.ReadAllBytes(path);
        if (!flushed) Assert.Equal(original, current);
        else { Assert.Equal((byte)'!', current[0]); Assert.Equal(original.AsSpan(1).ToArray(), current.AsSpan(1).ToArray()); }
        // Open, write and flush after every successful/refused verification, proving lease release.
        using (var released = OpenWriter()) { released.WriteByte(original[0]); released.Flush(true); }
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(0, RunConsole(f.Directory, name).Exit);
        _output.WriteLine("exclusion target={0} existing={1} boundary={2}/{3} reached={4} acquisitions={5} acquired={6} flushed={7} writerError={8} readerError={9} unchanged={10} released=true",
            target, alreadyOpen, boundary, occurrence, reached, acquisitions, acquired, flushed, writeError, error, !flushed);

        FileStream OpenWriter() => new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }
    static void AssertSharingContention(Exception? error)
    {
        var io = Assert.IsType<IOException>(error);
        Assert.Contains(io.HResult & 0xffff, new[] { 11, 32, 35 });
    }
    static void AssertLeaseContention(Exception? error)
    {
        var io = Assert.IsType<IOException>(error);
        var native = Assert.IsType<System.ComponentModel.Win32Exception>(io.InnerException);
        Assert.Equal(OperatingSystem.IsWindows() ? 32 : OperatingSystem.IsMacOS() ? 35 : 11, native.NativeErrorCode);
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
