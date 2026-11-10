using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Display-space change and normal-line edge tracking against a contemporaneous spatial reference.</summary>
internal static class DaylightShadowMeasure
{
    const int W = DaylightShadowStage.W, H = DaylightShadowStage.H;
    internal sealed record Pair(int Frame, bool Refit, double Change, double ReferenceChange);
    internal sealed record Result(int BandPixels, int OutsidePixels, int StableReferencePixels, int Sparkle,
        double MeanChange, double ReferenceMeanChange, double MaxChange, double ReferenceMaxChange,
        double SpikeRatio, double ReferenceSpikeRatio, bool LargestOnRefit, int TopEightOnRefit,
        double MeanLagPixels, double RmsPixels, double DeviationPixels, double ReferenceContrast);

    internal static Result Measure(byte[][] frames, byte[][] references, bool[] refits)
    {
        double[] positions = frames.Select(Edge).Select(x => x.Position).ToArray();
        var refEdges = references.Select(Edge).ToArray();
        double[] refPositions = refEdges.Select(x => x.Position).ToArray();
        double low = refPositions.Min() - 6, high = refPositions.Max() + 6;
        var band = new List<int>();
        var outside = new List<int>();
        for (int y = 16; y < H - 16; y++)
            for (int x = 16; x < W - 16; x++)
            {
                double distance = Vector2.Dot(new Vector2(x + 0.5f - W / 2f, y + 0.5f - H / 2f),
                    DaylightShadowStage.Normal);
                (distance >= low && distance <= high ? band : outside).Add(y * W + x);
            }
        var pairs = new Pair[frames.Length - 1];
        for (int i = 1; i < frames.Length; i++)
        {
            double change = 0, refChange = 0;
            foreach (int p in band)
            {
                change += Math.Abs(Luma(frames[i], p) - Luma(frames[i - 1], p));
                refChange += Math.Abs(Luma(references[i], p) - Luma(references[i - 1], p));
            }
            pairs[i - 1] = new Pair(DaylightShadowStage.Warm + i, refits[i], change / band.Count,
                refChange / band.Count);
        }
        int sparkle = 0, stable = 0;
        foreach (int p in outside)
        {
            if (Range(references, p) >= 0.005) continue;
            stable++;
            if (Range(frames, p) > 0.03) sparkle++;
        }
        double[] delta = positions.Zip(refPositions, (a, b) => a - b).ToArray();
        double mean = delta.Average();
        Pair[] ranked = pairs.OrderByDescending(p => p.Change).ToArray();
        return new Result(band.Count, outside.Count, stable, sparkle, pairs.Average(p => p.Change),
            pairs.Average(p => p.ReferenceChange), ranked[0].Change, pairs.Max(p => p.ReferenceChange),
            Spike(pairs.Select(p => p.Change)), Spike(pairs.Select(p => p.ReferenceChange)),
            ranked[0].Refit, ranked.Take(8).Count(p => p.Refit), mean,
            Math.Sqrt(delta.Average(d => d * d)), Math.Sqrt(delta.Average(d => (d - mean) * (d - mean))),
            refEdges.Average(x => x.Contrast));
    }

    static double Range(byte[][] frames, int p)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (byte[] f in frames) { double v = Luma(f, p); min = Math.Min(min, v); max = Math.Max(max, v); }
        return max - min;
    }

    static double Spike(IEnumerable<double> values)
    {
        double max = values.Max(), median = Median(values);
        // A frozen reference has neither a spike nor motion. NaN names the undefined nonzero-over-zero case.
        return median > 0 ? max / median : max == 0 ? 1 : double.NaN;
    }

    static double Median(IEnumerable<double> values)
    {
        double[] xs = values.Order().ToArray();
        return xs.Length % 2 == 0 ? (xs[xs.Length / 2 - 1] + xs[xs.Length / 2]) / 2 : xs[xs.Length / 2];
    }

    internal static double Luma(byte[] rgba, int p) =>
        (0.2126 * rgba[p * 4] + 0.7152 * rgba[p * 4 + 1] + 0.0722 * rgba[p * 4 + 2]) / 255;

    // Average parallel normal lines over a 160-pixel tangent segment to remove the ridge carrier. Find the
    // 50 percent crossing between this frame's own lit and shadow plateaus, with sub-pixel interpolation.
    static (double Position, double Contrast) Edge(byte[] frame)
    {
        const double Step = 0.25;
        var profile = new double[257];
        for (int j = 0; j < profile.Length; j++)
        {
            float d = (float)(-32 + j * Step);
            for (int t = -80; t <= 80; t++)
            {
                Vector2 p = new Vector2(W / 2f, H / 2f) + DaylightShadowStage.Normal * d
                    + DaylightShadowStage.Along * t;
                profile[j] += Sample(frame, p);
            }
            profile[j] /= 161;
        }
        double lit = profile.Take(32).Average(), shadow = profile.TakeLast(32).Average();
        double mid = (lit + shadow) / 2;
        double best = double.NaN, closest = double.PositiveInfinity;
        for (int j = 1; j < profile.Length; j++)
        {
            if (!(profile[j - 1] >= mid && profile[j] < mid)) continue;
            double crossing = -32 + (j - 1 + (profile[j - 1] - mid) / (profile[j - 1] - profile[j])) * Step;
            if (Math.Abs(crossing) >= closest) continue;
            closest = Math.Abs(crossing);
            best = crossing;
        }
        if (!double.IsFinite(best)) throw new InvalidOperationException("The receiver has no shadow crossing.");
        return (best, lit - shadow);
    }

    static double Sample(byte[] frame, Vector2 centre)
    {
        float x = Math.Clamp(centre.X - 0.5f, 0, W - 1), y = Math.Clamp(centre.Y - 0.5f, 0, H - 1);
        int ix = (int)x, iy = (int)y, nx = Math.Min(W - 1, ix + 1), ny = Math.Min(H - 1, iy + 1);
        double a = Luma(frame, iy * W + ix) * (1 - x + ix) + Luma(frame, iy * W + nx) * (x - ix);
        double b = Luma(frame, ny * W + ix) * (1 - x + ix) + Luma(frame, ny * W + nx) * (x - ix);
        return a * (1 - y + iy) + b * (y - iy);
    }
}
