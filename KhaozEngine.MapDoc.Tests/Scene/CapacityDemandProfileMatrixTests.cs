using System;
using System.Linq;
using System.Reflection;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Tests.MapDoc.Foliage;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;
using static KhaozEngine.Tests.CapacityDemandProbe;

namespace KhaozEngine.Tests.MapDoc.Scene;

/// <summary>One accepted and one refused evaluation per scene, foliage, refinement and query profile field. Each
/// expectation comes from the test's own ceiling and a fixture fact, never from the event under test.</summary>
[Collection("AllocSensitive")]
public sealed class CapacityDemandProfileMatrixTests
{
    static object[] At(CapacityDemandProbe probe, string site, string limit) =>
        probe.For(limit).Where(r => Read<string>(r, "Site") == site).ToArray();

    static void Row(object row, decimal current, decimal increment, decimal allowance, bool refused)
    {
        Assert.Equal(current, Read<decimal>(row, "CurrentDemand"));
        Assert.Equal(increment, Read<decimal>(row, "ProposedIncrement"));
        Assert.Equal(allowance, Read<decimal>(row, "Allowance"));
        Assert.Equal(refused, Read<bool>(row, "Refused"));
    }

    [Theory]
    [InlineData("MaxMetadataRows", "MapSceneMetadataReader.MaxMetadataRows.2", 0, 1, 1, 1, 1)]
    [InlineData("MaxMemberships", "MapSceneMetadataReader.MaxMemberships.1", 0, 2, 3, 2, 2)]
    [InlineData("MaxMembershipsPerResource", "MapSceneMetadataReader.MaxMembershipsPerResource.1", 0, 2, 2, 0, 3)]
    public void SceneScan_RowsAndMemberships(string limit, string site, int current, int increment, int allowance,
        int refusedCurrent, int refusedIncrement)
    {
        var scan = new MapSceneScan(new(MaxMetadataRows: limit == "MaxMetadataRows" ? 1 : 100,
            MaxMemberships: limit == "MaxMemberships" ? 3 : 100, MaxMembershipsPerResource: limit == "MaxMembershipsPerResource" ? 2 : 100,
            MaxMetadataBytes: 1_000_000));
        using var probe = new CapacityDemandProbe();
        scan.Retain(0, 2);
        Assert.Throws<MapSceneCapacityException>(() => scan.Retain(0, limit == "MaxMembershipsPerResource" ? 3 : 2));
        var rows = At(probe, site, limit);
        Assert.Equal(2, rows.Length);
        Row(rows[0], current, increment, allowance, false);
        Row(rows[1], refusedCurrent, refusedIncrement, allowance, true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SceneSource_TileCount(bool refused)
    {
        // The tile-owner fixture stores its owner in one tile.
        using var fixture = NativeSceneFixtures.Create("tile-owner");
        using var source = MapDocumentSource.FromDocument(fixture.Document);
        const int tiles = 1;
        using var probe = new CapacityDemandProbe();
        var result = MapSceneSource.Capture(source, fixture.Assets, new(MaxTiles: refused ? tiles - 1 : tiles));
        Assert.Equal(refused, result.Source is null);
        Row(Assert.Single(At(probe, "MapSceneSource.MaxTiles.1", "MaxTiles")), 0, tiles, refused ? tiles - 1 : tiles, refused);
    }

    [Fact]
    public void SceneAdmission_SurfacePatchesAndPreparedBytes()
    {
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 10), foliageLimits: new(MaxSurfacePatches: 1));
        Assert.True(admission.TryReservePreparation(7, 0));
        using var probe = new CapacityDemandProbe();
        admission.BeforeResource(new("Patch", "a"));
        Assert.Throws<MapSceneCapacityException>(() => admission.BeforeResource(new("Patch", "b")));
        Assert.True(admission.CanReservePreparation(3, 0));
        Assert.False(admission.CanReservePreparation(4, 0));
        var patches = At(probe, "MapSceneAdmission.MaxSurfacePatches.1", "MaxSurfacePatches");
        Row(patches[0], 0, 1, 1, false);
        Row(patches[1], 1, 1, 1, true);
        var bytes = At(probe, "MapPreparationLedger.CanReservePreparation", "MaxPreparedBytes");
        Row(bytes[0], 7, 3, 10, false);
        Row(bytes[1], 7, 4, 10, true);
    }

