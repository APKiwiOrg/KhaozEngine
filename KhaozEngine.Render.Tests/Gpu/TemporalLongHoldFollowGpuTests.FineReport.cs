using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalLongHoldFollowGpuTests
{
    [GpuTheory]
    [InlineData(TemporalDetailGate.Production)]
    [InlineData(TemporalDetailGate.Narrower)]
    [InlineData(TemporalDetailGate.Wider)]
    [Trait("Category", "Diagnostic")]
    public void Fine_reports_long_hold_follow_cells_without_new_bounds(TemporalDetailGate gate)
    {
        var od = ortho.For(TemporalHistoryDetail.Display);
        var of = ortho.ForGate(gate);
        foreach (float speed in Speeds)
            foreach (var preset in Presets)
            {
                var d = od.Run(preset, speed, LongHold);
                var f = of.Run(preset, speed, LongHold);
                int? bound = TemporalFollowCameraGpuTests.Bound(preset, speed, d.Total.Checked);
                output.WriteLine($"REPORT|long hold orthographic|{gate}|{preset}/{speed:R}/near|Display={d.Total.Excess}|Fine={f.Total.Excess}|bound={bound}");
                var dp = od.BeyondSpill(preset, speed, LongHold);
                var fp = of.BeyondSpill(preset, speed, LongHold);
                int? past = TemporalFollowCameraGpuTests.SpillBound(preset, speed, dp.Total.Checked);
                output.WriteLine($"REPORT|long hold orthographic|{gate}|{preset}/{speed:R}/past|Display={dp.Total.Excess}|Fine={fp.Total.Excess}|bound={past}");
            }
        var pd = perspective.For(TemporalHistoryDetail.Display);
        var pf = perspective.ForGate(gate);
        foreach (float pitch in new[] { BootPitch, TemporalPerspectiveFollowGpuTests.LowPitch })
            foreach (var heading in Headings)
                foreach (float speed in Speeds)
                    foreach (var preset in Presets)
                    {
                        var d = pd.Run(preset, speed, pitch, heading, LongHold);
                        var f = pf.Run(preset, speed, pitch, heading, LongHold);
                        int bound = TemporalPerspectiveFollowGpuTests.Bound(pitch, heading, speed, preset, d.Total.Checked);
                        output.WriteLine($"REPORT|long hold perspective|{gate}|{preset}/{pitch:R}/{heading}/{speed:R}/near|Display={d.Total.Excess}|Fine={f.Total.Excess}|bound={bound}");
                        var dp = pd.BeyondSpill(preset, speed, pitch, heading, LongHold);
                        var fp = pf.BeyondSpill(preset, speed, pitch, heading, LongHold);
                        int? past = TemporalPerspectiveFollowGpuTests.ReachBound(pitch, heading, speed, preset, dp.Total.Checked);
                        output.WriteLine($"REPORT|long hold perspective|{gate}|{preset}/{pitch:R}/{heading}/{speed:R}/past|Display={dp.Total.Excess}|Fine={fp.Total.Excess}|bound={past}");
                    }
    }
}
