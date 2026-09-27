using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>An integer pixel rectangle, inclusive minimum, exclusive maximum, top-left origin.</summary>
    internal readonly record struct PixelRect(int X0, int Y0, int X1, int Y1)
    {
        public bool Contains(int x, int y) => x >= X0 && x < X1 && y >= Y0 && y < Y1;
        public PixelRect Inflate(int n) => new(X0 - n, Y0 - n, X1 + n, Y1 + n);
        public int Area => Math.Max(0, X1 - X0) * Math.Max(0, Y1 - Y0);
        public PixelRect Clip(int w, int h) =>
            new(Math.Clamp(X0, 0, w), Math.Clamp(Y0, 0, h), Math.Clamp(X1, 0, w), Math.Clamp(Y1, 0, h));
    }

    /// <summary>How two pixels of RGBA8 images are compared, on a 0 to 1 scale.</summary>
    internal enum PixelDifference
    {
        /// <summary>The absolute difference of their Rec. 709 luma.</summary>
        Luma,

        /// <summary>The largest absolute difference of their red, green and blue bytes, over 255. It also sees a change
        /// of hue that keeps the luma, such as a red tint left on a grey wall of the same brightness.</summary>
        MaxChannel,
    }

    /// <summary>
    /// The per-pixel sign-flip flicker metric of the grass shimmer probe, counted over a region of RGBA8 frames added
    /// in order. For each pixel it takes the Rec. 709 luma of the stored bytes, <c>(0.2126 R + 0.7152 G + 0.0722 B) /
    /// 255</c>, and the change from the frame added just before. A change larger than <see cref="Threshold"/> is a
    /// step, and a smaller one is ignored. A flip is a step whose sign is the opposite of that pixel's previous step.
    /// The first frame only sets the starting luma, the first step only sets the sign, and a pixel keeps its sign
    /// across any number of ignored changes. So a pixel toggling between blade and ground flips on every reversal, a
    /// pixel crossed once by a moving edge steps once and never flips, and a slow drift or a ramp never flips at
    /// all.
    /// <para>
    /// The threshold, 0.03, is 7.65 steps of 8-bit luma. Rounding the output to 8 bits moves luma by at most one step,
    /// under 0.004, and the half-float history rounds a display value under 1 by less than 0.0005, so rounding noise on
    /// any backend never makes a step. Compare rates from one backend in one session only, such as a temporal run
    /// against an MSAA 4x run of the same path.
    /// </para>
    /// </summary>
    internal sealed class FlipCounter
    {
        public const float Threshold = 0.03f;
        readonly int _w;
        readonly PixelRect _region;
        readonly float[] _previous;
        readonly sbyte[] _sign;
        int _frames;

        /// <summary>A counter over <paramref name="region"/>, clipped to the <paramref name="width"/> by
        /// <paramref name="height"/> frames it will be given.</summary>
        public FlipCounter(int width, int height, PixelRect region)
        {
            _w = width;
            _region = region.Clip(width, height);
            _previous = new float[width * height];
            _sign = new sbyte[width * height];
        }

        /// <summary>Flips so far, summed over the region.</summary>
        public long Flips { get; private set; }

        /// <summary>Flips per region pixel per frame step: <see cref="Flips"/> over the clipped region's area times
        /// one less than the frames added.</summary>
        public double FlipsPerPixelPerFrame =>
            _frames <= 1 || _region.Area == 0 ? 0 : Flips / ((double)_region.Area * (_frames - 1));

        public void Add(byte[] rgba)
        {
            int frame = _frames++;
            for (int y = _region.Y0; y < _region.Y1; y++)
                for (int x = _region.X0; x < _region.X1; x++)
                {
                    int p = y * _w + x;
                    float luma = TemporalAcceptance.Luma(rgba, _w, x, y);
                    if (frame == 0) { _previous[p] = luma; continue; }
                    float delta = luma - _previous[p];
                    _previous[p] = luma;
                    if (MathF.Abs(delta) <= Threshold) continue;
                    sbyte sign = delta > 0f ? (sbyte)1 : (sbyte)-1;
                    if (_sign[p] != 0 && _sign[p] != sign) Flips++;
                    _sign[p] = sign;
                }
        }
    }

    /// <summary>
    /// The measures and frame sources the temporal acceptance tests share. Luma is Rec. 709 on 8-bit values, 0 to 1.
    /// Every measure compares frames from one session, never a stored image. Supersampled references come from
    /// <see cref="Supersampled"/>, never from the engine's supersampling mode.
    /// </summary>
    internal static class TemporalAcceptance
    {
        /// <summary>The largest width or height a <see cref="Supersampled"/> reference renders at, so a reference stays
        /// cheap and inside every backend's texture limits.</summary>
        public const int MaxReferenceSide = 8192;

        public static float Luma(byte[] rgba, int w, int x, int y)
        {
            int i = (y * w + x) * 4;
            return (0.2126f * rgba[i] + 0.7152f * rgba[i + 1] + 0.0722f * rgba[i + 2]) / 255f;
        }

        /// <summary>The difference of pixel (<paramref name="x"/>, <paramref name="y"/>) between two same-size images,
        /// 0 to 1.</summary>
        public static float Difference(byte[] a, byte[] b, int w, int x, int y, PixelDifference measure)
        {
            if (measure == PixelDifference.Luma) return MathF.Abs(Luma(a, w, x, y) - Luma(b, w, x, y));
            int i = (y * w + x) * 4;
            int d = Math.Max(Math.Abs(a[i] - b[i]),
                Math.Max(Math.Abs(a[i + 1] - b[i + 1]), Math.Abs(a[i + 2] - b[i + 2])));
            return d / 255f;
        }

        public static double MeanLuma(byte[] rgba, int w, PixelRect region)
        {
            double sum = 0;
            for (int y = region.Y0; y < region.Y1; y++)
                for (int x = region.X0; x < region.X1; x++) sum += Luma(rgba, w, x, y);
            return region.Area == 0 ? 0 : sum / region.Area;
        }

        public static double MeanAbsLuma(byte[] a, byte[] b, int w, PixelRect region)
        {
            double sum = 0;
            for (int y = region.Y0; y < region.Y1; y++)
                for (int x = region.X0; x < region.X1; x++) sum += MathF.Abs(Luma(a, w, x, y) - Luma(b, w, x, y));
            return region.Area == 0 ? 0 : sum / region.Area;
        }

        /// <summary>The screen rectangle of a world box, from its eight projected corners, clipped to the image. It
        /// holds every pixel the box touches, the partly covered ones included.</summary>
        public static PixelRect Footprint(IIsoCamera3D camera, Vector3 min, Vector3 max, int w, int h)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var c = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y,
                    (i & 4) == 0 ? min.Z : max.Z);
                if (!camera.WorldToScreen(c, w, h, out Vector2 p)) continue;
                x0 = MathF.Min(x0, p.X); y0 = MathF.Min(y0, p.Y); x1 = MathF.Max(x1, p.X); y1 = MathF.Max(y1, p.Y);
            }
            var rect = new PixelRect((int)MathF.Floor(x0), (int)MathF.Floor(y0), (int)MathF.Ceiling(x1),
                (int)MathF.Ceiling(y1));
            return rect.Clip(w, h);
        }

        /// <summary>
        /// The ghosting measure of TEMPORAL-RESOLVE-UPSCALING-DESIGN section 7 item 3. <paramref name="footprints"/>
        /// holds the object's rectangle now, one frame ago, two frames ago and so on, with an empty rectangle for a
        /// frame it was not drawn. A checked pixel is one the object last covered two or more frames ago and that lies
        /// more than one pixel from where it is now. It counts as trail when it differs from the same pixel of the
        /// background frame, rendered without the object at the same frame index, by more than
        /// <paramref name="tolerance"/> under <paramref name="measure"/>.
        /// </summary>
        public static (int Over, int Checked, float Worst) Trail(byte[] frame, byte[] background, int w, int h,
            IReadOnlyList<PixelRect> footprints, float tolerance, PixelDifference measure = PixelDifference.Luma)
        {
            PixelRect now = footprints[0].Inflate(1), previous = footprints[1];
            int over = 0, check = 0;
            float worst = 0f;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (now.Contains(x, y) || previous.Contains(x, y)) continue;
                    bool passed = false;
                    for (int k = 2; k < footprints.Count && !passed; k++) passed = footprints[k].Contains(x, y);
                    if (!passed) continue;
                    check++;
                    float d = Difference(frame, background, w, x, y, measure);
                    worst = MathF.Max(worst, d);
                    if (d > tolerance) over++;
                }
            return (over, check, worst);
        }

        /// <summary>Pixels differing from a same-pose reference by more than <paramref name="tolerance"/> that are not
        /// within one pixel of a reference edge (a four-neighbour luma step above <paramref name="edgeStep"/>). A
        /// frame that lags its own camera shows its old edges there.</summary>
        public static int GhostPixels(byte[] frame, byte[] reference, int w, int h, float tolerance, float edgeStep)
        {
            var edge = new bool[w * h];
            for (int y = 1; y < h - 1; y++)
                for (int x = 1; x < w - 1; x++)
                {
                    float c = Luma(reference, w, x, y);
                    if (MathF.Abs(c - Luma(reference, w, x + 1, y)) <= edgeStep
                        && MathF.Abs(c - Luma(reference, w, x, y + 1)) <= edgeStep) continue;
                    for (int dy = -1; dy <= 2; dy++)
                        for (int dx = -1; dx <= 2; dx++)
                            edge[Math.Clamp(y + dy, 0, h - 1) * w + Math.Clamp(x + dx, 0, w - 1)] = true;
                }
            int ghosts = 0;
            for (int y = 1; y < h - 1; y++)
                for (int x = 1; x < w - 1; x++)
                    if (!edge[y * w + x] && MathF.Abs(Luma(frame, w, x, y) - Luma(reference, w, x, y)) > tolerance)
                        ghosts++;
            return ghosts;
        }

        /// <summary>Bilinear upscale with pixel-centre alignment and clamped edges, the filter a bilinear blit
        /// applies.</summary>
        public static byte[] BilinearUpscale(byte[] rgba, int sw, int sh, int dw, int dh)
        {
            var dst = new byte[dw * dh * 4];
            for (int y = 0; y < dh; y++)
                for (int x = 0; x < dw; x++)
                {
                    float sx = Math.Clamp((x + 0.5f) * sw / dw - 0.5f, 0f, sw - 1f);
                    float sy = Math.Clamp((y + 0.5f) * sh / dh - 0.5f, 0f, sh - 1f);
                    int x0 = (int)sx, y0 = (int)sy, x1 = Math.Min(x0 + 1, sw - 1), y1 = Math.Min(y0 + 1, sh - 1);
                    float fx = sx - x0, fy = sy - y0;
                    for (int c = 0; c < 4; c++)
                    {
                        float top = rgba[(y0 * sw + x0) * 4 + c] * (1 - fx) + rgba[(y0 * sw + x1) * 4 + c] * fx;
                        float bottom = rgba[(y1 * sw + x0) * 4 + c] * (1 - fx) + rgba[(y1 * sw + x1) * 4 + c] * fx;
                        float v = MathF.Round(top * (1 - fy) + bottom * fy);
                        dst[(y * dw + x) * 4 + c] = (byte)Math.Clamp(v, 0f, 255f);
                    }
                }
            return dst;
        }

        /// <summary>Mean absolute four-neighbour Laplacian of luma over <paramref name="region"/>: texture
        /// detail.</summary>
        public static double LocalContrast(byte[] rgba, int w, int h, PixelRect region)
        {
            var r = new PixelRect(Math.Max(1, region.X0), Math.Max(1, region.Y0), Math.Min(w - 1, region.X1),
                Math.Min(h - 1, region.Y1));
            double sum = 0;
            for (int y = r.Y0; y < r.Y1; y++)
                for (int x = r.X0; x < r.X1; x++)
                    sum += MathF.Abs(Luma(rgba, w, x, y) - 0.25f * (Luma(rgba, w, x - 1, y) + Luma(rgba, w, x + 1, y)
                        + Luma(rgba, w, x, y - 1) + Luma(rgba, w, x, y + 1)));
            return r.Area == 0 ? 0 : sum / r.Area;
        }

        /// <summary>
        /// The first frame of a fresh <see cref="TemporalFixture"/> at <paramref name="w"/> by <paramref name="h"/>,
        /// for a single frame under anti-aliasing off, FXAA or MSAA. It refuses the engine's supersampling mode, whose
        /// capture on Direct3D 11 is barely anti-aliased (#1175), so a reference is always built by
        /// <see cref="Supersampled"/>.
        /// </summary>
        public static byte[] Snapshot(int w, int h, Action<Scene3D> setup, Action<Scene3D> draw)
        {
            using var fx = new TemporalFixture(w, h, setup);
            if (fx.Scene.Post.Quality.AntiAliasing.Mode == AntiAliasingMode.Ssaa)
                throw new ArgumentException("Build a supersampled reference with Supersampled, not the SSAA mode.",
                    nameof(setup));
            return fx.Frame((s, _) => draw(s));
        }

        /// <summary>
        /// A supersampled reference of frame <paramref name="frame"/> at <paramref name="w"/> by <paramref name="h"/>.
        /// A fresh <see cref="TemporalFixture"/> <paramref name="factor"/> times larger per axis runs
        /// <paramref name="setup"/>, then anti-aliasing is forced off at a 1:1 internal size, with no debug view and no
        /// forced temporal rendering. The render cap is raised to the reference size for this render alone, so the cap
        /// never shrinks it. The fixture skips to <paramref name="frame"/>, so the draw and the effect clock see the
        /// same frame number as the frame it is compared with, and the one rendered frame is box-filtered on the CPU
        /// by <see cref="Rgba8Stats.BoxDownsample"/>.
        /// <para>
        /// Each output pixel is the rounded mean of the <paramref name="factor"/> squared samples inside it, after the
        /// tonemap, the same display-space area average the engine's mip-filtered supersampling blit takes. The
        /// scene's camera frames the same view at every size, because its world extent does not depend on the pixel
        /// count. A feature sized in pixels, such as a pixel-wide line or the foliage density scale, renders
        /// differently at the larger size, so a reference scene avoids them.
        /// </para>
        /// </summary>
        public static byte[] Supersampled(int w, int h, int factor, Action<Scene3D> setup, Action<Scene3D, int> draw,
            int frame = 0)
        {
            if (factor < 1)
                throw new ArgumentOutOfRangeException(nameof(factor), factor, "The factor must be at least 1.");
            int rw = w * factor, rh = h * factor;
            if (rw > MaxReferenceSide || rh > MaxReferenceSide)
                throw new ArgumentOutOfRangeException(nameof(factor), factor,
                    $"A {rw} by {rh} reference is larger than {MaxReferenceSide} a side.");
            using var fx = new TemporalFixture(rw, rh, s =>
            {
                setup(s);
                s.Post.Quality.AntiAliasing = AntiAliasing.Off;
                s.Post.RenderScale = RenderScale.MatchViewport;
                s.Post.Supersample = 1f;
                s.Post.Pixelated = false;
                s.Post.MaxRenderWidth = Math.Max(s.Post.MaxRenderWidth, rw);
                s.Post.MaxRenderHeight = Math.Max(s.Post.MaxRenderHeight, rh);
                s.ForceTemporalForTests = false;
                s.DebugView = SceneDebugView.None;
            });
            fx.SkipFrames(frame);
            return Rgba8Stats.BoxDownsample(fx.Frame(draw), rw, rh, factor);
        }

        /// <summary>Flips per pixel per frame over <paramref name="region"/> for <paramref name="measured"/> frames
        /// after <paramref name="warm"/> unmeasured ones, on a fresh fixture. See <see cref="FlipCounter"/>.</summary>
        public static double FlipRate(int w, int h, Action<Scene3D> setup, Action<Scene3D, int> draw, PixelRect region,
            int warm, int measured)
        {
            using var fx = new TemporalFixture(w, h, setup);
            fx.Frames(warm, draw);
            var flips = new FlipCounter(w, h, region);
            for (int i = 0; i < measured; i++) flips.Add(fx.Frame(draw));
            return flips.FlipsPerPixelPerFrame;
        }
    }
}
