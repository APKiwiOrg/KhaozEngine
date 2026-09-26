using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// The motion target on a real device against analytic motion, within 0.05 internal pixels at every pixel opaque
/// geometry drew (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24, acceptance 2): a moving camera, a keyed rigid body, a 10 m
/// keyed teleport, an unkeyed mover, a render origin step and an orthographic zoom, plus the zero cases and the
/// background sentinel under the transparent passes.
/// </summary>
public sealed class MotionTargetGpuTests
{
    const int W = 320, H = 180;
    const float Line = .05f;
    static readonly MotionKey Key = MotionKey.From(7);

    static TemporalFixture Stage(float orthoSize) => new(W, H, s =>
    {
        s.ForceTemporalForTests = true;
        s.Camera.OrthoSize = orthoSize;
    });

    // A floor and two boxes that fill most of the image. Static.
    sealed class Yard
    {
        readonly MeshHandle _floor, _box;

        internal Yard(Scene3D scene)
        {
            _floor = scene.LoadMesh(MeshPrimitives.Plane(60f, 60f));
            _box = scene.LoadMesh(MeshPrimitives.Box(2f));
        }

        internal void Draw(Scene3D s)
        {
            s.Draw(_floor, Matrix4x4.Identity);
            s.Draw(_box, Matrix4x4.CreateTranslation(-3f, 1f, 2f));
            s.Draw(_box, Matrix4x4.CreateRotationY(.5f) * Matrix4x4.CreateTranslation(3f, 1f, -1f));
        }
    }

    static IsoCamera3D Camera(TemporalFixture fx) => MotionExpectation.Snapshot(fx.Scene.Camera);

    [GpuFact]
    public void AMovingCameraOverAStaticSceneReportsItsMotionAtEveryPixel()
    {
        using TemporalFixture fx = Stage(12f);
        var yard = new Yard(fx.Scene);
        void Draw(Scene3D s, int n)
        {
            s.Camera.Target = new Vector3(.35f * n, 0f, -.2f * n);
            yard.Draw(s);
        }

        fx.Frame(Draw);
        IsoCamera3D then = Camera(fx);
        fx.Frame(Draw);
        IsoCamera3D now = Camera(fx);
        Vector2 jitter = fx.Scene.CurrentFrameView.JitterPixels;

        MotionTargetReadback motion = fx.Scene.ReadMotionTargetForTests();
        Assert.True(MotionExpectation.StaticSurface(now, then, 0, 0, W, H, jitter).Length() > 2f);
        int drawn = MotionExpectation.AssertDrawnPixels(motion, (x, y) => MotionExpectation.StaticSurface(now, then, x, y, W, H, jitter), Line);
        Assert.True(drawn > W * H / 2, $"only {drawn} pixels drew");
    }

    [GpuFact]
    public void AStillCameraReportsNoMotionWhileTheJitterMovesEveryFrame()
    {
        using TemporalFixture fx = Stage(12f);
        var yard = new Yard(fx.Scene);
        fx.Frame((s, _) => yard.Draw(s));
        for (int i = 0; i < 4; i++)
        {
            Vector2 before = fx.Scene.CurrentFrameView.JitterPixels;
            fx.Frame((s, _) => yard.Draw(s));
            Assert.NotEqual(before, fx.Scene.CurrentFrameView.JitterPixels);
            // Zero to float rounding: both matrices are the same camera's, so jitter never reaches the motion.
            MotionExpectation.AssertDrawnPixels(fx.Scene.ReadMotionTargetForTests(), (_, _) => Vector2.Zero, 1e-3f);
        }
    }

    [GpuFact]
    public void TheFirstFrameAndTheFrameAfterACutReportExactlyZero()
    {
        using TemporalFixture fx = Stage(12f);
        var yard = new Yard(fx.Scene);
        void Draw(Scene3D s, int n)
        {
            s.Camera.Target = new Vector3(.35f * n, 0f, 0f);
            yard.Draw(s);
        }

        fx.Frame(Draw);   // no history yet
        Assert.Null(fx.Scene.PreviousFrameView);
        MotionExpectation.AssertDrawnPixels(fx.Scene.ReadMotionTargetForTests(), (_, _) => Vector2.Zero, 0f);

        fx.Frame(Draw);
        Assert.NotNull(fx.Scene.PreviousFrameView);

        fx.Scene.CameraCut();
        fx.Frame(Draw);
        Assert.Null(fx.Scene.PreviousFrameView);
        MotionExpectation.AssertDrawnPixels(fx.Scene.ReadMotionTargetForTests(), (_, _) => Vector2.Zero, 0f);
    }

