using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

/// <summary>
/// Airborne horizontal momentum (<see cref="MoveTuning.AirMomentum"/>): the carried
/// <see cref="MoveState.HorizontalVelocity"/>, the air-control steering blend, the
/// <see cref="MoveTuning.AirBrakeAccel"/> bleed, and the collision clip that decides what survives into the carry.
/// The single most important fixture here is the DEFAULT-UNCHANGED proof: the knob is off by default, every game on
/// the stack inherits that default, and the fleet has been burned once already by an engine bump silently retuning
/// inherited movement feel with a green build. So the airborne advance at the default is asserted against the exact
/// pre-momentum closed form, tick by tick, rather than against a tolerance that would swallow a real drift.
/// </summary>
public class AirMomentumTests
{
    static readonly Func<float, float, float> Flat = (x, z) => 0f;
    static readonly Func<float, float, float> FarBelow = (x, z) => -1000f;   // so a flight never lands
    // Half-height 0 makes groundY == groundHeight, so the horizontal numbers read against hand-computed values.
    static readonly MoveTuning Base = MoveTuning.Default with { CapsuleHalfHeight = 0f };
    const float Dt = 1f / 60f;

    // Camera yaw 0: forward is -Z, right is +X. So Forward travels -Z, Right travels +X, Backward travels +Z.
    static MoveCommand Forward => new(new Vector2(0f, 1f), run: false, cameraYaw: 0f);
    static MoveCommand Backward => new(new Vector2(0f, -1f), run: false, cameraYaw: 0f);
    static MoveCommand Right => new(new Vector2(1f, 0f), run: false, cameraYaw: 0f);
    static MoveCommand RunForward => new(new Vector2(0f, 1f), run: true, cameraYaw: 0f);
    static MoveCommand Idle => new(Vector2.Zero, run: false, cameraYaw: 0f);
    static MoveCommand JumpForward => new(new Vector2(0f, 1f), run: false, cameraYaw: 0f, jump: true);
    static MoveCommand JumpStill => new(Vector2.Zero, run: false, cameraYaw: 0f, jump: true);

    static MoveState Airborne(Vector2 carried = default, float scale = 1f) => new()
    {
        Position = new Vector3(0f, 50f, 0f),
        Grounded = false,
        TimeSinceGrounded = 1f,   // well past coyote, so nothing here can accidentally jump
        HorizontalVelocity = carried,
        SpeedScale = scale,
    };

    static MoveState OnGround(float scale = 1f) =>
        new() { Position = Vector3.Zero, Grounded = true, SpeedScale = scale };

    // The pre-momentum closed form for one airborne Forward tick: moveDir * (walk * AirControl * SpeedScale) * dt,
    // written in the same association order the step evaluates it in so the comparison can be an exact equality.
    static float PreMomentumDeltaZ(in MoveTuning t, float scale)
    {
        float speed = t.WalkSpeed * (t.AirControl * scale);
        return (-1f * speed) * Dt;
    }

    // ---- Acceptance 3: every existing game's jump is unchanged at the default ----

    [Theory]
    [InlineData(1f)]
    [InlineData(0.5f)]
    public void At_the_default_a_mid_air_speed_scale_change_still_collapses_the_arc_exactly(float airControl)
    {
        // The old model recomputes the horizontal from the command every tick, so a buff expiring mid-flight drops
        // the arc on that very tick. That is the behaviour momentum exists to fix, and it is also the behaviour that
        // must survive untouched while the knob is off: this asserts the exact closed form, not that it roughly fell.
        MoveTuning t = Base with { AirControl = airControl };
        MoveState s = Airborne(scale: 5f);
        float expectedZ = 0f;

        for (int i = 0; i < 5; i++)
        {
            s = CharacterMovement.Step(s, Forward, Dt, FarBelow, t);
            expectedZ += PreMomentumDeltaZ(t, 5f);
            Assert.Equal(expectedZ, s.Position.Z);
        }

        s.SpeedScale = 0.5f;   // the buff expires mid-flight
        for (int i = 0; i < 5; i++)
        {
            s = CharacterMovement.Step(s, Forward, Dt, FarBelow, t);
            expectedZ += PreMomentumDeltaZ(t, 0.5f);
            Assert.Equal(expectedZ, s.Position.Z);
        }
        Assert.False(s.Grounded);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(0.5f)]
    public void At_the_default_releasing_input_mid_air_still_stops_horizontal_travel_dead(float airControl)
    {
        MoveTuning t = Base with { AirControl = airControl };
        MoveState s = Airborne();
        float expectedZ = 0f;

        for (int i = 0; i < 5; i++)
        {
            s = CharacterMovement.Step(s, Forward, Dt, FarBelow, t);
            expectedZ += PreMomentumDeltaZ(t, 1f);
            Assert.Equal(expectedZ, s.Position.Z);
        }

        // Released: the pre-momentum step advances nothing at all, so the position is bit-for-bit frozen.
        for (int i = 0; i < 5; i++)
        {
            s = CharacterMovement.Step(s, Idle, Dt, FarBelow, t);
            Assert.Equal(expectedZ, s.Position.Z);
            Assert.Equal(0f, s.Position.X);
        }
    }

