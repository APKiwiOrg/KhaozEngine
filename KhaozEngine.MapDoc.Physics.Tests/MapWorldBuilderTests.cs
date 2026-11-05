using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Physics;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

public class MapWorldBuilderTests
{
    [Fact]
    public void TwoIndependentHeads_BuildIdenticalWorlds()
    {
        var a = NativeWorldFixtures.BuildStackedCave();
        var b = NativeWorldFixtures.BuildStackedCave();
        Assert.Equal(a.BuildHash, b.BuildHash);
        Assert.Equal(a.AuthoredHash, b.AuthoredHash);
        Assert.Equal(a.Statics.Select(s => (s.OwnerId, s.Digest, s.Position, s.Orientation)), b.Statics.Select(s => (s.OwnerId, s.Digest, s.Position, s.Orientation)));
    }

    [Fact]
    public void InvalidInputs_Refuse()
    {
        Assert.Contains("partial", Assert.Throws<MapDocumentException>(() => NativeWorldFixtures.BuildPartialWindow()).Message);
        Assert.Equal(new[] { "crate", "far-crate" },
            NativeWorldFixtures.BuildPartialWindow(loadWholeWorld: true).Document.Placements.Select(p => p.PlacementId));

        var resolver = Assert.Throws<MapDocumentException>(() => NativeWorldFixtures.BuildWithResolverMismatch());
        Assert.Equal(MapDocumentRefusal.ResolverContractMismatch, resolver.Reason);
        Assert.Equal(1, resolver.PayloadVersion);
        Assert.Equal(2, resolver.ResolverVersion);
        Assert.Equal(1, resolver.OptionsResolverVersion);
        Assert.Equal(2, resolver.ExpectedResolverVersion);
        var matching = NativeWorldFixtures.Crate();
        MapBuiltWorld matched = MapWorldBuilder.Build(matching.Document, matching.Assets, NativeWorldFixtures.Options());
        Assert.True(matched.IsNative);
        Assert.Equal("crate", Assert.Single(matched.Document.Placements).PlacementId);

        Assert.Contains("legacy support height", Assert.Throws<MapDocumentException>(() => NativeWorldFixtures.BuildLegacyWithoutHeight()).Message);
        var legacy = NativeWorldFixtures.LegacyCrate();
        MapBuiltWorld supported = MapWorldBuilder.Build(legacy.Document, legacy.Assets,
            NativeWorldFixtures.Options() with { Resolve = NativeWorldFixtures.LegacyResolveOptions, LegacySupportHeight = (_, _) => 0f });
        Assert.False(supported.IsNative);
        Assert.Equal("crate", Assert.Single(supported.Document.Placements).PlacementId);

        Assert.Throws<MapDocumentException>(() => NativeWorldFixtures.BuildWithPlacementScale(float.NaN));
        Assert.Equal(1f, Assert.Single(NativeWorldFixtures.BuildWithPlacementScale(1f).Document.Placements).Transform.Scale);
        Assert.Throws<MapDocumentException>(() => NativeWorldFixtures.BuildWithPlacementScale(0f));
        Assert.Equal(0.5f, Assert.Single(NativeWorldFixtures.BuildWithPlacementScale(0.5f).Document.Placements).Transform.Scale);
        Assert.Contains("1,000,000", Assert.Throws<MapDocumentException>(() => NativeWorldFixtures.BuildWithPlacementAt(1_000_001f)).Message);
        Assert.Equal(1_000_000f,
            Assert.Single(NativeWorldFixtures.BuildWithPlacementAt(1_000_000f).Document.Placements).Transform.Position.X);
    }

    [Fact]
    public void BuildHash_FollowsPolicyIdentityAndChunkPolicy()
    {
        var f = NativeWorldFixtures.StackedCave();
        string baseline = MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options()).BuildHash;
        Assert.NotEqual(baseline, MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options() with { ConsumerPolicyIdentity = "other" }).BuildHash);
        Assert.NotEqual(baseline, MapWorldBuilder.Build(f.Document, f.Assets, NativeWorldFixtures.Options() with { Chunks = new MapTerrainChunkPolicy(512) }).BuildHash);
    }

    [Fact]
    public void FeatureQueryLimits_AreReportedNotRefused()
    {
        var world = NativeWorldFixtures.BuildTreeAndWideCompound();
        Assert.Contains(new MapStaticDiagnostic("tree", MapFeatureQuerySupport.CurvedLeaf), world.Diagnostics);
        Assert.Contains(new MapStaticDiagnostic("parapet-70", MapFeatureQuerySupport.LeafCapacity), world.Diagnostics);
        Assert.Contains(new MapStaticDiagnostic("long-wall", MapFeatureQuerySupport.LocalExtent), world.Diagnostics);
        Assert.DoesNotContain(world.Diagnostics, d => d.OwnerId == "crate");
        Assert.DoesNotContain(world.Diagnostics, d => d.OwnerId == "spread-boxes");
    }

    [Fact]
    public void LegacyWorld_KeepsAnalyticTerrainAndIdentifiesItsSculpt()
    {
        Func<float, float, float> height = (_, _) => 0f;
        MapBuiltWorld world = NativeWorldFixtures.BuildLegacyCrateWithSculpt(1.5f, height);
        Assert.False(world.IsNative);
        Assert.Empty(world.Terrain.Chunks);
        Assert.Same(height, world.LegacySupportHeight);
        // Tile (0, 0) at 0.5 m cells reaches one cell past its first and last cell centres.
        Assert.Equal(new MapResolvedBounds(-0.5f, -0.5f, 16f, 16f), world.LegacySculptTiles[0].Footprint);

        MapBuiltWorld edited = NativeWorldFixtures.BuildLegacyCrateWithSculpt(2f, height);
        Assert.NotEqual(world.LegacyTerrainIdentity, edited.LegacyTerrainIdentity);
        Assert.Equal(world.LegacyTerrainBlockDigest, edited.LegacyTerrainBlockDigest);
    }
}
