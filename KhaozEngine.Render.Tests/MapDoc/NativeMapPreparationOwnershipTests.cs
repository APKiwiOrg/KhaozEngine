using System.Linq;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeMapPreparationOwnershipTests
{
    [Fact]
    public void SampleInvalidation_WaitsForTheLastHeldReader()
    {
        using var fixture = ResidencyFixture.Foliage();
        var state = fixture.FoliageState(); var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(state, new ResidencyDevice(), null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var plan = residency.AcceptedPlans.Single(); var key = plan.Inventory.Foliage.GenerationKeys[0];
        var cache = MapFoliageRenderCache.For(plan.FoliageContext);
        var reader = cache.Acquire(key.LayerId, key.Chunk, new(), null, null, null);
        long before = residency.Lifetime.CpuBytes, samples = cache.RetainedBytes;
        long held = reader.Batch.Work.RetainedBytes;
        cache.Invalidate(new(null, null, [], [], [], [], KhaozEngine.MapDoc.Editing.MapNativeInvalidation.Unbounded));
        Assert.Equal(0, cache.Count);
        Assert.True(samples > held);
        // Both accepted groups still own their columns. Only the three-slot cache index retires here.
        // Cache controls 248, two array headers 48, three 32-byte entries and three 4-byte buckets.
        Assert.Equal(before - 404, residency.Lifetime.CpuBytes);
        reader.Dispose(); reader.Dispose();
        Assert.Equal(before - 404, residency.Lifetime.CpuBytes);
    }

    [Fact]
    public void CaptureAdmission_DoesNotPublishTemporaryScenesAsResidentCpu()
    {
        var result = MapCapturePreparation.Prepare(CaptureDependencyFixtures.Request());
        Assert.True(result.Witness.Scene.CompleteForScope);
        Assert.True(result.Readiness.Pending > 0);
        Assert.Equal(0, result.Readiness.CpuBytes);
    }

    [Fact]
    public void SettledFoliageCpu_EqualsUniqueLiveReceiptInventory()
    {
        using var fixture = ResidencyFixture.Foliage();
        var state = fixture.FoliageState();
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(state, device, null, scheduler);
        residency.Update(); scheduler.RunAll();
        Assert.True(residency.Update().Transition.Status != MapSceneStatus.CapacityExceeded,
            residency.CurrentTransition.Findings.FirstOrDefault());
        ResidencyFixture.Complete(residency, scheduler);
        var plan = residency.AcceptedPlans.Single();
        long source = state.Source.Data.CaptureWork.RetainedBytes + state.Extents.MetadataBytes +
            state.Closure.CapturedResources.Sum(r => (long)r.ByteLength);
        long samples = MapFoliageRenderCache.For(plan.FoliageContext).RetainedBytes;
        long receipts = plan.PreparationOwnership!.Bytes + plan.Scene.PreparationOwnership!.Bytes +
            plan.FoliageContext.PreparationOwnership!.Bytes + samples;
        long uploads = plan.Resources.Sum(r => device.MeasureUpload(r).PeakCpuBytes);
        long recording = 512L + 512L * plan.Resources.Count;
        Assert.Equal(source + residency.AssetCache.RetainedBytes + receipts + uploads + recording, residency.Lifetime.CpuBytes);
        residency.Dispose();
        Assert.Equal(0, residency.Lifetime.CpuBytes);
    }

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
