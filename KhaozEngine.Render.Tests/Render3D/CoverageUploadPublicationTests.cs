using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageUploadPublicationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Equipment_evidence_survives_input_release_and_expires_with_its_mesh_generation(bool incremental)
    {
        using var fixture = new CoverageBindingFixture(materialSlices: true);
        using var binding = fixture.Binding();
        Assert.True(binding.TrySetEquipment([fixture.Equipment()], out _));
        var parts = GltfLoader.LoadPartsWithMaterials(fixture.Source.Bytes["support.glb"], "equipment-upload");
        Assert.Equal(2, parts.Count);
        var resources = new SceneResourceHandle[parts.Count];
        var meshes = new MeshHandle[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            if (incremental)
            {
                var data = new SceneUploadData(parts[i].Mesh, null, null, default);
                using var upload = fixture.Scene.PrepareResourceUpload(data,
                    new NativeUploadReservation(fixture.Scene.MeasureResourceUpload(data)));
                upload.Advance(long.MaxValue);
                Assert.True(upload.Ready);
                Assert.False(upload.HoldsInputsForTests);
                resources[i] = upload.Publish();
                meshes[i] = new SceneResourceAccess(fixture.Scene).MeshOf(resources[i]);
            }
            else meshes[i] = fixture.Scene.LoadMesh(parts[i].Mesh);
        }
        var draws = meshes.Select(mesh => new RigidInstanceDraw(mesh, Matrix4x4.Identity)).ToArray();
        var frozen = new RigidInstanceDraw[draws.Length];
        CoverageEquipmentCommand[] commands = [new(23, Assert.Single(fixture.Coverage.Supports))];
        fixture.Scene.Begin();
        Assert.True(fixture.Scene.TryStageCoverageEquipment(fixture.Actor, 71, commands, draws, frozen, out var failure));
        Assert.Equal(default, failure);
        Assert.All(meshes, mesh => Assert.True(fixture.Scene.CoverageUploadRetainedForTests(mesh)));
        for (int i = 0; i < meshes.Length; i++)
        {
            if (incremental) fixture.Scene.ReleaseResource(resources[i]);
            else fixture.Scene.UnloadMesh(meshes[i]);
            Assert.False(fixture.Scene.CoverageUploadRetainedForTests(meshes[i]));
        }
        var stale = Assert.Throws<CoverageFrameStateException>(() => fixture.Scene.Draw(frozen[0]));
        Assert.Equal(CoverageFailureCode.StaleFrame, stale.Reason);
        Assert.Empty(fixture.Scene.QueuedInstancesForTests);
        fixture.Scene.Begin();
        Assert.False(fixture.Scene.TryStageCoverageEquipment(fixture.Actor, 71, commands, draws, frozen, out failure));
        Assert.Equal(CoverageFailureCode.IdentityMismatch, failure.Code);
        var replacements = parts.Select(_ => fixture.Scene.LoadMesh(NativeResourceUploadTests.Triangle())).ToArray();
        Assert.Equal(meshes.Select(h => h.Index).Order(), replacements.Select(h => h.Index).Order());
        Assert.All(replacements, current => Assert.DoesNotContain(meshes, old => old.Index == current.Index && old.Generation == current.Generation));
        Assert.All(replacements, mesh => Assert.True(fixture.Scene.CoverageUploadRetainedForTests(mesh)));
        var replacementDraws = replacements.Select(mesh => new RigidInstanceDraw(mesh, Matrix4x4.Identity)).ToArray();
        Assert.False(fixture.Scene.TryStageCoverageEquipment(fixture.Actor, 71, commands, replacementDraws, frozen, out failure));
        Assert.Equal(CoverageFailureCode.IdentityMismatch, failure.Code);
        foreach (var mesh in replacements) fixture.Scene.UnloadMesh(mesh);
    }

    [Fact]
    public void Failed_publication_removes_its_witness_before_the_slot_can_be_reused()
    {
        using var fixture = new CoverageBindingFixture();
        var mesh = GltfLoader.LoadPartsWithMaterials(fixture.Source.Bytes["support.glb"], "failed-equipment-upload")[0].Mesh;
        var data = new SceneUploadData(mesh, null, null, default);
        var demand = fixture.Scene.MeasureResourceUpload(data);
        using var failed = fixture.Scene.PrepareResourceUpload(data, new NativeUploadReservation(demand) { FailPublication = true });
        failed.Advance(long.MaxValue);
        Assert.Throws<InvalidOperationException>(() => failed.Publish());
        var failedMesh = failed.PublishedMesh;
        Assert.True(failedMesh.Generation > 0);
        Assert.False(fixture.Scene.CoverageUploadRetainedForTests(failedMesh));
        using var control = fixture.Scene.PrepareResourceUpload(data, new NativeUploadReservation(demand));
        control.Advance(long.MaxValue);
        var resource = control.Publish();
        var accepted = new SceneResourceAccess(fixture.Scene).MeshOf(resource);
        Assert.Equal(failedMesh.Index, accepted.Index);
        Assert.NotEqual(failedMesh.Generation, accepted.Generation);
        Assert.True(fixture.Scene.CoverageUploadRetainedForTests(accepted));
        Assert.False(fixture.Scene.CoverageUploadRetainedForTests(failedMesh));
        fixture.Scene.ReleaseResource(resource);
        Assert.False(fixture.Scene.CoverageUploadRetainedForTests(accepted));
    }
}