    [Fact]
    public void At_the_default_the_commanded_velocity_is_exactly_the_command_direction_times_the_speed()
    {
        // The anomaly check pairs this export with the command's own direction, so at the default the vector and the
        // scalar it replaced must agree exactly or the check silently changes meaning for every shipped game.
        MoveState after = CharacterMovement.Step(Airborne(), Forward, Dt, FarBelow, Base);
        Assert.Equal(new Vector2(0f, -1f) * after.CommandedSpeed, after.CommandedVelocity);
        Assert.Equal(Base.WalkSpeed, after.CommandedSpeed, 4);
    }

    [Fact]
    public void Grounded_motion_is_untouched_by_the_momentum_knob()
    {
        // Momentum is airborne-only. Grounded travel stays instant-to-target, so turning the knob on must not move a
        // walking character by a single bit.
        MoveState off = OnGround(scale: 3f);
        MoveState on = OnGround(scale: 3f);
        MoveTuning t = Base with { AirMomentum = true };
        for (int i = 0; i < 30; i++)
        {
            off = CharacterMovement.Step(off, Forward, Dt, Flat, Base);
            on = CharacterMovement.Step(on, Forward, Dt, Flat, t);
        }
        Assert.Equal(off.Position, on.Position);
        Assert.Equal(off.HorizontalVelocity, on.HorizontalVelocity);
    }

    // ---- Acceptance 1: an arc launched at S travels at S ----

    [Fact]
    public void A_hasted_jump_keeps_its_takeoff_speed_when_the_buff_expires_mid_air()
    {
        // Takeoff needs no special case: the jump fires at the END of the step, so the tick that leaves the ground
        // computed its horizontal as GROUNDED (full speed, no air-control term) and its carried velocity is already
        // the takeoff speed. A jump at 30 therefore starts its arc at 30 by construction.
        MoveTuning t = Base with { AirMomentum = true };
        MoveState s = CharacterMovement.Step(OnGround(scale: 5f), JumpForward, Dt, Flat, t);
        Assert.False(s.Grounded);
        Assert.Equal(30f, s.HorizontalVelocity.Length(), 3);   // walk 6 * scale 5

        s.SpeedScale = 0.2f;   // the buff expires: the command underneath is now 1.2 m/s
        for (int i = 0; i < 20; i++) s = CharacterMovement.Step(s, Forward, Dt, Flat, t);

        Assert.False(s.Grounded);
        Assert.Equal(30f, s.HorizontalVelocity.Length(), 2);
        Assert.True(s.HorizontalVelocity.Y < -29f, $"the arc must still point along the command: {s.HorizontalVelocity}");
    }

    [Fact]
    public void Releasing_input_mid_air_holds_both_speed_and_direction()
    {
        MoveTuning t = Base with { AirMomentum = true };
        MoveState s = Airborne(new Vector2(12f, -16f));   // 20 m/s, off-axis so a direction bug shows up
        for (int i = 0; i < 20; i++) s = CharacterMovement.Step(s, Idle, Dt, FarBelow, t);

        Assert.Equal(12f, s.HorizontalVelocity.X, 2);
        Assert.Equal(-16f, s.HorizontalVelocity.Y, 2);
        Assert.Equal(12f * 20 * Dt, s.Position.X, 2);
        Assert.Equal(-16f * 20 * Dt, s.Position.Z, 2);
    }

