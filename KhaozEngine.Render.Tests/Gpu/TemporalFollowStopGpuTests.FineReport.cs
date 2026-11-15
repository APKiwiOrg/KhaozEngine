using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalFollowStopGpuTests
{
    [GpuTheory]
    [TemporalDetailReportGates]
    [Trait("Category", "Diagnostic")]
    public void Fine_reports_follow_stop_cells_against_the_existing_controls(TemporalDetailGate gate)
    {
        var d = new TemporalFollowStopGpuTests(runs.For(TemporalHistoryDetail.Display), output);
        var f = new TemporalFollowStopGpuTests(runs.ForGate(gate), output);
        var plainD = d.Walks(true).ToDictionary(row => row.Name);
        var plainF = f.Walks(true).ToDictionary(row => row.Name);
        var fine = f.Walks(true).Concat(f.Surrounded(true)).ToDictionary(row => row.Name);
        foreach (var display in d.Walks(true).Concat(d.Surrounded(true)))
        {
            var measured = fine[display.Name];
            var cd = runs.For(TemporalHistoryDetail.Display).Control(display);
            var cf = runs.ForGate(gate).Control(measured);
            StopRun? wd = display.Surround == StopSurround.Passer
                ? plainD[display.Name.Replace(TemporalFollowStopRuns.PasserSuffix, "", System.StringComparison.Ordinal)] : null;
            StopRun? wf = measured.Surround == StopSurround.Passer
                ? plainF[measured.Name.Replace(TemporalFollowStopRuns.PasserSuffix, "", System.StringComparison.Ordinal)] : null;
            foreach (string set in new[] { "whole", "inner" })
            {
                StopMeasure Pick(StopRun row) => set == "whole" ? row.Whole : row.Inner;
                double ld = Lead(Pick(display), Pick(cd), wd is null ? null : Pick(wd),
                    wd is null ? null : Pick(runs.For(TemporalHistoryDetail.Display).Control(wd)));
                double lf = Lead(Pick(measured), Pick(cf), wf is null ? null : Pick(wf),
                    wf is null ? null : Pick(runs.ForGate(gate).Control(wf)));
                double dv = Worst(display, set, Pick(display), Pick(cd), ld, new List<string>());
                double fv = Worst(measured, set, Pick(measured), Pick(cf), lf, new List<string>());
                double? bound = display.Surround == StopSurround.Passer && display.Ending == FollowEnding.DampedStop
                    ? null : Share(display, set);
                output.WriteLine($"REPORT|follow stop|{gate}|{display.Name}/{set}|Display={dv:R}|Fine={fv:R}|bound={bound:R}");
            }
        }
    }
}
