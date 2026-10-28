using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
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

    static (decimal Current, decimal Increment, decimal Allowance, bool Refused) Of(object row) =>
        (Read<decimal>(row, "CurrentDemand"), Read<decimal>(row, "ProposedIncrement"), Read<decimal>(row, "Allowance"), Read<bool>(row, "Refused"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SupportGrid_PublishesEachFaceOriginInItsOwnUnitsAtTheGateThreshold(bool refused)
    {
        var fixture = FoliageFixtures.Create();
        var view = MapScopedSurfaces.CompleteView(fixture.Document.Surfaces);
        var surface = view.Surfaces.First(s => s.Role == MapSurfaceRole.SupportFloor);
        // A rectangle beside the floor's authored cells, so the cell gate alone decides and no face compiles.
        var min = new MapExactXz(MapExactValue.FromSingle(0), MapExactValue.FromSingle(20));
        var max = new MapExactXz(MapExactValue.FromSingle(11), MapExactValue.FromSingle(31));
        var range = MapLatticeRanges.CellRange(surface.Frame, min, max);
        long cells = (range.MaxXExclusive - range.MinX + 2) * (range.MaxZExclusive - range.MinZ + 2);
        // The grid admits 4 * MaxFaces + 64 cells, so it needs ceil((cells - 64) / 4) faces. This grid lies one to
        // three cells above the next lower ceiling, where a cell-unit threshold of a scaled origin misses the refusal.
        Assert.True(cells > 64);
        Assert.NotEqual(0, (cells - 64) % 4);
        long faces = (cells - 64 + 3) / 4;
        long ceiling = refused ? faces - 1 : faces;
        const long scale = 2560, consumed = 7 * 2560 + 11;
        // A byte origin whose remaining allowance floors to the ceiling, keeping the largest remainder the floor drops.
        long bytes = consumed + scale * ceiling + scale - 1;
        var declared = new MapFoliageLimits(MaxFaces: 100_000);
        var budget = new object();
        var limits = new MapFoliageLimits(MaxFaces: (int)ceiling);
        using var probe = new CapacityDemandProbe();
        MapDemandOrigins.Derive(limits, "MaxFaces", MapDemandOrigins.Of(declared, "MaxFaces", declared.MaxFaces),
            MapDemandOrigins.Of(budget, "MaxPreparedBytes", bytes, consumed, scale));
        var builder = new MapFoliageSupportBuilder(view, new Dictionary<string, MapPaintRegionSet>(), view.Surfaces,
            fixture.Document.Foliage!.Layers[0], default, limits, null);
        var grid = typeof(MapFoliageSupportBuilder).GetMethod("Grid", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Action run = () => grid.Invoke(builder, new object[] { surface, min, max });
        if (refused)
            Assert.Equal(MapFoliageStatus.CapacityExceeded, Assert.IsType<MapFoliageRefusal>(Assert.Throws<TargetInvocationException>(run).InnerException).Status);
        else run();
        var rows = probe.Events.Where(r => Read<string>(r, "Site") == "MapFoliageSupportBuilder.MaxFaces.1").ToArray();
        // Each origin reports faces in its own field's units: faces for MaxFaces, bytes for MaxPreparedBytes.
        Assert.Equal(new[] { "MaxFaces", "MaxPreparedBytes" }, rows.Select(r => Read<string>(r, "LimitName")));
        Assert.Equal((0m, (decimal)faces, 100_000m, false), Of(rows[0]));
        Assert.Equal(((decimal)consumed, (decimal)(scale * faces), (decimal)bytes, refused), Of(rows[1]));
        Assert.All(rows, r => Assert.False(Read<bool>(r, "Saturated")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComparisonFaceOrigin_AcceptsAndRefusesInComparisonBytesAtThePreparedFaceGate(bool refused)
    {
        var context = FoliageFixtures.Context(FoliageFixtures.Create());
        var layer = context.Foliage!.Layers[0];
        long faces = MapFoliageSupport.Prepare(context, layer, default, new()).Work.Faces;
        // Comparison bytes already retained by earlier chunks, then 1280 bytes per prepared face, as a comparison derives
        // its chunk face ceiling. The remaining bytes floor to this chunk's faces, or to one face fewer.
        const long scale = 1280, consumed = 4096 + 3 * 1280;
        long limit = consumed + scale * faces + (refused ? -1 : scale - 1);
        var reconciliation = new object();
        var limits = new MapFoliageLimits(MaxFaces: (int)(refused ? faces - 1 : faces));
        using var probe = new CapacityDemandProbe();
        MapDemandOrigins.Derive(limits, "MaxFaces", MapDemandOrigins.Of(new MapFoliageLimits(), "MaxFaces", new MapFoliageLimits().MaxFaces),
            MapDemandOrigins.Of(reconciliation, "MaxComparisonRetainedBytes", limit, consumed, scale));
        Assert.Equal(refused ? MapFoliageStatus.CapacityExceeded : MapFoliageStatus.Complete, MapFoliageSupport.Prepare(context, layer, default, limits).Status);
        var rows = At(probe, "MapFoliageSupportBuilder.MaxFaces.2", "MaxComparisonRetainedBytes");
        Assert.NotEmpty(rows);
        Assert.Equal(consumed, Read<decimal>(rows[0], "CurrentDemand"));
        Assert.All(rows, r => Assert.Equal(limit, Read<decimal>(r, "Allowance")));
        Assert.All(rows, r => Assert.Equal(0, Read<decimal>(r, "ProposedIncrement") % scale));
        Assert.Equal(consumed + scale * faces, Read<decimal>(rows[^1], "Requested"));
        Assert.Equal(refused, Read<bool>(rows[^1], "Refused"));
        Assert.All(rows[..^1], r => Assert.False(Read<bool>(r, "Refused")));
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
