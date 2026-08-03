using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

// DIRECTIONAL SPEED SCALING under MoveCommand.FaceCamera (#479): while the character is pinned to the camera it has a
// front, so the direction it is asked to travel relative to that front is a thing the sim can charge for - full speed
// forward, MoveTuning.StrafeSpeedScale sideways, MoveTuning.BackpedalSpeedScale backwards with the run bit optionally
// refused. Without FaceCamera the character faces where it walks, there is no "backwards" to be slower than, and none
// of it is consulted.
//
// These pin the sector rule (including which sector owns each boundary ray, which is not a detail: a WASD diagonal
// lands EXACTLY on one), the speeds each sector resolves at, the run rule, and the two bit-identity claims the
// defaults rest on. The measurements are taken on flat analytic terrain with no physics world, so travel per tick is
// exactly the commanded speed and any scale shows up as distance.
public class DirectionalSpeedTests
{
    const float Dt = 1f / 30f;

    static readonly Func<float, float, float> Flat = (x, z) => 0f;

    // The reference feel: a slow sidestep, a much slower retreat, and no sprinting backwards. Ruinborne's opening
    // numbers from the playtest that raised #479.
    static MoveTuning Tuned => MoveTuning.Default with
    {
        StrafeSpeedScale = 0.4f,
        BackpedalSpeedScale = 0.2f,
        BackpedalAllowsRun = false,
    };

    static MoveState Grounded(in MoveTuning t) => new()
    {
        Position = new Vector3(0f, t.CapsuleHalfHeight, 0f),
        Grounded = true,
    };

    static MoveCommand Cmd(Vector2 axis, bool run = false, bool faceCamera = true) =>
        new(axis, run, cameraYaw: 0f, jump: false, faceCamera: faceCamera);

    // The speed one tick of this command actually travelled at, measured off the committed position. Reads the step's
    // own CommandedVelocity too and asserts the two agree, because the anti-cheat check downstream trusts the export
    // rather than the displacement, and a scale applied to only one of them is exactly the bug that would flag every
    // backpedalling player.
    static float SpeedOf(in MoveCommand cmd, in MoveTuning t)
    {
        MoveState before = Grounded(t);
        MoveState after = CharacterMovement.Step(before, cmd, Dt, Flat, t);
        float travelled = new Vector2(after.Position.X - before.Position.X, after.Position.Z - before.Position.Z).Length() / Dt;
        Assert.Equal(travelled, after.CommandedVelocity.Length(), 4);
        return travelled;
    }

    // ---- The sector rule and its boundaries ----

    [Theory]
    [InlineData(0f, 1f, MoveSector.Forward)]        // straight ahead
    [InlineData(1f, 1f, MoveSector.Forward)]        // W+D: EXACTLY 45 degrees, and forward owns it
    [InlineData(-1f, 1f, MoveSector.Forward)]       // W+A, the mirror
    [InlineData(0.5f, 0.5f, MoveSector.Forward)]    // the boundary is scale-invariant: half deflection, same answer
    [InlineData(1f, 0f, MoveSector.Strafe)]         // straight sideways
    [InlineData(1f, 0.99f, MoveSector.Strafe)]      // just outside the forward wedge
    [InlineData(1f, -0.99f, MoveSector.Strafe)]     // just outside the reverse wedge
    [InlineData(1f, -1f, MoveSector.Reverse)]       // S+D: EXACTLY 135 degrees, and reverse owns it
    [InlineData(-1f, -1f, MoveSector.Reverse)]      // S+A, the mirror
    [InlineData(0f, -1f, MoveSector.Reverse)]       // straight back
    [InlineData(0f, 0f, MoveSector.Forward)]        // idle has no direction; the neutral sector is the one that scales nothing
    public void The_sector_predicates_own_their_boundaries(float x, float y, MoveSector expected)
    {
        // THE BOUNDARY OWNERSHIP, stated once and pinned here. Forward is the CLOSED wedge |x| <= y, reverse the
        // CLOSED wedge |x| <= -y, and strafe is what is left, both OPEN wedges between. So each boundary ray belongs
        // to the sector nearer the axis it straddles: forward owns exactly 45 degrees, reverse owns exactly 135.
        //
        // That is not arbitrary. CharacterFacing.MoveAxis returns whole +/-1 components, so W+D is the vector (1, 1),
        // which is exactly 45 degrees and by far the most common thing a keyboard player holds - giving it to the
        // strafe sector would mean every forward diagonal ran at the strafe scale. The same argument mirrored gives
        // 135 to reverse: S+D is a retreat with a lean, not a sidestep, and reading it as a sidestep would hand a
        // player who wants to run away fast a strictly better key combination than the one that means "run away".
        Assert.Equal(expected, CharacterMovement.Sector(Cmd(new Vector2(x, y))));

        // The classification reads the COMMAND axis, so it is independent of where the camera is pointing. A rotated
        // camera turns the same command into a different world direction at the same speed.
        Assert.Equal(expected, CharacterMovement.Sector(
            new MoveCommand(new Vector2(x, y), run: false, cameraYaw: 2.3f, jump: false, faceCamera: true)));
    }

