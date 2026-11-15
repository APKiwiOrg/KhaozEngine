using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Moving detail, matched-pose error, added change and sustained stop recovery of the fine store.</summary>
public sealed partial class TemporalMotionClarityGpuTests(TemporalMotionClarityScene scene, ITestOutputHelper output)
    : IClassFixture<TemporalMotionClarityScene>
{
    [GpuFact]
    public void Fine_history_improves_every_moving_detail_cell_within_the_existing_change_and_recovery_limits()
    {
        foreach (var path in new[] { MotionPath.Translation, MotionPath.Rotation })
            foreach (float speed in new[] { 0.5f, 1.37f, 3.3f })
                foreach (var preset in new[] { TemporalUpscale.Native, TemporalUpscale.Quality })
                {
                    var run = scene.Compare(preset, path, speed);
                    output.WriteLine($"DEVICE|{scene.Backend}|{scene.DeviceName}");
                    Print(preset, path, speed, run);
                    string cell = $"{preset} {path} {speed:R}";
                    Assert.True(run.Fine.Detail.Energy > run.Display.Detail.Energy,
                        $"{cell}: Fine energy {run.Fine.Detail.Energy:R}, Display {run.Display.Detail.Energy:R}, bound Fine > Display.");
                    Assert.True(run.Fine.Detail.Rms < run.Display.Detail.Rms,
                        $"{cell}: Fine RMS {run.Fine.Detail.Rms:R}, Display {run.Display.Detail.Rms:R}, bound Fine < Display.");
                    Change("detail", run.Fine.Detail, run.Display.Detail, run.Fxaa.Detail);
                    Change("texture", run.Fine.Texture, run.Display.Texture, run.Fxaa.Texture);
                    Assert.True(run.Fine.Detail.SustainedRecoveryFrames is int fine
                        && run.Display.Detail.SustainedRecoveryFrames is int display && fine <= display,
                        $"{cell}: Fine recovery {run.Fine.Detail.SustainedRecoveryFrames}, Display {run.Display.Detail.SustainedRecoveryFrames}, bound Fine <= Display.");
                    if (preset == TemporalUpscale.Quality)
                        Print(TemporalUpscale.Balanced, path, speed, scene.Compare(TemporalUpscale.Balanced, path, speed));

                    void Change(string region, TemporalMotionClarityMeasure fine, TemporalMotionClarityMeasure display,
                        TemporalMotionClarityMeasure fxaa)
                    {
                        double ceiling = System.Math.Max(display.AddedChange, fxaa.AddedChange);
                        Assert.True(fine.AddedChange <= ceiling,
                            $"{cell} {region}: Fine added {fine.AddedChange:R}, Display {display.AddedChange:R}, FXAA {fxaa.AddedChange:R}, bound {ceiling:R}.");
                    }
                }
    }

    void Print(TemporalUpscale preset, MotionPath path, float speed, TemporalMotionClarityScene.Comparison run)
    {
        Arm("Display", run.Display);
        Arm("Fine", run.Fine);
        Arm("FXAA", run.Fxaa);
        Arm("Reference", run.Reference);
        output.WriteLine($"SETTLED|{preset}|{path}|{speed:R}|Fine still-from-start={run.Settled.Detail.SettledSharpness:R}"
            + $"|Display stopped={run.Display.Detail.SettledSharpness:R}|Fine stopped={run.Fine.Detail.SettledSharpness:R}");
        void Arm(string arm, TemporalMotionClarityScene.Measures measures)
        {
            Region("detail", measures.Detail);
            Region("texture", measures.Texture);
            void Region(string region, TemporalMotionClarityMeasure m) => output.WriteLine(
                $"CELL|{preset}|{path}|{speed:R}|{arm}|{region}|energy={m.Energy:R}|sharpness={m.Sharpness:R}"
                + $"|RMS={m.Rms:R}|added={m.AddedChange:R}|removed={m.RemovedChange:R}"
                + $"|recovery={m.SustainedRecoveryFrames}|plateau={m.PlateauChange:R}|settled={m.SettledSharpness:R}");
        }
    }

    [GpuFact]
    public void An_aged_motion_scene_matches_a_fresh_scene()
    {
        scene.Run(TemporalUpscale.Native, MotionPath.Translation, 3.3f, TemporalHistoryDetail.Fine);
        scene.Run(TemporalUpscale.Native, MotionPath.Rotation, 0.5f, arm: "R");
        scene.Run(TemporalUpscale.Balanced, MotionPath.Rotation, 3.3f);
        scene.Run(TemporalUpscale.Native, MotionPath.Translation, 0.5f, arm: "X");
        var aged = scene.Run(TemporalUpscale.Quality, MotionPath.Rotation, 1.37f, TemporalHistoryDetail.Fine);
        using var fresh = new TemporalMotionClarityScene();
        var expected = fresh.Run(TemporalUpscale.Quality, MotionPath.Rotation, 1.37f, TemporalHistoryDetail.Fine);
        Assert.NotEmpty(expected.Images);
        Assert.Equal(expected.Images.Length, aged.Images.Length);
        Assert.Equal(expected.Measured.Frames, aged.Measured.Frames);
        for (int i = 0; i < expected.Images.Length; i++) Assert.Equal(expected.Images[i], aged.Images[i]);
        output.WriteLine($"Aged/fresh parity: {aged.Images.Length} complete RGBA frames and their measures match.");
    }
}
