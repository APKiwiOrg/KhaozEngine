using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using Xunit.Abstractions;
using FoliageUniforms = KhaozEngine.Render3D.Rendering.ModelRenderer.FoliageUniforms;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// The motion target on a real device under a perspective <see cref="FlyCamera3D"/>, against
/// <see cref="PerspectiveExpectation"/> at every pixel opaque geometry drew, within 0.05 internal pixels
/// (TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24, acceptance 2). The orthographic readbacks see a clip w of 1 everywhere. These
/// prove what only a perspective camera exercises: last frame's clip position interpolated against a varying w, the
/// foliage wind fade's clip w, the write's guard for a last-frame w at or below zero, and its clamp to two screens for a
/// last-frame w just past the guard.
/// </summary>
public sealed class PerspectiveMotionGpuTests(ITestOutputHelper output)
{
    const int W = 320, H = 180;
    const float Line = .05f;
    static readonly MotionKey Key = MotionKey.From(7);

    // The camera's default 60 degree field of view at the fixture's aspect, at the origin looking along +Z.
    static FlyCamera3D Fly() => new() { AspectRatio = (float)W / H };

    static TemporalFixture Stage(FlyCamera3D camera) => new(W, H, s =>
    {
        s.ForceTemporalForTests = true;
        s.CameraOverride = camera;
    });

    MotionScan Hold(string name, MotionTargetReadback motion, Func<int, int, Vector2> expected, (int X, int Y) probe)
    {
        MotionScan scan = MotionExpectation.HoldDrawnPixels(motion, expected, Line);
        output.WriteLine($"{name}: {scan.Count} px, worst {scan.Worst:F4} px at {scan.At} reading {scan.Reported} for "
            + $"{scan.Expected}. At {probe} it read {motion.PixelsAt(probe.X, probe.Y)} for {expected(probe.X, probe.Y)}.");
        return scan;
    }

    [GpuFact]
    public void ADollyAndPanOverAStaticPlaneReportsEachPixelsPerspectiveMotion()
    {
        FlyCamera3D camera = Fly();
        using TemporalFixture fx = Stage(camera);
        MeshHandle floor = fx.Scene.LoadMesh(MeshPrimitives.Plane(120f, 120f));   // two triangles under the whole image
        const float Pitch = -.6f;
        var start = new Vector3(0f, 3f, -10f);
        Vector3 dolly = Pinhole.Of(new FlyCamera3D { Pitch = Pitch }).Forward * .4f;
        void Draw(Scene3D s, int n)
        {
            camera.Position = start + dolly * n;   // forward along the look and down with it
            camera.Yaw = .05f * n;
            camera.Pitch = Pitch;
            s.Draw(floor, Matrix4x4.Identity);
        }

        fx.Frame(Draw);
        Pinhole then = Pinhole.Of(camera);
        fx.Frame(Draw);
        Pinhole now = Pinhole.Of(camera);
        Vector2 jitter = fx.Scene.CurrentFrameView.JitterPixels;
        Assert.False(fx.Scene.CurrentFrameView.IsOrthographic);
        Assert.NotNull(fx.Scene.PreviousFrameView);

        Vector2 Expected(int x, int y) => PerspectiveExpectation.StaticPlane(now, then, x, y, W, H, jitter, Vector3.UnitY, 0f);
        // Down the centre column the motion bends: the middle row is pixels off the line through rows 1 and 179, which
        // no affine field does, so a camera read as orthographic cannot pass.
        Vector2 top = Expected(W / 2, 1), middle = Expected(W / 2, 90), bottom = Expected(W / 2, H - 1);
        Assert.True(Vector2.Distance(middle, (top + bottom) / 2f) > 2f, $"the column reads {top}, {middle}, {bottom}");
        output.WriteLine($"centre column expects {top} at row 1, {middle} at row 90 and {bottom} at row 179");
        MotionScan scan = Hold("static plane", fx.Scene.ReadMotionTargetForTests(), Expected, (W / 2, H - 1));
        Assert.Equal(W * H, scan.Count);   // the floor fills the image, so no pixel passes by reading as background
    }

