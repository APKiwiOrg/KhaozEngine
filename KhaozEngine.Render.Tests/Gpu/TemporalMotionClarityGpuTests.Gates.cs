using System.Linq;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class TemporalMotionClarityGpuTests
{
    [GpuFact]
    [Trait("Category", "Diagnostic")]
    public void The_three_gates_report_detail_change_recovery_and_fine_path_share()
    {
        foreach (var path in new[] { MotionPath.Translation, MotionPath.Rotation })
            foreach (float speed in new[] { 0.5f, 1.37f, 3.3f })
            {
                var reference = scene.Run(TemporalUpscale.Native, path, speed, arm: "R");
                var fxaa = scene.Run(TemporalUpscale.Native, path, speed, arm: "X", reference: reference.Images);
                foreach (var preset in new[] { TemporalUpscale.Native, TemporalUpscale.Quality, TemporalUpscale.Balanced })
                {
                    var display = scene.Run(preset, path, speed, reference: reference.Images);
                    foreach (var gate in TemporalDetailGateVariants.ReportGates)
                    {
                        var fine = scene.ForGate(gate).Run(preset, path, speed, TemporalHistoryDetail.Fine,
                            reference: reference.Images, collectDecisions: true);
                        double share = fine.Measured.Frames.Skip(64).Take(32).Average(frame => frame.Diagnostics!.FineShare);
                        Region("detail", display.Measured.Detail, fine.Measured.Detail, fxaa.Measured.Detail);
                        Region("texture", display.Measured.Texture, fine.Measured.Texture, fxaa.Measured.Texture);
                        void Region(string region, TemporalMotionClarityMeasure d, TemporalMotionClarityMeasure f,
                            TemporalMotionClarityMeasure x) => output.WriteLine(
                            $"GATE|{gate}|{preset}|{path}|{speed:R}|{region}|Display energy={d.Energy:R}|Fine energy={f.Energy:R}"
                            + $"|Display RMS={d.Rms:R}|Fine RMS={f.Rms:R}|Display added={d.AddedChange:R}|Fine added={f.AddedChange:R}"
                            + $"|FXAA added={x.AddedChange:R}|ceiling={System.Math.Max(d.AddedChange, x.AddedChange):R}"
                            + $"|Display recovery={d.SustainedRecoveryFrames}|Fine recovery={f.SustainedRecoveryFrames}|fine share={share:R}");
                    }
                }
            }
    }
}
