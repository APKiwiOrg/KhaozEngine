using System;
using System.Runtime.InteropServices;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;
using P = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Plane;
using V = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Vector;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Collection("AllocSensitive")]
[Trait("Category", "WaterKernelContract")]
public sealed class WaterKernelFloatingPointTests
{
    [Fact]
    public void ConfiguredHostedArchitecturesHaveANativeControlToken()
    {
        if ((OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) &&
            RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.X64)
            Assert.True(TileWaterFloatingPointState.TryRead(out _));
    }

    [Theory]
    [InlineData("up")]
    [InlineData("down")]
    [InlineData("flush-output")]
    public void APreparationUnderOtherControlsTakesTheDirectEvaluator(string mode)
    {
        using var environment = WaterFloatingPointEnvironment.Capture();
        if (environment is null) return; // Unsupported native ABI retains the direct evaluation contract.
        var shape = new TileWaterConvexShape([P.Point(0.95, -1, 0.17, -0.35), P.Point(1, 0, 1, 1)]);
        var frozen = new FrozenTileWaterConvexShape(shape);
        V start = V.Point(0.375, -0.45606077, 0.79904), drive = V.Point(0.13, 0.096971154, -0.4);
        var workspace = new TileWaterPreparationWorkspace();
        TileWaterQueryWork.Reset();
        try
        {
            Assert.True(environment.Set("nearest"));
            bool supported = TileWaterFloatingPointState.TryRead(out ulong before);
            var prepared = shape.Prepare(workspace, start, drive, I.Point(0), 0.3f);
            FrozenWaterComparison.Range(frozen.Intersect(start, drive, I.Point(0), 0.3f), prepared.Evaluate());
            int fills = TileWaterQueryWork.LazyCoefficientFills;
            Assert.True(environment.Set(mode));
            if (supported)
            {
                Assert.True(TileWaterFloatingPointState.TryRead(out ulong after));
                Assert.NotEqual(before, after);
            }
            FrozenWaterComparison.Range(frozen.Intersect(start, drive, I.Point(0), 0.3f, 0.25, 0.75), prepared.Evaluate(0.25, 0.75));
            Assert.Equal(fills, TileWaterQueryWork.LazyCoefficientFills);
            Assert.Equal(1, TileWaterQueryWork.ShapePreparations);
            Assert.Equal(2, TileWaterQueryWork.ConvexIntersections);
        }
        finally
        {
            environment.Restore();
            TileWaterQueryWork.Enabled = false;
        }
    }
}
