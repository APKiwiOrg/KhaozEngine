using System;
using System.Collections.Immutable;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

public sealed class RendererPerfFloorEvidenceTests
{
    static readonly RendererPerfReferenceRates Rates = new(1e9, null, 1e9, null, null, 40, "fp32-fma", "synthetic inputs");
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Skipped_and_cpu_only_rows_cannot_acquire_gpu_references(bool skipped)
    {
        var model = RendererPerfFloorModelTests.Model(50, 50, 0);
        model = model with
        {
            Components = model.Components.Add(skipped ? "execution" : "gpuEligible",
            new(0, 0, "count", "explicit frozen ownership", "exact", null))
        };
        var result = RendererPerfFloorModel.Evaluate(model, Rates, 200);
        Assert.Equal(skipped ? RendererPerfMetricStatus.NotExecuted : RendererPerfMetricStatus.Unsupported, result.Status);
        Assert.Null(result.PeakLowerBoundNs.Value); Assert.Null(result.Ratio.Value);
    }
    [Fact]
    public void Preserves_work_uncertainty_in_the_calibrated_range()
    {
        var model = RendererPerfFloorModelTests.Model(50, 50, 0);
        model = model with { Components = model.Components.SetItem("readBytes", new(50, 150, "bytes", "bounded traffic", "estimated", null)) };
        var result = RendererPerfFloorModel.Evaluate(model, Rates, 300);
        Assert.NotNull(result.CalibratedRange);
        Assert.Equal(100, result.CalibratedRange.Minimum); Assert.Equal(200, result.CalibratedRange.Maximum);
    }
    [Fact]
    public void Native_controls_publish_retention_and_work_facts_before_analysis()
    {
        using var device = new PostTraceDevice();
        var definition = RendererPerfControls.Definition(4);
        using var work = RendererPerfControls.Create(device, new(4, "1", definition.WorkSha256, 64, 128));
        Assert.Equal(4, work.ControlMetrics["control.readBytes"].Value);
        Assert.Equal(RendererPerfMetricStatus.NotExecuted, work.ControlMetrics["control.outputValidated"].Status);
    }
    [Fact]
    public void Achieved_rates_come_only_from_complete_validated_control_samples()
    {
        var definition = RendererPerfControls.Definition(7);
        var duration = new RendererPerfMetric(1000000, "ns", "synthetic hardware duration", RendererPerfMetricStatus.Available, null);
        var frame = new RendererPerfFrameSample(0, 0, 0, 1, 1, 1, 1, duration, [], [], ImmutableDictionary<string, RendererPerfMetric>.Empty, []);
        var report = new RendererPerfControlReport(new(7, "1", definition.WorkSha256, 64, 128), new string('a', 64),
            [.. Enumerable.Repeat(frame, 64)], [.. Enumerable.Repeat(frame, 128)],
            ImmutableDictionary<string, RendererPerfMetric>.Empty.Add("control.outputValidated", new(1, "count", "all outputs checked", RendererPerfMetricStatus.Available, null)));
        Assert.Equal(536870912000, RendererPerfControls.AchievedRates(report)["bandwidth.bytesPerSecond"].Value);
        Assert.Throws<InvalidOperationException>(() => RendererPerfControls.AchievedRates(report with { StateSha256 = "" }));
        Assert.Throws<InvalidOperationException>(() => RendererPerfControls.AchievedRates(report with { Measured = report.Measured.RemoveAt(0) }));
    }
    [Fact]
    public void Empty_mrt_control_matches_recipe_background_and_motion_sentinel_clears()
    {
        using var device = new PostTraceDevice();
        var definition = RendererPerfControls.Definition(2);
        using var work = RendererPerfControls.Create(device, new(2, "1", definition.WorkSha256, 64, 128));
        work.PrepareCold(-64);
        using var commands = (GeometryTraceCommands)device.Factory.CreateCommandList();
        commands.Begin(); work.Record(commands, -64); commands.End();
        var clears = commands.Events.Where(e => e.Op == "clear-color").ToArray();
        Assert.Equal(4, clears.Length);
        ulong Hash(KhaozEngine.Primitives.Color value)
        {
            ulong hash = 14695981039346656037UL;
            foreach (byte b in System.Runtime.InteropServices.MemoryMarshal.AsBytes(
                System.Runtime.InteropServices.MemoryMarshal.CreateReadOnlySpan(in value, 1)))
                hash = unchecked((hash ^ b) * 1099511628211UL);
            return hash;
        }
        Assert.Equal(Hash(new(.02f, .03f, .06f, 0f)), clears[0].Data);
        Assert.Equal(Hash(new(.02f, .03f, .06f, 0f)), clears[1].Data);
        Assert.Equal(Hash(new(.02f, .03f, .06f, 0f)), clears[2].Data);
        Assert.Equal(Hash(new(65504f, 65504f, 0f, 0f)), clears[3].Data);
    }
}
