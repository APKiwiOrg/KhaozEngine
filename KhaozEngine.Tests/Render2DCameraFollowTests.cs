using System;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>Headless coverage for the 5.x camera feel layer (PixelSnap, LookAheadSettings, CameraFollow).</summary>
public class Render2DCameraFollowTests
{
    private const int Vw = 800, Vh = 600;                    // center (400, 300)
    private const float Tol = 1e-2f;
    private static readonly Rect Unbounded = new(-1_000_000f, -1_000_000f, 2_000_000f, 2_000_000f);

    private static void AssertClose(Vector2 expected, Vector2 actual, float tol = Tol) =>
        Assert.True(Vector2.Distance(expected, actual) <= tol, $"expected {expected}, got {actual}");

    // ---- PixelSnap ----

    [Fact]
    public void PixelSnap_RoundsEachAxisToGrid()
    {
        var snap = new PixelSnap(10f);
        AssertClose(new Vector2(10f, 10f), snap.Apply(new Vector2(13f, 7f)));
        AssertClose(new Vector2(20f, -10f), snap.Apply(new Vector2(16f, -12f)));
    }

    [Fact]
    public void PixelSnap_DisabledIsIdentity()
    {
        var snap = default(PixelSnap);          // Enabled == false
        var p = new Vector2(13.37f, -4.2f);
        AssertClose(p, snap.Apply(p));
    }

    [Fact]
    public void PixelSnap_NonPositiveGridIsIdentity()
    {
        var snap = new PixelSnap(0f);           // non-positive grid -> Enabled stays false
        var p = new Vector2(13.37f, -4.2f);
        AssertClose(p, snap.Apply(p));
    }

    // ---- CameraFollow foundation ----

    [Fact]
    public void Follow_SnapsWhenStiffnessNonPositive()
    {
        var cam = new Camera2D();
        var follow = new CameraFollow(cam);
        follow.SetStiffness(0f);

        follow.Update(new Vector2(100f, -50f), 0.016f, Vw, Vh, Unbounded);

        AssertClose(new Vector2(100f, -50f), cam.Position);
    }

    [Fact]
    public void Follow_SmoothedStepMovesFractionTowardTarget()
    {
        var cam = new Camera2D { Position = Vector2.Zero };
        var follow = new CameraFollow(cam) { Stiffness = new Vector2(10f, 10f) };

        follow.Update(new Vector2(100f, 0f), 0.1f, Vw, Vh, Unbounded);

        // t = 1 - exp(-10 * 0.1) = 0.6321
        AssertClose(new Vector2(63.21f, 0f), cam.Position);
    }

    [Fact]
    public void Follow_SmoothingIsFrameRateIndependent()
    {
        var target = new Vector2(100f, 0f);

        var camOne = new Camera2D { Position = Vector2.Zero };
        var followOne = new CameraFollow(camOne) { Stiffness = new Vector2(10f, 10f) };
        followOne.Update(target, 0.2f, Vw, Vh, Unbounded);

        var camTwo = new Camera2D { Position = Vector2.Zero };
        var followTwo = new CameraFollow(camTwo) { Stiffness = new Vector2(10f, 10f) };
        followTwo.Update(target, 0.1f, Vw, Vh, Unbounded);
        followTwo.Update(target, 0.1f, Vw, Vh, Unbounded);

        AssertClose(camOne.Position, camTwo.Position, 0.1f);
        Assert.True(camOne.Position.X is > 86f and < 87f);   // 1 - exp(-2) = 0.8647
    }

    [Fact]
    public void Follow_PerAxisStiffnessIsIndependent()
    {
        var cam = new Camera2D { Position = Vector2.Zero };
        // X eases (stiffness 10), Y snaps (stiffness 0).
        var follow = new CameraFollow(cam) { Stiffness = new Vector2(10f, 0f) };

        follow.Update(new Vector2(100f, 100f), 0.1f, Vw, Vh, Unbounded);

        Assert.Equal(63.21f, cam.Position.X, Tol);   // eased
        Assert.Equal(100f, cam.Position.Y, Tol);     // snapped
    }

    [Fact]
    public void Follow_DeadzoneHoldsTargetWithoutMoving()
    {
        var cam = new Camera2D { Position = Vector2.Zero, Zoom = 1f };
        var follow = new CameraFollow(cam)
        {
            Stiffness = new Vector2(10f, 10f),
            Deadzone = new Rect(300f, 200f, 200f, 200f),
        };

        follow.Update(new Vector2(50f, 0f), 0.1f, Vw, Vh, Unbounded);   // screen (450,300), inside

        AssertClose(Vector2.Zero, cam.Position);
    }

    [Fact]
    public void Follow_DeadzoneChasesOnceTargetLeaves()
    {
        var cam = new Camera2D { Position = Vector2.Zero, Zoom = 1f };
        var follow = new CameraFollow(cam) { Deadzone = new Rect(300f, 200f, 200f, 200f) };
        follow.SetStiffness(0f);

        // world (200,0) -> screen (600,300); 100px past the right edge (500).
        follow.Update(new Vector2(200f, 0f), 0.1f, Vw, Vh, Unbounded);

        AssertClose(new Vector2(100f, 0f), cam.Position);
        AssertClose(new Vector2(500f, 300f), cam.WorldToScreen(new Vector2(200f, 0f), Vw, Vh));
    }

    [Fact]
    public void Follow_ClampsToWorldBounds()
    {
        var cam = new Camera2D { Zoom = 1f };
        var follow = new CameraFollow(cam);
        follow.SetStiffness(0f);
        var bounds = new Rect(0f, 0f, 1000f, 1000f);   // X[400,600], Y[300,700]

        follow.Update(new Vector2(9999f, 500f), 0.016f, Vw, Vh, bounds);

        Assert.Equal(600f, cam.Position.X, Tol);
        Assert.Equal(500f, cam.Position.Y, Tol);
    }

    [Fact]
    public void Follow_WarpHardSetsPositionBypassingSmoothing()
    {
        var cam = new Camera2D { Position = Vector2.Zero };
        var follow = new CameraFollow(cam) { Stiffness = new Vector2(10f, 10f) };

        follow.Warp(new Vector2(500f, 500f));
        Assert.Equal(new Vector2(500f, 500f), cam.Position);

        // One small step toward a far target eases from (500,500), not from the origin.
        follow.Update(new Vector2(1500f, 500f), 0.1f, Vw, Vh, Unbounded);
        Assert.Equal(500f + 1000f * (1f - MathF.Exp(-1f)), cam.Position.X, 0.5f);
    }
}
