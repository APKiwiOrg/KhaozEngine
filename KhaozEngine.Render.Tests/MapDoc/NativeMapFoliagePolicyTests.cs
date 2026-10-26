using System.Linq;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeMapFoliagePolicyTests
{
    [Fact]
    public void Distribution_PreservesEveryDeclaredAllowanceAboveItsDefault()
    {
        var defaults = new MapFoliageLimits();
        var declared = new MapFoliageLimits(defaults.MaxCandidates + 1, defaults.MaxSamples + 1, defaults.MaxPrepareScopes + 1,
            defaults.MaxFaces + 1, defaults.MaxRuleCandidates + 1, defaults.MaxMaskCandidates + 1, defaults.MaxQueryChunks + 1,
            defaults.MaxQuerySamples + 1, defaults.MaxQueryRetainedBytes + 1, defaults.MaxDifferentialCandidates + 1,
            defaults.MaxChangeRows + 1, defaults.MaxComparisonPreparedChunks + 1, defaults.MaxComparisonRetainedBytes + 1);
        Assert.Equal(declared, MapFoliageDistribution.Limits(declared));
        Assert.Equal(defaults, MapFoliageDistribution.Limits(null));
    }

    [Theory]
    [InlineData(67108864L)]
    [InlineData(134217728L)]
    public void Cache_RefusesTheFirstAllocationWithoutPricingTheWholeCandidateLattice(long allowance)
    {
        using var fixture = ResidencyFixture.Foliage(.09f);
        var state = fixture.FoliageState();
        var context = MapFoliageGenerationContext.CaptureWindow(state.Source, state.Windows[0]).Context!;
        var layer = Assert.Single(((IMapFoliageInputs)context).Foliage!.Layers);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 0));
        var cache = MapFoliageRenderCache.For(context);
        var error = Assert.Throws<MapSceneCapacityException>(() => cache.Get(layer.Id, default,
            new(MaxQueryRetainedBytes: allowance), admission, null));
        Assert.Equal("MaxPreparedBytes", error.Capacity.LimitName);
        Assert.Equal(0, error.Capacity.Allowance);
        Assert.InRange(error.Capacity.Requested, 1, 4096);
        Assert.Equal(0, cache.GenerationCalls);
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, admission.RetainedBytes);
    }

    [Fact]
    public void Cache_RefusesItsMinimumHeaderBeforeOffRasterGeneration()
    {
        using var fixture = ResidencyFixture.Foliage(4);
        var state = fixture.FoliageState();
        var context = MapFoliageGenerationContext.CaptureWindow(state.Source, state.Windows[0]).Context!;
        var layer = Assert.Single(((IMapFoliageInputs)context).Foliage!.Layers);
        var outside = context.ChunksFor(layer.Id).First(c => MapFoliageDistribution.KnownOffRaster(layer, MapFoliageDistribution.Rectangle(layer, c)));
        var cache = MapFoliageRenderCache.For(context);
        var admission = new MapSceneAdmission(new());
        var error = Assert.Throws<MapSceneCapacityException>(() => cache.Get(layer.Id, outside,
            new(MaxQueryRetainedBytes: 255), admission, null));
        Assert.Equal("MaxQueryRetainedBytes", error.Capacity.LimitName);
        Assert.Equal(255, error.Capacity.Allowance);
        Assert.True(error.Capacity.Requested > 255);
        Assert.Equal(0, cache.GenerationCalls);
        Assert.Equal(0, admission.RetainedBytes);
    }

    [Fact]
    public void Cache_DoesNotReserveAnotherCandidateEnvelopeAfterAnEmptyBatch()
    {
        using var fixture = ResidencyFixture.Foliage(.09f);
        var state = fixture.FoliageState();
        var context = MapFoliageGenerationContext.CaptureWindow(state.Source, state.Windows[0]).Context!;
        var layer = Assert.Single(((IMapFoliageInputs)context).Foliage!.Layers);
        var cache = MapFoliageRenderCache.For(context);
        var outside = context.ChunksFor(layer.Id).First(c => MapFoliageDistribution.KnownOffRaster(layer, MapFoliageDistribution.Rectangle(layer, c)));
        _ = cache.Get(layer.Id, outside, new());
        var error = Assert.Throws<MapSceneCapacityException>(() => cache.Get(layer.Id, default, new(), new(new(MaxPreparedBytes: 0)), null));
        Assert.Equal("MaxPreparedBytes", error.Capacity.LimitName);
        Assert.InRange(error.Capacity.Requested, 1, 4096);
        Assert.Equal(1, cache.GenerationCalls);
        Assert.Equal(1, cache.Count);
        cache.Clear();
    }
}