    [Fact]
    public void FoliageScopesMasksAndChunks()
    {
        var context = FoliageFixtures.Context(FoliageFixtures.Create());
        var layer = context.Foliage!.Layers[0];
        using (var probe = new CapacityDemandProbe())
        {
            Assert.Equal(MapFoliageStatus.Complete, MapFoliageSupport.Prepare(context, layer, default, new(MaxPrepareScopes: 1)).Status);
            Assert.Equal(MapFoliageStatus.CapacityExceeded, MapFoliageSupport.Prepare(context, layer, default, new(MaxPrepareScopes: 0)).Status);
            var rows = At(probe, "MapFoliageSupport.MaxPrepareScopes.1", "MaxPrepareScopes");
            Row(rows[0], 0, 1, 1, false);
            Row(rows[1], 0, 1, 0, true);
        }
        using (var probe = new CapacityDemandProbe())
        {
            // The basic layer has two density chunks along X and one along Z.
            Assert.Equal(MapFoliageStatus.Complete, MapFoliageDistribution.Query(context, "cover-a", new(-3, -5, 125, 1), new(MaxQueryChunks: 2)).Status);
            Assert.Equal(MapFoliageStatus.CapacityExceeded, MapFoliageDistribution.Query(context, "cover-a", new(-3, -5, 125, 1), new(MaxQueryChunks: 1)).Status);
            var rows = At(probe, "MapFoliageDistribution.MaxQueryChunks.2", "MaxQueryChunks");
            Row(rows[0], 0, 1, 2, false);
            Row(rows[1], 1, 1, 2, false);
            Row(rows.First(r => Read<decimal>(r, "Allowance") == 1 && Read<decimal>(r, "CurrentDemand") == 1), 1, 1, 1, true);
        }
        var masked = FoliageFixtures.Context(FoliageFixtures.CentreMask());
        using (var probe = new CapacityDemandProbe())
        {
            // The centre mask fixture has exactly one mask candidate in chunk zero.
            Assert.Equal(MapFoliageStatus.Complete, MapFoliageDistribution.Generate(masked, "cover-a", new(0, 0), new(MaxMaskCandidates: 1)).Status);
            Assert.Equal(MapFoliageStatus.CapacityExceeded, MapFoliageDistribution.Generate(masked, "cover-a", new(0, 0), new(MaxMaskCandidates: 0)).Status);
            var rows = At(probe, "MapFoliageClearance.MaxMaskCandidates.1", "MaxMaskCandidates");
            Row(rows.First(r => Read<decimal>(r, "Allowance") == 1), 0, 1, 1, false);
            Row(rows.Single(r => Read<decimal>(r, "Allowance") == 0), 0, 1, 0, true);
        }
    }

