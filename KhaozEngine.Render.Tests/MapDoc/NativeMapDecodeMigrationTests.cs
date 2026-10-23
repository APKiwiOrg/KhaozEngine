using System.Linq;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeMapDecodeMigrationTests
{
    [Fact]
    public void SharedDecoder_EmptySelectionDoesNotBorrowEarlierOwnership()
    {
        using var fixture = ResidencyFixture.Placement();
        var state = fixture.State(ResidencyFixture.Window());
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 200_000));
        using var active = admission.Enter();
        using var cache = new MapRenderAssetCache();
        var budget = new GltfDecodeBudget(new());
        using var first = cache.Acquire(state.Closure, ["prop-a"], [], state.Extents, budget).Input!;
        long before = admission.RetainedBytes;
        // This unrelated live payload belongs to the same decoder, but is absent from this selection.
        Assert.True(budget.TryReserve(64_000, 0));
        using var earlier = admission.Workspace(); earlier.Reserve(64_000);
        using var owned = earlier.Transfer(64_000);
        using (admission.LimitPreparation(8192, "MaxPlanBytes"))
        {
            var result = cache.Acquire(state.Closure, [], [], state.Extents, budget);
            Assert.True(result.Status == MapRenderAssetStatus.Complete, result.Detail);
            using var empty = result.Input!;
            Assert.Empty(empty.Assets.ById);
            Assert.Equal(before + 64_000 + empty.RetainedBytes, admission.RetainedBytes);
        }
        Assert.Equal(before + 64_000, admission.RetainedBytes);
        budget.ReleaseOwned(64_000);
    }
}
