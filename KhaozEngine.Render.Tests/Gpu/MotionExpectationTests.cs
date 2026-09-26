using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>The analytic expectation the motion readbacks are held to, checked against motions worked by hand.</summary>
public sealed class MotionExpectationTests
{
    const int W = 320, H = 180;

    static IsoCamera3D Camera(Vector3 target, float zoom) => new()
    {
        Target = target, OrthoSize = 12f, Zoom = zoom, AspectRatio = W / (float)H,
    };

    [Fact]
    public void AStillCameraExpectsNoMotionAnywhere()
    {
        IsoCamera3D camera = Camera(Vector3.Zero, 1f);
        foreach ((int x, int y) in new[] { (0, 0), (160, 90), (319, 179) })
            Assert.True(MotionExpectation.StaticSurface(camera, camera, x, y, W, H, new Vector2(.3f, -.2f)).Length() < 1e-3f);
    }

    [Fact]
    public void ACameraSteppingOneMetreRightMovesTheSceneOneMetreOfPixelsLeft()
    {
        IsoCamera3D then = Camera(Vector3.Zero, 1f);
        Vector3 right = Vector3.Normalize(Vector3.Cross(then.Forward, Vector3.UnitY));
        IsoCamera3D now = Camera(right, 1f);
        const float PixelsPerMetre = H / 12f;

        Vector2 motion = MotionExpectation.StaticSurface(now, then, 40, 150, W, H, Vector2.Zero);

        Assert.Equal(-PixelsPerMetre, motion.X, 3);
        Assert.Equal(0f, motion.Y, 3);
    }

    [Fact]
    public void ACameraSteppingOneMetreAwayOverTheGroundMovesTheSceneDownTheImage()
    {
        IsoCamera3D then = Camera(Vector3.Zero, 1f);
        Vector3 away = Vector3.Normalize(new Vector3(then.Forward.X, 0f, then.Forward.Z));
        IsoCamera3D now = Camera(away, 1f);
        const float PixelsPerMetre = H / 12f;

        // The step raises the view by sin(elevation) metres, so the ground slides down the image: positive y.
        Vector2 motion = MotionExpectation.StaticSurface(now, then, 250, 30, W, H, new Vector2(-.2f, .4f));

        Assert.Equal(0f, motion.X, 3);
        Assert.Equal(PixelsPerMetre * MathF.Sin(then.Elevation), motion.Y, 3);
    }

    [Fact]
    public void AZoomMovesEachPixelAwayFromTheCentreInProportion()
    {
        IsoCamera3D then = Camera(Vector3.Zero, 1f);
        IsoCamera3D now = Camera(Vector3.Zero, 1.25f);

        // A pixel dx from the centre now was dx / 1.25 from it last frame.
        Vector2 motion = MotionExpectation.StaticSurface(now, then, 260, 90, W, H, new Vector2(.5f, .5f));

        float dx = 260f - 160f;
        Assert.Equal(dx - dx / 1.25f, motion.X, 3);
        Assert.Equal(0f, motion.Y, 3);
    }

    [Fact]
    public void ANonFiniteMotionFailsTheReadbackWhereverItFallsInTheScan()
    {
        // One row in scan order: 5 px off, then NaN, then 0.001 px off. NaN compares false both ways, so a worst kept
        // by comparison alone takes the NaN, loses it to the next pixel and passes with 0.001 px.
        var motion = new MotionTargetReadback(
            [new Vector2(5f / 3f, 0f), new Vector2(float.NaN, float.NaN), new Vector2(.001f / 3f, 0f)], 3, 1);

        var failure = Assert.ThrowsAny<Xunit.Sdk.XunitException>(
            () => MotionExpectation.AssertDrawnPixels(motion, (_, _) => Vector2.Zero, .05f));
        Assert.Contains("pixel (1, 0)", failure.Message);
    }
}
