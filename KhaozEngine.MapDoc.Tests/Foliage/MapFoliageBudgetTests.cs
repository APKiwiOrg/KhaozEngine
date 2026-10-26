using System;
using System.Linq;
using KhaozEngine.MapDoc.Foliage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Foliage;

public sealed class MapFoliageBudgetTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(int.MaxValue - 1)]
    [InlineData(int.MaxValue)]
    public void Query_DeclaredChunkAllowanceMatchesOmittedPolicy(int maxQueryChunks)
    {
        var context = FoliageFixtures.Context(FoliageComparisonFixtures.TwoSamplesPerChunk());
        var expected = MapFoliageDistribution.Query(context, "cover-a", new(2, -2, 18, 2));
        Assert.Equal(MapFoliageStatus.Complete, expected.Status);
        Assert.Equal(2, expected.Batches.Count);
        Assert.All(expected.Batches, batch => Assert.Equal(2, batch.Samples.Count));

        var actual = MapFoliageDistribution.Query(context, "cover-a", new(2, -2, 18, 2),
            new(MaxQueryChunks: maxQueryChunks));
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.Batches.Count, actual.Batches.Count);
        for (int i = 0; i < expected.Batches.Count; i++)
            MapWindowFoliageTests.Same(expected.Batches[i], actual.Batches[i]);
        Assert.Equal(expected.Work, actual.Work);
    }

    [Fact]
    public void Query_AggregateRetentionRefusesIndividuallyLegalChunks()
    {
        var context = FoliageFixtures.Context(FoliageComparisonFixtures.TwoSamplesPerChunk());
        var first = MapFoliageDistribution.Generate(context, "cover-a", new(0, 0));
        var second = MapFoliageDistribution.Generate(context, "cover-a", new(1, 0));
        Assert.Equal(MapFoliageStatus.Complete, first.Status);
        Assert.Equal(MapFoliageStatus.Complete, second.Status);
        Assert.Equal(2, first.Samples.Count);
        Assert.Equal(2, second.Samples.Count);
        var refusedQuery = MapFoliageDistribution.Query(context, "cover-a", new(2, -2, 18, 2), new(MaxQuerySamples: 3));
        Assert.Equal(MapFoliageStatus.CapacityExceeded, refusedQuery.Status);
        Assert.Empty(refusedQuery.Batches);
        Assert.Equal(3, refusedQuery.Work.Samples);
        Assert.True(refusedQuery.Work.Candidates > 0);
        Assert.True(refusedQuery.Work.RetainedBytes > 0);
        var byteRefusal = MapFoliageDistribution.Query(context, "cover-a", new(2, -2, 18, 2),
            new(MaxQueryRetainedBytes: first.Work.RetainedBytes + 255));
        Assert.Equal(MapFoliageStatus.CapacityExceeded, byteRefusal.Status);
        Assert.Empty(byteRefusal.Batches);
        Assert.Equal(2, byteRefusal.Work.Samples);
        var complete = MapFoliageDistribution.Query(context, "cover-a", new(2, -2, 18, 2));
        Assert.Equal(MapFoliageStatus.Complete, complete.Status);
        Assert.Equal(first.Work.RetainedBytes + second.Work.RetainedBytes, complete.Work.RetainedBytes);
    }

    [Fact]
    public void Retention_RefusesBeforeCrossingTheAggregateAllowance()
    {
        var b = new MapFoliageRetentionBudget(1, 816);
        Assert.True(b.TryRetainBatch());
        Assert.True(b.TryRetain(560));
        Assert.False(b.TryRetain(0));
        Assert.False(b.TryRetainBatch());
        var bytes = new MapFoliageRetentionBudget(2, 815);
        Assert.True(bytes.TryRetainBatch());
        Assert.False(bytes.TryRetain(560));
    }

    [Fact]
    public void LargeSparseDomain_RefusesBeforeAreaAllocation()
    {
        FoliageFixture f = FoliageComparisonFixtures.Copy(FoliageFixtures.Create(), l =>
        {
            l["width"] = 1_048_576; l["depth"] = 1_048_576;
        });
        Assert.Equal(2, FoliageComparisonFixtures.Layer(f).Chunks.Count);
        var context = FoliageFixtures.Context(f);
        var query = MapFoliageDistribution.Query(context, "cover-a", new(-3, -5, 2_097_149, 2_097_147));
        Assert.Equal(MapFoliageStatus.CapacityExceeded, query.Status);
        Assert.Empty(query.Batches);
        Assert.Equal(0, query.Work.PrepareScopes);
        var compare = MapFoliageDifferential.Compare(context, context, "cover-a");
        Assert.Equal(MapFoliageStatus.CapacityExceeded, compare.Status);
        Assert.Equal(0, compare.Work.Candidates);
        Assert.Empty(compare.Digest);
    }

    [Fact]
    public void Generation_PerCallCeilingsRefuseBeforePublishingSamples()
    {
        var context = FoliageFixtures.Context(FoliageFixtures.Create());
        var batch = MapFoliageDistribution.Generate(context, "cover-a", new(0, 0));
        Assert.Equal(MapFoliageStatus.Complete, batch.Status);
        var limits = new[]
        {
            new MapFoliageLimits(MaxCandidates: (int)batch.Work.Candidates - 1),
            new MapFoliageLimits(MaxSamples: (int)batch.Work.Samples - 1),
            new MapFoliageLimits(MaxPrepareScopes: 0),
            new MapFoliageLimits(MaxFaces: (int)batch.Work.Faces - 1),
            new MapFoliageLimits(MaxQueryRetainedBytes: 255),
        };
        foreach (var limit in limits)
        {
            var result = MapFoliageDistribution.Generate(context, "cover-a", new(0, 0), limit);
            Assert.Equal(MapFoliageStatus.CapacityExceeded, result.Status);
            Assert.Empty(result.Samples);
            Assert.Empty(result.DependencyDigest);
        }
        Assert.Equal(MapFoliageStatus.CapacityExceeded, MapFoliageDistribution.Query(context, "cover-a", new(-3, -5, 125, 1), new(MaxQueryChunks: 1)).Status);
        FoliageFixture decorated = FoliageComparisonFixtures.Grown(2, 8, 1, 8);
        Assert.Equal(MapFoliageStatus.CapacityExceeded, MapFoliageDistribution.Generate(FoliageFixtures.Context(decorated), "cover-a", new(0, 0), new(MaxRuleCandidates: 0)).Status);
    }

    [Fact]
    public void Generate_AppliesBothQueryCeilingsAsAOneChunkQuery()
    {
        var context = FoliageFixtures.Context(FoliageFixtures.Create());
        var batch = MapFoliageDistribution.Generate(context, "cover-a", new(0, 0));
        Assert.Equal(MapFoliageStatus.Complete, batch.Status);
        Assert.NotEmpty(batch.Samples);
        var exact = MapFoliageDistribution.Generate(context, "cover-a", new(0, 0),
            new(MaxQuerySamples: batch.Work.Samples, MaxQueryRetainedBytes: batch.Work.RetainedBytes));
        Assert.Equal((MapFoliageStatus.Complete, batch.Work.RetainedBytes), (exact.Status, exact.Work.RetainedBytes));
        foreach (var limit in new[]
        {
            new MapFoliageLimits(MaxQueryRetainedBytes: batch.Work.RetainedBytes - 1),
            new MapFoliageLimits(MaxQuerySamples: batch.Work.Samples - 1),
        })
        {
            var refused = MapFoliageDistribution.Generate(context, "cover-a", new(0, 0), limit);
            Assert.Equal(MapFoliageStatus.CapacityExceeded, refused.Status);
            Assert.Empty(refused.Samples);
            Assert.Empty(refused.DependencyDigest);
        }
    }

    [Fact]
    public void Generation_MaskCeilingRefusesBeforePublishingAnOtherwiseLegalChunk()
    {
        var context = FoliageFixtures.Context(FoliageFixtures.CentreMask());
        var complete = MapFoliageDistribution.Generate(context, "cover-a", new(0, 0));
        Assert.Equal(MapFoliageStatus.Complete, complete.Status);
        Assert.Equal(1, complete.Work.MaskCandidates);
        var refused = MapFoliageDistribution.Generate(context, "cover-a", new(0, 0), new(MaxMaskCandidates: 0));
        Assert.Equal(MapFoliageStatus.CapacityExceeded, refused.Status);
        Assert.Empty(refused.Samples);
        Assert.Empty(refused.DependencyDigest);
    }

    [Fact]
    public void Preparation_ByteRefusalPreservesAlreadyEmittedMembershipFaces()
    {
        var context = FoliageFixtures.Context(FoliageFixtures.Stacked(FoliageFixtures.Surface("floor-1")));
        var budget = new MapFoliageDistribution.Reconciliation(30_000);
        var limits = new MapFoliageLimits { ChargePreparation = budget.Take };
        var refused = MapFoliageSupport.Prepare(context, context.Foliage!.Layers[0], new(0, 0), limits);
        Assert.Equal(MapFoliageStatus.CapacityExceeded, refused.Status);
        // Eight bound-floor triangles were emitted first, then eight physical ceiling triangles before the next charge.
        Assert.Equal(16, refused.Work.Faces);
        Assert.InRange(budget.Peak, 16 * 1280, 30_000);
    }

    [Fact]
    public void Comparison_PreparationRefusalReportsChargesBeforePartialAllocation()
    {
        var context = FoliageFixtures.Context(FoliageComparisonFixtures.Flat());
        var refused = MapFoliageDifferential.Compare(context, context, "cover-a", new(MaxComparisonRetainedBytes: 1_900_000));
        Assert.Equal(MapFoliageStatus.CapacityExceeded, refused.Status);
        Assert.True(refused.Work.Faces > 0, refused.Work.ToString());
        Assert.True(refused.Work.RetainedBytes >= 4096 + 1280 * refused.Work.Faces);
        Assert.InRange(refused.Work.RetainedBytes, 0, 1_900_000);
    }

    [Fact]
    public void Comparison_ChangeCeilingChargesOnlyAdmittedRows()
    {
        FoliageFixture f = FoliageComparisonFixtures.TwoSamplesPerChunk();
        var a = FoliageFixtures.Context(f);
        var b = FoliageFixtures.Context(FoliageComparisonFixtures.Copy(f, l => l["seed"] = 2));
        var zero = MapFoliageDifferential.Compare(a, b, "cover-a", new(MaxChangeRows: 0));
        var one = MapFoliageDifferential.Compare(a, b, "cover-a", new(MaxChangeRows: 1));
        Assert.Equal(MapFoliageStatus.CapacityExceeded, one.Status);
        Assert.Equal(zero.Work.RetainedBytes + 256, one.Work.RetainedBytes);
    }

    [Fact]
    public void Comparison_ChargesCandidatesChangesCachesAndDecisionRowsBeforeRetention()
    {
        var before = FoliageFixtures.Context(FoliageComparisonFixtures.TwoSamplesPerChunk());
        var after = FoliageFixtures.Context(FoliageComparisonFixtures.Copy(FoliageComparisonFixtures.TwoSamplesPerChunk(), l => l["seed"] = 2));
        foreach (var limit in new[]
        {
            new MapFoliageLimits(MaxDifferentialCandidates: 1), new MapFoliageLimits(MaxChangeRows: 0),
            new MapFoliageLimits(MaxComparisonPreparedChunks: 0), new MapFoliageLimits(MaxComparisonRetainedBytes: 1023),
        })
        {
            var result = MapFoliageDifferential.Compare(before, after, "cover-a", limit);
            Assert.Equal(MapFoliageStatus.CapacityExceeded, result.Status);
            Assert.Empty(result.Digest);
            Assert.InRange(result.Work.RetainedBytes, 0, limit.MaxComparisonRetainedBytes);
        }
        var exact = MapFoliageDifferential.Compare(before, before, "cover-a");
        Assert.Equal(MapFoliageStatus.Complete, exact.Status);
        Assert.True(exact.Work.RetainedBytes >= 1024);
    }
}
