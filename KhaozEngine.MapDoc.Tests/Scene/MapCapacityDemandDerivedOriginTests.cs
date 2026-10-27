using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.Tests.MapDoc.Foliage;
using Xunit;
using static KhaozEngine.Tests.CapacityDemandProbe;

namespace KhaozEngine.Tests.MapDoc.Scene;

/// <summary>Remainder and minimum gates publish each originating profile field with its own ceiling.</summary>
[Collection("AllocSensitive")]
public sealed class MapCapacityDemandDerivedOriginTests
{
    [Fact]
    public void FoliageQueryCap_PublishesTheScopeAndCapProfilesAtEveryCandidateGate()
    {
        var fixture = FoliageFixtures.Create();
        var resources = MapPrefabResolver.Expand(fixture.Document, fixture.Assets);
        var cap = new MapQueryLimits(MaxCandidatePatches: 200, MaxRecordReads: 70);
        var kernel = new MapFoliageInputKernel(fixture.Document.Foliage, fixture.Assets, resources.Product, queryLimits: cap);
        var layer = kernel.Foliage!.Layers[0];
        var scope = MapFoliageSupport.Scope(layer, default, kernel.SupportFloorsFor(layer, default), kernel.Transforms,
            new MapQueryLimits(MaxCandidatePatches: 100, MaxRecordReads: 90));
        using var probe = new CapacityDemandProbe();
        var view = kernel.Acquire(scope);
        Assert.Equal(MapAcquireStatus.Complete, view.Status);
        var rows = probe.For("MaxCandidatePatches").ToArray();
        Assert.NotEmpty(rows);
        // Each evaluation is published once against the scope's 100 and once against the cap's 200, with one requested total.
        Assert.Equal(0, rows.Length % 2);
        for (int i = 0; i < rows.Length; i += 2)
        {
            Assert.Equal((decimal)100, Read<decimal>(rows[i], "Allowance"));
            Assert.Equal((decimal)200, Read<decimal>(rows[i + 1], "Allowance"));
            Assert.Equal(Read<string>(rows[i], "Site"), Read<string>(rows[i + 1], "Site"));
            Assert.Equal(Read<decimal>(rows[i], "Requested"), Read<decimal>(rows[i + 1], "Requested"));
            Assert.All(new[] { rows[i], rows[i + 1] }, r => Assert.Equal(Read<decimal>(r, "Requested") > Read<decimal>(r, "Allowance"), Read<bool>(r, "Refused")));
        }
    }

    [Fact]
    public void FoliageContext_ReportsOnlyTheCandidatePatchCeilingItEnforces()
    {
        var fixture = FoliageFixtures.Create();
        var query = new MapQueryLimits { Refinement = new(MaxContextPatches: 1) };
        var context = MapFoliageGenerationContext.Capture(fixture.Document, fixture.Assets, null, query).Context!;
        using var probe = new CapacityDemandProbe();
        var result = MapFoliageSupport.Prepare(context, context.Foliage!.Layers[0], default, new());
        Assert.Equal(MapFoliageStatus.Complete, result.Status);
        var rows = probe.Events.Where(r => Read<string>(r, "Site") == "MapBoundPreparation.MaxContextPatches.1").ToArray();
        Assert.NotEmpty(rows);
        // The foliage context ceiling is the acquisition's 256 candidate patches. The refinement profile never bounds it.
        Assert.All(rows, r => Assert.Equal("MaxCandidatePatches", Read<string>(r, "LimitName")));
        Assert.All(rows, r => Assert.Equal(256, Read<decimal>(r, "Allowance")));
        Assert.All(rows, r => Assert.False(Read<bool>(r, "Refused")));
    }

