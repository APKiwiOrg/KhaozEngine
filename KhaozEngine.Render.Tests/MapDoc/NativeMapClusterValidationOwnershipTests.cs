using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeMapClusterValidationOwnershipTests
{
    [Fact]
    public void ClusterValidation_ReleasesItsAcquiredCellBeforeMerge()
    {
        var closure = NativeRenderAssetFixtures.Placements();
        var scene = NativeMapClusterCellTests.Scene(closure, 1, new MapEffectiveTransform(0, 0, 0, 0, 1));
        using var sceneOwner = scene.PreparationOwnership;
        using var assets = NativeMapPlacementTests.Assets(closure);
        var policy = NativeMapClusterCellTests.Policy(1000);
        var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
        using var batch = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        var request = Assert.Single(batch.Clusters).Request;
        var sources = request.Layer.HlodSourceMeshes!;
        long mergeWorkspace = 8 * request.Instances.Sum(i => 256L +
            ModelVertex.SizeInBytes * (long)sources[i.AssetId].Vertices.Length + 4L * sources[i.AssetId].Indices32.Length);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 1_000_000));
        using var active = admission.Enter();
        bool entered = false;
        using var result = NativeMapPlacements.PrepareCluster(scene, routing, request, input =>
        {
            entered = true;
            Assert.Equal(0, admission.RetainedBytes);
            Assert.Equal(mergeWorkspace, admission.WorkspaceBytes);
            return PropClusterRenderer.BuildNativeCpu(input);
        });
        Assert.True(entered);
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.True(admission.RetainedBytes > 0);
        result.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }
}
