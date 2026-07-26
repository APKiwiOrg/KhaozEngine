using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.NetWorld;

/// <summary>
/// Shared server-side movement-anomaly math, so <see cref="WorldServer"/> and <see cref="ShardedWorldServer"/>
/// detect "the client keeps fighting the authoritative constraints" identically. The per-tick correction distance
/// is how far the constrained authoritative position fell short of the client's intended unconstrained move (the
/// slope gate, static collision, or play-area bound denied it). A streak of corrected ticks - not a single one -
/// raises the signal, so a legitimate player brushing a wall does not trip it.
/// </summary>
internal static class MovementAnomaly
{
    /// <summary>XZ distance between the client's intended unconstrained target and where the authoritative step
    /// actually landed, so the comparison isolates ONLY the denial.
    /// <para>The intended target is built from the speed the step itself reported
    /// (<see cref="MoveState.CommandedSpeed"/> on <paramref name="after"/>) rather than rebuilt from
    /// <see cref="MoveTuning"/> here. That is the load-bearing part. Every server-side speed term this check does
    /// not know about otherwise reads as a large correction on EVERY tick, and the streak below then reports a
    /// legitimate player as a speed hacker: a swimmer travelling at <see cref="MoveTuning.SwimSpeed"/> measured
    /// against <see cref="MoveTuning.RunSpeed"/> raised the signal after a third of a second of ordinary swimming,
    /// and wading and a zone scale each ate most of a typical correction budget the same way. Reading the sim's own
    /// number covers all of them at once, and covers whatever speed term is added next.</para>
    /// It grants a client nothing: the speed is entirely server-derived (tuning, the medium the server samples, and
    /// the server-authored <see cref="MoveState.SpeedScale"/>), and the only client-supplied inputs to it are the
    /// direction and the run bit. A client that merely CLAIMS to be fast is still measured against the speed the
    /// server actually gave it.
    /// <para><paramref name="dt"/> must be the tick the step ran. A state that never went through a step reports a
    /// commanded speed of 0, which reads as "no denial" rather than as a large one - the safe direction, and the
    /// reason the sharded head zeroes it for an entity its cell sim skipped.</para></summary>
    public static float CorrectionDistance(in PlayerMoveState prev, in MoveCommand cmd, in PlayerMoveState after,
        float dt)
    {
        Vector2 intended = CharacterMovement.IntendedHorizontalTargetAtSpeed(
            prev.Position, cmd, dt, after.Move.CommandedSpeed);
        var actual = new Vector2(after.Position.X, after.Position.Z);
        return Vector2.Distance(intended, actual);
    }

    /// <summary>Updates a slot's consecutive-corrected-tick streak and returns true exactly when it reaches the
    /// configured threshold (then resets, so the signal is not re-raised every subsequent tick).</summary>
    public static bool RegisterCorrection(Dictionary<int, int> streaks, int slot, float correction, AntiCheatConfig cfg)
    {
        if (correction > cfg.MaxCorrectionDistance)
        {
            int streak = streaks.GetValueOrDefault(slot) + 1;
            if (streak >= cfg.CorrectionStreak) { streaks[slot] = 0; return true; }
            streaks[slot] = streak;
            return false;
        }
        streaks[slot] = 0;   // a clean tick breaks the streak
        return false;
    }
}
