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

// Shared by KhaozEngine.TileWorld.Physics.Tests, which runs the default-control row, and
// KhaozEngine.TileWorld.Physics.FloatingPoint.Tests, which runs every changed-control row.
public sealed partial class WaterProjectionWindowTests
{
    static void SignedZeroWindowsAndChangingPreparations(string mode, Func<WaterFloatingPointEnvironment, string, bool> change)
    {
        using var environment = WaterFloatingPointEnvironment.Capture();
        if (environment is null) return;
        Assert.True(environment.SetNearest());
        var field = GrazingWaterBank.Capture().Medium;
        var reference = new FrozenWaterReference(field.CaptureTraceForTests(), rebuildGeometry: true);
        if (!change(environment, mode)) return;
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
