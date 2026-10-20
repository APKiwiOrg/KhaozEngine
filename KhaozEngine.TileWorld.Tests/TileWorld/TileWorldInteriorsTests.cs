using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public class TileWorldInteriorsTests
{
    static readonly InteriorWindow Window = new(new Vector2(8f, -16f), 0.25f, 32);

    [Fact]
    public void OnePlaneBuildingCollectsBoundsAndRoofs()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        Indoors(doc, new TileRect(10, 10, 2, 2), 0);
        doc.SetCornerHeightCm(11, 11, 0, -100);
        doc.FillDerivedHeights(doc.GetRegion(new RegionCoord(0, 0))!, 1);
        doc.SetCornerHeightCm(11, 11, 1, 250);
        TileWorldCatalogs catalogs = Catalogs();
        doc.AddObject("roof_flat", 10, 10, 1, 1);
        doc.AddObject("roof_flat", 14, 14, 1, 0);
        var source = new TileWorldInteriors(doc, catalogs, new Meshes());

        InteriorSourceResult result = source.Collect(Window);

        InteriorVolume volume = Assert.Single(result.Volumes);
        Assert.Equal(4, volume.Outline.Count);
        Assert.Contains(new Vector2(10f, -10f), volume.Outline);
        Assert.Contains(new Vector2(12f, -10f), volume.Outline);
        Assert.Contains(new Vector2(12f, -12f), volume.Outline);
        Assert.Contains(new Vector2(10f, -12f), volume.Outline);
        Assert.Equal(-1f, volume.FloorY);
        Assert.Equal(2.5f, volume.CeilingY);
        InteriorRoof roof = Assert.Single(volume.Roofs);
        Assert.NotEmpty(roof.Samples.Points);
        Vector3 placed = Vector3.Transform(Vector3.UnitX, roof.LocalToWorld);
        Assert.Equal(11f, placed.X, 5);
        Assert.Equal(2.5f, placed.Y, 5);
        Assert.Equal(-10f, placed.Z, 5);
        Assert.Empty(result.Issues);
        Assert.Empty(source.LastIssues);
    }

    [Fact]
    public void TwoStoreysChooseOnlyTheRoofAbove()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        Indoors(doc, new TileRect(10, 10, 2, 2), 0);
        Indoors(doc, new TileRect(10, 10, 2, 2), 1);
        doc.AddObject("roof_flat", 10, 10, 1, 0);
        doc.AddObject("roof_flat", 10, 10, 2, 0);
        doc.AddObject("roof_flat", 10, 10, 3, 0);

        InteriorSourceResult result = new TileWorldInteriors(doc, Catalogs(), new Meshes()).Collect(Window);

        InteriorVolume volume = Assert.Single(result.Volumes);
        Assert.Equal(0f, volume.FloorY);
        Assert.Equal(6f, volume.CeilingY);
        Assert.Equal(6f, Assert.Single(volume.Roofs).LocalToWorld.Translation.Y);
        InteriorHeightField field = InteriorHeightFieldBuilder.Build(result.Volumes, Window, result.Encoding);
        Assert.True(field.Contains(new Vector3(10.5f, 5f, -10.5f)));
        Assert.False(field.Contains(new Vector3(10.5f, 8f, -10.5f)));
    }

    [Fact]
    public void HighestIndoorPlaneDerivesItsCeilingWithoutARoofPlane()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld(2);
        Indoors(doc, new TileRect(10, 10, 2, 2), 1);
        doc.PlaneHeight = 4f;
        doc.SetCornerHeightCm(11, 11, 1, 225);
        doc.AddObject("roof_flat", 10, 10, 1, 0);

        InteriorSourceResult result = new TileWorldInteriors(doc, Catalogs(), new Meshes()).Collect(Window);

        InteriorVolume volume = Assert.Single(result.Volumes);
        Assert.Equal(2.25f, volume.FloorY);
        Assert.Equal(6.25f, volume.CeilingY);
        Assert.Empty(volume.Roofs);
        Assert.Empty(result.Issues);
        InteriorHeightField field = InteriorHeightFieldBuilder.Build(result.Volumes, Window, result.Encoding);
        Assert.True(field.Contains(new Vector3(10.5f, 6f, -10.5f)));
        Assert.False(field.Contains(new Vector3(10.5f, 6.25f, -10.5f)));
    }

    [Fact]
    public void RowScannedRectanglesPreserveCourtyardsAndPlanePairs()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        Indoors(doc, new TileRect(10, 10, 3, 3), 0);
        doc.SetSettings(11, 11, 0, TileSettings.None);
        doc.SetSettings(12, 12, 1, TileSettings.Indoors);

        InteriorSourceResult result = new TileWorldInteriors(doc, Catalogs(), new Meshes()).Collect(Window);
        InteriorHeightField field = InteriorHeightFieldBuilder.Build(result.Volumes, Window, result.Encoding);

        Assert.Equal(5, result.Volumes.Count);
        Assert.False(field.Contains(new Vector3(11.5f, 1f, -11.5f)));
        Assert.True(field.Contains(new Vector3(10.5f, 1f, -11.5f)));
        Assert.True(field.Contains(new Vector3(12.5f, 5f, -12.5f)));
        Assert.False(field.Contains(new Vector3(10.5f, 5f, -12.5f)));
    }

    [Theory]
    [InlineData(0, 11, 10)]
    [InlineData(1, 10, 9)]
    [InlineData(2, 9, 10)]
    [InlineData(3, 10, 11)]
    public void OuterWallIsAcceptedAndInnerWallIsReported(int rotation, int innerX, int innerZ)
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetSettings(10, 10, 0, TileSettings.Indoors);
        doc.SetSettings(innerX, innerZ, 0, TileSettings.Indoors);
        TileObject wall = doc.AddObject("wall", 10, 10, 0, rotation);
        var source = new TileWorldInteriors(doc, Catalogs(), new Meshes());
        InteriorSourceResult outer = source.Collect(Window);
        Assert.Empty(source.LastIssues);
        Assert.True(InteriorHeightFieldBuilder.Build(outer.Volumes, Window, outer.Encoding)
            .Contains(new Vector3(10.5f, 1f, -10.5f)));

        wall.Rotation = (rotation + 2) & 3;
        InteriorSourceResult inner = source.Collect(Window);

        TileWorldIssue issue = Assert.Single(source.LastIssues, i => i.Code == "interior.wall-inner-edge");
        Assert.Contains($"#{wall.Id}", issue.Message);
        Assert.Contains("plane 0", issue.Message);
        Assert.Equal(new TileCoord(10, 10, 0), issue.Tile);
        Assert.Equal(new RegionCoord(0, 0), issue.Region);
        Assert.Contains(inner.Issues, text => text.Contains(issue.Code, StringComparison.Ordinal));
        InteriorHeightField field = InteriorHeightFieldBuilder.Build(inner.Volumes, Window, inner.Encoding);
        Assert.False(field.Contains(new Vector3(10.5f, 1f, -10.5f)));
        Assert.True(field.Contains(new Vector3(innerX + 0.5f, 1f, -innerZ - 0.5f)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void WallCornerOwnsBothEdges(int rotation)
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetSettings(10, 10, 0, TileSettings.Indoors);
        doc.AddObject("wall_corner", 10, 10, 0, rotation);
        doc.AddObject("wall_corner", 10, 10, 0, (rotation + 2) & 3);
        var source = new TileWorldInteriors(doc, Catalogs(), new Meshes());

        InteriorSourceResult result = source.Collect(Window);

        Assert.Empty(source.LastIssues);
        Assert.Single(result.Volumes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OppositeEdgeCanBeOwnedOnThisTileOrTheNeighbour(bool neighbour)
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        Indoors(doc, new TileRect(10, 9, 1, 3), 0);
        doc.AddObject("wall", 10, 10, 0, 0);
        TileObject owner = doc.AddObject("wall_corner", neighbour ? 11 : 10, 10, 0, neighbour ? 3 : 1);
        var source = new TileWorldInteriors(doc, Catalogs(), new Meshes());
        Assert.Empty(source.Collect(Window).Issues);

        doc.RemoveObject(owner.Id);
        InteriorSourceResult result = source.Collect(Window);

        Assert.Single(source.LastIssues, i => i.Code == "interior.wall-inner-edge");
        Assert.False(InteriorHeightFieldBuilder.Build(result.Volumes, Window, result.Encoding)
            .Contains(new Vector3(10.5f, 1f, -10.5f)));
    }

    [Fact]
    public void UnsafeWallExcludesOnlyItsPlane()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetSettings(10, 10, 0, TileSettings.Indoors);
        doc.SetSettings(10, 10, 1, TileSettings.Indoors);
        doc.AddObject("wall", 10, 10, 0, 0);
        doc.AddObject("roof_flat", 10, 10, 2, 0);

        InteriorSourceResult result = new TileWorldInteriors(doc, Catalogs(), new Meshes()).Collect(Window);

        InteriorVolume volume = Assert.Single(result.Volumes);
        Assert.Equal(3f, volume.FloorY);
        Assert.Equal(6f, volume.CeilingY);
        Assert.Single(volume.Roofs);
    }

    [Theory]
    [InlineData(1, 3f)]
    [InlineData(2, 6f)]
    public void UnsafeStoreyStopsTheColumnBeforeThatStorey(int unsafePlane, float ceiling)
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        for (int plane = 0; plane < 3; plane++) doc.SetSettings(10, 10, plane, TileSettings.Indoors);
        TileObject wall = doc.AddObject("wall", 10, 10, unsafePlane, 0);
        doc.AddObject("roof_flat", 10, 10, 1, 0);
        doc.AddObject("roof_flat", 10, 10, 2, 0);
        doc.AddObject("roof_flat", 10, 10, 3, 0);
        var source = new TileWorldInteriors(doc, Catalogs(), new Meshes());

        InteriorSourceResult result = source.Collect(Window);

        TileWorldIssue issue = Assert.Single(source.LastIssues);
        Assert.Equal("interior.wall-inner-edge", issue.Code);
        Assert.Equal(wall.Coord, issue.Tile);
        InteriorVolume volume = Assert.Single(result.Volumes);
        Assert.Equal(0f, volume.FloorY);
        Assert.Equal(ceiling, volume.CeilingY);
        Assert.Empty(volume.Roofs);
        InteriorHeightField field = InteriorHeightFieldBuilder.Build(result.Volumes, Window, result.Encoding);
        Assert.True(field.Contains(new Vector3(10.5f, ceiling - 0.5f, -10.5f)));
        Assert.False(field.Contains(new Vector3(10.5f, ceiling, -10.5f)));
        Assert.False(field.Contains(new Vector3(10.5f, unsafePlane * 3f + 1f, -10.5f)));
        Assert.False(field.Contains(new Vector3(10.5f, 7f, -10.5f)));
    }

    [Fact]
    public void MixedRoofEligibilityKeepsTheEligibleColumnRoof()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetSettings(10, 10, 0, TileSettings.Indoors);
        for (int plane = 0; plane < 3; plane++) doc.SetSettings(11, 10, plane, TileSettings.Indoors);
        doc.AddObject("wall", 11, 10, 1, 0);
        doc.AddObject("roof_flat", 10, 10, 1, 0);
        doc.AddObject("roof_flat", 10, 10, 3, 0);

        InteriorSourceResult result = new TileWorldInteriors(doc, Catalogs(), new Meshes()).Collect(Window);

        Assert.Equal(2, result.Volumes.Count);
        InteriorVolume eligible = Assert.Single(result.Volumes, v => v.Outline.Min(p => p.X) == 10f);
        InteriorVolume truncated = Assert.Single(result.Volumes, v => v.Outline.Min(p => p.X) == 11f);
        Assert.Equal(0f, eligible.FloorY);
        Assert.Equal(0f, truncated.FloorY);
        Assert.Equal(3f, eligible.CeilingY);
        Assert.Equal(3f, truncated.CeilingY);
        Assert.Single(eligible.Roofs);
        Assert.Empty(truncated.Roofs);
        InteriorHeightField field = InteriorHeightFieldBuilder.Build(result.Volumes, Window, result.Encoding);
        Assert.True(field.TryGetSpan(10, 22, out _, out float eligibleTop));
        Assert.Equal(4f, eligibleTop);
        Assert.True(field.TryGetSpan(14, 22, out _, out float truncatedTop));
        Assert.Equal(3f, truncatedTop);
        Assert.True(field.Contains(new Vector3(10.5f, 3.5f, -10.5f)));
        Assert.False(field.Contains(new Vector3(11.5f, 3.5f, -10.5f)));
    }

    [Fact]
    public void DisjointIndoorStoreysKeepOnlyTheLowestContiguousRun()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        doc.SetSettings(10, 10, 0, TileSettings.Indoors);
        doc.SetSettings(10, 10, 2, TileSettings.Indoors);
        doc.AddObject("roof_flat", 10, 10, 1, 0);
        doc.AddObject("roof_flat", 10, 10, 3, 0);

        InteriorSourceResult result = new TileWorldInteriors(doc, Catalogs(), new Meshes()).Collect(Window);

        InteriorVolume volume = Assert.Single(result.Volumes);
        Assert.Equal(0f, volume.FloorY);
        Assert.Equal(3f, volume.CeilingY);
        Assert.Empty(volume.Roofs);
    }

    [Fact]
    public void DiagonalBoundaryIsExcluded()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        Indoors(doc, new TileRect(10, 10, 3, 3), 0);
        TileObject boundary = doc.AddObject("diag_wall", 10, 11, 0, 0);
        doc.AddObject("diag_wall", 11, 11, 0, 1);
        var source = new TileWorldInteriors(doc, Catalogs(), new Meshes());

        InteriorSourceResult result = source.Collect(Window);

        TileWorldIssue issue = Assert.Single(source.LastIssues);
        Assert.Equal("interior.diagonal-wall", issue.Code);
        Assert.Contains($"#{boundary.Id}", issue.Message);
        Assert.Equal(new TileCoord(10, 11, 0), issue.Tile);
        InteriorHeightField field = InteriorHeightFieldBuilder.Build(result.Volumes, Window, result.Encoding);
        Assert.False(field.Contains(new Vector3(10.5f, 1f, -11.5f)));
        Assert.True(field.Contains(new Vector3(11.5f, 1f, -11.5f)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("missing")]
    [InlineData("bad")]
    public void UnreadableRoofFallsBackOnce(string? meshRef)
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        Indoors(doc, new TileRect(10, 10, 2, 2), 0);
        TileWorldCatalogs catalogs = Catalogs();
        catalogs.Archetype("roof_flat")!.MeshRef = meshRef!;
        TileObject roof = doc.AddObject("roof_flat", 10, 10, 1, 0);
        doc.AddObject("roof_flat", 11, 10, 1, 0);
        var source = new TileWorldInteriors(doc, catalogs, new Meshes());

        InteriorSourceResult result = source.Collect(Window);

        TileWorldIssue issue = Assert.Single(source.LastIssues, i => i.Code == "interior.roof-mesh");
        Assert.Contains("roof_flat", issue.Message);
        Assert.Contains($"#{roof.Id}", issue.Message);
        Assert.Equal(new TileCoord(10, 10, 1), issue.Tile);
        Assert.Single(result.Issues);
        Assert.Empty(Assert.Single(result.Volumes).Roofs);
        InteriorHeightField field = InteriorHeightFieldBuilder.Build(result.Volumes, Window, result.Encoding);
        Assert.True(field.TryGetSpan(10, 22, out float floor, out float top));
        Assert.Equal(0f, floor);
        Assert.Equal(3f, top);
    }

    [Theory]
    [InlineData(1f, 0.25f)]
    [InlineData(1.2f, 0.24f)]
    [InlineData(1.5f, 0.25f)]
    public void CellSizeDividesTile(float tileSize, float expected)
    {
        float cellSize = TileWorldInteriors.CellSizeFor(tileSize);
        Assert.Equal(expected, cellSize);
        Assert.Equal(tileSize, cellSize * MathF.Round(tileSize / cellSize), 5);
    }

    [Fact]
    public void EncodingCoversTheWholeWorld()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        Indoors(doc, new TileRect(10, 10, 2, 2), 0);
        Indoors(doc, new TileRect(30, 30, 2, 2), 0);
        for (int z = 30; z <= 32; z++)
            for (int x = 30; x <= 32; x++) doc.SetCornerHeightCm(x, z, 0, short.MinValue);
        for (int z = 10; z <= 12; z++)
            for (int x = 10; x <= 12; x++) doc.SetCornerHeightCm(x, z, 1, short.MaxValue);
        doc.AddObject("roof_flat", 10, 10, 1, 0);
        var source = new TileWorldInteriors(doc, Catalogs(), new Meshes());

        InteriorSourceResult local = source.Collect(Window);
        InteriorSourceResult distant = source.Collect(new InteriorWindow(new Vector2(28f, -36f), 0.25f, 32));

        Assert.Single(local.Volumes);
        Assert.Equal(-327.68f, local.Encoding.BaseY);
        Assert.True(local.Encoding.StepY > 0.01f);
        Assert.Equal(local.Encoding, distant.Encoding);
        Assert.Single(source.LastIssues, i => i.Code == "interior.step");
        Assert.Contains(distant.Issues, issue => issue.Contains("interior.step", StringComparison.Ordinal));
        Assert.Equal(0, InteriorHeightFieldBuilder.Build(local.Volumes, Window, local.Encoding).OutOfRangeCells);
    }

    [Fact]
    public void RoofEavesOutsideTheOutlineAreNeverCut()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        Indoors(doc, new TileRect(10, 10, 2, 2), 0);
        doc.AddObject("roof_flat", 10, 10, 1, 0);
        var meshes = new Meshes { Mesh = Roof(1.5f) };

        InteriorSourceResult result = new TileWorldInteriors(doc, Catalogs(), meshes).Collect(Window);
        InteriorHeightField field = InteriorHeightFieldBuilder.Build(result.Volumes, Window, result.Encoding);

        Assert.True(field.Contains(new Vector3(10.125f, 3.5f, -10.125f)));
        Assert.False(field.Contains(new Vector3(9.875f, 3.5f, -10.125f)));
        Assert.False(field.Contains(new Vector3(12.125f, 3.5f, -10.125f)));
    }

    [Fact]
    public void SampleCacheTracksMeshIdentity()
    {
        var meshes = new Meshes();
        var cache = new TileInteriorRoofSamples();
        ClearanceSamples first = cache.Get(meshes, "roof", 0.25f, out string? error)!;
        Assert.Null(error);
        Assert.Same(first, cache.Get(meshes, "roof", 0.25f, out error));
        Assert.Null(error);
        Assert.Equal(2, meshes.Loads);

        meshes.Mesh = Roof(1f, 2f);
        ClearanceSamples replacement = cache.Get(meshes, "roof", 0.25f, out error)!;
        Assert.Null(error);
        Assert.NotSame(first, replacement);
        Assert.All(replacement.Points, point => Assert.Equal(2f, point.Y));
        Assert.NotSame(replacement, cache.Get(meshes, "roof", 0.125f, out error));
        Assert.Null(error);
    }

    [Fact]
    public void SharedCacheRefreshesRoofsAcrossDetachedBuilds()
    {
        TileWorldDocument doc = TileWorldTestData.FlatWorld();
        Indoors(doc, new TileRect(10, 10, 2, 2), 0);
        doc.AddObject("roof_flat", 10, 10, 1, 0);
        var meshes = new Meshes();
        var cache = new TileInteriorRoofSamples();
        var source = new TileWorldInteriors(doc, Catalogs(), meshes, cache);
        InteriorSourceResult first = source.Collect(Window);
        InteriorSourceResult second = new TileWorldInteriors(doc, Catalogs(), meshes, cache).Collect(Window);
        Assert.Same(first.Volumes[0].Roofs[0].Samples, second.Volumes[0].Roofs[0].Samples);

        meshes.Mesh = Roof(1f, 700f);
        InteriorSourceResult changed = source.Collect(Window);

        Assert.NotSame(first.Volumes[0].Roofs[0].Samples, changed.Volumes[0].Roofs[0].Samples);
        Assert.True(changed.Encoding.StepY > 0.01f);
    }

    [Fact]
    public void EmptyWorldCollectsNoVolumesOrIssues()
    {
        var source = new TileWorldInteriors(TileWorldTestData.FlatWorld(), Catalogs(), new Meshes());
        InteriorSourceResult result = source.Collect(Window);
        Assert.Empty(result.Volumes);
        Assert.Empty(result.Issues);
        Assert.Equal(new InteriorEncoding(0f, 0.01f), result.Encoding);
    }

    static TileWorldCatalogs Catalogs()
    {
        TileWorldCatalogs catalogs = TileWorldCatalogs.Greybox();
        TileObjectArchetype roof = catalogs.Archetype("roof_flat")!;
        roof.SizeX = roof.SizeZ = 2;
        roof.MeshRef = "roof";
        return catalogs;
    }

    static void Indoors(TileWorldDocument doc, TileRect rect, int plane)
    {
        for (int z = rect.Z; z < rect.Z1; z++)
            for (int x = rect.X; x < rect.X1; x++) doc.SetSettings(x, z, plane, TileSettings.Indoors);
    }

    static ClearanceMesh Roof(float halfSize = 1f, float height = 1f) => new(
        new[] { new Vector3(-halfSize, height, -halfSize), new Vector3(halfSize, height, -halfSize),
            new Vector3(halfSize, height, halfSize), new Vector3(-halfSize, height, halfSize) },
        new[] { 0, 1, 2, 0, 2, 3 });

    sealed class Meshes : ITileMeshGeometry
    {
        public ClearanceMesh Mesh { get; set; } = Roof();
        public int Loads { get; private set; }

        public bool TryLoad(string meshRef, [NotNullWhen(true)] out ClearanceMesh? mesh,
            [NotNullWhen(false)] out string? error)
        {
            Loads++;
            if (meshRef == "bad") throw new ArgumentException("bad roof geometry");
            mesh = meshRef == "roof" ? Mesh : null;
            error = mesh is null ? "mesh missing" : null;
            return mesh is not null;
        }
    }
}
