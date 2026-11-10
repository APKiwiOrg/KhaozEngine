using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Daylight shadow following and its held control against a contemporaneous spatial reference.</summary>
[Collection("AllocSensitive")]
public sealed class TemporalDaylightShadowGpuTests(DaylightShadowScene scene, ITestOutputHelper output)
    : IClassFixture<DaylightShadowScene>
{
    [GpuTheory, InlineData(TemporalUpscale.Native), InlineData(TemporalUpscale.Quality)]
    public void A_daylight_shadow_edge_creeps_without_re_fit_pops(TemporalUpscale preset)
    {
        var (following, held, reference) = Measure(preset, ridges: false);
        if (preset == TemporalUpscale.Quality) Measure(TemporalUpscale.Balanced, ridges: false);
        AssertReceiver(reference);
        float k = preset == TemporalUpscale.Native ? 2.0f : 1.25f;
        Assert.True(following.MaxChange <= k * reference.MaxChange);
        Assert.True(held.MaxChange > k * reference.MaxChange);
        Assert.True(following.RmsPixels <= held.RmsPixels);
        Assert.True(following.Sparkle <= held.Sparkle);
    }

    [GpuTheory, InlineData(TemporalUpscale.Native), InlineData(TemporalUpscale.Quality)]
    public void A_daylight_shadow_over_shimmering_ridges_adds_no_pops_or_sparkle(TemporalUpscale preset)
    {
        var (following, held, reference) = Measure(preset, ridges: true);
        if (preset == TemporalUpscale.Quality) Measure(TemporalUpscale.Balanced, ridges: true);
        AssertReceiver(reference);
        Assert.True(following.MaxChange < held.MaxChange);
        int allowance = Math.Max(2, (int)Math.Ceiling(held.Sparkle * 0.02));
        Assert.True(following.Sparkle <= held.Sparkle + allowance);
    }

    static void AssertReceiver(DaylightShadowMeasure.Result reference)
    {
        Assert.True(reference.ReferenceContrast > 0.05);
        Assert.True(reference.BandPixels > 0 && reference.StableReferencePixels > 0);
    }

    (DaylightShadowMeasure.Result Following, DaylightShadowMeasure.Result Held, DaylightShadowMeasure.Result Reference)
        Measure(TemporalUpscale preset, bool ridges)
    {
        byte[][] frames = scene.Run(ridges, TemporalUpscale.Native, reference: true).Frames;
        var reference = DaylightShadowMeasure.Measure(frames, frames, new bool[frames.Length]);
        Print(preset, ridges, "reference", reference);
        var following = Capture(frames, preset, ridges, "following");
        var held = Capture(frames, preset, ridges, "held", temporalHold: 1f);
        output.WriteLine($"{preset}, ridges={ridges}: largest-change ratios F/R={following.MaxChange / reference.MaxChange:R}, "
            + $"H/R={held.MaxChange / reference.MaxChange:R}, F/H={following.MaxChange / held.MaxChange:R}, "
            + $"RMS F/H={following.RmsPixels / held.RmsPixels:R}");
        if (ridges)
        {
            // S uses the moving reference's band and stable-pixel mask, matching F, H and R exactly.
            var still = Capture(frames, preset, ridges, "still", moving: false);
            double budget = reference.MaxChange + still.MaxChange;
            output.WriteLine($"{preset}: report-only additive ridged budget max(R)+max(S)={budget:R}, "
                + $"max(F)/budget={following.MaxChange / budget:R}, "
                + $"sparkle allowance={Math.Max(2, (int)Math.Ceiling(held.Sparkle * 0.02))}");
        }
        return (following, held, reference);
    }

    [GpuFact]
    public void An_aged_daylight_scene_matches_a_fresh_scene_after_configuration_changes()
    {
        scene.Run(false, TemporalUpscale.Native, temporalHold: 1f);
        scene.Run(true, TemporalUpscale.Native, reference: true);
        scene.Run(true, TemporalUpscale.Balanced, moving: false);
        scene.Run(false, TemporalUpscale.Quality, temporalHold: 1f);
        DaylightShadowScene.Capture aged = scene.Run(true, TemporalUpscale.Quality);
        using var fresh = new DaylightShadowScene();
        DaylightShadowScene.Capture expected = fresh.Run(true, TemporalUpscale.Quality);
        Assert.NotEmpty(expected.Frames);
        Assert.Equal(expected.Frames.Length, aged.Frames.Length);
        Assert.Equal(expected.Refits, aged.Refits);
        for (int i = 0; i < expected.Frames.Length; i++) Assert.Equal(expected.Frames[i], aged.Frames[i]);
        output.WriteLine($"Aged/fresh parity: {aged.Frames.Length} frames and re-fit flags match byte for byte.");
    }

    DaylightShadowMeasure.Result Capture(byte[][] reference, TemporalUpscale preset,
        bool ridges, string name, float? temporalHold = null, bool moving = true)
    {
        DaylightShadowScene.Capture capture = scene.Run(ridges, preset, temporalHold, moving);
        var measured = DaylightShadowMeasure.Measure(capture.Frames, reference, capture.Refits);
        int count = 0;
        foreach (bool refit in capture.Refits) if (refit) count++;
        output.WriteLine($"{preset} {name}, {scene.Backend} on {scene.DeviceName}: "
            + $"{count}/{capture.Frames.Length} re-fits, geometry {JsonSerializer.Serialize(scene.Geometry(ridges))}");
        Print(preset, ridges, name, measured);
        return measured;
    }

    void Print(TemporalUpscale preset, bool ridges, string name, DaylightShadowMeasure.Result measured) =>
        output.WriteLine($"DAYLIGHT|{preset}|{(ridges ? "ridges" : "plain")}|{name}|"
            + JsonSerializer.Serialize(measured, new JsonSerializerOptions
            { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
}
