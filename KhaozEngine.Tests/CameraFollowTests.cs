using System;
using KhaozEngine.Graphics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>Headless coverage for <see cref="CameraFollow"/> (frame-rate-independent smoothing + deadzone).</summary>
public class CameraFollowTests
{
    private static Viewport Vp => new Viewport(0, 0, 800, 600);   // center (400, 300)
    private const float Tol = 1e-2f;
    private static readonly Rectangle Unbounded = new(-1_000_000, -1_000_000, 2_000_000, 2_000_000);

    private static void AssertClose(Vector2 expected, Vector2 actual, float tol = Tol) =>
        Assert.True(Vector2.Distance(expected, actual) <= tol, $"expected {expected}, got {actual}");

    [Fact]
    public void SnapsWhenStiffnessNonPositive()
    {
        var cam = new Camera2D();
        var follow = new CameraFollow(cam) { Stiffness = 0f };

        follow.Update(new Vector2(100, -50), 0.016f, Vp, Unbounded);

        AssertClose(new Vector2(100, -50), cam.Position);   // no smoothing -> centers on target
    }

    [Fact]
    public void SmoothedStepMovesFractionTowardTarget()
    {
        var cam = new Camera2D { Position = Vector2.Zero };
        var follow = new CameraFollow(cam) { Stiffness = 10f };

        follow.Update(new Vector2(100, 0), 0.1f, Vp, Unbounded);

        // t = 1 - exp(-10 * 0.1) = 1 - exp(-1) = 0.6321
        AssertClose(new Vector2(63.21f, 0), cam.Position);
    }

    [Fact]
    public void SmoothingIsFrameRateIndependent()
    {
        var target = new Vector2(100, 0);

        var camOne = new Camera2D { Position = Vector2.Zero };
        var followOne = new CameraFollow(camOne) { Stiffness = 10f };
        followOne.Update(target, 0.2f, Vp, Unbounded);            // one 0.2s step

        var camTwo = new Camera2D { Position = Vector2.Zero };
        var followTwo = new CameraFollow(camTwo) { Stiffness = 10f };
        followTwo.Update(target, 0.1f, Vp, Unbounded);            // two 0.1s steps
        followTwo.Update(target, 0.1f, Vp, Unbounded);

        // Same elapsed time -> same end position, regardless of step count.
        AssertClose(camOne.Position, camTwo.Position, 0.1f);
        Assert.True(camOne.Position.X is > 86f and < 87f);       // 1 - exp(-2) = 0.8647
    }

    [Fact]
    public void DeadzoneHoldsTargetWithoutMoving()
    {
        var cam = new Camera2D { Position = Vector2.Zero, Zoom = 1f };
        // Deadzone around screen center; target maps inside it, so the camera must not move.
        var follow = new CameraFollow(cam) { Stiffness = 10f, Deadzone = new Rectangle(300, 200, 200, 200) };

        follow.Update(new Vector2(50, 0), 0.1f, Vp, Unbounded);   // world (50,0) -> screen (450,300), inside

        AssertClose(Vector2.Zero, cam.Position);
    }

    [Fact]
    public void DeadzoneChasesOnceTargetLeaves()
    {
        var cam = new Camera2D { Position = Vector2.Zero, Zoom = 1f };
        var follow = new CameraFollow(cam) { Stiffness = 0f, Deadzone = new Rectangle(300, 200, 200, 200) };

        // world (200,0) -> screen (600,300); 100px past the deadzone right edge (500).
        follow.Update(new Vector2(200, 0), 0.1f, Vp, Unbounded);

        AssertClose(new Vector2(100, 0), cam.Position);          // snap moves camera so target sits on the edge
        AssertClose(new Vector2(500, 300), cam.WorldToScreen(new Vector2(200, 0), Vp));
    }

    [Fact]
    public void ClampsToWorldBounds()
    {
        var cam = new Camera2D { Zoom = 1f };
        var follow = new CameraFollow(cam) { Stiffness = 0f };
        var bounds = new Rectangle(0, 0, 1000, 1000);   // halfW 400 -> X[400,600], halfH 300 -> Y[300,700]

        follow.Update(new Vector2(9999, 500), 0.016f, Vp, bounds);

        Assert.Equal(600f, cam.Position.X, Tol);        // clamped to the right edge
        Assert.Equal(500f, cam.Position.Y, Tol);
    }
}
