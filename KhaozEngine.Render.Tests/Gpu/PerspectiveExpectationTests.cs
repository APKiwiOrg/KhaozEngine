using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;
using FoliageUniforms = KhaozEngine.Render3D.Rendering.ModelRenderer.FoliageUniforms;

namespace KhaozEngine.Tests.Gpu;

/// <summary>The perspective expectation the motion readbacks are held to, checked against the camera it describes and
/// against motions worked by hand, and the readback helpers the perspective readbacks add.</summary>
public sealed class PerspectiveExpectationTests
{
    const int W = 320, H = 180;

    // A 90 degree field of view: at depth d a pixel offset of 90 px from the centre, on either axis, is d metres.
    static Pinhole Square(Vector3 eye, float yaw = 0f) =>
        Pinhole.Of(new FlyCamera3D { Position = eye, Yaw = yaw, FieldOfView = MathF.PI / 2f, AspectRatio = (float)W / H });

    [Fact]
    public void ThePinholeProjectsAndCastsRaysAsTheFlyCameraDoes()
    {
        var poses = new[]
        {
            new FlyCamera3D { Position = new Vector3(0f, 3f, -10f), Yaw = .05f, Pitch = -.6f, AspectRatio = (float)W / H },
            new FlyCamera3D { Position = new Vector3(4f, 1.5f, 2f), Yaw = -2.1f, Pitch = .3f, FieldOfView = 1.1f, AspectRatio = 1.4f },
        };
        foreach (FlyCamera3D camera in poses)
        {
            Pinhole pinhole = Pinhole.Of(camera);
            foreach (Vector2 pixel in new[] { new Vector2(0f, 0f), new Vector2(37.25f, 150.5f), new Vector2(319f, 179f) })
            {
                Vector3 point = camera.Position + pinhole.Ray(pixel, W, H) * 7f;
                Assert.True(camera.WorldToScreen(point, W, H, out Vector2 engine));
                Assert.True(Vector2.Distance(engine, pinhole.Project(point, W, H)) < 1e-3f, $"{engine} against {pixel}");
                Assert.True(Vector2.Distance(pixel, pinhole.Project(point, W, H)) < 1e-3f);
                Vector3 ray = Vector3.Normalize(camera.ScreenToRay(pixel, W, H).Direction);
                Assert.True(Vector3.Dot(ray, Vector3.Normalize(pinhole.Ray(pixel, W, H))) > 1f - 1e-6f);
                Assert.Equal(7f, pinhole.Depth(point), 3);
            }
        }
    }

    [Fact]
    public void ADollyTowardAWallHalvesEachPixelsOffsetFromTheCentre()
    {
        // Two metres from a wall now and four last frame, so a point 40 px right of and 60 px above the centre was 20 px
        // right and 30 px above it: motion (20, -30).
        Pinhole then = Square(Vector3.Zero), now = Square(new Vector3(0f, 0f, 2f));

        Vector2 motion = PerspectiveExpectation.StaticPlane(now, then, 200, 30, W, H, new Vector2(.5f, .5f), Vector3.UnitZ, 4f);

        Assert.Equal(20f, motion.X, 3);
        Assert.Equal(-30f, motion.Y, 3);
    }

    [Fact]
    public void AYawMovesTheImageByTheTangentOfTheTurnAtAnyDepth()
    {
        // A turn of atan(0.1) toward +X, which is screen left, carries the image 0.1 of 90 px right, whatever the depth.
        Pinhole then = Square(Vector3.Zero), now = Square(Vector3.Zero, MathF.Atan(.1f));
        var jitter = new Vector2(-.5f, -.5f);   // puts pixel (159, 89)'s sample exactly on (160, 90)

        foreach (float wall in new[] { 3f, 40f })
        {
            Vector2 motion = PerspectiveExpectation.StaticPlane(now, then, 159, 89, W, H, jitter, Vector3.UnitZ, wall);
            Assert.Equal(9f, motion.X, 3);
            Assert.Equal(0f, motion.Y, 3);
        }
    }

    [Fact]
    public void AStepForwardCarriesAPointAboveTheCentreUpTheImage()
    {
        // One metre up and two ahead is 45 px above the centre. One metre ahead it is 90 px above, the top edge.
        Pinhole then = Square(Vector3.Zero), now = Square(Vector3.UnitZ);
        var point = new Vector3(0f, 1f, 2f);

        Assert.Equal(45f, then.Project(point, W, H).Y, 3);
        Vector2 motion = PerspectiveExpectation.Moved(now, then, point, point, W, H);

        Assert.Equal(0f, motion.X, 3);
        Assert.Equal(-45f, motion.Y, 3);
    }

    [Fact]
    public void ABoxRayEntersTheNearFaceAndAHairlineMissStillFindsTheSilhouette()
    {
        Vector3 min = new(-1f, -1f, 4f), max = new(1f, 1f, 6f);

        Assert.Equal(4f, PerspectiveExpectation.BoxHit(Vector3.Zero, Vector3.UnitZ, min, max), 5);
        Assert.True(float.IsNaN(PerspectiveExpectation.BoxHit(Vector3.Zero, new Vector3(.5f, 0f, 1f), min, max)));
        Assert.Equal(4f, PerspectiveExpectation.BoxHit(new Vector3(1.0005f, 0f, 0f), Vector3.UnitZ, min, max), 2);
        Assert.True(float.IsNaN(PerspectiveExpectation.BoxHit(Vector3.Zero, -Vector3.UnitZ, min, max)));
    }

