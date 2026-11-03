using System.Linq;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

public sealed class FrameDiagnosticsCandidateTests
{
    sealed class Gates : IFrameDiagnosticsShaderGates
    {
        internal int Mask, Exact = 1;
        public void Apply(int gates) => Mask = gates;
        public int ConsumeApplied() => Mask;
        public int ConsumeExactSizeCondition() => Exact;
    }

    [Fact]
    public void Candidate_rotation_records_the_exact_size_condition_even_when_G4_is_off_and_stops_all_gates()
    {
        var seam = new Gates();
        var gates = new FrameDiagnosticsShaderGateSchedule(seam, 300, candidate: true);
        var early = new FrameEarlySubmitCapture(300, candidate: true);
        for (int i = 0; i < 300; i++)
        {
            gates.Boundary(i, i);
            early.Boundary(i, i);
            Assert.True(early.Enabled);
            seam.Exact = i == 1 ? 0 : i == 2 ? -1 : i == 3 ? -2 : 1;
            gates.CallbackEnded(i);
            if (i != 2) early.Applied(i);
        }
        gates.End(300, 300);
        early.End(300, 300);
        gates.Boundary(360, 360);
        early.Boundary(360, 360);
        Assert.Equal(0, seam.Mask);
        Assert.False(early.Enabled);
        var report = gates.Report(300);
        Assert.Equal(new[] { 1, 9, 17, 33, 1 }, report.Schedule.Select(b => b.Gates));
        Assert.Equal(new[] { 1, 0, -1, -2, 1 }, report.ExactSizePerCallback.Take(5));
        Assert.Equal(300, report.Counts.Sum(c => c.MatchingCallbacks));
        Assert.Equal(299, early.Snapshot(300).AppliedCallbacks);
    }

    [Fact]
    public void Candidate_summary_keeps_TAA_exclusive_spans_separate_and_joins_split_frames_for_idle()
    {
        var schedule = new FrameDiagnosticsShaderGateSchedule(null, 10, candidate: true);
        schedule.Boundary(0, 0);
        schedule.End(10, 10);
        var labels = PassLabelsSnapshot.Unavailable() with { Labels = ["opaque.rigid", "temporal.accumulate"] };
        var passes = PassTimingSnapshot.Unavailable() with
        {
            ClockPairs = new([new(0, 0, 0, 0, 1_000_000, 100), new(10, 10, 10, 10, 1_001_000_000, 1_000_100)], 0, 0),
            Encoders = new([
                new(1, 4, 0, "render", 0, 1000, 2000, 2000, 4000, null, null, "valid", 0),
                new(1, 4, 1, "render", 1, 3000, 3000, 3000, 5000, null, null, "valid", 1)], 0, 0),
        };
        var timing = new FrameTimingSnapshot(new([
            new(1, 8, 1, 4, FrameTimingCommandKind.EarlyFrameRecording), new(2, 8, 2, 4, FrameTimingCommandKind.FrameRecording),
            new(3, 8, 3, 5, FrameTimingCommandKind.EarlyFrameRecording), new(4, 8, 4, 5, FrameTimingCommandKind.FrameRecording)], 0, 0),
            new([new(1, 8, 10, 4, 1, 1.001), new(2, 8, 11, 4, 1.002, 1.003),
                new(3, 8, 12, 4, 1.004, 1.005), new(4, 8, 13, 4, 1.006, 1.007)], 0, 0),
            new([], 0, 0), new([], 0, 0), 0);
        var state = FrameDiagnosticsShaderGateSummary.States(timing, passes, labels, schedule.Report(10))[0];
        Assert.Equal("opaque.rigid", state.MainPassLabel);
        Assert.Equal("temporal.accumulate", state.TaaPassLabel);
        Assert.Equal(4, state.FrameCompletionIntervalMs.Mean, 8);
        Assert.Equal(1, state.GpuIdlePerFrameMs.Mean, 8);
        Assert.Equal(1, state.SettledCallbacks);
        Assert.Equal(1, state.MainPassExclusiveVertexMs.Mean, 8);
        Assert.Equal(1.5, state.MainPassExclusiveFragmentMs.Mean, 8);
        Assert.Equal(2, state.TaaPassFragmentMs.Mean, 8);
        Assert.Equal(1.5, state.TaaPassExclusiveFragmentMs.Mean, 8);
        Assert.Equal(4, state.AttributedBusyMs.Mean, 8);
    }
}
