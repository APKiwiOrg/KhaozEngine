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
    /// actually landed. Uses the same effective speed scale the step used (1 grounded, AirControl airborne) so the
    /// comparison isolates only the denial, not air-control scaling.</summary>
    public static float CorrectionDistance(in PlayerMoveState prev, in MoveCommand cmd, in PlayerMoveState after,
        float dt, in MoveTuning tuning)
    {
        float speedScale = prev.Grounded ? 1f : tuning.AirControl;
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
