using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalMovingShadeLagGpuTests
{
    bool _reportOnly;
    TemporalHistoryDetail _detail;
    TemporalDetailGate _gate;
    Dictionary<Case, Dictionary<string, ShadowEdgeRegion>> _reported = new();
    Dictionary<Case, Dictionary<string, ShadowEdgeRegion>> _paired = new();

    void Detail(Scene3D scene)
    {
        scene.Post.Temporal.HistoryDetail = _detail;
        if (_detail == TemporalHistoryDetail.Fine && _gate != TemporalDetailGate.Production)
            scene.InstallTemporalResolveForTests(source => TemporalDetailGateVariants.Rewrite(source, _gate));
    }

    [GpuFact]
    [Trait("Category", "Diagnostic")]
    public void Fine_reports_moving_shade_lag_without_rebasing_the_controls()
    {
        var d = new TemporalMovingShadeLagGpuTests(output) { _reportOnly = true };
        d.Shadow_edge_lag_on_moving_pixels_and_fast_shadows_stays_within_its_bounds();
        foreach (var gate in new[] { TemporalDetailGate.Production, TemporalDetailGate.Narrower, TemporalDetailGate.Wider })
        {
            var f = new TemporalMovingShadeLagGpuTests(output)
                { _reportOnly = true, _detail = TemporalHistoryDetail.Fine, _gate = gate };
            f.Shadow_edge_lag_on_moving_pixels_and_fast_shadows_stays_within_its_bounds();
            foreach (var c in Cases())
            {
                var dr = d._reported[c];
                var fr = f._reported[c];
                foreach (string region in dr.Keys) Cell(region + "/mean", dr[region].Mean, fr[region].Mean, null);
                if (c.Kind == MovingShadeKind.Shadow ? MathF.Abs(c.Shadow) > 1f : c.Motion >= 1f)
                    Cell("band grain/ref", dr["band textured"].GrainOut / dr["band textured"].GrainRef,
                        fr["band textured"].GrainOut / fr["band textured"].GrainRef, BandGrainRatio);
                if (c.Kind == MovingShadeKind.Shadow && MathF.Abs(c.Shadow) > 1f)
                {
                    var slow = c with { Name = c.Name.Replace($"{MathF.Abs(c.Shadow):0.0} px", "1.0 px"), Shadow = c.Sign };
                    double ratio = c.Name == "Native S 8.0 px advancing" ? TemporalDeviceClass.Name switch
                    {
                        "hardware" => 1.610798383033032,
                        "WARP" => 1.605790596957714,
                        "lavapipe" => 1.570065765107464,
                        _ => FastTailRatio,
                    } : FastTailRatio;
                    Cell("tail/slow", dr["tail textured"].Mean / d._reported[slow]["tail textured"].Mean,
                        fr["tail textured"].Mean / f._reported[slow]["tail textured"].Mean, ratio);
                    if (MathF.Abs(c.Shadow) == 8f)
                        foreach (string region in new[] { "tail textured", "tail flat" })
                        {
                            double before = d._paired[c][region].Mean;
                            double baseline = OwnerAcceptedTailBaseline.TryGetValue((TemporalDeviceClass.Name, c.Name, region), out double accepted)
                                ? accepted : before;
                            Cell(region + "/absolute", dr[region].Mean, fr[region].Mean, baseline + 0.0000005);
                        }
                }
                else if (c.Kind == MovingShadeKind.Object && c.Motion >= 1f)
                    Cell("tail/far excess", dr["tail textured"].Mean - dr["far textured"].Mean,
                        fr["tail textured"].Mean - fr["far textured"].Mean, MovingTailExcess);
                else if (c.Kind == MovingShadeKind.Pan)
                {
                    var before = Control[c.Name];
                    Cell("band recorded", dr["band textured"].Mean, fr["band textured"].Mean, (1 + ControlTolerance) * before.Band);
                    Cell("tail recorded", dr["tail textured"].Mean, fr["tail textured"].Mean, (1 + ControlTolerance) * before.Tail);
                }
                void Cell(string metric, double dv, double fv, double? bound) => output.WriteLine(
                    $"REPORT|moving shade lag|{gate}|{c.Name}/{metric}|Display={dv:R}|Fine={fv:R}|bound={bound:R}");
            }
        }
    }
}
