using System;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

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

    // ---- GroupCamera ----

    [Fact]
    public void Group_WarpFramesTwoTargetsInsideViewport()
    {
        var cam = new Camera2D();
        var group = new GroupCamera(cam);
        var targets = new[] { new Vector2(0f, 0f), new Vector2(200f, 0f) };

        group.Warp(targets, Vw, Vh, Unbounded);

        AssertInViewport(cam, targets[0]);
        AssertInViewport(cam, targets[1]);
    }

    [Fact]
    public void Group_WarpCentersAndZoomsLikeSolve()
    {
        var cam = new Camera2D();
        var group = new GroupCamera(cam) { PaddingFraction = 0f, MinViewSize = Vector2.Zero };
        var targets = new[] { new Vector2(0f, 0f), new Vector2(200f, 100f) };

        group.Warp(targets, Vw, Vh, Unbounded);

        // bounds (0,0,200,100) -> center (100,50), zoom min(800/200,600/100)=4
        AssertClose(new Vector2(100f, 50f), cam.Position);
        Assert.Equal(4f, cam.Zoom, Tol);
    }

    [Fact]
    public void Group_UpdateEasesTowardFramingAndConverges()
    {
        var cam = new Camera2D { Position = Vector2.Zero, Zoom = 1f };
        var group = new GroupCamera(cam) { PaddingFraction = 0f, MinViewSize = Vector2.Zero };
        var targets = new[] { new Vector2(0f, 0f), new Vector2(200f, 100f) };

        for (int i = 0; i < 200; i++)
            group.Update(targets, 0.1f, Vw, Vh, Unbounded);

        AssertClose(new Vector2(100f, 50f), cam.Position);
        Assert.Equal(4f, cam.Zoom, Tol);
    }

    [Fact]
    public void Group_NonPositiveStiffnessSnapsToFraming()
    {
        var camSnap = new Camera2D();
        var snap = new GroupCamera(camSnap) { Stiffness = 0f, ZoomStiffness = 0f };
        var camWarp = new Camera2D();
        var warp = new GroupCamera(camWarp);
        var targets = new[] { new Vector2(10f, 20f), new Vector2(210f, 120f) };

        snap.Update(targets, 0.016f, Vw, Vh, Unbounded);
        warp.Warp(targets, Vw, Vh, Unbounded);

        AssertClose(camWarp.Position, camSnap.Position);
        Assert.Equal(camWarp.Zoom, camSnap.Zoom, Tol);
    }

    [Fact]
    public void Group_SeparatingTargetsZoomsOut()
    {
        var cam = new Camera2D();
        var group = new GroupCamera(cam) { PaddingFraction = 0f, MinViewSize = Vector2.Zero };

        var close = new[] { new Vector2(0f, 0f), new Vector2(50f, 0f) };
        group.Warp(close, Vw, Vh, Unbounded);
        float zoomClose = cam.Zoom;

        var spread = new[] { new Vector2(0f, 0f), new Vector2(400f, 0f) };
        for (int i = 0; i < 200; i++)
            group.Update(spread, 0.1f, Vw, Vh, Unbounded);

        Assert.True(cam.Zoom < zoomClose, $"expected zoom-out: spread {cam.Zoom} < close {zoomClose}");
    }

    [Fact]
    public void Group_FrameRateIndependent()
    {
        var targets = new[] { new Vector2(0f, 0f), new Vector2(200f, 100f) };

        var camOne = new Camera2D { Position = Vector2.Zero, Zoom = 1f };
        var groupOne = new GroupCamera(camOne) { PaddingFraction = 0f, MinViewSize = Vector2.Zero };
        groupOne.Update(targets, 0.2f, Vw, Vh, Unbounded);

        var camTwo = new Camera2D { Position = Vector2.Zero, Zoom = 1f };
        var groupTwo = new GroupCamera(camTwo) { PaddingFraction = 0f, MinViewSize = Vector2.Zero };
        groupTwo.Update(targets, 0.1f, Vw, Vh, Unbounded);
        groupTwo.Update(targets, 0.1f, Vw, Vh, Unbounded);

        AssertClose(camOne.Position, camTwo.Position, Tol);
        Assert.Equal(camOne.Zoom, camTwo.Zoom, Tol);
    }

    [Fact]
    public void Group_EmptyTargetsHoldsView()
    {
        var cam = new Camera2D { Position = new Vector2(5f, 5f), Zoom = 2f };
        var group = new GroupCamera(cam);

        group.Update(Array.Empty<Vector2>(), 0.1f, Vw, Vh, Unbounded);

        AssertClose(new Vector2(5f, 5f), cam.Position);
        Assert.Equal(2f, cam.Zoom, Tol);
    }

    [Fact]
    public void Group_UpdateClampsPositionToWorldBounds()
    {
        var cam = new Camera2D();
        var group = new GroupCamera(cam) { PaddingFraction = 0f, MinViewSize = new Vector2(100f, 100f) };
        // single point at origin -> zoom 6, desired center (0,0); world (0,0,1000,1000) clamps to (66.67, 50).
        var targets = new[] { Vector2.Zero };
        var bounds = new Rect(0f, 0f, 1000f, 1000f);

        for (int i = 0; i < 200; i++)
            group.Update(targets, 0.1f, Vw, Vh, bounds);

        Assert.Equal(6f, cam.Zoom, Tol);
        // Eases toward (0,0) but is clamped back to the world edge every frame using the eased zoom.
        Assert.Equal(66.6667f, cam.Position.X, 1e-1f);
        Assert.Equal(50f, cam.Position.Y, 1e-1f);
    }

    [Fact]
    public void Group_WarpClampsPositionToWorldBounds()
    {
        var cam = new Camera2D();
        var group = new GroupCamera(cam) { PaddingFraction = 0f, MinViewSize = new Vector2(100f, 100f) };
        // single point at origin -> bounds (-50,-50,100,100) -> zoom min(800/100,600/100)=6, center (0,0).
        var targets = new[] { Vector2.Zero };
        var bounds = new Rect(0f, 0f, 1000f, 1000f);   // halfW 800/(2*6)=66.67, halfH 600/(2*6)=50

        group.Warp(targets, Vw, Vh, bounds);

        Assert.Equal(6f, cam.Zoom, Tol);
        Assert.Equal(66.6667f, cam.Position.X, 1e-1f);   // clamped to worldBounds.X + halfW
        Assert.Equal(50f, cam.Position.Y, 1e-1f);        // clamped to worldBounds.Y + halfH
    }
}
