using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalShadowEdgeLagGpuTests
{
    bool _reportOnly;
    TemporalHistoryDetail _detail;
    TemporalDetailGate _gate;
    Dictionary<Case, Dictionary<string, ShadowEdgeRegion>> _reported = new();

    void Detail(Scene3D scene)
    {
        scene.Post.Temporal.HistoryDetail = _detail;
        if (_detail == TemporalHistoryDetail.Fine && _gate != TemporalDetailGate.Production)
            scene.InstallTemporalResolveForTests(source => TemporalDetailGateVariants.Rewrite(source, _gate));
    }

    [GpuFact]
    [Trait("Category", "Diagnostic")]
    public void Fine_reports_shadow_edge_lag_and_the_recorded_still_controls()
    {
        var d = new TemporalShadowEdgeLagGpuTests(output) { _reportOnly = true };
        d.Shadow_edge_lag_metrics_under_a_turning_key_light_at_each_travel_rate();
        foreach (var gate in new[] { TemporalDetailGate.Production, TemporalDetailGate.Narrower, TemporalDetailGate.Wider })
        {
            var f = new TemporalShadowEdgeLagGpuTests(output)
                { _reportOnly = true, _detail = TemporalHistoryDetail.Fine, _gate = gate };
            f.Shadow_edge_lag_metrics_under_a_turning_key_light_at_each_travel_rate();
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
