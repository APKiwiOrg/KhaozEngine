using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

internal static class CoverageGpuRoundingProbe
{
    internal const string Name = "CoverageRounding";
    internal const int InputWords = 5, OutputWords = 4;
    internal static string Source => """
        #version 450
        layout(local_size_x = 64) in;
        layout(std430, set = 0, binding = 0) readonly buffer InputBuf { uint Inputs[]; };
        layout(std430, set = 0, binding = 1) buffer ResultBuf { uint Results[]; };
        """ + CoverageGpuArithmeticGlsl.Source + CoverageGpuRoundingGlsl.Source + """
        void main() {
            uint row = gl_GlobalInvocationID.x;
            if (row >= Inputs[0]) return;
            uint s = 1u + row * 5u, d = row * 4u, count;
            float a = uintBitsToFloat(Inputs[s+1u]), b = uintBitsToFloat(Inputs[s+2u]);
            uint before;
            if (Inputs[s] == 0u) {
                if (Inputs[s+3u] == 0u) before = covDivRn32(a, b, count);
                else before = covDivSeeded(a, b, Inputs[s+4u], count);
                Results[d+3u] = floatBitsToUint(covDiv(a, b));
            } else {
                if (Inputs[s+3u] == 0u) before = covSqrtRn32(a, count);
                else before = covSqrtSeeded(a, Inputs[s+4u], count);
                Results[d+3u] = floatBitsToUint(covSqrt(a));
            }
            Results[d] = before;
            Results[d+1u] = floatBitsToUint(covF(uintBitsToFloat(before)));
            Results[d+2u] = count;
        }
        """;

    internal static readonly (uint A, uint B)[] Divisions =
    {
        (0x3f800000u, 0x40400000u), (0x01000000u, 0x40000000u), (0x01000001u, 0x40000000u),
        (0x7f7fffffu, 0x3f000000u), (0x7f000000u, 0x7f000000u), (0x3f800000u, 0x7f7fffffu),
        (0x00800001u, 0x4b800000u), (0x00800001u, 0x7f7fffffu),
        (0x00800001u, 0x40000000u), (0x00800003u, 0x40000000u),
        (0x80800003u, 0x40000000u), (0x01000000u, 0x4c000000u), (0x00ffffffu, 0x40000000u),
        (0x3f7fffffu, 0x3f800000u), (0x3f800001u, 0x3f800000u),
        (0x3f800000u, 0x3f800001u), (0x3f800000u, 0x3f7fffffu),
        (0xbf800000u, 0x40400000u), (0x3f800000u, 0xc0400000u), (0xbf800000u, 0xc0400000u),
        (0u, 0x3f800000u), (0x80000000u, 0x3f800000u), (0u, 0xbf800000u),
        (0x00800000u, 0x3f800000u), (0x80800000u, 0x3f800000u),
        (0x3f800000u, 0u), (0x3f800000u, 0x80000000u), (0u, 0u), (0u, 0x80000000u),
        (0x80000000u, 0u), (0x80000000u, 0x80000000u),
        (0x3f800000u, 0x7f800000u), (0x3f800000u, 0xff800000u),
        (0x7f800000u, 0x3f800000u), (0xff800000u, 0x3f800000u),
        (0x7f800000u, 0x7f800000u), (0x7f800000u, 0xff800000u),
        (0x3f800000u, 0x7fc00000u), (0x3f800000u, 0x7fa00001u), (0x7fc00000u, 0x3f800000u),
        (0x3f800000u, 0xffc00000u), (0x3f800000u, 0xffa00001u), (0x3f800000u, 0x00800000u),
    };
    internal static readonly uint[] Roots =
    {
        0x00800001u, 0x3f7fffffu, 0x3f800000u, 0x3f800001u, 0x40000000u, 0x407fffffu,
        0x40800000u, 0x7f7fffffu, 0u, 0x80000000u, 0xbf800000u, 0x80800001u,
        0x00800000u, 0x80800000u, 0x7f800000u, 0xff800000u, 0x7fc00000u, 0x7fa00001u,
        0xffc00000u, 0xffa00001u,
    };

    internal static (uint[] Inputs, uint[] Expected) Data()
    {
        var inputs = new List<uint> { 0u };
        var expected = new List<uint>();
        foreach (var pair in Divisions) Add(0, pair.A, pair.B, CoverageBinary32IntegerOracle.Divide(pair.A, pair.B));
        foreach (uint a in Roots) Add(1, a, 0, CoverageBinary32IntegerOracle.Sqrt(a));
        inputs[0] = (uint)expected.Count / 3;
        return (inputs.ToArray(), expected.ToArray());

        void Add(uint op, uint a, uint b, uint before)
        {
            uint magnitude = before & 0x7fffffffu;
            uint[] seeds = { 0, 0, 0x7f800000u, magnitude == 0 ? 0 : magnitude - 1,
                magnitude >= 0x7f800000u ? 0x7f800000u : magnitude + 1 };
            for (int i = 0; i < seeds.Length; i++)
            {
                inputs.AddRange(new[] { op, a, b, i == 0 ? 0u : 1u, seeds[i] });
                expected.Add(before); expected.Add(CoverageBinary32IntegerOracle.Flush(before));
                expected.Add(CoverageBinary32IntegerOracle.Flush(before));
            }
        }
    }
}
