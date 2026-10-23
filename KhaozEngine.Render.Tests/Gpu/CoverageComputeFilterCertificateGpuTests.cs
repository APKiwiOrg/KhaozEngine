using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageComputeFilterCertificateGpuTests(ITestOutputHelper output)
{
    [GpuFact]
    public void Sign_only_and_B30_certificates_match_independent_integer_words_in_every_admitted_mode()
    {
        using var gpu = GpuDeviceContext.CreateHeadless();
        var cases = CoverageFilterCertificateCases.All();
        foreach (var mode in CoverageFilterNativeEvidence.Modes(gpu.GpuDevice, output))
        {
            var words = new List<uint> { (uint)cases.Length };
            foreach (var row in cases)
            {
                words.Add(row.Input.Op);
                foreach (var p in new[] { row.Input.A, row.Input.B, row.Input.C, row.Input.D })
                {
                    words.Add(BitConverter.SingleToUInt32Bits(p.X)); words.Add(BitConverter.SingleToUInt32Bits(p.Y)); words.Add(BitConverter.SingleToUInt32Bits(p.Z));
                }
                words.Add(row.SignOnly ? 1u : 0u);
            }
            uint[] actual = CoverageGpuQueryProbe.Run(output, "CoverageFilteredEft" + mode + "Certificate", CoverageGpuFilterProbe.Source,
                words.ToArray(), cases.Length * 17, options: new(PreciseMath: true, RequireFloat32Eft: true,
                    FlushFloat32Denorms: mode == CoverageGpuFilterPath.FilteredFlush));
            int refused = 0, signs = 0, crossings = 0;
            for (int row = 0; row < cases.Length; row++)
            {
                var c = cases[row]; int d = row * 17;
                Assert.True(actual[d] == (c.Refuse ? 1u : 0u), $"{mode} {c.Name}: refusal={actual[d]}, expected={c.Refuse}");
                if (c.Refuse)
                {
                    refused++; output.WriteLine($"FILTER CERT mode={mode} case={c.Name} refused=true"); continue;
                }
                BigInteger exact = CoverageFilterCertificateCases.Exact(c.Input);
                Assert.Equal(exact.Sign, unchecked((int)actual[d + 1]));
                if (c.SignOnly)
                {
                    Assert.Equal(BitConverter.SingleToUInt32Bits(exact.Sign), actual[d + 2]);
                    if (actual[d + 13] == 0) Enclosed(exact, actual, d + 3, c.Name);
                    signs++;
                }
                else
                {
                    var expected = CoverageFilterCertificateCases.Crossing(c.Input);
                    Assert.Equal(expected.Power, unchecked((int)actual[d + 6]));
                    Assert.Equal(expected.A, actual[d + 7]); Assert.Equal(expected.B, actual[d + 8]);
                    Assert.Equal(expected.Crossing, actual[d + 2]);
                    Assert.Equal(CoverageFilterCertificateCases.Point(c.Input, expected.Crossing), actual.AsSpan(d + 14, 3).ToArray());
                    Assert.Equal(CoverageFilterCertificateCases.Exact(c.Input, true).Sign, unchecked((int)actual[d + 9]));
                    Enclosed(exact, actual, d + 3, c.Name);
                    Enclosed(CoverageFilterCertificateCases.Exact(c.Input, true), actual, d + 10, c.Name);
                    crossings++;
                }
                output.WriteLine($"FILTER CERT mode={mode} case={c.Name} refused=false power={unchecked((int)actual[d + 6])} a={actual[d + 7]:x8} b={actual[d + 8]:x8} result={actual[d + 2]:x8} point={actual[d + 14]:x8},{actual[d + 15]:x8},{actual[d + 16]:x8}");
            }
            Assert.True(signs > 20 && crossings > 12 && refused > 0);
            output.WriteLine($"FILTER CERT SUMMARY mode={mode} executed={cases.Length} signs={signs} crossings={crossings} refused={refused} differences=0");
        }
    }

    static void Enclosed(BigInteger exact, uint[] actual, int offset, string name)
    {
        BigInteger hi = CoverageFilterCertificateCases.Signed(actual[offset]), lo = CoverageFilterCertificateCases.Signed(actual[offset + 1]);
        BigInteger radius = CoverageFilterCertificateCases.Signed(actual[offset + 2]);
        Assert.True(radius.Sign >= 0 && BigInteger.Abs(exact - ((hi + lo) << 149)) <= radius << 149, name + ": enclosure");
    }
}
