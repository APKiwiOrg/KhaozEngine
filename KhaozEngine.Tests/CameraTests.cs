using KhaozEngine.Graphics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Xunit;

namespace KhaozEngine.Tests;

public class CameraTests
{
    private const int W = 800;
    private const int H = 600;
    private static Viewport Vp => new Viewport(0, 0, W, H);
    private const float Tol = 1e-3f;

    private static void AssertClose(Vector2 expected, Vector2 actual, float tol = Tol)
    {
        Assert.True(Vector2.Distance(expected, actual) <= tol,
            $"expected {expected}, got {actual}");
    }

    [Fact]
    public void WorldOrigin_AtDefaults_MapsToViewportCenter()
    {
        var camera = new Camera2D();
        AssertClose(new Vector2(W / 2f, H / 2f), camera.WorldToScreen(Vector2.Zero, Vp));
    }

    [Theory]
    [InlineData(123f, -45f, 1f, 0f)]
    [InlineData(-10f, 200f, 2.5f, 0.7f)]
    [InlineData(500f, 500f, 0.4f, -1.2f)]
    public void Position_AlwaysMapsToViewportCenter(float px, float py, float zoom, float rot)
    {
        var camera = new Camera2D
        {
            Position = new Vector2(px, py),
            Zoom = zoom,
            Rotation = rot,
        };
        AssertClose(new Vector2(W / 2f, H / 2f), camera.WorldToScreen(camera.Position, Vp));
    }

    [Theory]
    [InlineData(0f, 0f, 1f, 0f)]
    [InlineData(123f, -45f, 2.5f, 0.7f)]
    [InlineData(-10f, 200f, 0.4f, -1.2f)]
    public void ScreenToWorld_IsInverseOfWorldToScreen(float px, float py, float zoom, float rot)
    {
        var camera = new Camera2D
        {
            Position = new Vector2(px, py),
            Zoom = zoom,
            Rotation = rot,
        };
        foreach (var p in new[]
        {
            new Vector2(0f, 0f), new Vector2(50f, 120f),
            new Vector2(-300f, 80f), new Vector2(640f, 400f),
        })
        {
            var screen = camera.WorldToScreen(p, Vp);
            AssertClose(p, camera.ScreenToWorld(screen, Vp));
        }
    }

    [Fact]
    public void Zoom_ScalesWorldOffsetFromCenter()
    {
        var camera = new Camera2D { Position = Vector2.Zero, Zoom = 2f };
        // World (10,0) is 10 units right of Position; at zoom 2 that is 20 px right of center.
        AssertClose(new Vector2(W / 2f + 20f, H / 2f), camera.WorldToScreen(new Vector2(10f, 0f), Vp));
    }

    [Fact]
    public void Rotation_QuarterTurn_MapsWorldXOffsetToScreenYOffset()
    {
        var camera = new Camera2D { Position = Vector2.Zero, Zoom = 1f, Rotation = MathHelper.PiOver2 };
        // A +X world offset, rotated +90deg CCW under MonoGame's screen-space transform,
        // lands on the +Y screen axis (below center). Pins rotation direction + matrix fold.
        var screen = camera.WorldToScreen(new Vector2(10f, 0f), Vp);
        AssertClose(new Vector2(W / 2f, H / 2f + 10f), screen);
    }
}
