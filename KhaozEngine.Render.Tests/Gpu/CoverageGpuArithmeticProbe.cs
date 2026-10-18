using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

// All numerical operands come from storage, including matrix fields and the addend in the FMA control.
internal static class CoverageGpuArithmeticProbe
{
    internal const int InputWords = 25, OutputWords = 37;
    internal const string Name = "CoverageArithmetic";
    internal static string Source => """
        #version 450
        layout(local_size_x = 64) in;
        layout(std430, set = 0, binding = 0) readonly buffer InputBuf { uint Inputs[]; };
        layout(std430, set = 0, binding = 1) buffer ResultBuf { uint Results[]; };
        """ + CoverageGpuArithmeticGlsl.Source + """

        void main() {
            uint i = gl_GlobalInvocationID.x;
            if (i >= Inputs[0]) return;
            uint s = 1u + i * 25u, d = i * 37u;
            vec3 a = vec3(uintBitsToFloat(Inputs[s]), uintBitsToFloat(Inputs[s+1u]), uintBitsToFloat(Inputs[s+2u]));
            vec3 b = vec3(uintBitsToFloat(Inputs[s+3u]), uintBitsToFloat(Inputs[s+4u]), uintBitsToFloat(Inputs[s+5u]));
            mat4 m;
            for (uint col = 0u; col < 4u; col++)
                for (uint row = 0u; row < 4u; row++) m[col][row] = uintBitsToFloat(Inputs[s+6u+col*4u+row]);
            float lo = uintBitsToFloat(Inputs[s+22u]), hi = uintBitsToFloat(Inputs[s+23u]);
            float c = uintBitsToFloat(Inputs[s+24u]);
            Results[d] = floatBitsToUint(covF(a.x));
            Results[d+1u] = floatBitsToUint(covRead(Inputs[s]));
            covStore(Results[d+2u], a.x);
            Results[d+3u] = floatBitsToUint(covAdd(a.x, b.x));
            Results[d+4u] = floatBitsToUint(covSub(a.x, b.x));
            Results[d+5u] = floatBitsToUint(covMul(a.x, b.x));
            Results[d+6u] = floatBitsToUint(covAbs(a.x));
            Results[d+7u] = floatBitsToUint(covNeg(a.x));
            Results[d+8u] = floatBitsToUint(covMin(a.x, b.x));
            Results[d+9u] = floatBitsToUint(covMax(a.x, b.x));
            Results[d+10u] = floatBitsToUint(covClamp(a.x, lo, hi));
            Results[d+11u] = uint(covFinite(a.x));
            Results[d+12u] = uint(covLess(a.x, b.x));
            Results[d+13u] = uint(covLessOrEqual(a.x, b.x));
            Results[d+14u] = uint(covGreater(a.x, b.x));
            Results[d+15u] = uint(covGreaterOrEqual(a.x, b.x));
            Results[d+16u] = uint(covEqual(a.x, b.x));
            Results[d+17u] = uint(covNotEqual(a.x, b.x));
            Results[d+18u] = floatBitsToUint(covAdd(covMul(a.x, b.x), c));
            Results[d+19u] = floatBitsToUint(covDot3(a, b));
            Results[d+20u] = floatBitsToUint(covDot2(a.xy, b.xy));
            vec3 cross = covCross(a, b), sub = covSub3(a, b), point = covPoint(a, m), linear = covLinear(a, m);
            for (uint k = 0u; k < 3u; k++) {
                Results[d+21u+k] = floatBitsToUint(cross[k]);
                Results[d+24u+k] = floatBitsToUint(sub[k]);
                Results[d+27u+k] = floatBitsToUint(point[k]);
                Results[d+30u+k] = floatBitsToUint(linear[k]);
            }
            Results[d+33u] = uint(covEqual3(a, b));
            Results[d+34u] = uint(covFinite3(a));
            Results[d+35u] = covLess(a.x, b.x) ? Inputs[s] : Inputs[s+3u];
            covStore(Results[d+36u], a.x);
        }
        """;

    // Independent literal guard decisions, including a Z-only mismatch after equal X and Y.
    internal static readonly (uint[] A, uint[] B, uint Equal, uint Finite)[] VectorCases =
    {
        ([0x3f800000u, 0x40000000u, 0x40400000u], [0x3f800000u, 0x40000000u, 0x40400000u], 1u, 1u),
        ([0u, 0x80000000u, 0x00800000u], [0x80000000u, 0u, 0u], 1u, 1u),
        ([0x40000000u, 0x40000000u, 0x40400000u], [0x3f800000u, 0x40000000u, 0x40400000u], 0u, 1u),
        ([0x3f800000u, 0x40000000u, 0x40400000u], [0x3f800000u, 0x40400000u, 0x40400000u], 0u, 1u),
        ([0x3f800000u, 0x40000000u, 0x40400000u], [0x3f800000u, 0x40000000u, 0x40800000u], 0u, 1u),
        ([0x7fc00000u, 0x40000000u, 0x40400000u], [0x3f800000u, 0x40000000u, 0x40400000u], 0u, 0u),
        ([0x3f800000u, 0x7fc00000u, 0x40400000u], [0x3f800000u, 0x40000000u, 0x40400000u], 0u, 0u),
        ([0x3f800000u, 0x40000000u, 0x7fc00000u], [0x3f800000u, 0x40000000u, 0x40400000u], 0u, 0u),
        ([0x3f800000u, 0x00800001u, 0x40400000u], [0x3f800000u, 0u, 0x40400000u], 0u, 1u),
        ([0x3f800000u, 0x80800000u, 0x007fffffu], [0x3f800000u, 0u, 0x80000000u], 1u, 1u),
    };

