using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Tests.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Interiors;

public sealed class MapMembershipGeometryArenaTests
{
    [Theory]
    [InlineData("PolygonRow", 80)]
    [InlineData("TriangleRow", 160)]
    [InlineData("FaceRow", 32)]
    [InlineData("CellRow", 64)]
    public void FrozenColumns_HavePinnedLayouts(string name, int bytes)
    {
        var type = typeof(MapMembershipPolygon).Assembly.GetType("KhaozEngine.MapDoc.Spaces.MapMembershipGeometryArena+" + name);
        Assert.NotNull(type);
        Assert.Equal(bytes, Marshal.SizeOf(type));
    }

    [Fact]
    public void PolygonTriangleAndCellAdapters_AreHandlesWithoutPerProductArrays()
    {
        Assert.True(typeof(MapMembershipPolygon).IsValueType);
        Assert.True(typeof(MapMembershipTriangle).IsValueType);
        Assert.True(typeof(MapMembershipPreparedGeometry.Cell).IsValueType);
        Assert.Equal(16, Unsafe.SizeOf<MapMembershipPolygon>());
        Assert.Equal(16, Unsafe.SizeOf<MapMembershipTriangle>());
        Assert.Equal(16, Unsafe.SizeOf<MapMembershipPreparedGeometry.Cell>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrderedRings_PreserveReleasedCornerAndEdgeOwnership(bool clockwise)
    {
        MapExactXz[] vertices = [P(0, 0), P(2, 0), P(2, 2), P(0, 2)];
        if (clockwise) Array.Reverse(vertices);
        var polygon = new MapMembershipPolygon(vertices);
        var exact = new MapMembershipExact();
        Assert.True(polygon.Contains(P(1, 1), exact));
        Assert.False(polygon.Contains(P(3, 1), exact));
        Assert.Equal(!clockwise, polygon.Contains(P(0, 0), exact));
        Assert.Equal(clockwise, polygon.Contains(P(2, 0), exact));
        Assert.Equal(clockwise, polygon.Contains(P(2, 2), exact));
        Assert.Equal(clockwise, polygon.Contains(P(0, 2), exact));
        Assert.Equal(!clockwise, polygon.Contains(P(1, 0), exact));
        Assert.Equal(clockwise, polygon.Contains(P(2, 1), exact));
        Assert.Equal(clockwise, polygon.Contains(P(1, 2), exact));
        Assert.Equal(!clockwise, polygon.Contains(P(0, 1), exact));
    }

    [Fact]
    public void ConcaveAndCollinearCorners_KeepTheirOriginalWindingAndOwnership()
    {
        var polygon = new MapMembershipPolygon(new[] { P(0, 0), P(1, 0), P(3, 0), P(3, 1), P(1, 1), P(1, 3), P(0, 3) });
        var exact = new MapMembershipExact();
        Assert.True(polygon.Contains(P(1, 0), exact));
        Assert.True(polygon.Contains(new(new(1, 2), new(2, 1)), exact));
        Assert.True(polygon.Contains(new(new(2, 1), new(1, 2)), exact));
        Assert.False(polygon.Contains(P(1, 1), exact));
        Assert.False(polygon.Contains(P(2, 2), exact));
        Assert.True(polygon.Contains(P(0, 2), exact));
    }

    [Fact]
    public void CellCentrePrecision_IsStillValidatedWhenNoBoundNeedsTheCentre()
    {
        var (surface, patch) = CompilerFixtures.OneCell(0, 0, 0, 0, CompilerFixtures.Full);
        surface = surface with { Frame = new(new(int.MaxValue, 1), new(1, 100), MapRowDirection.PositiveZ, MapHeightDatum.WorldY0) };
        patch.Key = new("one", 33554431, 0);
        patch.SetPresent(0, 0, false);
        var set = new MapSurfaceSet(); set.Refs.Add(surface); set.Patches.Add(patch.Key, patch);
        var view = MapScopedSurfaces.CompleteView(set);
        var compiled = MapSurfaceCompiler.Compile(surface, patch);
        using var building = MapMembershipArenaBuilder.Begin();
        // Every corner fits Int64. The released constructor's sum of the first three X values does not.
        Assert.Throws<MapExactOverflowException>(() => new MapMembershipPreparedGeometry.Cell(0, 0, 0,
            view, surface, patch, compiled, compiled, [], []));
    }

    [Fact]
    public void Index_FirstMatchRankWinsWhenTraversalFindsAnotherCellFirst()
    {
        using var building = MapMembershipArenaBuilder.Begin();
        var arena = building.Arena;
        int a = new MapMembershipPolygon(new[] { P(0, 0), P(2, 0), P(2, 2), P(0, 2) }, cell: true).Index;
        int b = new MapMembershipPolygon(new[] { P(-1, 0), P(1, 0), P(1, 2), P(-1, 2) }, cell: true).Index;
        arena.Cells.Append(new(0, 0, 0, 0, a, 0, 0, 0, -1, -1, -1, -1, 0, 0, 0, 0));
        arena.Cells.Append(new(0, 0, 1, 0, b, 0, 0, 0, -1, -1, -1, -1, 1, 0, 1, 0));
        var index = new MapMembershipCellIndex(arena, [0, 1]);
        var selected = index.Find(new(new(0, 1), new(1, 2)), new MapMembershipExact());
        Assert.NotNull(selected);
        Assert.Equal(0, selected.Value.Id);
        Assert.Equal(80, Unsafe.SizeOf<MapMembershipCellIndex.Node>());
    }

    [Fact]
    public void Preparation_ReportsPhysicalColumnCapacitiesIncludingUnusedRowsAndHeaders()
    {
        using var native = MapMembershipIntervalFixtures.Create("plain");
        var scene = NativeSceneFixtures.Open(native, false).Acquire(MapMembershipIntervalTests.Scope()).Scene!;
        var result = MapRoofEvaluationBuffer.Prepare(scene, new MapSceneAdmission(new()));
        Assert.NotNull(result.Buffer);
        try
        {
            // Arena 112, eleven column owners of 48, coefficient store 32 and its two owners of 48.
            // Private cells own twelve pages. The separately leased footprint owns four pages.
            // Capacities are 64 polygon/cell rows, 128 vertex/corner/face rows and 32 triangle/side rows.
            Assert.Equal(32, Unsafe.SizeOf<MapPatchKey?>());
            Assert.Equal(16, Count("GeometryArenaPages"));
            Assert.Equal(30240 + 14432, Count("GeometryArenaCapacityBytes"));
            Assert.Equal(2, Count("FaceKeyRows"));
        }
        finally { result.Buffer.PreparationOwnership!.Dispose(); }
        long Count(string name)
        {
            var property = typeof(MapRoofPreparationCounts).GetProperty(name);
            Assert.NotNull(property);
            return Assert.IsType<long>(property.GetValue(result.Counts));
        }
    }

    static MapExactXz P(long x, long z) => new(new(x, 1), new(z, 1));
}
