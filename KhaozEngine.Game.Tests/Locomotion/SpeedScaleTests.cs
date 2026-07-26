using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

/// <summary>
/// The per-entity horizontal speed multiplier (<see cref="MoveState.SpeedScale"/>): haste, slow, root. The engine
/// owns the scale and its plumbing, never the buff, so everything here is about the multiplier composing correctly
/// into the ONE speed product and surviving the step unchanged. The load-bearing invariant is the same one wading
/// and swimming each had to earn: an unmodified character is BIT-IDENTICAL to the pre-feature behaviour, which
/// matters more here than anywhere else because every player carries this field on every tick.
/// </summary>
public class SpeedScaleTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    static readonly MoveTuning Tuning = MoveTuning.Default;
    const float Dt = 1f / 30f;

    static MoveCommand Forward => new(new Vector2(0f, 1f), run: false, cameraYaw: 0f);
    static MoveState Grounded(float scale = 1f) =>
        new() { Position = new Vector3(0f, Tuning.CapsuleHalfHeight, 0f), Grounded = true, SpeedScale = scale };

    // Travel distance from the origin after n forward ticks at the given scale.
    static float WalkedZ(float scale, int ticks = 10, MoveCommand? cmd = null)
    {
        MoveState s = Grounded(scale);
        for (int i = 0; i < ticks; i++) s = CharacterMovement.Step(s, cmd ?? Forward, Dt, Flat, Tuning);
        return MathF.Abs(s.Position.Z);
    }

    // ---- The default: unmodified, exactly, with no way to construct a frozen character by accident ----

    [Fact]
    public void Default_state_is_exactly_unmodified()
    {
        // Not "about 1": exactly 1. The scale multiplies the position delta on every tick of every entity, so a
        // default of 0.999 would be a silent global speed nerf and a default of 0 would be universal paralysis.
        Assert.Equal(1f, default(MoveState).SpeedScale);
        Assert.Equal(1f, new MoveState { Position = Vector3.One, Grounded = true }.SpeedScale);
    }

    [Fact]
    public void Setting_one_is_bit_identical_to_leaving_it_alone()
    {
        // The composition is `... * SpeedScale`, and multiplying by exactly 1.0f is the IEEE-754 identity, so an
        // unmodified character walks the pre-feature path bit-for-bit rather than approximately.
        MoveState untouched = Grounded();
        MoveState explicitOne = Grounded(1f);
        for (int i = 0; i < 20; i++)
        {
            untouched = CharacterMovement.Step(untouched, Forward, Dt, Flat, Tuning);
            explicitOne = CharacterMovement.Step(explicitOne, Forward, Dt, Flat, Tuning);
        }
        Assert.Equal(untouched.Position, explicitOne.Position);
        Assert.Equal(untouched.VerticalVelocity, explicitOne.VerticalVelocity);
    }

    [Fact]
    public void Negative_scales_clamp_to_a_standstill_rather_than_reversing()
    {
        // A negative multiplier would drive the capsule backwards against its own command, which is never what a
        // movement modifier means. The setter clamps, so even a mis-set game value can only ever stop you.
        Assert.Equal(0f, new MoveState { SpeedScale = -3f }.SpeedScale);
        Assert.Equal(0f, WalkedZ(-3f), 6);
    }

    // ---- Scaling the ground path ----

    [Fact]
    public void Haste_scales_travel_distance_proportionally()
    {
        float baseline = WalkedZ(1f);
        // Powers of two are exact under IEEE-754 scaling, so this is an equality, not an approximation.
        Assert.Equal(baseline * 2f, WalkedZ(2f));
        Assert.Equal(baseline * 0.5f, WalkedZ(0.5f));
        Assert.Equal(baseline * 5f, WalkedZ(5f), 4);
    }

    [Fact]
    public void A_root_stops_horizontal_travel_entirely()
    {
        // Exactly 0, not "very slow": a rooted player who drifts a centimetre a second is a bug report.
        Assert.Equal(0f, WalkedZ(0f));
    }

    [Fact]
    public void Scale_survives_the_step_unchanged()
    {
        // It is a movement INPUT. The step reads it and carries it, nothing in the sim derives or decays it, so a
        // buff lasts exactly as long as the game says it does.
        MoveState s = Grounded(3f);
        for (int i = 0; i < 50; i++) s = CharacterMovement.Step(s, Forward, Dt, Flat, Tuning);
        Assert.Equal(3f, s.SpeedScale);
    }

    [Fact]
    public void Scale_survives_a_jump_and_the_landing()
    {
        MoveState s = Grounded(2f);
        s = CharacterMovement.Step(s, new MoveCommand(new Vector2(0f, 1f), false, 0f, jump: true), Dt, Flat, Tuning);
        Assert.False(s.Grounded);
        for (int i = 0; i < 120 && !s.Grounded; i++) s = CharacterMovement.Step(s, Forward, Dt, Flat, Tuning);
        Assert.True(s.Grounded);
        Assert.Equal(2f, s.SpeedScale);
    }

    // ---- Composition with the other two speed terms ----

    [Fact]
    public void Scale_composes_with_air_control_rather_than_replacing_it()
    {
        // A deliberate feel decision, not an implementation accident: composing into the existing product means a
        // hasted player who jumps travels correspondingly further horizontally. Jump HEIGHT is untouched.
        var tuning = Tuning with { AirControl = 0.5f };
        MoveState Airborne(float scale) => new()
        {
            Position = new Vector3(0f, 20f, 0f), Grounded = false, SpeedScale = scale,
        };
        MoveState hasted = CharacterMovement.Step(Airborne(4f), Forward, Dt, Flat, tuning);
        MoveState unmodified = CharacterMovement.Step(Airborne(1f), Forward, Dt, Flat, tuning);

        Assert.Equal(MathF.Abs(unmodified.Position.Z) * 4f, MathF.Abs(hasted.Position.Z), 6);
        Assert.Equal(unmodified.Position.Y, hasted.Position.Y);   // vertical is untouched by a horizontal scale
    }

    [Fact]
    public void Scale_composes_with_the_wade_scale()
    {
        // Knee-deep water halves the pace, and a haste multiplies whatever is left rather than cancelling the wade.
        Func<float, float, float, MovementMedium> water = (x, z, feetY) => new MovementMedium(0.6f, inWater: true);
        var tuning = Tuning with { CapsuleHalfHeight = 0.5f, SwimEnterDepthFraction = 5f };   // never swims here
        MoveState Wading(float scale) => new()
        {
            Position = new Vector3(0f, tuning.CapsuleHalfHeight, 0f), Grounded = true, SpeedScale = scale,
        };
        MoveState slow = CharacterMovement.Step(Wading(1f), Forward, Dt, Flat, tuning, null, null, null, water);
        MoveState fast = CharacterMovement.Step(Wading(2f), Forward, Dt, Flat, tuning, null, null, null, water);

        float waded = MathF.Abs(slow.Position.Z);
        Assert.True(waded < Tuning.WalkSpeed * Dt, "the wade penalty should still apply");
        Assert.Equal(waded * 2f, MathF.Abs(fast.Position.Z));
    }

    [Fact]
    public void Scale_applies_while_swimming()
    {
        // Deliberate: a player who dives mid-boost keeps it. Losing the buff at the waterline would read as a bug.
        var tuning = Tuning with { CapsuleHalfHeight = 0.5f };
        Func<float, float, float, MovementMedium> deep = (x, z, feetY) => new MovementMedium(5f, inWater: true);
        MoveState Swimmer(float scale) => new()
        {
            Position = new Vector3(0f, 4.5f, 0f), Swimming = true, SpeedScale = scale,
        };
        MoveState plain = CharacterMovement.Step(Swimmer(1f), Forward, Dt, Flat, tuning, null, null, null, deep);
        MoveState hasted = CharacterMovement.Step(Swimmer(2f), Forward, Dt, Flat, tuning, null, null, null, deep);

        Assert.True(plain.Swimming);
        Assert.Equal(tuning.SwimSpeed * Dt, MathF.Abs(plain.Position.Z), 6);
        Assert.Equal(MathF.Abs(plain.Position.Z) * 2f, MathF.Abs(hasted.Position.Z));
        Assert.Equal(2f, hasted.SpeedScale);   // carried through the swim branch too
    }

    // ---- The one thing nothing in the engine had been exercised at ----

    [Fact]
    public void A_five_times_run_does_not_tunnel_a_thin_wall()
    {
        // 5x RunSpeed is 60 m/s, ~2 m per 30 Hz tick against a 0.4 m capsule radius and a zero-thickness quad. The
        // swept collide-and-slide substeps at half the radius so it should hold, but "should" was an assumption
        // until this ran: no part of the engine had been driven at boosted speed before.
        using IPhysicsWorld world = new BepuPhysicsWorld();
        var v = new[]
        {
            new Vector3(-20f, 0f, 8f), new Vector3(20f, 0f, 8f),
            new Vector3(20f, 4f, 8f), new Vector3(-20f, 4f, 8f),
        };
        world.AddStatic(new TriangleMeshShape(v, new[] { 0, 2, 1, 0, 3, 2 }), Pose.At(Vector3.Zero));
        world.Step(1f / 60f);

        MoveState s = Grounded(5f);
        var run = new MoveCommand(new Vector2(0f, -1f), run: true, cameraYaw: 0f);   // straight at +Z
        for (int i = 0; i < 120; i++)
            s = CharacterMovement.Step(s, run, Dt, Flat, Tuning, groundNormal: null, world: world);

        Assert.True(s.Position.Z < 8f - Tuning.CapsuleRadius + 0.05f,
            $"a 5x run tunnelled the wall, z={s.Position.Z}");
    }
}
