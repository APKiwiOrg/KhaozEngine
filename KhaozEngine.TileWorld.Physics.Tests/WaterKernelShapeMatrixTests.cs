using System;
using System.Linq;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;
using P = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Plane;
using V = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Vector;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Trait("Category", "WaterKernelContract")]
public sealed class WaterKernelShapeMatrixTests
{
    [Theory]
    [InlineData("tied-affine-witnesses")]
    [InlineData("feasible-distance-miss")]
    [InlineData("restricted-root-windows")]
    [InlineData("oblique-projection-order")]
    [InlineData("determinant-unresolved-defensive")]
    [InlineData("zero-motion-threshold")]
    [InlineData("nonfinite-triple-defensive")]
    [InlineData("nonfinite-project-defensive")]
    public void OrderedWindowEvaluationMatchesTheUnpreparedSnapshot(string kind)
    {
        // #1755. The named rows target preparation state, rather than relying on a random trace sweep.
        var shape = new TileWaterConvexShape([P.Point(1, 0, 0, 0)]);
        V start = V.Point(0, 0, 0), drive = V.Point(1, 0, 0);
        float radius = 0.25f;
        if (kind == "feasible-distance-miss")
        {
            shape = new([new(new(new(1, Math.BitIncrement(1)), I.Point(0), I.Point(0)), I.Point(0))]);
            start = V.Point(3, 0, 0);
            drive = V.Point(0, 0, 0);
        }
        if (kind == "restricted-root-windows")
        {
            shape = new([P.Point(1, 0, 0, 1), P.Point(-1, 0, 0, 1),
                P.Point(0, 1, 0, 1), P.Point(0, -1, 0, 1), P.Point(0, 0, 1, 1), P.Point(0, 0, -1, 1)]);
            start = V.Point(-3, 0, 0);
            drive = V.Point(6, 0, 0);
        }
        if (kind == "oblique-projection-order")
        {
            shape = new([P.Point(0.95, -1, 0.17, -0.35), P.Point(1, 0, 1, 1),
                P.Point(-1, 0, 0, 0), P.Point(0, 0, -1, 0), P.Point(0, 1, 0, 0)]);
            start = V.Point(0.375, -0.45606077, 0.79904);
            drive = V.Point(0.13, 0.096971154, -0.4);
            radius = 0.3f;
        }
        if (kind == "determinant-unresolved-defensive")
        {
            // Exact lower-endpoint normals have determinant 1. Interval crosses cannot prove its sign.
            shape = new([new(new(new(1, 2), new(1, 2), I.Point(0)), I.Point(-1000)),
                new(new(new(1, 2), new(2, 3), I.Point(0)), I.Point(-1000))]);
            start = V.Point(100, 100, 0);
        }
        if (kind == "zero-motion-threshold")
        {
            shape = new([new(V.Point(1, 0, 0), new(-0.000001, 0.000001))]);
            start = V.Point(0.25, 0, 0);
            drive = V.Point(0, -0d, 0);
        }
        if (kind == "nonfinite-triple-defensive")
        {
            // Ordinary construction also has earlier bases. Restricting it reaches the triple's no-finite-check path.
            shape = new([P.Point(1, 0, 0, 0), P.Point(0, 1, 0, 0), P.Point(0, 0, 1, 0)]);
            int ordinal = shape.CaptureGeometryForTests().Bases.Select((basis, i) => (basis, i)).Single(p => p.basis.C >= 0).i;
            shape = shape.KeepOnlyBasisForTests(ordinal);
            drive = V.Point(double.PositiveInfinity, 0, 0);
        }
        if (kind == "nonfinite-project-defensive")
        {
            shape = new([new(V.Point(1, 0, 0), I.Point(double.NaN))]);
            start = V.Point(1, 0, 0);
        }
        var frozen = new FrozenTileWaterConvexShape(shape);
        var full = frozen.Intersect(start, drive, I.Point(0), radius);
        if (kind == "tied-affine-witnesses")
        {
            Assert.True(full.Hit);
            Assert.Equal(0, full.Enter);
            Assert.Equal(1, full.WitnessDrive.X.Low);
        }
        if (kind == "feasible-distance-miss")
        {
            Assert.False(full.Hit);
            Assert.True(full.Error > 0);
            Assert.True(double.IsFinite(full.MinimumSquared));
        }
        if (kind is "determinant-unresolved-defensive" or "nonfinite-project-defensive") Assert.False(full.Resolved);
        (double Enter, double Exit)[] windows = [(0, 1), (0, 0), (1, 1), (0.125, 0.25), (0.5, 0.5), (0.25, 0.875)];
        if (full.Resolved && full.Hit && full.Enter > 0 && full.Exit < 1)
            windows = [.. windows, (full.Enter, full.Enter), (full.Exit, full.Exit),
                (Math.BitDecrement(full.Enter), Math.BitIncrement(full.Enter)),
                (Math.BitDecrement(full.Exit), Math.BitIncrement(full.Exit)),
                ((full.Enter + full.Exit) / 2, (full.Enter + full.Exit) / 2), (full.Enter / 2, full.Enter / 2)];
        Assert.NotEmpty(windows);
        foreach (var window in windows)
            Compare(start, drive, window.Enter, window.Exit);
        // Another preparation's input sits between equal requests on the same immutable shape.
        Compare(V.Point(-0.375, 0.5, 0.125), V.Point(-2, 0.25, -0d), 0, 1);
        foreach (var window in windows.Reverse())
            Compare(start, drive, window.Enter, window.Exit);

        void Compare(V from, V delta, double enter, double exit) => FrozenWaterComparison.Range(
            frozen.Intersect(from, delta, I.Point(0), radius, enter, exit),
            shape.Intersect(from, delta, I.Point(0), radius, enter, exit));
    }

    [Theory]
    [InlineData(-0.5, 1, false, 0.1, 0.9)]
    [InlineData(0.5, -1, false, 0.1, 0.9)]
    [InlineData(0, 0d, false, 0.1, 0.9)]
    [InlineData(0, 0d, true, 0.1, 0.9)]
    [InlineData(1, 0d, true, 0.1, 0.9)]
    [InlineData(0, 0d, false, 0.8, 0.2)] // Defensive invalid window. The zero-slope branch never inspects it.
    public void ClipSignsAndTheZeroSlopeBranchKeepExactEndpoints(double constant, double slope, bool negativeZero,
        double enter, double exit)
    {
        if (negativeZero) slope = BitConverter.Int64BitsToDouble(long.MinValue);
        double expectedEnter = enter, expectedExit = exit;
        bool expected = FrozenTileWaterConvexShape.ClipForTests(I.Point(constant), I.Point(slope), ref expectedEnter, ref expectedExit);
        bool actual = TileWaterConvexShape.ClipForTests(I.Point(constant), I.Point(slope), ref enter, ref exit);
        Assert.Equal(expected, actual);
        Assert.Equal(BitConverter.DoubleToInt64Bits(expectedEnter), BitConverter.DoubleToInt64Bits(enter));
        Assert.Equal(BitConverter.DoubleToInt64Bits(expectedExit), BitConverter.DoubleToInt64Bits(exit));
    }
}
