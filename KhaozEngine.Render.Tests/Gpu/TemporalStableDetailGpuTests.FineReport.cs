using System.Collections.Generic;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalStableDetailGpuTests
{
    bool _reportOnly;
    TemporalHistoryDetail _detail;
    TemporalDetailGate _gate;
    readonly Dictionary<string, Figures> _reported = new();

    void Detail(Scene3D scene)
    {
        scene.Post.Temporal.HistoryDetail = _detail;
        if (_detail == TemporalHistoryDetail.Fine && _gate != TemporalDetailGate.Production)
            scene.InstallTemporalResolveForTests(source => TemporalDetailGateVariants.Rewrite(source, _gate));
    }

    [GpuFact]
    [Trait("Category", "Diagnostic")]
    public void Fine_reports_stable_texture_detail_against_the_recorded_controls()
    {
        var d = new TemporalStableDetailGpuTests(output) { _reportOnly = true };
        Capture(d);
        foreach (var gate in TemporalDetailGateVariants.ReportGates)
        {
            var f = new TemporalStableDetailGpuTests(output)
                { _reportOnly = true, _detail = TemporalHistoryDetail.Fine, _gate = gate };
            Capture(f);
            foreach (var (cell, display) in d._reported)
            {
                var fine = f._reported[cell];
                var before = Baseline[cell];
                output.WriteLine($"REPORT|stable texture|{gate}|{cell}/error|Display={display.Error:R}|Fine={fine.Error:R}|bound={before.Error * (1 + Tolerance):R}");
                output.WriteLine($"REPORT|stable texture|{gate}|{cell}/temporal|Display={display.TemporalError:R}|Fine={fine.TemporalError:R}|bound={before.TemporalError * (1 + Tolerance):R}");
            }
        }
        static void Capture(TemporalStableDetailGpuTests source)
        {
            source.A_slow_pan_over_the_textured_wall_resolves_as_before_the_release();
            source.Texture_detail_near_the_display_nyquist_resolves_as_before_the_release_at_quality();
        }
    }
}
