using System;
using System.Linq;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageRoundedOwnershipTests
{
    static FoliageFixture Fixture()
    {
        var fixture = FoliageComparisonFixtures.Flat();
        var layer = FoliageFixtures.Layer("cover-a", FoliageFixtures.Surface("floor"), 0.1f, -1, 1, 65, 3,
            Enumerable.Repeat((byte)255, 65 * 3).ToArray(), []);
        return FoliageComparisonFixtures.Set(fixture, layer);
    }

    [Fact]
    public void RoundedLowerBoundary_BelongsToItsGenerationRectangle()
    {
        var layer = Fixture().Document.Foliage!.Layers[0];
        float boundary = MapFoliageDistribution.Rectangle(layer, new(1, 0)).MinX;
        Assert.Equal(32.099998474121094, (double)boundary);
        Assert.Equal(1, MapFoliageDistribution.ChunkIndex(layer, boundary, true));
        Assert.Equal(0, MapFoliageDistribution.ChunkIndex(layer, MathF.BitDecrement(boundary), true));
    }

    [Fact]
    public void Query_DoesNotChargeOrPrepareAnEmptyPrecedingRectangle()
    {
        var context = FoliageFixtures.Context(Fixture());
        float boundary = MapFoliageDistribution.Rectangle(context.Foliage!.Layers[0], new(1, 0)).MinX;

        var result = MapFoliageDistribution.Query(context, "cover-a", new(boundary, -0.5f, boundary + 0.1f, 0.5f),
            new() { MaxQueryChunks = 1 });

        Assert.Equal(MapFoliageStatus.Complete, result.Status);
        Assert.Equal(new MapFoliageChunkKey(1, 0), Assert.Single(result.Batches).Chunk);
        Assert.Equal(1, result.Work.PrepareScopes);
    }

    [Fact]
    public void RoundedBoundaryCandidate_UsesTheSamePreparationAsItsBatchDigest()
    {
        var fixture = Fixture();
        var layer = fixture.Document.Foliage!.Layers[0];
        float boundary = MapFoliageDistribution.Rectangle(layer, new(1, 0)).MinX;
        var unitCandidate = GroundCoverDistribution.Prepare(new() { Seed = layer.Seed, Spacing = 1, Models = [new("reed-a", 1)] }).Candidate(32, 0);
        layer = FoliageComparisonFixtures.Change(layer, l => l["spacing"] = boundary / unitCandidate.WorldX);
        fixture = FoliageComparisonFixtures.Set(fixture, layer);
        var context = FoliageFixtures.Context(fixture);
        var candidate = GroundCoverDistribution.Prepare(new() { Seed = layer.Seed, Spacing = layer.Spacing, Models = [new("reed-a", 1)] }).Candidate(32, 0);
        Assert.Equal(boundary, candidate.WorldX);
        var meter = new MapFoliageDistribution.Meter();
        var evaluation = new MapFoliageDistribution.Evaluation(context, context.Foliage!.Layers[0], new(), meter,
            new MapFoliageDistribution.Reconciliation(134_217_728));

        Assert.NotNull(evaluation.Decide(32, 0, MapFoliageDistribution.Rectangle(layer, new(1, 0)), _ => true).Sample);
        Assert.NotEmpty(evaluation.Digest(new(1, 0)));
        Assert.Equal(1, meter.PrepareScopes);
    }

    [Fact]
    public void BatchDigest_BindsThePrecedingDensityChunkReadAtItsRoundedEdge()
    {
        var fixture = Fixture();
        var changed = MapFoliageEdits.Prepare(fixture.Document, fixture.Assets, new MapSetFoliageDensity("cover-a", 31, 0, 1, 1, [0]));
        var before = MapFoliageDistribution.Generate(FoliageFixtures.Context(fixture), "cover-a", new(1, 0));
        var after = MapFoliageDistribution.Generate(FoliageFixtures.Context(new(changed.After, fixture.Assets)), "cover-a", new(1, 0));
        Assert.Equal(MapFoliageStatus.Complete, before.Status);
        Assert.Equal(MapFoliageStatus.Complete, after.Status);
        Assert.NotEqual(before.DependencyDigest, after.DependencyDigest);
    }

    [Fact]
    public void FinalizedEffects_NameTheReaderOfThePrecedingDensityChunk()
    {
        var fixture = Fixture();
        var changed = MapFoliageEdits.Prepare(fixture.Document, fixture.Assets, new MapSetFoliageDensity("cover-a", 31, 0, 1, 1, [0]));
        Assert.Empty(changed.Effects.FoliageLayers);
        Assert.Contains(changed.Effects.FoliageChunks, row => row.Chunk == new MapFoliageChunkKey(1, 0));
    }
}
