using System.Runtime.Versioning;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
public sealed class ShippedSceneFoliageRasterTests
{
    [Theory]
    [InlineData(0, 1, 4, -100000, 100000, 0, 0)]
    [InlineData(-4, 1, 33, -100000, 100000, 0, 1)]
    [InlineData(0, 1, 32, -100000, 100000, 0, 0)]
    [InlineData(1000000, .125f, 33, -100000, 100000, 0, 1)]
    [InlineData(0, 1, 1, -100000, 100000, 0, 0)]
    [InlineData(0, 1, 4, 1, 100000, 0, -1)]
    [InlineData(-4, 1, 4, -100000, -1, 0, -1)]
    public void RasterInterval_ClosesAtSourceEndpointBeforeEnumerating(float origin, float cell, int size,
        int first, int last, int expectedFirst, int expectedLast) =>
        Assert.Equal((expectedFirst, expectedLast), ShippedSceneFoliageRoster.RasterOwners(origin, cell, size, first, last));
}
