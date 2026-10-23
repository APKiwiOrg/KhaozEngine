using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Foliage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapPreparationAcquisitionTests
{
    [Fact]
    public void FoliageContext_HasItsOwnRetainedReceipt()
    {
        using var fixture = NativeSceneFixtures.Create("tile-owner");
        var source = NativeSceneFixtures.Open(fixture, false);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 1_000_000));
        using var active = admission.Enter();
        var result = MapFoliageGenerationContext.CaptureWindow(source, MapSceneScopeTests.Window());
        Assert.NotNull(result.Context);
        Assert.True(result.Context.PreparationOwnership!.Bytes > 0);
        Assert.Equal(0, admission.WorkspaceBytes);
        result.Context.PreparationOwnership.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }

    [Fact]
    public void FailedAcquisition_UnwindsItsWorkspaceAndUnpublishedInventory()
    {
        using var fixture = NativeSceneFixtures.Create("tile-owner");
        var source = NativeSceneFixtures.Open(fixture, false);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 1000));
        var result = MapSceneAcquisition.Run(source, MapSceneScopeTests.Window(), admission);
        Assert.Equal(MapSceneStatus.CapacityExceeded, result.Status);
        Assert.Null(result.Scene);
        Assert.Equal(0, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
    }

    [Fact]
    public void ReleasedScene_RestoresInventoryCreditWithoutGarbageCollection()
    {
        using var fixture = NativeSceneFixtures.Create("tile-owner");
        var source = NativeSceneFixtures.Open(fixture, false);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 1_000_000));
        var result = MapSceneAcquisition.Run(source, MapSceneScopeTests.Window(), admission);
        Assert.NotNull(result.Scene);
        Assert.True(admission.RetainedBytes > 0);
        Assert.Equal(result.Scene.PreparationOwnership!.Bytes, result.Work.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
        result.Scene.PreparationOwnership!.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }
}
