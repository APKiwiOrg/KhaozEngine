using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// LIP-CONTACT SHORT-RISER MOUNT. A SHORT step (effective riser well under StepHeight 0.4) on flat analytic ground is
// contacted by the capsule's rounded BOTTOM CAP over the tread edge, so the sweep reports an up-TILTED lip normal
// (measured here: normal.Y ~0.25 at riser 0.30 climbing to ~0.75 at riser 0.10, with normal.Z ~0.9) instead of a flat
// vertical wall face. On main this dead-stalls the mount two ways:
//   1. The step-up eligibility precheck was |normal.Y| < 0.5, so at effective risers <= ~0.18 (normal.Y >= 0.5) the
//      step-up was rejected outright and the capsule slid along the lip forever.
//   2. Even when the precheck passed, TryStepUp raised the capsule a full StepHeight then swept back DOWN to find the
//      tread - and for a one-sided TRIANGLE-mesh step the tread sat in the far half of that StepHeight range, where
//      Bepu's mesh sweep under-reports the hit, so the down-sweep found nothing and the mount was refused. A SOLID
//      convex box tread reports its sweep reliably and mounted; the identical one-sided riser (the shape real
//      buildings/curbs bake to) stalled. This is the engine-side reproduction of the consumer's staircase-corner
//      stall on rolling terrain.
// The fix (CharacterMovement): widen the step-up precheck to "not walkable, not a ceiling" (TryStepUp self-validates),
// and widen TryStepUp's down-sweep range to ~2x StepHeight so a short mesh tread lands in the sweep's near half.
//
// These drive CharacterMovement.Step against real Bepu geometry (a solid box and a one-sided triangle-mesh step) with
// the shipped consumer tuning (walk 3 / run 6, radius 0.4, half-height 0.9, StepHeight 0.4, MaxStepClimbSpeed 3.5),
// parametrized over riser height (0.15..0.30, all below StepHeight), approach angle (0/15/30 deg), walk/run, and dt
// 1/30 + 1/60. Pre-fix (main) the mesh cases at riser <= 0.22 and the box cases at riser <= 0.18 are RED (never mount);
// the fix makes every case mount.
public class LipContactShortRiserTests
{
    static MoveTuning Consumer => MoveTuning.Default with { WalkSpeed = 3f, RunSpeed = 6f };

    // Solid convex box: top at Y=height, buried below, wide in X, deep in Z (Z=0 back to -depth) so the capsule cannot
    // cross a shallow tread and walk off the back within the drive window.
    static void AddBox(IPhysicsWorld world, float height, float depth = 40f, float halfW = 20f)
    {
        float burial = 1.5f;
        float halfH = (height + burial) * 0.5f;
        world.AddStatic(new BoxShape(new Vector3(halfW, halfH, depth * 0.5f)),
            Pose.At(new Vector3(0f, height - halfH, -depth * 0.5f)));
    }

    // One-sided step mesh: a +Z-facing riser quad (Y 0..height at Z=0) and a +Y-facing tread quad (Y=height, Z 0..-depth),
    // wide in X - the shape a real building/curb collision proxy bakes to (SingleRiserMountTests uses the same build).
    static TriangleMeshShape Mesh(float height, float treadDepth = 40f, float halfX = 20f)
    {
        var v = new List<Vector3>(); var idx = new List<int>();
        void Tri(int a, int b, int c) { idx.Add(a); idx.Add(b); idx.Add(c); }
        int b0 = v.Count;
        v.Add(new Vector3(-halfX, 0f, 0f)); v.Add(new Vector3(halfX, 0f, 0f));
        v.Add(new Vector3(halfX, height, 0f)); v.Add(new Vector3(-halfX, height, 0f));
        Tri(b0 + 0, b0 + 2, b0 + 1); Tri(b0 + 0, b0 + 3, b0 + 2);
        b0 = v.Count;
        v.Add(new Vector3(-halfX, height, 0f)); v.Add(new Vector3(halfX, height, 0f));
        v.Add(new Vector3(halfX, height, -treadDepth)); v.Add(new Vector3(-halfX, height, -treadDepth));
        Tri(b0 + 0, b0 + 1, b0 + 2); Tri(b0 + 0, b0 + 2, b0 + 3);
        return new TriangleMeshShape(v.ToArray(), idx.ToArray());
    }

    // Drive a walk/run into the step from ~1.2 m in front, heading rotated by angleDeg off the straight-in (-Z) line.
    // Returns the largest run of consecutive ticks the capsule held at/above the tread top (a real mount holds it; a
    // stall never reaches it; a runner crossing a deep tread and walking off the back still records the held run).
    static int MountHeldTicks(IPhysicsWorld world, float h, MoveTuning t, float angleDeg, float dt, bool run, int ticks = 180)
    {
        float halfH = t.CapsuleHalfHeight;
        float mountedY = h + halfH;
        float a = angleDeg * MathF.PI / 180f;
        var move = new Vector2(MathF.Sin(a), MathF.Cos(a));   // cmd.Move: X = strafe, Y = forward (-Z at yaw 0)
        var state = new MoveState { Position = new Vector3(0f, halfH, 1.2f), Grounded = true };
        var cmd = new MoveCommand(move, run, cameraYaw: 0f, jump: false);
        float Ground(float x, float z) => 0f;
        Func<float, float, Vector3> normal = (x, z) => Vector3.UnitY;
        int streak = 0, best = 0;
        for (int i = 0; i < ticks; i++)
        {
            state = CharacterMovement.Step(state, cmd, dt, Ground, t, normal, world);
            if (state.Position.Y > mountedY - 0.05f) { streak++; best = Math.Max(best, streak); }
            else streak = 0;
        }
        return best;
    }

