using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Prefabs;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapPreparationCompilationTests
{
    [Fact]
    public void CountingFaces_ReleasesSubdivisionWorkspace()
    {
        var patch = PrefabFixtures.Patch("floor", 2, 2, 0);
        var surface = PrefabFixtures.Surface("floor", MapSurfaceRole.SupportFloor);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 64));
        using var active = admission.Enter();
        Assert.Equal(8, MapSurfaceCompiler.CountFaces(surface, patch, MapSlotCellMask.All));
        Assert.Equal(8, MapSurfaceCompiler.CountFaces(surface, patch, MapSlotCellMask.All));
        Assert.Equal(64, admission.PeakBytes);
        Assert.Equal(0, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
    }

    [Fact]
    public void Compilation_RetainsOnlyOutputAndExplicitReleaseRestoresAllowance()
    {
        var patch = PrefabFixtures.Patch("floor", 2, 2, 0);
        var surface = PrefabFixtures.Surface("floor", MapSurfaceRole.SupportFloor);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 20_000));
        using var active = admission.Enter();
        var compiled = MapSurfaceCompiler.Compile(surface, patch);
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.InRange(admission.RetainedBytes, 1, 8000);
        compiled.PreparationOwnership!.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
        Assert.Equal(8, admission.PreparedFaces);
    }
}
