using System;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>Headless coverage for room/region cameras (Camera2D zoom-clamp overload, CameraRoom, RoomCamera).</summary>
public class Render2DRoomCameraTests
{
    private const int Vw = 800, Vh = 600;
    private const float Tol = 1e-2f;

    private static void AssertClose(Vector2 expected, Vector2 actual, float tol = Tol) =>
        Assert.True(Vector2.Distance(expected, actual) <= tol, $"expected {expected}, got {actual}");

    // ---- Camera2D explicit-zoom ClampPosition overload ----

    [Fact]
    public void Clamp_ExplicitZoomMatchesInstanceZoom()
    {
        var cam = new Camera2D { Zoom = 2f };
        var bounds = new Rect(0f, 0f, 2000f, 1000f);
        var desired = new Vector2(50f, 500f);

        var viaField = cam.ClampPosition(desired, bounds, Vw, Vh);
        var viaArg = cam.ClampPosition(desired, bounds, Vw, Vh, 2f);

        AssertClose(viaField, viaArg);
    }

    [Fact]
    public void Clamp_HigherZoomAllowsPositionNearerEdge()
    {
        var cam = new Camera2D();
        var bounds = new Rect(0f, 0f, 2000f, 1000f);
        var desired = new Vector2(50f, 500f);   // near the left edge

        var atZoom1 = cam.ClampPosition(desired, bounds, Vw, Vh, 1f);   // halfW 400 -> x clamps to 400
        var atZoom2 = cam.ClampPosition(desired, bounds, Vw, Vh, 2f);   // halfW 200 -> x clamps to 200

        Assert.Equal(400f, atZoom1.X, Tol);
        Assert.Equal(200f, atZoom2.X, Tol);
        Assert.True(atZoom2.X < atZoom1.X, "higher zoom should sit nearer the bound edge");
    }
}