    internal static uint[] Inputs()
    {
        uint[] values = { 0u, 0x80000000u, 1u, 0x80000001u, 0x007fffffu, 0x807fffffu,
            0x00800000u, 0x80800000u, 0x00800001u, 0x80800001u, 0x00800002u,
            0x3f000000u, 0x3f800000u, 0xbf800000u, 0x40000000u, 0x3f800001u,
            0x3f7ffffeu, 0x4b800000u, 0x7f7fffffu };
        var words = new List<uint> { 0u };
        foreach (uint a in values)
            foreach (uint b in values) Add(words, a, b);
        // Nonfinite operands exercise guards and propagation without assuming a payload for invalid arithmetic.
        foreach (uint nonfinite in new[] { 0x7f800000u, 0xff800000u, 0x7fc00000u })
        {
            Add(words, nonfinite, 0x3f800000u);
            Add(words, 0x3f800000u, nonfinite);
        }
        foreach (var row in VectorCases)
        {
            Add(words, row.A[0], row.B[0]);
            int start = words.Count - InputWords;
            for (int component = 0; component < 3; component++)
            {
                words[start + component] = row.A[component];
                words[start + 3 + component] = row.B[component];
            }
        }
        words[0] = (uint)(words.Count - 1) / InputWords;
        return words.ToArray();
    }

    static void Add(List<uint> words, uint a, uint b)
    {
        words.AddRange(new[] { a, 0x3f800001u, 0x3f7ffffeu, b, 0x3f7ffffeu, 0xbf800000u,
            0x3f800001u, 0xbf800000u, 0x40000000u, 0x40400000u,
            0x3f7ffffeu, 0x00800001u, 0x3f000000u, 0x40800000u,
            0xbf800000u, 0x3f800000u, 0x80000000u, 0x40a00000u,
            0x3f800000u, 0x80800000u, 0x00800001u, 0x40c00000u,
            0u, 0x40000000u, 0xbf800000u });
    }

    internal static uint[] Reference(uint[] inputs)
    {
        var f = new CoverageReferenceArithmetic();
        var results = new uint[inputs[0] * OutputWords];
        for (int i = 0; i < inputs[0]; i++)
        {
            ReadOnlySpan<uint> words = inputs.AsSpan(1 + i * InputWords, InputWords);
            Span<uint> output = results.AsSpan(i * OutputWords, OutputWords);
            Vector3 a = new(F(words[0]), F(words[1]), F(words[2]));
            Vector3 b = new(F(words[3]), F(words[4]), F(words[5]));
            var m = new Matrix4x4(F(words[6]), F(words[7]), F(words[8]), F(words[9]),
                F(words[10]), F(words[11]), F(words[12]), F(words[13]), F(words[14]), F(words[15]),
                F(words[16]), F(words[17]), F(words[18]), F(words[19]), F(words[20]), F(words[21]));
            output[0] = output[1] = output[2] = U(f.Read(a.X));
            output[3] = U(f.Add(a.X, b.X)); output[4] = U(f.Sub(a.X, b.X));
            output[5] = U(f.Mul(a.X, b.X)); output[6] = U(f.Abs(a.X)); output[7] = U(f.Neg(a.X));
            output[8] = U(f.Min(a.X, b.X)); output[9] = U(f.Max(a.X, b.X));
            output[10] = U(f.Clamp(a.X, F(words[22]), F(words[23])));
            output[11] = B(f.IsFinite(a.X));
            output[12] = B(f.Less(a.X, b.X)); output[13] = B(f.LessOrEqual(a.X, b.X));
            output[14] = B(f.Greater(a.X, b.X)); output[15] = B(f.GreaterOrEqual(a.X, b.X));
            output[16] = B(f.Equal(a.X, b.X)); output[17] = B(f.NotEqual(a.X, b.X));
            output[18] = U(f.Add(f.Mul(a.X, b.X), F(words[24])));
            output[19] = U(CoverageReferenceMath.Dot3(f, a, b));
            output[20] = U(CoverageReferenceMath.Dot2(f, new(a.X, a.Y), new(b.X, b.Y)));
            Write(output[21..], CoverageReferenceMath.Cross(f, a, b));
            Write(output[24..], CoverageReferenceMath.Sub3(f, a, b));
            Write(output[27..], CoverageReferenceMath.Point(f, a, m));
            Write(output[30..], CoverageReferenceMath.Linear(f, a, m));
            output[33] = B(CoverageReferenceMath.Equal3(f, a, b));
            output[34] = B(CoverageReferenceMath.IsFinite3(f, a));
            output[35] = f.Less(a.X, b.X) ? words[0] : words[3];
            float stored = a.X;
            f.IsFinite(ref stored);
            output[36] = U(stored);
        }
        return results;
    }

    static float F(uint word) => BitConverter.UInt32BitsToSingle(word);
    static uint U(float value) => BitConverter.SingleToUInt32Bits(value);
    static uint B(bool value) => value ? 1u : 0u;
    static void Write(Span<uint> output, Vector3 value)
    { output[0] = U(value.X); output[1] = U(value.Y); output[2] = U(value.Z); }
}
