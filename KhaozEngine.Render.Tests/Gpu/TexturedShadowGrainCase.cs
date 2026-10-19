using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Diagonal texture ridges under a moving coarse shadow, from archive c1d32238a without its switches.</summary>
internal static class TexturedShadowGrainCase
{
    internal static byte[] RidgeTexture()
    {
        var rgba = new byte[FrontStage.TextureSize * FrontStage.TextureSize * 4];
        for (int y = 0; y < FrontStage.TextureSize; y++)
            for (int x = 0; x < FrontStage.TextureSize; x++)
            {
                int i = (y * FrontStage.TextureSize + x) * 4;
                rgba[i] = rgba[i + 1] = rgba[i + 2] = (x + y) % 8 == 0 ? (byte)255 : (byte)32;
                rgba[i + 3] = 255;
            }
        return rgba;
    }

    internal static void AssertGrain(GameShadowGrainScene scene, ITestOutputHelper output)
    {
        byte[][] reference = scene.Run(4f, 0f, AntiAliasing.Off, TemporalUpscale.Native, false,
            reference: true, textured: true).Frames;
        var failures = new List<string>();
        foreach (TemporalUpscale preset in new[] { TemporalUpscale.Native, TemporalUpscale.Quality, TemporalUpscale.Balanced })
        {
            byte[][] frames = scene.Run(4f, 1f, AntiAliasing.Temporal, preset, false, textured: true).Frames;
            var m = new GameShadowGrainMeasure(frames, reference, false, bandPixels: 12f);
            // Quality's archive baseline is 0.010852, versus 0.006727 without locks. Native's baseline is
            // 0.012026. These absolute bounds catch the held bright pixels without disabling thin-feature locks.
            // Balanced is reported only. Its reconstruction and shading-release gate are a separate issue.
            // Owner accepted on 2026-10-19 after real-scene clips: the visibility lock release removes held shadow
            // sparkle, and this synthetic value is the measured cost of that release. No large-change response ships.
            // Quality was 0.008. D measures 0.008031049526854536, rounded up to the six-decimal reporting place.
            double bound = preset == TemporalUpscale.Quality ? 0.008032 : 0.0113;
            output.WriteLine($"Textured grain {preset}, {scene.Backend} on {scene.DeviceName}, {TemporalDeviceClass.Name}: {m.Band.GrainOut:0.000000}, "
                + $"error grain {m.Band.ErrorGrain:0.000000}, reference grain {m.Band.GrainRef:0.000000}, "
                + $"change {m.Band.Change:0.000000}, bound {(preset == TemporalUpscale.Balanced ? "report only" : bound.ToString("0.000000"))}.");
            output.WriteLine($"GRAIN-EXACT|{TemporalDeviceClass.Name}|{preset}|{m.Band.GrainOut:R}");
            Assert.True(m.Band.Count > 0 && m.Contrast > 0.04, "The textured receiver must render a measured shadow edge.");
            if (preset != TemporalUpscale.Balanced && !(m.Band.GrainOut <= bound))
                failures.Add($"{preset} grain {m.Band.GrainOut:0.000000} exceeds {bound:0.000000}.");
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
