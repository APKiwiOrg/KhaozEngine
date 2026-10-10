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
    public void PatchFaceThatFitsNoAnchor_KeepsAChunkOfItsOwnUnderTheSplitGrammar()
    {
        var f = NativeWorldFixtures.TallCornerCell();
        var set = MapTerrainPhysics.Compile(f.View, new MapTerrainChunkPolicy());
        AssertEveryFaceOnceWithUniqueIds(f, set);
        Assert.Equal(new[] { "tall-corner/0,0/0,0,1/0.0", "tall-corner/0,0/0,0,1/1.0" }, set.Chunks.Select(c => c.ChunkId));
        Assert.All(set.Chunks, c =>
        {
            Assert.Matches(@"^[^/]+/-?\d+,-?\d+/\d+,\d+,\d+/\d+\.\d+$", c.ChunkId);
            Assert.Single(c.TriangleOwners);
            Assert.Contains(c.Shape.Vertices, v => MathF.Abs(v.Y) > 64);
        });
    }

    [Fact]
    public void StripSegmentThatFitsNoAnchor_SplitsByFaceUnderTheStripGrammar()
    {
        var f = NativeWorldFixtures.LongSegmentStrip();
        var set = MapTerrainPhysics.Compile(f.View, new MapTerrainChunkPolicy());
        AssertEveryFaceOnceWithUniqueIds(f, set);
        var strip = set.Chunks.Where(c => c.ChunkId.StartsWith("long-segment/", StringComparison.Ordinal)).ToList();
        Assert.Equal(new[] { "long-segment/0.0/Front", "long-segment/0.1/Front" }, strip.Select(c => c.ChunkId));
        Assert.All(strip, c =>
        {
            Assert.Matches(@"^[^/]+/\d+\.\d+/Front$", c.ChunkId);
            Assert.Single(c.TriangleOwners);
            Assert.Contains(c.Shape.Vertices, v => MathF.Abs(v.X) > 64);
        });
        Assert.All(set.Chunks.Except(strip), c => Assert.Matches(@"^long-segment-yard/-?\d+,-?\d+/\d+,\d+,\d+$", c.ChunkId));
    }

    [Theory]
    [InlineData(63, false)]
    [InlineData(64, true)]
    [InlineData(1024, true)]
    [InlineData(1025, false)]
    public void PolicyCap_IsValidFrom64To1024(int cap, bool valid)
    {
        var view = NativeWorldFixtures.FinePatch().View;
        if (valid) Assert.NotEmpty(MapTerrainPhysics.Compile(view, new MapTerrainChunkPolicy(cap)).Chunks);
        else Assert.Throws<ArgumentOutOfRangeException>(() => MapTerrainPhysics.Compile(view, new MapTerrainChunkPolicy(cap)));
    }

    static void AssertEveryFaceOnceWithUniqueIds(TerrainFixture f, MapTerrainChunkSet set)
    {
        Assert.Equal(f.CompiledFaceCount, set.Chunks.Sum(c => c.TriangleOwners.Count));
        Assert.Equal(f.CompiledFaceCount, set.Chunks.SelectMany(c => c.TriangleOwners).Distinct().Count());
        Assert.Equal(set.Chunks.Count, set.Chunks.Select(c => c.ChunkId).Distinct(StringComparer.Ordinal).Count());
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
