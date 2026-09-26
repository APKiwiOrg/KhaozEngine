using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Skinned motion on a real device through both skinning paths (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24,
/// acceptance 2): a keyed body reports its pose and placement motion, an unkeyed one the camera's alone.</summary>
public sealed class SkinnedMotionGpuTests
{
    const int W = 320, H = 180;
    static readonly MotionKey Key = MotionKey.From(21);

    // Every joint shifts by this in model space, and the body is placed further along each frame.
    static Vector3 PoseShift(int n) => new(.12f * n, .05f * n, 0f);
    static Vector3 Placement(int n, float x) => new(x + .1f * n, 0f, -.08f * n);

    [GpuTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void AKeyedBodyReportsItsPoseAndPlacementMotionAndAnUnkeyedOneOnlyTheCameras(bool gpuSkinning)
    {
        using var fx = new TemporalFixture(W, H, s =>
        {
            s.ForceTemporalForTests = true;
            s.UseGpuSkinning = gpuSkinning;
            s.Camera.OrthoSize = 6f;
        });
        SkinnedGltfMesh mesh = SkinnedMeshBuilder.BuildTube(.3f, 1.5f, 8, 6, 3, Axis.Y);
        SkinnedMeshHandle tube = fx.Scene.LoadSkinnedMesh(mesh);
        Matrix4x4[] bent = MotionTestScene.Bent(mesh, .35f);
        var pose = new Matrix4x4[bent.Length];
        void Draw(Scene3D s, int n)
        {
            for (int b = 0; b < bent.Length; b++) pose[b] = bent[b] * Matrix4x4.CreateTranslation(PoseShift(n));
            // The unkeyed body draws first, so the keyed one sits at a nonzero base vertex in the CPU-skinned streams
            // and at palette slot 1 under GPU skinning. A previous stream or slot misaligned with the current one then
            // shows as wrong motion on the keyed body.
            s.DrawSkinned(new SkinnedInstanceDraw(tube, Matrix4x4.CreateTranslation(Placement(n, 3f))), pose);
            s.DrawSkinned(new SkinnedInstanceDraw(tube, Matrix4x4.CreateTranslation(Placement(n, -3f))) { Motion = Key }, pose);
        }

        fx.Frame(Draw);
        IsoCamera3D then = MotionExpectation.Snapshot(fx.Scene.Camera);
        fx.Frame(Draw);
        IsoCamera3D now = MotionExpectation.Snapshot(fx.Scene.Camera);

        Vector3 moved = PoseShift(1) - PoseShift(0) + Placement(1, 0f) - Placement(0, 0f);
        Vector2 expected = MotionExpectation.Moved(now, then, moved, Vector3.Zero, W, H);
        Assert.True(expected.Length() > 5f, $"the keyed body moves {expected} px");

        MotionTargetReadback motion = fx.Scene.ReadMotionTargetForTests();
        // The keyed body sits left of the centre and the unkeyed one right of it, further apart than either is wide.
        int keyed = MotionExpectation.AssertDrawnPixels(motion, (_, _) => expected, .05f, (x, _) => x < W / 2);
        int unkeyed = MotionExpectation.AssertDrawnPixels(motion, (_, _) => Vector2.Zero, .05f, (x, _) => x >= W / 2);
        Assert.True(keyed > 100 && unkeyed > 100, $"{keyed} keyed and {unkeyed} unkeyed pixels drew");
    }
}
