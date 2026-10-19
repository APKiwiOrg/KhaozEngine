using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;

namespace KhaozEngine.Tests.Gpu;

internal static class CoverageGpuPredicateProbe
{
    internal const string Name = "CoveragePredicates";
    internal const int InputWords = 13, OutputWords = 21;
    internal static string Source => """
        #version 450
        layout(local_size_x = 64) in;
        layout(std430, set = 0, binding = 0) readonly buffer InputBuf { uint Inputs[]; };
        layout(std430, set = 0, binding = 1) buffer ResultBuf { uint Results[]; };
        """ + CoverageGpuArithmeticGlsl.Source + CoverageGpuExactWordsGlsl.Source + CoverageGpuRoundingGlsl.Source
        + CoverageGpuPredicatesGlsl.Source + """
        vec3 probePoint(uint offset) { return vec3(uintBitsToFloat(Inputs[offset]), uintBitsToFloat(Inputs[offset+1u]), uintBitsToFloat(Inputs[offset+2u])); }
        void main() {
            uint row = gl_GlobalInvocationID.x;
            if (row >= Inputs[0]) return;
            uint s = 1u + row * 13u, d = row * 21u;
            vec3 a = probePoint(s+1u), b = probePoint(s+4u), c = probePoint(s+7u), e = probePoint(s+10u);
            CovPredicate result;
            CovExact exact = covExactZero();
            if (Inputs[s] == 0u) { result = covEdge(a,b,c); if (covPredicateFinite(a) && covPredicateFinite(b) && covPredicateFinite(c)) exact = covEdgeExact(a,b,c); }
            else if (Inputs[s] == 1u) { result = covDot(a,b,c,e); if (covPredicateFinite(a) && covPredicateFinite(b) && covPredicateFinite(c) && covPredicateFinite(e)) exact = covDotExact(a,b,c,e); }
            else if (Inputs[s] == 2u) { result.sign = 0; result.value = covCrossing(a,b,c,e); }
            else { result.sign = covCell(a.x,b.x,c.x); result.value = 0.0; }
            Results[d] = uint(result.sign); Results[d+1u] = floatBitsToUint(result.value);
        """ + StoreLimbs() + """
            Results[d+20u] = uint(covExactBitLength(exact));
        }
        """;

    static string StoreLimbs()
    {
        var text = new StringBuilder();
        for (int i = 0; i < 18; i++) text.AppendLine($"Results[d+{i + 2}u] = exact.limb[{i}];");
        return text.ToString();
    }

    internal readonly record struct Case(uint Op, Vector3 A, Vector3 B, Vector3 C, Vector3 D);
    internal static Case[] Cases()
    {
        var rows = new List<Case>();
        void Edge(Vector3 a, Vector3 b, Vector3 c) => rows.Add(new(0, a, b, c, default));
        float tiny = MathF.ScaleB(1, -75);
        Edge(default, new(tiny, 0, 0), new(0, -tiny, 0));
        Edge(default, Vector3.One, new(2, 2, 0));
        Edge(new(-0.050000011920928955f, -0.04999999701976776f, 0),
            new(0.10000000149011612f, 0.10000000894069672f, 0), new(.25f, .25f, 0));
        Vector3[] sheet = [new(.25f, -.25f, .04f), new(.25f, .25f, .04f), new(0, 0, .02f)];
        foreach (Vector3 p in new Vector3[] { new(-0.050000011920928955f, -0.04999999701976776f, 0),
            new(0.20000001788139343f, 0.20000000298023224f, 0), new(-.2f, .2f, 0), default })
            for (int i = 0; i < 3; i++) Edge(sheet[i], sheet[(i + 1) % 3], p);
        foreach (var (host, frozenSheet) in CoverageReferencePredicateTests.FrozenGroup224Fixtures())
        {
            Edge(host[0], host[1], host[2]); Edge(frozenSheet[0], frozenSheet[1], frozenSheet[2]);
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                { Edge(frozenSheet[j], frozenSheet[(j + 1) % 3], host[i]); Edge(host[i], host[(i + 1) % 3], frozenSheet[j]); }
        }
        foreach (int half in new[] { 1, 3 })
            foreach (int tail in new[] { 0, 1, -1 })
            {
                Vector3 a = new(tail * MathF.ScaleB(1, -80), -half * MathF.ScaleB(1, -24), 0), b = new(1, 0, 0), p = new(0, 1, 0);
                Edge(a, b, p); Edge(a, p, b);
                rows.Add(new(1, a, b, new(a.Y, -a.X, 0), new(p.Y, -p.X, 0)));
            }
        Edge(new(float.MaxValue, float.MaxValue, 0), new(-float.MaxValue, 0, 0), new(0, -float.MaxValue, 0));
        Edge(new(MathF.ScaleB(1, 126), MathF.ScaleB(1, -125), 0), new(-MathF.ScaleB(1, 126), 0, 0), new(0, MathF.ScaleB(1, -124), 0));
        Edge(new(-0f, float.Epsilon, float.NaN), new(1, 0, float.PositiveInfinity), new(0, 1, float.NaN));
        var random = new Random(2704);
        float Coordinate() => BitConverter.UInt32BitsToSingle((uint)random.Next(0, 255) << 23 | (uint)random.Next(0, 1 << 23)
            | (random.Next(2) == 0 ? 0u : 0x80000000u));
        Vector3 Point() => new(Coordinate(), Coordinate(), 0);
        for (int i = 0; i < 256; i++)
        {
            Vector3 a = Point(), b = Point(), c = Point(), d = Point();
            Edge(a, b, c); rows.Add(new(1, a, b, c, d));
        }
        foreach (float bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            for (int input = 0; input < 4; input++)
            {
                Vector3[] p = [default, Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY];
                p[input] = new(bad, 0, 0);
                Edge(p[0], p[1], p[2]); rows.Add(new(1, p[0], p[1], p[2], p[3]));
                rows.Add(new(2, p[0], p[1], p[2], p[3]));
            }
        foreach (int exponent in new[] { -74, -75, -125, 126 })
        {
            float unit = MathF.ScaleB(1, exponent);
            rows.Add(new(2, default, new(unit, 0, 0), new(0, unit, 0), new(0, -unit, 0)));
            rows.Add(new(2, default, new(unit, 0, 0), new(0, unit, 0), new(0, -2 * unit, 0)));
        }
        foreach (var (a, b) in new[] { (0f, 1f), (1f, 0f), (1f, 2f), (-1f, -2f), (1.9999999f, -2.0000002f), (float.MaxValue, -MathF.ScaleB(1, -125)) })
            rows.Add(new(2, default, Vector3.UnitX, new(0, a, 0), new(0, b, 0)));
        foreach (float min in new[] { -1f, 0f, 1f })
            foreach (float max in new[] { min, min + 2 })
                foreach (float value in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity, MathF.BitDecrement(min), min,
                    MathF.BitIncrement(min), -.0f, MathF.ScaleB(-1, -25), MathF.BitDecrement(max), max, MathF.BitIncrement(max) })
                    rows.Add(new(3, new(value, 0, 0), new(min, 0, 0), new(max, 0, 0), default));
        for (int i = 0; i <= 16; i++)
            foreach (float value in new[] { MathF.BitDecrement(i / 8f - 1), i / 8f - 1, MathF.BitIncrement(i / 8f - 1) })
                rows.Add(new(3, new(value, 0, 0), new(-1, 0, 0), new(1, 0, 0), default));
        return rows.ToArray();
    }

