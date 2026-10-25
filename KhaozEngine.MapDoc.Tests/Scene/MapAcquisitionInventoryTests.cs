using System.Linq;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Scene;

public sealed class MapAcquisitionInventoryTests
{
    [Fact]
    public void EmptyScene_RetainsBothIndependentFilterCopies()
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        var scope = MapSceneScopeTests.Window();
        scope = scope with
        {
            Surfaces = scope.Surfaces with
            {
                Roles = Enumerable.Repeat(MapSurfaceRole.SupportFloor, 257).ToArray(),
                SpaceIds = Enumerable.Range(0, 257).Select(i => "space-" + i).ToArray(),
            },
        };
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 2_000_000));
        var scene = MapSceneAcquisition.Run(NativeSceneFixtures.Open(fixture, false), scope, admission).Scene!;
        Assert.NotSame(scene.Scope.Surfaces.Roles, scene.SurfaceView.Scope.Roles);
        Assert.NotSame(scene.Scope.Surfaces.SpaceIds, scene.SurfaceView.Scope.SpaceIds);
        long expected = new MapAcquisitionInventoryOracle().Scene(scene);
        Assert.Equal(expected, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
        scene.PreparationOwnership!.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }

    [Fact]
    public void EffectiveProduct_IncludesGeneratedIndexesIntentWaterAndSupportTables()
    {
        var fixture = Prefabs.PrefabFixtures.Create("all-resources");
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 20_000_000));
        using var active = admission.Enter();
        var product = MapEffectiveResolutionKernel.Resolve(MapEffectiveResolutionInput.FromDocument(fixture.Document), fixture.Assets);
        long expected = new MapAcquisitionInventoryOracle(fixture.Document.Prefabs?.Attachments).Product(product);
        Assert.Equal(expected, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
        product.PreparationOwnership!.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuiescentScene_EqualsIndependentlyEnumeratedOwnedGraph(bool tiled)
    {
        using var fixture = NativeSceneFixtures.Create("terrain-wall");
        var patch = fixture.Document.Surfaces.Patches.Values.Single();
        patch.Records.Add(new MapSpaceDoc("outside", MapSpaceKind.Exterior, null, null, [], [], [], []));
        fixture.Document.Surfaces.Refs[0] = fixture.Document.Surfaces.Refs[0] with
        {
            IndoorSpan = new("span", new("outside", patch.Key), 0, 300,
                Enumerable.Range(0, 33).Select(i => "tag-" + i).ToArray()),
        };
        var source = NativeSceneFixtures.Open(fixture, tiled);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 2_000_000));
        var result = MapSceneAcquisition.Run(source, MapSceneScopeTests.Window(), admission);
        Assert.NotNull(result.Scene);
        long expected = new MapAcquisitionInventoryOracle().Scene(result.Scene);
        Assert.Equal(expected, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
        result.Scene.PreparationOwnership!.Dispose();
        result.Scene.PreparationOwnership.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }
}
