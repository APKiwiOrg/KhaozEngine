using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Tests.MapDoc.Foliage;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;
using static KhaozEngine.Tests.CapacityDemandProbe;

namespace KhaozEngine.Tests.MapDoc.Scene;

/// <summary>Accepted and refused rows for origin, remainder, minimum and saturating sites.</summary>
[Collection("AllocSensitive")]
public sealed class CapacityDemandDerivedSiteMatrixTests
{
    static object[] At(CapacityDemandProbe probe, string site, string limit) =>
        probe.For(limit).Where(r => Read<string>(r, "Site") == site).ToArray();

    [Fact]
    public void FoliageQueryCap_RefusesAtTheCapOriginWhileTheScopeOriginAccepts()
    {
        var fixture = FoliageFixtures.Create();
        var resources = MapPrefabResolver.Expand(fixture.Document, fixture.Assets);
        var kernel = new MapFoliageInputKernel(fixture.Document.Foliage, fixture.Assets, resources.Product, queryLimits: new(MaxCandidatePatches: 1));
        var layer = kernel.Foliage!.Layers[0];
        var scope = MapFoliageSupport.Scope(layer, default, kernel.SupportFloorsFor(layer, default), kernel.Transforms, new(MaxCandidatePatches: 100));
        using var probe = new CapacityDemandProbe();
        Assert.Equal(MapAcquireStatus.CapacityExceeded, kernel.Acquire(scope).Status);
        var rows = probe.For("MaxCandidatePatches").ToArray();
        var refused = Assert.Single(rows, r => Read<bool>(r, "Refused"));
        Assert.Equal(1, Read<decimal>(refused, "Allowance"));
        var scopeOrigin = rows[Array.IndexOf(rows, refused) - 1];
        Assert.Equal(100, Read<decimal>(scopeOrigin, "Allowance"));
        Assert.False(Read<bool>(scopeOrigin, "Refused"));
        Assert.Equal(Read<decimal>(refused, "CurrentDemand"), Read<decimal>(scopeOrigin, "CurrentDemand"));
        Assert.Equal(Read<decimal>(refused, "ProposedIncrement"), Read<decimal>(scopeOrigin, "ProposedIncrement"));
    }

    [Fact]
    public void MembershipContext_FacesAndPatchCeilingsAcceptAndRefuse()
    {
        var resolved = PrefabFixtures.Effective(PrefabFixtures.Create());
        var complete = resolved.CompleteView();
        var point = PrefabFixtures.Point(resolved, 2, 1, -2);
        MapScopedSurfaces Acquire(Func<MapQueryLimits, MapQueryLimits> limits) => resolved.Acquire(complete.Scope with { Limits = limits(complete.Scope.Limits) });
        decimal faces;
        using (var probe = new CapacityDemandProbe())
        {
            // Four prism vertices are the consumed baseline of the point's context faces.
            Assert.Equal(MapMembershipStatus.Resolved, new MapSpaceMembership(Acquire(l => l with { MaxInspectedFaces = 4096 })).Query(point).Status);
            Assert.Equal(MapMembershipStatus.CapacityExceeded, new MapSpaceMembership(Acquire(l => l with { MaxInspectedFaces = 5 })).Query(point).Status);
            var rows = At(probe, "MapBoundFaceContext.MaxContextFaces.1", "MaxInspectedFaces");
            var accepted = rows.Where(r => Read<decimal>(r, "Allowance") == 4096).ToArray();
            var refused = Assert.Single(rows, r => Read<bool>(r, "Refused"));
            Assert.All(accepted, r => Assert.Equal(4, Read<decimal>(r, "CurrentDemand")));
            Assert.All(accepted, r => Assert.False(Read<bool>(r, "Refused")));
            faces = Read<decimal>(accepted[0], "ProposedIncrement");
            Assert.True(faces > 1);
            Assert.Equal((4m, 5m), (Read<decimal>(refused, "CurrentDemand"), Read<decimal>(refused, "Allowance")));
            Assert.InRange(Read<decimal>(refused, "ProposedIncrement"), 2, faces);
        }
        using (var probe = new CapacityDemandProbe())
        {
            // A declared refinement ceiling joins the query's candidate patch ceiling in a minimum.
            int patches = complete.Scope.Limits.MaxCandidatePatches;
            Assert.Equal(MapMembershipStatus.Resolved,
                new MapSpaceMembership(Acquire(l => l with { Refinement = new(MaxContextPatches: 256) })).Query(point).Status);
            Assert.Equal(MapMembershipStatus.CapacityExceeded,
                new MapSpaceMembership(Acquire(l => l with { Refinement = new(MaxContextPatches: 0) })).Query(point).Status);
            var site = "MapBoundPreparation.MaxContextPatches.1";
            var declared = At(probe, site, "MaxContextPatches");
            var query = At(probe, site, "MaxCandidatePatches");
            Assert.Equal(declared.Length, query.Length);
            var first = declared[0];
            Assert.Equal((0m, 1m, 256m, false), (Read<decimal>(first, "CurrentDemand"), Read<decimal>(first, "ProposedIncrement"),
                Read<decimal>(first, "Allowance"), Read<bool>(first, "Refused")));
            var refused = Assert.Single(declared, r => Read<bool>(r, "Refused"));
            Assert.Equal((0m, 1m, 0m), (Read<decimal>(refused, "CurrentDemand"), Read<decimal>(refused, "ProposedIncrement"), Read<decimal>(refused, "Allowance")));
            var paired = query[Array.IndexOf(declared, refused)];
            Assert.Equal((0m, 1m, (decimal)patches, false), (Read<decimal>(paired, "CurrentDemand"), Read<decimal>(paired, "ProposedIncrement"),
                Read<decimal>(paired, "Allowance"), Read<bool>(paired, "Refused")));
        }
    }

