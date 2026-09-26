using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// Analytic screen motion for the readback tests, from detached copies of the camera and nothing of the motion
/// pipeline. Pixels are internal pixels, x right and y down, and motion is this frame's position minus last frame's.
/// </summary>
internal static class MotionExpectation
{
    /// <summary>A detached copy of <paramref name="camera"/>'s state, taken after a frame renders (the render sets the
    /// aspect). The copy works in absolute world space.</summary>
    public static IsoCamera3D Snapshot(IsoCamera3D camera) => new()
    {
        Azimuth = camera.Azimuth,
        Elevation = camera.Elevation,
        Target = camera.Target,
        OrthoSize = camera.OrthoSize,
        Zoom = camera.Zoom,
        AspectRatio = camera.AspectRatio,
        Distance = camera.Distance,
        NearPlane = camera.NearPlane,
        FarPlane = camera.FarPlane,
    };

    /// <summary>The motion of a static surface under pixel (<paramref name="x"/>, <paramref name="y"/>) of a jittered
    /// image. The rasteriser sampled that pixel's centre, which is <paramref name="jitter"/> away from the unjittered
    /// point the motion is measured at. Valid while both cameras look the same way, which a translation and a zoom
    /// keep: every point on the pixel's ray then lands on one pixel last frame.</summary>
    public static Vector2 StaticSurface(IsoCamera3D now, IsoCamera3D then, int x, int y, int width, int height, Vector2 jitter)
    {
        Assert.Equal((now.Azimuth, now.Elevation), (then.Azimuth, then.Elevation));
        Vector2 at = new Vector2(x + .5f, y + .5f) - jitter;
        Vector3 world = now.ScreenToGround(at, width, height);
        Assert.True(then.WorldToScreen(world, width, height, out Vector2 before));
        return at - before;
    }

    /// <summary>The motion of a point that moved from <paramref name="pointThen"/> to <paramref name="pointNow"/>. Under
    /// an orthographic camera a translated body's motion is this at every one of its pixels.</summary>
    public static Vector2 Moved(IsoCamera3D now, IsoCamera3D then, Vector3 pointNow, Vector3 pointThen, int width, int height)
    {
        Assert.True(now.WorldToScreen(pointNow, width, height, out Vector2 here));
        Assert.True(then.WorldToScreen(pointThen, width, height, out Vector2 there));
        return here - there;
    }

    /// <summary>Hold every pixel opaque geometry drew (every pixel that is not the sentinel), and that
    /// <paramref name="where"/> admits, to within <paramref name="tolerance"/> internal pixels of
    /// <paramref name="expected"/>. Returns how many pixels were checked. The message names the worst one.</summary>
    public static int AssertDrawnPixels(MotionTargetReadback motion, Func<int, int, Vector2> expected, float tolerance,
        Func<int, int, bool>? where = null)
    {
        int count = 0;
        float worst = -1f;
        (int X, int Y) at = default;
        Vector2 got = default, want = default;
        for (int y = 0; y < motion.Height; y++)
            for (int x = 0; x < motion.Width; x++)
            {
                if (motion.IsBackground(x, y) || (where is not null && !where(x, y))) continue;
                count++;
                Vector2 reported = motion.PixelsAt(x, y), analytic = expected(x, y);
                float error = Vector2.Distance(reported, analytic);
                if (error <= worst) continue;
                (worst, at, got, want) = (error, (x, y), reported, analytic);
            }
        Assert.True(count > 0, "no pixel drew opaque geometry");
        Assert.True(worst <= tolerance,
            $"pixel {at} reported {got} px where {want} px was expected, {worst:F4} px off, over {count} drawn pixels");
        return count;
    }
}
