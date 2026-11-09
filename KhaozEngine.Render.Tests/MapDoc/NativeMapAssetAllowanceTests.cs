using System.Linq;
using System.Threading;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeMapAssetAllowanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedSet_EnforcesAggregatePartsAcrossFreshAndCachedPieces(bool cached)
    {
        using var fixture = ResidencyFixture.Placement(NativeRenderAssetFixtures.Create("distant"), true);
        var state = fixture.State(ResidencyFixture.Window());
        var budget = new GltfDecodeBudget(new(MaxPeakBytes: 67108864));
        int parts;
        using (var measured = MapRenderAssets.LoadSelected(state.Closure, ["prop-a"], state.Extents, budget).Assets!)
            parts = measured.ById["prop-a"].Lods.Sum(l => l.Parts.Count);
        var limits = new MapRenderAssetLimits(MaxParts: parts);
        using var cache = new MapRenderAssetCache();
        if (cached)
            foreach (string id in new[] { "prop-a", "prop-1" })
            {
                var warm = cache.Acquire(state.Closure, [id], [], state.Extents, budget, CancellationToken.None, assetLimits: limits);
                Assert.Equal(MapRenderAssetStatus.Complete, warm.Status);
                warm.Input!.Dispose();
            }
        long before = budget.OwnedBytes;
        var result = cache.Acquire(state.Closure, ["prop-a", "prop-1"], [], state.Extents, budget, CancellationToken.None, assetLimits: limits);
        Assert.Equal(MapRenderAssetStatus.CapacityExceeded, result.Status);
        Assert.Null(result.Input);
        Assert.Equal("MaxParts", result.Capacity!.LimitName);
        Assert.Equal(parts, result.Capacity.Allowance);
        Assert.True(result.Capacity.Requested > parts);
        Assert.Equal(before, budget.OwnedBytes);
    }

    [Fact]
    public void TightenedAssetProfile_RefusesAnAlreadyCachedInputSet()
    {
        using var fixture = ResidencyFixture.Placement();
        var state = fixture.State(ResidencyFixture.Window());
        var budget = new GltfDecodeBudget(new());
        using var cache = new MapRenderAssetCache();
        var first = cache.Acquire(state.Closure, ["prop-a"], [], state.Extents, budget, CancellationToken.None, assetLimits: new());
        Assert.Equal(MapRenderAssetStatus.Complete, first.Status);
        using var input = first.Input!;
        var result = cache.Acquire(state.Closure, ["prop-a"], [], state.Extents, budget, CancellationToken.None,
            assetLimits: new(MaxDecodedBytes: 1));
        Assert.Equal(MapRenderAssetStatus.CapacityExceeded, result.Status);
        Assert.Equal("MaxDecodedBytes", result.Capacity!.LimitName);
        Assert.Equal(1, result.Capacity.Allowance);
        Assert.True(result.Capacity.Requested > 1);
        Assert.Contains("prop-a", input.Assets.ById.Keys);
    }

    [Fact]
    public void ExplicitDecodePeak_RetainsItsOwnAllowanceBesidePreparationMemory()
    {
        using var fixture = ResidencyFixture.Placement();
        var state = fixture.State(ResidencyFixture.Window());
        var residencyLimits = new MapRenderBudgets();
        var allowance = new MapRenderDecodeAllowance(new(new()), new(residencyLimits), residencyLimits, new(MaxPeakBytes: 1));
        using var cache = new MapRenderAssetCache();
        var result = cache.Acquire(state.Closure, [], [], state.Extents, allowance.Budget);
        Assert.Equal(MapRenderAssetStatus.CapacityExceeded, result.Status);
        Assert.Equal(new MapSceneCapacity("MaxPeakBytes", 1024, 1), allowance.Resolve(result.Capacity!));
    }
}