    // ---- What each sector resolves at ----

    [Fact]
    public void Forward_is_full_speed_and_honours_run()
    {
        MoveTuning t = Tuned;
        Assert.Equal(t.WalkSpeed, SpeedOf(Cmd(new Vector2(0f, 1f)), t), 3);
        Assert.Equal(t.RunSpeed, SpeedOf(Cmd(new Vector2(0f, 1f), run: true), t), 3);

        // The forward DIAGONAL is full speed too, which is the boundary decision above showing up as feel.
        Assert.Equal(t.RunSpeed, SpeedOf(Cmd(new Vector2(1f, 1f), run: true), t), 3);
    }

    [Fact]
    public void Strafe_scales_the_speed_and_still_honours_run()
    {
        MoveTuning t = Tuned;
        Assert.Equal(t.WalkSpeed * t.StrafeSpeedScale, SpeedOf(Cmd(new Vector2(1f, 0f)), t), 3);

        // Run is HONOURED sideways: a sprinting sidestep is RunSpeed * the scale, not WalkSpeed * the scale.
        Assert.Equal(t.RunSpeed * t.StrafeSpeedScale, SpeedOf(Cmd(new Vector2(1f, 0f), run: true), t), 3);
        Assert.Equal(t.RunSpeed * t.StrafeSpeedScale, SpeedOf(Cmd(new Vector2(-1f, 0f), run: true), t), 3);
    }

    [Fact]
    public void Reverse_scales_the_speed_and_ignores_run_when_it_is_disallowed()
    {
        MoveTuning t = Tuned;   // BackpedalAllowsRun false
        Assert.Equal(t.WalkSpeed * t.BackpedalSpeedScale, SpeedOf(Cmd(new Vector2(0f, -1f)), t), 3);

        // The run bit buys NOTHING: the base is the walk speed either way, so holding sprint while retreating is
        // exactly as fast as not holding it.
        Assert.Equal(t.WalkSpeed * t.BackpedalSpeedScale, SpeedOf(Cmd(new Vector2(0f, -1f), run: true), t), 3);
        Assert.Equal(t.WalkSpeed * t.BackpedalSpeedScale, SpeedOf(Cmd(new Vector2(1f, -1f), run: true), t), 3);
    }

    [Fact]
    public void Reverse_honours_run_when_it_is_allowed()
    {
        // The other half of the knob, and the reason it is a knob rather than an implication of the scale: a game can
        // want a slow retreat a player may still sprint into.
        MoveTuning t = Tuned with { BackpedalAllowsRun = true };
        Assert.Equal(t.RunSpeed * t.BackpedalSpeedScale, SpeedOf(Cmd(new Vector2(0f, -1f), run: true), t), 3);
        Assert.Equal(t.WalkSpeed * t.BackpedalSpeedScale, SpeedOf(Cmd(new Vector2(0f, -1f)), t), 3);
    }

    [Fact]
    public void A_zero_or_hostile_scale_stops_the_move_rather_than_reversing_it()
    {
        // 0 is a legitimate setting (a character that cannot walk backwards), and a NEGATIVE or NaN one must read the
        // same way rather than reversing travel or poisoning the position with a NaN that would replicate.
        foreach (float scale in new[] { 0f, -1f, float.NaN })
        {
            MoveTuning t = Tuned with { BackpedalSpeedScale = scale };
            MoveState before = Grounded(t);
            MoveState after = CharacterMovement.Step(before, Cmd(new Vector2(0f, -1f), run: true), Dt, Flat, t);
            Assert.Equal(before.Position, after.Position);
            Assert.Equal(Vector2.Zero, after.CommandedVelocity);
        }
    }

    // ---- The two bit-identity claims ----

    [Fact]
    public void The_neutral_defaults_are_bit_identical_for_a_game_that_never_sets_them()
    {
        // The claim the whole feature rests on: MoveTuning.Default carries 1, 1 and true, so a game that has never
        // heard of these knobs gets EXACTLY what it got before, in every sector, holding FaceCamera or not. Bit
        // equality, not a tolerance - an inherited retune with a green build is the failure mode the fleet has
        // already been burned by once.
        MoveTuning neutral = MoveTuning.Default;
        MoveTuning explicitly = MoveTuning.Default with
        {
            StrafeSpeedScale = 1f, BackpedalSpeedScale = 1f, BackpedalAllowsRun = true,
        };
        Assert.Equal(1f, neutral.StrafeSpeedScale);
        Assert.Equal(1f, neutral.BackpedalSpeedScale);
        Assert.True(neutral.BackpedalAllowsRun);

        foreach (Vector2 axis in Axes())
        foreach (bool run in new[] { false, true })
        {
            MoveCommand cmd = Cmd(axis, run);
            MoveState a = CharacterMovement.Step(Grounded(neutral), cmd, Dt, Flat, neutral);
            MoveState b = CharacterMovement.Step(Grounded(explicitly), cmd, Dt, Flat, explicitly);
            Assert.Equal(a.Position, b.Position);

            // And the neutral run is the full-speed run in every sector, which is what "nothing changed" means in
            // numbers rather than in a comparison against itself.
            float expected = axis == Vector2.Zero ? 0f : (run ? neutral.RunSpeed : neutral.WalkSpeed);
            Assert.Equal(expected, SpeedOf(cmd, neutral), 3);
        }
    }

