using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>A paired diagnostic, with identical geometry, shadow history and jitter phases in each arm.</summary>
internal static class TexturedShadowGrainProbe
{
    // Diagonal one-texel lines leave genuine subpixel ridges that a jitter phase can miss.
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

    internal static void Measure(ITestOutputHelper output)
    {
        int requested = TemporalResolveDiagnostics.Mask;
        int filter = requested & TemporalResolveDiagnostics.ShadowFilterMask;
        try
        {
            TemporalResolveDiagnostics.OverrideMaskForMeasurement(TemporalResolveDiagnostics.NoOp | filter);
            using var scene = new GameShadowGrainScene();
            byte[][] reference = scene.Run(4f, 0f, AntiAliasing.Off, TemporalUpscale.Native, false,
                reference: true, textured: true).Frames;
            byte[][] stillReference = scene.Run(0f, 0f, AntiAliasing.Off, TemporalUpscale.Native, false,
                reference: true, textured: true).Frames;
            var failures = new List<string>();
            foreach (TemporalUpscale preset in new[] { TemporalUpscale.Native, TemporalUpscale.Quality })
            {
                ShadowEdgeRegion Grain(int mask, float rate = 4f)
                {
                    TemporalResolveDiagnostics.OverrideMaskForMeasurement(mask | filter);
                    byte[][] frames = scene.Run(rate, 1f, AntiAliasing.Temporal, preset, false, textured: true).Frames;
                    return new GameShadowGrainMeasure(frames, rate == 0f ? stillReference : reference, false, bandPixels: 12f).Band;
                }
                ShadowEdgeRegion b = Grain(TemporalResolveDiagnostics.NoOp);
                ShadowEdgeRegion u = Grain(TemporalResolveDiagnostics.LocksOff);
                double baseline = b.GrainOut, unlocked = u.GrainOut;
                double candidate = requested == 0 ? baseline : Grain(requested).GrainOut;
                double stillBaseline = Grain(TemporalResolveDiagnostics.NoOp, 0f).GrainOut;
                double stillUnlocked = Grain(TemporalResolveDiagnostics.LocksOff, 0f).GrainOut;
                output.WriteLine($"  {preset} moving error grain baseline/off {b.ErrorGrain:0.000000}/{u.ErrorGrain:0.000000}, "
                    + $"reference grain {b.GrainRef:0.000000}, still output grain baseline/off {stillBaseline:0.000000}/{stillUnlocked:0.000000}.");
                output.WriteLine($"Textured grain {preset}: baseline {baseline:0.000000}, locks-off {unlocked:0.000000}, "
                    + $"ratio {baseline / unlocked:0.000}, requested mask {requested} {candidate:0.000000}, "
                    + $"Quality gate applies only to Quality, bound {1.3 * unlocked:0.000000} (1.3 times locks-off).");
                if (preset == TemporalUpscale.Quality && !(baseline > 1.3 * unlocked))
                    failures.Add($"{preset} baseline grain {baseline:0.000000} must exceed 1.3 times locks-off {unlocked:0.000000}.");
            }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }
        finally { TemporalResolveDiagnostics.OverrideMaskForMeasurement(requested); }
    }
}
