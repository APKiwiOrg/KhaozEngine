using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageMeshEvidenceTests
{
    [Fact]
    public void Annotated_upload_requires_all_material_slices()
    {
        byte[] glb = CoverageImportTests.HostFixture().Build();
        var parts = GltfLoader.LoadSkinnedPartsWithMaterials(glb, "complete-host", CoverageImportTests.Map(glb));
        var incomplete = new[] { parts[0].Mesh };
        var error = Assert.Throws<HostCoverageDataException>(() => GltfSkinnedMeshPart.ValidateCoverageParts(parts[0].Mesh.Coverage!, incomplete));
        Assert.Equal(HostCoverageRefusal.MissingMaterialSlices, error.Reason);
        Assert.Equal("complete-host", error.Identity);
        Assert.Equal(new[] { 1 }, error.MissingMaterialSlices);
        Assert.Equal(1, error.Count);
        Assert.Equal(2, error.Limit);
        GltfSkinnedMeshPart.ValidateCoverageParts(parts[0].Mesh.Coverage!, parts.Select(p => p.Mesh).ToArray());
        using var device = new FakeGpuDevice();
        using var scene = new Scene3D(device, new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));
        var handles = parts.Select(p => scene.LoadSkinnedMesh(p.Mesh)).ToArray();
        GltfSkinnedMeshPart.ValidateCoverageParts(parts[0].Mesh.Coverage!, handles.Select(scene.SkinnedSourceSnapshotForTests).ToArray());
    }

    [Fact]
    public void Missing_material_slice_inventory_includes_an_empty_submission()
    {
        byte[] glb = CoverageImportTests.HostFixture().Build();
        var parts = GltfLoader.LoadSkinnedPartsWithMaterials(glb, "empty-host", CoverageImportTests.Map(glb));
        var error = Assert.Throws<HostCoverageDataException>(() => GltfSkinnedMeshPart.ValidateCoverageParts(parts[0].Mesh.Coverage!, Array.Empty<SkinnedGltfMesh>()));
        Assert.Equal(HostCoverageRefusal.MissingMaterialSlices, error.Reason);
        Assert.Equal("empty-host", error.Identity);
        Assert.Equal(new[] { 0, 1 }, error.MissingMaterialSlices);
        Assert.Equal(0, error.Count);
        Assert.Equal(2, error.Limit);
        GltfSkinnedMeshPart.ValidateCoverageParts(parts[0].Mesh.Coverage!, parts.Select(p => p.Mesh).ToArray());
    }

    [Theory]
    [InlineData("duplicate", HostCoverageRefusal.DuplicateMaterialSlice)]
    [InlineData("source", HostCoverageRefusal.SourceOwnershipMismatch)]
    [InlineData("skeleton", HostCoverageRefusal.SkeletonMismatch)]
    [InlineData("inverse-bind", HostCoverageRefusal.MeshChanged)]
    public void Complete_parts_require_exact_source_and_skin_ownership(string defect, HostCoverageRefusal reason)
    {
        byte[] glb = CoverageImportTests.HostFixture().Build();
        var parts = GltfLoader.LoadSkinnedPartsWithMaterials(glb, "owned-host", CoverageImportTests.Map(glb));
        var meshes = parts.Select(p => p.Mesh).ToArray();
        if (defect == "duplicate") meshes[1] = meshes[0];
        if (defect == "source") meshes[1] = GltfLoader.LoadSkinnedPartsWithMaterials(glb, "other-host", CoverageImportTests.Map(glb))[1].Mesh;
        if (defect == "inverse-bind") meshes[1].InverseBind[0] = Matrix4x4.CreateTranslation(1, 2, 3);
        if (defect == "skeleton")
        {
            var original = meshes[1];
            var rig = original.Skeleton!;
            var otherRig = new Skeleton(rig.ParentIndices.ToArray(), rig.RestLocal.ToArray(),
                rig.NodeLogicalIndex.ToArray(), rig.JointToNode.ToArray(), rig.NodeNames);
            meshes[1] = new SkinnedGltfMesh(original.Vertices, original.Indices32, original.InverseBind, original.RestPose, otherRig)
            { Coverage = original.Coverage };
        }
        Assert.Equal(reason, Assert.Throws<HostCoverageDataException>(() => GltfSkinnedMeshPart.ValidateCoverageParts(parts[0].Mesh.Coverage!, meshes)).Reason);
        var control = GltfLoader.LoadSkinnedPartsWithMaterials(glb, "control-host", CoverageImportTests.Map(glb));
        GltfSkinnedMeshPart.ValidateCoverageParts(control[0].Mesh.Coverage!, control.Select(p => p.Mesh).ToArray());
    }

    [Theory]
    [InlineData("vertex")]
    [InlineData("index")]
    [InlineData("inverse-bind")]
    [InlineData("rest-pose")]
    [InlineData("skeleton")]
    public void Upload_refuses_each_changed_source_before_allocating(string defect)
    {
        byte[] glb = CoverageImportTests.HostFixture().Build();
        var mesh = GltfLoader.LoadSkinnedPartsWithMaterials(glb, "changed-host", CoverageImportTests.Map(glb))[0].Mesh;
        switch (defect)
        {
            case "vertex": mesh.Vertices[0].Position = Vector3.One; break;
            case "index": mesh.Indices32[0] = 0; break;
            case "inverse-bind": mesh.InverseBind[0] = Matrix4x4.CreateTranslation(1, 2, 3); break;
            case "rest-pose": mesh.RestPose[0] = Matrix4x4.CreateTranslation(1, 2, 3); break;
            case "skeleton": mesh.Skeleton!.ParentIndices[0] = 0; break;
        }
        using var device = new FakeGpuDevice();
        using var scene = new Scene3D(device, new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));
        var factory = (FakeGpuResourceFactory)device.Factory;
        int buffers = factory.Buffers.Count;
        var error = Assert.Throws<HostCoverageDataException>(() => scene.LoadSkinnedMesh(mesh));
        Assert.Equal(HostCoverageRefusal.MeshChanged, error.Reason);
        Assert.Equal("changed-host", error.Identity);
        Assert.Equal(0, scene.LiveSkinnedMeshCount);
        Assert.Equal(buffers, factory.Buffers.Count);
        var control = GltfLoader.LoadSkinnedPartsWithMaterials(glb, "changed-host", CoverageImportTests.Map(glb))[0].Mesh;
        scene.LoadSkinnedMesh(control);
        Assert.Equal(1, scene.LiveSkinnedMeshCount);
    }

    [Fact]
    public void Mutation_after_upload_does_not_change_coverage_identity()
    {
        byte[] glb = CoverageImportTests.HostFixture().Build();
        var mesh = GltfLoader.LoadSkinnedPartsWithMaterials(glb, "snapshot-host", CoverageImportTests.Map(glb))[0].Mesh;
        var coverage = mesh.Coverage!;
        using var device = new FakeGpuDevice();
        using var scene = new Scene3D(device, new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm));
        var handle = scene.LoadSkinnedMesh(mesh);
        var before = scene.SkinnedSourceSnapshotForTests(handle);
        byte[] sourceBytes = MemoryMarshal.AsBytes(before.Vertices.AsSpan()).ToArray();
        string identity = before.Coverage!.MeshFingerprint;
        mesh.Vertices[0].Position = new(99);
        mesh.Indices32[0] = 0;
        mesh.InverseBind[0] = Matrix4x4.CreateTranslation(7, 11, 13);
        mesh.RestPose[0] = Matrix4x4.CreateTranslation(17, 19, 23);
        mesh.Skeleton!.ParentIndices[0] = 0;
        bool uploadMatchesMutatedSource = coverage.Matches(mesh);
        Assert.False(uploadMatchesMutatedSource);
        var after = scene.SkinnedSourceSnapshotForTests(handle);
        Assert.Equal(identity, after.Coverage!.MeshFingerprint);
        Assert.True(coverage.Matches(after));
        Assert.Equal(sourceBytes, MemoryMarshal.AsBytes(after.Vertices.AsSpan()).ToArray());
        Assert.Equal(before.Indices32, after.Indices32);
        Assert.Equal(before.InverseBind, after.InverseBind);
        Assert.Equal(before.RestPose, after.RestPose);
        Assert.Equal(before.Skeleton!.ParentIndices, after.Skeleton!.ParentIndices);
        // The inspection seam also returns a copy, never the upload's live mutable arrays.
        before.Vertices[0].Position = Vector3.One;
        Assert.True(coverage.Matches(scene.SkinnedSourceSnapshotForTests(handle)));
    }
}
