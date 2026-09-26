using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>Every grouped rigid slot and every recorded skinned draw carries its submission's motion key, which is
/// what the per-path motion work reads (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24 section 3).</summary>
public sealed class MotionKeyPlumbingTests
{
    [Fact]
    public void GroupingReportsEachSlotsKeyInTheGroupedOrder()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        var a = new MeshHandle(1, 1);
        var b = new MeshHandle(2, 1);
        MotionKey sword = MotionKey.From(1), shield = MotionKey.From(2);

        scene.Begin();
        scene.Draw(new RigidInstanceDraw(a, Matrix4x4.Identity) { Motion = sword });
        scene.Draw(new RigidInstanceDraw(b, Matrix4x4.Identity) { Motion = shield });
        scene.Draw(a, Matrix4x4.CreateTranslation(1f, 0f, 0f));

        var data = new List<ModelRenderer.InstanceData>();
        var runs = new List<Scene3D.MeshRun>();
        var keys = new List<MotionKey>();
        Scene3D.GroupInstances(scene.QueuedInstancesForTests, data, runs, motionKeys: keys);

        // Mesh a's run holds the sword then the unkeyed box, mesh b's run the shield.
        Assert.Equal(new[] { sword, MotionKey.None, shield }, keys);
        Assert.Equal(data.Count, keys.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARecordedSkinnedDrawCarriesItsKey(bool gpuSkinning)
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.UseGpuSkinning = gpuSkinning;
        SkinnedMeshHandle tube = harness.LoadTube(out SkinnedGltfMesh mesh);
        MotionKey body = MotionKey.From(12);

        scene.Begin();
        scene.DrawSkinned(new SkinnedInstanceDraw(tube, Matrix4x4.Identity) { Motion = body }, mesh.RestPose);
        harness.Render();

        if (gpuSkinning) Assert.Equal(body, scene.GpuSkinnedMotionForTests(0));
        else Assert.Equal((body, tube.Index, mesh.Vertices.Length), scene.CpuSkinnedMotionForTests(0));
    }
}