    [Fact]
    public void A_standing_jump_then_a_direction_accelerates_from_zero_to_the_commanded_speed()
    {
        MoveTuning full = Base with { AirMomentum = true };
        MoveState s = CharacterMovement.Step(OnGround(), JumpStill, Dt, Flat, full);
        Assert.False(s.Grounded);
        Assert.Equal(Vector2.Zero, s.HorizontalVelocity);   // straight up carries nothing

        // At full air control the command has full authority, so it reaches walk speed on the first tick it is held.
        MoveState instant = CharacterMovement.Step(s, Forward, Dt, FarBelow, full);
        Assert.Equal(full.WalkSpeed, instant.HorizontalVelocity.Length(), 3);

        // At half authority the same press ramps: half the gap closes per tick (3, 4.5, 5.25 ...) toward walk speed.
        MoveTuning half = full with { AirControl = 0.5f };
        MoveState h = CharacterMovement.Step(s, Forward, Dt, FarBelow, half);
        Assert.Equal(3f, h.HorizontalVelocity.Length(), 3);
        h = CharacterMovement.Step(h, Forward, Dt, FarBelow, half);
        Assert.Equal(4.5f, h.HorizontalVelocity.Length(), 3);
        for (int i = 0; i < 20; i++) h = CharacterMovement.Step(h, Forward, Dt, FarBelow, half);
        Assert.Equal(half.WalkSpeed, h.HorizontalVelocity.Length(), 3);
    }

    // ---- What AirControl means once momentum is on ----

    [Fact]
    public void Air_control_zero_is_a_true_ballistic_arc_that_ignores_input()
    {
        // The reading this knob gains under momentum. The old model froze the horizontal at 0, and now the arc flies
        // out on its own while the hardest possible reverse command cannot touch it.
        MoveTuning t = Base with { AirMomentum = true, AirControl = 0f };
        MoveState s = Airborne(new Vector2(4f, -3f));   // 5 m/s, off-axis
        for (int i = 0; i < 20; i++) s = CharacterMovement.Step(s, Backward, Dt, FarBelow, t);

        Assert.Equal(4f, s.HorizontalVelocity.X, 3);
        Assert.Equal(-3f, s.HorizontalVelocity.Y, 3);
        Assert.Equal(4f * 20 * Dt, s.Position.X, 3);
        Assert.Equal(-3f * 20 * Dt, s.Position.Z, 3);
    }

    [Fact]
    public void Air_control_half_bends_the_arc_toward_the_input_while_conserving_its_speed()
    {
        // Partial authority steers the DIRECTION over several ticks and never the speed: a 30 m/s arc turned toward a
        // 6 m/s command is still a 30 m/s arc, which is the whole point of splitting steering from scaling.
        MoveTuning t = Base with { AirMomentum = true, AirControl = 0.5f };
        MoveState s = Airborne(new Vector2(0f, -30f));   // flying -Z at 30, commanded +X

        for (int i = 0; i < 12; i++)
        {
            MoveState next = CharacterMovement.Step(s, Right, Dt, FarBelow, t);
            Assert.Equal(30f, next.HorizontalVelocity.Length(), 2);
            Assert.True(next.HorizontalVelocity.X > s.HorizontalVelocity.X,
                $"tick {i}: the arc should keep bending toward the command, {s.HorizontalVelocity} -> {next.HorizontalVelocity}");
            s = next;
        }
        Assert.True(s.HorizontalVelocity.X > 25f, $"12 ticks should be most of the way around: {s.HorizontalVelocity}");
    }

    // ---- The brake ----

    [Fact]
    public void Air_brake_bleeds_the_conserved_speed_to_the_commanded_one_and_stops_there()
    {
        // 24 m/s^2 at 60 Hz is 0.4 m/s per tick, so a 30 m/s arc under a 6 m/s command reaches the floor in 60 ticks
        // and then holds. Never below: undershooting the command would read as an invisible slow.
        MoveTuning t = Base with { AirMomentum = true, AirBrakeAccel = 24f };
        MoveState s = Airborne(new Vector2(0f, -30f));

        for (int i = 0; i < 10; i++) s = CharacterMovement.Step(s, Forward, Dt, FarBelow, t);
        Assert.Equal(26f, s.HorizontalVelocity.Length(), 2);   // exactly the configured rate, not merely "slower"

        float prev = s.HorizontalVelocity.Length();
        for (int i = 0; i < 200; i++)
        {
            s = CharacterMovement.Step(s, Forward, Dt, FarBelow, t);
            float now = s.HorizontalVelocity.Length();
            Assert.True(now <= prev + 1e-3f, $"tick {i}: braking must never speed the arc up ({prev} -> {now})");
            Assert.True(now >= t.WalkSpeed - 1e-3f, $"tick {i}: the bleed must floor at the commanded speed, got {now}");
            prev = now;
        }
        Assert.Equal(t.WalkSpeed, prev, 3);
    }

