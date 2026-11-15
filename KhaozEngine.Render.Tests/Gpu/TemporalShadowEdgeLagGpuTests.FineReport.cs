using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalShadowEdgeLagGpuTests
{
    sealed record Measurements(Dictionary<Case, Dictionary<string, ShadowEdgeRegion>> Values,
        Dictionary<Case, double> Contrasts);
    Dictionary<Case, double> _contrasts = new();
    void AssertCaptureValidity()
    {
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

    [GpuFact]
    [Trait("Category", "Diagnostic")]
    public void Fine_reports_shadow_edge_lag_and_the_recorded_still_controls()
    {
        var d = new TemporalShadowEdgeLagGpuTests(scene, output) { _reportOnly = true };
        d.Shadow_edge_lag_metrics_under_a_turning_key_light_at_each_travel_rate(TemporalHistoryDetail.Display);
        foreach (var gate in TemporalDetailGateVariants.ReportGates)
        {
            var f = new TemporalShadowEdgeLagGpuTests(scene, output)
                { _reportOnly = true, _detail = TemporalHistoryDetail.Fine, _gate = gate };
            f.Shadow_edge_lag_metrics_under_a_turning_key_light_at_each_travel_rate(TemporalHistoryDetail.Fine);
            foreach (var c in Cases())
            {
                var dr = d._reported[c];
                var fr = f._reported[c];
                foreach (string region in dr.Keys) Cell(region + "/mean", dr[region].Mean, fr[region].Mean, null);
                if (c.Hold == 1f && c.PixelsPerFrame == 0f)
                {
                    Cell("band recorded", dr["band textured"].Mean, fr["band textured"].Mean, StillBand(c.Preset));
                    Cell("far recorded", dr["far textured"].Mean, fr["far textured"].Mean, StillFar(c.Preset));
                    foreach (string region in new[] { "band textured", "far textured" })
                        Cell(region + "/grain", dr[region].GrainOut / dr[region].GrainRef,
                            fr[region].GrainOut / fr[region].GrainRef, StillGrainShare);
                }
                if (c.Hold == 1f && MathF.Abs(c.PixelsPerFrame) == 1f)
                {
                    var slow = c with { Name = c.Name.Replace("1.00", "0.25"), PixelsPerFrame = c.PixelsPerFrame / 4f };
                    Cell("tail/slow", dr["tail textured"].Mean / d._reported[slow]["tail textured"].Mean,
                        fr["tail textured"].Mean / f._reported[slow]["tail textured"].Mean, TravelTailRatio);
                    if (c.Preset == TemporalUpscale.Quality)
                        Cell("band", dr["band textured"].Mean, fr["band textured"].Mean, QualityBandLimit);
                }
                void Cell(string metric, double dv, double fv, double? bound) => output.WriteLine(
                    $"REPORT|shadow edge lag|{gate}|{c.Name}/{metric}|Display={dv:R}|Fine={fv:R}|bound={bound:R}");
            }
        }
    }
}
