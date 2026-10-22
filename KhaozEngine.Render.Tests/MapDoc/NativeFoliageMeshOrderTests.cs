using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeFoliageMeshOrderTests
{
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
