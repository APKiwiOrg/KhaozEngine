using System.Linq;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeMapPreparationOwnershipTests
{
    [Fact]
    public void StandalonePlacements_ReleaseDependencyAndConstructionInventory()
    {
        var closure = NativeRenderAssetFixtures.Placements();
        var scene = NativeMapPlacementTests.Scene(closure);
        using var assets = NativeMapPlacementTests.Assets(closure);
        var policy = NativeMapPlacementTests.Policy(true);
        var routing = MapPlacementRouting.Create(scene, policy, new System.Collections.Generic.HashSet<string>()).Routing!;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 100_000_000));
        using var active = admission.Enter();
        var batch = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        Assert.Equal(0, admission.WorkspaceBytes);
        batch.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }

    [Fact]
    public void PlanDisposal_ReleasesSceneAndProductReceipts()
    {
        using var fixture = ResidencyFixture.Placement();
        var state = fixture.State(ResidencyFixture.Window());
        var input = MapPreparedInputLease.LoadSelected(state.Closure, ["prop-a"], [], state.Extents, new(new())).Input!;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 100_000_000));
        using var active = admission.Enter();
        var scene = MapSceneAcquisition.Run(state.Source, state.Windows.Single(), admission).Scene!;
        var plan = MapRenderPlan.Prepare(state.Source, scene, input, state.Policy, 1);
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(scene.PreparationOwnership!.Bytes + plan.PreparationOwnership!.Bytes, admission.RetainedBytes);
        Assert.True(plan.PreparationOwnership.Bytes > 0);
        plan.Dispose(); plan.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }
}
