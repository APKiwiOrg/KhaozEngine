using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalMovingShadeLagGpuTests
{
    sealed record Measurements(Dictionary<Case, Dictionary<string, ShadowEdgeRegion>> Values,
        Dictionary<Case, Dictionary<string, ShadowEdgeRegion>> Controls,
        Dictionary<Case, double> Contrasts,
        Dictionary<Case, (bool Valid, float Jitter)> ControlValidity);
    Dictionary<Case, (bool Valid, float Jitter)> _controlValidity = new();
    Dictionary<Case, double> _contrasts = new();
    void AssertCaptureValidity()
    {
        foreach (var control in _controlValidity.Values)
        {
            Assert.True(control.Valid, "The paired control must retain colour history.");
            Assert.Equal(0, control.Jitter);
        }
        foreach (var (c, regions) in _reported)
        {
            foreach (var (name, region) in regions)
                Assert.True(region.Count > 0, $"{c.Name}: the {name} region holds no pixels");
            Assert.True(_contrasts[c] > 0.05,
                $"{c.Name}: the reference shows no shadow edge, contrast {_contrasts[c]:0.000}");
        }
    }
    bool _reportOnly;
    TemporalHistoryDetail _detail;
    TemporalDetailGate _gate;
    Dictionary<Case, Dictionary<string, ShadowEdgeRegion>> _reported = new();
    Dictionary<Case, Dictionary<string, ShadowEdgeRegion>> _paired = new();

    [GpuFact]
    [Trait("Category", "Diagnostic")]
    public void Fine_reports_moving_shade_lag_without_rebasing_the_controls()
    {
        var d = new TemporalMovingShadeLagGpuTests(scene, output) { _reportOnly = true };
        d.Shadow_edge_lag_on_moving_pixels_and_fast_shadows_stays_within_its_bounds(TemporalHistoryDetail.Display);
        foreach (var gate in TemporalDetailGateVariants.ReportGates)
        {
            var f = new TemporalMovingShadeLagGpuTests(scene, output)
                { _reportOnly = true, _detail = TemporalHistoryDetail.Fine, _gate = gate };
            f.Shadow_edge_lag_on_moving_pixels_and_fast_shadows_stays_within_its_bounds(TemporalHistoryDetail.Fine);
            foreach (var c in Cases())
            {
                var dr = d._reported[c];
                var fr = f._reported[c];
                foreach (string region in dr.Keys) Cell(region + "/mean", dr[region].Mean, fr[region].Mean, null);
                var displayBounds = EvaluateBounds(d._reported, d._paired);
                foreach (var bound in EvaluateBounds(f._reported, f._paired))
                {
                    if (bound.Case != c.Name) continue;
                    var display = System.Linq.Enumerable.First(displayBounds,
                        v => v.Case == bound.Case && v.Metric == bound.Metric);
                    Cell(bound.Metric, display.Value, bound.Value, bound.Limit);
                }
                void Cell(string metric, double dv, double fv, double? bound) => output.WriteLine(
                    $"REPORT|moving shade lag|{gate}|{c.Name}/{metric}|Display={dv:R}|Fine={fv:R}|bound={bound:R}");
            }
        }
    }
}
