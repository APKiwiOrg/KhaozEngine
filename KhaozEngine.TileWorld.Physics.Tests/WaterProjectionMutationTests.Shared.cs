using System;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using Xunit.Sdk;
using Xunit.Abstractions;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;
using P = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Plane;
using V = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Vector;
using S = KhaozEngine.TileWorld.Physics.TileWaterPreparedQuery.BasisState;

namespace KhaozEngine.Tests.TileWorld.Physics;

// Shared by KhaozEngine.TileWorld.Physics.Tests, which runs the default-control faults, and
// KhaozEngine.TileWorld.Physics.FloatingPoint.Tests, which runs the wrong-control fault.
public sealed partial class WaterProjectionMutationTests
{
    static void ReusedProjectionFault(string fault, ITestOutputHelper output,
        Func<WaterFloatingPointEnvironment, string, bool> change)
    {
        using var environment = WaterFloatingPointEnvironment.Capture();
        Assert.NotNull(environment);
        Assert.True(environment.SetNearest());
        Assert.True(TileWaterFloatingPointState.TryRead(out ulong initialToken));
        var parent = new TileWaterConvexShape([P.Point(3, 0, 0, 1)]);
        var shape = fault == "wrong-prefix-map"
            ? new TileWaterConvexShape([.. parent.Planes.ToArray(), parent.Planes[0].Opposite]) : parent;
        if (fault == "wrong-control")
            // A normal 2^-530 operand has an exactly subnormal squared normal, 2^-1060.
            // Reusing its nearest-mode projection under FZ hides the required unresolved refusal.
            shape = new([P.Point(Math.ScaleB(1, -530), 0, 0, 0)]);
        var source = fault == "stale-owner" ? new TileWaterConvexShape([P.Point(3, 0, 0, -1)]) : fault == "wrong-prefix-map" ? parent : shape;
        V start = V.Point(2, 0, 0), drive = V.Point(-3, 0, 0);
        if (fault == "wrong-control")
        {
            start = V.Point(1, 0, 0);
            drive = V.Point(-1, 0, 0);
        }
        var saved = new S[TileWaterPreparedQuery.MaxBases];
        var old = Prepare(source, saved, start, fault == "old-drive" ? V.Point(3, 0, 0) : drive);
        var original = old.Evaluate();
        Assert.True(original.Resolved);
        if (fault == "wrong-control")
        {
            Assert.True(change(environment, "flush-output"));
            Assert.True(TileWaterFloatingPointState.TryRead(out ulong changedToken));
            Assert.NotEqual(initialToken, changedToken);
        }
        var frozen = new FrozenTileWaterConvexShape(shape);
        var expected = frozen.Intersect(start, drive, I.Point(0), 0.25f);
        if (fault == "wrong-control") Assert.False(expected.Resolved);
        var states = new S[TileWaterPreparedQuery.MaxBases];
        var correct = Prepare(shape, states, start, drive);
        FrozenWaterComparison.Range(expected, correct.Evaluate());
        var broken = Prepare(shape, states, start, drive);
        for (int ordinal = 0; ordinal < shape.Bases.Length; ordinal++)
        {
            // Copy only G's projected values. Clips, roots and distances must execute afresh.
            int mapped = source.Bases.IndexOf(shape.Bases[ordinal]);
            if (mapped < 0) continue; // The appended face-only constraint always computes afresh.
            // Deliberately map the parent's active basis to its zero-active projection.
            S oldState = saved[fault == "wrong-prefix-map" ? 0 : mapped];
            Assert.True((oldState.Mask & 1) != 0);
            states[ordinal] = new S
            {
                Mask = 1,
                Projected = oldState.Projected,
                Q = oldState.Q,
                V = oldState.V,
                Lambda = oldState.Lambda,
                Velocity = oldState.Velocity
            };
        }
        var actual = broken.Evaluate();
        // Test output runs runner code on this thread, so it is written only under the default controls.
        Assert.True(environment.SetNearest());
        output.WriteLine($"PROJECTION_CONTROL {fault}: FrozenRangeBitsEqual");
        Assert.ThrowsAny<XunitException>(() => FrozenRangeBitsEqual(expected, actual));
    }

    static void FrozenRangeBitsEqual(FrozenTileWaterConvexShape.Range expected, TileWaterConvexShape.Range actual) =>
        FrozenWaterComparison.Range(expected, actual);

    static TileWaterPreparedQuery Prepare(TileWaterConvexShape shape, S[] states, V start, V drive) =>
        new(shape, states, new TileWaterPreparedQuery.ClipTerm[TileWaterPreparedQuery.MaxBases *
            (TileWaterPreparedQuery.MaxPlanes + 3)],
            new TileWaterPreparedQuery.PlaneState[TileWaterPreparedQuery.MaxPlanes], start, drive, I.Point(0), 0.25f);
}