    [Fact]
    public void Without_FaceCamera_nothing_is_scaled_however_the_knobs_are_set()
    {
        // The second claim: without FaceCamera the character faces where it walks, so there is no forward to be
        // relative to and no sector at all. A game may set the knobs to anything and a command sent without the flag
        // resolves exactly as it did before they existed.
        MoveTuning t = Tuned;
        MoveTuning neutral = MoveTuning.Default;

        foreach (Vector2 axis in Axes())
        foreach (bool run in new[] { false, true })
        {
            MoveCommand cmd = Cmd(axis, run, faceCamera: false);
            MoveState scaled = CharacterMovement.Step(Grounded(t), cmd, Dt, Flat, t);
            MoveState plain = CharacterMovement.Step(Grounded(neutral), cmd, Dt, Flat, neutral);
            Assert.Equal(plain.Position, scaled.Position);
            Assert.Equal(plain.CommandedVelocity, scaled.CommandedVelocity);
        }
    }

    [Fact]
    public void The_world_space_agent_path_never_sees_a_sector()
    {
        // StepTowards has no camera and no FaceCamera bit, so an NPC steered backwards relative to nothing keeps its
        // full speed whatever the tuning says. Pinned because the scale rides the resolved speed fraction, which is
        // the one thing the player and agent entry points share.
        MoveTuning t = Tuned;
        MoveState before = Grounded(t);
        foreach (Vector2 dir in new[] { new Vector2(0f, 1f), new Vector2(0f, -1f), new Vector2(1f, 0f) })
        {
            MoveState after = CharacterMovement.StepTowards(before, dir, run: true, Dt, Flat, t);
            float speed = new Vector2(after.Position.X - before.Position.X, after.Position.Z - before.Position.Z).Length() / Dt;
            Assert.Equal(t.RunSpeed, speed, 3);
        }
    }

    // ---- Airborne ----

    [Fact]
    public void Airborne_command_speed_is_scaled_the_same_way_grounded_is()
    {
        // AirControl already scales the authority a command has in the air, and the directional scale composes with it
        // rather than replacing it, so a backpedal in mid-air is the same fraction of a forward one as it is on the
        // ground.
        MoveTuning t = Tuned with { AirControl = 0.5f };
        var airborne = new MoveState { Position = new Vector3(0f, 40f, 0f), Grounded = false };

        MoveState fwd = CharacterMovement.Step(airborne, Cmd(new Vector2(0f, 1f)), Dt, Flat, t);
        MoveState back = CharacterMovement.Step(airborne, Cmd(new Vector2(0f, -1f)), Dt, Flat, t);

        Assert.Equal(t.WalkSpeed * t.AirControl, MathF.Abs(fwd.Position.Z - airborne.Position.Z) / Dt, 3);
        Assert.Equal(t.WalkSpeed * t.AirControl * t.BackpedalSpeedScale,
            MathF.Abs(back.Position.Z - airborne.Position.Z) / Dt, 3);
    }

    [Fact]
    public void Momentum_carry_is_not_scaled_only_the_command_is()
    {
        // Under MoveTuning.AirMomentum the arc flies the CARRIED velocity and the command only steers it. The scale
        // is a property of the command, so it must reach the steering target and nothing else: an arc launched at
        // full forward speed keeps that speed while the player holds backwards, exactly as it keeps it while the
        // player holds nothing.
        MoveTuning t = Tuned with { AirMomentum = true, AirControl = 0f };   // no steering authority at all
        var launched = new MoveState
        {
            Position = new Vector3(0f, 40f, 0f),
            Grounded = false,
            HorizontalVelocity = new Vector2(0f, -t.RunSpeed),   // flying north at run speed
        };

        MoveState back = CharacterMovement.Step(launched, Cmd(new Vector2(0f, -1f), run: true), Dt, Flat, t);
        MoveState idle = CharacterMovement.Step(launched, Cmd(Vector2.Zero), Dt, Flat, t);
        Assert.Equal(idle.Position, back.Position);
        Assert.Equal(t.RunSpeed, back.HorizontalVelocity.Length(), 3);
    }

    static Vector2[] Axes() => new[]
    {
        Vector2.Zero,
        new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(-1f, 1f),
        new Vector2(1f, 0f), new Vector2(-1f, 0f),
        new Vector2(1f, -1f), new Vector2(0f, -1f), new Vector2(-1f, -1f),
    };
}
