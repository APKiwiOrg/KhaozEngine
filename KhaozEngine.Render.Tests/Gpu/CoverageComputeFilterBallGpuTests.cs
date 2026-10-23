using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageComputeFilterBallGpuTests(ITestOutputHelper output)
{
    [GpuFact]
    public void Accepted_balls_enclose_integer_polynomials_and_certify_the_same_rn32_word()
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        var path = CoverageGpuFilterPolicy.Resolve(gpu.Backend, gpu.Capabilities);
        output.WriteLine($"EFT contract backend={gpu.Backend} adapter={gpu.Capabilities.DeviceName} path={path}");
        if (path == CoverageGpuFilterPath.Exact) return;
        var cases = CoverageGpuPredicateProbe.Cases().Where(c => c.Op <= 1u).ToList();
        foreach (int exponent in new[] { -100, -78, -39, 0, 50, 100 })
        {
            float a = MathF.ScaleB(1, exponent), b = MathF.BitIncrement(a);
            cases.Add(new(0, default, new(a, b, 0), new(b, a, 0), default));
            cases.Add(new(0, new(a, a, 0), new(b, b, 0), new(-a, -a, 0), default));
        }
        var words = new List<uint> { (uint)cases.Count };
        foreach (var c in cases)
        {
            words.Add(c.Op);
            foreach (var p in new[] { c.A, c.B, c.C, c.D })
                words.AddRange(new[] { BitConverter.SingleToUInt32Bits(p.X), BitConverter.SingleToUInt32Bits(p.Y), BitConverter.SingleToUInt32Bits(p.Z) });
        }
        uint[] actual = CoverageGpuQueryProbe.Run(output, "CoverageFilteredEftProbe", CoverageGpuFilterProbe.Source,
            words.ToArray(), cases.Count * 6, options: new(PreciseMath: true, RequireFloat32Eft: true,
                FlushFloat32Denorms: path == CoverageGpuFilterPath.FilteredFlush));
        int accepted = 0, refused = 0;
        for (int row = 0; row < cases.Count; row++)
        {
            if (actual[row * 6] != 0u) { refused++; continue; }
            var c = cases[row];
            bool finite = new[] { c.A, c.B, c.C, c.D }.Take(c.Op == 0 ? 3 : 4).All(p => float.IsFinite(p.X) && float.IsFinite(p.Y));
            if (!finite) { Assert.True(float.IsNaN(BitConverter.UInt32BitsToSingle(actual[row * 6 + 2]))); continue; }
            BigInteger ax = Units(c.A.X), ay = Units(c.A.Y), bx = Units(c.B.X), by = Units(c.B.Y);
            BigInteger cx = Units(c.C.X), cy = Units(c.C.Y), dx = Units(c.D.X), dy = Units(c.D.Y);
            BigInteger exact = c.Op == 0 ? (bx - ax) * (cy - ay) - (by - ay) * (cx - ax)
                : (bx - ax) * (dx - cx) + (by - ay) * (dy - cy);
            BigInteger center = (UnitsWord(actual[row * 6 + 3]) + UnitsWord(actual[row * 6 + 4])) << 149;
            BigInteger radius = UnitsWord(actual[row * 6 + 5]) << 149;
            Assert.True(BigInteger.Abs(exact - center) <= radius, $"row {row}: unproved enclosure");
            Assert.Equal(exact.Sign, unchecked((int)actual[row * 6 + 1]));
            uint rounded = CoverageBinary32IntegerOracle.Round(BigInteger.Abs(exact), -298, exact.Sign);
            Assert.Equal(CoverageBinary32IntegerOracle.Flush(rounded), actual[row * 6 + 2]);
            accepted++;
        }
        Assert.True(accepted > 30 && refused > 30);
        output.WriteLine($"EFT balls accepted={accepted} refused={refused} differing words=0");
    }

    static BigInteger Units(float value) => UnitsWord(CoverageBinary32IntegerOracle.Flush(BitConverter.SingleToUInt32Bits(value)));
    static BigInteger UnitsWord(uint word) => CoverageBinary32IntegerOracle.Units(word) * ((word & 0x80000000u) == 0u ? 1 : -1);
}
