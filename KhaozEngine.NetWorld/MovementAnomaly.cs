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
    /// actually landed. Uses the same effective speed scale the step used (1 grounded, AirControl airborne, times the
    /// entity's server-authored <see cref="MoveState.SpeedScale"/>) so the comparison isolates only the denial, not
    /// the scaling. The speed-scale term is load-bearing: without it a legitimately hasted player - who steps several
    /// times further per tick than an unscaled intended target - reads as a large correction on EVERY tick of the
    /// buff, and the streak below reports them as a speed hacker. The scale is server state (it reaches here from
    /// <see cref="MovementState.SpeedScaleQ"/>, never from the command), so folding it in grants a client nothing:
    /// an unhasted client claiming to be hasted still gets measured against the unhasted target.</summary>
    public static float CorrectionDistance(in PlayerMoveState prev, in MoveCommand cmd, in PlayerMoveState after,
        float dt, in MoveTuning tuning)
    {
        float speedScale = (prev.Grounded ? 1f : tuning.AirControl) * prev.Move.SpeedScale;
        Vector2 intended = CharacterMovement.IntendedHorizontalTarget(prev.Position, cmd, dt, tuning, speedScale);
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
