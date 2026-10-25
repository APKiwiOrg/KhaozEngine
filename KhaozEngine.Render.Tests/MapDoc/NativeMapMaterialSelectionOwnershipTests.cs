using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeMapMaterialSelectionOwnershipTests
{
    [Fact]
    public void MaterialSelection_OldAllowanceRefusesBeforeThePatchClone()
    {
        var request = CaptureDependencyFixtures.Request();
        var scene = request.Source.Acquire(request.Scope).Scene!;
        using var sceneOwner = scene.PreparationOwnership;
        int clones = 0;
        // The original allowance covers only the 844-byte patch and eight-byte selected-ID output.
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 852), stage =>
        {
            if (stage == "clone") clones++;
        });
        using var active = admission.Enter();
        var refusal = Assert.Throws<MapSceneCapacityException>(() => MapRenderPlan.SelectedMaterials(scene, request.Policy.Render));
        Assert.Equal(new MapSceneCapacity("MaxPreparedBytes", 1196, 852), refusal.Capacity);
        Assert.Equal(0, clones);
        Assert.Equal(0, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
    }

    [Fact]
    public void MaterialSelection_ReleasesItsPatchCopyBeforeTheNextStage()
    {
        var request = CaptureDependencyFixtures.Request();
        var scene = request.Source.Acquire(request.Scope).Scene!;
        using var sceneOwner = scene.PreparationOwnership;
        // The fixture has 81 four-byte heights, 64 eight-byte cells and one presence word.
        const long patchBytes = 81 * 4 + 64 * 8 + 8;
        // The detached set owns its row, descriptor, four reference slots and patch-tree node.
        const long surfaceSetBytes = patchBytes + 128 + 128 + 4 * 8 + 64;
        const long idsBytes = 8;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: surfaceSetBytes + idsBytes));
        using var active = admission.Enter();
        using (admission.BeginPreparation())
        {
            Assert.Equal(new[] { "floor-material" }, MapRenderPlan.SelectedMaterials(scene, request.Policy.Render));
            Assert.Equal(idsBytes, admission.RetainedBytes);
            Assert.Equal(0, admission.WorkspaceBytes);
            using var nextStage = admission.Workspace();
            nextStage.Reserve(surfaceSetBytes);
            Assert.Equal(surfaceSetBytes + idsBytes, admission.PeakBytes);
            var refusal = Assert.Throws<MapSceneCapacityException>(() => nextStage.Reserve(1));
            Assert.Equal(new MapSceneCapacity("MaxPreparedBytes", 1205, 1204), refusal.Capacity);
        }
        Assert.Equal(0, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
    }
}
