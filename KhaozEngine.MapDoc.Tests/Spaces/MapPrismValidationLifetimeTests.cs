using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Spaces;

public sealed class MapPrismValidationLifetimeTests
{
    [Fact]
    public void RootContext_ReleasesAtItsLastConsumerBeforeTheLaterPeak()
    {
        var view = Flat();
        const long rawBytes = 2 * (1024 + 3 * 256);
        const long laterBytes = 4096 + 64 * 4096;
        const long allowance = 100 + 32768 + rawBytes + laterBytes;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: allowance));
        using var active = admission.Enter();
        using var earlier = admission.Workspace();
        earlier.Reserve(100);
        using var retained = earlier.Transfer(100);
        var counts = new MapRoofPreparationCounts();
        using (var validation = new MapPrismValidationWorkspace(admission, counts, 0))
        {
            var result = MapOccupiedPartitions.BuildWide(view, new(new(0, 1), new(0, 1)), new(new(1, 1), new(1, 1)), new(), false);
            Assert.Equal(MapRefinementStatus.Complete, result.Status);
            Assert.Equal(2, result.Pieces.Count);
            Assert.Equal(32768 + rawBytes, admission.WorkspaceBytes);
            Assert.Equal(100, admission.RetainedBytes);
            using var later = MapPrismValidationWorkspace.Kernel(64);
            Assert.Equal(allowance, admission.WorkspaceBytes + admission.RetainedBytes);
            var refusal = Assert.Throws<MapSceneCapacityException>(() => later!.Reserve(1));
            Assert.Equal(new MapSceneCapacity("MaxPreparedBytes", allowance + 1, allowance), refusal.Capacity);
        }
        Assert.Equal(allowance, counts.ValidationPeakBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(100, admission.RetainedBytes);
    }

    [Fact]
    public void RootContext_RefusalUnwindsCompilerAndContextOwners()
    {
        var view = Flat();
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 50000));
        using var active = admission.Enter();
        using var earlier = admission.Workspace();
        earlier.Reserve(100);
        using var retained = earlier.Transfer(100);
        Assert.Throws<MapSceneCapacityException>(() =>
        {
            using var validation = new MapPrismValidationWorkspace(admission, new(), 0);
            _ = MapOccupiedPartitions.BuildWide(view, new(new(0, 1), new(0, 1)), new(new(1, 1), new(1, 1)), new(), false);
        });
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(100, admission.RetainedBytes);
    }

    internal static MapScopedSurfaces Flat()
    {
        var set = new MapSurfaceSet();
        set.Refs.Add(MixedResolutionFixtures.Surface("floor", 1, MapSurfaceRole.SupportFloor));
        set.Refs.Add(MixedResolutionFixtures.Surface("ceiling", 1, MapSurfaceRole.Ceiling));
        var floor = MixedResolutionFixtures.Patch(new("floor", 0, 0), 1, 1, (_, _) => 0);
        var ceiling = MixedResolutionFixtures.Patch(new("ceiling", 0, 0), 1, 1, (_, _) => 600);
        MixedResolutionFixtures.Room(floor, "outside", "outside-cells", MapSpaceKind.Exterior, floor.Key, [0], "floor", "ceiling");
        set.Patches.Add(floor.Key, floor);
        set.Patches.Add(ceiling.Key, ceiling);
        return MixedResolutionFixtures.ViewWithFootprint(set).View;
    }
}