    [GpuFact]
    public void AKeyedRigidBodyReportsItsOwnMotionOverTheCameras()
    {
        using TemporalFixture fx = Stage(12f);
        MeshHandle box = fx.Scene.LoadMesh(MeshPrimitives.Box(2f));
        static Vector3 At(int n) => new(-1f + .4f * n, 1f, -.3f * n);
        void Draw(Scene3D s, int n)
        {
            s.Camera.Target = new Vector3(.2f * n, 0f, 0f);
            s.Draw(new RigidInstanceDraw(box, Matrix4x4.CreateTranslation(At(n))) { Motion = Key });
        }

        fx.Frame(Draw);
        IsoCamera3D then = Camera(fx);
        fx.Frame(Draw);
        IsoCamera3D now = Camera(fx);

        Vector2 expected = MotionExpectation.Moved(now, then, At(1), At(0), W, H);
        Vector2 cameraOnly = MotionExpectation.Moved(now, then, At(1), At(1), W, H);
        Assert.True(Vector2.Distance(expected, cameraOnly) > 5f, "the body must move on screen apart from the camera");
        int drawn = MotionExpectation.AssertDrawnPixels(fx.Scene.ReadMotionTargetForTests(), (_, _) => expected, Line);
        Assert.True(drawn > 100, $"only {drawn} pixels drew");
    }

    [GpuFact]
    public void AKeyedRigidBodyThatJumpsTenMetresInOneFrameReportsTheWholeJump()
    {
        // Wide enough that the box is on screen both frames, and the jump stays under a tenth of the image, where
        // RG16F resolves a hundredth of a pixel.
        using TemporalFixture fx = Stage(40f);
        MeshHandle box = fx.Scene.LoadMesh(MeshPrimitives.Box(3f));
        static Vector3 At(int n) => new(n == 0 ? -5f : 5f, 1.5f, 0f);
        void Draw(Scene3D s, int n) => s.Draw(new RigidInstanceDraw(box, Matrix4x4.CreateTranslation(At(n))) { Motion = Key });

        fx.Frame(Draw);
        IsoCamera3D then = Camera(fx);
        fx.Frame(Draw);
        IsoCamera3D now = Camera(fx);

        Assert.NotNull(fx.Scene.PreviousFrameView);   // a body teleporting is not a camera cut
        Vector2 jump = MotionExpectation.Moved(now, then, At(1), At(0), W, H);
        Assert.True(jump.Length() > 30f, $"the jump spans {jump} px");
        int drawn = MotionExpectation.AssertDrawnPixels(fx.Scene.ReadMotionTargetForTests(), (_, _) => jump, Line);
        Assert.True(drawn > 100, $"only {drawn} pixels drew");
    }

    [GpuFact]
    public void AnUnkeyedMoverReportsOnlyTheCamerasMotion()
    {
        using TemporalFixture fx = Stage(12f);
        MeshHandle box = fx.Scene.LoadMesh(MeshPrimitives.Box(2f));
        static Vector3 At(int n) => new(.6f * n, 1f, -.4f * n);
        void Draw(Scene3D s, int n)
        {
            s.Camera.Target = new Vector3(-.3f * n, 0f, .1f * n);
            s.Draw(box, Matrix4x4.CreateTranslation(At(n)));
        }

        fx.Frame(Draw);
        IsoCamera3D then = Camera(fx);
        fx.Frame(Draw);
        IsoCamera3D now = Camera(fx);
        Vector2 jitter = fx.Scene.CurrentFrameView.JitterPixels;

        Assert.True(Vector2.Distance(MotionExpectation.Moved(now, then, At(1), At(0), W, H),
            MotionExpectation.Moved(now, then, At(1), At(1), W, H)) > 5f, "the mover's own motion must be visible");
        int drawn = MotionExpectation.AssertDrawnPixels(fx.Scene.ReadMotionTargetForTests(),
            (x, y) => MotionExpectation.StaticSurface(now, then, x, y, W, H, jitter), Line);
        Assert.True(drawn > 100, $"only {drawn} pixels drew");
    }

