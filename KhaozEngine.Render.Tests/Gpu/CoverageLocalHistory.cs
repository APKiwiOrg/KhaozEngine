using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

internal readonly record struct CoverageRgbInterval(Vector3 Min, Vector3 Max);

/// <summary>Local history bounds from independent controls and prior bounds, never from the colour under test.</summary>
internal sealed class CoverageLocalHistory(int width, int height)
{
    internal int Width { get; } = width;
    internal int Height { get; } = height;
    internal CoverageRgbInterval[] Pixels { get; } = new CoverageRgbInterval[width * height];
    internal Vector2[] Luma { get; } = new Vector2[width * height];
    internal float[] Eligibility { get; } = new float[width * height];
    internal float[] PossibleLocks { get; } = new float[width * height];

    internal void Store(int pixel, Vector3 yccMin, Vector3 yccMax, float possibleLock)
    {
        var joint = CoverageJointHistory.Bounds(yccMin, yccMax);
        StoreRaw(pixel, joint.Rgb, joint.Y, possibleLock);
    }

    internal void StoreReset(int pixel, Vector3 independentColour, float possibleLock)
    {
        var delta = new Vector3(CoverageGhostEnvelope.Tolerance);
        var raw = new CoverageRgbInterval(Vector3.Max(independentColour - delta, Vector3.Zero), independentColour + delta);
        StoreRaw(pixel, raw, WeightedY(raw), possibleLock);
    }

    void StoreRaw(int pixel, CoverageRgbInterval raw, Vector2 y, float possibleLock)
    {
        var lo = raw.Min; var hi = raw.Max;
        Assert.True(Finite(lo) && Finite(hi), "Nonfinite independently supported history interval.");
        var stored = new CoverageRgbInterval(new(Down(lo.X), Down(lo.Y), Down(lo.Z)), new(Up(hi.X), Up(hi.Y), Up(hi.Z)));
        Pixels[pixel] = stored;
        Vector3 rounding = Vector3.Max(lo - stored.Min, stored.Max - hi);
        float error = Vector3.Dot(Lipschitz(y), rounding);
        Luma[pixel] = new(Math.Max(0, MathF.BitDecrement(y.X - error)), MathF.BitIncrement(y.Y + error));
        Assert.True(float.IsFinite(Luma[pixel].X) && float.IsFinite(Luma[pixel].Y) && Luma[pixel].X <= Luma[pixel].Y);
        // The marked lock is stored in [-6, -5], whose binary16 grid is 1/256. Rounding the upper bound to
        // that grid also covers every finer plain/moved encoding. LockDecay is exactly 32 grid steps.
        PossibleLocks[pixel] = Math.Min(1, MathF.Ceiling(Math.Max(0, possibleLock) * 256) / 256);
    }

    internal Vector2 SampleY(Vector2 uv, CoverageRgbInterval rgb)
    {
        Vector2 position = uv * new Vector2(Width, Height) - new Vector2(.5f);
        int bx = (int)MathF.Floor(position.X), by = (int)MathF.Floor(position.Y);
        Vector2 f = position - new Vector2(bx, by);
        int reference = Index(bx + (f.X > .5f ? 1 : 0), by + (f.Y > .5f ? 1 : 0));
        Vector2 prior = Luma[reference];
        Vector3 displacement = CoverageHistoryDisplacement.Bound(this, uv, reference);
        // P is one of the four clamp texels, so the clamp cannot increase |sample-P|. For a=(.25,.5,.25)
        // and b=Rec709, f(P+d)-f(P)=((a-f(P)b).d)/(1+b.(P+d)). Stored/sample RGB is nonnegative, so
        // the denominator is at least one. This preserves exactly zero displacement at a texel centre.
        float error = Vector3.Dot(Lipschitz(prior), displacement);
        Vector2 box = WeightedY(rgb);
        Vector2 result = new(Math.Max(box.X, MathF.BitDecrement(prior.X - error)), Math.Min(box.Y, MathF.BitIncrement(prior.Y + error)));
        Assert.True(float.IsFinite(result.X) && float.IsFinite(result.Y) && result.X <= result.Y);
        return result;
    }

    static Vector3 Lipschitz(Vector2 y)
    {
        Vector3 a = new(.25f, .5f, .25f), b = CoverageJointHistory.Rec709;
        return Vector3.Max(Vector3.Abs(a - y.X * b), Vector3.Abs(a - y.Y * b));
    }

    internal void Write(string artifact)
    {
        if (CoverageArtifacts.DirectoryPath is null) return;
        var values = new float[Pixels.Length * 10];
        for (int i = 0; i < Pixels.Length; i++)
        {
            var p = Pixels[i];
            int at = i * 10;
            (values[at], values[at + 1], values[at + 2]) = (p.Min.X, p.Min.Y, p.Min.Z);
            (values[at + 3], values[at + 4], values[at + 5]) = (p.Max.X, p.Max.Y, p.Max.Z);
            values[at + 6] = PossibleLocks[i];
            values[at + 7] = Eligibility[i];
            values[at + 8] = Luma[i].X; values[at + 9] = Luma[i].Y;
        }
        CoverageArtifacts.WriteRaw($"{artifact}.independent-history.f32", values);
    }

    // The native history stores binary16. One outward half step covers store rounding and flushing without a new
    // colour tolerance. The bank is stored RGB because the actual history filter samples stored RGB.
    static float Down(float v)
    {
        ushort bits = BitConverter.HalfToUInt16Bits((Half)Math.Max(0, v));
        Assert.True(bits < 0x7c00, "Unbounded independent history support.");
        return (float)BitConverter.UInt16BitsToHalf(bits == 0 ? bits : (ushort)(bits - 1));
    }

