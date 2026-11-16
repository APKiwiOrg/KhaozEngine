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
    [InlineData("FrozenTileWaterArithmetic.cs", "6D55ACA2452FD70E0C76FBC0FB137CA15B174652396EBC14EE565BA613D87D84")]
    [InlineData("FrozenTileWaterBedSeam.cs", "4873FFF8EDEAD6D5CC441883C8236F015835D3D4F3F0A875E77E9AFC25D7EF39")]
    [InlineData("FrozenTileWaterBoxIndex.cs", "14809E5CFABB4284A2F8304446C413945F064349EA8A025FDF36FC65201A631C")]
    [InlineData("FrozenTileWaterColumnCoverage.cs", "5DC3D7470AF2DEE4011E867FEBDE5A2329825B99BDF9F0BC17CE1EF4CAB4F18F")]
    [InlineData("FrozenTileWaterConvexShape.cs", "5DFF6B316578CC8B3DA9BE5870F01E0159C5943F29803C435AAB97DC77B5B756")]
    [InlineData("FrozenTileWaterDrawnGroundRounding.cs", "C9487DDFC966B7B9E85347286C4AB34AE65C22CCBC3F04E9068D684D17F052DD")]
    [InlineData("FrozenTileWaterFlatIndex.cs", "96D58E66A2461ED88DEF0B35EAB0F2453EA4E7C664C6DC4588D44DB56E8F46CC")]
    [InlineData("FrozenTileWaterGeometry.cs", "25AED25A7F03F0455D54D8708C98E0D702C7D31ABA72165656CDA356A4642C31")]
    [InlineData("FrozenTileWaterLinearRank.cs", "C08A8DD42C643E55947C77E50E231882067A64CE415B14104258570474AA270C")]
    [InlineData("FrozenTileWaterPathBounds.cs", "783BED32BD7F70F957771DE71B50495B78493734B3B044432CBEA2C3CE922542")]
    [InlineData("FrozenTileWaterPrism.cs", "3F4D0308515DD7C614DCCC8197E1E3F7EFD8241539AD17502226E2EBC2907D69")]
    [InlineData("FrozenTileWaterPrismCoverage.cs", "9C7A9CAC0602712EE1EFC3A9573E9C534D81DC5F6DDD08450E943E47F49C4B41")]
    [InlineData("FrozenTileWaterPrismFaces.cs", "85DDFFE0118075565B55074E935F8DBC86157D319A18801246919D570A20D1CB")]
    [InlineData("FrozenTileWaterRootUncertainty.cs", "7193FDD99FB0FB677F1C673357DA7005B024CEBB2DC7CE187EFE3EC08F7A1EEC")]
    [InlineData("FrozenTileWaterWitness.cs", "BB62D480E7519277AEB9E07F48785CFF4D58B18A5F9AD146B295A323B964C23B")]
    public void FrozenSourceIsAnExplicitReviewedSnapshot(string file, string expected)
    {
        // #1755. This reference is deliberate. Change its hash only with a reviewed behavior decision.
        using var source = typeof(WaterKernelFrozenReferenceTests).Assembly.GetManifestResourceStream(
            "KhaozEngine.Tests.Fixtures.FrozenWater." + file);
        Assert.NotNull(source);
        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(source)));
    }

    [Fact]
    public void EveryFrozenEvaluatorAndGeometryHelperHasASourceGuard()
    {
        string[] expected = [
            "FrozenTileCapsuleBoxPath.cs", "FrozenTileWaterArithmetic.cs", "FrozenTileWaterBedSeam.cs",
            "FrozenTileWaterBoxIndex.cs", "FrozenTileWaterColumnCoverage.cs", "FrozenTileWaterConvexShape.cs",
            "FrozenTileWaterDrawnGroundRounding.cs", "FrozenTileWaterFlatIndex.cs", "FrozenTileWaterGeometry.cs",
            "FrozenTileWaterLinearRank.cs", "FrozenTileWaterPathBounds.cs", "FrozenTileWaterPrism.cs",
            "FrozenTileWaterPrismCoverage.cs", "FrozenTileWaterPrismFaces.cs", "FrozenTileWaterRootUncertainty.cs",
            "FrozenTileWaterWitness.cs"];
        string prefix = "KhaozEngine.Tests.Fixtures.FrozenWater.";
        Assert.Equal(expected.Order(StringComparer.Ordinal), typeof(WaterKernelFrozenReferenceTests).Assembly
            .GetManifestResourceNames().Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            .Select(name => name[prefix.Length..]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void PrismAndEveryFaceTemplateRetainFrozenOrderedPlanesAndBases()
    {
        var source = WaterKernelTraceFixture.RealFaceTypes();
        var frozen = new FrozenTileWaterGeometry(source, rebuild: true);
        Assert.Equal(source.Prisms.Length, frozen.Prisms.Length);
        Assert.Equal(source.Faces.Length, frozen.Faces.Length);
        for (int i = 0; i < source.Prisms.Length; i++) Compare(frozen.Prisms[i].Shape, source.Prisms[i].Shape);
        for (int i = 0; i < source.Faces.Length; i++)
        {
            var a = frozen.Faces[i];
            var b = source.Faces[i];
            Assert.Equal((a.Prism, a.Plane, a.Wet, a.Dry), (b.Prism, b.Plane, b.Wet, b.Dry));
            Compare(a.Shape, b.Shape);
        }

        static void Compare(FrozenTileWaterConvexShape expected, TileWaterConvexShape actual)
        {
            Assert.Equal(expected.Bases.ToArray().Select(b => (b.A, b.B, b.C)),
                actual.Bases.ToArray().Select(b => (b.A, b.B, b.C)));
            Assert.Equal(expected.Planes.Length, actual.Planes.Length);
            for (int i = 0; i < expected.Planes.Length; i++)
            {
                var a = expected.Planes[i];
                var b = actual.Planes[i];
                Assert.Equal(a.HeightSensitive, b.HeightSensitive);
                double[] left = [a.Normal.X.Low, a.Normal.X.High, a.Normal.Y.Low, a.Normal.Y.High,
                    a.Normal.Z.Low, a.Normal.Z.High, a.Offset.Low, a.Offset.High];
                double[] right = [b.Normal.X.Low, b.Normal.X.High, b.Normal.Y.Low, b.Normal.Y.High,
                    b.Normal.Z.Low, b.Normal.Z.High, b.Offset.Low, b.Offset.High];
                Assert.Equal(left.Select(BitConverter.DoubleToInt64Bits), right.Select(BitConverter.DoubleToInt64Bits));
            }
        }
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
