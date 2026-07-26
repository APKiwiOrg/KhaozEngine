using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The movement-correction anomaly must measure ONLY what the authoritative step denied (the slope gate, static
/// collision, the play-area bound), never a speed the step legitimately chose. It used to rebuild the client's
/// intended target from <see cref="MoveTuning.WalkSpeed"/>/<see cref="MoveTuning.RunSpeed"/> alone, so every
/// server-side speed term the step applied and the check did not know about read as a correction on every tick:
/// a swimming player travels at <see cref="MoveTuning.SwimSpeed"/> and was measured against
/// <see cref="MoveTuning.RunSpeed"/>, which fired <c>OnSuspiciousActivity</c> after a third of a second of
/// ordinary swimming. The step now exports the speed it actually commanded
/// (<see cref="MoveState.CommandedSpeed"/>) and the check reads that, so the two can never disagree again.
/// </summary>
public class MovementAnomalyMediumTests
{
    const float Dt = 1f / 30f;
    static readonly Func<float, float, float> Lakebed = (x, z) => -10f;
    static readonly MoveTuning Tuning = MoveTuning.Default;
    static MoveCommand Run => new(new Vector2(0f, 1f), run: true, cameraYaw: 0f);

    // The calibration a real consumer runs: "> one run tick" with the default streak.
    static AntiCheatConfig Cfg => new() { MaxCorrectionDistance = 0.25f, CorrectionStreak = 10 };

    static Func<float, float, float, MovementMedium> Water(float surfaceY, float zoneScale = 1f)
        => (x, z, feetY) => new MovementMedium(surfaceY, inWater: true, zoneScale);

    // Drives n ticks and returns (worst correction seen, whether the streak raised).
    static (float worst, bool raised) Drive(PlayerMoveSimulator sim, PlayerMoveState start, MoveCommand cmd, int ticks)
    {
        var streaks = new Dictionary<int, int>();
        PlayerMoveState prev = start;
        float worst = 0f;
        for (int i = 0; i < ticks; i++)
        {
            PlayerMoveState after = sim.Step(prev, cmd, Dt);
            float correction = MovementAnomaly.CorrectionDistance(prev, cmd, after, Dt);
            if (correction > worst) worst = correction;
            if (MovementAnomaly.RegisterCorrection(streaks, 0, correction, Cfg)) return (worst, true);
            prev = after;
        }
        return (worst, false);
    }

    static PlayerMoveState At(Vector3 position)
    {
        var s = new PlayerMoveState();
        s.Move.Position = position;
        return s;
    }

    static PlayerMoveState Standing()
    {
        PlayerMoveState s = At(new Vector3(0f, Tuning.CapsuleHalfHeight, 0f));
        s.Move.Grounded = true;
        return s;
    }

    [Fact]
    public void A_swimming_player_is_not_flagged()
    {
        // The shipped bug: SwimSpeed 2.5 travels 0.083 m/tick while the check expected RunSpeed 12 at 0.4 m/tick,
        // so it read 0.317 m of "correction" every tick and raised at tick 9.
        var sim = new PlayerMoveSimulator(Lakebed, Tuning, medium: Water(5f));
        (float worst, bool raised) = Drive(sim, At(new Vector3(0f, 4f, 0f)), Run, 60);

        Assert.False(raised, $"a swimming player raised the anomaly signal (worst correction {worst} m)");
        Assert.True(worst <= Cfg.MaxCorrectionDistance, $"worst correction {worst} m");
    }

    [Fact]
    public void A_wading_player_is_not_flagged()
    {
        // Wading did not fire at this calibration, but it consumed most of the budget (~0.22 m of the 0.25 m
        // allowance at the WadeMinSpeedScale floor), so a tighter threshold or a faster RunSpeed crossed it.
        var tuning = Tuning with { CapsuleHalfHeight = 0.5f, SwimEnterDepthFraction = 5f };   // deep wade, never swims
        var sim = new PlayerMoveSimulator(Lakebed, tuning, medium: Water(0.64f));
        PlayerMoveState start = At(new Vector3(0f, tuning.CapsuleHalfHeight - 10f, 0f));
        start.Move.Grounded = true;

        (float worst, bool raised) = Drive(sim, start, Run, 60);
        Assert.False(raised, $"a wading player raised the anomaly signal (worst correction {worst} m)");
    }

    [Fact]
    public void A_zone_slowed_player_is_not_flagged()
    {
        // A swamp zone dial (MovementMedium.WadeSpeedScale) is a third server-side speed term the check never knew
        // about. At 0.2 the player crawls, and every tick of that crawl used to read as a denial.
        var tuning = Tuning with { CapsuleHalfHeight = 0.5f, SwimEnterDepthFraction = 5f };
        var sim = new PlayerMoveSimulator(Lakebed, tuning, medium: Water(0.64f, zoneScale: 0.2f));
        PlayerMoveState start = At(new Vector3(0f, tuning.CapsuleHalfHeight - 10f, 0f));
        start.Move.Grounded = true;

        (float worst, bool raised) = Drive(sim, start, Run, 60);
        Assert.False(raised, $"a zone-slowed player raised the anomaly signal (worst correction {worst} m)");
    }

    [Fact]
    public void A_hasted_player_is_still_not_flagged()
    {
        // The 14.26.0 guarantee, re-pinned through the new export path rather than the old speed-scale term.
        var sim = new PlayerMoveSimulator((x, z) => 0f, Tuning);
        PlayerMoveState start = Standing();
        start.Move.SpeedScale = 5f;

        (float worst, bool raised) = Drive(sim, start, Run, 60);
        Assert.False(raised, $"a hasted player raised the anomaly signal (worst correction {worst} m)");
    }

    [Fact]
    public void A_player_driving_into_a_bound_is_still_flagged()
    {
        // The signal must survive the fix: this is what the detector is FOR. Denial by the play-area clamp still
        // reads at full magnitude and still raises.
        var sim = new PlayerMoveSimulator((x, z) => 0f, Tuning, bounds: new CircleBounds(Vector2.Zero, 1f));
        PlayerMoveState start = Standing();
        start.Move.Position = new Vector3(0f, Tuning.CapsuleHalfHeight, 1f);
        var into = new MoveCommand(new Vector2(0f, -1f), run: true, cameraYaw: 0f);   // straight at the +Z edge

        (float _, bool raised) = Drive(sim, start, into, 30);
        Assert.True(raised, "a client pinned against the play-area bound must still raise the signal");
    }

    [Fact]
    public void A_swimming_player_driving_into_a_bound_is_still_measured_as_fully_denied()
    {
        // The medium must not become a blanket EXEMPTION: a swimmer pinned against the bound is still measured as
        // denied, at the full magnitude of the stride it was denied.
        //
        // It does not cross a 0.25 m threshold, and that is the threshold's own semantics rather than a hole this
        // fix opened: MaxCorrectionDistance is an absolute per-tick distance, and a swimmer travelling 0.083 m/tick
        // cannot be denied 0.25 m in one tick no matter how hard it pushes. A consumer that wants constraint-fighting
        // caught at swim speed has to set a threshold scaled to swim speed. The detector's job here is to report the
        // denial honestly, which it now does - before this fix it reported 0.317 m for a swimmer in OPEN WATER,
        // denied nothing at all.
        var sim = new PlayerMoveSimulator(Lakebed, Tuning, bounds: new CircleBounds(Vector2.Zero, 1f),
            medium: Water(5f));
        PlayerMoveState start = At(new Vector3(0f, 4f, 1f));
        var into = new MoveCommand(new Vector2(0f, -1f), run: true, cameraYaw: 0f);   // straight at the +Z edge

        PlayerMoveState after = sim.Step(start, into, Dt);
        float correction = MovementAnomaly.CorrectionDistance(start, into, after, Dt);
        float fullStride = after.Move.CommandedSpeed * Dt;

        Assert.True(after.Move.Swimming, "the fixture should be swimming");
        Assert.True(fullStride > 0f, "the swimmer should have commanded a stride");
        Assert.Equal(fullStride, correction, 5);   // the bound denied the whole thing, and it is reported in full
    }

    [Fact]
    public void An_idle_command_commands_no_speed()
    {
        // The zero case has to stay zero: an idle tick must not read as a denial, and the exported speed is what
        // the anomaly check now trusts, so it has to be exactly 0 rather than merely small.
        MoveState idle = CharacterMovement.Step(
            new MoveState { Position = new Vector3(0f, Tuning.CapsuleHalfHeight, 0f), Grounded = true },
            MoveCommand.Idle, Dt, (x, z) => 0f, Tuning);
        Assert.Equal(0f, idle.CommandedSpeed);
    }

    [Fact]
    public void The_exported_speed_matches_the_distance_actually_travelled_when_nothing_denies_it()
    {
        // The invariant the whole fix rests on: with no slope gate, no collision and no bound, the step travels
        // exactly CommandedSpeed * dt. If that ever drifts, the anomaly check silently mis-measures every player.
        var cases = new (string name, MoveTuning tuning, Func<float, float, float, MovementMedium>? medium, Vector3 at, bool grounded)[]
        {
            ("dry run",  Tuning, null, new Vector3(0f, Tuning.CapsuleHalfHeight, 0f), true),
            ("wading",   Tuning with { CapsuleHalfHeight = 0.5f, SwimEnterDepthFraction = 5f }, Water(0.64f), new Vector3(0f, -9.5f, 0f), true),
            ("swimming", Tuning, Water(5f), new Vector3(0f, 4f, 0f), false),
        };
        foreach ((string name, MoveTuning tuning, Func<float, float, float, MovementMedium>? medium, Vector3 at, bool grounded) in cases)
        {
            var before = new MoveState { Position = at, Grounded = grounded };
            MoveState after = CharacterMovement.Step(before, Run, Dt, Lakebed, tuning, null, null, null, medium);
            float travelled = Vector2.Distance(
                new Vector2(before.Position.X, before.Position.Z), new Vector2(after.Position.X, after.Position.Z));
            Assert.Equal(after.CommandedSpeed * Dt, travelled, 5);
            Assert.True(after.CommandedSpeed > 0f, $"{name}: expected a non-zero commanded speed");
        }
    }
}
