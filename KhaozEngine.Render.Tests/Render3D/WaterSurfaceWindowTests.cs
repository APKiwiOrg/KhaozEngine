using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class WaterSurfaceWindowTests
{
    [Fact]
    public void Recipe_iso_camera_centres_on_its_view_target()
    {
        var camera = new IsoCamera3D
        {
            Distance = 150f,
            Elevation = .65f,
            Target = new Vector3(17f, 2f, -9f),
        };
        var window = WaterSurfaceWindow.Place(camera.Eye, camera.Forward, camera.ViewProjection,
            true, camera.Target.Y, Vector3.Zero);

        Assert.InRange(camera.Target.X - Centre(window).X, -4f, 4f);
        Assert.InRange(camera.Target.Z - Centre(window).Y, -4f, 4f);
        Assert.False(Contains(window, new Vector2(camera.Eye.X, camera.Eye.Z)));
    }

    [Theory]
    [InlineData(.04f, 27.8f, 28f)]
    [InlineData(.5f, 2.67f, 2.69f)]
    public void Low_perspective_camera_keeps_the_near_field_inside(float pitch, float minReach, float maxReach)
    {
        var eye = new Vector3(0f, 2f, 0f);
        var forward = Vector3.Normalize(new Vector3(0f, -pitch, -1f));
        var window = WaterSurfaceWindow.Place(eye, forward, ViewProjection(eye, forward), false, 0f, Vector3.Zero);
        // A 60 degree vertical field puts the lower ray 30 degrees below the forward ray.
        var nearHit = new Vector2(0f, -eye.Y / MathF.Tan(MathF.Atan(pitch) + MathF.PI / 6f));

        AssertNearFieldInside(window, nearHit);
        Assert.InRange(nearHit.Y - Centre(window).Y, minReach, maxReach);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(.2f)]
    [InlineData(.8f)]
    public void Level_or_upward_camera_falls_back_without_a_hit(float rise)
    {
        var eye = new Vector3(0f, 2f, 0f);
        var forward = Vector3.Normalize(new Vector3(0f, rise, -1f));
        var window = WaterSurfaceWindow.Place(eye, forward, ViewProjection(eye, forward), false, 0f, Vector3.Zero);
        float bottomPitch = MathF.PI / 6f - MathF.Atan(rise);
        var nearHit = bottomPitch > 0f ? new Vector2(0f, -eye.Y / MathF.Tan(bottomPitch)) : Vector2.Zero;

        AssertNearFieldInside(window, nearHit);
        Assert.InRange(nearHit.Y - Centre(window).Y, 27.8f, 28f);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(100000f, -50000f)]
    [InlineData(17.03f, -19.07f)]
    public void Window_snaps_to_whole_cells_in_absolute_world(float originX, float originZ)
    {
        var origin = new Vector3(originX, 0f, originZ);
        var eye = new Vector3(12.02f, 10f, -6.98f);
        var window = WaterSurfaceWindow.Place(eye, -Vector3.UnitY, Matrix4x4.Identity, true, 0f, origin);
        var shifted = WaterSurfaceWindow.Place(eye + new Vector3(.01f, 0f, .01f), -Vector3.UnitY,
            Matrix4x4.Identity, true, 0f, origin);

        double absoluteX = (double)window.MinX + origin.X;
        double absoluteZ = (double)window.MinZ + origin.Z;
        Assert.InRange(Math.Abs(absoluteX / .125d - Math.Round(absoluteX / .125d)), 0d, .001d);
        Assert.InRange(Math.Abs(absoluteZ / .125d - Math.Round(absoluteZ / .125d)), 0d, .001d);
        Assert.Equal(window, shifted);
    }

    static Matrix4x4 ViewProjection(Vector3 eye, Vector3 forward) =>
        Matrix4x4.CreateLookAt(eye, eye + forward, Vector3.UnitY)
        * Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 16f / 9f, .1f, 1000f);

    static Vector2 Centre(WaterSurfaceWindow window) => new(window.MinX + 32f, window.MinZ + 32f);

    static bool Contains(WaterSurfaceWindow window, Vector2 point) =>
        point.X >= window.MinX && point.X <= window.MinX + 64f
        && point.Y >= window.MinZ && point.Y <= window.MinZ + 64f;

    static void AssertNearFieldInside(WaterSurfaceWindow window, Vector2 point)
    {
        Assert.InRange(point.X - window.MinX, 4f, 60f);
        Assert.InRange(point.Y - window.MinZ, 4f, 60f);
    }
}
