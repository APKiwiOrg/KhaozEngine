using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Physics;
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
            var refused = probe.Events.Where(e => Read<string>(e, "Site") == "MapPageBudget.MaxPageMetadataChecks.1" && Read<bool>(e, "Refused")).ToArray();
            Assert.Contains(refused, e => Read<string>(e, "LimitName") == "MaxPageMetadataChecks" && Read<decimal>(e, "Allowance") == 32769m);
            Assert.Contains(refused, e => Read<string>(e, "LimitName") == "MaxPageReads" && Read<decimal>(e, "Allowance") == 32m);
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
    }

    static MapBuiltWorld Build(MarkerFixture fixture, MapResolveOptions options) =>
        MapWorldBuilder.Build(fixture.Document, fixture.Assets, new(options, "public-resolve-limits/1", new()));
}
