using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;
using P = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Plane;
using V = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Vector;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Collection("AllocSensitive")]
[Trait("Category", "WaterKernelContract")]
public sealed partial class WaterProjectionWindowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlushToZeroSubnormalEnterAndExitRetainAllRangeBits(bool subnormalExit)
    {
        using var environment = WaterFloatingPointEnvironment.Capture();
        Assert.NotNull(environment);
        Assert.True(environment.Set("flush-output"));
        var shape = new TileWaterConvexShape([P.Point(1, 0, 0, 0)]);
        var frozen = new FrozenTileWaterConvexShape(shape);
        V start = V.Point(-1, 0, 0), drive = V.Point(0, 0, 0);
        var workspace = new TileWaterPreparationWorkspace();
        var prepared = workspace.Prepare(shape, start, drive, I.Point(0), 0.25f);
        _ = prepared.Evaluate();
        double enter = subnormalExit ? 0 : double.Epsilon;
        double exit = subnormalExit ? double.Epsilon : 0.5;
        FrozenWaterComparison.Range(frozen.Intersect(start, drive, I.Point(0), 0.25f, enter, exit),
            prepared.Evaluate(enter, exit));
    }

    // The default-control row runs in KhaozEngine.TileWorld.Physics.Tests.
    [Theory]
    [InlineData("up")]
    [InlineData("down")]
    [InlineData("zero")]
    [InlineData("flush-output")]
    [InlineData("flush-input")]
    public void SignedZeroWindowsAndChangingPreparationsRetainAllRangeBits(string mode) =>
        SignedZeroWindowsAndChangingPreparations(mode, WaterFloatingPointEnvironment.AnyMode);
}