    [Fact]
    public void ABoxSteppingSidewaysUnderAStillCameraMovesItsNearFaceByItsDepth()
    {
        // Half a metre toward +X, screen left, at the near face four metres deep: 0.5 / 4 of 90 px.
        Pinhole camera = Square(Vector3.Zero);
        Vector3 half = Vector3.One, then = new(0f, 0f, 5f), now = then + new Vector3(.5f, 0f, 0f);

        Vector2 motion = PerspectiveExpectation.MovedBox(camera, camera, 149, 89, W, H, new Vector2(-.5f, -.5f), now, then, half);

        Assert.Equal(-11.25f, motion.X, 3);
        Assert.Equal(0f, motion.Y, 3);
    }

    [Fact]
    public void APointOnOrBehindLastFramesEyePlaneExpectsTheGuardedMotion()
    {
        Pinhole camera = Square(Vector3.Zero);
        var half = Vector3.One;
        foreach (float thenZ in new[] { -3f, 1f })   // the near face 4 m behind the eye, then on its plane
            Assert.Equal(new Vector2(640f, 360f), PerspectiveExpectation.MovedBox(camera, camera, 160, 90, W, H, Vector2.Zero,
                new Vector3(0f, 0f, 5f), new Vector3(0f, 0f, thenZ), half));
    }

    [Fact]
    public void APointJustInFrontOfLastFramesEyeExpectsTheClampedMotion()
    {
        // Last frame the wall point sat 10 micrometres in front of the eye, past the guard. Up and right of the centre, it
        // projected a vast distance up and right, so the motion is clamped to two screens left and down: (-640, 360) px.
        Pinhole camera = Square(Vector3.Zero);
        var shift = new Vector3(0f, 0f, 3f - 1e-5f);

        Vector2 motion = PerspectiveExpectation.Surface(camera, camera, 200, 30, W, H, Vector2.Zero,
            (eye, ray) => PerspectiveExpectation.PlaneHit(eye, ray, Vector3.UnitZ, 3f), p => p - shift);

        Assert.Equal(new Vector2(-640f, 360f), motion);
        // A motion inside two screens is not clamped, even one that already leaves the screen: a turn of 1.35 rad
        // carries the centre tan(1.35) of 90 px, about 401 px, right.
        Vector2 wide = PerspectiveExpectation.StaticPlane(Square(Vector3.Zero, 1.35f), camera, 159, 89,
            W, H, new Vector2(-.5f, -.5f), Vector3.UnitZ, 5f);
        Assert.Equal(90f * MathF.Tan(1.35f), wide.X, .05f);
        Assert.InRange(wide.X, 320f, 639f);
    }

    [Fact]
    public void AHoldReportsTheWorstPixelAndHowManyDrew()
    {
        var sentinel = new Vector2(MotionMath.Sentinel);
        var motion = new MotionTargetReadback([new Vector2(.01f / 3f, 0f), sentinel, new Vector2(0f, .03f)], 3, 1);

        MotionScan scan = MotionExpectation.HoldDrawnPixels(motion, (_, _) => Vector2.Zero, .05f);

        Assert.Equal(2, scan.Count);
        Assert.Equal((2, 0), scan.At);
        Assert.Equal(.03f, scan.Worst, 5);
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => MotionExpectation.HoldDrawnPixels(motion, (_, _) => Vector2.Zero, .02f));
    }

    [Fact]
    public void ACoveredPixelThatReadsAsBackgroundOrInfinityFails()
    {
        var sentinel = new Vector2(MotionMath.Sentinel);
        var drawn = new Vector2(2f, 2f);
        var motion = new MotionTargetReadback([drawn, sentinel, new Vector2(float.PositiveInfinity, 0f), drawn], 4, 1);

        Assert.Equal(1, MotionExpectation.AssertCovered(motion, (x, _) => x == 0));
        Assert.Equal(1, MotionExpectation.AssertCovered(motion, (x, _) => x == 3));
        var background = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => MotionExpectation.AssertCovered(motion, (x, _) => x < 2));
        Assert.Contains("pixel (1, 0)", background.Message);
        var infinity = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => MotionExpectation.AssertCovered(motion, (x, _) => x >= 2));
        Assert.Contains("pixel (2, 0)", infinity.Message);
    }

    [Fact]
    public void TheWindMirrorFadesABladeOneAndAHalfFadeHeightsTallByHalf()
    {
        // 30 px at 1/180 m per pixel and a depth of 4 m is a fade height of 2/3 m, so the 1 m blade is 1.5 of them.
        var settings = new FoliageRenderSettings
        {
            DrawRadius = 100f, DistantDensity = 1f, WindStrength = .2f, WindDirection = Vector2.UnitX, WindFadeBladePixels = 30f,
        };
        FoliageUniforms faded = FoliageUniforms.Build(Vector3.Zero, settings, [], 0f);
        faded.WindFade.Y = 1f / 180f;
        FoliageUniforms full = faded;
        full.WindFade.X = 0f;

        Vector3 fullOffset = FoliageWindMirror.TopOffset(full, Vector3.Zero, 0f);
        Vector3 fadedOffset = FoliageWindMirror.TopOffset(faded, Vector3.Zero, 4f);

        Assert.True(fullOffset.X > .05f, $"the unfaded bend is {fullOffset.X} m");
        Assert.Equal(fullOffset.X / 2f, fadedOffset.X, 6);
        Matrix4x4 fourMetresBack = Matrix4x4.CreateLookAt(new Vector3(0f, 0f, -4f), Vector3.Zero, Vector3.UnitY)
            * Matrix4x4.CreatePerspectiveFieldOfView(1f, 1.5f, .1f, 100f);
        Assert.True(Vector3.Distance(fadedOffset, FoliageWindMirror.TopOffset(faded, Vector3.Zero, fourMetresBack)) < 1e-6f);
    }
}
