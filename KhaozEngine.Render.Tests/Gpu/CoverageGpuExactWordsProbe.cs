using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

internal static class CoverageGpuExactWordsProbe
{
    internal const string Name = "CoverageExactWords";
    internal const int InputWords = 40, OutputWords = 23;
    internal static string Source => """
        #version 450
        layout(local_size_x = 64) in;
        layout(std430, set = 0, binding = 0) readonly buffer InputBuf { uint Inputs[]; };
        layout(std430, set = 0, binding = 1) buffer ResultBuf { uint Results[]; };
        """ + CoverageGpuExactWordsGlsl.Source + """
        void main() {
            uint row = gl_GlobalInvocationID.x;
            if (row >= Inputs[0]) return;
            uint s = 1u + row * 40u, d = row * 23u;
            CovExact a, b;
            a.sign = int(Inputs[s+1u]); b.sign = int(Inputs[s+20u]);
        """ + LoadLimbs() + """
            uint overflow = 0u;
            CovExact result = a;
            if (Inputs[s] == 0u) result = covExactAdd(a, b, overflow);
            if (Inputs[s] == 1u) result = covExactSub(a, b, overflow);
            if (Inputs[s] == 2u) result = covExactProduct(a, b, overflow);
            if (Inputs[s] == 3u) result = covExactFromWord(a.limb[0]);
        """ + StoreLimbs() + """
            Results[d+18u] = uint(result.sign);
            Results[d+19u] = overflow;
            Results[d+20u] = uint(covExactCompare(a, b));
            Results[d+21u] = uint(covExactBitLength(result));
            Results[d+22u] = covRound32(result, int(Inputs[s+39u]));
        }
        """;

    static string LoadLimbs()
    {
        var source = new StringBuilder();
        for (int j = 0; j < 18; j++)
            source.AppendLine($"a.limb[{j}] = Inputs[s+{2 + j}u]; b.limb[{j}] = Inputs[s+{21 + j}u];");
        return source.ToString();
    }

    static string StoreLimbs()
    {
        var source = new StringBuilder();
        for (int j = 0; j < 18; j++) source.AppendLine($"Results[d+{j}u] = result.limb[{j}];");
        return source.ToString();
    }

    internal static readonly (uint Op, BigInteger A, BigInteger B, int Power, int ZeroSign)[] Cases =
    {
        (0, (BigInteger.One << 544) - 1, 1, -520, 0),
        (0, (BigInteger.One << 575) - 1, 1, -552, 0),
        (0, -((BigInteger.One << 277) - 1), -1, -149, 0),
        (0, BigInteger.One << 277, -(BigInteger.One << 277), -149, 0),
        (1, BigInteger.One << 544, 1, -520, 0),
        (1, 1, BigInteger.One << 277, -149, 0),
        (2, (BigInteger.One << 278) - 1, -((BigInteger.One << 278) - 1), -298, 0),
        (2, BigInteger.One << 287, BigInteger.One << 287, -550, 0),
        (2, 0xffffffffu, 0xffffffffu, -40, 0),
        (2, 0xffff0001u, 0x0001ffffu, -40, 0),
        (2, 0x80000000u, 0xffffffffu, -40, 0),
        (2, 0, BigInteger.One << 277, -149, 0),
        (2, (BigInteger.One << 288) - 1, (BigInteger.One << 288) - 1, -550, 0),
        (2, (BigInteger.One << 576) - 1, 1, -552, 0),
        (2, 1, (BigInteger.One << 576) - 1, -552, 0),
        (2, BigInteger.One << 575, 1, -552, 0),
        (4, (BigInteger.One << 24) + 1, 0, -24, 0),
        (4, (BigInteger.One << 24) + 3, 0, -24, 0),
        (4, -((BigInteger.One << 24) + 3), 0, -24, 0),
        (4, (BigInteger.One << 25) - 1, 0, 103, 0),
        (4, (BigInteger.One << 26) - 3, 0, 102, 0),
        (4, 1, 0, -150, 0), (4, 3, 0, -150, 0),
        (4, (BigInteger.One << 24) - 1, 0, -150, 0),
        (4, 0, 0, -149, -1), (4, 1, 0, -800, 0),
        (3, 0x7f7fffffu, 0, -149, 0), (3, 0xff7fffffu, 0, -149, 0),
        (3, 0x00800001u, 0, -149, 0), (3, 1, 0, -149, 0),
        (4, 0x7fffffu, 0, -149, 0), (4, 0x800000u, 0, -149, 0),
        (4, 0x800001u, 0, -149, 0), (4, 0xffffffu, 0, -23, 0),
        (4, 0x1000001u, 0, -24, 0), (4, 0x1ffffffu, 0, -24, 0),
        (4, (BigInteger.One << 200) + (BigInteger.One << 176), 0, -200, 0),
        (4, (BigInteger.One << 200) + (BigInteger.One << 176) + 1, 0, -200, 0),
    };

    internal static (uint[] Inputs, uint[] Expected) Data()
    {
        var inputs = new List<uint> { (uint)Cases.Length };
        var expected = new List<uint>();
        foreach (var c in Cases)
        {
            inputs.Add(c.Op); inputs.Add(unchecked((uint)(c.A.IsZero ? c.ZeroSign : c.A.Sign)));
            WriteLimbs(inputs, c.A); inputs.Add(unchecked((uint)c.B.Sign)); WriteLimbs(inputs, c.B);
            inputs.Add(unchecked((uint)c.Power));
            BigInteger value = c.Op switch
            {
                0 => c.A + c.B,
                1 => c.A - c.B,
                2 => c.A * c.B,
                3 => CoverageBinary32IntegerOracle.Units((uint)c.A) * (((uint)c.A & 0x80000000u) == 0 ? 1 : -1),
                _ => c.A,
            };
            WriteLimbs(expected, value);
            int sign = value.IsZero && c.Op == 4 ? c.ZeroSign : value.Sign;
            expected.Add(unchecked((uint)sign)); expected.Add(0);
            expected.Add(unchecked((uint)c.A.CompareTo(c.B)));
            expected.Add((uint)BigInteger.Abs(value).GetBitLength());
            expected.Add(CoverageBinary32IntegerOracle.Round(BigInteger.Abs(value), c.Power, sign));
        }
        return (inputs.ToArray(), expected.ToArray());
    }

    static void WriteLimbs(List<uint> words, BigInteger value)
    {
        value = BigInteger.Abs(value);
        for (int i = 0; i < 18; i++) { words.Add((uint)(value & uint.MaxValue)); value >>= 32; }
        if (!value.IsZero) throw new InvalidOperationException("Test integer exceeds 18 limbs");
    }
}
