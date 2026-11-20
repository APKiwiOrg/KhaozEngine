using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Collection("AllocSensitive")]
[Trait("Category", "WaterKernelContract")]
public sealed partial class WaterBoundaryWadeCertificateTests
{
    // Every rounding direction is monotone and flushing to signed zero is weakly monotone, so the certificate's
    // bounds hold in each. The default-control row runs in KhaozEngine.TileWorld.Physics.Tests.
    [Theory]
    [InlineData("up")]
    [InlineData("down")]
    [InlineData("zero")]
    [InlineData("flush-output")]
    [InlineData("flush-input")]
    public void TheWadeBoundsHoldUnderEveryControlMode(string mode) =>
        TheWadeBoundsHold(mode, WaterFloatingPointEnvironment.AnyMode);
}
