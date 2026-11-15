using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalFollowCameraGpuTests
{
    [GpuTheory]
    [InlineData(TemporalDetailGate.Production)]
    [InlineData(TemporalDetailGate.Narrower)]
    [InlineData(TemporalDetailGate.Wider)]
    [Trait("Category", "Diagnostic")]
    public void Fine_reports_follow_camera_trails_against_the_existing_bounds(TemporalDetailGate gate)
    {
        var display = runs.For(TemporalHistoryDetail.Display);
        var fine = runs.ForGate(gate);
        foreach (object[] row in Walks)
        {
            var preset = (TemporalUpscale)row[0];
            float speed = (float)row[1];
            var d = display.Run(preset, speed);
            var f = fine.Run(preset, speed);
            int? bound = Bound(preset, speed, d.Total.Checked);
            output.WriteLine($"REPORT|follow camera|{gate}|{preset}/{speed:R}/near|Display={d.Total.Excess}|Fine={f.Total.Excess}|bound={bound}");
        }
        foreach (object[] row in SpillWalks)
        {
            var preset = (TemporalUpscale)row[0];
            float speed = (float)row[1];
            var d = display.BeyondSpill(preset, speed);
            var f = fine.BeyondSpill(preset, speed);
            int? bound = SpillBound(preset, speed, d.Total.Checked);
            output.WriteLine($"REPORT|follow camera|{gate}|{preset}/{speed:R}/past reach|Display={d.Total.Excess}|Fine={f.Total.Excess}|bound={bound}");
        }
    }
}
