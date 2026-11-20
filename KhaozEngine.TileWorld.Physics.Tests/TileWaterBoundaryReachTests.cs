using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

/// <summary>The capture facts and the reach collection the water boundary certificate rests on.</summary>
public class TileWaterBoundaryReachTests
{
    [Theory]
    [InlineData("real-shore", 1)]
    [InlineData("weir", 2)]
    [InlineData("region-seam", 2)]
    public void ACaptureVerifiesOneLevelPerDomainAndCountsItsDomains(string world, int domains)
    {
        // A body crossing a region border is two clipped pieces, two domains, each at its own rim.
        TileMediumSampler sampler = WaterBoundaryParityCorpus.Create(world).Sampler!;
        Assert.True(sampler.CaptureFacts.OneLevelPerDomain);
        Assert.Equal(domains, sampler.CaptureFacts.DomainCount);
        Assert.True(sampler.CaptureFacts.Hold);
    }

    [Fact]
    public void TheVerifierRefusesTwoLevelsInOneDomain()
    {
        var domain = new MovementDomainKey("water-world", "pool");
        TileWaterColumn first = Column(domain, 0, -0.02f), same = Column(domain, 1, -0.02f), lower = Column(domain, 1, -0.52f);
        Assert.True(TileWaterCaptureFacts.Verify([first, same]).Hold);
        TileWaterCaptureFacts split = TileWaterCaptureFacts.Verify([first, lower]);
        Assert.False(split.OneLevelPerDomain);
        Assert.False(split.Hold);
    }

    [Theory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void TheVerifierHoldsUpToTheLeaseDomainBound(int domains, bool hold)
    {
        var columns = Enumerable.Range(0, domains)
            .Select(i => Column(new("water-world", $"pool-{i}"), i, -0.02f)).ToArray();
        TileWaterCaptureFacts facts = TileWaterCaptureFacts.Verify(columns);
        Assert.True(facts.OneLevelPerDomain);
        Assert.Equal(domains, facts.DomainCount);
        Assert.Equal(hold, facts.Hold);
    }

    [Fact]
    public void ATestGeometrySamplerIsUnverified()
    {
        // Its borrowed point index addresses another column array, so no level may be read through it.
        TileMediumSampler sampler = WaterBoundaryParityCorpus.Create("real-shore").Sampler!;
        Assert.False(TileMediumSampler.FromTraceForTests(sampler.CaptureTraceForTests(), "water-world").CaptureFacts.Hold);
    }

    [Theory]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public void TheReachCountsUpToTheKernelColumnLimitAndDeclinesBeyondIt(int width, bool certifies)
    {
        // A 16 by 16 lake is 256 columns and a 17 by 16 lake 272. The 12 m capsule covers either completely.
        var document = TileWorldPhysicsTestData.FlatWorld();
        for (int z = 10; z <= 26; z++)
            for (int x = 10; x <= 10 + width; x++) document.SetCornerHeightCm(x, z, 0, -200);
        for (int z = 10; z < 26; z++)
            for (int x = 10; x < 10 + width; x++) document.SetUnderlay(x, z, 0, TileWorldPhysicsTestData.Water);
        document.SetCornerHeightCm(10, 10, 0, 0);
        TileMediumSampler sampler = TileWorldColliders.Build(document, TileWorldPhysicsTestData.Catalogs(), "water-world").Medium;
        var query = new MovementMediumSweepQuery(new(new(18, -0.5f, -18), 12, 12, WaterBoundaryParityCorpus.Space, null), Vector3.Zero);
        Assert.Equal(certifies, sampler.TryCertifyBoundaryReach(query, Vector3.Zero, out var reach));
        if (!certifies) return;
        Assert.Equal(256, reach.Columns);
        Assert.True(reach.UniformLevel);
        Assert.Equal(-0.02f, reach.HighestLevel);
    }

    [Theory]
    [InlineData("weir")]
    [InlineData("region-seam")]
    public void AReachAcrossTwoLevelsIsNotUniformAndNamesTheHigher(string world)
    {
        WaterBoundaryParityWorld corpus = WaterBoundaryParityCorpus.Create(world);
        foreach (MovementMediumSweepQuery query in corpus.Groups[0].Queries)
        {
            Assert.True(corpus.Sampler!.TryCertifyBoundaryReach(query, Vector3.Zero, out var reach));
            Assert.False(reach.UniformLevel);
            Assert.Equal(-0.02f, reach.HighestLevel);
        }
    }

    [Fact]
    public void AnEmptyReachCountsNoColumn()
    {
        TileMediumSampler sampler = WaterBoundaryParityCorpus.Create("weir").Sampler!;
        var query = new MovementMediumSweepQuery(new(new(30, 0, -30), 0.3f, 0.75f, WaterBoundaryParityCorpus.Space, null), Vector3.UnitX);
        Assert.True(sampler.TryCertifyBoundaryReach(query, Vector3.Zero, out var reach));
        Assert.Equal(0, reach.Columns);
    }

    // The corpus worlds whose questions select merged flat columns, including the wide merged lake bed.
    [Theory]
    [InlineData("scenario/flat-control")]
    [InlineData("drawn-bed/flat-patch")]
    [InlineData("drawn-bed/coarse")]
    [InlineData("weir")]
    [InlineData("region-seam")]
    public void EveryFlatColumnTheFlatKernelSelectsHasACountedColumnOfItsLevelInside(string world)
    {
        // The prism kernel selects through the same index and bounds as the reach. The flat kernel selects merged
        // columns through its own index, and each one must hand the certificate its domain and level.
        WaterBoundaryParityWorld corpus = WaterBoundaryParityCorpus.Create(world);
        TileMediumSampler sampler = corpus.Sampler!;
        var capture = sampler.CaptureTraceForTests();
        var flat = new TileWaterFlatIndex(capture.Columns, capture.Boundaries, capture.Exterior);
        Span<int> storage = stackalloc int[MovementQueryLease.MaxDomainContacts];
        int checkedColumns = 0;
        foreach (var (origin, queries) in corpus.Groups)
            foreach (MovementMediumSweepQuery query in queries)
            {
                TileWaterColumn[]? counted = sampler.ReachColumnsForTests(query, origin);
                if (counted is null) continue;
                var selected = flat.Columns.Select(query, origin, storage);
                for (int i = 0; i < selected.Count; i++)
                {
                    TileWaterColumn merged = capture.Columns[selected[i]];
                    checkedColumns++;
                    Assert.Contains(counted, column => column.Domain == merged.Domain &&
                        BitConverter.SingleToInt32Bits(column.Maximum.Y) == BitConverter.SingleToInt32Bits(merged.Maximum.Y) &&
                        column.Minimum.X >= merged.Minimum.X && column.Maximum.X <= merged.Maximum.X &&
                        column.Minimum.Z >= merged.Minimum.Z && column.Maximum.Z <= merged.Maximum.Z);
                }
            }
        Assert.True(checkedColumns > 0);
    }

    static TileWaterColumn Column(MovementDomainKey domain, int x, float top) => new(domain, new(x, -2, -1), new(x + 1, top, 0),
        "min-x", "max-x", "min-z", "max-z", "bed", "surface");
}
