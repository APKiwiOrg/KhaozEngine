using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeRoofBatchAdmissionTests
{
    [Fact]
    public void RoutingLayerArray_RefusesBeforeCellAcquisitionWhenItsBackingCannotFit()
    {
        var closure = NativeRenderAssetFixtures.Placements();
        using var fixture = NativeSceneFixtures.SeparatedPlacements(closure, 1, new MapEffectiveTransform(8, 0, 8, 0, 1));
        var scene = NativeSceneFixtures.Open(fixture, false).Acquire(NativePresentationFixtures.Scope).Scene!;
        var layers = Enumerable.Range(0, 10000).Select(i => new MapPlacementLayer("empty-" + i,
            new HashSet<string>(), [], 0, 100, 30, 10, 0, true)).Append(
            new("selected", new HashSet<string> { "prop-a" }, [10, 20], 4, 100, 30, 10, 0, true)).ToArray();
        var policy = new MapPlacementPolicy(layers, 100);
        var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 65536));
        using var active = admission.Enter();
        var refusal = Assert.Throws<MapSceneCapacityException>(() => MapPlacementCellInventory.Capture(scene, routing));
        // 512 bytes of stage metadata, 128 for the selected key and 10001 eight-byte layer references.
        Assert.Equal(80648, refusal.Capacity.Requested);
        Assert.Equal(65536, refusal.Capacity.Allowance);
        Assert.Equal(0, admission.PayloadReads);
        Assert.Equal(0, admission.WorkspaceBytes);
        Assert.Equal(0, admission.RetainedBytes);
    }

    [Fact]
    public void BatchReceipt_EqualsUniqueMeshesCanonicalPolicyAndFrameTableCapacities()
    {
        var closure = NativeRenderAssetFixtures.Placements();
        using var fixture = NativeSceneFixtures.SeparatedPlacements(closure, 1, new(8, 0, 8, 0, 1), new(16, 0, 8, 0, 1));
        fixture.Document.BuildingIntent = new(1, [], [], [], [])
        {
            Roofs = fixture.Document.Placements.Select(p =>
                new MapPrefabReference(MapPrefabReferenceScope.World, MapPrefabChildKind.Placement, p.Id)).ToArray()
        };
        var scene = NativeSceneFixtures.Open(fixture, false).Acquire(NativePresentationFixtures.Scope).Scene!;
        var proofs = MapRenderExtentValidator.Validate(closure, ["prop-a"], new(new())).Proofs!;
        using var input = MapPreparedInputLease.LoadSelected(closure, ["prop-a"], [], proofs, new(new())).Input!;
        var policy = new MapPlacementPolicy([new("caps", new HashSet<string> { "prop-a" }, [10, 20], 4, 100, 30, 10, 0, true)], 100);
        var routing = MapPlacementRouting.Create(scene, policy, new HashSet<string>()).Routing!;
        // Two six-vertex sources, fifteen detached resources, one empty membership buffer and tables
        // remain below 256 KiB even while the full merge, contributions and copy workspace coexist.
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 262144));
        using var active = admission.Enter();
        using var cells = MapPlacementCellInventory.Capture(scene, routing);
        var cell = Assert.Single(cells.Cells);
        long policyBytes = cell.RoofEvaluation!.RetainedBytes;
        using var batch = NativeMapPlacements.Prepare(scene, input, policy, routing, cells);
        var build = Assert.Single(batch.Clusters);
        Assert.Equal(2, build.Request.Instances.Count);
        Assert.Equal(12, build.BorrowMesh.Vertices.Length);
        var source = Assert.Single(build.Request.BorrowLayer.HlodSourceMeshes!).Value;
        Assert.Equal(6, source.Vertices.Length);
        MapDrawSet snapshot;
        using (MapSceneAdmission.Suspend())
        {
            var recorder = new MapDrawRecorder(); batch.Register(NativeMapPlacementTests.Stamp, recorder);
            snapshot = recorder.Snapshot();
        }
        Assert.Equal(15, snapshot.Resources.Count);
        long payloads = snapshot.Resources.Sum(r =>
        {
            var payload = Assert.IsType<MapRigidResource>(r.Payload);
            return Mesh(payload.BorrowMesh, payload.BorrowMaps);
        });
        // Two native rows, one tier dictionary, three tier references/objects, six handles,
        // two thresholds, one source dictionary/copy and the request's instance references.
        long request = 2 * 128L + 64 + 3L * IntPtr.Size + 3 * 24L + 6L * Unsafe.SizeOf<MeshHandle>() +
            2 * 4 + 64 + Mesh(source) + 2 * 16;
        long products = 1024 + 2 * 128L + 15 * 256L + 32 + payloads + Mesh(build.BorrowMesh) + request +
            2 * 8 + Mesh(build.BorrowContribution(0)) + Mesh(build.BorrowContribution(1));
        // The 15-entry part lookup grows to 17 slots. Each two-entry roof lookup has 3 slots.
        // One cohort has a two-index array and its own three-slot policy lookup.
        long frameTables = IntPtr.Size + 4 + 17 * 64L + 3 * 64L + 3 * 64L + 128 + 2 * 4 + 3 * 64L;
        long expected = products + policyBytes + frameTables;
        Assert.Equal(expected, batch.RetainedBytes);
        Assert.Equal(expected, batch.PreparationChargeBytes);
        Assert.Equal(expected, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
        cells.Dispose();
        Assert.Equal(expected, admission.RetainedBytes);
        batch.UpdateRoofs(NativeMapPlacementTests.Frame with { RoofMode = MapRoofMode.AlwaysHidden });
        Assert.All(cell.RoofEvaluation.Roofs, roof => Assert.False(roof.ColourVisible));
        batch.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }

    static long Mesh(GltfMesh mesh, GltfMaterialMaps maps = default) => 256L +
        (long)mesh.Vertices.Length * ModelVertex.SizeInBytes + 4L * mesh.Indices32.Length +
        (maps.Albedo?.Rgba.LongLength ?? 0) + (maps.Normal?.Rgba.LongLength ?? 0) + (maps.Roughness?.Rgba.LongLength ?? 0);
}
