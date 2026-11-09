using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Sub-texel diagonal blades over the textured panel, including frames whose centre sample misses a blade while
    /// the texture keeps its variance box open. The four diagonal history taps can cross the blade, unlike the
    /// vertical lines on flat ground in TemporalResolveLockGpuTests. A release that mistakes their spill and the
    /// texture's sample noise for changed shading raises the line's temporal error or its error against a separately
    /// box-integrated reference. Both, and the raw frame-to-frame change, stay within a tenth of v21.2.0 per device.
    /// The GPU receives point samples, camera-consistent motion and real jitter. The reference uses 16x16 sub-pixel
    /// samples and no temporal resolve. All figures use weighted luma, the space in which the release decides.
    /// </summary>
    public sealed class TemporalDiagonalDetailGpuTests(ITestOutputHelper output) : TemporalResolveGpuFacts
    {
        const int W = 120, H = 90, Warm = 80, Measured = 24, Margin = 8, ReferenceSide = 16;
        const float GroundMean = 0.12f, TextureAmplitude = 0.025f, Spacing = 13.7f;
        static readonly byte[] Texture = FrontStage.NoiseTexture();

        [GpuFact]
        public void Diagonal_sub_texel_lines_on_texture_keep_their_history_still_and_under_a_slow_pan()
        {
            var failures = new List<string>();
            foreach (TemporalUpscale preset in new[] { TemporalUpscale.Quality, TemporalUpscale.Native })
                foreach (int angle in new[] { 45, 30 })
                    foreach (int contrast in new[] { 2, 5, 10 })
                        foreach (float pan in new[] { 0f, 0.3f })
                        {
                            string name = $"{preset} {angle}deg {contrast}h pan{pan:0.0}";
                            TemporalReviewFigures figures = Run(preset, angle, contrast, pan);
                            output.WriteLine($"DIAGONAL {TemporalDeviceClass.Name}|{name}|{figures}");
                            TemporalReviewBaseline.Check("diagonal", name, figures, failures);
                        }
            Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        }

        TemporalReviewFigures Run(TemporalUpscale preset, int angle, int contrast, float pan)
        {
            float factor = TemporalSettings.DisplayOverInternal(preset), width = 0.375f * factor;
            float nominalHalfWidth = preset == TemporalUpscale.Quality ? 0.012f : 0.009f;
            int iw = (int)(W / factor), ih = (int)(H / factor);
            float radians = angle * MathF.PI / 180f;
            var normal = new Vector2(-MathF.Sin(radians), MathF.Cos(radians));
            float Distance(float x, float y) => MathF.Abs((Vector2.Dot(new Vector2(x, y), normal) + 1000f * Spacing)
                % Spacing - 0.5f * Spacing);
            float Sample(float x, float y) => Distance(x, y) < width * 0.5f
                ? GroundMean + contrast * nominalHalfWidth : Ground(x, y);
            Matrix4x4 projection = Perspective((float)W / H);
            float ndc = NdcDepth(projection, SceneMetres);
            float step = pan * SceneMetres / (projection.M11 * W * 0.5f);
            float[] motion = Pairs(iw, ih, (_, _) => new Vector2(-pan / W, 0f));
            using var rig = new Rig(iw, ih, W, H, ndc);
            float[]? previous = null;
            double error = 0, change = 0, referenceChange = 0, temporal = 0;
            int pixels = 0, changes = 0, missed = 0;
            var extents = new List<float>();
            for (int n = 0; n < Warm + Measured; n++)
            {
                Vector2 jitter = TemporalJitter.Offset(n, TemporalJitter.PhaseCount(factor));
                float[] samples = Grey(iw, ih, (x, y) => Q(Unweighted(Sample((x + 0.5f - jitter.X) * factor + n * pan,
                    (y + 0.5f - jitter.Y) * factor))));
                TemporalViewInput now = View(Eye + new Vector3(n * step, 0f, 0f), projection);
                TemporalViewInput then = View(Eye + new Vector3((n - 1) * step, 0f, 0f), projection);
                rig.BeginFrame();
                rig.Fill(samples, samples, motion);
                rig.Resolve(TemporalResolveMath.BuildUniforms(now, then, jitter, iw, ih, W, H, n > 0));
                if (n < Warm) continue;
                float[] color = rig.ReadColor(), state = rig.ReadState(), reference = new float[W * H];
                for (int y = Margin; y < H - Margin; y++)
                    for (int x = Margin; x < W - Margin; x++)
                    {
                        // Only the line's display footprint, not a wide background band that could hide shimmer.
                        if (Distance(x + 0.5f + n * pan, y + 0.5f) > width * 0.5f + 0.6f) continue;
                        float sum = 0;
                        for (int sy = 0; sy < ReferenceSide; sy++)
                            for (int sx = 0; sx < ReferenceSide; sx++)
                                sum += Sample(x + (sx + 0.5f) / ReferenceSide + n * pan,
                                    y + (sy + 0.5f) / ReferenceSide);
                        int p = y * W + x;
                        reference[p] = sum / (ReferenceSide * ReferenceSide);
                        float value = Weighted(color[p * 4]);
                        error += MathF.Abs(value - reference[p]);
                        pixels++;
                        if (previous != null)
                        {
                            float delta = value - Weighted(previous[p * 4]);
                            // Integrate the previous footprint here too, even where it was outside last frame's mask.
                            float old = 0;
                            for (int sy = 0; sy < ReferenceSide; sy++)
                                for (int sx = 0; sx < ReferenceSide; sx++)
                                    old += Sample(x + (sx + 0.5f) / ReferenceSide + (n - 1) * pan,
                                        y + (sy + 0.5f) / ReferenceSide);
                            float dr = reference[p] - old / (ReferenceSide * ReferenceSide);
                            change += MathF.Abs(delta);
                            referenceChange += MathF.Abs(dr);
                            temporal += MathF.Abs(delta - dr);
                            changes++;
                        }
                        int tx = Math.Clamp((int)MathF.Floor((x + 0.5f) / factor + jitter.X), 1, iw - 2);
                        int ty = Math.Clamp((int)MathF.Floor((y + 0.5f) / factor + jitter.Y), 1, ih - 2);
                        if (Distance((tx + 0.5f - jitter.X) * factor + n * pan,
                            (ty + 0.5f - jitter.Y) * factor) <= width * 0.5f || state[p * 4 + 1] <= 0f) continue;
                        float centre = Weighted(samples[(ty * iw + tx) * 4]);
                        bool Ridge(int ax, int ay, int bx, int by)
                        {
                            float a = Weighted(samples[((ty + ay) * iw + tx + ax) * 4]);
                            float b = Weighted(samples[((ty + by) * iw + tx + bx) * 4]);
                            float threshold = MathF.Max(0.02f, 0.1f * centre);
                            return centre - MathF.Max(a, b) > threshold || MathF.Min(a, b) - centre > threshold;
                        }
                        if (Ridge(-1, 0, 1, 0) || Ridge(0, -1, 0, 1) || Ridge(-1, -1, 1, 1)
                            || Ridge(1, -1, -1, 1)) continue;
                        double m = 0, m2 = 0;
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                float g = Ground((tx + dx + 0.5f - jitter.X) * factor + n * pan,
                                    (ty + dy + 0.5f - jitter.Y) * factor);
                                m += g; m2 += g * g;
                            }
                        extents.Add(1.5f * MathF.Sqrt((float)Math.Max(0, m2 / 9 - m * m / 81)));
                        missed++;
                    }
                previous = color;
            }
            extents.Sort();
            Assert.True(missed > 100, $"{preset} {angle} {contrast} {pan}: no carried, missed line samples");
            float halfWidth = extents[extents.Count / 2];
            output.WriteLine($"  missed locked samples {missed}, ground half-width median {halfWidth:0.000000}, "
                + $"line contrast {contrast * nominalHalfWidth:0.000000} ({contrast * nominalHalfWidth / halfWidth:0.00} half-widths)");
            Assert.True(halfWidth > 0.005f, "the textured ground must pass the carried-lock flat exemption");
            return new(error / pixels, change / changes, temporal / changes, referenceChange / changes);
        }

        // Bilinear magnification of the same independent-grey panel as FrontStage, in weighted luma. Scaling its
        // amplitude makes the contrasts controlled without quantising the low contrast case to an 8-bit image.
        static float Ground(float x, float y)
        {
            float u = x / FrontStage.TexelPixels, v = y / FrontStage.TexelPixels;
            int ix = (int)MathF.Floor(u), iy = (int)MathF.Floor(v);
            float fx = u - ix, fy = v - iy;
            float T(int tx, int ty) => Texture[(((ty % 64 + 64) % 64) * 64 + (tx % 64 + 64) % 64) * 4];
            float a = T(ix, iy) * (1 - fx) + T(ix + 1, iy) * fx;
            float b = T(ix, iy + 1) * (1 - fx) + T(ix + 1, iy + 1) * fx;
            return GroundMean + TextureAmplitude * ((a * (1 - fy) + b * fy - 127.5f) / 63.5f);
        }
    }
}
