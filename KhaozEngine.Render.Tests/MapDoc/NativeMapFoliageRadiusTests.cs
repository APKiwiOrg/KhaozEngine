using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeMapFoliageRadiusTests
{
    [Theory]
    [InlineData(4, 1, true)]
    [InlineData(0, 1, true)]
    [InlineData(4, 0, true)]
    [InlineData(0, 0, false)]
    public void ExplicitPlant_BetweenCoverAndPlacementRadiiSurvivesCoverCutoff(float radius, float density, bool generated)
    {
        using var fixture = Fixture();
        var state = State(fixture, Policy(fixture, 20, radius, density), generated);
        var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(state, new ResidencyDevice(), null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var set = ResidencyFixture.Draw(residency);
        var plant = Assert.Single(set.Draws, d => d.Resource.Key.OwnerId == "plant");
        var frame = plant.Foliage!.Value;
        Assert.Equal(new MapFoliageFrameOptions(20, 2, 1, 1, 1, .5f, GroundCoverFadeMode.HeightScale, 2), frame.Options);
        Assert.Equal(new Vector2(.25f, .75f), frame.State.WindDirection);
        Assert.Equal(.7f, frame.State.WindStrength);
        Assert.Equal(2, frame.State.WindSpeed);
        Assert.Equal(.1f, frame.State.WindSpatialFrequency);
        Assert.Equal(new Vector3(-10, 0, 0), plant.LocalFocus);
        Assert.Equal(1, Candidates(plant, Matrix4x4.Identity, 0));
        Assert.Equal(0, Candidates(plant with { LocalFocus = new(-21, 0, 0) }, Matrix4x4.Identity, 0));

        var cover = set.Draws.Where(d => d.Resource.Key.OwnerId != "plant").ToArray();
        if (!generated) { Assert.Empty(cover); return; }
        Assert.NotEmpty(cover);
        Assert.All(cover, draw =>
        {
            Assert.Equal(radius, draw.Foliage!.Value.Options.DrawRadius);
            Assert.Equal(density, draw.Foliage.Value.Options.QualityDensity);
            Assert.Equal(density, draw.Foliage.Value.Options.DistantDensity);
            var resource = Assert.IsType<MapFoliageResource>(set.Resources.Single(r => r.Reference == draw.Resource).Payload);
            int far = System.Array.FindIndex(resource.WorldTransforms.ToArray(), m => m.M41 > 5);
            Assert.True(far >= 0);
            var local = resource.WorldTransforms.Span[far];
            local.Translation -= new Vector3(draw.Anchor.X, draw.Anchor.Y, draw.Anchor.Z);
            Assert.Equal(0, Candidates(draw, local, resource.Ranks.Span[far]));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RadiusReplacement_ChangesOnlyItsFrameRadiusWithoutRegeneratingCover(bool changeCover)
    {
        using var fixture = Fixture();
        var state = State(fixture, Policy(fixture, 20, 4, 1), true);
        var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(state, new ResidencyDevice(), null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var original = residency.AcceptedPlans.Single();
        var generatedIdentities = original.Inventory.Foliage.GeneratedIdentities.ToArray();
        var cache = MapFoliageRenderCache.For(original.FoliageContext);
        long generated = cache.GenerationCalls;
        var before = ResidencyFixture.Draw(residency).Draws.ToArray();
        var policy = Policy(fixture, changeCover ? 20 : 40, changeCover ? 8 : 4, 1);
        Assert.NotEqual(state.Policy.Digest, policy.Digest);
        var transition = residency.RequestReplacement(state with { Policy = policy });
        Assert.Equal(state.Policy.Digest, transition.AcceptedWitness!.PolicyDigest);
        Assert.Equal(policy.Digest, transition.PendingWitness!.PolicyDigest);
        Assert.Equal(before, ResidencyFixture.Draw(residency).Draws);
        ResidencyFixture.Complete(residency, scheduler);

        var current = residency.AcceptedPlans.Single();
        Assert.Equal(policy.Digest, residency.CurrentTransition.AcceptedWitness!.PolicyDigest);
        Assert.Same(original.FoliageContext, current.FoliageContext);
        Assert.Equal(generated, cache.GenerationCalls);
        Assert.Equal(generatedIdentities, current.Inventory.Foliage.GeneratedIdentities);
        var draws = ResidencyFixture.Draw(residency).Draws;
        Assert.Equal(changeCover ? 20 : 40, Assert.Single(draws, d => d.Resource.Key.OwnerId == "plant").Foliage!.Value.Options.DrawRadius);
        Assert.All(draws.Where(d => d.Resource.Key.OwnerId != "plant"), d => Assert.Equal(changeCover ? 8 : 4, d.Foliage!.Value.Options.DrawRadius));
    }

    static ResidencyFixture Fixture()
    {
        var fixture = ResidencyFixture.Foliage();
        fixture.Document.Placements = [new() { Id = "plant", AssetId = "A", Kind = "plant", NumericId = 1, X = 10, Y = 0 }];
        fixture.Document.NumericIdHighWaterMark = 1;
        return fixture;
    }
    static MapRenderSourceState State(ResidencyFixture fixture, MapRenderPolicy policy, bool generated) =>
        fixture.State(ResidencyFixture.Window(-4, 36) with
        { Channels = MapSceneChannels.Placements | (generated ? MapSceneChannels.Foliage : 0) }) with
        { Policy = policy };
    static MapRenderPolicy Policy(ResidencyFixture fixture, float placementRadius, float coverRadius, float density)
    {
        var options = new GroundCoverRenderOptions
        {
            UseGpuBatches = true,
            FadeMode = GroundCoverFadeMode.HeightScale,
            DrawRadius = coverRadius,
            DensityRadius = 2,
            FadeBandWidth = 1,
            QualityDensity = density,
            DistantDensity = density,
            InstanceFadeBandWidth = .5f,
            WindFadeBladePixels = 2,
            WindDirection = new(.25f, .75f),
            WindStrength = .7f,
            WindSpeed = 2,
            WindSpatialFrequency = .1f
        };
        var foliage = new NativeFoliagePolicy(MapFrozenFoliageOptions.Capture(options), fixture.Closure.Assets.ToDictionary(a => a.Id,
            a => new KhaozEngine.MapDoc.Foliage.MapFoliageModelExtent(a.Id, a.RenderBounds, a.SourceUnitsToMetres, 0)));
        var policy = ResidencyFixture.Policy(placementRadius, foliage, new HashSet<string> { "A" });
        var placements = new MapPlacementPolicy([new("animated", new HashSet<string> { "A" }, [], 0, 2, 0, 0, 0, true)], placementRadius);
        return new(placements, policy.AnimatedAssets, foliage, policy.Water, policy.MaterialResourceIds, policy.TerrainMaterials);
    }
    static int Candidates(MapDrawRecord draw, Matrix4x4 transform, float rank)
    {
        var layout = FoliagePatchLayout.Build([new FoliageInstance(new MeshHandle(1), transform, rank)], _ => new(Vector3.Zero, Vector3.One));
        var options = draw.Foliage!.Value.Options;
        return Assert.Single(layout.Patches).CandidateCount(layout.Instances, draw.LocalFocus, new FoliageRenderSettings
        {
            DrawRadius = options.DrawRadius,
            DensityRadius = options.DensityRadius,
            FadeBandWidth = options.FadeBandWidth,
            QualityDensity = options.QualityDensity,
            DistantDensity = options.DistantDensity
        });
    }
}
