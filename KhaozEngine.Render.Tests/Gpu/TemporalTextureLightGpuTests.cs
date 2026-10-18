using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Low-luma texture must keep its locks, and animated local light must not add isolated bright history.</summary>
public sealed class TemporalTextureLightGpuTests(GameShadowGrainScene scene, ITestOutputHelper output)
    : IClassFixture<GameShadowGrainScene>
{
    // Paired 21.4.0 measurements on Metal. Error, error-image grain and temporal error, in luma 0..1.
    // A tenth preserves the established detail-guard margin without allowing a candidate to set its own baseline.
    static readonly Dictionary<(string, TemporalUpscale), (double Error, double Grain, double Change)> Before = new()
    {
        [("dark", TemporalUpscale.Native)] = (0.000360, 0.000356, 0.000045),
        [("dark", TemporalUpscale.Quality)] = (0.001003, 0.000877, 0.000085),
        [("flicker", TemporalUpscale.Native)] = (0.014685, 0.005503, 0.010689),
        [("flicker", TemporalUpscale.Quality)] = (0.016049, 0.005340, 0.011537),
        [("moving", TemporalUpscale.Native)] = (0.010694, 0.003822, 0.003290),
        [("moving", TemporalUpscale.Quality)] = (0.012462, 0.004421, 0.003293),
    };

    [GpuFact]
    public void Dark_texture_and_animated_local_light_keep_detail_stable()
    {
        var failures = new List<string>();
        foreach (string kind in new[] { "dark", "flicker", "moving" })
        {
            void Light(Scene3D s, int n)
            {
                s.Post.LightColor = new Color(0f, 0f, 0f, 1f);
                s.Post.FillLightColor = new Color(0f, 0f, 0f, 1f);
                float ambient = kind == "dark" ? 0.08f : 0.03f;
                s.Post.AmbientColor = new Color(ambient, ambient, ambient, 1f);
                float intensity = kind == "dark" ? 0.03f
                    : kind == "flicker" ? 0.4f + 0.35f * MathF.Sin(n * 0.8f) : 0.5f;
                float x = kind == "moving" ? 0.6f * MathF.Sin(n * 0.18f) : 0f;
                s.AddLight(new Vector3(x, 0.05f, -1.5f), Color.White, 1.5f, intensity);
            }
            byte[][] reference = scene.Run(0f, 0f, AntiAliasing.Off, TemporalUpscale.Native, false,
                reference: true, textured: true, lighting: Light).Frames;
            foreach (TemporalUpscale preset in new[] { TemporalUpscale.Native, TemporalUpscale.Quality })
            {
                byte[][] frames = scene.Run(0f, 0f, AntiAliasing.Temporal, preset, false,
                    textured: true, lighting: Light).Frames;
                (double error, double grain, double change, double luma) = Measure(frames, reference);
                output.WriteLine($"Texture light {kind} {preset} {TemporalDeviceClass.Name}: error {error:0.000000}, "
                    + $"error grain {grain:0.000000}, change error {change:0.000000}, mean luma {luma:0.000000}.");
                (double e, double g, double c) = Before[(kind, preset)];
                output.WriteLine($"  bounds error {1.1 * e:0.000000}, grain {1.1 * g:0.000000}, change {1.1 * c:0.000000}.");
                if (!(error <= 1.1 * e && grain <= 1.1 * g && change <= 1.1 * c))
                    failures.Add($"{kind} {preset} exceeds its paired detail or speckle bounds.");
                if (kind == "dark") Assert.InRange(luma, 0.001, 0.08);
            }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [GpuFact]
    public void Local_light_state_leaves_the_shared_scene_equal_to_a_fresh_scene()
    {
        void Dim(Scene3D s, int n)
        {
            s.Post.LightColor = Color.Black;
            s.Post.FillLightColor = Color.Black;
            s.Post.AmbientColor = new Color(0.08f, 0.08f, 0.08f, 1f);
            s.AddLight(new Vector3(0.2f * MathF.Sin(n), 0.05f, -1.5f), Color.White, 1.5f, 0.4f);
        }
        scene.Run(0f, 0f, AntiAliasing.Temporal, TemporalUpscale.Native, false, textured: true, lighting: Dim);
        scene.Run(0f, 0f, AntiAliasing.Fxaa, TemporalUpscale.Native, false, reference: true,
            textured: true, lighting: Dim);
        byte[][] reused = scene.Run(4f, 1f, AntiAliasing.Temporal, TemporalUpscale.Quality, false, textured: true).Frames;
        using var fresh = new GameShadowGrainScene();
        byte[][] expected = fresh.Run(4f, 1f, AntiAliasing.Temporal, TemporalUpscale.Quality, false, textured: true).Frames;
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], reused[i]);
    }

    static (double Error, double Grain, double Change, double Luma) Measure(byte[][] frames, byte[][] reference)
    {
        const int W = GameShadowGrainScene.W, H = GameShadowGrainScene.H;
        double error = 0, grain = 0, change = 0, luma = 0;
        long count = 0, steps = 0;
        var window = new float[9];
        float E(int t, int x, int y) => TemporalAcceptance.Luma(frames[t], W, x, y)
            - TemporalAcceptance.Luma(reference[t], W, x, y);
        for (int t = 0; t < frames.Length; t++)
            for (int y = 20; y < H - 20; y++)
                for (int x = 4; x < W - 4; x++)
                {
                    if (!GameShadowGrainScene.Receiver(GameShadowGrainScene.Warm + t, x, false)) continue;
                    float e = E(t, x, y);
                    int k = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++) window[k++] = E(t, x + dx, y + dy);
                    Array.Sort(window);
                    error += Math.Abs(e);
                    grain += Math.Abs(e - window[4]);
                    luma += TemporalAcceptance.Luma(reference[t], W, x, y);
                    count++;
                    if (t == 0) continue;
                    change += Math.Abs(e - E(t - 1, x, y));
                    steps++;
                }
        Assert.True(count > 0 && steps > 0);
        return (error / count, grain / count, change / steps, luma / count);
    }
}
