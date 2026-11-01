using System;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfControlTests
{
    [Fact]
    public void Controls_are_finite_matched_and_hash_all_work()
    {
        var controls = Enumerable.Range(1, 12).Select(RendererPerfControls.Definition).ToArray();
        Assert.Equal(12, controls.Select(c => c.WorkSha256).Distinct().Count());
        Assert.All(controls, c => { Assert.Equal(64, c.WarmupFrames); Assert.Equal(128, c.MeasuredFrames); Assert.Equal(64, c.WorkSha256.Length); });
        Assert.Equal(36, controls[4].Dispatches);
        Assert.Equal(36, controls[5].Dispatches);
        Assert.Equal(35, controls[4].SerialEdges);
        Assert.Equal(0, controls[5].SerialEdges);
        Assert.Equal(268435456, controls[6].ReadBytes + controls[6].WriteBytes);
        Assert.Equal(67108864, controls[7].ReadBytes + controls[7].WriteBytes);
        Assert.Equal(67108864, controls[8].Operations);
        Assert.Equal("dependent-divide-root", controls[9].InstructionClass);
        Assert.Equal(8, controls[11].Draws);
        Assert.Throws<ArgumentOutOfRangeException>(() => RendererPerfControls.Definition(13));
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(2, 0, 0)]
    [InlineData(3, 0, 0)]
    [InlineData(4, 1, 0)]
    [InlineData(5, 36, 0)]
    [InlineData(6, 36, 0)]
    [InlineData(7, 1, 0)]
    [InlineData(8, 1, 0)]
    [InlineData(9, 1, 0)]
    [InlineData(10, 1, 0)]
    [InlineData(11, 0, 1)]
    [InlineData(12, 0, 8)]
    public void Records_only_fixed_work_without_submit_wait_or_readback(int id, int dispatches, int draws)
    {
        using var device = new PostTraceDevice();
        var definition = RendererPerfControls.Definition(id);
        using var work = RendererPerfControls.Create(device, new(id, "1", definition.WorkSha256, 64, 128));
        work.PrepareCold(-64);
        int waits = device.Waits;
        using var commands = (GeometryTraceCommands)device.Factory.CreateCommandList();
        commands.Begin(); work.Prepare(-64); work.Record(commands, -64); commands.End();
        Assert.Equal(dispatches, commands.Events.Count(e => e.Op == "dispatch"));
        Assert.Equal(draws, commands.Events.Count(e => e.Op == "draw"));
        Assert.DoesNotContain(commands.Events, e => e.Op.StartsWith("copy", StringComparison.Ordinal) || e.Op.StartsWith("update", StringComparison.Ordinal));
        Assert.Equal(waits, device.Waits);
        Assert.Null(work.ControlStateSha256);
        Assert.Throws<InvalidOperationException>(() => work.Record(commands, -64));
        Assert.Throws<InvalidOperationException>(() => work.ValidateOutput());
    }

    [Fact]
    public void Output_validation_rejects_removed_work_and_hashes_checked_outputs()
    {
        uint[] dependent = Enumerable.Range(1, 36).Select(i => 0x12340000u + (uint)i).ToArray();
        string hash = RendererPerfControls.ValidateWords(5, dependent);
        Assert.Equal(64, hash.Length);
        dependent[18]--;
        Assert.Throws<InvalidOperationException>(() => RendererPerfControls.ValidateWords(5, dependent));
        Assert.Throws<InvalidOperationException>(() => RendererPerfControls.ValidateWords(5, []));
        float[] values = [.29651895f, .41376600f, .53101289f, .64826018f, .78006589f, .89003378f, 1f, 1.10996854f];
        Assert.Equal(8, values.Length);
        Assert.Equal(64, RendererPerfControls.ValidateArithmetic(9, values, 1).Length);
        values[5] = float.NaN;
        Assert.Throws<InvalidOperationException>(() => RendererPerfControls.ValidateArithmetic(9, values, 1));
    }
}
