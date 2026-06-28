using System;
using System.Numerics;
using KhaozEngine.Collision;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// A domed rock is a vertical CYLINDER collider (radius = the footprint) whose Top is the rock's PEAK, paired with a
// walkable surface that ramps from a low rim up to that peak. Gating the side-block on the peak makes the rock only
// mountable by dropping onto it from directly above: walking or jumping up from the side is blocked because the
// cylinder keeps the capsule centre outside the surface footprint and a jump's apex is below the peak. The side-block
// must instead release at the surface height where you would step onto the prop (the rim toward you).
public class RockRimMountTests
{
    static float Flat(float x, float z) => 0f;

    // n x n unit dome centred on the origin: height ramps from peak at the centre down to rimFloor at footprintR,
    // then NaN (uncovered) beyond - mirroring a baked rock whose collider radius exceeds the surface footprint.
    static PropSurface RadialDome(int n, float cell, float peak, float rimFloor, float footprintR)
    {
        float origin = -((n - 1) * cell) / 2f;
        var h = new float[n * n];
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            float x = origin + i * cell, z = origin + j * cell;
            float r = MathF.Sqrt(x * x + z * z);
            h[j * n + i] = r <= footprintR ? peak + (rimFloor - peak) * (r / footprintR) : float.NaN;
        }
        return new PropSurface(n, n, cell, origin, origin, h);
    }

    // Dome: peak 1.8 (above a jump's ~1.28 m apex, so the OLD peak-gate blocks), rim ~0.6, surface to r=0.9; the
    // cylinder collider radius 1.0 exceeds the 0.9 surface footprint (as a real baked rock does).
    static (WorldSurfaces surfaces, WorldColliders colliders) Rock()
    {
        var ps = RadialDome(9, 0.25f, peak: 1.8f, rimFloor: 0.6f, footprintR: 0.9f);
        var surfaces = new WorldSurfaces(new[] { new WorldSurface(ps, Vector2.Zero, 1f, 0f, 0f) });
        var colliders = new WorldColliders(new[] { WorldCollider.Cylinder(Vector2.Zero, 1.0f, top: ps.MaxHeight) });
        return (surfaces, colliders);
    }

    [Fact]
    public void JumpsUpDomeFromSide_Mounts()
    {
        var (surfaces, colliders) = Rock();
        var tuning = MoveTuning.Default;
        // Start on the terrain just outside the rock (capsule centre clears cylinder 1.0 + capsule 0.4), walking -X
        // toward it and jumping on every grounded tick. The old peak-gate jammed the capsule at the cylinder edge
        // (1.4) forever; the rim-gate lets a jump carry it up onto the dome.
        var s = new MoveState { Position = new Vector3(1.6f, tuning.CapsuleHalfHeight, 0f), Grounded = true };
        bool mounted = false;
        for (int i = 0; i < 80 && !mounted; i++)
        {
            var cmd = new MoveCommand(new Vector2(-1f, 0f), run: false, cameraYaw: 0f, jump: s.Grounded);
            s = CharacterMovement.Step(s, cmd, 1f / 60f, Flat, tuning, null, colliders, null, surfaces);
            // Mounted = grounded over the surface footprint, well above the terrain (0.9).
            bool overFootprint = surfaces.Query(s.Position.X, s.Position.Z).HasValue;
            if (s.Grounded && overFootprint && s.Position.Y > 1.3f) mounted = true;
        }
        Assert.True(mounted, $"never mounted the rock; ended at ({s.Position.X:0.00},{s.Position.Y:0.00})");
    }

    [Fact]
    public void WalksIntoDomeBaseAtGroundLevel_Blocked()
    {
        var (surfaces, colliders) = Rock();
        var tuning = MoveTuning.Default;
        // Walk -X into the rock at ground level WITHOUT jumping: feet stay at 0, below the rim, so the side blocks it.
        var s = new MoveState { Position = new Vector3(1.6f, tuning.CapsuleHalfHeight, 0f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(-1f, 0f), run: false, cameraYaw: 0f);
        for (int i = 0; i < 120; i++)
            s = CharacterMovement.Step(s, cmd, 1f / 60f, Flat, tuning, null, colliders, null, surfaces);
        float dist = new Vector2(s.Position.X, s.Position.Z).Length();
        Assert.True(dist > 1.3f, $"walked through the rock base at ground level: dist={dist}");
        Assert.Equal(tuning.CapsuleHalfHeight, s.Position.Y, 1); // still on the terrain, not on the rock
    }

    [Fact]
    public void Resolve_FeetAtRim_NotPushed()
    {
        // Direct on the new height-aware overload: capsule centre at the cylinder edge, feet at the rim height the
        // dome offers on this side -> standing on it, not a side hit, so not pushed.
        var (surfaces, colliders) = Rock();
        // Feet at 0.85, above the ~0.8 rim the dome offers on this side -> standing on it, not pushed.
        Vector2 r = colliders.Resolve(new Vector2(1.0f, 0f), 0.4f, footY: 0.85f, surfaces);
        Assert.Equal(1.0f, r.X, 2);
        Assert.Equal(0f, r.Y, 2);
    }

    [Fact]
    public void Resolve_FeetBelowRim_Pushed()
    {
        // Feet below the rim (at ground level) -> a genuine side hit into the rock base, pushed out.
        var (surfaces, colliders) = Rock();
        Vector2 r = colliders.Resolve(new Vector2(1.0f, 0f), 0.4f, footY: 0.0f, surfaces);
        Assert.True(r.X > 1.3f, $"not pushed out of the base: x={r.X}");
    }

    [Fact]
    public void StandsOnDome_NotShovedOff()
    {
        // Regression of the 7.56.1 fix under the new gate: dropped onto the dome, it stays (not pushed off the side).
        var (surfaces, colliders) = Rock();
        var tuning = MoveTuning.Default;
        var s = new MoveState { Position = new Vector3(0.4f, 5f, 0f), Grounded = false };
        for (int i = 0; i < 180; i++)
            s = CharacterMovement.Step(s, default, 1f / 60f, Flat, tuning, null, colliders, null, surfaces);
        Assert.True(s.Grounded, "did not settle on the dome");
        Assert.True(s.Position.Y > 1.3f, $"shoved off the dome: y={s.Position.Y}");
        Assert.True(new Vector2(s.Position.X, s.Position.Z).Length() < 0.7f, "pushed out of the footprint");
    }
}
