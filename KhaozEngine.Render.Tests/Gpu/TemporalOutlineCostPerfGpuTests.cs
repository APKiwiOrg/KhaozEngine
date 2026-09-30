using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// What the toon edge outline costs under temporal anti-aliasing at a 2560x1440 display, as a measurement on any
    /// real GPU. Every round renders one block of frames with the outline off and one with it on, on one scene, in an
    /// order that alternates from round to round so drift lands on both alike. The difference of the medians is the
    /// outline's cost, and the paired round differences show the spread. It prints the backend, the device, the
    /// internal size and every block, and asserts only that each block resolved. Virtual devices skip
    /// (<c>RequiresRealGpu</c>) and software rasterisers return before measuring.
    /// </summary>
    [Collection("GpuTimingSensitive")]
    public sealed class TemporalOutlineCostPerfGpuTests(ITestOutputHelper output)
    {
        const int Rounds = 15, Warm = 3, Block = 12, Settle = 8;

        [GpuFact(RequiresRealGpu = true)]
        public void TheOutlineAtQualityIsMeasuredAt1440p() => Run(2560, 1440, TemporalUpscale.Quality);

        [GpuFact(RequiresRealGpu = true)]
        public void TheOutlineAtNativeIsMeasuredAt1440p() => Run(2560, 1440, TemporalUpscale.Native);

        static double Median(IEnumerable<double> xs)
        {
            double[] s = xs.OrderBy(x => x).ToArray();
            return s.Length % 2 == 1 ? s[s.Length / 2] : 0.5 * (s[s.Length / 2 - 1] + s[s.Length / 2]);
        }

        static string F(double x) => x.ToString("0.000", CultureInfo.InvariantCulture);

        static string Join(IEnumerable<double> xs) => string.Join(" ", xs.Select(F));

        void Run(int w, int h, TemporalUpscale preset)
        {
            var stage = new FrontStage(w, h, 4.5f);
            using var fx = new TemporalFixture(w, h, s => stage.Setup(s, AntiAliasing.Temporal, preset));
            string device = fx.Device.Capabilities.DeviceName;
            output.WriteLine($"{fx.Device.Backend} on {device}");
            if (TemporalResolveCostPerfGpuTests.IsSoftwareDevice(device))
            {
                output.WriteLine("  not measured: a software rasteriser says nothing about a GPU's cost");
                return;
            }
            Scene3D scene = fx.Scene;
            Action<Scene3D, int> draw = Boxes(stage);
            scene.Post.Outline = true;
            fx.Frames(Settle, draw);
            scene.Post.Outline = false;
            fx.Frames(Settle, draw);
            int iw = scene.CurrentFrameView.Width, ih = scene.CurrentFrameView.Height;

            var off = new List<double>();
            var on = new List<double>();
            for (int r = 0; r < Rounds; r++)
                for (int k = 0; k < 2; k++)
                {
                    bool outline = ((r + k) & 1) == 1;
                    scene.Post.Outline = outline;
                    fx.Frames(Warm, draw);
                    long t0 = Stopwatch.GetTimestamp();
                    fx.Frames(Block, draw);
                    (outline ? on : off).Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds / Block);
                    Assert.True(scene.ResolvedLastRenderForTests, "a measured block did not resolve");
                }

            var paired = on.Zip(off, (a, b) => a - b).ToList();
            output.WriteLine($"  {w}x{h} {preset}, internal {iw}x{ih}, the {scene.TemporalResolveRendererForTests?.LastEntry} "
                + $"entry: {Rounds} rounds of {Block} frames after {Warm}, one block each way a round, the order alternating");
            output.WriteLine($"  outline off: {F(Median(off))} ms a frame, blocks {Join(off)}");
            output.WriteLine($"  outline on: {F(Median(on))} ms a frame, blocks {Join(on)}");
            output.WriteLine($"  the outline costs {F(Median(on) - Median(off))} ms from the medians, paired round "
                + $"differences median {F(Median(paired))} min {F(paired.Min())} max {F(paired.Max())}");
        }

        static readonly Color BoxTint = new(0.8f, 0.6f, 0.4f, 1f);

        // The flat wall and twelve keyed boxes drifting right, turned so their silhouettes cross pixels at an angle.
        static Action<Scene3D, int> Boxes(FrontStage stage) => (s, n) =>
        {
            stage.Wall(s);
            for (int i = 0; i < 12; i++)
                s.Draw(new RigidInstanceDraw(stage.Box, Matrix4x4.CreateRotationZ(0.2f * i)
                    * Matrix4x4.CreateTranslation(-3f + 0.5f * i + 0.002f * n, 0.3f * (i % 3), 0f))
                { Tint = BoxTint, Motion = MotionKey.From((ulong)(100 + i)) });
        };
    }
}