    [GpuFact]
    public void AKeyedBoxUnderAMovingPerspectiveCameraReportsItsMotionAtEveryPixel()
    {
        FlyCamera3D camera = Fly();
        camera.Pitch = -.3f;
        using TemporalFixture fx = Stage(camera);
        MeshHandle box = fx.Scene.LoadMesh(MeshPrimitives.Box(2f));
        static Vector3 At(int n) => new(-.6f + .5f * n, 1f, 2f - .4f * n);
        void Draw(Scene3D s, int n)
        {
            camera.Position = new Vector3(.3f * n, 2.5f, -3f);
            s.Draw(new RigidInstanceDraw(box, Matrix4x4.CreateTranslation(At(n))) { Motion = Key });
        }

        fx.Frame(Draw);
        Pinhole then = Pinhole.Of(camera);
        fx.Frame(Draw);
        Pinhole now = Pinhole.Of(camera);
        Vector2 jitter = fx.Scene.CurrentFrameView.JitterPixels;

        Vector3 half = Vector3.One;
        Vector2 Expected(int x, int y) => PerspectiveExpectation.MovedBox(now, then, x, y, W, H, jitter, At(1), At(0), half);
        Vector2 centre = now.Project(At(1), W, H);
        (int X, int Y) probe = ((int)centre.X, (int)centre.Y);
        Vector2 cameraOnly = PerspectiveExpectation.MovedBox(now, then, probe.X, probe.Y, W, H, jitter, At(1), At(1), half);
        Assert.True(Vector2.Distance(Expected(probe.X, probe.Y), cameraOnly) > 5f, "the box must move apart from the camera");
        // A rigid translation moves the near corner and the far corner by different amounts on screen.
        Vector3 near = new(1f, 1f, -1f), far = new(-1f, -1f, 1f);
        Assert.True(Vector2.Distance(PerspectiveExpectation.Moved(now, then, At(1) + near, At(0) + near, W, H),
            PerspectiveExpectation.Moved(now, then, At(1) + far, At(0) + far, W, H)) > 2f, "the motion must vary with depth");
        MotionScan scan = Hold("keyed box", fx.Scene.ReadMotionTargetForTests(), Expected, probe);
        Assert.True(scan.Count > 2000, $"only {scan.Count} pixels drew");
    }

    [GpuFact]
    public void ADollyOverAWindFadedBladeReportsTheFadeAtEachFramesDepth()
    {
        FlyCamera3D camera = Fly();
        camera.Pitch = -.742f;
        using TemporalFixture fx = Stage(camera);
        MeshHandle plate = fx.Scene.LoadMesh(FoliageAndGroundMotionGpuTests.Plate());
        using FoliageBatch batch = fx.Scene.CreateFoliageBatch(new[] { new FoliageInstance(plate, Matrix4x4.Identity, .1f) });
        var settings = new FoliageRenderSettings
        {
            DrawRadius = 100f, DistantDensity = 1f, WindStrength = 1f, WindDirection = Vector2.UnitX, WindFadeBladePixels = 30f,
        };
        var start = new Vector3(0f, 3.2f, -2.4f);
        Vector3 dolly = Pinhole.Of(camera).Forward * .5f;
        void Draw(Scene3D s, int n)
        {
            // The wind clock stands still near a gust's peak, so the blade's own motion is the fade's alone.
            s.EffectTimeSeconds = 4.35f;
            camera.Position = start + dolly * n;
            Assert.Equal(1, s.DrawFoliage(batch, Vector3.Zero, settings, []));
        }

        fx.Frame(Draw);
        Pinhole then = Pinhole.Of(camera);
        FoliageUniforms last = fx.Scene.FoliageUniformsForTests[0];
        fx.Frame(Draw);
        Pinhole now = Pinhole.Of(camera);
        FoliageUniforms current = fx.Scene.FoliageUniformsForTests[0];
        Vector2 jitter = fx.Scene.CurrentFrameView.JitterPixels;
        Assert.NotNull(fx.Scene.PreviousFrameView);

        // Each upload's pixel scale is the pinhole's, and last frame's reaches this frame's previous evaluation.
        float scale = now.MetresPerPixel(H);
        Assert.True(MathF.Abs(current.WindFade.Y - scale) <= 1e-6f * scale, $"{current.WindFade.Y} m per pixel");
        Assert.Equal(last.WindFade.Y, current.WindFade.Z);
        // The dolly takes the root from 3.93 m to 3.43 m deep: a 1 m blade 1.32 fade heights tall, then 1.51, both on
        // the fade's slope. Last frame's evaluation that read this frame's clip w would fade it as this frame does.
        Vector3 root = Vector3.Zero;
        float heightsThen = 1f / (then.Depth(root) * scale * settings.WindFadeBladePixels);
        float heightsNow = 1f / (now.Depth(root) * scale * settings.WindFadeBladePixels);
        Assert.InRange(heightsThen, 1.25f, 1.4f);
        Assert.InRange(heightsNow, 1.45f, 1.6f);
        Vector3 offsetThen = FoliageWindMirror.TopOffset(last, root, then.Depth(root));
        Vector3 offsetNow = FoliageWindMirror.TopOffset(current, root, now.Depth(root));
        var top = new Vector3(0f, 1f, 0f);
        Vector2 own = PerspectiveExpectation.Moved(then, then, top + offsetNow, top + offsetThen, W, H);
        Assert.True(own.Length() > 5f, $"the fade moves the plate {own} px");
        output.WriteLine($"blade {heightsThen:F3} then {heightsNow:F3} fade heights tall, own motion {own} px");

        // The plate is a level square at the blade's top, carried rigidly by the offset.
        Vector2 Expected(int x, int y) => PerspectiveExpectation.Surface(now, then, x, y, W, H, jitter,
            (eye, ray) => PerspectiveExpectation.PlaneHit(eye, ray, Vector3.UnitY, 1f + offsetNow.Y),
            p => p - offsetNow + offsetThen);
        Vector2 centre = now.Project(top + offsetNow, W, H);
        MotionScan scan = Hold("foliage plate", fx.Scene.ReadMotionTargetForTests(), Expected, ((int)centre.X, (int)centre.Y));
        Assert.True(scan.Count > 500, $"only {scan.Count} pixels drew");
    }

