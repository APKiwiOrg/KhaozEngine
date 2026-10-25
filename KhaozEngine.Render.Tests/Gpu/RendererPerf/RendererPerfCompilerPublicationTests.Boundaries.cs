using System;
using System.IO;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed partial class RendererPerfCompilerPublicationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Cancellation_or_deadline_during_verification_or_stamp_retains_evidence(bool stamp, bool cancel)
    {
        using var f = new RendererPerfCompilerPublicationFixture();
        string path = Path.Combine(f.Directory, f.Report.Manifest.UnitId + ".json");
        bool triggered = false;
        var budget = f.Budget with
        {
            Boundary = stage =>
        {
            if (triggered || stage != (stamp ? "renderer.publication.slot.after" : "renderer.verify.file.open.after")) return;
            triggered = true;
            if (cancel) f.Cancellation.Cancel(); else f.Now = 1500100;
        }
        };
        var writer = RendererPerfPublicationWriter.Files(f.Directory);
        Assert.True(writer.PrepareCollection(f.Report, f.Inventory, budget).Ready);
        var cold = f.Collect(writer, budget); Assert.Empty(cold.Failures);
        var result = writer.Publish(f.Report with { Compiler = cold.Compiler, Pipelines = cold.Pipelines }, f.Inventory, budget);
        Assert.True(triggered);
        Assert.Contains(result.Failures, f => cancel ? f.Error is OperationCanceledException : f.Error is TimeoutException);
        Assert.False(result.Report.Publication.FinalUnitPublished); Assert.False(result.Report.Assessment.ValidForPromotion);
        Assert.True(File.Exists(Path.Combine(f.Directory, f.Report.Manifest.UnitId + ".partial.json")));
        Assert.NotEmpty(result.Report.Pipelines); Assert.Equal(f.Report.Blocks.Length, result.Report.Blocks.Length);
        _output.WriteLine("file boundary stamp={0} cancel={1} failures={2} elapsed={3}", stamp, cancel, result.Failures.Length,
            result.Report.Publication.ElapsedMilliseconds.Value);
    }
    [Fact]
    public void Inventory_exact_byte_and_depth_bounds_are_enforced_before_decoding()
    {
        using var f = new RendererPerfCompilerPublicationFixture();
        byte[] bytes = RendererPerfCompilerJson.Serialize(f.Inventory, 67108864);
        Assert.Equal(bytes, RendererPerfCompilerJson.Serialize(f.Inventory, bytes.Length));
        Assert.ThrowsAny<Exception>(() => RendererPerfCompilerJson.Serialize(f.Inventory, bytes.Length - 1));
        Assert.ThrowsAny<Exception>(() => RendererPerfCompilerJson.Parse(bytes, bytes.Length - 1));
        byte[] deep = System.Text.Encoding.UTF8.GetBytes(new string('[', 33) + "0" + new string(']', 33));
        Assert.ThrowsAny<Exception>(() => RendererPerfCompilerJson.Parse(deep, deep.Length));
    }
}
