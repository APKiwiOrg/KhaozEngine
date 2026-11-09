using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Foliage;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapPreparationAcquisitionTests
{
    [Fact]
    public void Acquisition_AdmitsSelectedMetadataBeforePatchCopies()
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        fixture.Document.Surfaces.Refs.Clear(); fixture.Document.Surfaces.Patches.Clear();
        var patch = KhaozEngine.Tests.MapDoc.Prefabs.PrefabFixtures.Patch("tiny", 1, 1, 0);
        fixture.Document.Surfaces.Refs.Add(KhaozEngine.Tests.MapDoc.Prefabs.PrefabFixtures.Surface("tiny", KhaozEngine.MapDoc.Surfaces.MapSurfaceRole.SupportFloor));
        fixture.Document.Surfaces.Patches.Add(patch.Key, patch);
        var source = NativeSceneFixtures.Open(fixture, false);
        long minimum = long.MaxValue;
        MapSceneAdmission? admission = null;
        admission = new(new(MaxPreparedBytes: 100_000), stage =>
        { if (stage == "clone") minimum = System.Math.Min(minimum, admission!.WorkspaceBytes + admission.RetainedBytes); });
        var result = MapSceneAcquisition.Run(source, MapSceneScopeTests.Window(), admission);
        Assert.NotNull(result.Scene);
        Assert.InRange(minimum, 544, 100_000);
    }

    [Fact]
    public void CoverageLease_ReleasesItsUniqueRetainedInventory()
    {
        using var fixture = NativeSceneFixtures.Create("tile-owner");
        var source = NativeSceneFixtures.Open(fixture, false);
        var lease = MapRequiredDomainCoverage.Acquire(source, [MapSceneScopeTests.Window()]);
        Assert.Equal(MapSceneStatus.CompleteForScope, lease.Status);
        Assert.True(lease.PreparationOwnership!.Bytes > 0);
        lease.Dispose(); lease.Dispose();
        Assert.Equal(0, lease.PreparationOwnership.Bytes);
    }

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
