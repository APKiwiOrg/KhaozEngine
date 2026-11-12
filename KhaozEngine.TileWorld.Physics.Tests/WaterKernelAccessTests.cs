using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.TileWorld.Physics;
using Xunit;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;
using P = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Plane;
using V = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Vector;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Trait("Category", "WaterKernelContract")]
public sealed class WaterKernelAccessTests
{
    [Fact]
    public void GeometrySnapshotOwnsOrderedReadOnlyCopies()
    {
        // #1755. The frozen evaluator must read geometry, never prepared values or live mutable arrays.
        P[] planes = [P.Point(1, 0, 0, 1), P.Point(0, 1, 0, 2), P.Point(0, 0, 1, 3)];
        var shape = new TileWaterConvexShape(planes);
        var geometry = shape.CaptureGeometryForTests();
        Assert.Equal(planes, geometry.Planes);
        Assert.Equal(new[] { (-1, -1, -1), (0, -1, -1), (0, 1, -1), (0, 1, 2), (0, 2, -1),
            (1, -1, -1), (1, 2, -1), (2, -1, -1) }, geometry.Bases.Select(b => (b.A, b.B, b.C)));
        planes[0] = P.Point(-1, 0, 0, 7);
        Assert.Equal(P.Point(1, 0, 0, 1), geometry.Planes[0]);
        Assert.Throws<NotSupportedException>(() => ((IList<P>)geometry.Planes)[0] = planes[0]);
        Assert.Null(geometry.BedSeam);
    }

    [Fact]
    public void PreparationCountersAreZeroBeforePreparationExistsAndWindowCallsKeepTheirMeaning()
    {
        var shape = new TileWaterConvexShape([P.Point(1, 0, 0, 0)]);
        TileWaterQueryWork.Reset();
        try
        {
            _ = shape.Intersect(V.Point(0, 0, 0), V.Point(1, 0, 0), I.Point(0), 0.25f);
            _ = shape.Intersect(V.Point(0, 0, 0), V.Point(1, 0, 0), I.Point(0), 0.25f, 0, 0);
            Assert.Equal(2, TileWaterQueryWork.ConvexIntersections);
            Assert.Equal(0, TileWaterQueryWork.ShapePreparations);
            Assert.Equal(0, TileWaterQueryWork.LazyCoefficientFills);
            TileWaterQueryWork.Enabled = false;
            _ = shape.Intersect(V.Point(0, 0, 0), V.Point(1, 0, 0), I.Point(0), 0.25f);
            Assert.Equal(2, TileWaterQueryWork.ConvexIntersections);
            Assert.Equal(0, TileWaterQueryWork.ShapePreparations);
            Assert.Equal(0, TileWaterQueryWork.LazyCoefficientFills);
        }
        finally { TileWaterQueryWork.Enabled = false; }
    }
}
