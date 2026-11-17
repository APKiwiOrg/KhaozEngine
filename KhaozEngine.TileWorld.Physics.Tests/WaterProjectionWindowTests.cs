using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Collection("AllocSensitive")]
[Trait("Category", "WaterKernelContract")]
public sealed partial class WaterProjectionWindowTests
{
    // FlushToZeroSubnormalEnterAndExitRetainAllRangeBits and the changed-control rows below run in
    // KhaozEngine.TileWorld.Physics.FloatingPoint.Tests.
    [Theory]
    [InlineData("nearest")]
    public void SignedZeroWindowsAndChangingPreparationsRetainAllRangeBits(string mode) =>
        SignedZeroWindowsAndChangingPreparations(mode, WaterFloatingPointEnvironment.NearestOnly);
}
