using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapPolicyAcquisitionOwnershipTests
{
    [Fact]
    public void PrismClosure_RetiresEachReplacedSceneAndKeepsRealOverlapCharged()
    {
        using var fixture = Chain();
        var source = NativeSceneFixtures.Open(fixture, false);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 2_000_000));
        var diagnostics = admission.ObserveAcquisition();
        using var active = admission.Enter();
        var first = MapSceneAcquisition.Run(source, MapSceneScopeTests.Window(0, 1), admission).Scene!;
        long before = admission.RetainedBytes;
        var complete = MapRoofPolicyAcquisition.Complete(first);
        Assert.Equal(3, complete.SurfaceView.Witness.Present.Count);
        Assert.Equal(3, diagnostics.Callers[MapAcquisitionCaller.PolicyReacquisition].Scenes);
        long expected = new MapAcquisitionInventoryOracle().Scene(complete);
        Assert.Equal(expected, admission.RetainedBytes);
        Assert.Equal(0, first.PreparationOwnership!.Bytes);
        Assert.True(diagnostics.Stages[MapAcquisitionStage.Policy].RetainedHighWater >= before + expected);
        Assert.Equal(0, admission.WorkspaceBytes);
        complete.PreparationOwnership!.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }

    [Fact]
    public void RefusedPolicyReplacement_LeavesItsInputUsableAndCharged()
    {
        using var fixture = Chain();
        var source = NativeSceneFixtures.Open(fixture, false);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 2_000_000));
        using var active = admission.Enter();
        var first = MapSceneAcquisition.Run(source, MapSceneScopeTests.Window(0, 1), admission).Scene!;
        long before = admission.RetainedBytes;
        using (admission.LimitPreparation(0, "MaxPlanBytes"))
        {
            var error = Assert.Throws<MapSceneSourceException>(() => MapRoofPolicyAcquisition.Complete(first));
            Assert.Equal(new MapSceneCapacity("MaxPlanBytes", 140, 0), error.Capacity);
        }
        Assert.True(first.Witness.CompleteForScope);
        Assert.Equal(before, first.PreparationOwnership!.Bytes);
        Assert.Equal(before, admission.RetainedBytes);
        first.PreparationOwnership.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }

    static NativeSceneFixture Chain()
    {
        var fixture = NativeSceneFixtures.Create("empty");
        var outside = new MapRecordRef("outside", new("floor-0", 0, 0));
        fixture.Document.BuildingIntent = new(1, [], [], [], []) { Storeys = [new(1, "storey", "Storey", null)] };
        for (int i = 0; i < 3; i++)
        {
            string floor = "floor-" + i, room = "room-" + i;
            var patch = PrefabFixtures.Patch(floor, 1, 1, 0);
            patch.Key = new(floor, i * 2, 0);
            if (i == 0) patch.Records.Add(new MapSpaceDoc("outside", MapSpaceKind.Exterior, null, null, [], [], [], []));
            patch.Records.Add(new MapSpaceDoc(room, MapSpaceKind.Room, outside, null, [], [], [], []));
            patch.Records.Add(new MapPrismDomain("prism-" + i, 1, new(room, patch.Key), outside, "", "storey",
                new(MapLatticeFrame.ImportedMetreCentimetre, [new MapWaterRect(i * 128, 0, i * 128 + 130, 1)], []), 0, 3));
            fixture.Document.Surfaces.Refs.Add(PrefabFixtures.Surface(floor, MapSurfaceRole.SupportFloor));
            fixture.Document.Surfaces.Patches.Add(patch.Key, patch);
        }
        return fixture;
    }
}
