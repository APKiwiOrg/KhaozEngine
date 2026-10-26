using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeRoofSharedMembershipAdmissionTests
{
    static MapPlacementPolicy Policy => new([
        new("caps", new HashSet<string> { "roof" }, [], 0, 100, 30, 10, 0, true),
        new("shelters", new HashSet<string> { "object" }, [], 0, 100, 30, 10, 0, true)], 100);
    [Fact]
    public void MatchingPlanAndTwoRoofCohorts_KeepSharedMembershipAdmitted()
    {
        using var fixture = Create();
        var state = fixture.State(NativePresentationFixtures.Scope);
        var admission = new MapSceneAdmission(new());
        using var active = admission.Enter();
        using var window = admission.LimitPreparation(100000000, "window");
        var scene = state.Source.Acquire(state.Windows[0]).Scene!;
        MapRoofEvaluationBuffer? main = null;
        try
        {
            var policy = Policy;
            var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
            using var cells = MapPlacementCellInventory.Capture(scene, routing);
            var cohorts = cells.Cells.Where(c => c.RoofEvaluation is not null).ToArray();
            Assert.Equal(2, cohorts.Length);
            main = MapRoofEvaluationBuffer.Prepare(scene, admission).Buffer!;
            Assert.Equal(0, cohorts[0].RoofEvaluation!.Counts.FootprintCacheHits);
            Assert.Equal(1, cohorts[1].RoofEvaluation!.Counts.FootprintCacheHits);
            Assert.Equal(1, main.Counts.FootprintCacheHits);
            long shared = MembershipAdmissionFixtures.Footprint() + 512;
            long privateBytes = MembershipAdmissionFixtures.Private(roofs: 2);
            Assert.Equal(privateBytes, main.Counts.PrivateCapacityBytes);
            Assert.Equal(shared, main.Counts.SharedCapacityBytes);
            Assert.All(cohorts, c =>
            {
                Assert.Equal(privateBytes, c.RoofEvaluation!.Counts.PrivateCapacityBytes);
                Assert.Equal(shared, c.RoofEvaluation.Counts.SharedCapacityBytes);
            });
            // Two cell entries reach the same footprint. The collector owns it only once.
            Assert.Equal(128 + 64 * 2 + cohorts.Sum(c => c.RetainedBytes) - shared,
                cells.PreparationOwnership!.Bytes);
            Assert.Equal(0, admission.WorkspaceBytes);
        }
        finally { main?.PreparationOwnership?.Dispose(); scene.PreparationOwnership?.Dispose(); }
        Assert.Equal(0, admission.RetainedBytes);
    }

    [Fact]
    public void PlacementBatch_ChargesTheTwoCohortsSharedFootprintOnce()
    {
        using var fixture = Create();
        var state = fixture.State(NativePresentationFixtures.Scope);
        var scene = state.Source.Acquire(state.Windows[0]).Scene!;
        var admission = new MapSceneAdmission(new());
        using var active = admission.Enter();
        var policy = Policy;
        var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
        using var input = MapPreparedInputLease.LoadSelected(state.Closure, ["roof", "object"], [], state.Extents, new(new())).Input!;
        using var batch = NativeMapPlacements.Prepare(scene, input, policy, routing);
        Assert.Equal(2, batch.Clusters.Count);
        Assert.Equal(batch.RetainedBytes - MembershipAdmissionFixtures.Footprint() - 512, batch.PreparationChargeBytes);
    }

    [Fact]
    public void CompletedWindow_ChargesThreeBuffersOnceRecordsAndReturnsToSourceOnly()
    {
        using var fixture = Create();
        var state = fixture.State(NativePresentationFixtures.Scope);
        state = state with
        {
            Policy = new(Policy, state.Policy.AnimatedAssets, state.Policy.Foliage,
            state.Policy.Water, state.Policy.MaterialResourceIds, state.Policy.TerrainMaterials)
        };
        var scheduler = new ResidencyScheduler();
        var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(state, device,
            new(MaxDisposalsPerUpdate: 16, MaxPlanBytes: 67108864), scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var plan = Assert.Single(residency.AcceptedPlans);
        long reachable = 1024 + plan.Inventory.Terrain.RetainedBytes + plan.Inventory.Placements.RetainedBytes +
            plan.Inventory.Water.RetainedBytes + plan.Inventory.Foliage.RetainedBytes + plan.PresentationGeometry.RetainedBytes +
            plan.RoofEvaluation.RetainedBytes + 128 * 2 + 128L * plan.Resources.Count;
        // Residency supplies its separately retained foliage context. This fixture has no lights or water cells.
        Assert.Equal(reachable - 2 * (MembershipAdmissionFixtures.Footprint() + 512), plan.PreparationBytes);
        Assert.NotEmpty(ResidencyFixture.Draw(residency).Draws);
        residency.SetWindows([]);
        ResidencyFixture.Complete(residency, scheduler);
        long sourceOnly = state.Source.Data.CaptureWork.RetainedBytes + state.Extents.MetadataBytes +
            state.Closure.CapturedResources.Sum(r => (long)r.ByteLength);
        Assert.Equal(sourceOnly, residency.Lifetime.CpuBytes);
        Assert.Equal(0, residency.Lifetime.GpuBytes);
        Assert.Empty(device.Live);
        Assert.Empty(ResidencyFixture.Draw(residency).Draws);
    }

    static ResidencyFixture Create()
    {
        using var native = MembershipAdmissionFixtures.Create();
        var closure = NativeRenderAssetFixtures.Presentation();
        var doc = native.Document;
        doc.NativeAssets = closure.Roots.ToList();
        doc.NumericIdHighWaterMark = 2;
        doc.Placements = [
            new() { Id = "roof-a", AssetId = "roof", Kind = "invented", NumericId = 1, X = 0, Y = 1, Z = 2 },
            new() { Id = "shelter", AssetId = "object", Kind = "invented", NumericId = 2, X = 8, Y = 1, Z = 2 }];
        doc.BuildingIntent = new(1, [], [], [], [])
        {
            Roofs = [new(MapPrefabReferenceScope.World, MapPrefabChildKind.Placement, "roof-a"),
                new(MapPrefabReferenceScope.World, MapPrefabChildKind.Placement, "shelter")]
        };
        return new(doc, closure);
    }
}
