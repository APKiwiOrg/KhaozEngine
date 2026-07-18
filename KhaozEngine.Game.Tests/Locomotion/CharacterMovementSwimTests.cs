using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

/// <summary>
/// Surface-swim v1 (built on the movement-medium seam): enter/exit hysteresis around the chest threshold, buoyancy
/// settle to a resting waterline, suspended gravity/ground-snap, <see cref="MoveTuning.SwimSpeed"/> horizontal travel
/// with the zone scale composing, and the deep-vs-near-shore jump rule. Headless and deterministic (fixed dt, pure
/// provider). The load-bearing invariant, as with wading, is that a null provider is BIT-IDENTICAL to the pre-swim
/// behaviour: a land character never touches the swim path.
/// </summary>
public class CharacterMovementSwimTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    // Half-height 0.5 -> body height 1.0, so a submersion FRACTION equals the raw depth in metres and the thresholds
    // read cleanly against hand-computed numbers (enter 0.65, exit 0.55).
    static readonly MoveTuning Unit = MoveTuning.Default with { CapsuleHalfHeight = 0.5f };

    static MoveCommand Forward => new(new Vector2(0f, 1f), run: false, cameraYaw: 0f);

    // A medium reporting a fixed water surface everywhere (InWater), optionally with a zone scale.
    static Func<float, float, float, MovementMedium> Water(float surfaceY, float zoneScale = 1f)
        => (x, z, feetY) => new MovementMedium(surfaceY, inWater: true, zoneScale);

    // ---- ResolveSwimming: hysteresis around enter (0.65) / exit (0.55) ----

    [Fact]
    public void Not_swimming_below_the_enter_threshold()
    {
        // Feet at 0, surface 0.6 -> depthFraction 0.6 < enter 0.65: a wading character does not begin swimming.
        Assert.False(CharacterMovement.ResolveSwimming(false, new MovementMedium(0.6f, inWater: true), feetY: 0f, Unit));
    }

    [Fact]
    public void Begins_swimming_at_and_above_the_enter_threshold()
    {
        Assert.True(CharacterMovement.ResolveSwimming(false, new MovementMedium(0.65f, inWater: true), feetY: 0f, Unit));
        Assert.True(CharacterMovement.ResolveSwimming(false, new MovementMedium(0.90f, inWater: true), feetY: 0f, Unit));
    }

    [Fact]
    public void Keeps_swimming_in_the_hysteresis_band_below_enter_but_above_exit()
    {
        // depthFraction 0.60 sits between exit (0.55) and enter (0.65): a NON-swimmer stays out, a SWIMMER stays in.
        Assert.False(CharacterMovement.ResolveSwimming(false, new MovementMedium(0.60f, inWater: true), 0f, Unit));
        Assert.True(CharacterMovement.ResolveSwimming(true, new MovementMedium(0.60f, inWater: true), 0f, Unit));
    }

    [Fact]
    public void Exits_swimming_below_the_exit_threshold()
    {
        // depthFraction 0.50 < exit 0.55: even a swimmer drops back to wading.
        Assert.False(CharacterMovement.ResolveSwimming(true, new MovementMedium(0.50f, inWater: true), 0f, Unit));
    }

    [Fact]
    public void Leaving_the_water_always_exits_swim()
    {
        // Out of water: never swimming regardless of the carried flag (and the null-Dry path).
        Assert.False(CharacterMovement.ResolveSwimming(true, new MovementMedium(5f, inWater: false), 0f, Unit));
        Assert.False(CharacterMovement.ResolveSwimming(true, MovementMedium.Dry, 0f, Unit));
    }

    // ---- Hysteresis walking a gentle slope into water: flips exactly once, no flicker ----

    [Fact]
    public void Walking_a_gentle_slope_into_water_flips_swim_exactly_once_no_flicker()
    {
        // A shallowing lakebed: the FLOOR descends gently as the character walks forward (into -Z), so its feet go
        // deeper each step and cross the chest line ONCE. Water surface is flat at y=0. The ground drops 0.02 m per
        // metre of -Z travelled, so the character's feet (on the floor) submerge progressively. We drive Forward and
        // count how many times Swimming changes: hysteresis must make it flip exactly once (never chatter at the line).
        Func<float, float, float> slope = (x, z) => 0.5f + 0.15f * z;   // z goes negative moving forward -> floor drops
        Func<float, float, float, MovementMedium> lake = (x, z, feetY) => new MovementMedium(0f, inWater: feetY < 0f);

        var s = new MoveState { Position = new Vector3(0f, slope(0f, 0f) + Unit.CapsuleHalfHeight, 0f), Grounded = true };
        const float dt = 1f / 30f;
        int flips = 0;
        bool prev = s.Swimming;
        for (int i = 0; i < 400; i++)
        {
            s = CharacterMovement.Step(s, Forward, dt, slope, Unit, null, null, null, lake);
            if (s.Swimming != prev) { flips++; prev = s.Swimming; }
        }
        Assert.Equal(1, flips);          // crossed the chest line once, no boundary flicker
        Assert.True(s.Swimming, "expected to end up swimming in the deep end");
    }

    // ---- Buoyancy settle: converges to the resting waterline, no oscillation blowup, deterministic ----

    [Fact]
    public void Buoyancy_settles_to_the_resting_waterline_without_oscillation()
    {
        // Deep water surface at y=2. Start the capsule well below its target and let it settle in place (no input).
        // Target feet submersion 0.6 of body height (1.0) -> targetFeetY = 2 - 0.6 = 1.4, targetY (centre) = 1.9.
        Func<float, float, float, MovementMedium> deep = Water(2.0f);
        Func<float, float, float> floor = (x, z) => 0f;   // lakebed far below, never clamps
        float targetY = 2.0f - Unit.SwimSurfaceSubmersionFraction * (2f * Unit.CapsuleHalfHeight) + Unit.CapsuleHalfHeight;

        var s = new MoveState { Position = new Vector3(0f, 0.6f, 0f), Swimming = true, Grounded = false };
        const float dt = 1f / 60f;
        float prevErr = MathF.Abs(s.Position.Y - targetY);
        for (int i = 0; i < 600; i++)
        {
            s = CharacterMovement.Step(s, MoveCommand.Idle, dt, floor, Unit, null, null, null, deep);
            float err = MathF.Abs(s.Position.Y - targetY);
            // Monotone (critically damped: no overshoot), with a tiny tolerance for float noise.
            Assert.True(err <= prevErr + 1e-4f, $"settle overshot at tick {i}: err {err} > prevErr {prevErr}");
            prevErr = err;
        }
        Assert.True(MathF.Abs(s.Position.Y - targetY) < 1e-3f, $"did not converge: y {s.Position.Y} target {targetY}");
        Assert.True(s.Swimming);
    }

    [Fact]
    public void Buoyancy_settle_is_deterministic()
    {
        Func<float, float, float, MovementMedium> deep = Water(2.0f);
        var s0 = new MoveState { Position = new Vector3(1f, 0.3f, -2f), VerticalVelocity = -4f, Swimming = true };
        var a = s0; var b = s0;
        for (int i = 0; i < 50; i++)
        {
            a = CharacterMovement.Step(a, Forward, 0.017f, Flat, Unit, null, null, null, deep);
            b = CharacterMovement.Step(b, Forward, 0.017f, Flat, Unit, null, null, null, deep);
        }
        Assert.Equal(a.Position, b.Position);
        Assert.Equal(a.VerticalVelocity, b.VerticalVelocity);
        Assert.Equal(a.Swimming, b.Swimming);
    }

    [Fact]
    public void Buoyancy_never_blows_up_at_a_large_timestep()
    {
        // A huge dt with a stiff spring is exactly where explicit integration explodes; the analytic solution stays
        // finite and does not overshoot past the target.
        var stiff = Unit with { SwimBuoyancyStiffness = 50f };
        Func<float, float, float, MovementMedium> deep = Water(2.0f);
        float targetY = 2.0f - stiff.SwimSurfaceSubmersionFraction * (2f * stiff.CapsuleHalfHeight) + stiff.CapsuleHalfHeight;
        var s = new MoveState { Position = new Vector3(0f, -10f, 0f), Swimming = true };
        s = CharacterMovement.Step(s, MoveCommand.Idle, 1.0f, (x, z) => -1000f, stiff, null, null, null, deep);
        Assert.True(float.IsFinite(s.Position.Y));
        Assert.True(s.Position.Y <= targetY + 1e-3f, $"overshot the target on a huge dt: {s.Position.Y} > {targetY}");
    }

    // ---- Gravity / ground-snap suspended while swimming ----

    [Fact]
    public void Gravity_is_suspended_while_swimming()
    {
        // In deep water with no input, a swimming capsule already at its waterline stays there: it does NOT fall under
        // gravity (a land capsule would accumulate a large negative VerticalVelocity over the same ticks).
        Func<float, float, float, MovementMedium> deep = Water(2.0f);
        float targetY = 2.0f - Unit.SwimSurfaceSubmersionFraction + Unit.CapsuleHalfHeight;   // body height 1.0
        var s = new MoveState { Position = new Vector3(0f, targetY, 0f), Swimming = true };
        for (int i = 0; i < 120; i++) s = CharacterMovement.Step(s, MoveCommand.Idle, 1f / 60f, (x, z) => 0f, Unit, null, null, null, deep);
        Assert.False(s.Grounded);
        Assert.True(MathF.Abs(s.Position.Y - targetY) < 1e-3f, $"buoyant capsule drifted from its waterline: {s.Position.Y}");
        Assert.True(MathF.Abs(s.VerticalVelocity) < 1e-2f, $"expected near-zero settle velocity, got {s.VerticalVelocity}");
    }

    // ---- Swim speed applies, with the zone scale composing ----

    [Fact]
    public void Horizontal_travel_uses_swim_speed()
    {
        Func<float, float, float, MovementMedium> deep = Water(2.0f);
        var s = new MoveState { Position = new Vector3(0f, 1.4f + Unit.CapsuleHalfHeight, 0f), Swimming = true };
        const float dt = 1f / 30f;
        float z0 = s.Position.Z;
        for (int i = 0; i < 30; i++) s = CharacterMovement.Step(s, Forward, dt, (x, z) => 0f, Unit, null, null, null, deep);
        float travelled = MathF.Abs(s.Position.Z - z0);
        float expected = Unit.SwimSpeed * 30f * dt;   // steady swim speed, forward = -Z
        Assert.Equal(expected, travelled, 3);
    }

    [Fact]
    public void Swim_speed_composes_the_zone_scale()
    {
        // A swamp zone (0.5) halves swim speed on top of SwimSpeed. Same as the wade zone-scale composition.
        Func<float, float, float, MovementMedium> swamp = Water(2.0f, zoneScale: 0.5f);
        var s = new MoveState { Position = new Vector3(0f, 1.4f + Unit.CapsuleHalfHeight, 0f), Swimming = true };
        const float dt = 1f / 30f;
        float z0 = s.Position.Z;
        for (int i = 0; i < 30; i++) s = CharacterMovement.Step(s, Forward, dt, (x, z) => 0f, Unit, null, null, null, swamp);
        float travelled = MathF.Abs(s.Position.Z - z0);
        float expected = Unit.SwimSpeed * 0.5f * 30f * dt;
        Assert.Equal(expected, travelled, 3);
    }

    // ---- Jump: ignored in deep water, hop-out in near-shore shallows ----

    [Fact]
    public void Jump_is_ignored_in_deep_water()
    {
        // Deep: surface well above the head, submersion way past the exit band. A jump press does nothing - the
        // character keeps swimming, no upward launch.
        Func<float, float, float, MovementMedium> deep = Water(2.0f);
        var s = new MoveState { Position = new Vector3(0f, 1.4f + Unit.CapsuleHalfHeight, 0f), Swimming = true };
        var jump = new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: true);
        s = CharacterMovement.Step(s, jump, 1f / 60f, (x, z) => 0f, Unit, null, null, null, deep);
        Assert.True(s.Swimming, "a deep-water jump must be ignored, not a hop-out");
        Assert.True(s.VerticalVelocity < Unit.JumpSpeed, "no jump launch in deep water");
    }

    [Fact]
    public void Jump_hops_out_in_near_shore_shallows()
    {
        // Near-shore: the feet sit within the exit band (submersion just at/under exit 0.55). A jump press hops out -
        // it launches the ordinary jump velocity and drops swim, so the next tick is a land/airborne character.
        // Surface at y=0.5, feet at 0 -> submersion fraction 0.5 (< exit 0.55): near-shore.
        Func<float, float, float, MovementMedium> shore = Water(0.5f);
        var s = new MoveState { Position = new Vector3(0f, 0.5f, 0f), Swimming = true };
        var jump = new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f, jump: true);
        Func<float, float, float> ground = (x, z) => -1f;   // ground far below so the floor clamp does not interfere
        s = CharacterMovement.Step(s, jump, 1f / 60f, ground, Unit, null, null, null, shore);
        Assert.False(s.Swimming, "a near-shore jump is a hop-out: swim is dropped");
        Assert.Equal(Unit.JumpSpeed, s.VerticalVelocity, 3);
    }

    // ---- Null provider bit-identity: the swim path never perturbs a land character ----

    [Fact]
    public void Null_provider_never_engages_swim_and_is_bit_identical()
    {
        // A land character stepped with a null provider produces exactly the pre-swim result and never sets Swimming.
        var s = new MoveState { Position = new Vector3(0f, 0.5f, 0f), Grounded = true };
        var cmd = new MoveCommand(new Vector2(1f, 1f), run: true, cameraYaw: 0.4f, jump: false);
        MoveState baseline = CharacterMovement.Step(s, cmd, 1f / 60f, Flat, Unit);
        MoveState withNull = CharacterMovement.Step(s, cmd, 1f / 60f, Flat, Unit, null, null, null, medium: null);
        Assert.Equal(baseline.Position, withNull.Position);
        Assert.Equal(baseline.VerticalVelocity, withNull.VerticalVelocity);
        Assert.Equal(baseline.Grounded, withNull.Grounded);
        Assert.False(withNull.Swimming);
    }
}
