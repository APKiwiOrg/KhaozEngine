using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

/// <summary>How R2's compiled terrain faces become bounded, one-sided physics mesh chunks.</summary>
public class MapTerrainPhysicsTests
{
    [Fact]
    public void StackedCaveFloorsAndCeilings_PhysicalHalf()
    {
        var set = MapTerrainPhysics.Compile(NativeWorldFixtures.StackedCave().View, new MapTerrainChunkPolicy());
        var roles = set.Chunks.SelectMany(c => c.TriangleRoles).Distinct().ToList();
        Assert.Contains(MapFaceRole.SupportFloor, roles);
        Assert.Contains(MapFaceRole.Ceiling, roles);
        Assert.Contains(MapFaceRole.Wall, roles);
        foreach (var chunk in set.Chunks)
            for (int t = 0; t < chunk.TriangleOwners.Count; t++)
                Assert.True(NativeWorldFixtures.BackendFrontAlongCompiledNormal(chunk, t));
    }

    [Fact]
    public void ChunksRespectTheCap_AndEveryTriangleHasItsFace()
    {
        var f = NativeWorldFixtures.FinePatch();
        var set = MapTerrainPhysics.Compile(f.View, new MapTerrainChunkPolicy(64));
        Assert.All(set.Chunks, c => Assert.InRange(c.TriangleOwners.Count, 1, 64));
        Assert.Equal(f.CompiledFaceCount, set.Chunks.Sum(c => c.TriangleOwners.Count));
        Assert.Equal(f.CompiledFaceCount, set.Chunks.SelectMany(c => c.TriangleOwners).Distinct().Count());
    }

    [Fact]
    public void LegacyFallbackCells_EmitNoTriangles()
    {
        var f = NativeWorldFixtures.LegacyExteriorWithFallback();
        var set = MapTerrainPhysics.Compile(f.View, new MapTerrainChunkPolicy());
        Assert.Equal(f.FallbackCellCount, set.LegacyFallbackCellsSkipped);
        Assert.DoesNotContain(set.Chunks.SelectMany(c => c.TriangleOwners), f.IsFallbackFace);
    }

    [Fact]
    public void DenseCell_RefusesWithPhysicsChunkCapacity()
        => Assert.Contains("physics chunk capacity", Assert.Throws<MapDocumentException>(() =>
            MapTerrainPhysics.Compile(NativeWorldFixtures.DenseCell().View, new MapTerrainChunkPolicy(64))).Message);

    [Fact]
    public void LongStripsAndHighLegacyTerrain_StayWithinTheFeatureQueryExtent()
    {
        foreach (var f in new[] { NativeWorldFixtures.LongWallStrip(lengthMetres: 300f), NativeWorldFixtures.LongWallStrip(lengthMetres: 129f), NativeWorldFixtures.HighLegacyPatch(heightMetres: 180f) })
        {
            var set = MapTerrainPhysics.Compile(f.View, new MapTerrainChunkPolicy());
            Assert.All(set.Chunks, c => Assert.All(c.Shape.Vertices, v => Assert.True(MathF.Abs(v.X) <= 64 && MathF.Abs(v.Y) <= 64 && MathF.Abs(v.Z) <= 64)));
            Assert.Equal(f.CompiledFaceCount, set.Chunks.Sum(c => c.TriangleOwners.Count));
        }
    }

    [Fact]
    public void TwoSidedStrip_ChunksEachSideSeparately()
    {
        var set = MapTerrainPhysics.Compile(NativeWorldFixtures.TwoSidedStrip().View, new MapTerrainChunkPolicy());
        Assert.All(set.Chunks, c => Assert.Single(c.TriangleOwners.Select(k => k.Side).Distinct()));
        Assert.Contains(set.Chunks, c => c.ChunkId.EndsWith("/Front", StringComparison.Ordinal));
        Assert.Contains(set.Chunks, c => c.ChunkId.EndsWith("/Back", StringComparison.Ordinal));
    }

    [Fact]
    public void IncompleteViewAndUnresolvedChain_Refuse()
    {
        Assert.Contains("complete", Assert.Throws<MapDocumentException>(() =>
            MapTerrainPhysics.Compile(NativeWorldFixtures.IncompleteView(), new MapTerrainChunkPolicy())).Message);
        Assert.Contains("unresolved chain", Assert.Throws<MapDocumentException>(() =>
            MapTerrainPhysics.Compile(NativeWorldFixtures.UnresolvedStripView(), new MapTerrainChunkPolicy())).Message);
    }
}
