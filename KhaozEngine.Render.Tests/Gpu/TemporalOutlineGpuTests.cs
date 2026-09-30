using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The toon edge outline under temporal anti-aliasing. It runs ahead of the resolve, so under a still camera it
    /// converges to steady pixels instead of lines that move with the jitter, it lies where the FXAA frame draws it,
    /// and the frame stays upright. The outline is green, a hue nothing else in the frame has.
    /// </summary>
    public sealed class TemporalOutlineGpuTests
    {
        const int W = 320, H = 180, Warm = 16, Measured = 16;

        // Acceptance thresholds, first estimates the first real-Metal run retunes in one place.
        const int MinBoxPixels = 100;            // the blue box has to cover at least this many pixels
        const int MinChainOutlinePixels = 100;   // FXAA's own outline has to be visible at all
        const double MinOutlineShare = 0.5;      // resolved and shared outline pixels, as a share of FXAA's
        const double MaxStillFlipRate = 0.002;   // flips per pixel per frame for a converged still outline
        const int UprightRowMargin = 20;         // rows below the middle the low blue box sits when upright

        readonly ITestOutputHelper _out;
        public TemporalOutlineGpuTests(ITestOutputHelper output) => _out = output;

        static void Setup(FrontStage stage, Scene3D s, AntiAliasing aa)
        {
            stage.Setup(s, aa, TemporalUpscale.Native, sharpness: 0f);
            s.Post.Outline = true;   // after FrontStage's UseSmoothPreset, which turns it off
            s.Post.OutlineColor = new Color(0f, 1f, 0f, 1f);
        }

        // A tall box left of centre and a flat one low on the right, both turned so their silhouettes cross pixels at
        // an angle, over the empty background, whose depth step is the one the edge test finds. A wall behind them
        // would sit too near in depth for it.
        static void Draw(FrontStage stage, Scene3D s)
        {
            s.Draw(stage.Box, Matrix4x4.CreateScale(1.1f, 2.4f, 1f) * Matrix4x4.CreateRotationZ(0.3f)
                * Matrix4x4.CreateTranslation(-1.4f, 0.2f, 0f), new Color(0.85f, 0.55f, 0.35f, 1f));
            s.Draw(stage.Box, Matrix4x4.CreateScale(2.2f, 0.7f, 1f) * Matrix4x4.CreateRotationZ(-0.2f)
                * Matrix4x4.CreateTranslation(1.6f, -0.9f, 0f), new Color(0.35f, 0.45f, 0.85f, 1f));
        }

        static bool IsOutline(byte[] p, int i) => p[i + 1] > p[i] + 60 && p[i + 1] > p[i + 2] + 60;

        static int CountOutline(byte[] rgba)
        {
            int n = 0;
            for (int i = 0; i < rgba.Length; i += 4) if (IsOutline(rgba, i)) n++;
            return n;
        }

        // The mean row of the blue box's pixels: below the middle when the frame is upright.
        static double BlueRow(byte[] rgba)
        {
            double sum = 0;
            int n = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = (y * W + x) * 4;
                    if (rgba[i + 2] > rgba[i] + 60 && rgba[i + 2] > rgba[i + 1] + 30) { sum += y; n++; }
                }
            Assert.True(n > MinBoxPixels, $"the blue box covered only {n} pixels");
            return sum / n;
        }

        // The reactive estimate a pixel of the Reactive debug view shows: it mixes the dimmed grey scene towards
        // (1, 0.85, 0.1) by the estimate, so red less blue is 0.9 of it (TemporalDebugMainGlsl).
        static float Reactive(byte[] view, int i) => (view[i] - view[i + 2]) / (0.9f * 255f);

        // A pixel the resolve reads as transparent content. Low, because the control's overlay blends faintly: it
        // peaks near 0.3, where an unoutlined opaque copy would leave a green line against the box's colour, a far
        // larger difference, at the clamp of 1.
        const float ReactiveMark = 0.1f;
        const int MinControlReactivePixels = 500;   // the control's overlay must mark at least this many

        /// <summary>
        /// The outline pass writes the outlined opaque copy with the same edge as the outlined scene colour, so the
        /// resolve's reactive estimate, which reads the difference between the two as transparent content, marks no
        /// outline pixel. Read through the Reactive debug view on a converged still frame. The control draws a faint
        /// white translucent overlay over the tall box, which lands in the scene colour after the opaque copy, the
        /// same kind of difference an unoutlined opaque copy would leave under every line and a smaller one, and the
        /// view must mark it.
        /// </summary>
        [GpuFact]
        public void TheResolveReadsNoOutlinePixelAsTransparentContent()
        {
            var stage = new FrontStage(W, H, 4.5f);
            (int OnOutline, int Anywhere, float Max, int Outline, float FrameMax) Run(bool overlay)
            {
                using var fx = new TemporalFixture(W, H, s => Setup(stage, s, AntiAliasing.Temporal));
                Action<Scene3D, int> draw = (s, _) =>
                {
                    Draw(stage, s);
                    if (overlay)
                        s.DrawOverlayMesh(stage.Box, Matrix4x4.CreateScale(0.8f, 1.6f, 0.2f)
                            * Matrix4x4.CreateRotationZ(0.3f) * Matrix4x4.CreateTranslation(-1.4f, 0.2f, 1f));
                };
                fx.Frames(Warm, draw);
                byte[] frame = fx.Frame(draw);
                fx.Scene.DebugView = SceneDebugView.Reactive;
                byte[] view = fx.Frame(draw);
                int onOutline = 0, anywhere = 0, outline = 0;
                float max = 0f, frameMax = 0f;
                for (int i = 0; i < view.Length; i += 4)
                {
                    float r = Reactive(view, i);
                    bool marked = r >= ReactiveMark;
                    if (marked) anywhere++;
                    frameMax = MathF.Max(frameMax, r);
                    if (!IsOutline(frame, i)) continue;
                    outline++;
                    max = MathF.Max(max, r);
                    if (marked) onOutline++;
                }
                return (onOutline, anywhere, max, outline, frameMax);
            }

            var still = Run(overlay: false);
            var control = Run(overlay: true);
            _out.WriteLine($"reactive under the outline: {still.OnOutline} of {still.Outline} outline pixels marked, largest "
                + $"{still.Max:0.000}, {still.Anywhere} marked in the frame. Control with a translucent overlay: "
                + $"{control.Anywhere} marked in the frame, largest {control.FrameMax:0.000}");
            Assert.True(still.Outline > MinChainOutlinePixels, $"the frame drew only {still.Outline} outline pixels");
            Assert.True(still.OnOutline == 0, $"the resolve read {still.OnOutline} outline pixels as transparent content");
            Assert.True(control.Anywhere >= MinControlReactivePixels,
                $"the control's overlay marked only {control.Anywhere} pixels, so the view cannot see this difference");
        }

        [GpuFact]
        public void UnderTemporalAntiAliasingAStillOutlineConvergesToSteadyPixels()
        {
            var stage = new FrontStage(W, H, 4.5f);
            var region = new PixelRect(4, 4, W - 4, H - 4);
            var flips = new FlipCounter(W, H, region);
            byte[] last = Array.Empty<byte>();
            using (var fx = new TemporalFixture(W, H, s => Setup(stage, s, AntiAliasing.Temporal)))
            {
                fx.Frames(Warm, (s, _) => Draw(stage, s));
                for (int i = 0; i < Measured; i++)
                {
                    last = fx.Frame((s, _) => Draw(stage, s));
                    flips.Add(last);
                }
                Assert.True(fx.Scene.TemporalOutlineBuiltForTests, "temporal anti-aliasing with the outline on must build the pass");
            }

            byte[] fxaa;
            using (var plain = new TemporalFixture(W, H, s => Setup(stage, s, AntiAliasing.Fxaa)))
            {
                fxaa = plain.Frame((s, _) => Draw(stage, s));
                Assert.False(plain.Scene.TemporalOutlineBuiltForTests, "FXAA must run the chain's own outline and build nothing");
            }

            int resolved = CountOutline(last), chain = CountOutline(fxaa), both = 0;
            for (int i = 0; i < last.Length; i += 4) if (IsOutline(last, i) && IsOutline(fxaa, i)) both++;
            double rate = flips.FlipsPerPixelPerFrame;
            _out.WriteLine($"outline under temporal anti-aliasing, still camera: {rate:0.00000} flips per pixel per frame "
                + $"over {Measured} frames, {resolved} outline pixels against FXAA's {chain}, {both} shared");
            Assert.True(chain > MinChainOutlinePixels, $"the FXAA frame drew only {chain} outline pixels");
            Assert.True(resolved >= chain * MinOutlineShare, $"the resolved outline must stay visible: {resolved} pixels against FXAA's {chain}");
            Assert.True(both >= chain * MinOutlineShare, $"the resolved outline must lie where FXAA's does: {both} of {chain} shared");
            Assert.True(rate <= MaxStillFlipRate, $"a still outline must converge: {rate:0.00000} flips per pixel per frame");
            Assert.True(BlueRow(last) > H / 2 + UprightRowMargin, $"the frame must stay upright: the low blue box sits at row {BlueRow(last):0.0}");
        }
    }
}
