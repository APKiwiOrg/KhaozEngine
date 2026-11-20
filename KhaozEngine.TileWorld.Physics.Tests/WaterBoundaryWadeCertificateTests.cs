using KhaozEngine.Locomotion;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Collection("AllocSensitive")]
[Trait("Category", "WaterKernelContract")]
public sealed partial class WaterBoundaryWadeCertificateTests
{
    // Every row uses a free level of -0.001f, half height 0.75 and enter fraction 0.625, so 1 - 2 * 0.625 = -0.25 and
    // the body offset is 0.75 * -0.25 = -0.1875 exactly. The certificate adds the 0.001f error bound, the same binary32
    // value as the level's magnitude, so its line is -0.001f - 0.1875 + 0.001f = -0.1875 exactly in binary64.
    // The lowest path centre is the start plus the downward part of the drop, also exact: -0.0625 - 0.125 = -0.1875.
    // -0.18749999f is the next binary32 above -0.1875 (-0.1875 + 2^-26), -0.18750001f the next below, and
    // -0.12499999f the next above -0.125 (-0.125 + 2^-27). A proximity of 0.001f doubles the added term to 0.002f,
    // which raises the line to -0.1865 and covers the 2^-26 margin.
    [Theory]
    [InlineData(-0.1875f, 0f, 0f, false)]
    [InlineData(-0.18749999f, 0f, 0f, true)]
    [InlineData(-0.18750001f, 0f, 0f, false)]
    [InlineData(-0.0625f, -0.125f, 0f, false)]
    [InlineData(-0.0625f, -0.12499999f, 0f, true)]
    [InlineData(-0.1875f, 1f, 0f, false)]
    [InlineData(-0.18749999f, 1f, 0f, true)]
    [InlineData(-0.18749999f, 0f, 0.001f, false)]
    public void TheWadeCertificateIsStrictAndExactAtItsLine(float centre, float delta, float proximity, bool clear)
    {
        Assert.Equal(-0.1875d, MovementWaterBoundary.WadeThreshold(-0.001f, 0.75f, 0.625f,
            MovementQueryLease.CoverageSkinMetres, 0));
        Assert.Equal(clear, MovementWaterBoundaryCertificate.WadeClear(-0.001f, 0.75f, 0.625f, proximity, centre, delta));
    }

    // The directed rounding and flush rows run in KhaozEngine.TileWorld.Physics.FloatingPoint.Tests.
    [Theory]
    [InlineData("nearest")]
    public void TheWadeBoundsHoldUnderEveryControlMode(string mode) =>
        TheWadeBoundsHold(mode, WaterFloatingPointEnvironment.NearestOnly);
}
