using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// TEMPORAL-RESOLVE-UPSCALING-DESIGN section 7 item 9, the engine half, as a measurement on any real GPU: what the
    /// resolve and the sharpen cost at a 2560x1440 and a 3456x2234 display, on the single-pass resolve and on the
    /// two-pass resolve (<c>TemporalResolvePath</c>), and what each of the two passes costs on its own. Every round
    /// renders one block of frames in each mode on one scene, anti-aliasing off at the internal size, the single pass,
    /// the two passes, and each pass alone, in an order that rotates from round to round so drift lands on every mode
    /// alike. The difference of a mode's median block from the off median is an upper bound on its resolve and
    /// sharpen, which also carries the motion target, the opaque copy and the post chain at display size in place of
    /// the internal size. The paired round differences show the spread.
    /// <para>
    /// It prints the backend, the device and every block, and asserts only that each block rendered the mode it
    /// claims. The design's budget of 1.0 ms at 2560x1440 is printed against each path, not asserted, because neither
    /// path meets it yet. Virtual devices skip (<c>RequiresRealGpu</c>) and software rasterisers return before
    /// measuring. The Grimhollow town and meadow measurement is the game-side one.
    /// </para>
    /// <para>
    /// Scenes: the flat wall with twelve keyed boxes, and a heavy scene of thin keyed bars moving over the textured
    /// wall in both axes in front of a wide keyed box the camera follows while the wall pans under it, so most display
    /// pixels sit beside a moving edge and take the resolve's own-motion, narrow-feature, band and disocclusion paths.
    /// </para>
    /// </summary>
    [Collection("GpuTimingSensitive")]
    public sealed class TemporalResolveCostPerfGpuTests(ITestOutputHelper output)
    {
        const int Rounds = 15, Warm = 2, Block = 12;

        // The spec's budget for the resolve and the sharpen together, in ms at a 2560x1440 display.
        const double ResolveBudgetMs = 1.0;

        [GpuFact(RequiresRealGpu = true)]
        public void TheBoxesAtQualityAreMeasuredAt1440p() => Run(2560, 1440, Boxes, TemporalUpscale.Quality);

        [GpuFact(RequiresRealGpu = true)]
        public void TheBoxesAtNativeAreMeasuredAt1440p() => Run(2560, 1440, Boxes, TemporalUpscale.Native);

        [GpuFact(RequiresRealGpu = true)]
        public void TheMovingFieldAtQualityIsMeasuredAt1440p() => Run(2560, 1440, Field, TemporalUpscale.Quality);

        [GpuFact(RequiresRealGpu = true)]
        public void TheMovingFieldAtNativeIsMeasuredAt1440p() => Run(2560, 1440, Field, TemporalUpscale.Native);

        [GpuFact(RequiresRealGpu = true)]
        public void TheBoxesAtQualityAreMeasuredAt3456x2234() => Run(3456, 2234, Boxes, TemporalUpscale.Quality);

        [GpuFact(RequiresRealGpu = true)]
        public void TheBoxesAtNativeAreMeasuredAt3456x2234() => Run(3456, 2234, Boxes, TemporalUpscale.Native);

        [GpuFact(RequiresRealGpu = true)]
        public void TheMovingFieldAtQualityIsMeasuredAt3456x2234() => Run(3456, 2234, Field, TemporalUpscale.Quality);

        [GpuFact(RequiresRealGpu = true)]
        public void TheMovingFieldAtNativeIsMeasuredAt3456x2234() => Run(3456, 2234, Field, TemporalUpscale.Native);

        const string Boxes = "twelve keyed boxes", Field = "moving thin field";

        /// <summary>The software rasterisers a hosted runner can fall back to. A name test, since the seam reports no
        /// device kind.</summary>
        internal static bool IsSoftwareDevice(string? name) => name is { } n
            && (n.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase)
                || n.Contains("lavapipe", StringComparison.OrdinalIgnoreCase)
                || n.Contains("SwiftShader", StringComparison.OrdinalIgnoreCase)
                || n.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)
                || n.Contains("WARP", StringComparison.Ordinal)
                || n.Contains("Software", StringComparison.OrdinalIgnoreCase));

        // The modes of one round. Each temporal mode names the resolve it selects and the pass it leaves out.
        enum Mode { Off, Single, Split, PassOneAlone, PassTwoAlone }

        static readonly Mode[] Modes = [Mode.Off, Mode.Single, Mode.Split, Mode.PassOneAlone, Mode.PassTwoAlone];

        static double Median(IEnumerable<double> xs)
        {
            double[] s = xs.OrderBy(x => x).ToArray();
            return s.Length % 2 == 1 ? s[s.Length / 2] : 0.5 * (s[s.Length / 2 - 1] + s[s.Length / 2]);
        }

        static string Join(IEnumerable<double> xs) =>
            string.Join(" ", xs.Select(x => x.ToString("0.000", CultureInfo.InvariantCulture)));

        static string F(double x) => x.ToString("0.000", CultureInfo.InvariantCulture);

        void Run(int w, int h, string name, TemporalUpscale preset)
        {
            var stage = new FrontStage(w, h, 4.5f);
            using var fx = new TemporalFixture(w, h, s => stage.Setup(s, AntiAliasing.Temporal, preset));
            string device = fx.Device.Capabilities.DeviceName;
            output.WriteLine($"{fx.Device.Backend} on {device}");
            if (IsSoftwareDevice(device))
            {
                output.WriteLine("  not measured: a software rasteriser says nothing about a GPU's cost");
                return;
            }
            Scene3D scene = fx.Scene;
            Action<Scene3D, int> draw = name == Boxes ? BoxesScene(stage) : FieldScene(stage);

            // Settle on each resolve, which builds the two-pass resolve's objects, then read the internal size the
            // resolve reads from and sample its counts once.
            scene.TemporalResolveSplitForTests = true;
            fx.Frames(4, draw);
            scene.TemporalResolveSplitForTests = false;
            fx.Frames(4, draw);
            scene.RequestTemporalCounts();
            fx.Frames(2, draw);
            TemporalDiagnostics counts = scene.LastTemporalDiagnostics;
            Assert.True(counts.CountsFrameIndex >= 0, "the resolve's counts were never sampled");
            int iw = counts.InternalWidth, ih = counts.InternalHeight;
            var renderer = scene.TemporalResolveRendererForTests
                ?? throw new InvalidOperationException("the resolve never ran");
            var split = renderer.SplitResolveForTests
                ?? throw new InvalidOperationException("the two-pass resolve was never built");

            var wall = Modes.ToDictionary(m => m, _ => new List<double>());
            var submit = Modes.ToDictionary(m => m, _ => new List<double>());
            for (int r = 0; r < Rounds; r++)
                for (int k = 0; k < Modes.Length; k++)
                {
                    Mode mode = Modes[(r + k) % Modes.Length];
                    bool temporal = mode != Mode.Off;
                    scene.Post.Quality.AntiAliasing = temporal ? AntiAliasing.Temporal : AntiAliasing.Off;
                    scene.Post.RenderScale = temporal ? RenderScale.MatchViewport : RenderScale.FixedInternal;
                    scene.Post.RenderWidth = iw;
                    scene.Post.RenderHeight = ih;
                    scene.TemporalResolveSplitForTests = mode is not (Mode.Off or Mode.Single);
                    split.SkipPassForTests = mode == Mode.PassOneAlone ? 2 : mode == Mode.PassTwoAlone ? 1 : 0;
                    fx.Frames(Warm, draw);
                    double submitted = 0;
                    long t0 = Stopwatch.GetTimestamp();
                    for (int i = 0; i < Block; i++)
                    {
                        fx.Frames(1, draw);
                        submitted += fx.LastSubmitMilliseconds;
                    }
                    wall[mode].Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds / Block);
                    submit[mode].Add(submitted / Block);
                    // Each block rendered the mode it claims, at the one internal size.
                    Assert.Equal(temporal, scene.ResolvedLastRenderForTests);
                    Assert.Equal((iw, ih), (scene.CurrentFrameView.Width, scene.CurrentFrameView.Height));
                    if (temporal) Assert.Equal(mode != Mode.Single, renderer.Split);
                }
            split.SkipPassForTests = 0;
            scene.TemporalResolveSplitForTests = null;

            double off = Median(wall[Mode.Off]);
            output.WriteLine($"  {w}x{h} {preset}, {name}, internal {iw}x{ih}: {Rounds} rounds of {Block} frames after "
                + $"{Warm}, one block a mode a round, the order rotating");
            output.WriteLine($"  off at {iw}x{ih}: {F(off)} ms a frame, blocks {Join(wall[Mode.Off])}");
            foreach (Mode mode in Modes.Skip(1))
            {
                var paired = wall[mode].Zip(wall[Mode.Off], (t, o) => t - o).ToList();
                double submitted = Median(submit[mode]) - Median(submit[Mode.Off]);
                output.WriteLine($"  {Label(mode)}: {F(Median(wall[mode]))} ms a frame, resolve and sharpen at most "
                    + $"{F(Median(wall[mode]) - off)} ms, paired round differences median {F(Median(paired))} min "
                    + $"{F(paired.Min())} max {F(paired.Max())}, submit and drain difference {F(submitted)}");
                output.WriteLine($"    blocks {Join(wall[mode])}");
            }
            PrintPair("pass one", wall[Mode.Split], wall[Mode.PassTwoAlone]);
            PrintPair("pass two", wall[Mode.Split], wall[Mode.PassOneAlone]);
            PrintPair("two passes against the single pass", wall[Mode.Split], wall[Mode.Single]);
            if (w == 2560 && h == 1440)
                output.WriteLine($"  budget {F(ResolveBudgetMs)} ms at 2560x1440: single pass "
                    + $"{Verdict(Median(wall[Mode.Single]) - off)}, two passes "
                    + $"{Verdict(Median(wall[Mode.Split]) - off)}");
            output.WriteLine($"  resolve counts on frame {counts.CountsFrameIndex}: {counts.DisoccludedPixels} "
                + $"disoccluded, {counts.ClippedPixels} clipped, of {w * h} display pixels");
        }

        static string Label(Mode mode) => mode switch
        {
            Mode.Single => "single pass",
            Mode.Split => "two passes",
            Mode.PassOneAlone => "pass one alone",
            _ => "pass two alone",
        };

        static string Verdict(double ms) => ms < ResolveBudgetMs ? $"{F(ms)} met" : $"{F(ms)} over";

        // A difference of two modes, from their medians and round by round.
        void PrintPair(string what, List<double> a, List<double> b)
        {
            var paired = a.Zip(b, (x, y) => x - y).ToList();
            output.WriteLine($"  {what}: {F(Median(a) - Median(b))} ms from the medians, paired round differences "
                + $"median {F(Median(paired))} min {F(paired.Min())} max {F(paired.Max())}");
        }

        static readonly Color BoxTint = new(0.8f, 0.6f, 0.4f, 1f);

        // The flat wall and twelve keyed boxes drifting right.
        static Action<Scene3D, int> BoxesScene(FrontStage stage) => (s, n) =>
        {
            stage.Wall(s);
            for (int i = 0; i < 12; i++)
                s.Draw(new RigidInstanceDraw(stage.Box,
                    Matrix4x4.CreateTranslation(-3f + 0.5f * i + 0.002f * n, 0.3f * (i % 3), 0f))
                { Tint = BoxTint, Motion = MotionKey.From((ulong)(100 + i)) });
        };

        // The heavy scene, in display pixels: bars this wide every FieldSpacing, swaying up to Sway either side, and a
        // camera that pans Pan a frame while following the wide box.
        const float FieldSpacing = 12f, BarWidth = 1.5f, Sway = 12f, Pan = 2f;
        static readonly Color ColumnTint = new(0.95f, 0.8f, 0.35f, 1f), RowTint = new(0.3f, 0.55f, 0.95f, 1f);
        static readonly Color WideTint = new(0.85f, 0.25f, 0.2f, 1f);

        // The textured wall panning under a follow camera, a wide keyed box the camera follows, in front of the wall,
        // and in front of both a field of keyed bars one internal pixel wide at Quality, vertical ones swaying across
        // and horizontal ones swaying up and down, each on its own phase, so each moves against the wall and the box.
        static Action<Scene3D, int> FieldScene(FrontStage stage)
        {
            float px = stage.PixelWorld;
            float halfW = 0.5f * stage.W * px, halfH = 0.5f * stage.H * px;
            int columns = (int)(stage.W / FieldSpacing), rows = (int)(stage.H / FieldSpacing);
            return (s, n) =>
            {
                float x = Pan * px * n;
                s.Camera.Target = new Vector3(x, 0f, 0f);
                stage.TexturedWall(s);
                s.Draw(new RigidInstanceDraw(stage.Box, Matrix4x4.CreateScale(1.2f * halfW, 1.2f * halfH, 0.05f)
                    * Matrix4x4.CreateTranslation(x, 0f, -1.8f))
                { Tint = WideTint, Motion = MotionKey.From(1) });
                for (int i = 0; i < columns; i++)
                {
                    float bx = x - halfW + ((i + 0.5f) * FieldSpacing + Sway * MathF.Sin(0.2f * n + i)) * px;
                    s.Draw(new RigidInstanceDraw(stage.Box, Matrix4x4.CreateScale(BarWidth * px, 2.2f * halfH, 0.02f)
                        * Matrix4x4.CreateTranslation(bx, 0f, -1.6f))
                    { Tint = ColumnTint, Motion = MotionKey.From((ulong)(1000 + i)) });
                }
                for (int j = 0; j < rows; j++)
                {
                    float by = -halfH + ((j + 0.5f) * FieldSpacing + Sway * MathF.Cos(0.17f * n + j)) * px;
                    s.Draw(new RigidInstanceDraw(stage.Box, Matrix4x4.CreateScale(2.2f * halfW, BarWidth * px, 0.02f)
                        * Matrix4x4.CreateTranslation(x, by, -1.4f))
                    { Tint = RowTint, Motion = MotionKey.From((ulong)(5000 + j)) });
                }
            };
        }
    }
}
