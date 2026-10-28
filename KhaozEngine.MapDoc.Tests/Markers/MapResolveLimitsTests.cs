using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;
using static KhaozEngine.Tests.CapacityDemandProbe;

namespace KhaozEngine.Tests.MapDoc.Markers;

/// <summary>Resolver-2 entry points resolve under the caller's query and prefab expansion profile, and the profile only
/// decides where resolution refuses. Serial because the demand observer is process-wide.</summary>
[Collection("AllocSensitive")]
public sealed class MapResolveLimitsTests
{
    static MapResolveOptions Declared() => MarkerFixtures.Options() with { QueryLimits = new(MaxPageReads: 64) };

    [Fact]
    public void BoundSlotSupport_RefusesAtTheDefaultProfileNamingItsOriginatingPageReads()
    {
        MarkerFixture fixture = MarkerFixtures.WideBounds();
        using var probe = new CapacityDemandProbe();
        var error = Assert.Throws<MapDocumentException>(() => MapNativeResolution.Resolve(fixture.Document, fixture.Assets, MarkerFixtures.Options()));
        Assert.Contains("Marker 'stacked-disabled' support status CapacityExceeded", error.Message, StringComparison.Ordinal);
        Assert.Contains("MaxPageMetadataChecks: requested 32770, allowance 32769", error.Message, StringComparison.Ordinal);

        // The scaled ceiling reports itself and the caller's MaxPageReads, each refusing exactly where the gate does.
        var refused = probe.Events.Where(e => Read<string>(e, "Site") == "MapPageBudget.MaxPageMetadataChecks.1" && Read<bool>(e, "Refused")).ToArray();
        var ceiling = Assert.Single(refused, e => Read<string>(e, "LimitName") == "MaxPageMetadataChecks");
        Assert.Equal(32770m, Requested(ceiling));
        Assert.Equal(32769m, Read<decimal>(ceiling, "Allowance"));
        var pages = Assert.Single(refused, e => Read<string>(e, "LimitName") == "MaxPageReads");
        Assert.Equal(32769m / 1024m, Requested(pages));
        Assert.Equal(32m, Read<decimal>(pages, "Allowance"));
        Assert.Equal(Read<long>(ceiling, "AdmissionId"), Read<long>(pages, "AdmissionId"));
        // The last admitted check sits exactly on the caller's allowance.
        Assert.Contains(probe.Events, e => Read<string>(e, "Site") == "MapPageBudget.MaxPageMetadataChecks.1" &&
            Read<string>(e, "LimitName") == "MaxPageReads" && !Read<bool>(e, "Refused") && Requested(e) == 32m);
    }

    [Fact]
    public void DeclaredProfile_ReachesResolutionPlacementsAndTheEditFinalizer()
    {
        MarkerFixture fixture = MarkerFixtures.WideBounds();
        MapResolveOptions declared = Declared();
        MapResolvedDocument resolved = MapNativeResolution.Resolve(fixture.Document, fixture.Assets, declared);
        MapEffectiveResources edited = MapDocumentEditFinalizer.Expand(fixture.Document, fixture.Assets, declared.QueryLimits, declared.ExpansionLimits);
        Assert.Empty(MapNativeResolution.ResolvePlacements(fixture.Document, fixture.Assets, declared, []));

        Assert.Equal(3, MapEffectiveAnnotations.Markers(resolved.EffectiveResources!).Count);
        Assert.Equal(Markers(resolved.EffectiveResources!), Markers(edited));
        Assert.Equal(resolved.EffectiveResources!.AnnotationDigest, edited.AnnotationDigest);
        Assert.Throws<MapDocumentException>(() => MapNativeResolution.ResolvePlacements(fixture.Document, fixture.Assets, MarkerFixtures.Options(), []));
        Assert.Throws<MapDocumentException>(() => MapDocumentEditFinalizer.Expand(fixture.Document, fixture.Assets));
    }

    [Fact]
    public void DeclaredExpansionProfile_ReachesEveryExpandingEntryPoint()
    {
        MarkerFixture fixture = MarkerFixtures.Stacked();
        fixture.Document.Prefabs = new(1, 1, [], []);
        // The stacked document holds sixteen patches, so one admitted patch refuses at the expansion preflight.
        MapResolveOptions tight = MarkerFixtures.Options() with { ExpansionLimits = new(MaxPatches: 1) };
        foreach (Action run in new Action[]
        {
            () => MapNativeResolution.Resolve(fixture.Document, fixture.Assets, tight),
            () => MapNativeResolution.ResolvePlacements(fixture.Document, fixture.Assets, tight, []),
            () => MapDocumentEditFinalizer.Expand(fixture.Document, fixture.Assets, tight.QueryLimits, tight.ExpansionLimits),
        })
            Assert.Equal("Prefab expansion preflight limit exceeded.", Assert.Throws<MapDocumentException>(run).Message);
    }

