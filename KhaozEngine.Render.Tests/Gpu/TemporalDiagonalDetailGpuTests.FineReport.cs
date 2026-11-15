using System.Collections.Generic;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalDiagonalDetailGpuTests
{
    bool _reportOnly;
    TemporalHistoryDetail _detail;
    TemporalDetailGate _gate;
    readonly Dictionary<string, TemporalReviewFigures> _reported = new();

    [GpuFact]
    [Trait("Category", "Diagnostic")]
    public void Fine_reports_diagonal_detail_with_the_existing_control_bounds()
    {
        var d = new TemporalDiagonalDetailGpuTests(output) { _reportOnly = true };
        d.Diagonal_sub_texel_lines_on_texture_keep_their_history_still_and_under_a_slow_pan();
        foreach (var gate in new[] { TemporalDetailGate.Production, TemporalDetailGate.Narrower, TemporalDetailGate.Wider })
        {
            var f = new TemporalDiagonalDetailGpuTests(output)
                { _reportOnly = true, _detail = TemporalHistoryDetail.Fine, _gate = gate };
            f.Diagonal_sub_texel_lines_on_texture_keep_their_history_still_and_under_a_slow_pan();
            foreach (var (name, display) in d._reported)
            {
                var fine = f._reported[name];
                var bounds = TemporalReviewBaseline.ReportLimits("diagonal", name);
                Cell("error", display.Error, fine.Error, bounds.Error);
                Cell("change", display.Change, fine.Change, bounds.Change);
                Cell("temporal", display.Temporal, fine.Temporal, bounds.Temporal);
                void Cell(string metric, double dv, double fv, double? bound) => output.WriteLine(
                    $"REPORT|diagonal|{gate}|{name}/{metric}|Display={dv:R}|Fine={fv:R}|bound={bound:R}");
            }
        }
    }
}
