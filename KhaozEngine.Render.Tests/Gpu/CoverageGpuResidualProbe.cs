using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tests.Gpu;

internal static class CoverageGpuResidualProbe
{
    internal const string Name = "CoverageResidual";
    internal static string Source => """
        #version 450
        layout(local_size_x = 64) in;
        layout(std430, set = 0, binding = 0) readonly buffer InputBuf { uint Inputs[]; };
        layout(std430, set = 0, binding = 1) buffer ResultBuf { uint Results[]; };
        """ + CoverageGpuResidualGlsl.Source + """
        void main() {
            uint row = gl_GlobalInvocationID.x;
            if (row >= Inputs[0]) return;
            uint s = 1u + row*5u, d = row*3u;
            CovResidual a = CovResidual(Inputs[s], 0u, int(Inputs[s+1u]));
            CovResidual b = covResidualProduct(CovResidual(Inputs[s+2u], 0u, 0),
                CovResidual(Inputs[s+3u], 0u, int(Inputs[s+4u])));
            Results[d] = b.low; Results[d+1u] = b.high;
            Results[d+2u] = uint(covResidualCompare(a, b));
        }
        """;

    // Literal factors exercise zero, both exponent directions, the 32-bit boundary, 49/50-bit
    // products, and equal leading powers differing by one low bit. Expected words use BigInteger.
    static readonly (uint A, int APower, uint B, uint C, int ProductPower)[] Cases =
    {
        (0, 0, 0, 0, 0), (0, 100, 1, 1, -300), (1, -300, 0, 1, 100),
        (1, 0, 1, 1, 0), (1, 254, 0x1ffffff, 0x1ffffff, -300),
        (1, -300, 0x1ffffff, 0x1ffffff, 254),
        (1, 31, 0x7fff, 0xffff, 0), (1, 31, 0x8000, 0x10000, 0),
        (1, 31, 0x8001, 0x10000, 0),
        (1, 32, 0xffff, 0xffff, 0), (1, 32, 0x10000, 0x10000, 0),
        (1, 32, 0x10001, 0x10000, 0),
        (1, 49, 0xffffff, 0x1ffffff, 0), (1, 49, 0x1ffffff, 0x1ffffff, 0),
        (1, 50, 0x1ffffff, 0x1ffffff, 0),
        (0xffffff, 26, 0x1ffffff, 0x1ffffff, 0),
        (0xffffff, 25, 0xffffff, 0x1ffffff, 0), (0xffffff, 25, 0x1ffffff, 0xffffff, 0),
        (0x800000, -149, 0x1000001, 1, -150),
        (0x800001, -149, 0x1000001, 1, -150),
        (0x800001, -149, 0x1000001, 0x800001, -300),
        (0x800001, 0, 0x800001, 1, 0), (0xffffff, 0, 0xffffff, 1, 0),
        (0xffffff, 23, 0xffffff, 0x800000, 0),
        (0xffffff, 24, 0xffffff, 0x1000000, 0),
        (0x800001, -30, 0x800001, 0x1000000, -54),
        (1, -150, 1, 1, -150), (1, -150, 1, 1, -149), (1, -149, 1, 1, -150),
    };

    internal static (uint[] Inputs, uint[] Expected) Data()
    {
        var inputs = new List<uint> { (uint)Cases.Length };
        var expected = new List<uint>();
        foreach (var c in Cases)
        {
            inputs.AddRange(new[] { c.A, unchecked((uint)c.APower), c.B, c.C, unchecked((uint)c.ProductPower) });
            BigInteger product = (BigInteger)c.B * c.C;
            expected.Add((uint)(product & uint.MaxValue)); expected.Add((uint)(product >> 32));
            int common = System.Math.Min(c.APower, c.ProductPower);
            BigInteger a = (BigInteger)c.A << (c.APower - common);
            BigInteger b = product << (c.ProductPower - common);
            expected.Add(unchecked((uint)a.CompareTo(b)));
        }
        return (inputs.ToArray(), expected.ToArray());
    }
}
