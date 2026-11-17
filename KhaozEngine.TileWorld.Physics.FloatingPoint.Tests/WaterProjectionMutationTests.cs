using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Collection("AllocSensitive")]
[Trait("Category", "WaterKernelContract")]
public sealed partial class WaterProjectionMutationTests(ITestOutputHelper output)
{
    // The default-control faults run in KhaozEngine.TileWorld.Physics.Tests.
    [Theory]
    [InlineData("wrong-control")]
    public void ReusedProjectionFaultBreaksFrozenRangeParity(string fault) =>
        ReusedProjectionFault(fault, output, WaterFloatingPointEnvironment.AnyMode);
}
