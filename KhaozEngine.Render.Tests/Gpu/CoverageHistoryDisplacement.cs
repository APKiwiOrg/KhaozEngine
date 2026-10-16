using System;
using System.Numerics;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Signed five-tap filter displacement from one of the four clamp texels, retaining the zero-centre case.</summary>
internal static class CoverageHistoryDisplacement
{
    internal static Vector3 Bound(CoverageLocalHistory bank, Vector2 uv, int reference)
    {
        Span<Term> terms = stackalloc Term[20];
        int count = 0;
        Vector2 size = new(bank.Width, bank.Height), pos = uv * size;
        Vector2 centre = new(MathF.Floor(pos.X - .5f) + .5f, MathF.Floor(pos.Y - .5f) + .5f), f = pos - centre;
        Vector2 w0 = f * (-new Vector2(.5f) + f * (Vector2.One - .5f * f));
        Vector2 w1 = Vector2.One + f * f * (-new Vector2(2.5f) + 1.5f * f);
        Vector2 w2 = f * (new Vector2(.5f) + f * (new Vector2(2) - 1.5f * f));
        Vector2 w3 = f * f * (-new Vector2(.5f) + .5f * f), w12 = w1 + w2;
        Vector2 t0 = (centre - Vector2.One) / size, t3 = (centre + new Vector2(2)) / size, t12 = (centre + w2 / w12) / size;
        Add(t12.X, t0.Y, w12.X * w0.Y, bank, terms, ref count);
        Add(t0.X, t12.Y, w0.X * w12.Y, bank, terms, ref count);
        Add(t12.X, t12.Y, w12.X * w12.Y, bank, terms, ref count);
        Add(t3.X, t12.Y, w3.X * w12.Y, bank, terms, ref count);
        Add(t12.X, t3.Y, w12.X * w3.Y, bank, terms, ref count);
        float weight = w12.X * w0.Y + w0.X * w12.Y + w12.X * w12.Y + w3.X * w12.Y + w12.X * w3.Y;
        Vector3 lo = Vector3.Zero, hi = Vector3.Zero, magnitude = Vector3.Zero;
        bool nonzero = false;
        for (int i = 0; i < count; i++)
        {
            float coefficient = terms[i].Weight / weight - (terms[i].Index == reference ? 1 : 0);
            var p = bank.Pixels[terms[i].Index];
            lo += coefficient * (coefficient >= 0 ? p.Min : p.Max);
            hi += coefficient * (coefficient >= 0 ? p.Max : p.Min);
            magnitude += Math.Abs(terms[i].Weight / weight) * p.Max;
            nonzero |= coefficient != 0;
        }
        if (!nonzero) return Vector3.Zero;
        // Five bilinear taps use 35 multiply/add steps, their weighted sum nine, and normalization one. gamma45
        // encloses those float roundings. This is arithmetic support, not an added colour acceptance tolerance.
        const float unitRoundoff = 1f / 16777216f;
        float gamma45 = 45 * unitRoundoff / (1 - 45 * unitRoundoff);
        return Vector3.Max(Vector3.Abs(lo), Vector3.Abs(hi)) + magnitude * gamma45;
    }

    static void Add(float x, float y, float coefficient, CoverageLocalHistory bank, Span<Term> terms, ref int count)
    {
        Vector2 pos = new Vector2(x, y) * new Vector2(bank.Width, bank.Height) - new Vector2(.5f);
        int bx = (int)MathF.Floor(pos.X), by = (int)MathF.Floor(pos.Y);
        Vector2 f = pos - new Vector2(bx, by);
        for (int corner = 0; corner < 4; corner++)
        {
            int dx = corner & 1, dy = corner >> 1;
            float share = (dx == 0 ? 1 - f.X : f.X) * (dy == 0 ? 1 - f.Y : f.Y);
            float value = coefficient * share;
            if (value == 0) continue;
            int at = Math.Clamp(by + dy, 0, bank.Height - 1) * bank.Width + Math.Clamp(bx + dx, 0, bank.Width - 1);
            int i = 0;
            while (i < count && terms[i].Index != at) i++;
            if (i == count) terms[count++] = new(at, value);
            else terms[i] = terms[i] with { Weight = terms[i].Weight + value };
        }
    }

    readonly record struct Term(int Index, float Weight);
}