    [Fact]
    public void SupportGrid_AcceptsInCellsAgainstFourFacesPerCeilingFace()
    {
        var context = FoliageFixtures.Context(FoliageFixtures.Create());
        using var probe = new CapacityDemandProbe();
        Assert.Equal(MapFoliageStatus.Complete, MapFoliageSupport.Prepare(context, context.Foliage!.Layers[0], default, new(MaxFaces: 100_000)).Status);
        var rows = At(probe, "MapFoliageSupportBuilder.MaxFaces.1", "MaxFaces");
        Assert.NotEmpty(rows);
        // The refused, saturated counterpart is MapCapacityDemandBoundaryTests.HugeFoliageCellRange.
        Assert.All(rows, r => Assert.Equal(400_064, Read<decimal>(r, "Allowance")));
        Assert.All(rows, r => Assert.Equal(0, Read<decimal>(r, "CurrentDemand")));
        Assert.All(rows, r => Assert.False(Read<bool>(r, "Refused") || Read<bool>(r, "Saturated")));
    }

    [Fact]
    public void ComparisonFaceCeiling_AcceptsEarlierChunksAndRefusesAtTheFaceGate()
    {
        var context = FoliageFixtures.Context(FoliageComparisonFixtures.Flat());
        using var probe = new CapacityDemandProbe();
        Assert.Equal(MapFoliageStatus.CapacityExceeded, MapFoliageDifferential.Compare(context, context, "cover-a", new(MaxComparisonRetainedBytes: 1_900_000)).Status);
        var rows = At(probe, "MapFoliageSupportBuilder.MaxFaces.2", "MaxComparisonRetainedBytes");
        Assert.Contains(rows, r => !Read<bool>(r, "Refused"));
        var refused = Assert.Single(rows, r => Read<bool>(r, "Refused"));
        Assert.True(Read<decimal>(refused, "Requested") > 1_900_000);
    }

    [Fact]
    public void SourceChargeAndChunkSamples_AcceptAndRefuse()
    {
        var fixture = FoliageFixtures.Create();
        var complete = MapFoliageGenerationContext.Capture(fixture.Document, fixture.Assets, new());
        long source = complete.Work.SourceUtf8Bytes;
        using (var probe = new CapacityDemandProbe())
        {
            MapFoliageGenerationContext.Capture(fixture.Document, fixture.Assets, new(MaxSourceUtf8Bytes: source));
            Assert.Equal(MapFoliageStatus.CapacityExceeded, MapFoliageGenerationContext.Capture(fixture.Document, fixture.Assets, new(MaxSourceUtf8Bytes: source - 1)).Status);
            var rows = At(probe, "MapFoliageContextPreflight.SourceCharge", "MaxSourceUtf8Bytes");
            var accepted = rows.Where(r => Read<decimal>(r, "Allowance") == source).ToArray();
            // The charge's running total is the measured source bytes, and the refused charge crosses one byte less.
            Assert.Equal(0, Read<decimal>(accepted[0], "CurrentDemand"));
            Assert.Equal(source, Read<decimal>(accepted[^1], "Requested"));
            Assert.All(accepted, r => Assert.False(Read<bool>(r, "Refused")));
            var refused = rows.Single(r => Read<decimal>(r, "Allowance") == source - 1 && Read<bool>(r, "Refused"));
            Assert.Equal(source, Read<decimal>(refused, "Requested"));
        }
        var context = FoliageFixtures.Context(fixture);
        using (var probe = new CapacityDemandProbe())
        {
            MapFoliageDistribution.Generate(context, "cover-a", new(0, 0), new(MaxSamples: 1));
            var rows = At(probe, "MapFoliageDistribution.MaxSamples.1", "MaxSamples");
            Assert.Equal((0m, 1m, 1m, false), (Read<decimal>(rows[0], "CurrentDemand"), Read<decimal>(rows[0], "ProposedIncrement"),
                Read<decimal>(rows[0], "Allowance"), Read<bool>(rows[0], "Refused")));
            Assert.Equal((1m, 1m, 1m, true), (Read<decimal>(rows[1], "CurrentDemand"), Read<decimal>(rows[1], "ProposedIncrement"),
                Read<decimal>(rows[1], "Allowance"), Read<bool>(rows[1], "Refused")));
        }
    }

