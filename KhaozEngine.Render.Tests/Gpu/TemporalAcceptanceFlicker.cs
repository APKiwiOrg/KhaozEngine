using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// How a frame sequence f changes against its reference sequence r over one region, with <c>df</c> and
    /// <c>dr</c> the luma change of a pixel from one frame to the next. Every value is a mean over the region's pixels
    /// and the frame steps, on luma from 0 to 1.
    /// <list type="bullet">
    /// <item><see cref="Error"/>: <c>|df - dr|</c>, the temporal error.</item>
    /// <item><see cref="OwnChange"/>: <c>|df|</c>.</item>
    /// <item><see cref="ReferenceChange"/>: <c>|dr|</c>, the error a frozen image scores.</item>
    /// <item><see cref="Added"/>: <c>max(|df| - |dr|, 0)</c>, change the reference lacks. Shimmer adds it, and so
    /// does blur that spreads a change sideways.</item>
    /// <item><see cref="Removed"/>: <c>max(|dr| - |df|, 0)</c>, change the output lacks. Blur and suppression remove
    /// it.</item>
    /// </list>
    /// <see cref="Added"/> minus <see cref="Removed"/> is <see cref="OwnChange"/> minus <see cref="ReferenceChange"/>,
    /// and their sum is at most <see cref="Error"/>.
    /// </summary>
    internal readonly record struct ChangeStats(double Error, double OwnChange, double ReferenceChange, double Added,
        double Removed);

    /// <summary>
    /// The flicker of one frame sequence against its supersampled reference sequence over one region.
    /// <list type="bullet">
    /// <item><see cref="TemporalError"/>, <see cref="ReferenceChange"/>, <see cref="OwnChange"/>,
    /// <see cref="AddedChange"/> and <see cref="RemovedChange"/> are the full-resolution <see cref="ChangeStats"/>.
    /// <see cref="ReferenceChange"/> is the error of a frozen image.</item>
    /// <item><see cref="LowPassedError"/> is the temporal error after a 5 by 5 box low-pass of both sequences, which
    /// keeps swimming and lag and drops change that stays within a few pixels.</item>
    /// <item><see cref="Flips"/> and <see cref="FastFlips"/> are the tested sequence's raw and fast flips per pixel
    /// per frame, and <see cref="ReferenceFlips"/> and <see cref="ReferenceFastFlips"/> the same counts on the
    /// reference, the floor of legitimate crossings.</item>
    /// <item><see cref="Sharpness"/> is the tested frames' summed local contrast as a share of the reference's, below
    /// 1 when the result is soft.</item>
    /// <item><see cref="Energy"/> is the tested frames' summed luma above the background as a share of the
    /// reference's. Blur keeps it, and a bias or a clip does not.</item>
    /// </list>
    /// </summary>
    internal readonly record struct FlickerStats(double TemporalError, double ReferenceChange, double OwnChange,
        double AddedChange, double RemovedChange, double LowPassedError, double Flips, double FastFlips,
        double ReferenceFlips, double ReferenceFastFlips, double Sharpness, double Energy)
    {
        public override string ToString() =>
            $"error {TemporalError:0.00000} (frozen {ReferenceChange:0.00000}), own {OwnChange:0.00000}, added "
            + $"{AddedChange:0.00000}, removed {RemovedChange:0.00000}, 5x5 error {LowPassedError:0.00000}, flips "
            + $"{Flips:0.00000} (fast {FastFlips:0.00000}), reference floor {ReferenceFlips:0.00000} (fast "
            + $"{ReferenceFastFlips:0.00000}), sharpness {Sharpness:0.000}, energy {Energy:0.000}";
    }

    internal static partial class TemporalAcceptance
    {
        /// <summary>The per-axis factor of <see cref="ReferenceSequence"/>'s references: 16 box-filtered samples a
        /// pixel.</summary>
        public const int SequenceReferenceFactor = 4;

        /// <summary>The radius of <see cref="LowPassedError"/>'s box low-pass: 2, a 5 by 5 box.</summary>
        public const int LowPassRadius = 2;

        /// <summary>Frames <paramref name="warm"/> to <paramref name="warm"/> plus <paramref name="count"/> minus 1
        /// of a fresh <see cref="TemporalFixture"/> run, after <paramref name="warm"/> frames rendered and not read
        /// back, its jitter sequence started <paramref name="startPhase"/> phases on
        /// (<see cref="TemporalFixture.SkipPhases"/>).</summary>
        public static byte[][] Sequence(int w, int h, Action<Scene3D> setup, Action<Scene3D, int> draw, int warm,
            int count, int startPhase = 0)
        {
            using var fx = new TemporalFixture(w, h, setup);
            fx.SkipPhases(startPhase);
            fx.Frames(warm, draw);
            var frames = new byte[count][];
            for (int i = 0; i < count; i++) frames[i] = fx.Frame(draw);
            return frames;
        }

        /// <summary>
        /// The supersampled reference of every frame from <paramref name="firstFrame"/> to
        /// <paramref name="firstFrame"/> plus <paramref name="count"/> minus 1 of a camera path, one reference per
        /// frame, so it lines up with a <see cref="Sequence"/> whose warm count is <paramref name="firstFrame"/>. Each
        /// frame is rendered as <see cref="Supersampled"/> renders one, with anti-aliasing off at
        /// <paramref name="factor"/> times the size and the CPU box filter, at the same frame number and effect clock.
        /// With anti-aliasing off nothing carries between frames, so the frames before <paramref name="firstFrame"/>
        /// are skipped, not rendered. Each frame reads back a fresh RGBA8 image at the larger size, 14.7 MB at 8x of
        /// 320 by 180, through <see cref="TemporalFixture.Frame"/>.
        /// </summary>
        public static byte[][] ReferenceSequence(int w, int h, Action<Scene3D> setup, Action<Scene3D, int> draw,
            int firstFrame, int count, int factor = SequenceReferenceFactor)
        {
            using TemporalFixture fx = ReferenceFixture(w, h, factor, setup);
            fx.SkipFrames(firstFrame);
            var references = new byte[count][];
            for (int i = 0; i < count; i++)
                references[i] = Rgba8Stats.BoxDownsample(fx.Frame(draw), w * factor, h * factor, factor);
            return references;
        }

        /// <summary>The one-frame-lag control: <paramref name="references"/> one frame late, frame t holding the
        /// reference of frame t minus 1 and the first holding <paramref name="before"/>, the reference of the frame
        /// before the window. It is <see cref="ReferenceSequence"/> started one frame earlier, for one extra reference
        /// frame. Its <see cref="TemporalError"/> against <paramref name="references"/> is the error of an output that
        /// tracks the reference exactly, one frame late.</summary>
        public static byte[][] LaggedReference(IReadOnlyList<byte[]> references, byte[] before)
        {
            var lagged = new byte[references.Count][];
            lagged[0] = before;
            for (int t = 1; t < lagged.Length; t++) lagged[t] = references[t - 1];
            return lagged;
        }

        /// <summary>
        /// The temporal error of <paramref name="frames"/> against <paramref name="references"/> of the same frames:
        /// the mean over <paramref name="region"/> and every frame step t of
        /// <c>|(f[t] - f[t - 1]) - (r[t] - r[t - 1])|</c> on luma, 0 to 1.
        /// <para>
        /// It compares how each pixel changes from frame to frame with how the reference changes, not how it looks.
        /// A thin feature legitimately crossing a pixel changes the reference too, so a perfect anti-aliaser scores 0
        /// however many crossings the path holds. Aliasing shimmer adds change the reference lacks. Blur and lag remove
        /// or delay change the reference has. Both count, which raw flips cannot promise. Where the two sequences agree
        /// before rounding, rounding to 8 bits adds at most 2/255 a pixel a step.
        /// </para>
        /// <para>
        /// A frozen image scores the reference's own change, <see cref="MeanChange"/> of the references. That is not
        /// the most blur and lag can cost. By the triangle inequality <c>|df - dr|</c> is at most <c>|df| + |dr|</c>,
        /// so an output that never changes more than the reference scores at most twice the frozen score, and a
        /// one-frame lag of isolated steps reaches it (<see cref="LaggedReference"/>). Measured on the fence pan, a
        /// Gaussian blur of sigma 1.5 pixels scores 1.16 times frozen, a two-frame lag 1.29 times and a four-frame lag
        /// 1.91 times.
        /// Shimmer has no such bound. Read <see cref="ChangeStats.Added"/> for flicker and
        /// <see cref="ChangeStats.Removed"/> and <see cref="Sharpness"/> for blur.
        /// </para>
        /// <para>
        /// A bias cancels in the differences only while the content under it holds still. On a moving feature a
        /// contrast scaled by k about a still background gives <c>df = k dr</c>, which costs <c>|1 - k|</c> times the
        /// frozen score there, so the resolve's luma-weighted darkening of a thin bright feature shows in the error
        /// once the feature moves.
        /// </para>
        /// </summary>
        public static double TemporalError(IReadOnlyList<byte[]> frames, IReadOnlyList<byte[]> references, int w,
            int h, PixelRect region) => Changes(frames, references, w, h, region).Error;

        /// <summary>The <see cref="TemporalError"/> after a box low-pass of <paramref name="radius"/> pixels, 5 by 5
        /// by default, of both sequences' luma, kept in float with clamped edges. Change that moves a few pixels
        /// sideways, as blur spreads it, cancels. Swimming and lag across a wider area stay.</summary>
        public static double LowPassedError(IReadOnlyList<byte[]> frames, IReadOnlyList<byte[]> references, int w,
            int h, PixelRect region, int radius = LowPassRadius) =>
            Changes(frames, references, w, h, region, radius).Error;

        /// <summary>The mean of <c>max(|df| - |dr|, 0)</c>: change the reference lacks. See
        /// <see cref="ChangeStats"/>.</summary>
        public static double AddedChange(IReadOnlyList<byte[]> frames, IReadOnlyList<byte[]> references, int w, int h,
            PixelRect region) => Changes(frames, references, w, h, region).Added;

        /// <summary>The mean of <c>max(|dr| - |df|, 0)</c>: change the output lacks. See
        /// <see cref="ChangeStats"/>.</summary>
        public static double RemovedChange(IReadOnlyList<byte[]> frames, IReadOnlyList<byte[]> references, int w,
            int h, PixelRect region) => Changes(frames, references, w, h, region).Removed;

        /// <summary>The mean over <paramref name="region"/> and every frame step of <c>|f[t] - f[t - 1]|</c> on luma,
        /// the sequence's own change. Of a reference sequence, it is the <see cref="TemporalError"/> a frozen image
        /// scores against it.</summary>
        public static double MeanChange(IReadOnlyList<byte[]> frames, int w, int h, PixelRect region) =>
            Changes(frames, frames, w, h, region).OwnChange;

        /// <summary>The same as <see cref="MeanChange"/>, named for a tested sequence.</summary>
        public static double OwnChange(IReadOnlyList<byte[]> frames, int w, int h, PixelRect region) =>
            MeanChange(frames, w, h, region);

        /// <summary>Every <see cref="ChangeStats"/> measure of <paramref name="frames"/> against
        /// <paramref name="references"/>, after a box low-pass of <paramref name="radius"/> pixels when it is above
        /// 0.</summary>
        public static ChangeStats Changes(IReadOnlyList<byte[]> frames, IReadOnlyList<byte[]> references, int w, int h,
            PixelRect region, int radius = 0)
        {
            if (frames.Count != references.Count)
                throw new ArgumentException("Every frame needs its reference.", nameof(references));
            if (frames.Count < 2) throw new ArgumentException("The measure needs two frames or more.", nameof(frames));
            PixelRect r = region.Clip(w, h);
            if (r.Area == 0) return default;
            float[] fPrevious = LumaImage(frames[0], w, h, radius), rPrevious = LumaImage(references[0], w, h, radius);
            double error = 0, own = 0, wanted = 0, added = 0, removed = 0;
            for (int t = 1; t < frames.Count; t++)
            {
                float[] f = LumaImage(frames[t], w, h, radius), g = LumaImage(references[t], w, h, radius);
                for (int y = r.Y0; y < r.Y1; y++)
                    for (int x = r.X0; x < r.X1; x++)
                    {
                        int p = y * w + x;
                        float df = f[p] - fPrevious[p], dr = g[p] - rPrevious[p];
                        float af = MathF.Abs(df), ar = MathF.Abs(dr);
                        error += MathF.Abs(df - dr);
                        own += af;
                        wanted += ar;
                        added += MathF.Max(af - ar, 0f);
                        removed += MathF.Max(ar - af, 0f);
                    }
                fPrevious = f;
                rPrevious = g;
            }
            double n = (double)r.Area * (frames.Count - 1);
            return new ChangeStats(error / n, own / n, wanted / n, added / n, removed / n);
        }

        /// <summary><see cref="LocalContrast"/> of <paramref name="frame"/> over <paramref name="region"/> as a share
        /// of its reference's: 1 keeps the reference's detail, below 1 is softer, above 1 is harsher, as aliased edges
        /// and oversharpening are. NaN when the reference has no detail there.</summary>
        public static double Sharpness(byte[] frame, byte[] reference, int w, int h, PixelRect region)
        {
            double wanted = LocalContrast(reference, w, h, region);
            return wanted == 0 ? double.NaN : LocalContrast(frame, w, h, region) / wanted;
        }

        /// <summary>The luma above the background summed over <paramref name="region"/>, the background being each
        /// image's own luma at (<paramref name="backgroundX"/>, <paramref name="backgroundY"/>), which must lie outside
        /// the features.</summary>
        public static double LumaAboveBackground(byte[] rgba, int w, int h, PixelRect region, int backgroundX = 2,
            int backgroundY = 2)
        {
            PixelRect r = region.Clip(w, h);
            float background = Luma(rgba, w, backgroundX, backgroundY);
            double sum = 0;
            for (int y = r.Y0; y < r.Y1; y++)
                for (int x = r.X0; x < r.X1; x++) sum += Luma(rgba, w, x, y) - background;
            return sum;
        }

        /// <summary>The energy of <paramref name="frame"/>: its luma above the background over
        /// <paramref name="region"/> as a share of its reference's, each against its own background pixel. Blur keeps
        /// it, and a bias or a clip does not. NaN when the reference holds nothing above its background.</summary>
        public static double Energy(byte[] frame, byte[] reference, int w, int h, PixelRect region, int backgroundX = 2,
            int backgroundY = 2)
        {
            double wanted = LumaAboveBackground(reference, w, h, region, backgroundX, backgroundY);
            return wanted == 0 ? double.NaN
                : LumaAboveBackground(frame, w, h, region, backgroundX, backgroundY) / wanted;
        }

        /// <summary>A reference pixel whose luma differs from its background pixel's by more than this is covered by
        /// the object: half an 8-bit step, above the rounding of the reference's box filter.</summary>
        public const float CoverageLumaStep = 0.5f / 255f;

        /// <summary>
        /// The energy over each reference frame's own coverage, and over the ring around it. A frame's coverage is
        /// the pixels of <paramref name="region"/> whose reference luma differs from the reference's background pixel
        /// by more than <see cref="CoverageLumaStep"/>, and its ring the pixels next to the coverage along a row, a
        /// column or a diagonal, where the reference shows only background. <c>Coverage</c> is the tested frames' luma
        /// above their background over each frame's coverage as a share of the references', summed over the frames as
        /// <see cref="Flicker"/> sums the energy. <c>Ring</c> is the tested frames' luma above their background over
        /// each frame's ring as a share of the same reference sum: luma the result keeps where the reference has
        /// nothing, as a smear or a trail does. Both are NaN when the references cover nothing.
        /// <para>
        /// The background must be one luma. The coverage is every pixel whose reference luma differs from the
        /// background pixel's, so over a textured background it takes in the texture, and neither value measures the
        /// object. The method cannot tell: it returns a number there all the same, so a caller whose scene has a
        /// textured background passes NaN in its place and prints it as not applicable, as the fast-edge runs do for
        /// the box over the textured wall.
        /// </para>
        /// </summary>
        public static (double Coverage, double Ring) CoverageEnergy(IReadOnlyList<byte[]> frames,
            IReadOnlyList<byte[]> references, int w, int h, PixelRect region, int backgroundX = 2, int backgroundY = 2)
        {
            PixelRect r = region.Clip(w, h);
            var inside = new bool[w * h];
            double covered = 0, ring = 0, wanted = 0;
            for (int t = 0; t < frames.Count; t++)
            {
                float tested = Luma(frames[t], w, backgroundX, backgroundY);
                float background = Luma(references[t], w, backgroundX, backgroundY);
                Array.Clear(inside);
                for (int y = r.Y0; y < r.Y1; y++)
                    for (int x = r.X0; x < r.X1; x++)
                        inside[y * w + x] = MathF.Abs(Luma(references[t], w, x, y) - background) > CoverageLumaStep;
                for (int y = r.Y0; y < r.Y1; y++)
                    for (int x = r.X0; x < r.X1; x++)
                    {
                        double above = Luma(frames[t], w, x, y) - tested;
                        if (inside[y * w + x])
                        {
                            covered += above;
                            wanted += Luma(references[t], w, x, y) - background;
                            continue;
                        }
                        bool beside = false;
                        for (int dy = -1; dy <= 1 && !beside; dy++)
                            for (int dx = -1; dx <= 1 && !beside; dx++)
                            {
                                int ny = Math.Clamp(y + dy, r.Y0, r.Y1 - 1), nx = Math.Clamp(x + dx, r.X0, r.X1 - 1);
                                beside = inside[ny * w + nx];
                            }
                        if (beside) ring += above;
                    }
            }
            return wanted == 0 ? (double.NaN, double.NaN) : (covered / wanted, ring / wanted);
        }

        /// <summary>
        /// Every flicker measure of <paramref name="frames"/> against <paramref name="references"/> over
        /// <paramref name="region"/>. See <see cref="FlickerStats"/>. Sharpness and energy are ratios of sums over the
        /// frames, so one flat reference frame cannot make them NaN and near-flat frames do not dominate. Where the
        /// reference holds steady, as on every path here, that equals the mean of the per-frame ratios. Energy takes
        /// the background at pixel (2, 2).
        /// </summary>
        public static FlickerStats Flicker(IReadOnlyList<byte[]> frames, IReadOnlyList<byte[]> references, int w, int h,
            PixelRect region)
        {
            ChangeStats full = Changes(frames, references, w, h, region);
            double lowPassed = Changes(frames, references, w, h, region, LowPassRadius).Error;
            var tested = new FlipCounter(w, h, region);
            var floor = new FlipCounter(w, h, region);
            double contrast = 0, wantedContrast = 0, energy = 0, wantedEnergy = 0;
            for (int t = 0; t < frames.Count; t++)
            {
                tested.Add(frames[t]);
                floor.Add(references[t]);
                contrast += LocalContrast(frames[t], w, h, region);
                wantedContrast += LocalContrast(references[t], w, h, region);
                energy += LumaAboveBackground(frames[t], w, h, region);
                wantedEnergy += LumaAboveBackground(references[t], w, h, region);
            }
            return new FlickerStats(full.Error, full.ReferenceChange, full.OwnChange, full.Added, full.Removed,
                lowPassed, tested.FlipsPerPixelPerFrame, tested.FastFlipsPerPixelPerFrame, floor.FlipsPerPixelPerFrame,
                floor.FastFlipsPerPixelPerFrame, wantedContrast == 0 ? double.NaN : contrast / wantedContrast,
                wantedEnergy == 0 ? double.NaN : energy / wantedEnergy);
        }

        // The image's luma, box-filtered over (2 radius + 1) squared pixels with clamped edges when radius is above 0,
        // in float so the low-pass does not round away the change it measures. The box is separable, rows then columns.
        static float[] LumaImage(byte[] rgba, int w, int h, int radius)
        {
            var luma = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++) luma[y * w + x] = Luma(rgba, w, x, y);
            if (radius <= 0) return luma;
            var rows = new float[w * h];
            var box = new float[w * h];
            float n = 2 * radius + 1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float s = 0;
                    for (int d = -radius; d <= radius; d++) s += luma[y * w + Math.Clamp(x + d, 0, w - 1)];
                    rows[y * w + x] = s;
                }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float s = 0;
                    for (int d = -radius; d <= radius; d++) s += rows[Math.Clamp(y + d, 0, h - 1) * w + x];
                    box[y * w + x] = s / (n * n);
                }
            return box;
        }
    }
}
