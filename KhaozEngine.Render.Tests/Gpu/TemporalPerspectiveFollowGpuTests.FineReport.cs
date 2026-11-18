using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalPerspectiveFollowGpuTests
{
    [GpuTheory]
    [TemporalDetailReportGates]
    [Trait("Category", "Diagnostic")]
    public void Fine_reports_perspective_follow_trails_against_the_existing_bounds(TemporalDetailGate gate)
    {
        var display = runs.For(TemporalHistoryDetail.Display);
        var fine = runs.ForGate(gate);
        foreach (var (pitch, heading, speed, preset) in TableWalks(fullOverride: true))
        {
            var d = display.Run(preset, speed, pitch, heading);
            var f = fine.Run(preset, speed, pitch, heading);
            int bound = Bound(pitch, heading, speed, preset, d.Total.Checked);
            output.WriteLine($"REPORT|perspective follow|{gate}|{preset}/{pitch:R}/{heading}/{speed:R}/near|Display={d.Total.Excess}|Fine={f.Total.Excess}|bound={bound}");
        }
        foreach (var (pitch, heading, speed, preset) in ReachWalks(fullOverride: true))
        {
            var d = display.BeyondSpill(preset, speed, pitch, heading);
            var f = fine.BeyondSpill(preset, speed, pitch, heading);
            int? bound = ReachBound(pitch, heading, speed, preset, d.Total.Checked);
            output.WriteLine($"REPORT|perspective follow|{gate}|{preset}/{pitch:R}/{heading}/{speed:R}/past reach|Display={d.Total.Excess}|Fine={f.Total.Excess}|bound={bound}");
        }
    }
}