    [Fact]
    public void Air_brake_never_accelerates_a_slower_arc_toward_a_faster_command()
    {
        // The bleed is ONE-DIRECTIONAL by definition: it can only bring a fast arc down toward the command, never
        // push a slow one up. Accelerating is the steer blend's job alone, and with no steering authority at all
        // there is nothing to accelerate with. Otherwise a snare that happened to set a brake would speed a
        // ballistic character up, which is the opposite of what a snare means.
        MoveTuning t = Base with { AirMomentum = true, AirControl = 0f, AirBrakeAccel = 24f };
        MoveState s = Airborne(new Vector2(0f, -2f));   // a 2 m/s arc under a 12 m/s run command
        for (int i = 0; i < 30; i++) s = CharacterMovement.Step(s, RunForward, Dt, FarBelow, t);
        Assert.Equal(2f, s.HorizontalVelocity.Length(), 4);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(5000f)]
    [InlineData(12345.6f)]
    public void A_long_free_flight_conserves_its_speed_at_any_distance_from_the_origin(float origin)
    {
        // A free-flight tick was denied nothing, so its carry must come back out exactly as it went in. Re-deriving
        // it from the committed position is what broke that: float rounding makes the measured value straddle the
        // intended one, and the clamp keeps every low reading while discarding every high one, so the speed can only
        // ratchet DOWN and never back up. It is direction-dependent and position-dependent rather than uniform (an
        // axis-aligned round arc lands on float grid points and holds, an off-axis one at overworld range does not),
        // which is exactly why it needs pinning at several distances rather than reasoning about once.
        MoveTuning t = Base with { AirMomentum = true, Gravity = 0f };   // gravity off: this is about the horizontal
        var launch = new Vector2(18.7f, -23.4f);   // off-axis and non-round, so nothing lands on a convenient grid
        MoveState s = new()
        {
            Position = new Vector3(origin, 50f, origin),
            Grounded = false, TimeSinceGrounded = 1f,
            HorizontalVelocity = launch,
        };

        for (int i = 0; i < 600; i++) s = CharacterMovement.Step(s, Idle, Dt, FarBelow, t);   // ten seconds of flight

        Assert.Equal(launch.Length(), s.HorizontalVelocity.Length(), 4);
    }

    [Fact]
    public void The_default_brake_of_zero_conserves_the_arc_indefinitely()
    {
        MoveTuning t = Base with { AirMomentum = true };
        Assert.Equal(0f, t.AirBrakeAccel);
        MoveState s = Airborne(new Vector2(0f, -30f));
        for (int i = 0; i < 120; i++) s = CharacterMovement.Step(s, Forward, Dt, FarBelow, t);
        Assert.Equal(30f, s.HorizontalVelocity.Length(), 1);
    }

    // ---- The collision clip: never injects, only sheds ----

    [Fact]
    public void A_wall_clips_the_carried_velocity_instead_of_carrying_it_through()
    {
        using IPhysicsWorld world = new BepuPhysicsWorld();
        var v = new[]
        {
            new Vector3(-20f, -10f, 8f), new Vector3(20f, -10f, 8f),
            new Vector3(20f, 20f, 8f), new Vector3(-20f, 20f, 8f),
        };
        world.AddStatic(new TriangleMeshShape(v, new[] { 0, 2, 1, 0, 3, 2 }), Pose.At(Vector3.Zero));
        world.Step(1f / 60f);

        // Gravity off so this is purely about the horizontal carry, with a real capsule against real geometry.
        MoveTuning t = MoveTuning.Default with { AirMomentum = true, Gravity = 0f };
        MoveState s = new()
        {
            Position = new Vector3(0f, 3f, 0f), Grounded = false, TimeSinceGrounded = 1f,
            HorizontalVelocity = new Vector2(0f, 20f),   // flying at the wall at 20, commanded at walk 6
        };

        // A free-flight tick is untouched: the capsule reached exactly where the velocity aimed, so the projection
        // returns the whole magnitude and the arc is carried at 20 rather than collapsing to the 6 being commanded.
        MoveState free = CharacterMovement.Step(s, Backward, Dt, FarBelow, t, null, world);
        Assert.Equal(20f, free.HorizontalVelocity.Length(), 2);
        Assert.Equal(20f * Dt, free.Position.Z, 3);

        s = free;
        for (int i = 0; i < 60; i++) s = CharacterMovement.Step(s, Backward, Dt, FarBelow, t, null, world);

        Assert.True(s.Position.Z < 8f, $"the flight tunnelled the wall, z={s.Position.Z}");
        Assert.True(s.HorizontalVelocity.Length() < 0.5f,
            $"a head-on wall must clip the carry to ~0, got {s.HorizontalVelocity}");
    }

