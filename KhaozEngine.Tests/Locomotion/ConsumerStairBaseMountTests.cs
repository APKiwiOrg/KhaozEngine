using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// Regression for the STICKY / COLLAPSE-BOB BOTTOM STAIR (the support tread-find fix). On the ticks right after the
// first step-up at a staircase BASE, step 4's full-radius downward prop sweep MISSES the tread the capsule is
// mounting: the 0.8 m-diameter footprint STRADDLES the 0.4 m tread, the sweep grazes the vertical riser FRONT face,
// and both of its guards (walkable-up normal + under-footprint point) reject that steep, off-footprint contact. So
// groundY collapses to terrainGroundY; a single step below, that terrain sits within GroundedEpsilon (0.3) of the
// partially-mounted capsule, so the onGround snap DROPS it a whole riser back to the flat, then depenetration and the
// next step-up re-mount it - the sticky, bobbing bottom stair. Mid-climb the terrain is metres below, so the same
// probe miss collapses to nothing reachable and never shows; ONLY the base misbehaves.
//
// These drive CharacterMovement.Step against the EXACT consumer geometry: a convex-BoxShape staircase (riser 0.30,
// tread 0.40, width 2.0, each column buried 1.5 m so its riser fronts and tread tops match) built exactly like
// Ruinborne's TestStaircase.Generate + BuildStairWorld, seated on an ANALYTIC flat approach (Ground = 0, no physics
// floor - as a placed staircase sits on the consumer's analytic terrain), with the shipped consumer tuning (walk 3 /
// run 6, radius 0.4, half-height 0.9, StepHeight 0.4, GroundedEpsilon 0.3, MaxStepClimbSpeed 3.5). A full sweep of
// arrival phases (startZ 1.00..1.60, 0.01 steps = 61 phases) x walk/run at dt 1/30 covers every sub-tick offset at
// which the footprint can land on the base tread.
//
// Pre-fix (main) these are RED: a grounded-tick support collapse >= 0.1 m fires on ~42/61 walk and ~48/61 run phases
// (worst ~0.30 m, a whole riser), and sustained penetration >= 0.15 m on ~30/61 walk and ~21/61 run. The fix drops a
// radius-less ray fan over the footprint when the sweep misses (WalkableTreadUnderFeet), finds the tread the sweep
// cannot, and seats groundY on it, so the mount stays smooth: no collapse-bob, small penetration, no backslide, and
// the climb reaches the top at any approach speed and phase.
//
// NOTE - one-sided meshes are a SEPARATE, still-open failure mode (documented, not asserted here). A hand-built
// one-sided triangle-mesh staircase (thin riser/tread quads, no solid body) with these same phases STICKS at the base
// on WALK - the capsule mounts one riser then never advances - IDENTICALLY on main and with this fix. That stall is a
// horizontal-advance blockage on the thin one-sided riser, orthogonal to this vertical-support fix (which corrects the
// support HEIGHT, not the forward push), so the fix neither covers nor regresses it. The consumer's TestStaircase is
// convex boxes (this fixture), which the fix fully covers; the one-sided building-proxy stall is left open.
public class ConsumerStairBaseMountTests
{
    const float Dt = 1f / 30f;
    const float Riser = 0.30f, Tread = 0.40f, Burial = 1.5f, HalfW = 1.0f, PlatDepth = 2.0f;
    const int Steps = 15;
    const int Ticks = 160;

    // Consumer tuning (Ruinborne): walk 3, run 6, and the MoveTuning defaults radius 0.4 / half-height 0.9 /
    // StepHeight 0.4 / GroundedEpsilon 0.3 / MaxStepClimbSpeed 3.5 - the same tuning StairRunTangentPacingTests uses.
    static MoveTuning Consumer => MoveTuning.Default with { WalkSpeed = 3f, RunSpeed = 6f };

