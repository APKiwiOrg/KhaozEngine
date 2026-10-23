using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapPreparationWaterTests
{
    [Fact]
    public void CanonicalWater_RetainsCellsAndReleasesAllCompilerWorkspace()
    {
        using var fixture = NativeSceneFixtures.Create("water-notch");
        var scene = NativeSceneFixtures.Open(fixture, false).Acquire(NativeSceneFixtures.WaterWindow()).Scene!;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 100_000_000));
        using var active = admission.Enter();
        var water = MapWaterScopedCompiler.Compile(scene);
        Assert.Equal(MapSceneStatus.CompleteForScope, water.Status);
        Assert.NotEmpty(water.Cells);
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.True(water.PreparationOwnership!.Bytes > 0);
        Assert.Equal(water.PreparationOwnership.Bytes, admission.RetainedBytes);
        water.PreparationOwnership.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }
}
