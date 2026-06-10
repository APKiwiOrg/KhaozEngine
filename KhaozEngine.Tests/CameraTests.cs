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
}
