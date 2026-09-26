using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>The keyed-draw and collision counts in <see cref="Scene3D.LastTemporalDiagnostics"/>
/// (TEMPORAL-FOUNDATIONS-DESIGN section 5), read after a real fake-device render.</summary>
public sealed class TemporalKeyDiagnosticsTests
{
    [Fact]
    public void The_frames_keyed_draws_and_collisions_reach_the_diagnostics()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.ForceTemporalForTests = true;
        MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1f));
        SkinnedMeshHandle tube = harness.LoadTube(out SkinnedGltfMesh mesh);
        MotionKey a = MotionKey.From(1), b = MotionKey.From(2);

        scene.Begin();
        scene.Draw(new RigidInstanceDraw(box, Matrix4x4.Identity) { Motion = a });
        scene.Draw(new RigidInstanceDraw(box, Matrix4x4.CreateTranslation(1f, 0f, 0f)) { Motion = b });
        scene.Draw(new RigidInstanceDraw(box, Matrix4x4.CreateTranslation(2f, 0f, 0f)) { Motion = a });   // collides
        scene.Draw(box, Matrix4x4.CreateTranslation(3f, 0f, 0f));                                          // unkeyed
        // Rigid and skinned keys are counted in separate maps, so reusing a rigid key here is not a collision.
        scene.DrawSkinned(new SkinnedInstanceDraw(tube, Matrix4x4.Identity) { Motion = a }, mesh.RestPose);
        harness.Render();

        TemporalDiagnostics diagnostics = scene.LastTemporalDiagnostics;
        Assert.Equal(3, diagnostics.KeyedRigid);
        Assert.Equal(1, diagnostics.KeyedSkinned);
        Assert.Equal(1, diagnostics.KeyCollisions);
    }

    [Fact]
    public void A_second_render_in_the_frame_leaves_the_counts_of_the_first()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        scene.ForceTemporalForTests = true;
        MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1f));
        MotionKey a = MotionKey.From(1), b = MotionKey.From(2);

        scene.Begin();
        scene.Draw(new RigidInstanceDraw(box, Matrix4x4.Identity) { Motion = a });
        scene.Draw(new RigidInstanceDraw(box, Matrix4x4.CreateTranslation(1f, 0f, 0f)) { Motion = b });
        harness.Render();
        TemporalDiagnostics first = scene.LastTemporalDiagnostics;
        Assert.Equal(2, first.KeyedRigid);

        scene.Draw(new RigidInstanceDraw(box, Matrix4x4.CreateTranslation(2f, 0f, 0f)) { Motion = a });   // collides
        harness.Render();   // a second render in the same frame, such as an offscreen capture
        Assert.Equal(first, scene.LastTemporalDiagnostics);
    }

    [Fact]
    public void With_temporal_inactive_the_counts_stay_zero()
    {
        using var harness = new MotionTestScene();
        Scene3D scene = harness.Scene;
        MeshHandle box = scene.LoadMesh(MeshPrimitives.Box(1f));

        scene.Begin();
        scene.Draw(new RigidInstanceDraw(box, Matrix4x4.Identity) { Motion = MotionKey.From(1) });
        scene.Draw(new RigidInstanceDraw(box, Matrix4x4.Identity) { Motion = MotionKey.From(1) });
        harness.Render();

        TemporalDiagnostics diagnostics = scene.LastTemporalDiagnostics;
        Assert.Equal(0, diagnostics.KeyedRigid);
        Assert.Equal(0, diagnostics.KeyedSkinned);
        Assert.Equal(0, diagnostics.KeyCollisions);
    }
}