    [Fact]
    public void MetadataRowWindow_AcceptsAndRefusesItsRemainder()
    {
        var row = typeof(MapSceneMetadataReader).GetNestedType("LimitedRow", BindingFlags.NonPublic)!;
        using var probe = new CapacityDemandProbe();
        using var stream = (Stream)Activator.CreateInstance(row, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { 5, new object() }, null)!;
        stream.Write(new byte[3], 0, 3);
        Assert.Throws<MapSceneCapacityException>(() => stream.Write(new byte[3], 0, 3));
        var rows = At(probe, "MapSceneMetadataReader.MaxMetadataBytes.3", "MaxMetadataBytes");
        Assert.Equal((0m, 3m, 5m, false), (Read<decimal>(rows[0], "CurrentDemand"), Read<decimal>(rows[0], "ProposedIncrement"),
            Read<decimal>(rows[0], "Allowance"), Read<bool>(rows[0], "Refused")));
        Assert.Equal((3m, 3m, 5m, true), (Read<decimal>(rows[1], "CurrentDemand"), Read<decimal>(rows[1], "ProposedIncrement"),
            Read<decimal>(rows[1], "Allowance"), Read<bool>(rows[1], "Refused")));
    }

    [Theory]
    [InlineData(700, 5000, false)]
    [InlineData(9900, 100, true)]
    public void ColumnBudget_PublishesTheRetainedOriginOfADerivedCopy(int consumed, int remaining, bool refused)
    {
        var original = new MapFoliageLimits(MaxQueryRetainedBytes: 10_000);
        var copy = original with { MaxQueryRetainedBytes = remaining };
        using var probe = new CapacityDemandProbe();
        MapDemandOrigins.Derive(copy, "MaxQueryRetainedBytes", MapDemandOrigins.Of(original, "MaxQueryRetainedBytes", 10_000, consumed));
        Action build = () => new MapFoliageInstanceBuilder("layer", new("legacy-tile", 1, 0), default, copy).Dispose();
        if (refused) Assert.Throws<MapFoliageRefusal>(build); else build();
        // The builder's first charge is its construction controls, after the bytes earlier chunks already retained.
        var row = probe.For("MaxQueryRetainedBytes").First(r => Read<string>(r, "Site") == "MapFoliageColumnBudget.MaxQueryRetainedBytes.1");
        Assert.Equal(((decimal)consumed, (decimal)MapFoliageInventory.ConstructionControls, 10_000m, refused),
            (Read<decimal>(row, "CurrentDemand"), Read<decimal>(row, "ProposedIncrement"), Read<decimal>(row, "Allowance"), Read<bool>(row, "Refused")));
    }

    [Fact]
    public void WaterTraversal_SaturatesAWideRefusal()
    {
        var meter = new MapWaterWorkMeter(new(MaxBoundCells: 2));
        using var probe = new CapacityDemandProbe();
        meter.ChargeBoundTraversal(BigInteger.Zero, 1);
        Assert.Throws<MapWaterCapacityException>(() => meter.ChargeBoundTraversal(BigInteger.Pow(2, 200), 0));
        var rows = probe.For("MaxBoundCells").Where(r => Read<string>(r, "Site") == "MapWaterWorkMeter.Traversal").ToArray();
        Assert.Equal((0m, 1m, 2m, false, false), (Read<decimal>(rows[0], "CurrentDemand"), Read<decimal>(rows[0], "ProposedIncrement"),
            Read<decimal>(rows[0], "Allowance"), Read<bool>(rows[0], "Refused"), Read<bool>(rows[0], "Saturated")));
        // A count wider than decimal reports decimal.MaxValue as an explicit lower bound.
        Assert.Equal((1m, decimal.MaxValue, true, true), (Read<decimal>(rows[1], "CurrentDemand"), Read<decimal>(rows[1], "Requested"),
            Read<bool>(rows[1], "Refused"), Read<bool>(rows[1], "Saturated")));
    }
}