    [GpuFact]
    public void ABoxThatWasBehindTheCameraReadsTheGuardedOffScreenMotion()
    {
        FlyCamera3D camera = Fly();
        using TemporalFixture fx = Stage(camera);
        MeshHandle box = fx.Scene.LoadMesh(MeshPrimitives.Box(2f));
        // Last frame every point was 2 to 4 m behind the eye, clip w from -4 to -2. Now the box is 4 to 6 m in front.
        static Vector3 At(int n) => new(.5f, .2f, n == 0 ? -3f : 5f);
        void Draw(Scene3D s, int n) => s.Draw(new RigidInstanceDraw(box, Matrix4x4.CreateTranslation(At(n))) { Motion = Key });

        fx.Frame(Draw);
        Pinhole then = Pinhole.Of(camera);
        fx.Frame(Draw);
        Pinhole now = Pinhole.Of(camera);
        Vector2 jitter = fx.Scene.CurrentFrameView.JitterPixels;
        Assert.True(then.Depth(At(0) + Vector3.UnitZ) <= -2f);

        Vector2 Expected(int x, int y) => PerspectiveExpectation.MovedBox(now, then, x, y, W, H, jitter, At(1), At(0), Vector3.One);
        AssertFace(fx.Scene.ReadMotionTargetForTests(), now, Expected, At(1) - Vector3.UnitZ, 1f,
            PerspectiveExpectation.OffScreen(W, H), "box from behind");
    }

    [GpuFact]
    public void AQuadThatLayInTheCamerasPlaneLastFrameReadsTheGuardedMotionNotAnInfinity()
    {
        FlyCamera3D camera = Fly();
        using TemporalFixture fx = Stage(camera);
        MeshHandle quad = fx.Scene.LoadMesh(Upright());
        // Last frame the quad lay in the plane through the eye, clip w exactly zero, where an unguarded divide gives an
        // infinity, or NaN where x and y are zero too. Now it faces the camera 3 m ahead.
        static Vector3 At(int n) => new(-.4f, .3f, 3f * n);
        void Draw(Scene3D s, int n) => s.Draw(new RigidInstanceDraw(quad, Matrix4x4.CreateTranslation(At(n))) { Motion = Key });

        fx.Frame(Draw);
        Pinhole then = Pinhole.Of(camera);
        fx.Frame(Draw);
        Pinhole now = Pinhole.Of(camera);
        Vector2 jitter = fx.Scene.CurrentFrameView.JitterPixels;
        Assert.Equal(0f, then.Depth(At(0) + new Vector3(1f, 1f, 0f)));

        Vector2 Expected(int x, int y) => PerspectiveExpectation.Surface(now, then, x, y, W, H, jitter,
            (eye, ray) => PerspectiveExpectation.PlaneHit(eye, ray, Vector3.UnitZ, At(1).Z), p => p - At(1) + At(0));
        AssertFace(fx.Scene.ReadMotionTargetForTests(), now, Expected, At(1), 1f,
            PerspectiveExpectation.OffScreen(W, H), "quad from the eye plane");
    }

