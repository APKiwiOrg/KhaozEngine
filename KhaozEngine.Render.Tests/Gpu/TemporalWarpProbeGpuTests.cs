using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>Scratch probe: prints what each backend computes where the WARP leg fails.</summary>
    public sealed class TemporalWarpProbeGpuTests(ITestOutputHelper output)
    {
        const float HalfLeastNormal = 6.1035156e-5f;

        [GpuFact]
        public void Probe_static_orbit_band()
        {
            Probe(StaticPath.FastOrbit, TemporalStaticOrbitRuns.GrazingPitch, TemporalStaticOrbitRuns.MaxZoom);
            Probe(StaticPath.FastOrbit, TemporalStaticOrbitRuns.GrazingPitch, GroundStage.Distance);
        }

        void Probe(StaticPath path, float pitch, float distance)
        {
            const int w = 2560, h = 1440;
            var field = new TemporalStaticOrbitRuns.StillField(w, h, path, true, pitch, distance);
            using var fx = new TemporalFixture(w, h, s => field.Setup(s, TemporalUpscale.Quality));
            output.WriteLine($"== {path} far Quality {w}x{h} pitch {pitch} {distance} m on {fx.Device.Backend} "
                + $"{fx.Device.Capabilities.DeviceName}");
            int frames = TemporalStaticOrbitRuns.Warm + TemporalStaticOrbitRuns.Measured;
            for (int n = 0; n < frames; n++)
            {
                fx.Frames(1, field.Draw);
                TemporalHistory history = fx.Scene.TemporalHistory;
                float[] state = TemporalTextureIo.Read(fx.Device, history.Confidence(history.WriteIndex));
                MotionTargetReadback motion = fx.Scene.ReadMotionTargetForTests();
                long zero = 0, sub = 0, tiny = 0, texels = 0;
                foreach (Vector2 m in motion.Motion)
                {
                    if (MathF.Abs(m.X) > TemporalResolveTuning.MotionSentinel) continue;
                    texels++;
                    foreach (float c in new[] { m.X, m.Y })
                    {
                        if (c == 0f) zero++;
                        else if (MathF.Abs(c) < HalfLeastNormal) sub++;
                        else if (MathF.Abs(c) < 4f * HalfLeastNormal) tiny++;
                    }
                }
                var bands = new List<int>();
                for (int i = 1; i < state.Length; i += 2) if (state[i] < -2.5f) bands.Add(i / 2);
                output.WriteLine($"frame {n}: band {bands.Count}, motion texels {texels}, components zero {zero}, "
                    + $"subnormal {sub}, below 4 least normals {tiny}, origin {fx.Scene.RenderOrigin}");
                if (bands.Count == 0) continue;
                TemporalResolveUniforms u = fx.Scene.TemporalResolveRendererForTests!.LastUniforms;
                float[] depth = TemporalTextureIo.Read(fx.Device, history.PreviousDepth(history.WriteIndex));
                var size = new Vector2(u.Sizes.X, u.Sizes.Y);
                var jitter = new Vector2(u.Jitter.X, u.Jitter.Y);
                output.WriteLine($"  internal {size}, jitter {jitter}, P11 {u.PreviousProjection.M11}, "
                    + $"current depth {u.CurrentDepth}");
                foreach (int p in bands.Take(12))
                {
                    int x = p % w, y = p / w;
                    Vector2 centreF = (new Vector2(x + 0.5f, y + 0.5f) / new Vector2(w, h)) * size + jitter;
                    int cx = Math.Clamp((int)MathF.Floor(centreF.X), 0, motion.Width - 1);
                    int cy = Math.Clamp((int)MathF.Floor(centreF.Y), 0, motion.Height - 1);
                    int bx = cx, by = cy;
                    float best = float.MaxValue;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = Math.Clamp(cx + dx, 0, motion.Width - 1), yy = Math.Clamp(cy + dy, 0, motion.Height - 1);
                            float d = depth[yy * motion.Width + xx];
                            if (d < best) { best = d; bx = xx; by = yy; }
                        }
                    output.WriteLine($"  band pixel ({x},{y}) state {state[2 * p]:0.000} {state[2 * p + 1]:0.000}, "
                        + $"centre texel ({cx},{cy}), closest ({bx},{by})");
                    output.WriteLine("    centre  " + Texel(cx, cy, motion, depth, u, size, jitter));
                    output.WriteLine("    closest " + Texel(bx, by, motion, depth, u, size, jitter));
                }
            }
        }

        static string Texel(int x, int y, MotionTargetReadback motion, float[] depth, TemporalResolveUniforms u,
            Vector2 size, Vector2 jitter)
        {
            int i = y * motion.Width + x;
            Vector2 m = motion.Motion[i];
            Vector2 sampleUv = (new Vector2(x + 0.5f, y + 0.5f) - jitter) / size;
            var ndc = new Vector2(sampleUv.X * 2f - 1f, 1f - sampleUv.Y * 2f);
            string travel = "no static uv";
            float screen = (m * size).Length();
            if (TemporalResolveMath.StaticPreviousUv(u, ndc, depth[i]) is Vector2 staticUv)
            {
                Vector2 v = (staticUv - (sampleUv - m)) * size;
                float t = v.Length();
                float clipW = u.CurrentDepth.X > 0.5f ? depth[i] : 1f;
                float mpp = 2f * clipW / (u.PreviousProjection.M11 * size.X);
                float worldPx = TemporalResolveTuning.WorldMotionMetres / mpp
                    + screen * TemporalResolveTuning.MovingSurfaceMotionFraction;
                travel = $"travel {t:0.000000} px ({v.X:0.000000},{v.Y:0.000000}), world test {worldPx:0.000000} px, "
                    + $"metres per px {mpp:0.000000}, travel over screen {(screen > 0 ? t / screen : float.PositiveInfinity):0.000}";
            }
            return $"motion ({m.X:G9},{m.Y:G9}) uv, screen {screen:0.000000} px, depth {depth[i]:0.0000} m, {travel}";
        }

        [GpuFact]
        public void Probe_follow_stop_inner()
        {
            var runs = new TemporalFollowStopRuns();
            foreach (FollowEnding ending in new[] { FollowEnding.Reversal, FollowEnding.Stop })
            {
                StopRun walk = runs.Orthographic(ending, TemporalUpscale.Quality, 1f);
                StopRun control = runs.Control(walk);
                var (wf, inner, reference) = TemporalFollowStopRuns.Kept[walk.Name];
                var (cf, _, _) = TemporalFollowStopRuns.Kept[control.Name];
                output.WriteLine($"== {walk.Name}: inner pixels {walk.Inner.Pixels}");
                for (int k = 0; k < walk.Inner.Errors.Length; k++)
                {
                    int f = TemporalFollowStopRuns.Lead - 1 + k;
                    int differ = 0, rgbDiffer = 0;
                    double lsbSum = 0;
                    var hist = new SortedDictionary<int, int>();
                    var worst = new List<string>();
                    for (int i = 0; i < inner[f].Length; i++)
                    {
                        if (!inner[f][i]) continue;
                        int x = i % TemporalFollowStopRuns.W, y = i / TemporalFollowStopRuns.W;
                        float lw = TemporalAcceptance.Luma(wf[f], TemporalFollowStopRuns.W, x, y);
                        float lc = TemporalAcceptance.Luma(cf[f], TemporalFollowStopRuns.W, x, y);
                        float lr = TemporalAcceptance.Luma(reference[f], TemporalFollowStopRuns.W, x, y);
                        bool rgb = wf[f][4 * i] != cf[f][4 * i] || wf[f][4 * i + 1] != cf[f][4 * i + 1]
                            || wf[f][4 * i + 2] != cf[f][4 * i + 2];
                        if (rgb) rgbDiffer++;
                        if (lw != lc)
                        {
                            differ++;
                            int step = (int)MathF.Round((lw - lc) * 255f * 16f);
                            hist[step] = hist.GetValueOrDefault(step) + 1;
                            lsbSum += (MathF.Abs(lw - lr) - MathF.Abs(lc - lr)) * 255.0;
                            if (worst.Count < 8)
                                worst.Add($"({x},{y}) walk {wf[f][4 * i]},{wf[f][4 * i + 1]},{wf[f][4 * i + 2]} control "
                                    + $"{cf[f][4 * i]},{cf[f][4 * i + 1]},{cf[f][4 * i + 2]} ref "
                                    + $"{reference[f][4 * i]},{reference[f][4 * i + 1]},{reference[f][4 * i + 2]}");
                        }
                    }
                    output.WriteLine($"  frame {k - 1:+0;-0}: walk {walk.Inner.Errors[k]:0.00000000} control "
                        + $"{control.Inner.Errors[k]:0.00000000} diff {(walk.Inner.Errors[k] - control.Inner.Errors[k]) * walk.Inner.Pixels * 255:0.000} "
                        + $"luma steps summed, pixels luma differ {differ}, rgb differ {rgbDiffer}, error steps from them "
                        + $"{lsbSum:0.000}, sixteenths of a step: {string.Join(" ", hist.Select(e => $"{e.Key}:{e.Value}"))}");
                    if (k == 13) foreach (string s in worst) output.WriteLine("    " + s);
                }
                output.WriteLine($"  whole walk {string.Join(" ", walk.Whole.Errors.Select(e => e.ToString("0.00000")))}");
                output.WriteLine($"  cycle walk {string.Join(" ", walk.Inner.Cycle.Select(e => e.ToString("0.0000000")))}");
                output.WriteLine($"  cycle ctrl {string.Join(" ", control.Inner.Cycle.Select(e => e.ToString("0.0000000")))}");
            }
        }

        [GpuFact]
        public void Probe_follow_stop_motion()
        {
            foreach (bool still in new[] { false, true })
            {
                var scene = new TemporalFollowStopRuns.OrthographicStop(FollowEnding.Reversal, 1f, StopSurround.Ground,
                    still);
                using var fx = new TemporalFixture(TemporalFollowStopRuns.W, TemporalFollowStopRuns.H,
                    s => scene.Setup(s, TemporalUpscale.Quality));
                output.WriteLine($"== {scene.Name} on {fx.Device.Backend}");
                for (int n = 0; n <= TemporalFollowStopRuns.Turn + TemporalFollowStopRuns.After; n++)
                {
                    fx.Frames(1, scene.Draw);
                    if (n < TemporalFollowStopRuns.Turn - 6) continue;
                    MotionTargetReadback motion = fx.Scene.ReadMotionTargetForTests();
                    long zero = 0, sub = 0, small = 0;
                    float maxSmall = 0f;
                    var values = new SortedDictionary<float, int>();
                    foreach (Vector2 m in motion.Motion)
                    {
                        if (MathF.Abs(m.X) > TemporalResolveTuning.MotionSentinel) continue;
                        foreach (float c in new[] { m.X, m.Y })
                        {
                            float a = MathF.Abs(c);
                            if (a == 0f) zero++;
                            else if (a < HalfLeastNormal) sub++;
                            if (a > 0f && a < 1e-3f)
                            {
                                small++;
                                maxSmall = MathF.Max(maxSmall, a);
                                values[c] = values.GetValueOrDefault(c) + 1;
                            }
                        }
                    }
                    output.WriteLine($"  frame {n - TemporalFollowStopRuns.Turn:+0;-0}: zero {zero}, subnormal {sub}, "
                        + $"nonzero below 1e-3 {small} (max {maxSmall:G6}), values "
                        + string.Join(" ", values.OrderByDescending(e => e.Value).Take(6).Select(e => $"{e.Key:G6}x{e.Value}")));
                }
            }
        }

        [GpuFact]
        public void Probe_ridged_box_trail()
        {
            var runs = new TemporalNarrowCrossingRuns();
            foreach (TemporalUpscale preset in new[] { TemporalUpscale.Quality, TemporalUpscale.Native })
            {
                CrossingTrail t = runs.Run(NarrowCrossing.RidgedBox, preset);
                output.WriteLine($"== {t.Name}: {t.Total}");
                var (frame, wall, floors, footprints) = TemporalNarrowCrossingRuns.Kept[(NarrowCrossing.RidgedBox, preset)];
                const int W = TemporalNarrowCrossingRuns.W, H = TemporalNarrowCrossingRuns.H;
                PixelRect now = footprints[0].Inflate(1);
                for (int y = 0; y < H; y++)
                    for (int x = 0; x < W; x++)
                    {
                        if (now.Contains(x, y) || footprints[1].Contains(x, y)) continue;
                        int age = 0;
                        for (int k = TemporalGhostingRuns.FirstAge; k < footprints.Length && age == 0; k++)
                            if (footprints[k].Contains(x, y)) age = k;
                        if (age == 0) continue;
                        float d = TemporalAcceptance.Difference(frame, wall, W, x, y, PixelDifference.MaxChannel);
                        float fl = TemporalAcceptance.Difference(floors[age], wall, W, x, y, PixelDifference.MaxChannel);
                        if (d - fl <= TemporalGhostingRuns.Tolerance) continue;
                        int i = 4 * (y * W + x);
                        output.WriteLine($"  excess ({x},{y}) age {age}, behind edge {footprints[0].X0 - x}, d {d:0.000} "
                            + $"floor {fl:0.000}, frame {frame[i]},{frame[i + 1]},{frame[i + 2]} wall {wall[i]},"
                            + $"{wall[i + 1]},{wall[i + 2]} floor {floors[age][i]},{floors[age][i + 1]},{floors[age][i + 2]}");
                    }
                output.WriteLine($"  footprints now {footprints[0]}, one ago {footprints[1]}, two ago {footprints[2]}");
            }
        }
    }
}
