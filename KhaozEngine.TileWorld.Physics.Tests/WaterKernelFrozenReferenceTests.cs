using System;
using System.Security.Cryptography;
using System.Numerics;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;
using P = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Plane;
using V = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Vector;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Trait("Category", "WaterKernelContract")]
public sealed class WaterKernelFrozenReferenceTests
{
    [Theory]
    [InlineData("FrozenTileWaterConvexShape.cs", "6650BDD1B9EC4CA0219B087EC02A58CEDF14D9AA57ED0AECD3717CD1EE0EA482")]
    [InlineData("FrozenTileWaterPrismCoverage.cs", "5A4A5B698C8E4CA3C0F758F86B5186A16974AD45A3D39CB10FDCF0C8956F1811")]
    public void FrozenSourceIsAnExplicitReviewedSnapshot(string file, string expected)
    {
        // #1755. This reference is deliberate. Change its hash only with a reviewed behavior decision.
        using var source = typeof(WaterKernelFrozenReferenceTests).Assembly.GetManifestResourceStream(
            "KhaozEngine.Tests.Fixtures.FrozenWater." + file);
        Assert.NotNull(source);
        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(source)));
    }

    [Fact]
    public void FrozenEvaluationKeepsProductionCountersUntouched()
    {
        var shape = new TileWaterConvexShape([P.Point(1, 0, 0, 0)]);
        var frozen = new FrozenTileWaterConvexShape(shape);
        var field = GrazingWaterBank.Capture().Medium;
        var reference = new FrozenWaterReference(field.CaptureTraceForTests());
        TileWaterQueryWork.Reset();
        TileWaterQueryWork.Intersections = 2;
        TileWaterQueryWork.NonuniformVisits = 3;
        TileWaterQueryWork.IndexNodes = 5;
        TileWaterQueryWork.ConvexIntersections = 7;
        TileWaterQueryWork.BedSeamProofs = 11;
        TileWaterQueryWork.BedSeamCertificates = 13;
        TileWaterQueryWork.ShapePreparations = 17;
        TileWaterQueryWork.LazyCoefficientFills = 19;
        try
        {
            _ = frozen.Intersect(V.Point(0, 0, 0), V.Point(1, 0, 0), I.Point(0), 0.25f);
            _ = reference.Trace(GrazingWaterBank.Lift(), Vector3.Zero, GrazingWaterBank.Identity,
                new KhaozEngine.Locomotion.MovementCoverageSpan[64], new KhaozEngine.Locomotion.MovementDomainContact[256]);
            Assert.True(TileWaterQueryWork.Enabled);
            Assert.Equal(new[] { 2, 3, 5, 7, 11, 13, 17, 19 }, new[] { TileWaterQueryWork.Intersections,
                TileWaterQueryWork.NonuniformVisits, TileWaterQueryWork.IndexNodes, TileWaterQueryWork.ConvexIntersections,
                TileWaterQueryWork.BedSeamProofs, TileWaterQueryWork.BedSeamCertificates,
                TileWaterQueryWork.ShapePreparations, TileWaterQueryWork.LazyCoefficientFills });
        }
        finally { TileWaterQueryWork.Enabled = false; }
    }

    [Fact]
    public void FrozenShapeAndTraceCompareCompleteRecordsAndCallerSentinels()
    {
        var shape = new TileWaterConvexShape([P.Point(1, 0, 0, 0)]);
        var frozen = new FrozenTileWaterConvexShape(shape);
        FrozenWaterComparison.Range(frozen.Intersect(V.Point(0, 0, 0), V.Point(1, 0, 0), I.Point(0), 0.25f),
            shape.Intersect(V.Point(0, 0, 0), V.Point(1, 0, 0), I.Point(0), 0.25f));
        var field = GrazingWaterBank.Capture().Medium;
        _ = FrozenWaterComparison.Check(field, new(field.CaptureTraceForTests()), GrazingWaterBank.Lift());
    }
}
