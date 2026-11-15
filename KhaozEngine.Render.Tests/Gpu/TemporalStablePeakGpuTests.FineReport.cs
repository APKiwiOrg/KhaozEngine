using System.Collections.Generic;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalStablePeakGpuTests
{
    bool _reportOnly;
    TemporalHistoryDetail _detail;
    TemporalDetailGate _gate;
    readonly Dictionary<Case, (double Change, double Error)> _reported = new();

    void Detail(Scene3D scene)
    {
        scene.Post.Temporal.HistoryDetail = _detail;
        if (_detail == TemporalHistoryDetail.Fine && _gate != TemporalDetailGate.Production)
            scene.InstallTemporalResolveForTests(source => TemporalDetailGateVariants.Rewrite(source, _gate));
    }

    [GpuFact]
    [Trait("Category", "Diagnostic")]
    public void Fine_reports_stable_peaks_against_the_recorded_controls()
    {
        var d = new TemporalStablePeakGpuTests(output) { _reportOnly = true };
        d.Isolated_stable_peaks_keep_their_history_under_a_still_camera_and_light();
        foreach (var gate in TemporalDetailGateVariants.ReportGates)
        {
            var f = new TemporalStablePeakGpuTests(output)
                { _reportOnly = true, _detail = TemporalHistoryDetail.Fine, _gate = gate };
            f.Isolated_stable_peaks_keep_their_history_under_a_still_camera_and_light();
            foreach (var (cell, display) in d._reported)
            {
                var fine = f._reported[cell];
                var before = Baseline[cell];
                output.WriteLine($"REPORT|stable peak|{gate}|{cell.Name}/change|Display={display.Change:R}|Fine={fine.Change:R}|bound={before.Change * (1 + Tolerance):R}");
                output.WriteLine($"REPORT|stable peak|{gate}|{cell.Name}/error|Display={display.Error:R}|Fine={fine.Error:R}|bound={before.Error * (1 + Tolerance):R}");
            }
        }
    }
}
