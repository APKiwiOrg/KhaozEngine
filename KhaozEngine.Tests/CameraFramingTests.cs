using KhaozEngine.Graphics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>Headless coverage for <see cref="Camera2D"/>'s framing helpers (CenterOn, Focus fit-zoom).</summary>
public class CameraFramingTests
{
    private const int W = 800;
    private const int H = 600;
    private static Viewport Vp => new Viewport(0, 0, W, H);   // center (400, 300)
    private const float Tol = 1e-2f;

    private static void AssertClose(Vector2 expected, Vector2 actual, float tol = Tol) =>
        Assert.True(Vector2.Distance(expected, actual) <= tol, $"expected {expected}, got {actual}");

    [Fact]
    public void CenterOn_PutsWorldPointAtViewportCenter()
    {
        var camera = new Camera2D { Zoom = 2f };
        camera.CenterOn(new Vector2(123, -45));
        Assert.Equal(new Vector2(123, -45), camera.Position);
        AssertClose(new Vector2(W / 2f, H / 2f), camera.WorldToScreen(new Vector2(123, -45), Vp));
    }

    [Fact]
    public void Focus_WideRect_FitsWidthAndCentersHeight()
    {
        var camera = new Camera2D();
        camera.Focus(new Rectangle(0, 0, 1600, 600), Vp);

        Assert.Equal(0.5f, camera.Zoom, Tol);                 // min(800/1600, 600/600) = 0.5
        AssertClose(new Vector2(800, 300), camera.Position);  // rect center

        // The padded-zero rect is fully contained: corners land on the fitted edges.
        AssertClose(new Vector2(0, 150), camera.WorldToScreen(new Vector2(0, 0), Vp));
        AssertClose(new Vector2(800, 450), camera.WorldToScreen(new Vector2(1600, 600), Vp));
    }

    [Fact]
    public void Focus_TallRect_FitsHeight()
    {
        var camera = new Camera2D();
        camera.Focus(new Rectangle(0, 0, 800, 1200), Vp);

        Assert.Equal(0.5f, camera.Zoom, Tol);                 // min(800/800, 600/1200) = 0.5
        AssertClose(new Vector2(400, 600), camera.Position);
    }

    [Fact]
    public void Focus_SmallRect_ClampsToMaxZoom()
    {
        var camera = new Camera2D();
        camera.Focus(new Rectangle(0, 0, 80, 60), Vp, paddingFraction: 0f, minZoom: 0.1f, maxZoom: 4f);
        Assert.Equal(4f, camera.Zoom, Tol);                   // raw fit 10 -> clamped to 4
    }

    [Fact]
    public void Focus_HugeRect_ClampsToMinZoom()
    {
        var camera = new Camera2D();
        camera.Focus(new Rectangle(0, 0, 80000, 60000), Vp, paddingFraction: 0f, minZoom: 0.5f, maxZoom: 10f);
        Assert.Equal(0.5f, camera.Zoom, Tol);                 // raw fit 0.01 -> clamped to 0.5
    }

    [Fact]
    public void Focus_PaddingFraction_LeavesMargin()
    {
        var camera = new Camera2D();
        // Exact-fit rect, then 25% padding each side -> effective dims x1.5 -> zoom 1/1.5.
        camera.Focus(new Rectangle(0, 0, 800, 600), Vp, paddingFraction: 0.25f);
        Assert.Equal(1f / 1.5f, camera.Zoom, Tol);
        AssertClose(new Vector2(400, 300), camera.Position);
    }
}
