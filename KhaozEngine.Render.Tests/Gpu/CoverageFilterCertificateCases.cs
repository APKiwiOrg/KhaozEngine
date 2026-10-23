using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Tests.Gpu;

// Integer polynomials and their independent B30 normalization. No filter helpers are used here.
internal static class CoverageFilterCertificateCases
{
    internal sealed record Case(string Name, CoverageGpuPredicateProbe.Case Input, bool SignOnly, bool Refuse);
    internal readonly record struct CrossingWords(int Power, uint A, uint B, uint Crossing);

    internal static Case[] All()
    {
        var rows = new List<Case>();
        void Edge(string name, float x, float y, bool refuse = false) => rows.Add(new(name,
            new(0, default, new(x, 0, 0), new(0, y, 0), default), true, refuse));
        void Cross(string name, float x, float a, float b, bool refuse = false) => rows.Add(new(name,
            new(2, default, new(x, 0, 0), new(0, a, 0), new(0, b, 0)), false, refuse));
        foreach (int sign in new[] { 1, -1 })
        {
            foreach (int e in new[] { -78, 0, 100 })
            {
                float unit = MathF.ScaleB(1, e);
                // Literal guard edge and its immediate input words, independently designated.
                Edge($"sign-{sign}-e{e}", 1, sign * unit);
                Edge($"sign-{sign}-below-e{e}", 1, sign * MathF.BitDecrement(unit), e == -78);
                Edge($"sign-{sign}-above-e{e}", 1, sign * MathF.BitIncrement(unit));
            }
            Edge($"sign-{sign}-operand-low", MathF.ScaleB(1, -100), sign * MathF.ScaleB(1, 30));
            Edge($"sign-{sign}-operand-high", MathF.ScaleB(1, 100), sign * MathF.ScaleB(1, -30));
            Edge($"sign-{sign}-product-low-word-neighbor", MathF.ScaleB(1, -39), sign * MathF.BitDecrement(MathF.ScaleB(1, -39)), true);
            Edge($"sign-{sign}-product-high-word-neighbor", MathF.ScaleB(1, 50), sign * MathF.BitDecrement(MathF.ScaleB(1, 51)));
            Cross($"cross-low-{sign}", MathF.ScaleB(1, -39), sign * MathF.ScaleB(1, -39), -sign * MathF.ScaleB(1, -39));
            Cross($"cross-low-excluded-{sign}", MathF.ScaleB(1, -39), sign * MathF.BitDecrement(MathF.ScaleB(1, -39)), -sign * MathF.ScaleB(1, -39), true);
            Cross($"cross-high-{sign}", MathF.ScaleB(1, 50), sign * MathF.ScaleB(1, 50), -sign * MathF.ScaleB(1, 50));
            Cross($"cross-high-excluded-{sign}", MathF.ScaleB(1, 50), sign * MathF.ScaleB(1, 51), -sign * MathF.ScaleB(1, 50), true);
            Edge($"sign-{sign}-product-low-excluded", MathF.ScaleB(1, -39), sign * MathF.ScaleB(1, -40), true);
            Edge($"sign-{sign}-product-low", MathF.ScaleB(1, -39), sign * MathF.ScaleB(1, -39));
            Edge($"sign-{sign}-product-high", MathF.ScaleB(1, 50), sign * MathF.ScaleB(1, 50));
            Edge($"sign-{sign}-product-high-excluded", MathF.ScaleB(1, 50), sign * MathF.ScaleB(1, 51), true);
            Edge($"sign-{sign}-operand-low-excluded", MathF.ScaleB(1, -101), sign * MathF.ScaleB(1, 30), true);
            Edge($"sign-{sign}-operand-high-excluded", MathF.ScaleB(1, 101), sign * MathF.ScaleB(1, -30), true);
            foreach (uint word in new uint[] { 0x3f7fffff, 0x3f800000, 0x3f800001, 0x3fffffff, 0x40000000, 0x40000001 })
                Cross($"binade-{sign}-{word:x8}", 1, sign * BitConverter.UInt32BitsToSingle(word), -sign * .25f);
            Cross($"unequal-{sign}", 1, sign * MathF.ScaleB(1, -60), -sign * 16);
            Cross($"tiny-{sign}", MathF.ScaleB(1, -75), sign * MathF.ScaleB(1, -75), -sign * MathF.ScaleB(1, -75), true);
            // RN32 would move this raw side across a binade. The current certificate must refuse.
            rows.Add(new($"binade-tie-refuse-{sign}", new(2, new(0, sign * MathF.ScaleB(1, -25), 0),
                Vector3.UnitX, new(0, sign, 0), new(0, -sign * .25f, 0)), false, true));
            foreach (int half in new[] { 1, 3 })
            {
                // det = sign * (1 + half * 2^-24), an exact RN32 midpoint.
                var a = new Vector3(0, -sign * half * MathF.ScaleB(1, -24), 0);
                var b = new Vector3(1, 0, 0);
                var c = new Vector3(0, sign, 0);
                var d = new Vector3(2, -sign, 0);
                rows.Add(new($"tie-cross-{sign}-{half}", new(2, a, b, c, d), false, false));
                rows.Add(new($"cancel-sign-{sign}-{half}", new(0, a, b, c, default), true, false));
            }
        }
        var cancellation = new CoverageGpuPredicateProbe.Case(2, new(1, 1, 0), new(MathF.BitIncrement(1), 1, 0),
            new(1, MathF.BitIncrement(1), 0), new(1, MathF.BitDecrement(1), 0));
        rows.Add(new("cancel-cross", cancellation, false, false));
        rows.Add(new("cancel-sign", cancellation with { Op = 0 }, true, false));
        Edge("negative-zero", -0f, 1);
        rows.Add(new("collinear", new(0, default, Vector3.One, new(2, 2, 0), default), true, false));
        rows.Add(new("dot-cancel", new(1, default, Vector3.One, default, new(1, -1, 0)), true, false));
        rows.Add(new("dot-positive", new(1, default, Vector3.One, default, new(1, 1, 0)), true, false));
        rows.Add(new("dot-negative", new(1, default, Vector3.One, default, new(-1, -1, 0)), true, false));
        Cross("endpoint-a", 1, -0f, -1);
        Cross("endpoint-b", 1, 1, 0f);
        Cross("same-side", 1, 1, 2);
        Cross("exact-collinear", 1, -0f, 0f);
        return rows.ToArray();
    }

