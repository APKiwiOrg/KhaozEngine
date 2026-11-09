using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapAcquisitionDependencyTransferTests
{
    [Fact]
    public void AggregateOwner_PreservesTheContextForALaterBorrower()
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        var patch = PrefabFixtures.Patch("floor", 32, 32, 0);
        fixture.Document.Surfaces.Refs.Add(PrefabFixtures.Surface("floor", MapSurfaceRole.SupportFloor));
        fixture.Document.Surfaces.Patches.Add(patch.Key, patch);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 2_000_000));
        using var active = admission.Enter();
        MapEffectiveResolutionProduct product;
        MapPreparationReservation aggregate;
        long expected;
        using (var collector = admission.BeginPreparation())
        {
            product = MapEffectiveResolutionKernel.Resolve(MapEffectiveResolutionInput.FromDocument(fixture.Document), fixture.Assets);
            var first = product.Acquire(MapSceneScopeTests.Window(1024, 1025).Surfaces);
            var inventory = new MapAcquisitionInventoryOracle();
            expected = inventory.Product(product) + inventory.View(first);
            aggregate = collector.Complete(expected);
        }
        using (aggregate)
        {
            var later = product.Acquire(MapSceneScopeTests.Window(1024, 1025).Surfaces);
            using var reader = later.PreparationOwnership!;
            long contextBytes = new MapAcquisitionInventoryOracle().Context(product.CapturedContext);
            expected += new MapAcquisitionInventoryOracle().View(later) - contextBytes;
            Assert.Equal(expected, admission.RetainedBytes);
            aggregate.Dispose();
            Assert.Equal(new MapAcquisitionInventoryOracle().View(later), admission.RetainedBytes);
            reader.Dispose();
            Assert.Equal(0, admission.RetainedBytes);
            Assert.Equal(0, admission.WorkspaceBytes);
        }
    }
}