    [Theory]
    [InlineData(1, 100)]
    [InlineData(100, 1)]
    public void ChunkRetention_PublishesBothSampleCeilingsOfItsMinimum(int maxSamples, int maxQuerySamples)
    {
        var context = FoliageFixtures.Context(FoliageFixtures.Create());
        using var probe = new CapacityDemandProbe();
        var batch = MapFoliageDistribution.Generate(context, "cover-a", new(0, 0), new(MaxSamples: maxSamples, MaxQuerySamples: maxQuerySamples));
        Assert.Equal(MapFoliageStatus.CapacityExceeded, batch.Status);
        var rows = probe.Events.Where(r => Read<string>(r, "Site") == "MapFoliageRetentionBudget.MaxQuerySamples.1").ToArray();
        var samples = rows.Where(r => Read<string>(r, "LimitName") == "MaxSamples").ToArray();
        var query = rows.Where(r => Read<string>(r, "LimitName") == "MaxQuerySamples").ToArray();
        // Both ceilings of the shared minimum report every retention attempt with their own allowance.
        Assert.NotEmpty(samples);
        Assert.Equal(samples.Length, query.Length);
        Assert.All(samples, r => Assert.Equal(maxSamples, Read<decimal>(r, "Allowance")));
        Assert.All(query, r => Assert.Equal(maxQuerySamples, Read<decimal>(r, "Allowance")));
        Assert.Equal(0, Read<decimal>(samples[0], "CurrentDemand"));
        Assert.False(Read<bool>(samples[0], "Refused"));
        if (maxQuerySamples != 1) return;
        // With one query sample, the second attempt is refused by the query ceiling and accepted by the chunk ceiling.
        Assert.Equal(2, query.Length);
        Assert.Equal(new[] { false, true }, query.Select(r => Read<bool>(r, "Refused")));
        Assert.Equal(new[] { false, false }, samples.Select(r => Read<bool>(r, "Refused")));
    }

    [Fact]
    public void ComparisonFaceCeiling_PublishesComparisonBytesAtTheFaceGate()
    {
        var context = FoliageFixtures.Context(FoliageComparisonFixtures.Flat());
        using var probe = new CapacityDemandProbe();
        var refused = MapFoliageDifferential.Compare(context, context, "cover-a", new(MaxComparisonRetainedBytes: 1_900_000));
        Assert.Equal(MapFoliageStatus.CapacityExceeded, refused.Status);
        var faces = probe.Events.Where(r => Read<string>(r, "Site") == "MapFoliageSupportBuilder.MaxFaces.2").ToArray();
        var bytes = faces.Where(r => Read<string>(r, "LimitName") == "MaxComparisonRetainedBytes").ToArray();
        var declared = faces.Where(r => Read<string>(r, "LimitName") == "MaxFaces").ToArray();
        Assert.NotEmpty(bytes);
        Assert.True(declared.Length >= bytes.Length);
        // Each face is worth 1280 comparison bytes, after the bytes the comparison retained before this chunk.
        Assert.All(bytes, r => Assert.Equal(1_900_000, Read<decimal>(r, "Allowance")));
        Assert.All(bytes, r => Assert.Equal(0, Read<decimal>(r, "ProposedIncrement") % 1280));
        Assert.All(bytes, r => Assert.True(Read<decimal>(r, "CurrentDemand") > 0));
        Assert.All(declared, r => Assert.Equal(new MapFoliageLimits().MaxFaces, Read<decimal>(r, "Allowance")));
    }

    [Fact]
    public void ClampedTokenWindow_PublishesTheProfileAndArrayCeilings()
    {
        var tokens = typeof(MapSceneMetadataReader).GetNestedType("Tokens", BindingFlags.NonPublic)!;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("\"" + new string('a', 20000) + "\""));
        using var probe = new CapacityDemandProbe();
        object reader = Activator.CreateInstance(tokens, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { stream, long.MaxValue }, null)!;
        Assert.True((bool)tokens.GetMethod("Read", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(reader, null)!);
        var rows = probe.Events.Where(r => Read<string>(r, "Site") == "MapSceneMetadataReader.MaxMetadataBytes.1").ToArray();
        // The initial 16384-byte window grows once for the 20002-byte token.
        var profile = Assert.Single(rows, r => Read<string>(r, "LimitName") == "MaxMetadataBytes");
        var array = Assert.Single(rows, r => Read<string>(r, "LimitName") == "Int32.MaxValue");
        Assert.Equal(long.MaxValue, Read<decimal>(profile, "Allowance"));
        Assert.Equal(int.MaxValue, Read<decimal>(array, "Allowance"));
        Assert.All(rows, r => Assert.Equal(16384, Read<decimal>(r, "CurrentDemand")));
        Assert.All(rows, r => Assert.Equal(1, Read<decimal>(r, "ProposedIncrement")));
        Assert.All(rows, r => Assert.False(Read<bool>(r, "Refused")));
    }
}