    [Fact]
    public void FoliageCandidatesFacesAndRules()
    {
        var context = FoliageFixtures.Context(FoliageFixtures.Create());
        var layer = context.Foliage!.Layers[0];
        var complete = MapFoliageDistribution.Generate(context, "cover-a", new(0, 0));
        long candidates = complete.Work.Candidates;
        using (var probe = new CapacityDemandProbe())
        {
            MapFoliageDistribution.Generate(context, "cover-a", new(0, 0), new(MaxCandidates: (int)candidates));
            Assert.Equal(MapFoliageStatus.CapacityExceeded, MapFoliageDistribution.Generate(context, "cover-a", new(0, 0), new(MaxCandidates: (int)candidates - 1)).Status);
            var rows = At(probe, "MapFoliageDistribution.MaxCandidates.1", "MaxCandidates");
            Row(rows[0], 0, candidates, candidates, false);
            Row(rows[1], 0, candidates, candidates - 1, true);
        }
        long faces = MapFoliageSupport.Prepare(context, layer, default, new()).Work.Faces;
        using (var probe = new CapacityDemandProbe())
        {
            Assert.Equal(MapFoliageStatus.Complete, MapFoliageSupport.Prepare(context, layer, default, new(MaxFaces: (int)faces)).Status);
            Assert.Equal(MapFoliageStatus.CapacityExceeded, MapFoliageSupport.Prepare(context, layer, default, new(MaxFaces: (int)faces - 1)).Status);
            var rows = At(probe, "MapFoliageSupportBuilder.MaxFaces.2", "MaxFaces");
            var accepted = rows.Where(r => Read<decimal>(r, "Allowance") == faces).ToArray();
            var refused = rows.Where(r => Read<decimal>(r, "Allowance") == faces - 1).ToArray();
            // The prepared work's face total is the last accepted evaluation's requested total and the refused request.
            Assert.All(accepted, r => Assert.False(Read<bool>(r, "Refused")));
            Assert.Equal(faces, Read<decimal>(accepted[^1], "Requested"));
            Assert.Equal(faces, Read<decimal>(refused[^1], "Requested"));
            Assert.True(Read<bool>(refused[^1], "Refused"));
            Assert.Equal(Read<decimal>(accepted[^1], "CurrentDemand"), Read<decimal>(refused[^1], "CurrentDemand"));
        }
        var decorated = FoliageFixtures.Context(FoliageComparisonFixtures.Grown(2, 8, 1, 8));
        long rules = MapFoliageDistribution.Generate(decorated, "cover-a", new(0, 0)).Work.RuleCandidates;
        Assert.True(rules > 0);
        using (var probe = new CapacityDemandProbe())
        {
            MapFoliageDistribution.Generate(decorated, "cover-a", new(0, 0), new(MaxRuleCandidates: (int)rules));
            var refusal = MapFoliageDistribution.Generate(decorated, "cover-a", new(0, 0), new(MaxRuleCandidates: 0));
            Assert.Equal(MapFoliageStatus.CapacityExceeded, refusal.Status);
            // The last matched rule reaches the accepted run's rule total. A zero ceiling refuses the inspected owner
            // candidates, which the refused work reports as its rule candidates.
            Row(At(probe, "MapFoliageClearance.MaxRuleCandidates.2", "MaxRuleCandidates").Where(r => Read<decimal>(r, "Allowance") == rules).Last(), 0, rules, rules, false);
            var refused = Assert.Single(probe.For("MaxRuleCandidates"), r => Read<bool>(r, "Refused"));
            Assert.Equal("MapFoliageClearance.MaxRuleCandidates.1", Read<string>(refused, "Site"));
            Assert.True(refusal.Work.RuleCandidates > 0);
            Row(refused, 0, refusal.Work.RuleCandidates, 0, true);
        }
    }

