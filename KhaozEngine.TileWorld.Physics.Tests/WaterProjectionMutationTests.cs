using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Collection("AllocSensitive")]
[Trait("Category", "WaterKernelContract")]
public sealed partial class WaterProjectionMutationTests(ITestOutputHelper output)
{
    // The wrong-control fault runs in KhaozEngine.TileWorld.Physics.FloatingPoint.Tests.
    [Theory]
    [InlineData("old-drive")]
    [InlineData("wrong-prefix-map")]
    [InlineData("stale-owner")]
    public void ReusedProjectionFaultBreaksFrozenRangeParity(string fault) =>
        ReusedProjectionFault(fault, output, WaterFloatingPointEnvironment.NearestOnly);
}
