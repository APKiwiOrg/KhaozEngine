using System.Collections.Generic;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalFollowStopGpuTests
{
    [GpuFact]
    [Trait("Category", "Diagnostic")]
    public void The_worst_damped_stop_reports_each_rules_inner_pixel_cost()
    {
        var trace = TemporalFollowStopRuns.TraceWorstStop();
        var r = trace.Walk;
        double lead = Lead(r.Inner, trace.Control.Inner, null, null);
        Worst(r, "inner", r.Inner, trace.Control.Inner, lead, new List<string>(), (frame, margin, failed) =>
        {
            var c = trace.Counts[TemporalFollowStopRuns.Turn + frame];
            output.WriteLine($"ATTRIBUTION|frame={frame}|margin={margin:R}|bound={Share(r, "inner"):R}|failed={failed}"
                + $"|inner={c.Inner}|base coarse={c.BaseCoarse}|lock={c.Lock}|marks={c.Marks}|both={c.Both}"
                + $"|lock alone={c.LockAlone}|marks alone={c.MarksAlone}|live coarse={c.LiveCoarse}|mismatch={c.Mismatch}");
        });
    }
}