    [Fact]
    public void FoliageComparisonCeilings()
    {
        var context = FoliageFixtures.Context(FoliageComparisonFixtures.TwoSamplesPerChunk());
        var changed = FoliageFixtures.Context(FoliageComparisonFixtures.Copy(FoliageComparisonFixtures.TwoSamplesPerChunk(), l => l["seed"] = 2));
        long candidates = MapFoliageDifferential.Compare(context, changed, "cover-a").Work.Candidates;
        using (var probe = new CapacityDemandProbe())
        {
            MapFoliageDifferential.Compare(context, changed, "cover-a", new(MaxDifferentialCandidates: candidates));
            Assert.Equal(MapFoliageStatus.CapacityExceeded, MapFoliageDifferential.Compare(context, changed, "cover-a", new(MaxDifferentialCandidates: candidates - 1)).Status);
            var rows = At(probe, "MapFoliageDifferential.MaxDifferentialCandidates.1", "MaxDifferentialCandidates");
            Row(rows[0], 0, candidates, candidates, false);
            Row(rows[1], 0, candidates, candidates - 1, true);
        }
        using (var probe = new CapacityDemandProbe())
        {
            MapFoliageDifferential.Compare(context, changed, "cover-a", new(MaxChangeRows: 1000));
            Assert.Equal(MapFoliageStatus.CapacityExceeded, MapFoliageDifferential.Compare(context, changed, "cover-a", new(MaxChangeRows: 0)).Status);
            var rows = At(probe, "MapFoliageDifferential.MaxChangeRows.1", "MaxChangeRows");
            Row(rows[0], 0, 1, 1000, false);
            Row(rows.Single(r => Read<decimal>(r, "Allowance") == 0), 0, 1, 0, true);
        }
        using (var probe = new CapacityDemandProbe())
        {
            MapFoliageDifferential.Compare(context, changed, "cover-a", new(MaxComparisonPreparedChunks: 1));
            Assert.Equal(MapFoliageStatus.CapacityExceeded, MapFoliageDifferential.Compare(context, changed, "cover-a", new(MaxComparisonPreparedChunks: 0)).Status);
            var rows = At(probe, "MapFoliageDistribution.MaxComparisonPreparedChunks.1", "MaxComparisonPreparedChunks");
            Row(rows[0], 0, 1, 1, false);
            Row(rows.Single(r => Read<decimal>(r, "Allowance") == 0), 0, 1, 0, true);
        }
        using (var probe = new CapacityDemandProbe())
        {
            var budget = new MapFoliageDistribution.Reconciliation(10);
            budget.Take(7);
            Assert.Throws<MapFoliageRefusal>(() => budget.Take(4));
            var rows = At(probe, "MapFoliageDistribution.MaxComparisonRetainedBytes.1", "MaxComparisonRetainedBytes");
            Row(rows[0], 0, 7, 10, false);
            Row(rows[1], 7, 4, 10, true);
        }
    }

    [Fact]
    public void RefinementPairsRegionsAndContextFaces()
    {
        var refinement = new MapRefinementLimits(MaxPairChecks: 3, MaxRefinementFaces: 1, MaxRefinementVertices: 5, MaxContextFaces: 2);
        using (var probe = new CapacityDemandProbe())
        {
            // Occupied partitions and prism coverage count refinement pair checks in this budget.
            var pairs = new MapWaterWorkBudget(refinement.MaxPairChecks, compile: false, source: refinement);
            pairs.Charge(2);
            Assert.Throws<MapDocumentException>(() => pairs.Charge(2));
            var rows = At(probe, "MapWaterWorkBudget.MaxPairChecks.1", "MaxPairChecks");
            Row(rows[0], 0, 2, 3, false);
            Row(rows[1], 2, 2, 3, true);
        }
        var work = typeof(MapPartialPaintCompiler).GetNestedType("Work", BindingFlags.NonPublic)!;
        var source = work.GetMethod("Source", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var (limits, name) in new[] { (refinement with { MaxRefinementVertices = 100 }, "MaxRefinementFaces"), (refinement with { MaxRefinementFaces = 100 }, "MaxRefinementVertices") })
        {
            using var probe = new CapacityDemandProbe();
            using var regions = (IDisposable)Activator.CreateInstance(work, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new object[] { limits }, null)!;
            source.Invoke(regions, new object[] { 3 });
            Assert.IsType<MapDocumentException>(Assert.Throws<TargetInvocationException>(() => source.Invoke(regions, new object[] { 3 })).InnerException);
            // Each paint source region charges one region and its three vertices, as a request against the whole ceiling.
            var rows = At(probe, "MapPartialPaintCompiler.Source", name);
            if (name == "MaxRefinementFaces") { Row(rows[0], 0, 1, 1, false); Row(rows[1], 0, 2, 1, true); }
            else { Row(rows[0], 0, 3, 5, false); Row(rows[1], 0, 6, 5, true); }
        }
        var (view, _) = BoundFaceContextFixtures.EightByEight();
        using (var probe = new CapacityDemandProbe())
        {
            // A flat floor cell compiles to two triangles.
            MapCellDemand[] cell = [new(new("floor8", 0, 0), 0)];
            Assert.NotNull(MapBoundFaceContext.Prepare(view, cell, 1, 2, null, out _));
            Assert.Null(MapBoundFaceContext.Prepare(view, cell, 1, 1, null, out _));
            var rows = At(probe, "MapBoundFaceContext.MaxContextFaces.1", "MaxContextFaces");
            Row(rows[0], 0, 2, 2, false);
            Row(rows[1], 0, 2, 1, true);
        }
    }

