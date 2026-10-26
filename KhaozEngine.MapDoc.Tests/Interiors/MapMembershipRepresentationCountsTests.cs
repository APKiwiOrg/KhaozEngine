using System;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Interiors;

public sealed class MapMembershipRepresentationCountsTests
{
    [Theory]
    [InlineData("plain", 2, 8, 2, 0)]
    [InlineData("opening", 3, 12, 4, 2)]
    [InlineData("prism", 4, 16, 4, 0)]
    [InlineData("face-free", 2, 8, 0, 0)]
    public void Preparation_CountsActualCornersAndTriangleFamilies(string shape, long polygons, long corners,
        long triangles, long opening)
    {
        using var native = MapMembershipIntervalFixtures.Create(shape);
        var scene = NativeSceneFixtures.Open(native, false).Acquire(MapMembershipIntervalTests.Scope()).Scene!;
        var result = MapRoofEvaluationBuffer.Prepare(scene, new MapSceneAdmission(new()));
        Assert.NotNull(result.Buffer);
        try
        {
            var counts = result.Counts!;
            Assert.Equal(polygons, Count(counts, "Polygons"));
            Assert.Equal(corners, Count(counts, "PolygonCorners"));
            Assert.Equal(triangles, Count(counts, "Triangles"));
            Assert.Equal(opening, Count(counts, "OpeningTriangles"));
            Assert.Equal(4 * (corners + 4 * triangles), Count(counts, "Coefficients"));
            Assert.Equal(shape is "opening" or "prism" ? 2 : 1, Count(counts, "UniqueCellProducts"));
            Assert.Equal(1, Count(counts, "UniqueFootprintPolygons"));
        }
        finally { result.Buffer.PreparationOwnership!.Dispose(); }
    }

    [Fact]
    public void GeneralRing_CountsFiveCornersWithoutAQuadDefault()
    {
        var counts = new MapRoofPreparationCounts();
        var admission = new MapSceneAdmission(new());
        using var product = admission.BeginPreparation();
        using var build = new MapMembershipBuild(admission, product, counts);
        _ = new MapMembershipPolygon(new MapExactXz[]
        { new(new(0, 1), new(0, 1)), new(new(3, 1), new(0, 1)), new(new(3, 1), new(2, 1)),
            new(new(1, 1), new(1, 1)), new(new(0, 1), new(2, 1)) });
        Assert.Equal(1, Count(counts, "Polygons"));
        Assert.Equal(5, Count(counts, "PolygonCorners"));
        Assert.Equal(20, Count(counts, "Coefficients"));
    }

    // Reflection lets the regression fail on the missing diagnostic contract before production changes.
    static long Count(MapRoofPreparationCounts counts, string name)
    {
        var property = typeof(MapRoofPreparationCounts).GetProperty(name);
        Assert.NotNull(property);
        return Assert.IsType<long>(property.GetValue(counts));
    }
}
