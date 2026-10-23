using System;
using System.Linq;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class CoverageFilterCertificateTests
{
    [Theory]
    [InlineData(0x3f7fffffu, -297, 0x3fffffffu, 0xbf000000u, 0x3f4cccccu)]
    [InlineData(0x3f800000u, -298, 0x3f800000u, 0xbe800000u, 0x3f4ccccdu)]
    [InlineData(0x3f800001u, -298, 0x3f800001u, 0xbe800000u, 0x3f4ccccdu)]
    public void B30_oracle_uses_unrounded_common_binade(uint side, int power, uint a, uint b, uint crossing)
    {
        var row = CoverageFilterCertificateCases.All().Single(c => c.Name == $"binade-1-{side:x8}");
        Assert.Equal(new CoverageFilterCertificateCases.CrossingWords(power, a, b, crossing), CoverageFilterCertificateCases.Crossing(row.Input));
    }

    [Fact]
    public void Targeted_cases_pin_midpoints_refusals_and_endpoint_semantics_independently()
    {
        var rows = CoverageFilterCertificateCases.All();
        Assert.Equal(rows.Length, rows.Select(c => c.Name).Distinct().Count());
        foreach (int sign in new[] { 1, -1 })
            foreach (int half in new[] { 1, 3 })
            {
                var row = rows.Single(c => c.Name == $"tie-cross-{sign}-{half}");
                var actual = CoverageFilterCertificateCases.Crossing(row.Input);
                Assert.Equal(-298, actual.Power);
                Assert.Equal((sign < 0 ? 0x80000000u : 0u) | (half == 1 ? 0x3f800000u : 0x3f800002u), actual.A);
                Assert.Equal(actual.A ^ 0x80000000u, actual.B);
                Assert.Equal(0x3f000000u, actual.Crossing);
                Assert.Equal(new uint[] { 0x3f800000, 0, 0 }, CoverageFilterCertificateCases.Point(row.Input, actual.Crossing));
            }
        foreach (string name in new[] { "endpoint-a", "endpoint-b", "same-side", "exact-collinear" })
            Assert.Equal(0x7fc00000u, CoverageFilterCertificateCases.Crossing(rows.Single(c => c.Name == name).Input).Crossing);
        var boundary = CoverageFilterCertificateCases.Crossing(rows.Single(c => c.Name == "binade-tie-refuse-1").Input);
        Assert.Equal(-297, boundary.Power);
        Assert.Equal(0x40000000u, boundary.A);
        Assert.Equal(0xbf000001u, boundary.B);
        Assert.Equal(20, rows.Count(c => c.Refuse));
        Assert.True(rows.Count(c => c.SignOnly && !c.Refuse) >= 20);
        Assert.Equal(0, CoverageFilterCertificateCases.Exact(rows.Single(c => c.Name == "dot-cancel").Input).Sign);
    }
}
