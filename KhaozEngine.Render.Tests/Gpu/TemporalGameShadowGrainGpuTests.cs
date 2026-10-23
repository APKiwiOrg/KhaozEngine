using System;
using System.Linq;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// Diagnosis, not an acceptance bound. Default 2048 by three shadows, five display pixels per cascade 0 texel,
/// a uniform flat-shaded 16-sided torso, a still close camera, and a held turning key light. The sinusoidal keyed
/// receiver sway peaks at 0.2 display pixels per frame. The spatial reference is 4x per axis with temporal and
/// light hold off. FXAA renders the identical path and hold history, including warmup. The band follows each
/// reference column's half-contrast crossing, avoiding the normal offset's face-dependent displacement.
/// </summary>
public sealed class TemporalGameShadowGrainGpuTests(GameShadowGrainScene scene, ITestOutputHelper output)
    : IClassFixture<GameShadowGrainScene>
{
    [GpuFact]
    public void Coarse_shadow_texels_on_a_swaying_faceted_receiver_report_grain_against_fxaa()
    {
        (int cascade, float texel, float holdPixels) = GameShadowGrainScene.Geometry();
        Assert.Equal(0, cascade);
        Assert.InRange(texel, 4f, 6f);
        const bool sway = true;
        output.WriteLine($"{GameShadowGrainScene.W}x{GameShadowGrainScene.H}, {scene.Backend} on {scene.DeviceName}, {TemporalDeviceClass.Name}, cascade {cascade}, "
            + $"texel {texel:0.00} px, predicted hold threshold {holdPixels:0.00} px, sway peak {(sway ? 0.2 : 0):0.0} px/frame.");
        output.WriteLine($"Frames {GameShadowGrainScene.Warm} to {GameShadowGrainScene.Warm + GameShadowGrainScene.Count - 1}, "
            + $"band +/-{GameShadowGrainMeasure.BandPixels} px, control beyond +/-{GameShadowGrainMeasure.FarPixels} px.");
        output.WriteLine("Luma units 0..1. Grain is absolute output minus its 3x3 median. Width is mean 10..90% contrast px.");
        output.WriteLine("rate hold mode bandErr p95 grainOut grainRef grainFXAA grainError change changeRef changeErr "
            + "farErr farGrain farGrainRef farGrainFXAA farChange farChangeRef farChangeErr width");
        foreach (float rate in new[] { 0f, 1f, 4f, 8f })
        {
            byte[][] reference = scene.Run(rate, 0f, AntiAliasing.Off, TemporalUpscale.Native, sway, reference: true).Frames;
            foreach (float hold in new[] { 1f, 0f })
            {
                GameShadowGrainScene.Capture fxaa = scene.Run(rate, hold, AntiAliasing.Fxaa, TemporalUpscale.Native, sway);
                var fm = new GameShadowGrainMeasure(fxaa.Frames, reference, sway);
                foreach (TemporalUpscale preset in new[] { TemporalUpscale.Quality, TemporalUpscale.Native })
                {
                    GameShadowGrainScene.Capture capture = scene.Run(rate, hold, AntiAliasing.Temporal, preset, sway);
                    var m = new GameShadowGrainMeasure(capture.Frames, reference, sway);
                    Print(rate, hold, preset.ToString(), m, fm, capture.Refits);
                }
                Print(rate, hold, "FXAA", fm, fm, fxaa.Refits);
            }
        }
    }

    [GpuFact]
    public void Texture_ridges_under_a_coarse_moving_shadow_do_not_hold_bright_grain()
        => TexturedShadowGrainCase.AssertGrain(scene, output);

    void Print(float rate, float hold, string name, GameShadowGrainMeasure m, GameShadowGrainMeasure fxaa, bool[] refits)
    {
        ShadowEdgeRegion b = m.Band, f = m.Far;
        output.WriteLine($"{rate,4:0} {hold,4:0} {name,-7} {b.Mean:0.00000} {b.P95:0.0000} {b.GrainOut:0.00000} "
            + $"{b.GrainRef:0.00000} {fxaa.Band.GrainOut:0.00000} {b.ErrorGrain:0.00000} {b.Change:0.00000} {b.ChangeRef:0.00000} "
            + $"{b.ChangeError:0.00000} {f.Mean:0.00000} {f.GrainOut:0.00000} {f.GrainRef:0.00000} {fxaa.Far.GrainOut:0.00000} "
            + $"{f.Change:0.00000} {f.ChangeRef:0.00000} {f.ChangeError:0.00000} {m.Penumbra:0.00}");
        output.WriteLine($"  refit {string.Concat(refits.Select(r => r ? '1' : '0'))}, contrast {m.Contrast:0.000}, "
            + $"band/control pixels {b.Count}/{f.Count}.");
        Assert.True(b.Count > 0 && f.Count > 0, "The measured receiver regions must contain pixels.");
        Assert.True(m.Contrast > 0.04, "The reference must render a shadow edge on the receiver.");
        Assert.True(double.IsFinite(m.Penumbra), "The measured image must render a shadow transition.");
    }

    [GpuFact]
    public void Shared_scene_matches_a_fresh_scene_after_changes_of_size_hold_and_temporal_mode()
    {
        scene.Run(8f, 1f, AntiAliasing.Temporal, TemporalUpscale.Quality, true);
        scene.Run(4f, 0f, AntiAliasing.Fxaa, TemporalUpscale.Native, false, reference: true);
        byte[][] reused = scene.Run(1f, 1f, AntiAliasing.Temporal, TemporalUpscale.Native, true).Frames;
        using var fresh = new GameShadowGrainScene();
        byte[][] expected = fresh.Run(1f, 1f, AntiAliasing.Temporal, TemporalUpscale.Native, true).Frames;
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], reused[i]);
    }
}
