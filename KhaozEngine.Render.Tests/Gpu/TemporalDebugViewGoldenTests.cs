using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The History, Disocclusion and Reactive debug views over one scene: a grey wall, a keyed red box that holds
    /// still for 16 frames then moves right at 3 display pixels a frame, and a bright particle cloud to the right.
    /// Each view has a golden and a metric the golden grid cannot see, and each test prints its measured metric.
    /// </summary>
    public sealed class TemporalDebugViewGoldenTests(ITestOutputHelper output)
    {
        const int W = 320, H = 180;
        const int Still = 16, Frames = 24;
        static readonly Vector3 BoxSize = new(1.25f, 1.25f, 0.5f);
        static readonly Vector3 Cloud = new(2.6f, 0.6f, 0.5f);

        static float Px => 4.5f / H;
        static Vector3 BoxAt(int n) => new(-2.2f + Math.Max(0, n - Still) * 3f * Px, -0.3f, 0f);

        static void Setup(Scene3D s, AntiAliasing aa, SceneDebugView view, ref MeshHandle box)
        {
            s.Post.UseSmoothPreset();
            s.Post.Quality.AntiAliasing = aa;
            s.Post.Temporal.Upscale = TemporalUpscale.Native;
            s.Post.TransparentBackground = false;
            s.Post.AmbientColor = new Color(0.9f, 0.9f, 0.9f, 1f);
            s.Camera.Azimuth = 0f; s.Camera.Elevation = 0f; s.Camera.AspectRatio = (float)W / H;
            s.Camera.OrthoSize = 4.5f; s.Camera.Target = Vector3.Zero;
            s.DebugView = view;
            box = s.LoadMesh(MeshPrimitives.Box(1f));
        }

        static void Draw(Scene3D s, MeshHandle box, int i)
        {
            s.Draw(box, Matrix4x4.CreateScale(12f, 7f, 0.1f) * Matrix4x4.CreateTranslation(0f, 0f, -2f),
                new Color(0.45f, 0.5f, 0.55f, 1f));
            s.Draw(new RigidInstanceDraw(box, Matrix4x4.CreateScale(BoxSize) * Matrix4x4.CreateTranslation(BoxAt(i)))
            { Tint = new Color(1f, 0.15f, 0.1f, 1f), Motion = MotionKey.From(31) });
            for (int p = 0; p < 24; p++)
                s.DrawParticle(new ParticleSprite
                {
                    Position = Cloud + new Vector3(0.25f * MathF.Cos(p * 0.9f), 0.25f * MathF.Sin(p * 1.3f), 0f),
                    Size = 0.35f,
                    Color = new Color(1f, 0.9f, 0.6f, 1f),
                    Shape = ParticleShape.SoftGlow,
                    Blend = BillboardBlend.Additive,
                    LifeNorm = 0.3f,
                    Seed = p,
                });
        }

        static byte[] Run(AntiAliasing aa, SceneDebugView view, out Scene3D? keep)
        {
            MeshHandle box = default;
            using var fx = new TemporalFixture(W, H, s => Setup(s, aa, view, ref box));
            byte[] last = Array.Empty<byte>();
            for (int n = 0; n < Frames; n++)
                last = fx.Frame((s, i) => Draw(s, box, i));
            keep = null;
            return last;
        }

        // These tests carry the rectangle, the footprint and the mean luma they need.
        readonly record struct PixelRect(int X0, int Y0, int X1, int Y1)
        {
            public bool Contains(int x, int y) => x >= X0 && x < X1 && y >= Y0 && y < Y1;
            public PixelRect Inflate(int n) => new(X0 - n, Y0 - n, X1 + n, Y1 + n);
            public int Area => Math.Max(0, X1 - X0) * Math.Max(0, Y1 - Y0);
        }

        // The screen rectangle of a world box on the front camera, from its eight projected corners.
        static PixelRect Rect(Vector3 centre, Vector3 size)
        {
            var cam = new IsoCamera3D { Azimuth = 0f, Elevation = 0f, AspectRatio = (float)W / H, OrthoSize = 4.5f };
            Vector3 min = centre - size * 0.5f, max = centre + size * 0.5f;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var c = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y,
                    (i & 4) == 0 ? min.Z : max.Z);
                if (!cam.WorldToScreen(c, W, H, out Vector2 p)) continue;
                x0 = MathF.Min(x0, p.X); y0 = MathF.Min(y0, p.Y); x1 = MathF.Max(x1, p.X); y1 = MathF.Max(y1, p.Y);
            }
            return new PixelRect(Math.Clamp((int)MathF.Floor(x0), 0, W), Math.Clamp((int)MathF.Floor(y0), 0, H),
                Math.Clamp((int)MathF.Ceiling(x1), 0, W), Math.Clamp((int)MathF.Ceiling(y1), 0, H));
        }

        // Mean Rec. 709 luma of the 8-bit frame over a rectangle, 0 to 1.
        static double MeanLuma(byte[] rgba, PixelRect r)
        {
            double sum = 0;
            for (int y = r.Y0; y < r.Y1; y++)
                for (int x = r.X0; x < r.X1; x++)
                {
                    int i = (y * W + x) * 4;
                    sum += (0.2126 * rgba[i] + 0.7152 * rgba[i + 1] + 0.0722 * rgba[i + 2]) / 255.0;
                }
            return r.Area == 0 ? 0 : sum / r.Area;
        }

        static double Share(byte[] rgba, PixelRect region, Func<byte, byte, byte, bool> hit)
        {
            int n = 0;
            for (int y = region.Y0; y < region.Y1; y++)
                for (int x = region.X0; x < region.X1; x++)
                {
                    int i = (y * W + x) * 4;
                    if (hit(rgba[i], rgba[i + 1], rgba[i + 2])) n++;
                }
            return region.Area == 0 ? 0 : (double)n / region.Area;
        }

        [GpuFact]
        public void HistoryShowsConvergedWallBrightAndFreshRevealDark()
        {
            byte[] img = Run(AntiAliasing.Temporal, SceneDebugView.History, out _);
            PixelRect calm = new(8, 8, 60, 60);
            PixelRect revealed = Rect(BoxAt(Frames - 2), BoxSize);
            revealed = new PixelRect(revealed.X0, revealed.Y0, Rect(BoxAt(Frames - 1), BoxSize).X0 - 1, revealed.Y1);
            double calmLuma = MeanLuma(img, calm), freshLuma = MeanLuma(img, revealed);
            output.WriteLine($"History: calm wall mean {calmLuma:0.000}, revealed strip {revealed} mean "
                + $"{freshLuma:0.000}");
            Assert.True(calmLuma >= 0.6, $"converged wall must read as full history, mean {calmLuma:0.00}");
            Assert.True(freshLuma <= 0.5,
                $"the strip revealed last frame must read as fresh history, mean {freshLuma:0.00}");
            GoldenCompare.AssertOrUpdate("temporal_debug_history", img, W, H);
        }

        [GpuFact]
        public void DisocclusionPaintsTheRevealedStripRed()
        {
            byte[] img = Run(AntiAliasing.Temporal, SceneDebugView.Disocclusion, out _);
            PixelRect now = Rect(BoxAt(Frames - 1), BoxSize), before = Rect(BoxAt(Frames - 2), BoxSize);
            var strip = new PixelRect(before.X0, before.Y0 + 1, now.X0, before.Y1 - 1);
            static bool Red(byte r, byte g, byte b) => r > 200 && g < 80 && b < 80;
            double inside = Share(img, strip, Red);
            double outside = 0;
            int outsideHits = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    if (strip.Inflate(3).Contains(x, y)) continue;
                    int i = (y * W + x) * 4;
                    if (Red(img[i], img[i + 1], img[i + 2])) outsideHits++;
                }
            outside = (double)outsideHits / (W * H);
            output.WriteLine($"Disocclusion: strip {strip} red share {inside:0.000}, elsewhere {outside:P3}");
            Assert.True(inside >= 0.5, $"at least half the revealed strip must read disoccluded, got {inside:0.00}");
            Assert.True(outside <= 0.002,
                $"disocclusion must stay near the reveal, {outside:P2} of the frame elsewhere");
            GoldenCompare.AssertOrUpdate("temporal_debug_disocclusion", img, W, H);
        }

        [GpuFact]
        public void ReactiveMarksTheParticleCloud()
        {
            byte[] img = Run(AntiAliasing.Temporal, SceneDebugView.Reactive, out _);
            PixelRect cloud = Rect(Cloud, new Vector3(0.4f, 0.4f, 0.1f));
            static bool Yellow(byte r, byte g, byte b) => r > 170 && g > 140 && b < 110;
            double inside = Share(img, cloud, Yellow);
            double wall = Share(img, new PixelRect(8, 8, 60, 60), Yellow);
            output.WriteLine($"Reactive: cloud core {cloud} yellow share {inside:0.000}, still wall {wall:0.000}");
            Assert.True(inside >= 0.5, $"the particle cloud's core must read reactive, got {inside:0.00}");
            Assert.True(wall <= 0.01, "the still wall must not read reactive");
            GoldenCompare.AssertOrUpdate("temporal_debug_reactive", img, W, H);
        }

        // The frame's last image and, inside the same frame, a later render at the display size into a scratch target,
        // as an offscreen capture makes.
        static (byte[] First, byte[] Later) RunWithLaterRender(SceneDebugView view)
        {
            MeshHandle box = default;
            using var fx = new TemporalFixture(W, H, s => Setup(s, AntiAliasing.Temporal, view, ref box));
            byte[] first = Array.Empty<byte>();
            for (int n = 0; n < Frames; n++)
                first = fx.Frame((s, i) => Draw(s, box, i));
            return (first, fx.RenderSecond(W, H));
        }

        /// <summary>The view describes the frame's first render, the one the resolve ran on. A later render of the
        /// frame is unjittered and unresolved, so it draws no view and shows the ordinary image.</summary>
        [GpuFact]
        public void ALaterRenderInTheFrameShowsTheOrdinaryImage()
        {
            var (plainFirst, plainLater) = RunWithLaterRender(SceneDebugView.None);
            var (viewFirst, viewLater) = RunWithLaterRender(SceneDebugView.History);
            Assert.False(plainFirst.AsSpan().SequenceEqual(viewFirst), "the frame's first render must draw the view");
            Assert.True(plainLater.AsSpan().SequenceEqual(viewLater),
                "a later render in the frame must show the ordinary image, not the view");
        }

        [GpuFact]
        public void TheViewsAreIgnoredOutsideTemporalAntiAliasing()
        {
            byte[] plain = Run(AntiAliasing.Fxaa, SceneDebugView.None, out _);
            byte[] history = Run(AntiAliasing.Fxaa, SceneDebugView.History, out _);
            Assert.True(plain.AsSpan().SequenceEqual(history), "History under FXAA must render the ordinary frame");
        }
    }
}