    [GpuFact]
    public void AStaticSceneAcrossARenderOriginStepReportsNoMotion()
    {
        using TemporalFixture fx = Stage(12f);
        var yard = new Yard(fx.Scene);
        fx.Frame((s, _) => yard.Draw(s));

        var step = new Vector3(128f, 0f, -128f);
        fx.Scene.RenderOrigin = step;   // latched at the next Begin
        fx.Frame((s, _) => yard.Draw(s));

        Assert.Equal(step, fx.Scene.CurrentFrameView.RenderOrigin);
        Assert.NotNull(fx.Scene.PreviousFrameView);   // an origin step keeps history
        int drawn = MotionExpectation.AssertDrawnPixels(fx.Scene.ReadMotionTargetForTests(), (_, _) => Vector2.Zero, Line);
        Assert.True(drawn > W * H / 2, $"only {drawn} pixels drew");
    }

    [GpuFact]
    public void AnOrthographicZoomOverAStaticSceneReportsEachPixelsOwnMotion()
    {
        using TemporalFixture fx = Stage(12f);
        var yard = new Yard(fx.Scene);
        void Draw(Scene3D s, int n)
        {
            s.Camera.Zoom = n == 0 ? 1f : 1.25f;
            yard.Draw(s);
        }

        fx.Frame(Draw);
        IsoCamera3D then = Camera(fx);
        fx.Frame(Draw);
        IsoCamera3D now = Camera(fx);
        Vector2 jitter = fx.Scene.CurrentFrameView.JitterPixels;

        Assert.True(fx.Scene.CurrentFrameView.IsOrthographic);
        Assert.NotNull(fx.Scene.PreviousFrameView);   // a zoom is a projection change, not a cut
        // Zero at the centre and tens of pixels at the edges: only a per-pixel comparison proves the field. Its y
        // component changes sign across the centre row, so this is the case that pins the readback's row order.
        Assert.True(MotionExpectation.StaticSurface(now, then, 0, 0, W, H, jitter).Length() > 20f);
        int drawn = MotionExpectation.AssertDrawnPixels(fx.Scene.ReadMotionTargetForTests(),
            (x, y) => MotionExpectation.StaticSurface(now, then, x, y, W, H, jitter), Line);
        Assert.True(drawn > W * H / 2, $"only {drawn} pixels drew");
    }

    [GpuFact]
    public void TheBackgroundHoldsTheSentinelAndTheTransparentPassesKeepTheOpaqueMotion()
    {
        using TemporalFixture fx = Stage(16f);
        MeshHandle box = fx.Scene.LoadMesh(MeshPrimitives.Box(2f));
        MeshHandle shell = fx.Scene.LoadMesh(MeshPrimitives.Box(12f));
        Vector3 toEye = -fx.Scene.Camera.Forward;
        void Draw(Scene3D s, int n)
        {
            s.Camera.Target = new Vector3(.3f * n, 0f, 0f);
            s.Draw(box, Matrix4x4.Identity);
            // A translucent shell around the box and a beam across it, both in front of it and both reaching far over
            // the empty background.
            s.DrawOverlayMesh(shell, Matrix4x4.Identity);
            s.DrawBeam(new Vector3(-8f, 0f, 0f) + toEye * 6f, new Vector3(8f, 0f, 0f) + toEye * 6f, .6f, Color.White);
        }

        fx.Frame(Draw);
        IsoCamera3D then = Camera(fx);
        fx.Frame(Draw);
        IsoCamera3D now = Camera(fx);
        Vector2 jitter = fx.Scene.CurrentFrameView.JitterPixels;
        MotionTargetReadback motion = fx.Scene.ReadMotionTargetForTests();

        // Under the shell and the beam the box keeps its own motion, which is the camera's.
        int drawn = MotionExpectation.AssertDrawnPixels(motion,
            (x, y) => MotionExpectation.StaticSurface(now, then, x, y, W, H, jitter), Line);
        Assert.True(drawn > 100, $"only {drawn} pixels drew");

        // Nothing opaque drew under the beam's far end, the shell's top or the corner: the sentinel, exactly.
        var sentinel = new Vector2(MotionMath.Sentinel, MotionMath.Sentinel);
        foreach (Vector3 point in new[] { new Vector3(-7f, 0f, 0f), new Vector3(0f, 5.5f, 0f) })
        {
            Assert.True(now.WorldToScreen(point, W, H, out Vector2 pixel));
            Assert.Equal(sentinel, motion.Motion[(int)pixel.Y * W + (int)pixel.X]);
        }
        Assert.Equal(sentinel, motion.Motion[0]);
    }
}
