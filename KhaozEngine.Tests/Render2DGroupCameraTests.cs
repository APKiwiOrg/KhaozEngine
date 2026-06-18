using System;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>Headless coverage for multi-target framing (CameraFraming + GroupCamera).</summary>
public class Render2DGroupCameraTests
{
    private const int Vw = 800, Vh = 600;
    private const float Tol = 1e-2f;
    private static readonly Rect Unbounded = new(-1_000_000f, -1_000_000f, 2_000_000f, 2_000_000f);

    private static void AssertClose(Vector2 expected, Vector2 actual, float tol = Tol) =>
        Assert.True(Vector2.Distance(expected, actual) <= tol, $"expected {expected}, got {actual}");

    private static void AssertInViewport(Camera2D cam, Vector2 world)
    {
        var s = cam.WorldToScreen(world, Vw, Vh);
        Assert.True(s.X is >= 0f and <= Vw && s.Y is >= 0f and <= Vh, $"world {world} -> screen {s} outside viewport");
    }

    // ---- CameraFraming ----

    [Fact]
    public void Bounds_IsTightAabbWithNoPadding()
    {
        var pts = new[] { new Vector2(0f, 0f), new Vector2(100f, 40f) };
        var b = CameraFraming.Bounds(pts, 0f, Vector2.Zero);
        Assert.Equal(0f, b.X, Tol);
        Assert.Equal(0f, b.Y, Tol);
        Assert.Equal(100f, b.Width, Tol);
        Assert.Equal(40f, b.Height, Tol);
    }

    [Fact]
    public void Bounds_PaddingExpandsSymmetrically()
    {
        var pts = new[] { new Vector2(0f, 0f), new Vector2(100f, 40f) };
        var b = CameraFraming.Bounds(pts, 0.1f, Vector2.Zero);   // w*1.2=120, h*1.2=48, center (50,20)
        Assert.Equal(-10f, b.X, Tol);
        Assert.Equal(-4f, b.Y, Tol);
        Assert.Equal(120f, b.Width, Tol);
        Assert.Equal(48f, b.Height, Tol);
    }

    [Fact]
    public void Bounds_MinViewSizeFloorsClusteredPoints()
    {
        var pts = new[] { new Vector2(5f, 5f), new Vector2(5f, 5f) };   // zero-extent cluster
        var b = CameraFraming.Bounds(pts, 0f, new Vector2(10f, 10f));   // floored to 10x10 centered on (5,5)
        Assert.Equal(0f, b.X, Tol);
        Assert.Equal(0f, b.Y, Tol);
        Assert.Equal(10f, b.Width, Tol);
        Assert.Equal(10f, b.Height, Tol);
    }

    [Fact]
    public void Solve_CentersPositionAndContainFits()
    {
        var (pos, zoom) = CameraFraming.Solve(new Rect(0f, 0f, 200f, 100f), Vw, Vh, 0.0001f, float.MaxValue);
        AssertClose(new Vector2(100f, 50f), pos);
        Assert.Equal(4f, zoom, Tol);   // min(800/200, 600/100) = min(4,6) = 4
    }

    [Fact]
    public void Solve_ClampsZoomToMax()
    {
        var (_, zoom) = CameraFraming.Solve(new Rect(0f, 0f, 200f, 100f), Vw, Vh, 0.0001f, 2f);
        Assert.Equal(2f, zoom, Tol);
    }

    [Fact]
    public void Solve_ZeroSizeBoundsDoesNotDivideByZero()
    {
        var (pos, zoom) = CameraFraming.Solve(new Rect(0f, 0f, 0f, 0f), Vw, Vh, 0.0001f, 100f);
        AssertClose(Vector2.Zero, pos);
        Assert.Equal(100f, zoom, Tol);   // huge fit, clamped to maxZoom; no NaN/Infinity
    }

    [Fact]
    public void Bounds_SinglePointWithZeroMinSizeFloorsToEpsilon()
    {
        var pts = new[] { new Vector2(7f, -3f) };
        var b = CameraFraming.Bounds(pts, 0f, Vector2.Zero);   // zero extent + zero min -> epsilon-sized box on the point
        Assert.True(b.Width > 0f && b.Width < 1e-3f, $"width {b.Width} not epsilon-sized");
        Assert.True(b.Height > 0f && b.Height < 1e-3f, $"height {b.Height} not epsilon-sized");
        // box stays centered on the point
        Assert.Equal(7f, b.X + b.Width * 0.5f, 1e-3f);
        Assert.Equal(-3f, b.Y + b.Height * 0.5f, 1e-3f);
    }

    [Fact]
    public void Solve_ClampsZoomToMin()
    {
        // Bounds far larger than the viewport -> fit < minZoom -> clamped up to minZoom.
        var (_, zoom) = CameraFraming.Solve(new Rect(0f, 0f, 80_000f, 60_000f), Vw, Vh, 0.5f, float.MaxValue);
        Assert.Equal(0.5f, zoom, Tol);   // fit = min(800/80000, 600/60000) = 0.01, clamped up to 0.5
    }
}
