using System;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class LatticeAddressTests
{
    [Fact]
    public void ExactValue_NormalizesAndConvertsFloatsExactlyOrRefuses()
    {
        Assert.Equal(new MapExactValue(1, 2), new MapExactValue(-2, -4));
        Assert.Equal(new MapExactValue(0, 1), default(MapExactValue));
        Assert.Equal(("1/2", "-3"), (new MapExactValue(2, 4).ToString(), new MapExactValue(-6, 2).ToString()));
        Assert.Equal(new MapExactValue(13421773, 134217728), MapExactValue.FromSingle(0.1f));
        Assert.Throws<MapExactOverflowException>(() => MapExactValue.FromSingle(1e-30f));
        Assert.Contains("finite", Assert.Throws<ArgumentException>(() => MapExactValue.FromSingle(float.NaN)).Message);
        Assert.Equal((2L, 3L), (new MapExactValue(5, 2).Floor(), new MapExactValue(5, 2).Ceiling()));
        Assert.Equal((-3L, -2L), (new MapExactValue(-5, 2).Floor(), new MapExactValue(-5, 2).Ceiling()));
    }
    [Fact]
    public void Address_ReducesAndOneWorldVertexMatchesAcrossUnits()
    {
        Assert.Equal(MapLatticeAddress.Create(1, 2, 2), MapLatticeAddress.Create(2, 4, 4));
        Assert.Equal(MapLatticeAddress.Corner(1, 0), MapLatticeAddress.Create(3, 0, 3));
        var coarse = new MapLatticeFrame(new(1, 1), new(1, 100), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0);
        var half = new MapLatticeFrame(new(1, 2), new(1, 200), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0);
        var third = new MapLatticeFrame(new(1, 3), new(1, 300), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0);
        var sixtyFourth = new MapLatticeFrame(new(1, 64), new(1, 6400), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0);
        Assert.Equal(coarse.WorldXz(MapLatticeAddress.Create(4, 3, 3)), third.WorldXz(MapLatticeAddress.Corner(4, 3)));
        Assert.Equal(coarse.WorldXz(MapLatticeAddress.Create(127, 64, 64)), sixtyFourth.WorldXz(MapLatticeAddress.Corner(127, 64)));
        Assert.Equal(half.WorldXz(MapLatticeAddress.Corner(2, 0)), third.WorldXz(MapLatticeAddress.Corner(3, 0)));       // 1 m on both
        Assert.Equal(new MapExactValue(1, 2), third.WorldXz(MapLatticeAddress.Create(3, 0, 2)).X);                       // 1.5 third-cells
        Assert.Equal(MapLatticeAddress.Create(4, 3, 3), coarse.AddressOf(new(4, 3), new(1, 1)));
        Assert.Equal(new MapExactXz(new(4, 3), new(-1, 1)), MapLatticeFrame.ImportedMetreCentimetre.WorldXz(MapLatticeAddress.Create(4, 3, 3)));
        Assert.Contains("denominator", Assert.Throws<MapDocumentException>(() => coarse.AddressOf(new(1, 128), new(0, 1))).Message);
        Assert.Throws<MapDocumentException>(() => MapLatticeAddress.Create(1, 0, 65));
    }
    [Fact]
    public void Subdivision_LegalOnlyOnThePatchBoundaryOrAPresenceRim()
    {
        MapSurfacePatch p = SurfacePatchFixtures.Row(3);
        p.EdgeSubdivisions.Add(new(0, 0, MapCellEdge.West, 3));
        p.EdgeSubdivisions.Add(new(2, 0, MapCellEdge.East, 64));
        Assert.Empty(p.ValidateLocal());
        p.EdgeSubdivisions.Add(new(0, 0, MapCellEdge.East, 2));
        Assert.Contains(p.ValidateLocal(), f => f.Contains("interior"));
        p.SetPresent(1, 0, false);
        Assert.Empty(p.ValidateLocal());                                 // cell 1 is now a rim, cell 0 East is legal
        p.EdgeSubdivisions.Add(new(0, 0, MapCellEdge.East, 2));
        Assert.Contains(p.ValidateLocal(), f => f.Contains("duplicate"));
        p.EdgeSubdivisions[^1] = new(0, 0, MapCellEdge.South, 65);
        Assert.Contains(p.ValidateLocal(), f => f.Contains("segments"));
    }

    [Fact]
    public void ExactValue_RoundsBinary32MidpointsWithoutDoubleRounding()
    {
        const long denominator = 1L << 60;
        long midpoint = denominator + (1L << 36);
        Assert.Equal(0x3f800000, BitConverter.SingleToInt32Bits(new MapExactValue(midpoint, denominator).ToSingle()));
        Assert.Equal(0x3f800001, BitConverter.SingleToInt32Bits(new MapExactValue(midpoint + 1, denominator).ToSingle()));
        Assert.Equal(0x3f800000, BitConverter.SingleToInt32Bits(new MapExactValue(midpoint - 1, denominator).ToSingle()));
        Assert.Equal(0x3f800002, BitConverter.SingleToInt32Bits(new MapExactValue(16777219, 16777216).ToSingle()));
        Assert.Equal(unchecked((int)0xbf800001), BitConverter.SingleToInt32Bits(new MapExactValue(-midpoint - 1, denominator).ToSingle()));
        Assert.Equal(new MapExactValue(long.MinValue, 1), MapExactValue.FromSingle(-9223372036854775808f));
        Assert.Throws<MapExactOverflowException>(() => MapExactValue.FromSingle(9223372036854775808f));
    }

    [Fact]
    public void ExactValue_ArithmeticReducesBeforeRangeChecksAndRejectsInvalidAddresses()
    {
        Assert.Equal(new MapExactValue(1, 1), new MapExactValue(long.MinValue, long.MinValue));
        Assert.Equal(default, new MapExactValue(0, long.MinValue));
        var large = new MapExactValue(long.MaxValue, long.MaxValue - 1);
        Assert.Equal(default, large.Add(large.Negate()));
        Assert.Equal(new MapExactValue(1, 6), new MapExactValue(1, 2).Subtract(new(1, 3)));
        Assert.Equal(new MapExactValue(2, 3), new MapExactValue(1, 2).Divide(new(3, 4)));
        Assert.Equal(new MapExactValue(1, 1), new MapExactValue(long.MinValue, 1).Divide(new(long.MinValue, 1)));
        Assert.Throws<MapExactOverflowException>(() => new MapExactValue(long.MinValue, 1).Negate());
        Assert.Throws<ArgumentException>(() => new MapExactValue(1, 0));
        Assert.Throws<ArgumentException>(() => large.Divide(default));
        Assert.Throws<MapDocumentException>(() => MapLatticeFrame.ImportedMetreCentimetre.WorldXz(default));
        Assert.Throws<MapDocumentException>(() => new MapLatticeFrame(default, new(1, 100),
            MapRowDirection.PositiveZ, MapHeightDatum.WorldY0).WorldXz(MapLatticeAddress.Corner(0, 0)));
        Assert.Equal(MapLatticeAddress.Corner(long.MinValue, 0), MapLatticeAddress.Create(long.MinValue, 0, 1));
    }
}
