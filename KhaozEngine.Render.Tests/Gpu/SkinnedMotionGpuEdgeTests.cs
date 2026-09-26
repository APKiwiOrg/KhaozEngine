using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Skinned motion on a real device through both skinning paths where the previous state is harder to reach
/// (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24, acceptance 2): a render origin that steps between the frames, and a key
/// whose last palette has another bone count.</summary>
public sealed class SkinnedMotionGpuEdgeTests
{
    const int W = 320, H = 180;
    static readonly MotionKey Key = MotionKey.From(22);

    static TemporalFixture Stage(bool gpuSkinning) => new(W, H, s =>
    {
        s.ForceTemporalForTests = true;
        s.UseGpuSkinning = gpuSkinning;
        s.Camera.OrthoSize = 6f;
    });

    static SkinnedGltfMesh Tube(int bones) => SkinnedMeshBuilder.BuildTube(.3f, 1.5f, 8, 6, bones, Axis.Y);

    [GpuTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void SkinnedMotionHoldsAcrossARenderOriginThatStepsBetweenTheFrames(bool gpuSkinning)
    {
        using TemporalFixture fx = Stage(gpuSkinning);
        SkinnedGltfMesh mesh = Tube(3);
        SkinnedMeshHandle tube = fx.Scene.LoadSkinnedMesh(mesh);
        Matrix4x4[] bent = MotionTestScene.Bent(mesh, .35f);
        var pose = new Matrix4x4[bent.Length];
        // Far enough from the world origin that the render-relative positions differ from the absolute ones, and
        // between the two origins below, both on the 128 m grid.
        var site = new Vector3(300f, 0f, -300f);
        static Vector3 PoseShift(int n) => new(.12f * n, .05f * n, 0f);
        static Vector3 Placement(int n, float x) => new(x + .1f * n, 0f, -.08f * n);
        void Draw(Scene3D s, int n)
        {
            s.Camera.Target = site + new Vector3(.08f * n, 0f, .05f * n);
            for (int b = 0; b < bent.Length; b++) pose[b] = bent[b] * Matrix4x4.CreateTranslation(PoseShift(n));
            s.DrawSkinned(new SkinnedInstanceDraw(tube, Matrix4x4.CreateTranslation(site + Placement(n, 3f))), pose);
            s.DrawSkinned(new SkinnedInstanceDraw(tube, Matrix4x4.CreateTranslation(site + Placement(n, -3f))) { Motion = Key }, pose);
        }

        fx.Scene.RenderOrigin = new Vector3(256f, 0f, -256f);
        fx.Frame(Draw);
        IsoCamera3D then = MotionExpectation.Snapshot(fx.Scene.Camera);
        var origin = new Vector3(384f, 0f, -384f);
        fx.Scene.RenderOrigin = origin;
        fx.Frame(Draw);
        IsoCamera3D now = MotionExpectation.Snapshot(fx.Scene.Camera);

        Assert.Equal(origin, fx.Scene.CurrentFrameView.RenderOrigin);
        Assert.NotNull(fx.Scene.PreviousFrameView);
        Vector3 moved = PoseShift(1) - PoseShift(0) + Placement(1, 0f) - Placement(0, 0f);
        Vector2 cameraOnly = MotionExpectation.Moved(now, then, site, site, W, H);
        Vector2 keyedOwn = MotionExpectation.Moved(now, then, site + moved, site, W, H);
        Assert.True(cameraOnly.Length() > 1f && Vector2.Distance(keyedOwn, cameraOnly) > 5f,
            $"the camera moves {cameraOnly} px and the keyed body {keyedOwn} px");

        MotionTargetReadback motion = fx.Scene.ReadMotionTargetForTests();
        int keyed = MotionExpectation.AssertDrawnPixels(motion, (_, _) => keyedOwn, .05f, (x, _) => x < W / 2);
        int unkeyed = MotionExpectation.AssertDrawnPixels(motion, (_, _) => cameraOnly, .05f, (x, _) => x >= W / 2);
        Assert.True(keyed > 100 && unkeyed > 100, $"{keyed} keyed and {unkeyed} unkeyed pixels drew");
    }

    [GpuTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void AKeyThatMovesToAMeshWithAnotherBoneCountReadsCameraOnlyMotionFromItsOwnSkin(bool gpuSkinning)
    {
        using TemporalFixture fx = Stage(gpuSkinning);
        SkinnedGltfMesh threeBones = Tube(3), fiveBones = Tube(5);
        SkinnedMeshHandle before = fx.Scene.LoadSkinnedMesh(threeBones), after = fx.Scene.LoadSkinnedMesh(fiveBones);
        Matrix4x4[] beforePose = MotionTestScene.Bent(threeBones, .35f), afterPose = MotionTestScene.Bent(fiveBones, .2f);
        static Vector3 At(int n) => new(.25f * n, 0f, -.2f * n);
        void Draw(Scene3D s, int n)
        {
            s.Camera.Target = new Vector3(.15f * n, 0f, -.1f * n);
            // The key moves to a mesh with more bones, so its last palette has the wrong length for this draw.
            if (n == 0) s.DrawSkinned(new SkinnedInstanceDraw(before, Matrix4x4.CreateTranslation(At(n))) { Motion = Key }, beforePose);
            else s.DrawSkinned(new SkinnedInstanceDraw(after, Matrix4x4.CreateTranslation(At(n))) { Motion = Key }, afterPose);
        }

        fx.Frame(Draw);
        IsoCamera3D then = MotionExpectation.Snapshot(fx.Scene.Camera);
        fx.Frame(Draw);
        IsoCamera3D now = MotionExpectation.Snapshot(fx.Scene.Camera);

        Assert.NotNull(fx.Scene.PreviousFrameView);
        Vector2 cameraOnly = MotionExpectation.Moved(now, then, Vector3.Zero, Vector3.Zero, W, H);
        Vector2 bodyOwn = MotionExpectation.Moved(now, then, At(1), At(0), W, H);
        Assert.True(cameraOnly.Length() > 2f && Vector2.Distance(bodyOwn, cameraOnly) > 5f,
            $"the camera moves {cameraOnly} px and the body {bodyOwn} px");
        int drawn = MotionExpectation.AssertDrawnPixels(fx.Scene.ReadMotionTargetForTests(), (_, _) => cameraOnly, .05f);
        Assert.True(drawn > 100, $"only {drawn} pixels drew");
    }
}
