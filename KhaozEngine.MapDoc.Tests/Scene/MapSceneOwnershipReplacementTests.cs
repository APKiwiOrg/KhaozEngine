using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapSceneOwnershipReplacementTests
{
    [Fact]
    public void DependencyFailure_PreservesEarlierSnapshotsAndExactWindowFinding()
    {
        using var fixture = NativeSceneFixtures.Create("terrain-wall");
        var source = NativeSceneFixtures.Open(fixture, false);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 2_000_000));
        var diagnostics = admission.ObserveAcquisition();
        using var active = admission.Enter();
        var first = MapSceneAcquisition.Run(source, MapSceneScopeTests.Window(), admission).Scene!;
        long firstBytes = new MapAcquisitionInventoryOracle().Scene(first);
        var second = first.AcquireDependencies(MapSceneScopeTests.Window(0, 32)).Scene!;
        long secondBytes = new MapAcquisitionInventoryOracle().Scene(second);
        Assert.Equal(firstBytes + secondBytes, admission.RetainedBytes);
        Assert.NotSame(first.SurfaceView, second.SurfaceView);
        second.PreparationOwnership!.Dispose();
        Assert.Equal(firstBytes, admission.RetainedBytes);
        string identity = first.SurfaceView.Identity.Digest;
        using (admission.LimitPreparation(0, "MaxPlanBytes"))
        {
            var refused = first.AcquireDependencies(MapSceneScopeTests.Window(-1, 65));
            Assert.Null(refused.Scene);
            Assert.Equal(new MapSceneCapacity("MaxPlanBytes", 140, 0), refused.Capacity);
            Assert.Contains("MaxPlanBytes", Assert.Single(refused.Findings));
        }
        Assert.Equal(identity, first.SurfaceView.Identity.Digest);
        Assert.Equal(firstBytes, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(2, diagnostics.Callers[MapAcquisitionCaller.Dependency].Scenes);
        first.PreparationOwnership!.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }

    [Fact]
    public void Coverage_KeepsDistinctSnapshotsAndDeduplicatesOnlyExactRequests()
    {
        using var fixture = NativeSceneFixtures.Create("terrain-wall");
        var source = NativeSceneFixtures.Open(fixture, false);
        var first = MapSceneScopeTests.Window();
        var second = MapSceneScopeTests.Window(0, 32);
        using var lease = MapRequiredDomainCoverage.Acquire(source, [first, first, second]);
        Assert.Equal(MapSceneStatus.CompleteForScope, lease.Status);
        Assert.Equal(2, lease.Scenes.Count);
        long expected = 3 * 128 + lease.Scenes.Sum(s => new MapAcquisitionInventoryOracle().Scene(s));
        Assert.Equal(expected, lease.PreparationOwnership!.Bytes);
        lease.Dispose(); lease.Dispose();
        Assert.Equal(0, lease.PreparationOwnership.Bytes);
    }

    [Fact]
    public void Coverage_LaterScopeRefusalPublishesNoPartialLease()
    {
        using var fixture = NativeSceneFixtures.Create("terrain");
        using var document = MapDocumentSource.FromDocument(fixture.Document);
        var source = MapSceneSource.Capture(document, fixture.Assets, new(MaxPatchScopes: 2)).Source!;
        using var lease = MapRequiredDomainCoverage.Acquire(source, [MapSceneScopeTests.Window(), MapSceneScopeTests.Window(0, 32)]);
        Assert.Equal(MapSceneStatus.CapacityExceeded, lease.Status);
        Assert.Empty(lease.Scenes);
        Assert.Null(lease.PreparationOwnership);
        Assert.Equal(new MapSceneCapacity("MaxPatchScopes", 3, 2), lease.Capacity);
    }
}
