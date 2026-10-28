using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Markers;
using Xunit;
using static KhaozEngine.Tests.CapacityDemandProbe;

namespace KhaozEngine.Tests.MapDoc.Physics;

[CollectionDefinition("resolve-limits-demand", DisableParallelization = true)]
public sealed class ResolveLimitsDemandCollection;

/// <summary>The physics world builder resolves a native document under the caller's resolver-2 profile, as the scene
/// residency path does. Serial because the demand observer is process-wide.</summary>
[Collection("resolve-limits-demand")]
public sealed class MapResolveLimitsWorldTests
{
    [Fact]
    public void WideBoundSlots_BuildUnderTheDeclaredProfileAndRefuseAtTheDefault()
    {
        // Without a prefab section the builder skips whole-view space coverage, which over the wide cells only costs time.
        MarkerFixture fixture = MarkerFixtures.WideBounds();
        fixture.Document.Prefabs = null;
        using (var probe = new CapacityDemandProbe())
        {
            var error = Assert.Throws<MapDocumentException>(() => Build(fixture, MarkerFixtures.Options()));
            Assert.Contains("support status CapacityExceeded", error.Message, StringComparison.Ordinal);
            Assert.Contains(probe.Events, e => Read<string>(e, "Site") == "MapPageBudget.MaxPageMetadataChecks.1" && Read<bool>(e, "Refused") &&
                Read<string>(e, "LimitName") == "MaxPageMetadataChecks" && Read<decimal>(e, "Allowance") == 32769m);
            Assert.Contains(probe.Events, e => Read<string>(e, "Site") == "MapPageBudget.MaxPageMetadataChecks.2" && Read<bool>(e, "Refused") &&
                Read<string>(e, "LimitName") == "MaxPageReads" && Read<decimal>(e, "Requested") == 33m && Read<decimal>(e, "Allowance") == 32m);
        }

        MapBuiltWorld world = Build(fixture, MarkerFixtures.Options() with { QueryLimits = new(MaxPageReads: 64) });
        Assert.Equal(["stacked-disabled", "stacked-space", "stacked-surface"], world.Markers.Select(m => m.Id));
        Assert.All(world.Markers, m => Assert.NotNull(m.HeightProvenance!.WorldY));
    }

    [Fact]
    public void Profile_NeverEntersTheBuiltWorld()
    {
        MarkerFixture fixture = MarkerFixtures.Stacked();
        MapBuiltWorld standard = Build(fixture, MarkerFixtures.Options());
        MapBuiltWorld permissive = Build(fixture, MarkerFixtures.Options() with
        {
            QueryLimits = new(MaxCandidatePatches: 1024, MaxPageReads: 4096, MaxRecordReads: 1024),
            ExpansionLimits = new(MaxInstances: 131072),
        });
        Assert.Equal(standard.AuthoredHash, permissive.AuthoredHash);
        Assert.Equal(standard.BuildHash, permissive.BuildHash);
        Assert.Equal(standard.AnnotationDigest, permissive.AnnotationDigest);
        Assert.Equal(standard.Markers.Select(m => (m.Id, m.Position, m.HeightProvenance)), permissive.Markers.Select(m => (m.Id, m.Position, m.HeightProvenance)));
        Assert.Equal(standard.Terrain.Chunks.Select(c => c.Digest), permissive.Terrain.Chunks.Select(c => c.Digest));

        // R-RES-1a. The terrain and surface witnesses name the request, budgets included, so they differ only by it.
        MapBuiltWorld same = Build(fixture, MarkerFixtures.Options() with { QueryLimits = new MapQueryLimits(), ExpansionLimits = new MapPrefabExpansionLimits() });
        Assert.Equal(Witness(standard.Terrain.Witness), Witness(permissive.Terrain.Witness));
        Assert.NotEqual(standard.Terrain.Witness.ScopedDigest, permissive.Terrain.Witness.ScopedDigest);
        Assert.NotEqual(standard.Surfaces.Witness.ScopeDigest, permissive.Surfaces.Witness.ScopeDigest);
        Assert.Equal(standard.Terrain.Witness.ScopedDigest, same.Terrain.Witness.ScopedDigest);
        Assert.Equal(standard.Surfaces.Witness.ScopeDigest, same.Surfaces.Witness.ScopeDigest);
        Assert.Equal(standard.BuildHash, same.BuildHash);
    }

    [Fact]
    public void CoverageRefinement_FollowsTheDeclaredProfile()
    {
        // The complete view of 289 patches needs more than the default 256 candidate patches, so both runs declare them
        // and differ only in refinement.
        MarkerFixture fixture = MarkerFixtures.ContextGrid();
        var patches = MarkerFixtures.Options() with { QueryLimits = new(MaxCandidatePatches: 512) };
        var error = Assert.Throws<MapDocumentException>(() => Build(fixture, patches));
        Assert.Contains("refinement capacity: footprint 'grid-0-0' (context patches)", error.Message, StringComparison.Ordinal);
        var declared = patches with { QueryLimits = patches.QueryLimits with { Refinement = new(MaxContextPatches: 512) } };
        MapBuiltWorld world = Build(fixture, declared);
        Assert.Equal(Build(fixture, declared).BuildHash, world.BuildHash);
        Assert.Single(world.Markers);
    }

    [Fact]
    public void IncompleteCompleteView_RefusesAsCapacityBeforeCoverage()
    {
        // At the default 256 candidate patches the 289-patch complete view is incomplete. The build refuses as a document
        // capacity failure, never by handing a partial view to coverage or terrain.
        MarkerFixture fixture = MarkerFixtures.ContextGrid();
        var error = Assert.Throws<MapDocumentException>(() => Build(fixture, MarkerFixtures.Options()));
        Assert.Contains("Native world surfaces CapacityExceeded", error.Message, StringComparison.Ordinal);
    }

    static (string SnapshotId, WorldFrame Frame, int QueryPolicyVersion, bool Complete) Witness(MapReadWitness witness) =>
        (witness.SnapshotId, witness.Frame, witness.QueryPolicyVersion, witness.Complete);

    static MapBuiltWorld Build(MarkerFixture fixture, MapResolveOptions options) =>
        MapWorldBuilder.Build(fixture.Document, fixture.Assets, new(options, "public-resolve-limits/1", new()));
}
