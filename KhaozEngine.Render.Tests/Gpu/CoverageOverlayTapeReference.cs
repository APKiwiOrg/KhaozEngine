using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

// Test-only transcription of the serial binary32 overlay. Arithmetic stays in the frozen reference home.
internal static class CoverageOverlayTapeReference
{
    internal sealed record Case(string Name, Vector3[] Host, Vector3[][] Sheets, uint State = 1);
    internal sealed record Result(bool Ok, uint Maximum, uint[][] Pairs, uint[] Guards, uint[] Samples);
    internal static readonly Vector3[] Unit = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];

    internal static Case[] Cases()
    {
        Vector3[] flat = [new(0, 0, -.25f), new(2, 0, -.25f), new(0, 2, -.25f)];
        Vector3[] bad = [Vector3.Zero, Vector3.One, Vector3.One * 2];
        Vector3[] huge = [new(0, 0, -8), new(2, 0, -8), new(0, 2, -8)];
        var cases = new List<Case>();
        foreach (int count in new[] { 0, 1, 3, 4, 5, 9 })
            cases.Add(new("supports-" + count, Unit, Enumerable.Repeat(flat, count).ToArray()));
        cases.Add(new("inactive-poison", [new(float.NaN), new(float.NaN), new(float.NaN)], [bad], 0));
        cases.Add(new("finished-poison", [new(float.NaN), new(float.NaN), new(float.NaN)], [bad], 2));
        cases.Add(new("host-nonfinite-empty", [new(float.NaN), Unit[1], Unit[2]], []));
        cases.Add(new("host-degenerate-empty", bad, []));
        cases.Add(new("host-overflow", [new(-float.MaxValue, 0, 0), new(float.MaxValue, 0, 0), Unit[2]], [flat]));
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            cases.Add(new("host-nonfinite-" + value, [new(0, 0, value), Unit[1], Unit[2]], [flat]));
            cases.Add(new("sheet-nonfinite-" + value, Unit, [flat, [new(0, 0, value), flat[1], flat[2]], huge]));
        }
        cases.Add(new("early-failure-later-larger", Unit, [flat, bad, huge, flat, bad]));
        cases.Add(new("multiple-failures", Unit, [bad, bad, flat, bad, huge]));
        cases.Add(new("failure-inside-pair-prefix", [new(0, 0, 0), new(1, 0, 0), new(0, 1, float.MaxValue)],
            [[new(0, 0, 0), new(1, 0, 0), new(0, 1, -float.MaxValue)], huge]));
        Vector3[] negativeZero = [new(0, 0, -0f), new(2, 0, -0f), new(0, 2, -0f)];
        Vector3[] positiveZero = [new(0, 0, 0), new(2, 0, 0), new(0, 2, 0)];
        Vector3[] zeroHost = [new(0, 0, -0f), new(1, 0, -0f), new(0, 1, -0f)];
        cases.Add(new("zero-minus-plus", zeroHost, [positiveZero, negativeZero]));
        cases.Add(new("zero-plus-minus", zeroHost, [negativeZero, positiveZero]));
        cases.Add(new("no-overlap", Unit, [[new(3, 3, 0), new(4, 3, 0), new(3, 4, 0)]]));
        cases.Add(new("endpoint", Unit, [[new(1, 0, 0), new(2, 0, .1f), new(2, 1, .2f)]]));
        cases.Add(new("collinear", [new(0, 0, .1f), new(2, 0, .2f), new(0, 1, .3f)],
            [[new(1, 0, 0), new(3, 0, .1f), new(2, -1, .2f)]]));
        cases.Add(new("crossing", [new(0, 0, .2f), new(2, 0, .3f), new(1, 2, .1f)],
            [[new(0, 1.25f, 0), new(2, 1.25f, .1f), new(1, -.75f, .05f)]]));
        foreach (int exponent in new[] { -74, -75 })
        {
            float u = MathF.ScaleB(1, exponent);
            cases.Add(new("tiny-opposite-" + exponent, [new(0, u, 0), new(u, -2 * u, 0), new(1, 1, 0)],
                [[Vector3.Zero, new(u, 0, 0), new(0, -1, 0)]]));
        }
        return cases.ToArray();
    }

    internal static bool HostValid(Vector3[] host, CoverageReferenceArithmetic f, out CoverageReferenceScalar area)
    {
        area = default;
        if (host.Any(p => !CoverageReferenceMath.IsFinite3(f, p))) return false;
        area = f.Edge(host[0], host[1], host[2]);
        return area.Sign != 0 && f.IsFinite(area.Value);
    }

    internal static Result Serial(Case item)
    {
        if (item.State != 1) return new(false, 0x7fc00001, [], [], []);
        var f = new CoverageReferenceArithmetic();
        float maximum = float.NegativeInfinity;
        var samples = new List<uint>();
        bool ok = HostValid(item.Host, f, out var area);
        foreach (Vector3[] sheet in item.Sheets)
        {
            if (!ok) break;
            ok = Pair(f, item.Host, sheet, area, (ordinal, value) =>
            { maximum = f.Max(maximum, value); samples.Add(Bits(value)); });
        }
        return new(ok, Bits(maximum), [], [], samples.ToArray());
    }

    internal static Result Striped(Case item, bool reverse = false)
    {
        var pairs = item.Sheets.Select(_ => Enumerable.Repeat(0x7fc00001u, 17).ToArray()).ToArray();
        if (item.State != 1) return new(false, 0x7fc00001, pairs, Enumerable.Repeat(0x7fc00001u, 4).ToArray(), []);
        uint[] guards = new uint[4];
        foreach (int stripe in reverse ? new[] { 3, 2, 1, 0 } : new[] { 0, 1, 2, 3 })
        {
            var f = new CoverageReferenceArithmetic();
            if (!HostValid(item.Host, f, out var area)) continue;
            guards[stripe] = 1;
            int first = stripe * pairs.Length / 4, end = (stripe + 1) * pairs.Length / 4;
            for (int p = first; p < end; p++) { pairs[p][15] = 0; pairs[p][16] = 0; }
            for (int p = first; p < end; p++)
            {
                uint[] tape = pairs[p];
                bool complete = Pair(f, item.Host, item.Sheets[p], area, (ordinal, value) =>
                { tape[ordinal] = Bits(value); tape[15] |= 1u << ordinal; });
                if (!complete) break;
                tape[16] = 1;
            }
        }
        var fold = new CoverageReferenceArithmetic();
        float maximum = float.NegativeInfinity;
        var samples = new List<uint>();
        bool ok = guards.All(g => g == 1);
        foreach (uint[] tape in pairs)
        {
            if (!ok) break;
            for (int c = 0; c < 15; c++)
                if ((tape[15] & (1u << c)) != 0)
                { maximum = fold.Max(maximum, BitConverter.UInt32BitsToSingle(tape[c])); samples.Add(tape[c]); }
            ok = tape[16] == 1;
        }
        return new(ok, Bits(maximum), pairs, guards, samples.ToArray());
    }

    static bool Pair(CoverageReferenceArithmetic f, Vector3[] host, Vector3[] sheet, CoverageReferenceScalar ha, Action<int, float> sample)
    {
        if (sheet.Any(p => !CoverageReferenceMath.IsFinite3(f, p))) return false;
        var sa = f.Edge(sheet[0], sheet[1], sheet[2]);
        if (sa.Sign == 0 || !f.IsFinite(sa.Value)) return false;
        var hs = new CoverageReferenceScalar[9]; var ss = new CoverageReferenceScalar[9];
        bool Inside(Vector3[] triangle, Vector3 p, CoverageReferenceScalar area, CoverageReferenceScalar[] sides, int corner, out bool inside)
        {
            inside = true;
            for (int s = 0; s < 3; s++)
            {
                var side = sides[corner * 3 + s] = f.Edge(triangle[s], triangle[(s + 1) % 3], p);
                if (!f.IsFinite(side.Value)) return false;
                if (side.Sign * area.Sign < 0) inside = false;
            }
            return true;
        }
        bool Accumulate(int ordinal, Vector3 p)
        {
            float h = CoverageReferenceMath.Height(f, host[0], host[1], host[2], new(p.X, p.Y));
            float s = CoverageReferenceMath.Height(f, sheet[0], sheet[1], sheet[2], new(p.X, p.Y));
            float difference = f.Sub(h, s);
            if (!f.IsFinite(h) || !f.IsFinite(s) || !f.IsFinite(difference)) return false;
            sample(ordinal, difference); return true;
        }
        for (int i = 0; i < 3; i++)
        {
            if (!Inside(sheet, host[i], sa, hs, i, out bool inside)) return false;
            if (inside && !Accumulate(2 * i, host[i])) return false;
            if (!Inside(host, sheet[i], ha, ss, i, out inside)) return false;
            if (inside && !Accumulate(2 * i + 1, sheet[i])) return false;
        }
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                int ni = (i + 1) % 3, nj = (j + 1) % 3;
                int ca = hs[i * 3 + j].Sign, cb = hs[ni * 3 + j].Sign, ac = ss[j * 3 + i].Sign, ad = ss[nj * 3 + i].Sign;
                if (ca * cb > 0 || ac * ad > 0 || ca == 0 && cb == 0) continue;
                Vector3 a = host[i], b = host[ni], p;
                if (ca == 0) p = a;
                else if (cb == 0) p = b;
                else
                {
                    float quotient = f.CrossingParameter(sheet[j], sheet[nj], a, b);
                    if (!f.IsFinite(quotient)) return false;
                    float u = f.Clamp(quotient, 0, 1);
                    p = new(f.Add(a.X, f.Mul(u, f.Sub(b.X, a.X))), f.Add(a.Y, f.Mul(u, f.Sub(b.Y, a.Y))), f.Add(a.Z, f.Mul(u, f.Sub(b.Z, a.Z))));
                }
                if (!CoverageReferenceMath.IsFinite3(f, p) || !Accumulate(6 + 3 * i + j, p)) return false;
            }
        return true;
    }

    internal static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
}
