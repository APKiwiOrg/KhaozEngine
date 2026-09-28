using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A white keyed line one internal texel wide and tilted, as the fast keyed line of
    /// <see cref="TemporalFastEdgeGpuTests"/> is, crossing the textured wall at 2 internal pixels a frame
    /// (<see cref="TemporalHeldBackgroundRuns"/>). The wall's texels are independent greys, so its pixels take ridges
    /// and hold their locks. Where a pixel's hold is whole, the moving share's colour of
    /// TEMPORAL-RESOLVE-UPSCALING-DESIGN amendment 23 takes only the share the hold does not keep, so over a held
    /// background the line reaches the output through the current sample alone. The line's energy on each frame is its
    /// luma less the same frame's without it, over its supersampled coverage, as a share of the reference's own
    /// difference. The same line over the flat wall, which holds no lock, is the control. Report only. HDR is off, the
    /// sharpen is at its default, and the measured values in the comments are Metal on Apple silicon.
    /// </summary>
    public sealed class TemporalHeldBackgroundGpuTests(TemporalHeldBackgroundRuns runs, ITestOutputHelper output)
        : IClassFixture<TemporalHeldBackgroundRuns>
    {
        /// <summary>The line's worst and mean energy share over the textured wall and over the flat one, at Native and
        /// Quality, with the share of the wall's pixels in its path whose hold was whole before it arrived, and the
        /// share on each frame. A redesign of the held share is read against it.</summary>
        const int First = TemporalHeldBackgroundRuns.Warm;

        [GpuFact]
        public void A_fast_narrow_line_over_a_held_textured_wall_prints_its_energy()
        {
            foreach (TemporalUpscale preset in new[] { TemporalUpscale.Native, TemporalUpscale.Quality })
                foreach (bool textured in new[] { true, false })
                {
                    HeldBackgroundRun r = runs.Run(preset, textured);
                    string wall = textured ? "textured" : "flat";
                    output.WriteLine($"{preset}, {wall} wall: energy worst {r.Worst:0.000} on frame {r.WorstFrame}, "
                        + $"mean {r.Mean:0.000}, over {r.Pixels} covered pixels, held before it {r.HeldShare:0.000}. "
                        + string.Join(" ", r.Shares.Select((v, t) => $"{First + t}:{v:0.00}")));
                    Assert.True(r.Pixels > 0, $"{preset}: the line covered nothing");
                }
        }
    }

    /// <summary>One run: the line's energy share on each measured frame, the worst and its frame, the mean over the
    /// frames, the covered pixels summed over them, and the share of the wall's pixels in the line's path whose hold
    /// was whole on the frame before the line appeared.</summary>
    internal sealed record HeldBackgroundRun(double[] Shares, double Worst, int WorstFrame, double Mean, int Pixels,
        double HeldShare);

    /// <summary>
    /// The held-background runs, rendered on first use and kept for the test class. Only the wall draws for the first
    /// <see cref="StillFrames"/> frames, so the wall converges and its ridged pixels take their locks, then the line
    /// moves right from display x <see cref="StartPixels"/>, and the window is <see cref="Measured"/> frames after
    /// <see cref="Warm"/>.
    /// </summary>
    public sealed class TemporalHeldBackgroundRuns
    {
        public const int W = 320, H = 180, StillFrames = 16, Warm = StillFrames + 8, Measured = 16;
        public const float StartPixels = 60f, InternalPixelsPerFrame = 2f;
        const float OrthoSize = 4.5f, LineHeight = 3.6f, LineDepth = 0.2f, LineTilt = 0.3f;
        const ulong Key = 73;

        readonly Dictionary<(TemporalUpscale, bool), HeldBackgroundRun> _runs = new();

        internal HeldBackgroundRun Run(TemporalUpscale preset, bool textured)
        {
            if (_runs.TryGetValue((preset, textured), out HeldBackgroundRun? cached)) return cached;
            var stage = new FrontStage(W, H, OrthoSize);
            float factor = TemporalSettings.DisplayOverInternal(preset), speed = InternalPixelsPerFrame * factor;
            var size = new Vector3(factor * stage.PixelWorld, LineHeight, LineDepth);
            Vector3 Centre(int n) =>
                new((StartPixels + Math.Max(0, n - StillFrames) * speed + factor / 2f - W / 2f) * stage.PixelWorld,
                    0f, 0f);
            void Background(Scene3D s, int n)
            {
                if (textured) stage.TexturedWall(s);
                else stage.Wall(s);
            }
            void Draw(Scene3D s, int n)
            {
                Background(s, n);
                if (n < StillFrames) return;
                Matrix4x4 world = Matrix4x4.CreateScale(size) * Matrix4x4.CreateRotationZ(LineTilt)
                    * Matrix4x4.CreateTranslation(Centre(n));
                s.Draw(new RigidInstanceDraw(stage.Box, world)
                {
                    Tint = new Color(1f, 1f, 1f, 1f),
                    Motion = MotionKey.From(Key),
                });
            }
            Action<Scene3D> setup = s =>
            {
                stage.Setup(s, AntiAliasing.Temporal, preset);
                s.Post.Hdr.Enabled = false;
            };
            byte[][] with = TemporalAcceptance.Sequence(W, H, setup, Draw, Warm, Measured);
            byte[][] without = TemporalAcceptance.Sequence(W, H, setup, Background, Warm, Measured);
            byte[][] referenceWith = TemporalAcceptance.ReferenceSequence(W, H, setup, Draw, Warm, Measured);
            byte[][] referenceWithout = TemporalAcceptance.ReferenceSequence(W, H, setup, Background, Warm, Measured);
            // The tilted line's half extent, its bounding box's.
            var half = new Vector3(MathF.Cos(LineTilt) * size.X / 2f + MathF.Sin(LineTilt) * size.Y / 2f,
                MathF.Sin(LineTilt) * size.X / 2f + MathF.Cos(LineTilt) * size.Y / 2f, size.Z / 2f);
            var shares = new double[Measured];
            int pixels = 0;
            double covered = 0, wanted = 0;
            for (int t = 0; t < Measured; t++)
            {
                Vector3 c = Centre(Warm + t);
                PixelRect region = TemporalAcceptance.Footprint(stage.Camera(), c - half, c + half, W, H).Inflate(2)
                    .Clip(W, H);
                double frameCovered = 0, frameWanted = 0;
                for (int y = region.Y0; y < region.Y1; y++)
                    for (int x = region.X0; x < region.X1; x++)
                    {
                        float want = TemporalAcceptance.Luma(referenceWith[t], W, x, y)
                            - TemporalAcceptance.Luma(referenceWithout[t], W, x, y);
                        if (MathF.Abs(want) <= TemporalAcceptance.CoverageLumaStep) continue;
                        frameWanted += want;
                        frameCovered += TemporalAcceptance.Luma(with[t], W, x, y)
                            - TemporalAcceptance.Luma(without[t], W, x, y);
                        pixels++;
                    }
                shares[t] = frameCovered / frameWanted;
                covered += frameCovered;
                wanted += frameWanted;
            }
            int worst = Array.IndexOf(shares, shares.Min());
            return _runs[(preset, textured)] = new HeldBackgroundRun(shares, shares[worst], Warm + worst,
                covered / wanted, pixels, HeldShare(setup, Background, stage, Centre, half));
        }

        // The share of the wall's display pixels in the line's path over the window whose stored lock holds the clip
        // whole, at least 1 / LockHoldGain, on the frame before the line appears.
        static double HeldShare(Action<Scene3D> setup, Action<Scene3D, int> background, FrontStage stage,
            Func<int, Vector3> centre, Vector3 half)
        {
            using var fx = new TemporalFixture(W, H, setup);
            fx.Frames(StillFrames, background);
            TemporalHistory history = fx.Scene.TemporalHistory;
            float[] state = TemporalTextureIo.Read(fx.Device, history.Confidence(history.WriteIndex));
            Vector3 first = centre(Warm), last = centre(Warm + Measured - 1);
            PixelRect path = TemporalAcceptance.Footprint(stage.Camera(), first - half, last + half, W, H).Clip(W, H);
            int held = 0;
            for (int y = path.Y0; y < path.Y1; y++)
                for (int x = path.X0; x < path.X1; x++)
                    if (state[(y * W + x) * 2 + 1] >= 1f / TemporalResolveTuning.LockHoldGain) held++;
            return path.Area == 0 ? double.NaN : (double)held / path.Area;
        }
    }
}
