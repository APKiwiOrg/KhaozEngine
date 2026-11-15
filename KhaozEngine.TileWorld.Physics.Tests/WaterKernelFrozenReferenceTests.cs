using System;
using System.Linq;
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
    [InlineData("FrozenTileCapsuleBoxPath.cs", "6A3523881EDFA5F695453461D261564E1424824F45D9B58341246EC428B438B9")]
    [InlineData("FrozenTileWaterArithmetic.cs", "6017A0EB8952198BC6E6F42CBC58C137374A1DD3CC399B68D006B709DFE0287F")]
    [InlineData("FrozenTileWaterBoxIndex.cs", "14809E5CFABB4284A2F8304446C413945F064349EA8A025FDF36FC65201A631C")]
    [InlineData("FrozenTileWaterColumnCoverage.cs", "5DC3D7470AF2DEE4011E867FEBDE5A2329825B99BDF9F0BC17CE1EF4CAB4F18F")]
    [InlineData("FrozenTileWaterConvexShape.cs", "6650BDD1B9EC4CA0219B087EC02A58CEDF14D9AA57ED0AECD3717CD1EE0EA482")]
    [InlineData("FrozenTileWaterFlatIndex.cs", "96D58E66A2461ED88DEF0B35EAB0F2453EA4E7C664C6DC4588D44DB56E8F46CC")]
    [InlineData("FrozenTileWaterPathBounds.cs", "783BED32BD7F70F957771DE71B50495B78493734B3B044432CBEA2C3CE922542")]
    [InlineData("FrozenTileWaterPrismCoverage.cs", "A77BCCCBDAD85644DF735EE78F19BFEF660C25E689A955B1E390E63E0B2F9C50")]
    public void FrozenSourceIsAnExplicitReviewedSnapshot(string file, string expected)
    {
        // #1755. This reference is deliberate. Change its hash only with a reviewed behavior decision.
        using var source = typeof(WaterKernelFrozenReferenceTests).Assembly.GetManifestResourceStream(
            "KhaozEngine.Tests.Fixtures.FrozenWater." + file);
        Assert.NotNull(source);
        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(source)));
    }

    [Fact]
    public void EveryFrozenEvaluatorAndFlatHelperHasASourceGuard()
    {
        string[] expected = ["FrozenTileCapsuleBoxPath.cs", "FrozenTileWaterArithmetic.cs", "FrozenTileWaterBoxIndex.cs",
            "FrozenTileWaterColumnCoverage.cs", "FrozenTileWaterConvexShape.cs", "FrozenTileWaterFlatIndex.cs",
            "FrozenTileWaterPathBounds.cs", "FrozenTileWaterPrismCoverage.cs"];
        string prefix = "KhaozEngine.Tests.Fixtures.FrozenWater.";
        Assert.Equal(expected.Order(StringComparer.Ordinal), typeof(WaterKernelFrozenReferenceTests).Assembly
            .GetManifestResourceNames().Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            .Select(name => name[prefix.Length..]).Order(StringComparer.Ordinal));
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
