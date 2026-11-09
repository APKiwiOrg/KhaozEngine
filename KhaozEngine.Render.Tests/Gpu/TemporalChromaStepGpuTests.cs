using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

/// <summary>A bright warm surface and a dark cool surface share almost the same weighted Cg.
/// Their shallow diagonal edge has no overlap and only a small depth step.</summary>
public sealed class TemporalChromaStepGpuTests(ITestOutputHelper output) : TemporalResolveGpuFacts
{
    const int Width = 64, Height = 64, Warmup = 80, Frames = 24;
    static readonly Vector3 Dark = new(.0822f, .0957f, .1030f);
    static readonly Vector3 Light = new(.6211f, .4555f, .28125f);

    [GpuFact]
    public void Half_float_chroma_rounding_does_not_turn_into_a_luma_flash_at_a_still_step()
    {
        Matrix4x4 projection = Perspective(1f);
        TemporalViewInput still = View(Eye, projection);
        Vector2 jitter = TemporalJitter.Offset(7, 8);
        using var rig = new TemporalResolveRig(Width, Height, Width, Height, NdcDepth(projection, 10.7f));
        var scene = new float[Width * Height * 4];
        var history = new float[scene.Length];
        var depth = new float[Width * Height];
        // Two RGB half steps change Cg by about 0.0001 while leaving Y almost unchanged.
        Vector3 stored = Dark + new Vector3(.00012f, -.00012f, .00012f);
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                bool light = y + .5f - jitter.Y < Edge(x + .5f - jitter.X, Height);
                Vector3 c = light ? Light : Dark;
                int p = y * Width + x;
                scene[p * 4] = c.X;
                scene[p * 4 + 1] = c.Y;
                scene[p * 4 + 2] = c.Z;
                scene[p * 4 + 3] = history[p * 4 + 3] = 1f;
                history[p * 4] = stored.X;
                history[p * 4 + 1] = stored.Y;
                history[p * 4 + 2] = stored.Z;
                depth[p] = NdcDepth(projection, light ? 10.72f : 10.7f);
            }
        rig.BeginFrame();
        rig.Fill(scene, scene, Pairs(Width, Height, (_, _) => Vector2.Zero));
        rig.FillDepth(depth);
        int channels = TemporalTextureIo.Channels(TemporalFormats.HistoryConfidence);
        var state = new float[Width * Height * channels];
        for (int p = 0; p < Width * Height; p++) state[p * channels] = 1f;
        rig.FillHistory(history, state, 10.7f);
        rig.Resolve(TemporalResolveMath.BuildUniforms(still, still, jitter, Width, Height, Width, Height, true));
        float[] resolved = rig.ReadColor();
        const int at = (33 * Width + 32) * 4;
        float before = Y(new Vector3(Q(stored.X), Q(stored.Y), Q(stored.Z)));
        float after = Y(new Vector3(resolved[at], resolved[at + 1], resolved[at + 2]));
        output.WriteLine($"weighted luma before {before}, after {after}, displacement {after - before}");
        Assert.True(MathF.Abs(after - before) <= .001f, $"Chroma rounding moved luma by {after - before}");
    }

    [GpuTheory]
    [InlineData(TemporalUpscale.Native)]
    [InlineData(TemporalUpscale.Quality)]
    public void A_still_coloured_step_does_not_pump_luma_from_a_narrow_chroma_box(TemporalUpscale preset)
    {
        float factor = TemporalSettings.DisplayOverInternal(preset);
        int iw = (int)(Width / factor), ih = (int)(Height / factor);
        var pixels = new List<int>();
        for (int y = 2; y < Height - 2; y++)
            for (int x = 2; x < Width - 2; x++)
            {
                float distance = (y + .5f) / factor - Edge((x + .5f) / factor, ih);
                if (distance > .75f && distance < 1.4f) pixels.Add(y * Width + x);
            }
        Assert.NotEmpty(pixels);
        Matrix4x4 projection = Perspective(1f);
        TemporalViewInput still = View(Eye, projection);
        using var rig = new TemporalResolveRig(iw, ih, Width, Height, NdcDepth(projection, 10.7f));
        float[] motion = Pairs(iw, ih, (_, _) => Vector2.Zero);
        var minimum = new float[pixels.Count];
        var maximum = new float[pixels.Count];
        Array.Fill(minimum, float.PositiveInfinity);
        Array.Fill(maximum, float.NegativeInfinity);
        for (int frame = 0; frame < Warmup + Frames; frame++)
        {
            Vector2 jitter = TemporalJitter.Offset(frame, TemporalJitter.PhaseCount(factor));
            var scene = new float[iw * ih * 4];
            var depth = new float[iw * ih];
            for (int y = 0; y < ih; y++)
                for (int x = 0; x < iw; x++)
                {
                    float sx = x + .5f - jitter.X, sy = y + .5f - jitter.Y;
                    bool light = sy < Edge(sx, ih);
                    Vector3 color = light ? Light : Dark;
                    // A fixed, sub-display-step shading gradient makes neighbouring chroma round independently.
                    color.Y += .00002f * (sx - iw * .5f);
                    int p = y * iw + x;
                    scene[p * 4] = color.X;
                    scene[p * 4 + 1] = color.Y;
                    scene[p * 4 + 2] = color.Z;
                    scene[p * 4 + 3] = 1f;
                    depth[p] = NdcDepth(projection, 10.7f + .003f * sy + (light ? .02f : 0f));
                }
            rig.BeginFrame();
            rig.Fill(scene, scene, motion);
            rig.FillDepth(depth);
            rig.Resolve(TemporalResolveMath.BuildUniforms(still, still, jitter, iw, ih, Width, Height, frame > 0));
            if (frame < Warmup) continue;
            float[] resolved = rig.ReadColor();
            for (int i = 0; i < pixels.Count; i++)
            {
                int p = pixels[i] * 4;
                Vector3 weighted = TemporalResolveMath.ToWeighted(new Vector3(resolved[p], resolved[p + 1], resolved[p + 2]));
                float y = .25f * weighted.X + .5f * weighted.Y + .25f * weighted.Z;
                minimum[i] = MathF.Min(minimum[i], y);
                maximum[i] = MathF.Max(maximum[i], y);
            }
        }
        float range = 0f;
        for (int i = 0; i < pixels.Count; i++) range = MathF.Max(range, maximum[i] - minimum[i]);
        output.WriteLine($"{preset}, {pixels.Count} dark-side edge pixels, weighted-luma range {range}");
        Assert.True(range <= .015f, $"{preset}: a chroma clip pumped weighted luma by {range}");
    }

    static float Edge(float x, int height) => height * .2f + .6f * x + .37f;

    static float Y(Vector3 color)
    {
        Vector3 weighted = TemporalResolveMath.ToWeighted(color);
        return .25f * weighted.X + .5f * weighted.Y + .25f * weighted.Z;
    }
}
