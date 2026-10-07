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
}
