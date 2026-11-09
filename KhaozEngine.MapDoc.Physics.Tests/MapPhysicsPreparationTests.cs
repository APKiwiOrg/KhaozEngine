using System.Linq;
using KhaozEngine.MapDoc.Physics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

public sealed class MapPhysicsPreparationTests
{
    [Theory]
    [InlineData("empty")]
    [InlineData("terrain-wall")]
    [InlineData("terrain-posed")]
    public void DiscardedWorlds_RestorePreparationCredit(string variant)
    {
        using var fixture = Fixture(variant);
        var scene = NativeSceneFixtures.Open(fixture, false).Acquire(Window()).Scene!;
        var admission = scene.SurfaceView.Scope.SceneAdmission!;
        long before = admission.RetainedBytes;
        var first = Build(scene, fixture);
        long held = admission.RetainedBytes;
        Assert.True(held > before);
        first.Dispose();
        Assert.Equal(before, admission.RetainedBytes);
        var second = Build(scene, fixture);
        Assert.Equal(held, admission.RetainedBytes);
        second.Dispose();
        Assert.Equal(before, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Null(MapSceneAdmission.Current);
    }

    [Fact]
    public void FailedBuild_AfterTransformedPatchesRestoresEarlierInventory()
    {
        using var fixture = Fixture("terrain-posed");
        var source = NativeSceneFixtures.Open(fixture, false);
        bool building = false;
        int compiles = 0;
        var admission = new MapSceneAdmission(new(), stage =>
        {
            // Refuse the second compiler entry after the first transformed patch has been consumed.
            if (building && stage == "compile" && ++compiles == 2)
                throw new MapSceneCapacityException("injected compiler refusal", 2, 1);
        });
        var acquired = MapSceneAcquisition.Run(source, Window(), admission);
        Assert.True(acquired.Scene is not null, string.Join("\n", acquired.Findings));
        var scene = acquired.Scene;
        Assert.NotEmpty(scene.Transforms);
        long before = admission.RetainedBytes;
        building = true;
        var failed = MapScopedWorldBuilder.Build(scene, fixture.Assets, NativeWorldFixtures.Options());
        Assert.Equal(MapSceneStatus.CapacityExceeded, failed.Status);
        Assert.Null(failed.World);
        Assert.Equal(new MapSceneCapacity("injected compiler refusal", 2, 1), failed.Capacity);
        Assert.Equal(2, compiles);
        Assert.Equal(before, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Null(MapSceneAdmission.Current);
    }

    [Fact]
    public void SimulationDisposal_ReleasesWorldAndSceneInventoryOnce()
    {
        using var fixture = Fixture("terrain-wall");
        var source = NativeSceneFixtures.Open(fixture, false);
        var lease = new MapSimulationCoverage(source, fixture.Assets, NativeWorldFixtures.Options()).Acquire([Window()]);
        var world = Assert.Single(lease.Worlds);
        var admission = world.Admission;
        Assert.True(admission.RetainedBytes > 0);
        lease.Dispose();
        lease.Dispose();
        Assert.Empty(lease.Worlds);
        Assert.Equal(0, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
    }

    [Fact]
    public void SimulationDisposal_ReleasesDerivedNavigationProducts()
    {
        using var fixture = NativeCellHandoffFixtures.Create();
        var source = NativeSceneFixtures.Open(fixture, false);
        var lease = new MapSimulationCoverage(source, fixture.Assets, NativeWorldFixtures.Options())
            .Acquire([NativeCellHandoffFixtures.Domain(0)]);
        var world = Assert.Single(lease.Worlds);
        var tiles = MapNavTiling.Partition(world, NativeCellHandoffFixtures.Grids, NativeCellHandoffFixtures.Nav);
        _ = MapNavTiling.Seams(tiles, world, NativeCellHandoffFixtures.Nav);
        _ = MapNavTiling.Links(world, NativeCellHandoffFixtures.Grids);
        var admission = world.Admission;
        lease.Dispose();
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(0, admission.RetainedBytes);
    }

    static MapScopedWorld Build(MapScopedScene scene, NativeSceneFixture fixture)
    {
        var result = MapScopedWorldBuilder.Build(scene, fixture.Assets, NativeWorldFixtures.Options());
        Assert.True(result.World is not null, string.Join("\n", result.Findings));
        return result.World;
    }

    static MapSceneScope Window() => MapPhysicsPreparationFixtures.Window(-2, 4);
    static NativeSceneFixture Fixture(string variant)
    {
        var fixture = NativeSceneFixtures.Create(variant);
        if (variant == "terrain-posed")
            fixture.Document.NativeResourceTransforms = new[] { "ground", "ceiling" }.Select(id =>
                new MapStandaloneResourceTransform(MapPrefabChildKind.Surface, id,
                    new(.25, 0, .25, .2, 1), new string('a', 64))).ToArray();
        return fixture;
    }
}