    [Fact]
    public void QueryPatchesFacesRecordsAndDepth()
    {
        var resolved = PrefabFixtures.Effective(PrefabFixtures.Create());
        var complete = resolved.CompleteView();
        int records = complete.Witness.Records.Count;
        Assert.True(records > 0);
        MapScopedSurfaces Acquire(Func<MapQueryLimits, MapQueryLimits> limits) => resolved.Acquire(complete.Scope with { Limits = limits(complete.Scope.Limits) });
        using (var probe = new CapacityDemandProbe())
        {
            // An eight by eight floor patch and a sixteen by sixteen ceiling patch, both inside the complete scope.
            var set = new MapSurfaceSet();
            set.Refs.Add(MixedResolutionFixtures.Surface("floor8", 1, MapSurfaceRole.SupportFloor));
            set.Refs.Add(MixedResolutionFixtures.Surface("ceil8", 2, MapSurfaceRole.Ceiling));
            foreach (var patch in new[] { MixedResolutionFixtures.Patch(new("floor8", 0, 0), 8, 8, (_, _) => 0),
                MixedResolutionFixtures.Patch(new("ceil8", 0, 0), 16, 16, (_, _) => 600) })
                set.Patches.Add(patch.Key, patch);
            var document = MixedResolutionFixtures.Document(set);
            var source = MapDocumentSurfaceSource.Capture(document);
            var scope = MapScopedSurfaces.CompleteView(document.Surfaces).Scope;
            Assert.Equal(MapAcquireStatus.Complete, MapScopedSurfaces.Acquire(source, scope with { Limits = new(MaxCandidatePatches: 2) }, []).Status);
            Assert.Equal(MapAcquireStatus.CapacityExceeded, MapScopedSurfaces.Acquire(source, scope with { Limits = new(MaxCandidatePatches: 1) }, []).Status);
            var rows = probe.For("MaxCandidatePatches").ToArray();
            var refused = Assert.Single(rows, r => Read<bool>(r, "Refused"));
            Row(refused, 1, 1, 1, true);
            string site = Read<string>(refused, "Site");
            Assert.Equal("MapSurfaceQuery.MaxCandidatePatches.1", site);
            var accepted = rows.Where(r => Read<string>(r, "Site") == site && Read<decimal>(r, "Allowance") == 2).ToArray();
            Row(accepted[0], 0, 1, 2, false);
            Row(accepted[1], 1, 1, 2, false);
        }
        using (var probe = new CapacityDemandProbe())
        {
            Assert.Equal(MapAcquireStatus.Complete, Acquire(l => l with { MaxRecordReads = records }).Status);
            Assert.Equal(MapAcquireStatus.CapacityExceeded, Acquire(l => l with { MaxRecordReads = records - 1 }).Status);
            var rows = probe.For("MaxRecordReads").ToArray();
            Assert.Equal(records, rows.Where(r => Read<decimal>(r, "Allowance") == records).Max(r => Read<decimal>(r, "Requested")));
            var refused = Assert.Single(rows, r => Read<bool>(r, "Refused"));
            // The effective source admits its whole record request before reading any record.
            Assert.Equal("MapEffectiveSurfaceSource.MaxRecordReads.1", Read<string>(refused, "Site"));
            Row(refused, 0, records, records - 1, true);
        }
        using (var probe = new CapacityDemandProbe())
        {
            // Far anchored records are reached through one anchor, one level deep.
            string dir = Storage.AnchorFixtures.FarAnchors();
            try
            {
                MapScopedSurfaces Far(int depth) => Storage.ScopeFixtures.Acquire(MapStoredSurfaceSource.Open(dir),
                    Storage.ScopeFixtures.Around(Storage.AnchorFixtures.FarFrame, 2, 2, half: 1.5f, limits: new MapQueryLimits(MaxRecordDepth: depth)));
                Assert.Equal(MapAcquireStatus.Complete, Far(8).Status);
                Assert.Equal(MapAcquireStatus.CapacityExceeded, Far(0).Status);
            }
            finally { System.IO.Directory.Delete(dir, recursive: true); }
            var rows = probe.For("MaxRecordDepth").ToArray();
            var refused = Assert.Single(rows, r => Read<bool>(r, "Refused"));
            Row(refused, 0, 1, 0, true);
            Assert.Equal("MapScopedAcquisition.MaxRecordDepth.1", Read<string>(refused, "Site"));
            Assert.Contains(rows, r => Read<string>(r, "Site") == Read<string>(refused, "Site") && Read<decimal>(r, "Allowance") == 8 &&
                Read<decimal>(r, "CurrentDemand") == 0 && Read<decimal>(r, "ProposedIncrement") == 1 && !Read<bool>(r, "Refused"));
        }
        using (var probe = new CapacityDemandProbe())
        {
            // The invented room has one rectangular prism with four footprint vertices.
            var point = PrefabFixtures.Point(resolved, 2, 1, -2);
            new MapSpaceMembership(Acquire(l => l with { MaxInspectedFaces = 4096 })).Query(point);
            Assert.Equal(MapMembershipStatus.CapacityExceeded, new MapSpaceMembership(Acquire(l => l with { MaxInspectedFaces = 3 })).Query(point).Status);
            var rows = At(probe, "MapSpaceMembership.MaxInspectedFaces.1", "MaxInspectedFaces");
            Row(rows[0], 0, 4, 4096, false);
            Row(rows[1], 0, 4, 3, true);
        }
    }

