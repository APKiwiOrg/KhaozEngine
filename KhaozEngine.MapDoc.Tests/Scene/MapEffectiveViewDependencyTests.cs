using System.Linq;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapEffectiveViewDependencyTests
{
    [Fact]
    public void SmallEffectiveView_RefusesItsLargerBorrowedContextInATightChild()
    {
        using var fixture = Fixture();
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 2_000_000));
        using var active = admission.Enter();
        var product = MapEffectiveResolutionKernel.Resolve(MapEffectiveResolutionInput.FromDocument(fixture.Document), fixture.Assets);
        using var owner = product.PreparationOwnership!;
        long before = new MapAcquisitionInventoryOracle().Product(product);
        long contextBytes = new MapAcquisitionInventoryOracle().Context(product.CapturedContext);
        using (admission.LimitPreparation(contextBytes - 1, "view-child"))
        {
            var error = Assert.Throws<MapSceneCapacityException>(() => View(product, false));
            Assert.Equal("view-child", error.Capacity.LimitName);
            Assert.Equal(contextBytes - 1, error.Capacity.Allowance);
            Assert.True(error.Capacity.Requested >= contextBytes);
            Assert.Equal(contextBytes - 1, admission.RemainingPreparedBytes);
        }
        Assert.Equal(before, admission.RetainedBytes);
        Assert.Equal(before, owner.Bytes);
        Assert.Equal(0, admission.WorkspaceBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExportedView_KeepsOnlyItsContextAfterTheProductCollectorRetires(bool complete)
    {
        using var fixture = Fixture();
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 2_000_000));
        using var active = admission.Enter();
        using var consumer = admission.BeginPreparation();
        MapScopedSurfaces view;
        using (var productCollector = admission.BeginPreparation())
        {
            var product = MapEffectiveResolutionKernel.Resolve(MapEffectiveResolutionInput.FromDocument(fixture.Document), fixture.Assets);
            long productBytes = new MapAcquisitionInventoryOracle().Product(product);
            long contextBytes = new MapAcquisitionInventoryOracle().Context(product.CapturedContext);
            view = View(product, complete);
            long viewBytes = new MapAcquisitionInventoryOracle().View(view);
            Assert.Equal(productBytes + viewBytes - contextBytes, admission.RetainedBytes);
            productCollector.Export(view.PreparationOwnership!, consumer);
        }
        long expected = new MapAcquisitionInventoryOracle().View(view);
        Assert.Equal(expected, admission.RetainedBytes);
        Assert.Equal(expected, view.PreparationOwnership!.Bytes);
        Assert.Single(view.EffectiveContext!.Surfaces.Patches);
        Assert.NotEmpty(view.EffectiveContext.Surfaces.Patches.Values.Single().Heights);
        view.PreparationOwnership.Dispose();
        view.PreparationOwnership.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
    }

    [Fact]
    public void RefusedBorrow_ReportsTheTighterAncestorAndUnwindsOnlyItsAttempt()
    {
        using var fixture = Fixture();
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 2_000_000));
        using var active = admission.Enter();
        var product = MapEffectiveResolutionKernel.Resolve(MapEffectiveResolutionInput.FromDocument(fixture.Document), fixture.Assets);
        using var owner = product.PreparationOwnership!;
        long before = new MapAcquisitionInventoryOracle().Product(product);
        using (admission.LimitPreparation(2000, "tight-ancestor"))
        using (admission.LimitPreparation(4000, "wide-child"))
        {
            var error = Assert.Throws<MapSceneCapacityException>(() => View(product, false));
            Assert.Equal("tight-ancestor", error.Capacity.LimitName);
            Assert.Equal(2000, error.Capacity.Allowance);
            Assert.Equal(2000, admission.RemainingPreparedBytes);
        }
        Assert.Equal(before, admission.RetainedBytes);
        Assert.Equal(before, owner.Bytes);
        Assert.Equal(0, admission.WorkspaceBytes);
    }

    [Fact]
    public void NestedBorrowers_KeepTheContextChargedAfterTheEarlierViewReleases()
    {
        using var fixture = Fixture();
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 2_000_000));
        using var active = admission.Enter();
        var product = MapEffectiveResolutionKernel.Resolve(MapEffectiveResolutionInput.FromDocument(fixture.Document), fixture.Assets);
        using var owner = product.PreparationOwnership!;
        long before = new MapAcquisitionInventoryOracle().Product(product);
        long contextBytes = new MapAcquisitionInventoryOracle().Context(product.CapturedContext);
        const long allowance = 1_000_000;
        using (admission.LimitPreparation(allowance, "outer-view"))
        {
            var earlier = View(product, false);
            long earlierBytes = new MapAcquisitionInventoryOracle().View(earlier);
            Assert.Equal(earlierBytes, allowance - admission.RemainingPreparedBytes);
            MapScopedSurfaces later;
            using (admission.LimitPreparation(allowance, "inner-view"))
            {
                later = View(product, false);
                long laterBytes = new MapAcquisitionInventoryOracle().View(later);
                Assert.Equal(before + earlierBytes + laterBytes - 2 * contextBytes, admission.RetainedBytes);
                earlier.PreparationOwnership!.Dispose();
            }
            long expected = new MapAcquisitionInventoryOracle().View(later);
            Assert.Equal(expected, allowance - admission.RemainingPreparedBytes);
            later.PreparationOwnership!.Dispose();
            Assert.Equal(allowance, admission.RemainingPreparedBytes);
        }
        Assert.Equal(before, admission.RetainedBytes);
        owner.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }

    static MapScopedSurfaces View(MapEffectiveResolutionProduct product, bool complete) => complete
        ? MapScopedSurfaces.CompleteEffective(product.CapturedContext)
        : product.Acquire(MapSceneScopeTests.Window(1024, 1025).Surfaces);

    static NativeSceneFixture Fixture()
    {
        var fixture = NativeSceneFixtures.Create("empty");
        var patch = PrefabFixtures.Patch("floor", 32, 32, 0);
        fixture.Document.Surfaces.Refs.Add(PrefabFixtures.Surface("floor", MapSurfaceRole.SupportFloor));
        fixture.Document.Surfaces.Patches.Add(patch.Key, patch);
        return fixture;
    }
}
