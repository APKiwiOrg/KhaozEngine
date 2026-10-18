using System;
using System.Collections.Generic;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Flat-face shadow metrics on the reference's drawn edge, with no analytic bias or face lighting assumption.</summary>
internal sealed class GameShadowGrainMeasure
{
    internal const float BandPixels = 40f, FarPixels = 64f;
    internal readonly ShadowEdgeRegion Band = new(), Far = new();
    internal readonly List<double> Released = new();
    internal double Penumbra { get; private set; }
    internal double Contrast { get; private set; }

    internal GameShadowGrainMeasure(byte[][] frames, byte[][] reference, bool sway, byte[][]? release = null, float bandPixels = BandPixels)
    {
        int w = GameShadowGrainScene.W, h = GameShadowGrainScene.H;
        float[]? previous = null, previousRef = null;
        var window = new float[9];
        double widths = 0, contrasts = 0;
        int widthCount = 0, contrastCount = 0;
        for (int t = 0; t < frames.Length; t++)
        {
            float[] lo = Luma(frames[t]), lr = Luma(reference[t]);
            var error = new float[lo.Length];
            for (int i = 0; i < lo.Length; i++) error[i] = lo[i] - lr[i];
            int band = 0, released = 0;
            for (int x = 4; x < w - 4; x++)
            {
                if (!GameShadowGrainScene.Receiver(GameShadowGrainScene.Warm + t, x, sway)) continue;
                float low = float.MaxValue, high = float.MinValue;
                for (int y = 4; y < h - 4; y++)
                {
                    low = MathF.Min(low, lr[y * w + x]);
                    high = MathF.Max(high, lr[y * w + x]);
                }
                if (high - low < 0.04f) continue;
                float edge = Crossing(lr, x, low + 0.5f * (high - low));
                if (!float.IsFinite(edge)) continue;
                contrasts += high - low;
                contrastCount++;
                float p10 = Crossing(lo, x, low + 0.1f * (high - low));
                float p90 = Crossing(lo, x, low + 0.9f * (high - low));
                if (float.IsFinite(p10) && float.IsFinite(p90))
                {
                    widths += Math.Abs(p90 - p10);
                    widthCount++;
                }
                for (int y = 4; y < h - 4; y++)
                {
                    float d = MathF.Abs(y + 0.5f - edge);
                    ShadowEdgeRegion? region = d <= bandPixels ? Band : d >= FarPixels ? Far : null;
                    if (region == null) continue;
                    int p = y * w + x;
                    region.Add(error[p], MathF.Abs(lo[p] - Median(lo, x, y, window)),
                        MathF.Abs(lr[p] - Median(lr, x, y, window)), MathF.Abs(error[p] - Median(error, x, y, window)));
                    if (previous != null && previousRef != null)
                        region.AddChange(lo[p] - previous[p], lr[p] - previousRef[p]);
                    if (region != Band) continue;
                    band++;
                    if (release != null && release[t][4 * p] > 127) released++;
                }
            }
            Released.Add(release == null || band == 0 ? double.NaN : (double)released / band);
            previous = lo;
            previousRef = lr;
        }
        Penumbra = widthCount == 0 ? double.NaN : widths / widthCount;
        Contrast = contrastCount == 0 ? 0 : contrasts / contrastCount;
    }

    static float[] Luma(byte[] rgba)
    {
        var result = new float[GameShadowGrainScene.W * GameShadowGrainScene.H];
        for (int y = 0; y < GameShadowGrainScene.H; y++)
            for (int x = 0; x < GameShadowGrainScene.W; x++)
                result[y * GameShadowGrainScene.W + x] = TemporalAcceptance.Luma(rgba, GameShadowGrainScene.W, x, y);
        return result;
    }

    // The shadow is above the lit face. Interpolate its first upward threshold crossing down the column.
    static float Crossing(float[] image, int x, float threshold)
    {
        int w = GameShadowGrainScene.W;
        for (int y = 4; y < GameShadowGrainScene.H - 5; y++)
        {
            float a = image[y * w + x], b = image[(y + 1) * w + x];
            if (a <= threshold && b > threshold) return y + 0.5f + (threshold - a) / (b - a);
        }
        return float.NaN;
    }

    static float Median(float[] image, int x, int y, float[] window)
    {
        int k = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++) window[k++] = image[(y + dy) * GameShadowGrainScene.W + x + dx];
        Array.Sort(window);
        return window[4];
    }
}