    [Fact]
    public void SupportIntersectionsAndPageReads()
    {
        using (var probe = new CapacityDemandProbe())
        {
            // Eight stacked floors lie inside the hundred-metre downward reach.
            var doc = SupportFixtures.EightStacked();
            var request = SupportFixtures.Request(0.5f, 70.2f, 0.5f, 0.5f, 100f);
            SupportFixtures.Select(doc, request, new MapQueryLimits(MaxSupportIntersections: 8));
            Assert.Equal(MapSupportStatus.CapacityExceeded, SupportFixtures.Select(doc, request, new MapQueryLimits(MaxSupportIntersections: 2)).Status);
            var rows = At(probe, "MapSupportSelection.MaxSupportIntersections.1", "MaxSupportIntersections");
            Row(rows[0], 0, 1, 8, false);
            Row(rows.Single(r => Read<bool>(r, "Refused")), 2, 1, 2, true);
        }
        using (var probe = new CapacityDemandProbe())
        {
            var pages = new MapPageBudget(1);
            pages.BeforeVisit();
            Assert.Throws<MapSurfaceCapacityException>(() => pages.BeforeVisit());
            var rows = At(probe, "MapPageBudget.MaxPageReads.1", "MaxPageReads");
            Row(rows[0], 0, 1, 1, false);
            Row(rows[1], 1, 1, 1, true);
        }
    }
}
