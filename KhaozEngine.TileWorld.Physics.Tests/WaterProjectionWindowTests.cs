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
public sealed class WaterProjectionWindowTests
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

    [Theory]
    [InlineData("nearest")]
    [InlineData("up")]
    [InlineData("down")]
    [InlineData("zero")]
    [InlineData("flush-output")]
    [InlineData("flush-input")]
    public void SignedZeroWindowsAndChangingPreparationsRetainAllRangeBits(string mode)
    {
        using var environment = WaterFloatingPointEnvironment.Capture();
        if (environment is null) return;
        Assert.True(environment.Set("nearest"));
        var field = GrazingWaterBank.Capture().Medium;
        var reference = new FrozenWaterReference(field.CaptureTraceForTests(), rebuildGeometry: true);
        if (!environment.Set(mode)) return;
        var shape = new TileWaterConvexShape([P.Point(0.95, -1, 0.17, -0.35), P.Point(1, 0, 1, 1)]);
        var frozen = new FrozenTileWaterConvexShape(shape);
        var storage = new TileWaterPreparationStorage();
        foreach (double zero in new[] { 0d, BitConverter.Int64BitsToDouble(long.MinValue) })
        {
            V start = V.Point(0.375, -0.45606077, 0.79904), drive = V.Point(0.13, 0.096971154, zero);
            storage.BeginTrace();
            try
            {
                var prepared = storage.Workspace.Prepare(shape, start, drive, I.Point(zero), 0.3f);
                FrozenWaterComparison.Range(frozen.Intersect(start, drive, I.Point(zero), 0.3f), prepared.Evaluate());
                int offset = storage.Retain(ref prepared);
                Assert.True(offset >= 0);
                foreach (var window in new[] { (zero, 1d), (zero, zero), (0.25, 0.75), (1d, 1d) })
                    FrozenWaterComparison.Range(frozen.Intersect(start, drive, I.Point(zero), 0.3f, window.Item1, window.Item2),
                        storage.Evaluate(offset, shape, start, drive, I.Point(zero), 0.3f, window.Item1, window.Item2, 0, 0));
            }
            finally { storage.EndTrace(); }
            var lift = GrazingWaterBank.Lift();
            var query = new MovementMediumSweepQuery(lift.Body, new((float)zero, lift.Delta.Y, (float)zero));
            var origin = new Vector3((float)zero, (float)zero, (float)zero);
            _ = FrozenWaterComparison.Check(field, reference, query, origin);
            _ = FrozenWaterComparison.Check(field, reference, query, origin, spanCapacity: 0);
            _ = FrozenWaterComparison.Check(field, reference, query, origin, contactCapacity: 0);
        }
    }
}
