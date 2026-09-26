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
        // Wide enough that the box is on screen both frames, and the jump stays under an eighth of the image, where
        // the RG16F step doubles. At 31.8 px the x step is 0.0195 px, and on Metal, which rounds toward zero, the
        // error can reach the whole step.
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
    public void AKeyedBodyDrawnAfterUnkeyedOnesOfItsMeshReadsItsOwnPreviousTransform()
    {
        using TemporalFixture fx = Stage(12f);
        MeshHandle crate = fx.Scene.LoadMesh(MeshPrimitives.Box(1.5f));
        MeshHandle box = fx.Scene.LoadMesh(MeshPrimitives.Box(2f));
        Vector3 right = Vector3.Normalize(Vector3.Cross(fx.Scene.Camera.Forward, Vector3.UnitY));
        // Four movers in four lanes across the image, 15 px to the metre, each lane band wider than its body and its
        // motion together.
        Vector3 Lane(float metres, float height) => right * metres + new Vector3(0f, height, 0f);
        Vector3 CrateAt(int n) => Lane(-7f, .75f) + new Vector3(0f, .5f * n, 0f);
        Vector3 FirstAt(int n) => Lane(-2.5f, 1f) + new Vector3(-.25f * n, 0f, .25f * n);
        Vector3 SecondAt(int n) => Lane(2.5f, 1f) + new Vector3(-.25f * n, 0f, .25f * n);
        Vector3 KeyedAt(int n) => Lane(7f, 1f) + right * (.4f * n);
        void Draw(Scene3D s, int n)
        {
            s.Camera.Target = new Vector3(.2f * n, 0f, 0f);
            // The crate's run comes first, so the box run starts at instance 1. Its keyed body follows two unkeyed
            // ones, so it takes instance slot 3 and previous transform 1, after the crate's 0.
            s.Draw(new RigidInstanceDraw(crate, Matrix4x4.CreateTranslation(CrateAt(n))) { Motion = MotionKey.From(8) });
            s.Draw(box, Matrix4x4.CreateTranslation(FirstAt(n)));
            s.Draw(box, Matrix4x4.CreateTranslation(SecondAt(n)));
            s.Draw(new RigidInstanceDraw(box, Matrix4x4.CreateTranslation(KeyedAt(n))) { Motion = Key });
        }

        fx.Frame(Draw);
        IsoCamera3D then = Camera(fx);
        fx.Frame(Draw);
        IsoCamera3D now = Camera(fx);

        Vector2 cameraOnly = MotionExpectation.Moved(now, then, Vector3.Zero, Vector3.Zero, W, H);
        Vector2 crateOwn = MotionExpectation.Moved(now, then, CrateAt(1), CrateAt(0), W, H);
        Vector2 keyedOwn = MotionExpectation.Moved(now, then, KeyedAt(1), KeyedAt(0), W, H);
        Assert.True(Vector2.Distance(crateOwn, cameraOnly) > 5f && Vector2.Distance(keyedOwn, cameraOnly) > 5f
            && Vector2.Distance(keyedOwn, crateOwn) > 5f, "each keyed body must move on screen apart from the rest");
        MotionTargetReadback motion = fx.Scene.ReadMotionTargetForTests();
        (int From, int To, Vector2 Expected)[] lanes =
            [(0, 89, crateOwn), (89, 160, cameraOnly), (160, 231, cameraOnly), (231, W, keyedOwn)];
        foreach ((int from, int to, Vector2 expected) in lanes)
        {
            int drawn = MotionExpectation.AssertDrawnPixels(motion, (_, _) => expected, Line, (x, _) => x >= from && x < to);
            Assert.True(drawn > 100, $"only {drawn} pixels drew in lane {from} to {to}");
        }
    }

    [GpuFact]
    public void AKeyedBodyMovingAcrossACutReadsTheStillCamerasZeroThenItsOwnMotionAgain()
    {
        using TemporalFixture fx = Stage(12f);
        MeshHandle box = fx.Scene.LoadMesh(MeshPrimitives.Box(2f));
        static Vector3 At(int n) => new(-2f + .5f * n, 1f, -.3f * n);
        void Draw(Scene3D s, int n) => s.Draw(new RigidInstanceDraw(box, Matrix4x4.CreateTranslation(At(n))) { Motion = Key });

        fx.Frame(Draw);
        fx.Frame(Draw);
        IsoCamera3D camera = Camera(fx);
        Vector2 own = MotionExpectation.Moved(camera, camera, At(1), At(0), W, H);
        Assert.True(own.Length() > 5f, $"the body moves {own} px");
        MotionExpectation.AssertDrawnPixels(fx.Scene.ReadMotionTargetForTests(), (_, _) => own, Line);

        // The body keeps moving, but the cut frame has no last frame: camera-only motion, and the camera is still.
        fx.Scene.CameraCut();
        fx.Frame(Draw);
        Assert.Null(fx.Scene.PreviousFrameView);
        int drawn = MotionExpectation.AssertDrawnPixels(fx.Scene.ReadMotionTargetForTests(), (_, _) => Vector2.Zero, 0f);
        Assert.True(drawn > 100, $"only {drawn} pixels drew");

        // The frame after reads the cut frame's transform, not the one from before the cut.
        fx.Frame(Draw);
        Assert.NotNull(fx.Scene.PreviousFrameView);
        Vector2 after = MotionExpectation.Moved(camera, camera, At(3), At(2), W, H);
        MotionExpectation.AssertDrawnPixels(fx.Scene.ReadMotionTargetForTests(), (_, _) => after, Line);
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

    // The second of two frames of a box under an overlay shell and a beam, with the camera moving. The beam's far
    // end reaches past the shell's outline, so the beam probe sits under the beam alone and the shell probe, well
    // above the beam, under the shell alone.
    readonly record struct Veiled(byte[] Colour, MotionTargetReadback Motion, IsoCamera3D Then, IsoCamera3D Now, Vector2 Jitter);

    static readonly Vector3 BeamProbe = new(-13f, 0f, 0f), ShellProbe = new(0f, 5.5f, 0f);

    static Veiled RenderVeiled(bool transparent)
    {
        using TemporalFixture fx = Stage(16f);
        MeshHandle box = fx.Scene.LoadMesh(MeshPrimitives.Box(2f));
        MeshHandle shell = fx.Scene.LoadMesh(MeshPrimitives.Box(12f));
        Vector3 toEye = -fx.Scene.Camera.Forward;
        void Draw(Scene3D s, int n)
        {
            s.Camera.Target = new Vector3(.3f * n, 0f, 0f);
            s.Draw(box, Matrix4x4.Identity);
            if (!transparent) return;
            s.DrawOverlayMesh(shell, Matrix4x4.Identity);
            s.DrawBeam(new Vector3(-14f, 0f, 0f) + toEye * 6f, new Vector3(8f, 0f, 0f) + toEye * 6f, .6f, Color.White);
        }

        fx.Frame(Draw);
        IsoCamera3D then = Camera(fx);
        byte[] colour = fx.Frame(Draw);
        return new Veiled(colour, fx.Scene.ReadMotionTargetForTests(), then, Camera(fx), fx.Scene.CurrentFrameView.JitterPixels);
    }

    [GpuFact]
    public void TheBackgroundHoldsTheSentinelAndTheTransparentPassesKeepTheOpaqueMotion()
    {
        Veiled veiled = RenderVeiled(transparent: true), bare = RenderVeiled(transparent: false);
        MotionTargetReadback motion = veiled.Motion;

        // Under the shell and the beam the box keeps its own motion, which is the camera's, bit for bit the motion of
        // the same frames drawn without them.
        int drawn = MotionExpectation.AssertDrawnPixels(motion,
            (x, y) => MotionExpectation.StaticSurface(veiled.Now, veiled.Then, x, y, W, H, veiled.Jitter), Line);
        Assert.True(drawn > 100, $"only {drawn} pixels drew");
        Assert.Equal(bare.Motion.Motion, motion.Motion);

        // Nothing opaque drew under the probes or the corner: the sentinel, exactly. Each probe's colour differs from
        // the bare frame's, so a transparent pass that stopped drawing over its probe fails here.
        var sentinel = new Vector2(MotionMath.Sentinel, MotionMath.Sentinel);
        foreach (Vector3 point in new[] { BeamProbe, ShellProbe })
        {
            Assert.True(veiled.Now.WorldToScreen(point, W, H, out Vector2 pixel));
            int at = (int)pixel.Y * W + (int)pixel.X;
            Assert.Equal(sentinel, motion.Motion[at]);
            int change = 0;
            for (int c = 0; c < 3; c++) change += Math.Abs(veiled.Colour[4 * at + c] - bare.Colour[4 * at + c]);
            Assert.True(change > 96, $"the pass over {point} changed pixel {pixel} by only {change} of 765");
        }
        Assert.Equal(sentinel, motion.Motion[0]);
    }
}
