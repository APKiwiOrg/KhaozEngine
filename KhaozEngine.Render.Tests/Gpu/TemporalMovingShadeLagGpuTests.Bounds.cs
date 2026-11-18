using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalMovingShadeLagGpuTests
{
    readonly record struct LagBound(string Case, string Metric, double Value, double Limit, bool Live, bool Passed, string Failure);

    static IReadOnlyList<LagBound> EvaluateBounds(Dictionary<Case, Dictionary<string, ShadowEdgeRegion>> measured,
        Dictionary<Case, Dictionary<string, ShadowEdgeRegion>> controls)
    {
        var values = new List<LagBound>();
        string name = "", metric = "";
        double value = 0, limit = 0;
        bool live = true;
        foreach ((Case c, Dictionary<string, ShadowEdgeRegion> r) in measured)
        {
            name = c.Name;
            ShadowEdgeRegion band = r["band textured"];
            if (c.Kind == MovingShadeKind.Shadow ? MathF.Abs(c.Shadow) > 1f : c.Motion >= 1f)
            {
                metric = "band grain/ref"; value = band.GrainOut / band.GrainRef; limit = BandGrainRatio; live = true;
                Check(band.GrainOut <= limit * band.GrainRef,
                    $"{c.Name}: textured band grain {band.GrainOut:0.0000} above {BandGrainRatio} times the "
                    + $"reference's {band.GrainRef:0.0000}");
            }
            double tail = r["tail textured"].Mean;
            if (c.Kind == MovingShadeKind.Shadow && MathF.Abs(c.Shadow) > 1f)
            {
                Case slow = c with
                {
                    Name = c.Name.Replace($"{MathF.Abs(c.Shadow):0.0} px", "1.0 px"),
                    Shadow = c.Sign
                };
                double slowTail = measured[slow]["tail textured"].Mean;
                if (MathF.Abs(c.Shadow) == 8f)
                    foreach (string region in new[] { "tail textured", "tail flat" })
                    {
                        double actual = r[region].Mean, before = controls[c][region].Mean;
                        // Owner accepted on 2026-10-19 after real-scene clips: the visibility lock release removes held shadow
                        // sparkle, and this synthetic value is the measured cost of that release. No large-change response ships.
                        // Owner accepted per driver on 2026-10-19 after hosted run 39215298727: WARP and lavapipe show the same lock-release costs.
                        double baseline = OwnerAcceptedTailBaseline.TryGetValue((TemporalDeviceClass.Name, c.Name, region), out double accepted)
                            ? accepted : before;
                        metric = region + "/absolute"; value = actual; limit = baseline + 0.0000005; live = false;
                        Check(double.IsFinite(actual) && actual <= limit,
                            $"{c.Name}: absolute {region} {actual:R} above baseline {baseline:R}, paired bilinear control {before:R}, "
                            + $"1 px denominator {measured[slow][region].Mean:R}, control {controls[slow][region].Mean:R}");
                    }
                // Owner ruling 2026-10-29, precision-safe chroma floor. Only this Native advancing case
                // takes its measured per-driver ratio. Other speeds, directions and presets keep FastTailRatio.
                double fastTailRatio = c.Name == "Native S 8.0 px advancing"
                    ? TemporalDeviceClass.Name switch
                    {
                        // Metal: 0.004814773364462807 / 0.0029890602170812297, 7.39 percent above D.
                        "hardware" => 1.610798383033032,
                        // WARP: 0.004802969203941581 / 0.0029910308436487004, 7.05 percent above D.
                        "WARP" => 1.605790596957714,
                        // Lavapipe: 0.0047991955092875314 / 0.0030566843860575796, 4.67 percent above D.
                        "lavapipe" => 1.570065765107464,
                        _ => FastTailRatio,
                    } : FastTailRatio;
                metric = "tail/slow"; value = tail / slowTail; limit = fastTailRatio;
                live = c.Name != "Native S 8.0 px advancing";
                Check(tail <= limit * slowTail,
                    $"{c.Name}: textured tail {tail:0.0000} above {fastTailRatio} times the 1 px tail {slowTail:0.0000}");
            }
            else if (c.Kind == MovingShadeKind.Object && c.Motion >= 1f)
            {
                double far = r["far textured"].Mean;
                metric = "tail/far excess"; value = tail - far; limit = MovingTailExcess; live = true;
                Check(tail <= far + limit,
                    $"{c.Name}: textured tail {tail:0.0000} more than {MovingTailExcess} above its control {far:0.0000}");
            }
            else if (c.Kind == MovingShadeKind.Pan)
            {
                (double before, double beforeTail) = Control[c.Name];
                metric = "band recorded"; value = band.Mean; limit = (1 + ControlTolerance) * before; live = false;
                Check(band.Mean <= limit,
                    $"{c.Name}: textured band {band.Mean:0.0000} above {before:0.0000} plus a tenth");
                metric = "tail recorded"; value = tail; limit = (1 + ControlTolerance) * beforeTail;
                Check(tail <= limit,
                    $"{c.Name}: textured tail {tail:0.0000} above {beforeTail:0.0000} plus a tenth");
            }
        }
        return values;

        void Check(bool ok, string failure)
        {
            values.Add(new(name, metric, value, limit, live, ok, failure));
        }
    }
}