    static float Up(float v)
    {
        ushort bits = BitConverter.HalfToUInt16Bits((Half)Math.Max(0, v));
        Assert.True(bits < 0x7bff, "Unbounded independent history support.");
        return (float)BitConverter.UInt16BitsToHalf((ushort)(bits + 1));
    }

    internal (CoverageRgbInterval Colour, float PossibleLock) Sample(Vector2 uv)
    {
        Vector2 size = new(Width, Height), pos = uv * size;
        Vector2 baseCentre = new(MathF.Floor(pos.X - .5f) + .5f, MathF.Floor(pos.Y - .5f) + .5f);
        Vector2 f = pos - baseCentre;
        Vector2 w0 = f * (-new Vector2(.5f) + f * (Vector2.One - .5f * f));
        Vector2 w1 = Vector2.One + f * f * (-new Vector2(2.5f) + 1.5f * f);
        Vector2 w2 = f * (new Vector2(.5f) + f * (new Vector2(2) - 1.5f * f));
        Vector2 w3 = f * f * (-new Vector2(.5f) + .5f * f), w12 = w1 + w2;
        Vector2 t0 = (baseCentre - Vector2.One) / size, t3 = (baseCentre + new Vector2(2)) / size;
        Vector2 t12 = (baseCentre + w2 / w12) / size;
        Vector3 lo = Vector3.Zero, hi = Vector3.Zero;
        float weight = 0;
        Add(new(t12.X, t0.Y), w12.X * w0.Y);
        Add(new(t0.X, t12.Y), w0.X * w12.Y);
        Add(t12, w12.X * w12.Y);
        Add(new(t3.X, t12.Y), w3.X * w12.Y);
        Add(new(t12.X, t3.Y), w12.X * w3.Y);
        Assert.True(weight > 0 && float.IsFinite(weight));
        lo /= weight; hi /= weight;
        int bx = (int)MathF.Floor(pos.X - .5f), by = (int)MathF.Floor(pos.Y - .5f);
        Vector3 minLo = new(float.MaxValue), minHi = minLo, maxLo = new(float.MinValue), maxHi = maxLo;
        float possibleLock = 0;
        for (int corner = 0; corner < 4; corner++)
        {
            int dx = corner & 1, dy = corner >> 1, i = Index(bx + dx, by + dy);
            var p = Pixels[i];
            minLo = Vector3.Min(minLo, p.Min); minHi = Vector3.Min(minHi, p.Max);
            maxLo = Vector3.Max(maxLo, p.Min); maxHi = Vector3.Max(maxHi, p.Max);
            float share = (dx == 0 ? 1 - f.X : f.X) * (dy == 0 ? 1 - f.Y : f.Y);
            if (share >= .001f) possibleLock = Math.Max(possibleLock, PossibleLocks[i]);
        }
        // sampleHistoryCatmullRom clamps to the four history texels, not its five bilinear taps.
        return (new(Vector3.Min(Vector3.Max(lo, minLo), maxLo), Vector3.Min(Vector3.Max(hi, minHi), maxHi)), possibleLock);

        void Add(Vector2 at, float coefficient)
        {
            var p = Linear(at);
            lo += (coefficient >= 0 ? p.Min : p.Max) * coefficient;
            hi += (coefficient >= 0 ? p.Max : p.Min) * coefficient;
            weight += coefficient;
        }
    }

    CoverageRgbInterval Linear(Vector2 uv)
    {
        Vector2 pos = uv * new Vector2(Width, Height) - new Vector2(.5f);
        int bx = (int)MathF.Floor(pos.X), by = (int)MathF.Floor(pos.Y);
        Vector2 f = pos - new Vector2(bx, by);
        Vector3 lo = Vector3.Zero, hi = Vector3.Zero;
        for (int corner = 0; corner < 4; corner++)
        {
            int dx = corner & 1, dy = corner >> 1;
            float share = (dx == 0 ? 1 - f.X : f.X) * (dy == 0 ? 1 - f.Y : f.Y);
            var p = Pixels[Index(bx + dx, by + dy)];
            lo += p.Min * share; hi += p.Max * share;
        }
        return new(lo, hi);
    }

    int Index(int x, int y) => Math.Clamp(y, 0, Height - 1) * Width + Math.Clamp(x, 0, Width - 1);

    // YCoCg Y / (1 + Rec709 RGB luma) is linear-fractional over a nonnegative RGB box. Its extrema are at corners.
    internal static Vector2 WeightedY(CoverageRgbInterval p)
    {
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        for (int corner = 0; corner < 8; corner++)
        {
            double red = (corner & 1) == 0 ? p.Min.X : p.Max.X;
            double green = (corner & 2) == 0 ? p.Min.Y : p.Max.Y;
            double blue = (corner & 4) == 0 ? p.Min.Z : p.Max.Z;
            double y = (.25 * red + .5 * green + .25 * blue) / (1 + .2126 * red + .7152 * green + .0722 * blue);
            lo = Math.Min(lo, y); hi = Math.Max(hi, y);
        }
        Assert.True(double.IsFinite(lo) && double.IsFinite(hi));
        return new(MathF.BitDecrement((float)lo), MathF.BitIncrement((float)hi));
    }

    internal static Vector2 ControlY(Vector3 colour)
    {
        var delta = new Vector3(CoveragePixelProof.OpaqueColourTolerance);
        return WeightedY(new(Vector3.Max(colour - delta, Vector3.Zero), Vector3.Min(colour + delta, new Vector3(65504))));
    }

    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}

internal sealed record CoverageHistoryInputs(CoverageLocalHistory? Previous, MotionTargetReadback Motion,
    TemporalResolveUniforms Resolve, CoverageMaskReadback? Mask);
