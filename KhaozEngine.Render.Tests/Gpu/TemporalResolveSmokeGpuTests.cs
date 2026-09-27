using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A smoke over whole frames, ahead of the temporal acceptance tests: a still scene converges, the resolved image
    /// stays upright, the converged image is anti-aliased, keeps its brightness and is not one jittered frame, and the
    /// upscaling presets land close to native. Relative same-session measurements, no goldens. Measured on Metal: the
    /// frame-to-frame change falls from 1.393 to 0.119, the marker sits within half a row of the aliased frame, the
    /// converged frame is 0.718 from the reference supersampled 4x per axis against the aliased frame's 2.582, 2.139
    /// from a single jittered frame, and keeps a mean luma of 30.2, and Quality and UltraPerformance land 1.353 and
    /// 2.072 from native with the marker on the same row. Only Metal was measured. The anti-aliasing gate is the
    /// tightest, 1.8x over its measured value (0.718 against a limit of 1.291), and every other gate leaves a wider
    /// margin. Every message prints the measured values.
    /// </summary>
    public sealed class TemporalResolveSmokeGpuTests
    {
        const int W = 240, H = 240, Bars = 15;

        // cutEveryFrame calls CameraCut before every frame, so the last frame is one jittered frame with no history, at
        // the same jitter phase as an uncut render of the same length.
        static byte[] Render(AntiAliasing aa, int frames, TemporalUpscale upscale = TemporalUpscale.Native,
            bool cutEveryFrame = false)
        {
            MeshHandle box = default;
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    scene.Post.UseSmoothPreset();
                    scene.Post.RenderScale = RenderScale.MatchViewport;   // 1:1 unless temporal forces its own scale
                    scene.Post.Quality.AntiAliasing = aa;
                    scene.Post.Temporal.Upscale = upscale;
                    scene.Post.TransparentBackground = false;
                    scene.Camera.Azimuth = 0f; scene.Camera.Elevation = 0f;
                    scene.Camera.AspectRatio = 1f; scene.Camera.OrthoSize = 4.4f; scene.Camera.Target = Vector3.Zero;
                    box = scene.LoadMesh(MeshPrimitives.Box(1f));
                },
                drawFrame: scene =>
                {
                    if (cutEveryFrame) scene.CameraCut();
                    for (int i = 0; i < Bars; i++)
                    {
                        float x = -2.6f + 5.2f * i / (Bars - 1);
                        scene.Draw(box, Matrix4x4.CreateScale(0.08f, 3f, 0.08f) * Matrix4x4.CreateRotationZ(0.52f)
                            * Matrix4x4.CreateTranslation(x, -0.5f, 0f), new Color(0.92f, 0.92f, 0.95f, 1f));
                    }
                    // The orientation marker: a red block in the top quarter of the view (OrthoSize is the full height).
                    scene.Draw(box, Matrix4x4.CreateScale(2.4f, 0.4f, 0.2f) * Matrix4x4.CreateTranslation(0f, 1.7f, 0.5f),
                        new Color(0.9f, 0.2f, 0.15f, 1f));
                },
                frames: frames);
        }

        [GpuFact]
        public void A_still_scene_converges_upright_anti_aliased_and_unlike_a_single_jittered_frame()
        {
            byte[] off = Render(AntiAliasing.Off, 2);
            byte[] reference = Render(AntiAliasing.Ssaa(4f), 2);
            byte[] first = Render(AntiAliasing.Temporal, 1);
            byte[] second = Render(AntiAliasing.Temporal, 2);
            byte[] converged = Render(AntiAliasing.Temporal, 32);
            byte[] next = Render(AntiAliasing.Temporal, 33);
            byte[] single = Render(AntiAliasing.Temporal, 32, cutEveryFrame: true);   // frame 32's phase, no history

            double early = MeanAbs(first, second), late = MeanAbs(converged, next);
            double offFromReference = MeanAbs(off, reference), taaFromReference = MeanAbs(converged, reference);
            string ctx = $"(early={early:0.000} late={late:0.000} refOff={offFromReference:0.000} refTaa={taaFromReference:0.000} "
                + $"midOff={MidCount(off)} midTaa={MidCount(converged)} "
                + $"fromSingle={MeanAbs(converged, single):0.000} meanOff={MeanLuma(off):0.0} meanTaa={MeanLuma(converged):0.0} "
                + $"markerOff={MarkerRow(off):0.0} markerTaa={MarkerRow(converged):0.0})";

            Assert.True(MarkerRow(off) < H / 4.0, "the marker is not where the scene put it " + ctx);
            Assert.True(Math.Abs(MarkerRow(converged) - MarkerRow(off)) < 2.0, "the resolved image is not upright " + ctx);
            Assert.True(late < 1.5 && late <= 0.5 * early, "the history does not settle on a still scene " + ctx);
            // Anti-aliasing is judged by the distance to a reference supersampled 4x per axis, not by a count of
            // mid-luma edge pixels. With anti-aliasing off the bars' interiors already sit inside the counted band, so
            // every real anti-aliasing mode, the reference included at 1.092x, fell below a 1.15x count gate. The count
            // stays in the message as a reported number. The strict convergence acceptance against 8x supersampling
            // belongs to the temporal acceptance tests.
            Assert.True(taaFromReference < 0.5 * offFromReference, "the converged frame is not anti-aliased " + ctx);
            Assert.True(MeanAbs(converged, single) > 0.1, "the converged frame is still a single jittered frame " + ctx);
            Assert.True(Math.Abs(MeanLuma(converged) - MeanLuma(off)) < Math.Max(2.0, MeanLuma(off) * 0.05),
                "the resolve changed the image's brightness " + ctx);
        }

        [GpuFact]
        public void The_upscaling_presets_land_close_to_native_and_upright()
        {
            byte[] native = Render(AntiAliasing.Temporal, 32);
            byte[] quality = Render(AntiAliasing.Temporal, 48, TemporalUpscale.Quality);
            byte[] ultra = Render(AntiAliasing.Temporal, 150, TemporalUpscale.UltraPerformance);
            string ctx = $"(quality={MeanAbs(quality, native):0.000} ultra={MeanAbs(ultra, native):0.000} "
                + $"markerNative={MarkerRow(native):0.0} markerQuality={MarkerRow(quality):0.0} markerUltra={MarkerRow(ultra):0.0})";

            Assert.True(MarkerRow(native) < H / 4.0, "the native frame is not upright " + ctx);
            Assert.True(MeanAbs(quality, native) < 8.0, "Quality drifted from native " + ctx);
            Assert.True(MeanAbs(ultra, native) < 16.0, "UltraPerformance drifted from native " + ctx);
            Assert.True(Math.Abs(MarkerRow(quality) - MarkerRow(native)) < 3.0, "Quality is not upright " + ctx);
            Assert.True(Math.Abs(MarkerRow(ultra) - MarkerRow(native)) < 4.0, "UltraPerformance is not upright " + ctx);
        }

        static double Luma(byte[] p, int i) => 0.299 * p[i] + 0.587 * p[i + 1] + 0.114 * p[i + 2];

        static double MeanAbs(byte[] a, byte[] b)
        {
            double sum = 0;
            for (int i = 0; i < a.Length; i++)
                if ((i & 3) != 3) sum += Math.Abs(a[i] - b[i]);
            return sum / (a.Length / 4 * 3);
        }

        // Pixels whose luma lies between 40 and 190. With anti-aliasing off the bars' interiors and the marker already
        // fall inside this band, so the count is reported, never gated.
        static int MidCount(byte[] rgba)
        {
            int n = 0;
            for (int p = 0; p < rgba.Length / 4; p++)
            {
                double l = Luma(rgba, p * 4);
                if (l > 40 && l < 190) n++;
            }
            return n;
        }

        static double MeanLuma(byte[] rgba)
        {
            double s = 0;
            int n = rgba.Length / 4;
            for (int p = 0; p < n; p++) s += Luma(rgba, p * 4);
            return s / n;
        }

        // The mean row of the red marker's pixels, row 0 at the top of the image.
        static double MarkerRow(byte[] rgba)
        {
            double rows = 0;
            int count = 0;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = (y * W + x) * 4;
                    if (rgba[i] > 120 && rgba[i] > 2 * rgba[i + 1]) { rows += y; count++; }
                }
            return count == 0 ? double.NaN : rows / count;
        }
    }
}
