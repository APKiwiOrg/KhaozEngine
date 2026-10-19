using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Render3D;
using CoverageReferenceSupport = KhaozEngine.Render3D.CoverageReferenceSupport;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageComputePredicateGpuTests(ITestOutputHelper output)
{
    // All backends run this fact. Direct3D11 and Vulkan native execution is hosted-only.
    [GpuFact]
    public void Exact_predicates_and_grid_preserve_discrete_decisions_on_device()
    {
        var cases = CoverageGpuPredicateProbe.Cases();
        uint[] words = CoverageGpuQueryProbe.Run(output, CoverageGpuPredicateProbe.Name, CoverageGpuPredicateProbe.Source,
            CoverageGpuPredicateProbe.Inputs(cases), cases.Length * CoverageGpuPredicateProbe.OutputWords);
        Assert.Equal(unchecked((uint)-1), words[0]);
        Assert.Equal(0x80000000u, words[1]);
        Assert.Equal(0u, words[21]); Assert.Equal(0u, words[22]);
        for (int row = 0; row < cases.Length; row++)
        {
            var c = cases[row];
            int offset = row * CoverageGpuPredicateProbe.OutputWords;
            var f = new CoverageReferenceArithmetic();
            if (c.Op <= 1 && Finite(c.A) && Finite(c.B) && Finite(c.C) && (c.Op == 0 || Finite(c.D)))
            {
                BigInteger exact = CoverageGpuPredicateProbe.Exact(c), magnitude = BigInteger.Abs(exact);
                Assert.Equal(unchecked((uint)exact.Sign), words[offset]);
                uint expected = CoverageBinary32IntegerOracle.Flush(BitConverter.SingleToUInt32Bits(CoverageReferencePredicateTests.Round(exact)));
                Assert.Equal(expected, words[offset + 1]);
                for (int limb = 0; limb < 18; limb++)
                { Assert.Equal((uint)(magnitude & uint.MaxValue), words[offset + 2 + limb]); magnitude >>= 32; }
                Assert.Equal((uint)BigInteger.Abs(exact).GetBitLength(), words[offset + 20]);
                var reference = c.Op == 0 ? f.Edge(c.A, c.B, c.C) : f.Dot(c.A, c.B, c.C, c.D);
                Assert.Equal(exact.Sign, reference.Sign);
                Assert.Equal(expected, BitConverter.SingleToUInt32Bits(reference.Value));
            }
            else if (c.Op <= 1)
            {
                Assert.Equal(0u, words[offset]);
                Assert.True(float.IsNaN(BitConverter.UInt32BitsToSingle(words[offset + 1])));
            }
            else if (c.Op == 2)
            {
                float expected = f.CrossingParameter(c.A, c.B, c.C, c.D);
                if (float.IsNaN(expected)) Assert.True(float.IsNaN(BitConverter.UInt32BitsToSingle(words[offset + 1])));
                else
                {
                    uint independent = CoverageGpuPredicateProbe.Crossing(c);
                    Assert.Equal(independent, words[offset + 1]);
                    Assert.Equal(independent, BitConverter.SingleToUInt32Bits(expected));
                }
                if (!float.IsNaN(expected) && c.C.Y == -c.D.Y && c.C.Y != 0) Assert.Equal(0x3f000000u, words[offset + 1]);
            }
            else
            {
                int expected = CoverageReferenceSupport.Cell(f, c.A.X, c.B.X, c.C.X);
                Assert.Equal((uint)expected, words[offset]);
                if (float.IsNaN(c.A.X)) Assert.Equal(0u, words[offset]);
                if (c.A.X == c.C.X && c.B.X < c.C.X) Assert.Equal(15u, words[offset]);
            }
        }
        output.WriteLine($"Matched signs, all 18 limbs, RN32/F words, crossings and grid cells for {cases.Length} rows");
    }

    static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y);
}
