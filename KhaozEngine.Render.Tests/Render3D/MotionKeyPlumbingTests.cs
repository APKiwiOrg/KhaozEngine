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

    [Fact]
    public void CulledSlotsTakeNoKeyAndEveryKeptSlotKeepsItsOwn()
    {
        // Instance k sits at x = k and carries key k + 1, so a slot's translation names the key it must hold. Meshes
        // interleave and two instances are culled, so a key written in submission or run order would misalign.
        var a = new MeshHandle(1, 1);
        var b = new MeshHandle(2, 1);
        MeshHandle[] meshes = [a, b, a, b, a, b];
        var items = new SceneInstances.Instance[meshes.Length];
        for (int k = 0; k < items.Length; k++)
        {
            var draw = new RigidInstanceDraw(meshes[k], Matrix4x4.CreateTranslation(k, 0f, 0f))
                { Motion = MotionKey.From((ulong)k + 1) };
            items[k] = new SceneInstances.Instance(in draw);
        }
        bool[] retained = [false, true, true, false, true, true];

        var data = new List<ModelRenderer.InstanceData>();
        var runs = new List<Scene3D.MeshRun>();
        var keys = new List<MotionKey>();
        var cursors = new List<uint>();
        Scene3D.GroupInstances(items, data, runs, retained: retained, writeCursorScratch: cursors, motionKeys: keys);

        Assert.Equal(4, data.Count);
        Assert.Equal(data.Count, keys.Count);
        for (int slot = 0; slot < data.Count; slot++)
            Assert.Equal(MotionKey.From((ulong)data[slot].Model.M41 + 1), keys[slot]);
        Assert.DoesNotContain(MotionKey.From(1), keys);   // instance 0 was culled
        Assert.DoesNotContain(MotionKey.From(4), keys);   // instance 3 was culled

        Scene3D.GroupInstances(System.Array.Empty<SceneInstances.Instance>(), data, runs, motionKeys: keys);
        Assert.Empty(keys);   // an empty frame leaves no stale keys behind
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARecordedSkinnedDrawCarriesItsKey(bool gpuSkinning)
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.UseGpuSkinning = gpuSkinning;
        harness.LoadTube(out _);   // an undrawn mesh first, so the drawn one's slot is not zero like every other index
        SkinnedMeshHandle tube = harness.LoadTube(out SkinnedGltfMesh mesh);
        Assert.Equal(1, tube.Index);
        MotionKey body = MotionKey.From(12);

        scene.Begin();
        scene.DrawSkinned(new SkinnedInstanceDraw(tube, Matrix4x4.Identity) { Motion = body }, mesh.RestPose);
        harness.Render();

        if (gpuSkinning) Assert.Equal(body, scene.GpuSkinnedMotionForTests(0));
        else Assert.Equal((body, tube.Index, mesh.Vertices.Length), scene.CpuSkinnedMotionForTests(0));
    }
}