    [Fact]
    public void ResolvePlacements_SupportScopeTakesTheDeclaredQueryProfile()
    {
        MarkerFixture fixture = MarkerFixtures.Stacked();
        fixture.Document.Placements.Add(new MapPlacement
        { Id = "seat", Kind = "scenery", AssetId = "tree", X = 0.5f, Z = 0.5f, Scale = 1, SupportBinding = MarkerFixtures.SurfaceBinding("floor-2") });
        MapResolvedPlacement seated = Assert.Single(MapNativeResolution.ResolvePlacements(fixture.Document, fixture.Assets, MarkerFixtures.Options(), ["seat"]));
        Assert.Equal(20f, seated.Transform.Position.Y);
        // The captured document source visits two index pages for each of the sixteen surfaces in the seat's scope.
        var tight = MarkerFixtures.Options() with { QueryLimits = new(MaxPageReads: 31) };
        var error = Assert.Throws<MapDocumentException>(() => MapNativeResolution.ResolvePlacements(fixture.Document, fixture.Assets, tight, ["seat"]));
        Assert.Contains("Placement 'seat' support status CapacityExceeded", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PointReadout_TakesTheCallerProfile()
    {
        MarkerFixture fixture = MarkerFixtures.Stacked();
        MapEffectiveResources resources = MapPrefabResolver.Expand(fixture.Document, fixture.Assets);
        var point = new MapExactXz(MapExactValue.FromSingle(0.5f), MapExactValue.FromSingle(0.5f));
        MapSupportResult standard = MapNativePointReadout.SurfaceAt(resources, point, "floor-2");
        Assert.Equal(MapSupportStatus.Supported, standard.Status);
        Assert.Equal(standard.WorldY, MapNativePointReadout.SurfaceAt(resources, point, "floor-2", new MapQueryLimits(MaxCandidatePatches: 16)).WorldY);
        // The per-point scope holds the eight stacked floor and eight ceiling patches.
        Assert.Equal(MapSupportStatus.CapacityExceeded,
            MapNativePointReadout.SurfaceAt(resources, point, "floor-2", new MapQueryLimits(MaxCandidatePatches: 15)).Status);
    }

    [Fact]
    public void Profile_NeverChangesASuccessfulResolutionOrItsIdentity()
    {
        MarkerFixture fixture = MarkerFixtures.Stacked();
        fixture.Document.Placements.Add(new MapPlacement
        { Id = "seat", Kind = "scenery", AssetId = "tree", X = 0.5f, Z = 0.5f, Scale = 1, SupportBinding = MarkerFixtures.SurfaceBinding("floor-2") });
        MapResolveOptions standard = MarkerFixtures.Options(), permissive = standard with
        {
            QueryLimits = new(MaxCandidatePatches: 1024, MaxInspectedFaces: 65536, MaxPageReads: 4096, MaxRecordReads: 1024, MaxRecordDepth: 64),
            ExpansionLimits = new(MaxInstances: 131072, MaxPatches: 2097152, MaxRecords: 8388608),
        };
        Assert.NotEqual(standard, permissive);
        Assert.Equal(new MapQueryLimits(), standard.QueryLimits);
        Assert.Equal(new MapPrefabExpansionLimits(), standard.ExpansionLimits);

        MapResolvedDocument a = MapNativeResolution.Resolve(fixture.Document, fixture.Assets, standard);
        MapResolvedDocument b = MapNativeResolution.Resolve(fixture.Document, fixture.Assets, permissive);
        Assert.Equal(a.AuthoredHash, b.AuthoredHash);
        Assert.Equal(MapAuthoredIdentityV2.Compute(fixture.Document, fixture.Assets, standard),
            MapAuthoredIdentityV2.Compute(fixture.Document, fixture.Assets, permissive));
        Assert.Equal(a.Placements.Select(p => (p.PlacementId, p.Transform)), b.Placements.Select(p => (p.PlacementId, p.Transform)));
        Assert.Equal(a.EffectiveResources!.Digest, b.EffectiveResources!.Digest);
        Assert.Equal(a.EffectiveResources.AnnotationDigest, b.EffectiveResources.AnnotationDigest);
        Assert.Equal(Markers(a.EffectiveResources), Markers(b.EffectiveResources));
        Assert.Equal(a.EffectiveResources.PlacementSupports.Select(s => (s.PlacementId, s.Support.WorldY, s.Support.Face)),
            b.EffectiveResources.PlacementSupports.Select(s => (s.PlacementId, s.Support.WorldY, s.Support.Face)));
        Assert.Equal(MapNativeResolution.ResolvePlacements(fixture.Document, fixture.Assets, standard, ["seat"]).Select(p => p.Transform),
            MapNativeResolution.ResolvePlacements(fixture.Document, fixture.Assets, permissive, ["seat"]).Select(p => p.Transform));
    }

    [Fact]
    public void Options_RefuseANullProfile()
    {
        Assert.Throws<ArgumentNullException>(() => MarkerFixtures.Options() with { QueryLimits = null! });
        Assert.Throws<ArgumentNullException>(() => MarkerFixtures.Options() with { ExpansionLimits = null! });
    }

    static decimal Requested(object demand) => Read<decimal>(demand, "Requested");

    static (string Id, MapExactPoint Position, MapMarkerHeightProvenance? Provenance)[] Markers(MapEffectiveResources resources) =>
        MapEffectiveAnnotations.Markers(resources).Select(m => (m.Id, m.Position, m.HeightProvenance)).ToArray();
}
