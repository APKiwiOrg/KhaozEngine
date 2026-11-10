using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Daylight shadow following and its held control against a contemporaneous spatial reference.</summary>
[Collection("AllocSensitive")]
public sealed class TemporalDaylightShadowGpuTests(ITestOutputHelper output)
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
        var stage = new DaylightShadowStage(ridges);
        byte[][] frames = TemporalAcceptance.ReferenceSequence(DaylightShadowStage.W, DaylightShadowStage.H,
            s => stage.Setup(s, AntiAliasing.Off, TemporalUpscale.Native, 0f), stage.Draw,
            DaylightShadowStage.Warm, DaylightShadowStage.Count, factor: 4);
        var reference = DaylightShadowMeasure.Measure(frames, frames, new bool[frames.Length]);
        Print(preset, ridges, "reference", reference);
        var following = Capture(stage, frames, preset, ridges, "following");
        var held = Capture(stage, frames, preset, ridges, "held", temporalHold: 1f);
        output.WriteLine($"{preset}, ridges={ridges}: largest-change ratios F/R={following.MaxChange / reference.MaxChange:R}, "
            + $"H/R={held.MaxChange / reference.MaxChange:R}, F/H={following.MaxChange / held.MaxChange:R}, "
            + $"RMS F/H={following.RmsPixels / held.RmsPixels:R}");
        if (ridges)
        {
            // S uses the moving reference's band and stable-pixel mask, matching F, H and R exactly.
            var still = Capture(new DaylightShadowStage(ridges: true, moving: false), frames, preset, ridges, "still");
            double budget = reference.MaxChange + still.MaxChange;
            output.WriteLine($"{preset}: report-only additive ridged budget max(R)+max(S)={budget:R}, "
                + $"max(F)/budget={following.MaxChange / budget:R}, "
                + $"sparkle allowance={Math.Max(2, (int)Math.Ceiling(held.Sparkle * 0.02))}");
        }
        return (following, held, reference);
    }

    DaylightShadowMeasure.Result Capture(DaylightShadowStage stage, byte[][] reference, TemporalUpscale preset,
        bool ridges, string name, float? temporalHold = null)
    {
        using var fx = new TemporalFixture(DaylightShadowStage.W, DaylightShadowStage.H, s =>
        {
            stage.Setup(s, AntiAliasing.Temporal, preset, 1f);
            if (temporalHold is float hold) s.Post.Temporal.ShadowLightHoldTexels = hold;
        });
        fx.Frames(DaylightShadowStage.Warm, stage.Draw);
        var frames = new byte[DaylightShadowStage.Count][];
        var refits = new bool[frames.Length];
        int count = 0;
        for (int i = 0; i < frames.Length; i++)
        {
            frames[i] = fx.Frame(stage.Draw);
            refits[i] = fx.Scene.LastShadowPassDiagnostics.LightMatrixChanged;
            if (refits[i]) count++;
        }
        var measured = DaylightShadowMeasure.Measure(frames, reference, refits);
        output.WriteLine($"{preset} {name}, {fx.Device.Backend} on {fx.Device.Capabilities.DeviceName}: "
            + $"{count}/{frames.Length} re-fits, geometry {JsonSerializer.Serialize(stage.Geometry())}");
        Print(preset, ridges, name, measured);
        return measured;
    }

    void Print(TemporalUpscale preset, bool ridges, string name, DaylightShadowMeasure.Result measured) =>
        output.WriteLine($"DAYLIGHT|{preset}|{(ridges ? "ridges" : "plain")}|{name}|"
            + JsonSerializer.Serialize(measured, new JsonSerializerOptions
            { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals }));
}