    // Convex-box staircase mirroring TestStaircase.Generate (mirrored to climb -Z so yaw 0 forward = -Z walks INTO it).
    // Column n spans local Z [n*tread, (n+1)*tread] and local Y [-burial, (n+1)*riser], so its riser front sits at
    // Z=-n*tread and its tread top at Y=(n+1)*riser; the burial matches the consumer's terrain-seated columns and is
    // immaterial to the contact (the capsule never goes below Y=0). A top platform level with the last tread ends the
    // run, exactly as the generator emits it.
    static void AddStairs(IPhysicsWorld world)
    {
        for (int n = 0; n < Steps; n++)
        {
            float treadTop = (n + 1) * Riser;
            float halfH = (treadTop + Burial) * 0.5f;
            float centerZ = -(n * Tread + Tread * 0.5f);
            world.AddStatic(new BoxShape(new Vector3(HalfW, halfH, Tread * 0.5f)),
                Pose.At(new Vector3(0f, treadTop - halfH, centerZ)));
        }
        float platTop = Steps * Riser;
        float platHalfH = (platTop + Burial) * 0.5f;
        float platHalfD = PlatDepth * 0.5f;
        world.AddStatic(new BoxShape(new Vector3(HalfW, platHalfH, platHalfD)),
            Pose.At(new Vector3(0f, platTop - platHalfH, -(Steps * Tread + platHalfD))));
    }

    sealed class Result
    {
        public float MaxY;          // highest capsule-centre Y reached
        public float WorstDrop;     // largest single-tick support DROP while continuously grounded on a step
        public float WorstPen;      // largest ComputePenetration MTV length while on a step
        public float WorstBack;     // most-negative forward (into-the-stairs) advance while on a step
    }

    // Drive a head-on climb from startZ, capturing the mount metrics. A "step tick" = grounded THIS and LAST tick with
    // the PREVIOUS position elevated on a step below the top landing; keying on the previous tick is essential because
    // a collapse lands the capsule back at the flat (a curr-elevated gate would hide the very drop we measure), and it
    // excludes the legitimate fall-and-land after the capsule walks off the top platform.
    static Result Drive(float startZ, bool run)
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        AddStairs(world);
        world.Step(Dt);

