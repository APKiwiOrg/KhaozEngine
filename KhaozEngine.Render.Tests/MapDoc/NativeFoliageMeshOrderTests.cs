using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeFoliageMeshOrderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeldUploads_OriginResidencyPreservesPartOrder(bool reverse)
    {
        using var device = new ReadbackRingFakeDevice();
        using var host = new NativeUploadTestScene(device);
        using var setup = NativeMapGoldenSetup.Create("origin");
        var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(setup.State, new MapScene3DSink(host.Scene), new(MaxAppliesPerUpdate: 64), scheduler);
        scheduler.RunAll();
        Assert.True(residency.Update().Pending > 0);
        var fences = device.SubmittedFences.Cast<ManualFence>().ToArray();
        Assert.NotEmpty(fences);
        foreach (var fence in reverse ? fences.Reverse() : fences)
        {
            fence.Signal();
            residency.Update();
        }
        residency.Update();
        foreach (ManualFence fence in device.SubmittedFences) fence.Signal();
        residency.Update();
        Assert.Equal(0, residency.Update().Pending);
        var recorder = new MapDrawRecorder(); residency.Record(setup.Frame, recorder);
        var resources = recorder.Snapshot().Resources.Where(r => r.Payload is MapFoliageResource).ToArray();
        Assert.NotEmpty(resources);
        foreach (var resource in resources)
        {
            Assert.True(residency.TryGetHandle(resource.Reference, out var handle));
            var prepared = host.Scene.ResourceOf(new(handle.Value)).PreparedFoliage!;
            var source = (MapFoliageResource)resource.Payload;
            var cells = prepared.Patches.GroupBy(p => (p.CellX, p.CellZ)).Where(c => c.Count() == 3).ToArray();
            Assert.NotEmpty(cells);
            foreach (var cell in cells)
                Assert.Equal(new[] { ("B", 0), ("A", 0), ("A", 1) }, cell.Select(p =>
                {
                    var part = prepared.ExpandedParts[p.Start];
                    return (source.Models[part.ModelIndex].ModelId, part.PartIndex);
                }));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void HeldUploads_PreservePreparationOrderWhenCompletionReverses(bool recycle, bool reverse)
    {
        using var device = new ReadbackRingFakeDevice();
        using var host = new NativeUploadTestScene(device);
        if (recycle)
            foreach (var mesh in Load(host.Scene, false)) host.Scene.UnloadMesh(mesh);
        var uploads = NativeRenderAssetFixtures.FoliageParts().Select(part =>
        {
            var data = new SceneUploadData(part.Mesh, null, null, default);
            return host.Scene.PrepareResourceUpload(data, new NativeUploadReservation(host.Scene.MeasureResourceUpload(data)));
        }).ToArray();
        foreach (var upload in uploads) upload.Advance(long.MaxValue);
        Assert.All(uploads, upload => Assert.False(upload.Ready));
        var fences = device.SubmittedFences.Cast<ManualFence>().ToArray();
        Assert.Equal(3, fences.Length);
        var meshes = new MeshHandle[3];
        foreach (int index in reverse ? new[] { 2, 1, 0 } : new[] { 0, 1, 2 })
        {
            fences[index].Signal();
            Assert.True(uploads[index].Ready);
            meshes[index] = new SceneResourceAccess(host.Scene).MeshOf(uploads[index].Publish());
        }
        var source = new SceneFoliageSource(default,
            Enumerable.Repeat(Matrix4x4.CreateTranslation(1, 0, 1), 4).ToArray(),
            new float[] { .1f, .2f, .3f, .4f }, new[] { 0, 1, 0, 1 },
            [new("A", [meshes[0], meshes[1]], FoliageMotionProfile.Blade, true),
             new("B", [meshes[2]], FoliageMotionProfile.Blade, false)]);
        var prepared = host.Scene.PrepareFoliageUpload(source, new(new string('a', 64), 0, 0, 0, 0, 0));
        Assert.Equal(new[] { (0, 0, 0), (2, 0, 0), (0, 0, 1), (2, 0, 1), (1, 1, 0), (3, 1, 0) },
            prepared.ExpandedParts.Select(p => (p.SourceRow, p.ModelIndex, p.PartIndex)));
        Assert.Equal(meshes, prepared.Patches.Select(p => p.Mesh));
        using var retained = host.Scene.CreateFoliageBatch(
            [new(meshes[2], Matrix4x4.Identity, .1f), new(meshes[0], Matrix4x4.Identity, .9f),
             new(meshes[1], Matrix4x4.Identity, .3f), new(meshes[0], Matrix4x4.Identity, .2f)]);
        Assert.Equal(new[] { 3, 1, 2, 0 }, retained.Layout.InputOrder);
        Assert.Equal(meshes, retained.Layout.Patches.Select(p => p.Mesh));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparedMultipartOrder_PreservesAdmissionOrderAcrossSlotReuse(bool recycle)
    {
        using var host = new NativeUploadTestScene(); var meshes = Load(host.Scene, recycle);
        var source = new SceneFoliageSource(default,
            Enumerable.Repeat(Matrix4x4.CreateTranslation(1, 0, 1), 4).ToArray(),
            new float[] { .1f, .2f, .3f, .4f }, new[] { 0, 1, 0, 1 },
            [new("A", [meshes[0], meshes[1]], FoliageMotionProfile.Blade, true),
             new("B", [meshes[2]], FoliageMotionProfile.Blade, false)]);
        var prepared = host.Scene.PrepareFoliageUpload(source, new(new string('a', 64), 0, 0, 0, 0, 0));
        Assert.Equal(new[] { (0, 0, 0), (2, 0, 0), (0, 0, 1), (2, 0, 1), (1, 1, 0), (3, 1, 0) },
            prepared.ExpandedParts.Select(p => (p.SourceRow, p.ModelIndex, p.PartIndex)));
        Assert.Equal(meshes, prepared.Patches.Select(p => p.Mesh));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetainedPatchOrder_PreservesAdmissionOrderAndRankAcrossSlotReuse(bool recycle)
    {
        using var host = new NativeUploadTestScene(); var meshes = Load(host.Scene, recycle);
        using var batch = host.Scene.CreateFoliageBatch(
            [new(meshes[2], Matrix4x4.Identity, .1f), new(meshes[0], Matrix4x4.Identity, .9f),
             new(meshes[1], Matrix4x4.Identity, .3f), new(meshes[0], Matrix4x4.Identity, .2f)]);
        Assert.Equal(new[] { 3, 1, 2, 0 }, batch.Layout.InputOrder);
        Assert.Equal(meshes, batch.Layout.Patches.Select(p => p.Mesh));
    }

    static MeshHandle[] Load(Scene3D scene, bool recycle)
    {
        var parts = NativeRenderAssetFixtures.FoliageParts();
        if (recycle)
        {
            var previous = parts.Select(p => scene.LoadMesh(p.Mesh)).ToArray();
            foreach (var mesh in previous) scene.UnloadMesh(mesh);
        }
        return parts.Select(p => scene.LoadMesh(p.Mesh)).ToArray();
    }
}
