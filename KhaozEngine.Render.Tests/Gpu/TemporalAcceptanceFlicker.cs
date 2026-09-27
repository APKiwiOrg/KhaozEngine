using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The flicker of one frame sequence against its supersampled reference sequence over one region, every rate per
    /// region pixel per frame step. <see cref="TemporalError"/> ranks anti-aliasing modes. The rest explain the rank:
    /// <list type="bullet">
    /// <item><see cref="ReferenceChange"/> is the reference's own mean frame-to-frame change, the temporal error a
    /// frozen image would score, so the most that blur and lag alone can cost.</item>
    /// <item><see cref="Flips"/> and <see cref="FastFlips"/> are the tested sequence's raw and fast flips.</item>
    /// <item><see cref="ReferenceFlips"/> and <see cref="ReferenceFastFlips"/> are the same counts on the reference,
    /// the floor of legitimate crossings.</item>
    /// <item><see cref="Sharpness"/> is the mean local contrast of the tested frames as a share of the reference's,
    /// below 1 when the result is soft.</item>
    /// </list>
    /// </summary>
    internal readonly record struct FlickerStats(double TemporalError, double ReferenceChange, double Flips,
        double FastFlips, double ReferenceFlips, double ReferenceFastFlips, double Sharpness)
    {
        public override string ToString() =>
            $"temporal error {TemporalError:0.00000} (frozen {ReferenceChange:0.00000}), flips {Flips:0.00000} "
            + $"(fast {FastFlips:0.00000}), reference floor {ReferenceFlips:0.00000} (fast "
            + $"{ReferenceFastFlips:0.00000}), sharpness {Sharpness:0.000}";
    }

    internal static partial class TemporalAcceptance
    {
        /// <summary>The per-axis factor of <see cref="ReferenceSequence"/>'s references: 16 box-filtered samples a
        /// pixel.</summary>
        public const int SequenceReferenceFactor = 4;

        /// <summary>Frames <paramref name="warm"/> to <paramref name="warm"/> plus <paramref name="count"/> minus 1
        /// of a fresh <see cref="TemporalFixture"/> run, after <paramref name="warm"/> frames rendered and not read
        /// back.</summary>
        public static byte[][] Sequence(int w, int h, Action<Scene3D> setup, Action<Scene3D, int> draw, int warm,
            int count)
        {
            using var fx = new TemporalFixture(w, h, setup);
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
        /// are skipped, not rendered.
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

        /// <summary>
        /// The temporal error of <paramref name="frames"/> against <paramref name="references"/> of the same frames:
        /// the mean over <paramref name="region"/> and every frame step t of
        /// <c>|(f[t] - f[t - 1]) - (r[t] - r[t - 1])|</c> on luma, 0 to 1.
        /// <para>
        /// It compares how each pixel changes from frame to frame with how the reference changes, not how it looks.
        /// A thin feature legitimately crossing a pixel changes the reference too, so a perfect anti-aliaser scores 0
        /// however many crossings the path holds. Aliasing shimmer adds change the reference lacks. Blur and lag remove
        /// or delay change the reference has. Both count, which raw flips cannot promise. A bias that holds still, such
        /// as the resolve's luma-weighted darkening of a still feature, cancels in the differences. Where the two
        /// sequences agree before rounding, rounding to 8 bits adds at most 2/255 a pixel a step.
        /// </para>
        /// <para>
        /// The cost of removing change is bounded and the cost of adding it is not. A frozen image scores the
        /// reference's own <see cref="MeanChange"/>, however soft or late, while shimmer can score far more. So read
        /// the error beside that bound: a result near it has suppressed the motion rather than tracked it, and
        /// <see cref="Sharpness"/> shows whether it did so by blurring.
        /// </para>
        /// </summary>
        public static double TemporalError(IReadOnlyList<byte[]> frames, IReadOnlyList<byte[]> references, int w,
            int h, PixelRect region)
        {
            if (frames.Count != references.Count)
                throw new ArgumentException("Every frame needs its reference.", nameof(references));
            if (frames.Count < 2) throw new ArgumentException("The error needs two frames or more.", nameof(frames));
            PixelRect r = region.Clip(w, h);
            if (r.Area == 0) return 0;
            double sum = 0;
            for (int t = 1; t < frames.Count; t++)
                for (int y = r.Y0; y < r.Y1; y++)
                    for (int x = r.X0; x < r.X1; x++)
                    {
                        float tested = Luma(frames[t], w, x, y) - Luma(frames[t - 1], w, x, y);
                        float wanted = Luma(references[t], w, x, y) - Luma(references[t - 1], w, x, y);
                        sum += MathF.Abs(tested - wanted);
                    }
            return sum / ((double)r.Area * (frames.Count - 1));
        }

        /// <summary>The mean over <paramref name="region"/> and every frame step of <c>|f[t] - f[t - 1]|</c> on luma.
        /// Of a reference sequence, it is the <see cref="TemporalError"/> a frozen image scores against it.</summary>
        public static double MeanChange(IReadOnlyList<byte[]> frames, int w, int h, PixelRect region)
        {
            if (frames.Count < 2) throw new ArgumentException("The change needs two frames or more.", nameof(frames));
            PixelRect r = region.Clip(w, h);
            if (r.Area == 0) return 0;
            double sum = 0;
            for (int t = 1; t < frames.Count; t++)
                for (int y = r.Y0; y < r.Y1; y++)
                    for (int x = r.X0; x < r.X1; x++)
                        sum += MathF.Abs(Luma(frames[t], w, x, y) - Luma(frames[t - 1], w, x, y));
            return sum / ((double)r.Area * (frames.Count - 1));
        }

        /// <summary><see cref="LocalContrast"/> of <paramref name="frame"/> over <paramref name="region"/> as a share
        /// of its reference's: 1 keeps the reference's detail, below 1 is softer, above 1 is harsher, as aliased edges
        /// and oversharpening are. NaN when the reference has no detail there.</summary>
        public static double Sharpness(byte[] frame, byte[] reference, int w, int h, PixelRect region)
        {
            double wanted = LocalContrast(reference, w, h, region);
            return wanted == 0 ? double.NaN : LocalContrast(frame, w, h, region) / wanted;
        }

        /// <summary>Every flicker measure of <paramref name="frames"/> against <paramref name="references"/> over
        /// <paramref name="region"/>. See <see cref="FlickerStats"/>.</summary>
        public static FlickerStats Flicker(IReadOnlyList<byte[]> frames, IReadOnlyList<byte[]> references, int w, int h,
            PixelRect region)
        {
            double error = TemporalError(frames, references, w, h, region);
            var tested = new FlipCounter(w, h, region);
            var floor = new FlipCounter(w, h, region);
            double sharpness = 0;
            for (int t = 0; t < frames.Count; t++)
            {
                tested.Add(frames[t]);
                floor.Add(references[t]);
                sharpness += Sharpness(frames[t], references[t], w, h, region);
            }
            return new FlickerStats(error, MeanChange(references, w, h, region), tested.FlipsPerPixelPerFrame,
                tested.FastFlipsPerPixelPerFrame, floor.FlipsPerPixelPerFrame, floor.FastFlipsPerPixelPerFrame,
                sharpness / frames.Count);
        }
    }
}