    internal static uint[] Inputs(Case[] rows)
    {
        var words = new List<uint> { (uint)rows.Length };
        foreach (var row in rows)
        {
            words.Add(row.Op);
            foreach (Vector3 p in new[] { row.A, row.B, row.C, row.D })
            { words.Add(BitConverter.SingleToUInt32Bits(p.X)); words.Add(BitConverter.SingleToUInt32Bits(p.Y)); words.Add(BitConverter.SingleToUInt32Bits(p.Z)); }
        }
        return words.ToArray();
    }

    internal static uint Crossing(Case c)
    {
        BigInteger ca = Exact(new(0, c.A, c.B, c.C, default)), cb = Exact(new(0, c.A, c.B, c.D, default));
        if (ca.Sign == 0 || cb.Sign == 0 || ca.Sign == cb.Sign) return CoverageBinary32IntegerOracle.Invalid;
        int power = 1 - (int)BigInteger.Max(BigInteger.Abs(ca), BigInteger.Abs(cb)).GetBitLength();
        uint x = CoverageBinary32IntegerOracle.Flush(CoverageBinary32IntegerOracle.Round(BigInteger.Abs(ca), power, ca.Sign));
        uint y = CoverageBinary32IntegerOracle.Flush(CoverageBinary32IntegerOracle.Round(BigInteger.Abs(cb), power, cb.Sign));
        BigInteger Signed(uint word) => CoverageBinary32IntegerOracle.Units(word) * ((word & 0x80000000u) == 0 ? 1 : -1);
        BigInteger difference = Signed(x) - Signed(y);
        uint denominator = CoverageBinary32IntegerOracle.Flush(CoverageBinary32IntegerOracle.Round(BigInteger.Abs(difference), -149, difference.Sign));
        return CoverageBinary32IntegerOracle.Flush(CoverageBinary32IntegerOracle.Divide(x, denominator));
    }

    internal static BigInteger Exact(Case c)
    {
        float F(float x) => BitConverter.UInt32BitsToSingle(CoverageBinary32IntegerOracle.Flush(BitConverter.SingleToUInt32Bits(x)));
        Vector3 Point(Vector3 p) => new(F(p.X), F(p.Y), p.Z);
        if (c.Op == 0) return CoverageReferencePredicateTests.Edge(Point(c.A), Point(c.B), Point(c.C));
        BigInteger I(float x) => CoverageReferencePredicateTests.Integer(F(x));
        return (I(c.B.X) - I(c.A.X)) * (I(c.D.X) - I(c.C.X)) + (I(c.B.Y) - I(c.A.Y)) * (I(c.D.Y) - I(c.C.Y));
    }
}
