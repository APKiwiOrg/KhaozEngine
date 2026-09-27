using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The sharpen joins the post chain without disturbing it: built only while temporal anti-aliasing sharpens,
    /// the frame stays upright, the edge outline stays on the silhouette (both flip parities count the pass), and
    /// it raises edge contrast. The HDR order runs it after the tonemap and the legacy order first, so the chain
    /// facts run in both. It sharpens only the display chain of a resolving frame: a later render inside that frame
    /// and a NaN sharpness leave the output byte-identical to a chain without it. It follows the display targets
    /// whenever they are rebuilt.
    /// </summary>
    public sealed class TemporalSharpenChainGpuTests
    {
        const int W = 160, H = 90;

        sealed class Rig
        {
            MeshHandle _box;

            public void Setup(Scene3D s, AntiAliasing aa, float sharpness, bool outline, bool hdr = true)
            {
                s.Post.UseSmoothPreset();
                s.Post.Hdr.Enabled = hdr;
                s.Post.Outline = outline;
                // A hue nothing else has, so a softened line still reads.
                s.Post.OutlineColor = new Color(0f, 1f, 0f, 1f);
                s.Post.Quality.AntiAliasing = aa;
                s.Post.Temporal.Upscale = TemporalUpscale.Native;
                s.Post.Temporal.Sharpness = sharpness;
                s.Post.TransparentBackground = false;
                s.Post.BackgroundColor = new Color(0.75f, 0.75f, 0.75f, 1f);
                s.Post.AmbientColor = new Color(0.6f, 0.6f, 0.6f, 1f);
                s.Camera.Azimuth = 0f; s.Camera.Elevation = 0f; s.Camera.AspectRatio = (float)W / H;
                s.Camera.OrthoSize = 4.5f; s.Camera.Target = Vector3.Zero;
                _box = s.LoadMesh(MeshPrimitives.Box(1f));
            }

            // A tall box upper left and a short one lower right: no vertical symmetry to hide a flip.
            public void Draw(Scene3D s, int frame)
            {
                s.Draw(_box, Matrix4x4.CreateScale(1.2f, 2.2f, 1f) * Matrix4x4.CreateTranslation(-2.2f, 0.8f, 0f),
                    new Color(0.95f, 0.3f, 0.2f, 1f));
                s.Draw(_box, Matrix4x4.CreateScale(0.8f, 0.6f, 1f) * Matrix4x4.CreateTranslation(2.4f, -1.4f, 0f),
                    new Color(0.2f, 0.35f, 0.9f, 1f));
            }
        }

        static byte[] Render(AntiAliasing aa, float sharpness, bool outline, out bool sharpenBuilt, bool hdr = true)
        {
            var rig = new Rig();
            using var fx = new TemporalFixture(W, H, s => rig.Setup(s, aa, sharpness, outline, hdr));
            byte[] last = Array.Empty<byte>();
            for (int i = 0; i < 16; i++) last = fx.Frame(rig.Draw);
            sharpenBuilt = fx.Scene.TemporalSharpenBuiltForTests;
            return last;
        }

        static float Luma(byte[] p, int i) => (0.2126f * p[i] + 0.7152f * p[i + 1] + 0.0722f * p[i + 2]) / 255f;

        // The green outline, by hue, which keeps reading a line the chain has softened.
        static bool IsOutline(byte[] p, int i) => p[i + 1] > p[i] + 60 && p[i + 1] > p[i + 2] + 60;

        static (double X, double Y) RedCentroid(byte[] rgba)
        {
            double sx = 0, sy = 0; int n = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = (y * W + x) * 4;
                    if (rgba[i] > 150 && rgba[i + 2] < 110) { sx += x; sy += y; n++; }
                }
            Assert.True(n > 100, "the red box did not render");
            return (sx / n, sy / n);
        }

        [GpuFact]
        public void TheSharpenIsBuiltOnlyWhileTemporalSharpens()
        {
            Render(AntiAliasing.Temporal, 0f, outline: false, out bool atZero);
            Render(AntiAliasing.Temporal, 0.25f, outline: false, out bool atDefault);
            Render(AntiAliasing.Temporal, 0.25f, outline: false, out bool inLegacy, hdr: false);
            Render(AntiAliasing.Fxaa, 0.25f, outline: false, out bool underFxaa);
            Assert.False(atZero, "Sharpness 0 must skip the pass without building it");
            Assert.True(atDefault, "Sharpness 0.25 under temporal anti-aliasing must build and run the pass");
            Assert.True(inLegacy, "the legacy order must build and run the pass too");
            Assert.False(underFxaa, "FXAA must never build the temporal sharpen");
        }

        [GpuTheory]
        [InlineData(true)]
        [InlineData(false)]
        public void SharpeningKeepsTheFrameUpright(bool hdr)
        {
            var plain = RedCentroid(Render(AntiAliasing.Temporal, 0f, false, out _, hdr));
            var sharp = RedCentroid(Render(AntiAliasing.Temporal, 0.25f, false, out _, hdr));
            Assert.True(Math.Abs(plain.X - sharp.X) < 0.5 && Math.Abs(plain.Y - sharp.Y) < 0.5,
                $"the tall red box moved when the sharpen joined the chain: {plain} against {sharp}");
            Assert.True(sharp.Y < H / 2, $"the tall red box must sit in the upper half, found y {sharp.Y:0.0}");
        }

        [GpuTheory]
        [InlineData(true)]
        [InlineData(false)]
        public void SharpeningKeepsTheOutlineOnTheSilhouette(bool hdr)
        {
            byte[] plain = Render(AntiAliasing.Temporal, 0f, true, out _, hdr);
            byte[] sharp = Render(AntiAliasing.Temporal, 0.25f, true, out _, hdr);
            int both = 0, either = 0;
            for (int i = 0; i < plain.Length; i += 4)
            {
                bool a = IsOutline(plain, i), b = IsOutline(sharp, i);
                if (a && b) both++;
                if (a || b) either++;
            }
            double iou = either == 0 ? 0 : (double)both / either;
            Assert.True(either > 50, "the outline pass drew nothing, so the parity check measured nothing");
            Assert.True(iou >= 0.9, $"outline pixels moved when the sharpen joined the chain: IoU {iou:0.00}");
        }

        [GpuTheory]
        [InlineData(true)]
        [InlineData(false)]
        public void SharpeningRaisesEdgeContrast(bool hdr)
        {
            static double Gradient(byte[] p)
            {
                double sum = 0;
                for (int y = 0; y < H; y++)
                    for (int x = 0; x + 1 < W; x++)
                        sum += Math.Abs(Luma(p, (y * W + x + 1) * 4) - Luma(p, (y * W + x) * 4));
                return sum;
            }
            double g0 = Gradient(Render(AntiAliasing.Temporal, 0f, false, out _, hdr));
            double g1 = Gradient(Render(AntiAliasing.Temporal, 1f, false, out _, hdr));
            Assert.True(g1 > g0 * 1.05, $"Sharpness 1 must raise the summed edge gradient by 5%: {g0:0.0} to {g1:0.0}");
        }

        [GpuFact]
        public void ANaNSharpnessIsOffAndLeavesTheFrameAsSharpnessZeroDoes()
        {
            byte[] zero = Render(AntiAliasing.Temporal, 0f, false, out _);
            byte[] nan = Render(AntiAliasing.Temporal, float.NaN, false, out bool built);
            Assert.False(built, "a NaN sharpness must not build the pass");
            Assert.True(zero.AsSpan().SequenceEqual(nan), "a NaN sharpness changed the frame a sharpness of 0 renders");
        }

        /// <summary>A later render inside a resolving frame, such as an offscreen capture, runs the internal chain on a
        /// post chain of its own. The sharpen belongs to the display chain after the resolve, so the later chain never
        /// builds it and the capture at the strongest sharpness is byte-identical to the capture at 0.</summary>
        [GpuTheory]
        [InlineData(true)]
        [InlineData(false)]
        public void ALaterRenderInAResolvingFrameIsNeverSharpened(bool hdr)
        {
            static (byte[] Capture, bool DisplayBuilt, bool LaterBuilt) Captures(float sharpness, bool hdr)
            {
                var rig = new Rig();
                using var fx = new TemporalFixture(W, H,
                    s => rig.Setup(s, AntiAliasing.Temporal, sharpness, false, hdr));
                byte[] capture = Array.Empty<byte>();
                for (int i = 0; i < 8; i++)
                {
                    fx.Frame(rig.Draw);
                    capture = fx.RenderSecond(W, H);
                }
                Assert.True(fx.Scene.LaterRenderPostCreatedForTests, "the later render did not run its own chain");
                return (capture, fx.Scene.TemporalSharpenBuiltForTests, fx.Scene.LaterRenderSharpenBuiltForTests);
            }
            var plain = Captures(0f, hdr);
            var sharp = Captures(1f, hdr);
            Assert.True(sharp.DisplayBuilt, "the display chain of the resolving render must build the pass");
            Assert.False(sharp.LaterBuilt, "a later render's internal chain must never build the pass");
            Assert.True(plain.Capture.AsSpan().SequenceEqual(sharp.Capture),
                "the sharpen reached a later render: the capture changed with the sharpness");
        }

        /// <summary>The pass caches a set per texture it reads. Whenever the display targets are rebuilt, by a resize
        /// or the HDR toggle, it holds sets over the new targets only and draws with a pipeline built for their colour
        /// format, and once the resolve stops it holds no set. The legacy order starts it, so the pass reads the
        /// history colours as well as the pings.</summary>
        [GpuFact]
        public void TheSharpenFollowsTheDisplayTargetsWheneverTheyAreRebuilt()
        {
            var rig = new Rig();
            using var fx = new TemporalFixture(W, H,
                s => rig.Setup(s, AntiAliasing.Temporal, 0.25f, false, hdr: false));
            Scene3D scene = fx.Scene;

            void HoldsOnlyCurrent(string when)
            {
                TemporalPostTargets t = scene.TemporalPostTargetsForTests!;
                TemporalSharpenPass pass = scene.TemporalSharpenForTests!;
                IGpuTexture[] current = { t.PingA, t.PingB, t.Source(0), t.Source(1) };
                IReadOnlyCollection<IGpuTexture> held = pass.CachedSources;
                Assert.True(held.Count > 0, $"{when}: the pass holds no set, so the check saw nothing");
                foreach (IGpuTexture texture in held)
                    Assert.True(Array.Exists(current, c => ReferenceEquals(c, texture)),
                        $"{when}: the pass holds a set over a texture the display targets no longer report");
                Assert.Equal(t.PingAFB.Outputs.Colour[0], pass.OutputFormat);
            }

            fx.Frames(4, rig.Draw);
            HoldsOnlyCurrent("legacy");
            fx.Resize(W + 32, H + 18);
            fx.Frames(3, rig.Draw);
            HoldsOnlyCurrent("after a resize");
            scene.Post.Hdr.Enabled = true;
            fx.Frames(3, rig.Draw);
            HoldsOnlyCurrent("after the HDR toggle");
            scene.Post.Quality.AntiAliasing = AntiAliasing.Fxaa;
            fx.Frames(1, rig.Draw);
            Assert.True(scene.TemporalSharpenBuiltForTests, "the pass is kept for the next time the resolve runs");
            Assert.Empty(scene.TemporalSharpenForTests!.CachedSources);
        }
    }
}
