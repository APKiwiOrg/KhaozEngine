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

    // A domed rock: peak 1.5 at the centre, sloping down to 0.5 at the corners. The walkable surface under an
    // off-centre point sits BELOW the 1.5 peak (the prop's single max solid top), which is what the side-block bug
    // misclassified as a side hit.
    static PropSurface Dome() => new PropSurface(3, 3, 1f, -1f, -1f,
        new[] { 0.5f, 0.8f, 0.5f, 0.8f, 1.5f, 0.8f, 0.5f, 0.8f, 0.5f });

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
    public void LandsOnDomeSurface_NotShovedOff()
    {
        // The repro: a domed rock whose walkable surface (1.15 at the landing point) is below its 1.5 max top, with
        // a matching cylinder collider (top = the 1.5 peak). A capsule dropped onto an off-centre point must land on
        // the dome and STAY there - the height-aware side-block must not push it off because its feet (on the
        // surface) are below the collider's max top.
        var surfaces = new WorldSurfaces(new[] { new WorldSurface(Dome(), Vector2.Zero, 1f, 0f, 0f) });
        var colliders = new WorldColliders(new[] { WorldCollider.Cylinder(Vector2.Zero, 1f, top: 1.5f) });
        var tuning = MoveTuning.Default;
        var s = new MoveState { Position = new Vector3(0.5f, 5f, 0f), Grounded = false };
        for (int i = 0; i < 120; i++)
            s = CharacterMovement.Step(s, default, 1f / 60f, Flat, tuning, null, colliders, null, surfaces);

        // Surface under (0.5, 0) is ~1.15 -> the capsule rests near 1.15 + half-height, well above terrain (0.9).
        Assert.True(s.Grounded, "capsule did not settle grounded on the dome");
        Assert.True(s.Position.Y > 1.5f, $"shoved off the dome onto the ground: y={s.Position.Y}");
        Assert.True(new Vector2(s.Position.X, s.Position.Z).Length() < 0.7f,
            $"pushed out of the dome footprint: ({s.Position.X}, {s.Position.Z})");
    }

    [Fact]
    public void WalksAcrossDomeTop_StaysElevated()
    {
        // Standing on the dome peak (1.5), a walk command moves the capsule down the +X slope of the top; it must
        // ride the surface the whole way (never pushed off to the terrain at 0.9) and keep advancing.
        var surfaces = new WorldSurfaces(new[] { new WorldSurface(Dome(), Vector2.Zero, 1f, 0f, 0f) });
        var colliders = new WorldColliders(new[] { WorldCollider.Cylinder(Vector2.Zero, 1f, top: 1.5f) });
        var tuning = MoveTuning.Default;
        var s = new MoveState { Position = new Vector3(0f, 1.5f + tuning.CapsuleHalfHeight, 0f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(1f, 0f), run: false, cameraYaw: 0f); // walk +X across the top
        for (int i = 0; i < 12; i++) // 12 * 3 m/s * (1/60) = 0.6 m, staying within the dome footprint
        {
            s = CharacterMovement.Step(s, cmd, 1f / 60f, Flat, tuning, null, colliders, null, surfaces);
            Assert.True(s.Position.Y > 1.4f, $"fell off the dome at tick {i}: y={s.Position.Y}");
        }
        Assert.True(s.Position.X > 0.4f, $"did not walk across the top: x={s.Position.X}");
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
