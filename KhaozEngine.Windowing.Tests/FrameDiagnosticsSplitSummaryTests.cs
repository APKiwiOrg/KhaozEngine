using System;
using System.Text.Json;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

public sealed class FrameDiagnosticsSplitSummaryTests
{
    [Theory]
    [InlineData(1.025, 2)]
    [InlineData(1.022, 0)]
    public void Idle_uses_the_frame_pair_edges_and_busy_excludes_its_internal_gap(double nextStart, double idleMs)
    {
        var snapshot = new FrameTimingSnapshot(new([
            new(1, 8, 110, 0, FrameTimingCommandKind.EarlyFrameRecording),
            new(2, 8, 120, 0, FrameTimingCommandKind.FrameRecording),
            new(1, 8, 210, 1, FrameTimingCommandKind.EarlyFrameRecording),
            new(4, 8, 220, 1, FrameTimingCommandKind.FrameRecording),
            new(5, 8, 310, 2, FrameTimingCommandKind.EarlyFrameRecording),
            new(6, 8, 320, 2, FrameTimingCommandKind.FrameRecording),
            new(7, 8, 410, 3, FrameTimingCommandKind.FrameRecording)], 0, 0),
            new([
            new(4, 8, 260, 4, 1.050, 1.054),
            new(1, 8, 250, 4, nextStart, nextStart + .002),
            new(2, 8, 160, 4, 1.020, 1.023),
            new(1, 8, 150, 4, 1.000, 1.002),
            new(6, 8, 360, 4, 1.070, 1.080),
            new(7, 8, 450, 4, 1.090, 1.094)], 0, 0),
            new([], 0, 0), new([], 0, 0), 0);
        var summary = FrameDiagnosticsGpuSummary.Create(snapshot, 4);
        Assert.Equal(new[] { true, true, false, true }, summary.FrameCompletionCoverage);
        Assert.Equal(3, summary.FrameGpuBusyMs.Count);
        Assert.Equal(5, summary.FrameGpuBusyMs.Mean, 8);
        Assert.Equal(1, summary.FrameCompletionIntervalMs.Count);
        Assert.Equal(31, summary.FrameCompletionIntervalMs.Mean, 8);
        Assert.Equal(1, summary.GpuIdlePerFrameMs.Count);
        Assert.Equal(idleMs, summary.GpuIdlePerFrameMs.Mean, 8);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(summary,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        Assert.Equal(idleMs, json.RootElement.GetProperty("gpuIdlePerFrameMs").GetProperty("mean").GetDouble(), 8);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(1)]
    public void Missing_either_half_of_a_split_frame_provides_no_busy_or_frame_interval(int missing)
    {
        var submits = new[] { new FrameTimingSubmit(1, 8, 10, 0, FrameTimingCommandKind.EarlyFrameRecording),
            new FrameTimingSubmit(2, 8, 20, 0, FrameTimingCommandKind.FrameRecording) };
        var remaining = Assert.Single(submits, s => (int)s.Kind != missing);
        var snapshot = new FrameTimingSnapshot(new(submits, 0, 0),
            new([new(remaining.CommandBuffer, 8, 30, 4, 1, 1.002)], 0, 0), new([], 0, 0), new([], 0, 0), 0);
        var summary = FrameDiagnosticsGpuSummary.Create(snapshot, 1);
        Assert.Equal(new[] { false }, summary.FrameCompletionCoverage);
        Assert.Equal(0, summary.FrameGpuBusyMs.Count);
        Assert.Equal(0, summary.GpuIdlePerFrameMs.Count);
        Assert.Equal(0, summary.FrameCompletionIntervalMs.Count);
    }
}