    [GpuFact]
    public void AQuadJustInFrontOfTheEyeLastFrameReadsTheClampedMotionNotTheSentinel()
    {
        FlyCamera3D camera = Fly();
        using TemporalFixture fx = Stage(camera);
        MeshHandle quad = fx.Scene.LoadMesh(Upright());
        // Last frame the 1.2 m quad lay 10 micrometres in front of the eye, clip w 1e-5, just past the guard. Its clip x
        // of 0.88 to 2.05 and y of 0.52 to 2.6 divide to UV motion of 2.6e4 to 1.3e5, which unclamped reads as sky past
        // the background threshold or as an infinity past half precision. Now it faces the camera 3 m ahead, up and to the
        // right, and every pixel reads the clamp, two screens on each axis: UV (-2, 2).
        static Vector3 At(int n) => new(-1.5f, .9f, n == 0 ? 1e-5f : 3f);
        void Draw(Scene3D s, int n) =>
            s.Draw(new RigidInstanceDraw(quad, Matrix4x4.CreateScale(.6f) * Matrix4x4.CreateTranslation(At(n))) { Motion = Key });

        fx.Frame(Draw);
        Pinhole then = Pinhole.Of(camera);
        fx.Frame(Draw);
        Pinhole now = Pinhole.Of(camera);
        Vector2 jitter = fx.Scene.CurrentFrameView.JitterPixels;
        Assert.InRange(then.Depth(At(0)), 5e-6f, 2e-5f);

        Vector2 Expected(int x, int y) => PerspectiveExpectation.Surface(now, then, x, y, W, H, jitter,
            (eye, ray) => PerspectiveExpectation.PlaneHit(eye, ray, Vector3.UnitZ, At(1).Z), p => p - At(1) + At(0));
        AssertFace(fx.Scene.ReadMotionTargetForTests(), now, Expected, At(1), .6f, new Vector2(-2f * W, 2f * H),
            "quad just in front of the eye");
    }

    // Every pixel inside the square face of half-side half centred on faceCentre, facing the camera, two pixels in from
    // its edges, drew, and every drawn pixel reads the expectation, which at the face's centre is want.
    void AssertFace(MotionTargetReadback motion, Pinhole now, Func<int, int, Vector2> expected, Vector3 faceCentre, float half,
        Vector2 want, string name)
    {
        var corner = new Vector3(half, half, 0f);
        Vector2 a = now.Project(faceCentre - corner, W, H), b = now.Project(faceCentre + corner, W, H);
        Vector2 min = Vector2.Min(a, b) + new Vector2(2f), max = Vector2.Max(a, b) - new Vector2(2f);
        int inside = MotionExpectation.AssertCovered(motion, (x, y) => x >= min.X && x <= max.X && y >= min.Y && y <= max.Y);
        Assert.True(inside > 2000, $"only {inside} pixels inside the face");
        Vector2 centre = (a + b) / 2f;
        Assert.Equal(want, expected((int)centre.X, (int)centre.Y));
        MotionScan scan = Hold(name, motion, expected, ((int)centre.X, (int)centre.Y));
        Assert.True(scan.Count >= inside, $"{scan.Count} drawn pixels, {inside} inside the face");
    }

    // A 2 m square in the XY plane at z = 0, facing -Z.
    static GltfMesh Upright()
    {
        ModelVertex Corner(float x, float y) => new(new Vector3(x, y, 0f), -Vector3.UnitZ, Vector4.One);
        return new GltfMesh([Corner(-1f, -1f), Corner(1f, -1f), Corner(1f, 1f), Corner(-1f, 1f)], new ushort[] { 0, 1, 2, 0, 2, 3 });
    }
}
