using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeMapMaterialSelectionOwnershipTests
{
    [Fact]
    public void MaterialSelection_ReleasesItsPatchCopyBeforeTheNextStage()
    {
        var request = CaptureDependencyFixtures.Request();
        var scene = request.Source.Acquire(request.Scope).Scene!;
        using var sceneOwner = scene.PreparationOwnership;
        // The fixture has 81 four-byte heights, 64 eight-byte cells and one presence word.
        const long patchBytes = 81 * 4 + 64 * 8 + 8;
        const long idsBytes = 8;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: patchBytes + idsBytes));
        using var active = admission.Enter();
        using (admission.BeginPreparation())
        {
            Assert.Equal(new[] { "floor-material" }, MapRenderPlan.SelectedMaterials(scene, request.Policy.Render));
            Assert.Equal(idsBytes, admission.RetainedBytes);
            Assert.Equal(0, admission.WorkspaceBytes);
            using var nextStage = admission.Workspace();
            nextStage.Reserve(patchBytes);
            Assert.Equal(patchBytes + idsBytes, admission.PeakBytes);
            var refusal = Assert.Throws<MapSceneCapacityException>(() => nextStage.Reserve(1));
            Assert.Equal(new MapSceneCapacity("MaxPreparedBytes", 853, 852), refusal.Capacity);
        }
        Assert.Equal(0, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
    }
}