    internal static BigInteger Exact(CoverageGpuPredicateProbe.Case c, bool second = false) =>
        CoverageGpuPredicateProbe.Exact(c with { Op = c.Op == 2 ? 0u : c.Op, C = second ? c.D : c.C });

    internal static CrossingWords Crossing(CoverageGpuPredicateProbe.Case c)
    {
        BigInteger a = Exact(c), b = Exact(c, true);
        if (a.Sign == 0 || b.Sign == 0 || a.Sign == b.Sign) return new(0, 0, 0, 0x7fc00000);
        int power = 1 - checked((int)BigInteger.Max(BigInteger.Abs(a), BigInteger.Abs(b)).GetBitLength());
        uint x = F(CoverageBinary32IntegerOracle.Round(BigInteger.Abs(a), power, a.Sign));
        uint y = F(CoverageBinary32IntegerOracle.Round(BigInteger.Abs(b), power, b.Sign));
        BigInteger difference = Signed(x) - Signed(y);
        uint denominator = F(CoverageBinary32IntegerOracle.Round(BigInteger.Abs(difference), -149, difference.Sign));
        return new(power, x, y, F(CoverageBinary32IntegerOracle.Divide(x, denominator)));
    }

    internal static uint[] Point(CoverageGpuPredicateProbe.Case c, uint parameter)
    {
        if ((parameter & 0x7fffffffu) > 0x7f800000u) return [0, 0, 0];
        uint Component(float a, float b)
        {
            uint x = F(BitConverter.SingleToUInt32Bits(a)), y = F(BitConverter.SingleToUInt32Bits(b));
            uint delta = Add(y, x ^ 0x80000000u);
            BigInteger product = Signed(parameter) * Signed(delta);
            uint scaled = F(CoverageBinary32IntegerOracle.Round(BigInteger.Abs(product), -298,
                ((parameter ^ delta) >> 31) == 0 ? 1 : -1));
            return Add(x, scaled);
        }
        return [Component(c.C.X, c.D.X), Component(c.C.Y, c.D.Y), Component(c.C.Z, c.D.Z)];
    }

    static uint Add(uint a, uint b)
    {
        BigInteger sum = Signed(a) + Signed(b);
        int sign = sum.IsZero ? ((a & b & 0x80000000u) != 0 ? -1 : 1) : sum.Sign;
        return F(CoverageBinary32IntegerOracle.Round(BigInteger.Abs(sum), -149, sign));
    }

    internal static BigInteger Signed(uint word) => CoverageBinary32IntegerOracle.Units(word) * ((word >> 31) == 0 ? 1 : -1);
    static uint F(uint word) => CoverageBinary32IntegerOracle.Flush(word);
}
