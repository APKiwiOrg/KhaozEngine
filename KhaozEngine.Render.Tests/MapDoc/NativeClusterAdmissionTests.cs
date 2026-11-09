using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeClusterAdmissionTests
{
    [Fact]
    public void StandaloneCluster_OversizedSourceRefusesBeforeTheBuilder()
    {
        var (scene, routing, request) = Request();
        var sources = request.Layer.HlodSourceMeshes!.ToDictionary(p => p.Key, p => p.Value);
        var source = sources["prop-a"];
        sources["prop-a"] = new(Enumerable.Repeat(source.Vertices[0], 100000).ToArray(), source.Indices32);
        var oversized = new NativePropClusterRequest(request.Key, request.Generation, request.Area,
            request.Layer.WithHlod(sources, 30, 0, 10), request.Instances, request.Anchor);
        // Canonical acquisition fits 64 KiB. The caller-supplied 100000-vertex source's output cannot.
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 65536));
        using var active = admission.Enter();
        int entries = 0;
        var error = Record.Exception(() => NativeMapPlacements.PrepareCluster(scene, routing, oversized,
            input => { entries++; return PropClusterRenderer.BuildNativeCpu(input); }));
        Assert.Equal(0, entries);
        var refusal = Assert.IsType<MapRenderAssetRefusal>(error);
        Assert.Equal(MapRenderAssetStatus.CapacityExceeded, refusal.Status);
        Assert.True(refusal.Capacity!.Requested > 65536);
        Assert.Equal(65536, refusal.Capacity.Allowance);
        Assert.Equal(0, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
    }

    [Fact]
    public void StandaloneCluster_ReturnedGeometryKeepsOneReceiptUntilDisposal()
    {
        var (scene, routing, request) = Request();
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 65536));
        using var active = admission.Enter();
        var build = NativeMapPlacements.PrepareCluster(scene, routing, request);
        Assert.Equal(6, build.BorrowMesh.Vertices.Length);
        Assert.Equal(6, build.BorrowMesh.Indices32.Length);
        // Two six-vertex meshes, one native row, one asset's three tier references, two LOD
        // distances, one source-map entry and the represented/request identity references.
        long mesh = 256 + 6 * ModelVertex.SizeInBytes + 6 * 4;
        long expected = 2 * mesh + 128 + 64 + 3 * 8 + 2 * 4 + 64 + 16;
        Assert.Equal(expected, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(new[] { "sparse-0" }, build.RepresentedIds);
        var owner = Assert.IsAssignableFrom<IDisposable>(build);
        owner.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
        using var other = admission.Workspace(); other.Reserve(17);
        owner.Dispose();
        Assert.Equal(17, admission.WorkspaceBytes);
        Assert.Equal(0, admission.RetainedBytes);
    }

    [Fact]
    public void StandaloneWeldedCluster_KeepsExactlyOneProvenanceReceiptUntilDisposal()
    {
        var (scene, routing, request) = Request(.0001f);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 65536));
        using var active = admission.Enter();
        using var build = NativeMapPlacements.PrepareCluster(scene, routing, request);
        var expectedMesh = PropHlod.BuildMergedMesh([new("prop-a", 8, 0, 8, 1, 0, 0)], request.Layer.HlodSourceMeshes!, .0001f);
        Assert.Equal(expectedMesh.Indices32, build.BorrowMesh.Indices32);
        long sourceBytes = 256 + 6 * ModelVertex.SizeInBytes + 6 * 4;
        long mergedBytes = 256 + expectedMesh.Vertices.Length * ModelVertex.SizeInBytes + expectedMesh.Indices32.Length * 4;
        // The original placement array and read-only view exist once, independently of native rows.
        long provenanceBytes = 64 + Unsafe.SizeOf<PropPlacement>();
        long expected = sourceBytes + mergedBytes + 128 + 64 + 3 * 8 + 2 * 4 + 64 + 16 + provenanceBytes;
        Assert.Equal(expected, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
        build.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
        using var other = admission.Workspace(); other.Reserve(17);
        build.Dispose();
        Assert.Equal(17, admission.WorkspaceBytes);
    }

    [Fact]
    public void WeldedBatch_ChargesProvenanceOnceAndReleasesWithOwner()
    {
        var closure = NativeRenderAssetFixtures.Placements();
        var scene = NativeMapClusterCellTests.Scene(closure, 1, new MapEffectiveTransform(8, 0, 8, 0, 1));
        using var assets = NativeMapPlacementTests.Assets(closure);
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 1_000_000));
        using var active = admission.Enter();
        var zeroPolicy = NativeMapClusterCellTests.Policy(1000);
        var zeroRouting = MapPlacementRouting.Create(scene, zeroPolicy, new HashSet<string>()).Routing!;
        using var zero = NativeMapPlacements.Prepare(scene, assets, zeroPolicy, zeroRouting);
        long noWeldBytes = zero.RetainedBytes;
        Assert.Null(Assert.Single(zero.Clusters).Request.WeldPlacements);
        Assert.Equal(noWeldBytes, admission.RetainedBytes);
        zero.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
        var policy = new MapPlacementPolicy([new("selected", new HashSet<string> { "prop-a" }, [10, 20], 4, 1000, 30, 10, .0001f, true)], 1000);
        var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
        using var batch = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        var request = Assert.Single(batch.Clusters).Request;
        Assert.Equal(new PropPlacement("prop-a", 8, 0, 8, 1, 0, 0), Assert.Single(request.WeldPlacements!));
        long expected = noWeldBytes + 64 + Unsafe.SizeOf<PropPlacement>();
        Assert.Equal(expected, batch.RetainedBytes);
        Assert.Equal(expected, batch.PreparationChargeBytes);
        Assert.Equal(expected, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
        batch.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }

    static (MapScopedScene Scene, MapPlacementRouting Routing, NativePropClusterRequest Request) Request(float weldCell = 0)
    {
        var closure = NativeRenderAssetFixtures.Placements();
        var scene = NativeMapClusterCellTests.Scene(closure, 1, new MapEffectiveTransform(8, 0, 8, 0, 1));
        using var assets = NativeMapPlacementTests.Assets(closure);
        var policy = new MapPlacementPolicy([new("selected", new HashSet<string> { "prop-a" }, [10, 20], 4, 1000, 30, 10, weldCell, true)], 1000);
        var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
        using var batch = NativeMapPlacements.Prepare(scene, assets, policy, routing);
        return (scene, routing, Assert.Single(batch.Clusters).Request);
    }
}