    [Fact]
    public void The_slope_gate_still_blocks_a_momentum_flight()
    {
        // Momentum changes where the velocity comes from, never what the world is willing to let it reach. The face
        // stands ABOVE the flight (feet at Y=50, the cliff top at 60), which is what the direction-aware gate refuses:
        // a steep normal whose ground sits BELOW the feet is a descent and the arc is allowed to fly out over it.
        MoveTuning t = Base with { AirMomentum = true };
        Func<float, float, Vector3> cliff = (x, z) => z < -0.05f ? new Vector3(0f, 0.14f, 0.99f) : Vector3.UnitY;
        Func<float, float, float> cliffTop = (x, z) => z < -0.05f ? 60f : -1000f;
        MoveState after = CharacterMovement.Step(Airborne(new Vector2(0f, -30f)), Idle, Dt, cliffTop, t, cliff);

        Assert.Equal(0f, after.Position.Z);                     // the whole move refused, exactly as a command is
        Assert.Equal(Vector2.Zero, after.HorizontalVelocity);   // and nothing denied survives into the carry
    }

    // ---- Water ----

    [Fact]
    public void Water_entry_mid_flight_replaces_the_carried_arc_with_the_swim_velocity()
    {
        // A flight into a lake drops its arc at the waterline rather than skating across the surface at takeoff speed.
        MoveTuning t = Base with { AirMomentum = true, CapsuleHalfHeight = 0.5f };
        Func<float, float, float, MovementMedium> deep = (x, z, feetY) => new MovementMedium(100f, inWater: true);
        MoveState flying = new()
        {
            Position = new Vector3(0f, 90f, 0f), Grounded = false, TimeSinceGrounded = 1f,
            HorizontalVelocity = new Vector2(0f, -30f),
        };

        MoveState drifting = CharacterMovement.Step(flying, Idle, Dt, FarBelow, t, null, null, null, deep);
        Assert.True(drifting.Swimming);
        Assert.Equal(Vector2.Zero, drifting.HorizontalVelocity);

        MoveState swimming = CharacterMovement.Step(flying, Forward, Dt, FarBelow, t, null, null, null, deep);
        Assert.True(swimming.Swimming);
        Assert.Equal(t.SwimSpeed, swimming.HorizontalVelocity.Length(), 4);
        Assert.Equal(t.SwimSpeed, swimming.CommandedSpeed, 4);
    }

    // ---- The carry is maintained on every tick, knob or no knob ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_grounded_tick_carries_the_commanded_velocity_and_an_idle_one_carries_nothing(bool momentum)
    {
        // The field is written on EVERY tick and consumed only when the knob is on, which is what makes the
        // unchanged-at-the-default claim structural: with momentum off it is written here and never read.
        MoveTuning t = Base with { AirMomentum = momentum };

        MoveState moved = CharacterMovement.Step(OnGround(), Forward, Dt, Flat, t);
        Assert.True(moved.Grounded);
        Assert.Equal(t.WalkSpeed, moved.HorizontalVelocity.Length(), 3);
        Assert.Equal(moved.CommandedVelocity.X, moved.HorizontalVelocity.X, 4);
        Assert.Equal(moved.CommandedVelocity.Y, moved.HorizontalVelocity.Y, 4);

        MoveState still = CharacterMovement.Step(OnGround(), Idle, Dt, Flat, t);
        Assert.True(still.Grounded);
        Assert.Equal(Vector2.Zero, still.HorizontalVelocity);
        Assert.Equal(Vector2.Zero, still.CommandedVelocity);
    }

    [Fact]
    public void A_default_state_carries_nothing_and_reports_no_commanded_speed()
    {
        // Zero is byte-identical to a pre-feature state and reads as "carrying nothing" plus "commanded nothing",
        // both of which are the safe direction for a state that never went through a step.
        Assert.Equal(Vector2.Zero, default(MoveState).HorizontalVelocity);
        Assert.Equal(Vector2.Zero, default(MoveState).CommandedVelocity);
        Assert.Equal(0f, default(MoveState).CommandedSpeed);
    }
}