    public static IEnumerable<object[]> Cases()
    {
        float[] heights = { 0.15f, 0.18f, 0.20f, 0.22f, 0.24f, 0.30f };
        float[] angles = { 0f, 15f, 30f };
        (float dt, bool run)[] speeds = { (1f / 30f, false), (1f / 60f, false), (1f / 30f, true), (1f / 60f, true) };
        foreach (bool mesh in new[] { false, true })
            foreach (float h in heights)
                foreach (float ang in angles)
                    foreach ((float dt, bool run) in speeds)
                        yield return new object[] { mesh, h, ang, dt, run };
    }

    // The short riser mounts at every height / angle / speed / dt: a curb, doorstep, or shallow tread lip on uneven
    // ground is climbed, not slid along forever. Pre-fix the mesh (riser <= 0.22) and box (riser <= 0.18) cases are RED.
    [Theory]
    [MemberData(nameof(Cases))]
    public void ShortRiser_Mounts(bool mesh, float h, float angleDeg, float dt, bool run)
    {
        var t = Consumer;
        using IPhysicsWorld world = new BepuPhysicsWorld();
        if (mesh) world.AddStatic(Mesh(h), Pose.At(Vector3.Zero)); else AddBox(world, h);
        world.Step(dt);

        int held = MountHeldTicks(world, h, t, angleDeg, dt, run);
        string tag = $"{(mesh ? "mesh" : "box")} riser={h:F2} angle={angleDeg:F0} dt={dt:F4} {(run ? "run" : "walk")}";
        Assert.True(held > 2,
            $"short riser never mounted ({tag}): capsule never held the tread top (~{h + t.CapsuleHalfHeight:F2} m) - it " +
            $"dead-stalled at the lip instead of climbing.");
    }

    // Vertical progress while mounting is MONOTONE (no rise-fall vibrate): the stall signature was a per-tick mount-then-
    // fall as the capsule lost and re-touched the lip. A clean mount only ever rises until seated. Head-on, one-sided
    // mesh riser 0.20 (a case that dead-stalls on main).
    [Fact]
    public void ShortRiser_VerticalProgress_IsMonotone_WhileMounting()
    {
        var t = Consumer;
        float halfH = t.CapsuleHalfHeight;
        const float h = 0.20f;
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(Mesh(h), Pose.At(Vector3.Zero));
        world.Step(1f / 30f);

        var state = new MoveState { Position = new Vector3(0f, halfH, 1.2f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f, jump: false);
        float Ground(float x, float z) => 0f;
        Func<float, float, Vector3> normal = (x, z) => Vector3.UnitY;

        var ys = new List<float>();
        for (int i = 0; i < 60; i++) { state = CharacterMovement.Step(state, cmd, 1f / 30f, Ground, t, normal, world); ys.Add(state.Position.Y); }

        // engaged = first tick off the flat floor.
        int engage = ys.FindIndex(y => y > halfH + 0.01f);
        Assert.True(engage >= 0, $"never engaged the riser (dead stall): final Y {ys[^1]:F3}, flat {halfH:F3}.");
        Assert.True(ys[^1] > h + halfH - 0.05f, $"never seated on the tread: final Y {ys[^1]:F3}, expected ~{h + halfH:F3}.");
        for (int i = engage + 1; i < ys.Count && ys[i] < h + halfH - 0.02f; i++)
            Assert.True(ys[i] >= ys[i - 1] - 1e-3f,
                $"vertical progress went BACKWARDS at tick {i}: {ys[i - 1]:F4} -> {ys[i]:F4} (the rise-fall lip vibrate).");
    }

    // The paced mount respects MaxStepClimbSpeed: the short riser is not snapped in one tick when pacing is on, so the
    // climb is smooth (the same smoothness pin SingleRiserMountTests holds, now on a lip-contact riser).
    [Fact]
    public void ShortRiser_Mount_RespectsMaxStepClimbSpeed()
    {
        var t = Consumer;
        float halfH = t.CapsuleHalfHeight;
        const float h = 0.20f;
        using IPhysicsWorld world = new BepuPhysicsWorld();
        world.AddStatic(Mesh(h), Pose.At(Vector3.Zero));
        world.Step(1f / 30f);

        var state = new MoveState { Position = new Vector3(0f, halfH, 1.2f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, 1f), run: false, cameraYaw: 0f, jump: false);
        float Ground(float x, float z) => 0f;
        Func<float, float, Vector3> normal = (x, z) => Vector3.UnitY;

        float maxRise = t.MaxStepClimbSpeed * (1f / 30f);
        float prev = state.Position.Y, maxUp = 0f;
        for (int i = 0; i < 60; i++)
        {
            state = CharacterMovement.Step(state, cmd, 1f / 30f, Ground, t, normal, world);
            maxUp = MathF.Max(maxUp, state.Position.Y - prev);
            prev = state.Position.Y;
        }
        Assert.True(state.Position.Y > h + halfH - 0.05f, $"did not mount: final Y {state.Position.Y:F3}.");
        Assert.True(maxUp <= maxRise + 0.01f,
            $"a tick rose {maxUp:F4} m, over the {maxRise:F4} m/tick climb budget (MaxStepClimbSpeed {t.MaxStepClimbSpeed}): the lip mount is not paced.");
    }
}