        var t = Consumer;
        float halfH = t.CapsuleHalfHeight;
        float topY = Steps * Riser + halfH;
        var state = new MoveState { Position = new Vector3(0f, halfH, startZ), Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, 1f), run, cameraYaw: 0f, jump: false);
        float Ground(float x, float z) => 0f;
        Func<float, float, Vector3> normal = (x, z) => Vector3.UnitY;
        CapsuleShape capsule = CharacterMovement.CapsuleFor(t);

        var r = new Result { MaxY = halfH };
        Vector3 prev = state.Position;
        bool prevGrounded = state.Grounded;
        for (int i = 0; i < Ticks; i++)
        {
            state = CharacterMovement.Step(state, cmd, Dt, Ground, t, normal, world);
            Vector3 p = state.Position;
            bool step = state.Grounded && prevGrounded && prev.Y > halfH + 0.05f && prev.Y < topY - 0.05f;
            if (step)
            {
                r.WorstDrop = MathF.Max(r.WorstDrop, prev.Y - p.Y);
                world.ComputePenetration(capsule, Pose.At(p), out Vector3 mtv);
                r.WorstPen = MathF.Max(r.WorstPen, mtv.Length());
                r.WorstBack = MathF.Min(r.WorstBack, -(p.Z - prev.Z));
            }
            r.MaxY = MathF.Max(r.MaxY, p.Y);
            prev = p;
            prevGrounded = state.Grounded;
        }
        return r;
    }

    // Walk DOWN off the base step: seat the capsule on tread 1 (centre resting on the first tread top) and command
    // movement OFF the front edge (+Z, away from the stairs). Captures the per-tick capsule-centre Y so a descent can
    // be checked for a prompt drop to the flat AND for the absence of a re-seat back onto the tread.
    static List<(float Z, float Y)> DriveDescent(float startZ)
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        AddStairs(world);
        world.Step(Dt);

        var t = Consumer;
        float halfH = t.CapsuleHalfHeight;
        float tread1Y = Riser + halfH;   // capsule centre seated on tread 1 top (0.30 + 0.9)
        var state = new MoveState { Position = new Vector3(0f, tread1Y, startZ), Grounded = true };
        // Forward (0,1) at yaw 0 is -Z INTO the stairs, so (0,-1) walks +Z OFF the front edge, down onto the flat.
        var cmd = new MoveCommand(new Vector2(0f, -1f), run: false, cameraYaw: 0f, jump: false);
        float Ground(float x, float z) => 0f;
        Func<float, float, Vector3> normal = (x, z) => Vector3.UnitY;

        var path = new List<(float Z, float Y)>();
        for (int i = 0; i < 40; i++)
        {
            state = CharacterMovement.Step(state, cmd, Dt, Ground, t, normal, world);
            path.Add((state.Position.Z, state.Position.Y));
        }
        return path;
    }

    // Descent regression for the fan-ring vs sweep-gate window: walking OFF the front of the base step must DROP to the
    // flat and stay there, NOT get re-seated back up onto the tread by the tread-find fan. The fan's outer ring is 0.95 R
    // - deliberately just past the downward sweep's 0.9 R UnderFootprint reach (the ~0.02 m window that lets a partial
    // MOUNT's leading arc catch the tread). A ring widened to >= 1.0 R would reach a full radius back and re-grab the
    // tread being STEPPED OFF, stalling the descent; this pins that it does not. Start well inside tread 1 so a couple of
    // on-tread ticks precede the walk-off.
    [Theory]
    [InlineData(-0.30f)]
    [InlineData(-0.25f)]
    [InlineData(-0.20f)]
    [InlineData(-0.15f)]
    public void WalksDownOffBaseStep_DropsToFlat_NotReseated(float startZ)
    {
        var t = Consumer;
        float halfH = t.CapsuleHalfHeight;
        float radius = CharacterMovement.CapsuleFor(t).Radius;
        float flatY = halfH;                 // capsule centre resting on the flat ground (Y = 0 + halfH)
        float treadLevel = Riser + halfH;    // capsule centre seated on the tread top
        // Tread 1 spans Z in [-Tread, 0]; its front edge is Z=0. The support sweep's UnderFootprint gate holds the tread
        // until the centre is ~0.9 R past the edge, and the 0.95 R fan extends the legit catch to ~0.95 R past it, so once
        // the centre has walked a FULL radius past the front edge the capsule can no longer be legitimately supported by
        // tread 1 and MUST be dropping/dropped. A fan ring widened to >= 1.0 R reaches a full radius back and keeps the
        // capsule re-seated up there past this line - which is exactly the descent-stall this guards.
        float clearedEdge = radius;          // centre Z at/after which tread-1 support is no longer legitimate
        List<(float Z, float Y)> path = DriveDescent(startZ);
        string dump = string.Join(" ", path.ConvertAll(p => $"(z{p.Z:F2},y{p.Y:F2})"));

        // Started on the tread: the capsule sat at the tread top before walking off (else the scenario is void).
        Assert.Contains(path, p => p.Y >= treadLevel - 0.05f);

        // Reaches the flat: the capsule settles on the ground below the step by the end of the walk-off.
        Assert.True(path[^1].Y <= flatY + 0.02f,
            $"descent from startZ={startZ:F2} did not reach the flat: final Y {path[^1].Y:F3}, expected ~{flatY:F3} ({dump}).");

        // The guard: once the centre has cleared the front edge by a full radius, the capsule must be on the flat, NOT
        // still lifted up on the tread it stepped off. A too-wide fan ring (>= 1.0 R) re-grabs the departing tread and
        // holds the capsule up here, stalling the descent; the shipped 0.95 R ring does not.
        foreach ((float z, float y) in path)
            if (z >= clearedEdge)
                Assert.True(y <= flatY + 0.05f,
                    $"descent from startZ={startZ:F2} was RE-SEATED onto the tread at Z={z:F2} (a full radius past the " +
                    $"front edge): Y {y:F3} still near the tread top {treadLevel:F3} - the fan re-grabbed the departing tread ({dump}).");
    }

    // 61 arrival phases (startZ 1.00..1.60, 0.01 steps) x walk/run. The named reference phases (walk 1.13; run 1.34,
    // 1.14, 1.36, 1.16 - the investigation's worst collapse offsets) fall inside this sweep.
    public static IEnumerable<object[]> Phases()
    {
        foreach (bool run in new[] { false, true })
            for (int k = 0; k <= 60; k++)
                yield return new object[] { run, 1.00f + 0.01f * k };
    }

    // The base mount is CLEAN at every arrival phase and speed: no collapse-bob, bounded penetration, no backslide,
    // and it climbs to the top. Pre-fix the collapse-bob and penetration asserts are RED across most phases; the fix
    // makes all four green.
    [Theory]
    [MemberData(nameof(Phases))]
    public void BaseMount_ClimbsCleanly(bool run, float startZ)
    {
        Result r = Drive(startZ, run);
        string tag = $"{(run ? "run" : "walk")} startZ={startZ:F2}";
        float topY = Steps * Riser + MoveTuning.Default.CapsuleHalfHeight;

        // (1) No collapse-bob: no grounded step-tick drops the support half a riser or more. Pre-fix the sweep miss
        //     dropped the capsule a whole riser (~0.30 m) back to the flat.
        Assert.True(r.WorstDrop < 0.1f,
            $"support collapsed {r.WorstDrop:F3} m in one grounded step-tick ({tag}) - the base collapse-bob.");
        // (2) Bounded penetration: the mount does not plow into the risers.
        Assert.True(r.WorstPen < 0.15f,
            $"base mount penetration {r.WorstPen:F3} m ({tag}).");
        // (3) No backslide: forward progress never reverses on a grounded step-tick (the riser-depenetration backslide).
        Assert.True(r.WorstBack > -0.02f,
            $"forward advance went backwards {r.WorstBack:F4} m on a grounded step-tick ({tag}) - the base backslide.");
        // (4) Reaches the top (no stall / deadlock): a stuck base never climbs off the first riser.
        Assert.True(r.MaxY >= topY - 0.3f,
            $"climb did not reach the top ({tag}): peak Y {r.MaxY:F3}, expected ~{topY:F3} - a base stall.");
    }

    // The explicit named reference phases from the investigation (walk 1.13; run 1.34 / 1.14 / 1.36 / 1.16), pinned
    // separately so a regression names the exact worst-collapse arrival offsets.
    [Theory]
    [InlineData(false, 1.13f)]
    [InlineData(true, 1.34f)]
    [InlineData(true, 1.14f)]
    [InlineData(true, 1.36f)]
    [InlineData(true, 1.16f)]
    public void ReferencePhase_MountsCleanly(bool run, float startZ)
    {
        Result r = Drive(startZ, run);
        string tag = $"{(run ? "run" : "walk")} startZ={startZ:F2}";
        float topY = Steps * Riser + MoveTuning.Default.CapsuleHalfHeight;
        Assert.True(r.WorstDrop < 0.1f, $"reference collapse-bob {r.WorstDrop:F3} m ({tag}).");
        Assert.True(r.WorstPen < 0.15f, $"reference penetration {r.WorstPen:F3} m ({tag}).");
        Assert.True(r.WorstBack > -0.02f, $"reference backslide {r.WorstBack:F4} m ({tag}).");
        Assert.True(r.MaxY >= topY - 0.3f, $"reference climb stalled: peak Y {r.MaxY:F3} ({tag}).");
    }
}
