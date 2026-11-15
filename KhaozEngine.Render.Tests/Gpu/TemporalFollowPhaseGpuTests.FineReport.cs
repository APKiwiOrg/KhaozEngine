using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalFollowPhaseGpuTests
{
    bool _reportOnly;
    bool _fullReport;
    bool FullTable => _fullReport || TemporalStabilityRuns.FullTable;
    readonly List<(string Family, PhaseSweep Sweep)> _reported = new();

    [GpuTheory]
    [InlineData(TemporalDetailGate.Production)]
    [InlineData(TemporalDetailGate.Narrower)]
    [InlineData(TemporalDetailGate.Wider)]
    [Trait("Category", "Diagnostic")]
    public void Fine_reports_all_follow_phase_cells_without_a_new_bound(TemporalDetailGate gate)
    {
        var d = new TemporalFollowPhaseGpuTests(ortho.For(TemporalHistoryDetail.Display),
            perspective.For(TemporalHistoryDetail.Display), lines.For(TemporalHistoryDetail.Display),
            stops.For(TemporalHistoryDetail.Display), output) { _reportOnly = true, _fullReport = true };
        var f = new TemporalFollowPhaseGpuTests(ortho.ForGate(gate), perspective.ForGate(gate),
            lines.ForGate(gate), stops.ForGate(gate), output) { _reportOnly = true, _fullReport = true };
        Capture(d);
        Capture(f);
        var fine = f._reported.ToDictionary(row => (row.Family, row.Sweep.Cell), row => row.Sweep);
        foreach (var (family, display) in d._reported)
        {
            var measured = fine[(family, display.Cell)];
            for (int i = 0; i < display.Phases.Length; i++)
                output.WriteLine($"REPORT|follow phase/{family}|{gate}|{display.Cell}/phase {display.Phases[i]}"
                    + $"|Display={display.Values[i]}|Fine={measured.Values[i]}|bound={display.Bounds[i]}|at least={display.AtLeast}");
        }

        static void Capture(TemporalFollowPhaseGpuTests source)
        {
            source.OrthographicTable(N, Q, P, U);
            source.PerspectiveTable(new[] { N, Q, P, U });
            source.The_follow_lines_hold_their_share_from_every_start_phase();
            source.The_follow_stop_holds_its_bounds_from_every_start_phase();
        }
    }
}
