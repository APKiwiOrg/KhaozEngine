using System.Linq;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalMovingShadeRimGpuTests
{
    [GpuFact]
    [Trait("Category", "Diagnostic")]
    public void Fine_reports_moving_shade_rims_against_the_existing_controls()
    {
        foreach (var preset in new[] { TemporalUpscale.Quality, TemporalUpscale.Native })
            foreach (float shadow in new[] { 0f, 1f })
                foreach (float motion in shadow == 0f ? new[] { 0.5f, 1f, 2f, 4f, 8f } : new[] { 1f, 2f })
                {
                    string name = $"{preset} {(shadow == 0f ? "M" : "MS")} {motion:0.0}";
                    byte[][] reference = scene.Capture(preset, shadow, motion, reference: true);
                    var display = Measure(scene.Capture(preset, shadow, motion, reference: false), reference, motion,
                        validate: false).ToDictionary(row => row.Item1, row => row.Item2);
                    foreach (var gate in new[] { TemporalDetailGate.Production, TemporalDetailGate.Narrower, TemporalDetailGate.Wider })
                    {
                        var source = scene.For(TemporalHistoryDetail.Fine, gate);
                        var measured = Measure(source.Capture(preset, shadow, motion, reference: false), reference, motion,
                            validate: false);
                        foreach (var (rim, fine) in measured)
                        {
                            var d = display[rim];
                            var bounds = TemporalReviewBaseline.ReportLimits("rim", name + " " + rim);
                            Cell("error", d.Error, fine.Error, bounds.Error);
                            Cell("temporal", d.Temporal, fine.Temporal, bounds.Temporal);
                            Cell("raw change", d.Change, fine.Change, null);
                            void Cell(string metric, double dv, double fv, double? bound) => output.WriteLine(
                                $"REPORT|moving shade rim|{gate}|{name}/{rim}/{metric}|Display={dv:R}|Fine={fv:R}|bound={bound:R}");
                        }
                    }
                }
    }
}
