using System;
using System.Numerics;
using KhaozEngine.Collision;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class SurfaceMovementTests
{
    static float Flat(float x, float z) => 0f;
    static PropSurface Slab(float y) { return new PropSurface(3, 3, 1f, -1.5f, -1.5f, new[] { y, y, y, y, y, y, y, y, y }); }

    [Fact]
    public void StandsOnRockSurface_WhenAbove()
    {
        // A 1.5 m flat-topped slab at origin. A capsule dropped from above lands on top (y = top + half-height).
        var surfaces = new WorldSurfaces(new[] { new WorldSurface(Slab(1.5f), Vector2.Zero, 1f, 0f, 0f) });
        var tuning = MoveTuning.Default;
        var s = new MoveState { Position = new Vector3(0f, 5f, 0f), VerticalVelocity = 0f, Grounded = false };
        for (int i = 0; i < 120; i++)
            s = CharacterMovement.Step(s, default, 1f / 60f, Flat, tuning, null, null, null, surfaces);
        Assert.Equal(1.5f + tuning.CapsuleHalfHeight, s.Position.Y, 1);
        Assert.True(s.Grounded);
    }

    [Fact]
    public void NoSurfaces_FallsToTerrain_Unchanged()
    {
        var tuning = MoveTuning.Default;
        var a = new MoveState { Position = new Vector3(0f, 5f, 0f) };
        var b = a;
        for (int i = 0; i < 120; i++)
        {
            a = CharacterMovement.Step(a, default, 1f / 60f, Flat, tuning, null, null, null, null);
            b = CharacterMovement.Step(b, default, 1f / 60f, Flat, tuning, null, null, null, new WorldSurfaces(Array.Empty<WorldSurface>()));
        }
        Assert.Equal(a.Position.Y, b.Position.Y, 4);
    }

    [Fact]
    public void StepsUpLowLedge_WithoutJump()
    {
        // A 0.3 m ledge (below the 0.4 step height) centred at z=-1 (near edge z=0.5). Start north of it (z=2) on
        // the terrain and walk -Z onto it; it is mounted without a jump and the capsule ends standing on the ledge.
        var surfaces = new WorldSurfaces(new[] { new WorldSurface(Slab(0.3f), new Vector2(0f, -1.0f), 1f, 0f, 0f) });
        var tuning = MoveTuning.Default;
        var cmd = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f); // walk -Z toward the ledge
        var s = new MoveState { Position = new Vector3(0f, tuning.CapsuleHalfHeight, 2f), Grounded = true };
        for (int i = 0; i < 60; i++)
            s = CharacterMovement.Step(s, cmd, 1f / 60f, Flat, tuning, null, null, null, surfaces);
        Assert.True(s.Position.Z < 0f, $"did not advance onto the ledge: z={s.Position.Z}");
        Assert.Equal(0.3f + tuning.CapsuleHalfHeight, s.Position.Y, 1); // standing on the 0.3 m ledge
    }
}
