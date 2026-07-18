using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

/// <summary>
/// The movement-medium seam: the optional <c>(x, z, feetY) -> MovementMedium</c> provider threaded into
/// <see cref="CharacterMovement"/> and the depth-ramped wade speed it drives. Headless and deterministic (fixed dt,
/// pure provider). The load-bearing invariant is that a null provider (or a dry sample) is BIT-IDENTICAL to the
/// pre-medium behaviour, so these tests pin the ramp maths against the raw <c>CharacterMovement.Step</c>
/// baseline the ground delegate always produced.
/// </summary>
public class CharacterMovementMediumTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    // Half-height 0.5 -> body height 1.0, so the submersion FRACTION equals the raw depth in metres and the ramp
    // reads cleanly against hand-computed numbers.
    static readonly MoveTuning Unit = MoveTuning.Default with { CapsuleHalfHeight = 0.5f };
    // Wade-only tuning: swim enter/exit pushed above any depth these tests reach, so the vertical Step exercises the
    // WADE ramp in isolation. Surface swim now takes over past chest depth (SwimEnterDepthFraction 0.65, exactly where
    // the wade ramp bottoms out), so a deep-water vertical step would otherwise swim, not wade; that swim path is
    // covered by CharacterMovementSwimTests. The pure WadeSpeedScale helper below is unaffected by swim regardless.
    static readonly MoveTuning WadeOnly = Unit with { SwimEnterDepthFraction = 10f, SwimExitDepthFraction = 9f };

    static MoveCommand Forward => new(new Vector2(0f, 1f), run: false, cameraYaw: 0f);

    // A medium provider that reports a fixed water surface everywhere (InWater), optionally with a zone scale.
    static Func<float, float, float, MovementMedium> Water(float surfaceY, float zoneScale = 1f)
        => (x, z, feetY) => new MovementMedium(surfaceY, inWater: true, zoneScale);

    // Feet sit at pos.Y - halfHeight. With a capsule centre on the ground (Y = groundHeight + halfHeight = 0.5),
    // feetY = 0, so a WaterSurfaceY value IS the submersion depth.
    static Vector3 Standing => new(0f, 0.5f, 0f);   // centre for Flat ground (0) + halfHeight (0.5)

    // ---- WadeSpeedScale helper: hand-computed ramp values (start 0.15, end 0.65, floor 0.45 on a 1.0 body) ----

    [Fact]
    public void Wade_scale_is_one_with_a_null_provider()
    {
        Assert.Equal(1f, CharacterMovement.WadeSpeedScale(0f, 0f, 0f, Unit, medium: null), 6);
    }

    [Fact]
    public void Wade_scale_is_one_out_of_water()
    {
        var dry = new Func<float, float, float, MovementMedium>((x, z, feetY) => new MovementMedium(5f, inWater: false));
        Assert.Equal(1f, CharacterMovement.WadeSpeedScale(0f, 0f, 0f, Unit, dry), 6);
    }

    [Fact]
    public void Wade_scale_is_full_at_and_below_ankle_depth()
    {
        // At the start depth (0.15) exactly, and shallower (0.05), still full speed.
        Assert.Equal(1f, CharacterMovement.WadeSpeedScale(0f, 0f, 0f, Unit, Water(0.15f)), 6);
        Assert.Equal(1f, CharacterMovement.WadeSpeedScale(0f, 0f, 0f, Unit, Water(0.05f)), 6);
    }

    [Fact]
    public void Wade_scale_hits_the_floor_at_and_beyond_chest_depth()
    {
        // At the end depth (0.65) and deeper (1.0) the ramp sits at the floor (0.45).
        Assert.Equal(Unit.WadeMinSpeedScale, CharacterMovement.WadeSpeedScale(0f, 0f, 0f, Unit, Water(0.65f)), 6);
        Assert.Equal(Unit.WadeMinSpeedScale, CharacterMovement.WadeSpeedScale(0f, 0f, 0f, Unit, Water(1.0f)), 6);
    }

    [Fact]
    public void Wade_scale_lerps_linearly_between_ankle_and_chest()
    {
        // Midpoint depth 0.40: tNorm = (0.40 - 0.15) / (0.65 - 0.15) = 0.5, ramp = 1 + (0.45 - 1) * 0.5 = 0.725.
        Assert.Equal(0.725f, CharacterMovement.WadeSpeedScale(0f, 0f, 0f, Unit, Water(0.40f)), 5);
        // Quarter of the way in, depth 0.275: tNorm = 0.25, ramp = 1 + (-0.55) * 0.25 = 0.8625.
        Assert.Equal(0.8625f, CharacterMovement.WadeSpeedScale(0f, 0f, 0f, Unit, Water(0.275f)), 5);
    }

    [Fact]
    public void Provider_wade_scale_composes_as_an_extra_multiplier()
    {
        // Midpoint ramp 0.725 times a zone scale of 0.5 = 0.3625 (a swamp dragging on top of the depth ramp).
        Assert.Equal(0.725f * 0.5f, CharacterMovement.WadeSpeedScale(0f, 0f, 0f, Unit, Water(0.40f, zoneScale: 0.5f)), 5);
        // The zone scale alone (full-speed depth, ankle): 1.0 * 0.6 = 0.6.
        Assert.Equal(0.6f, CharacterMovement.WadeSpeedScale(0f, 0f, 0f, Unit, Water(0.10f, zoneScale: 0.6f)), 5);
    }

    [Fact]
    public void Negative_zone_scale_can_never_reverse_travel()
    {
        // A hostile / mis-set negative zone scale is clamped to a full stop, not a backwards drift.
        Assert.Equal(0f, CharacterMovement.WadeSpeedScale(0f, 0f, 0f, Unit, Water(0.10f, zoneScale: -2f)), 6);
    }

    [Fact]
    public void Zone_scale_above_one_is_uncapped()
    {
        // A zone scale > 1 (an aiding current, allowed) lifts the result PAST 1 - the ramp is only floored at >= 0,
        // never ceiling-capped. At ankle depth (ramp 1) a zone scale of 1.5 yields 1.5, not a clamped 1.
        Assert.Equal(1.5f, CharacterMovement.WadeSpeedScale(0f, 0f, 0f, Unit, Water(0.10f, zoneScale: 1.5f)), 5);
        // And composed with a mid-ramp depth (0.725 at depth 0.40) times 2.0 = 1.45, still uncapped.
        Assert.Equal(0.725f * 2.0f, CharacterMovement.WadeSpeedScale(0f, 0f, 0f, Unit, Water(0.40f, zoneScale: 2.0f)), 5);
    }

    // ---- Null provider = bit-identical existing behaviour (both Step overloads) ----

    [Fact]
    public void Horizontal_step_null_provider_is_bit_identical_to_the_no_medium_overload()
    {
        var cmd = new MoveCommand(new Vector2(1f, 1f), run: true, cameraYaw: 0.7f);
        Vector3 baseline = CharacterMovement.Step(Standing, cmd, 0.123f, Flat, Unit);           // pre-medium overload
        Vector3 withNull = CharacterMovement.Step(Standing, cmd, 0.123f, Flat, Unit, null, medium: null);
        Assert.Equal(baseline, withNull);   // exact bit equality: the medium path must not perturb the dry step
    }

    [Fact]
    public void Vertical_step_null_provider_is_bit_identical_to_the_no_medium_overload()
    {
        var s = new MoveState { Position = Standing, Grounded = true };
        var cmd = new MoveCommand(new Vector2(0f, 1f), run: true, cameraYaw: 0f);
        MoveState baseline = CharacterMovement.Step(s, cmd, 1f / 60f, Flat, Unit);
        MoveState withNull = CharacterMovement.Step(s, cmd, 1f / 60f, Flat, Unit, null, null, null, medium: null);
        Assert.Equal(baseline.Position, withNull.Position);
        Assert.Equal(baseline.VerticalVelocity, withNull.VerticalVelocity);
        Assert.Equal(baseline.Grounded, withNull.Grounded);
    }

    [Fact]
    public void Dry_water_sample_is_bit_identical_to_a_null_provider()
    {
        // A provider that reports "not in water" everywhere must produce exactly the dry step (the InWater gate).
        var dry = new Func<float, float, float, MovementMedium>((x, z, feetY) => MovementMedium.Dry);
        var cmd = new MoveCommand(new Vector2(1f, 0f), run: false, cameraYaw: 0.2f);
        Vector3 baseline = CharacterMovement.Step(Standing, cmd, 0.1f, Flat, Unit);
        Vector3 withDry = CharacterMovement.Step(Standing, cmd, 0.1f, Flat, Unit, null, dry);
        Assert.Equal(baseline, withDry);
    }

    // ---- The scale actually reaches the produced displacement ----

    [Fact]
    public void Wading_scales_the_horizontal_displacement_by_the_ramp()
    {
        // Chest-deep water (surface 1.0 >= end depth 0.65) -> floor scale 0.45 on the walk step.
        Vector3 dry = CharacterMovement.Step(Standing, Forward, 1f, Flat, Unit);
        Vector3 wet = CharacterMovement.Step(Standing, Forward, 1f, Flat, Unit, null, Water(1.0f));
        float dryDist = MathF.Abs(dry.Z - Standing.Z);
        float wetDist = MathF.Abs(wet.Z - Standing.Z);
        Assert.Equal(dryDist * Unit.WadeMinSpeedScale, wetDist, 5);
    }

    [Fact]
    public void Wading_scales_the_vertical_step_horizontal_by_the_ramp()
    {
        var s = new MoveState { Position = Standing, Grounded = true };
        MoveState dry = CharacterMovement.Step(s, Forward, 1f, Flat, Unit);
        MoveState wet = CharacterMovement.Step(s, Forward, 1f, Flat, Unit, null, null, null, Water(0.40f));  // ramp 0.725
        float dryDist = MathF.Abs(dry.Position.Z - Standing.Z);
        float wetDist = MathF.Abs(wet.Position.Z - Standing.Z);
        Assert.Equal(dryDist * 0.725f, wetDist, 5);
    }

    // ---- Determinism ----

    [Fact]
    public void Wade_step_is_deterministic_same_inputs_same_output()
    {
        var s = new MoveState { Position = Standing, Grounded = true };
        var cmd = new MoveCommand(new Vector2(1f, 1f), run: true, cameraYaw: 0.9f);
        MoveState a = CharacterMovement.Step(s, cmd, 0.117f, Flat, Unit, null, null, null, Water(0.5f, 0.8f));
        MoveState b = CharacterMovement.Step(s, cmd, 0.117f, Flat, Unit, null, null, null, Water(0.5f, 0.8f));
        Assert.Equal(a.Position, b.Position);
        Assert.Equal(a.VerticalVelocity, b.VerticalVelocity);
    }

    [Fact]
    public void Multi_tick_wade_matches_a_hand_computed_slowed_walk()
    {
        // Ten ticks of chest-deep wading forward: total distance = walkSpeed * floorScale * total-time, exactly.
        var s = new MoveState { Position = Standing, Grounded = true };
        Func<float, float, float, MovementMedium> deep = Water(1.0f);   // >= chest depth -> floor 0.45
        const float dt = 1f / 30f;
        // WadeOnly: keep this a pure wade test (past chest the vertical step would surface-swim, covered elsewhere).
        for (int i = 0; i < 10; i++) s = CharacterMovement.Step(s, Forward, dt, Flat, WadeOnly, null, null, null, deep);
        float expected = -WadeOnly.WalkSpeed * WadeOnly.WadeMinSpeedScale * 10f * dt;  // forward = -Z
        Assert.Equal(expected, s.Position.Z, 4);
    }
}
