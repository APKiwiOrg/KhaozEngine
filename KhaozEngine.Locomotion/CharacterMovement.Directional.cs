using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

// DIRECTIONAL SPEED SCALING under MoveCommand.FaceCamera (#479). While the character is pinned to the camera it has a
// front that does not turn with its travel, so "which way is it moving relative to where it is looking" becomes a
// question the sim can answer and charge for: full speed forward, MoveTuning.StrafeSpeedScale sideways,
// MoveTuning.BackpedalSpeedScale backwards with MoveTuning.BackpedalAllowsRun deciding whether the run bit survives
// the trip. Without FaceCamera the facing follows the movement, there is no reverse to be slower than, and none of
// this is reached.
//
// It is sim-side rather than a scale the client applies to its own input, and that is the whole point: a client-side
// scale is both a speed hack (the client can simply not apply it) and a misprediction (the server would not apply it
// either). Being a pure function of THIS tick's command plus the tuning is what makes it free for the netcode - the
// authoritative head and the prediction replay reach the same answer with nothing carried between ticks, so a
// reconcile replay is bit-identical (DirectionalSpeedReconcileParityTests).
//
// Its own partial file, beside CharacterMovement.Horizontal.cs, so the main CharacterMovement.cs - already the
// engine's largest and one line under the file-size cap (#480) - does not grow by a single line for this feature.
public static partial class CharacterMovement
{
    /// <summary>Which <see cref="MoveSector"/> <paramref name="cmd"/>'s camera-relative axis falls in: the sector
    /// rule the sim charges <see cref="MoveTuning.StrafeSpeedScale"/> and <see cref="MoveTuning.BackpedalSpeedScale"/>
    /// by, exposed so a consumer choosing a locomotion animation reads the same answer the movement did instead of
    /// re-deriving it. Note this is the SECTOR alone: whether it costs anything is up to the tuning and to
    /// <see cref="MoveCommand.FaceCamera"/>, which this deliberately does not look at (a consumer wants the sector for
    /// presentation whether or not the sim is charging for it, and the neutral defaults mean "charging" is a
    /// per-game decision).
    /// <para>THE PREDICATES, and they are the whole specification. With <c>a = |Move.X|</c> (the lateral component):
    /// <c>Move.Y &gt;= a</c> is <see cref="MoveSector.Forward"/>, <c>Move.Y &lt;= -a</c> is
    /// <see cref="MoveSector.Reverse"/>, and everything else is <see cref="MoveSector.Strafe"/>. An absolute value and
    /// two comparisons: no <c>atan2</c>, no normalize, no division, nothing whose last bit could differ between a
    /// server and a client, and nothing that cares about the axis's LENGTH (half a stick deflection classifies exactly
    /// as a full one does, since both sides of each comparison scale together).</para>
    /// <para>THE BOUNDARIES, which are a decision rather than an accident. Forward and reverse are CLOSED wedges and
    /// strafe is what is left over, so the ray at exactly 45 degrees belongs to forward and the ray at exactly 135
    /// belongs to reverse. A keyboard is why this matters: the WASD axis is built from whole +/-1 components, so W+D
    /// is the vector (1, 1), which is EXACTLY 45 degrees and one of the most common things a player holds. Reading it
    /// as a strafe would run every forward diagonal at the strafe scale. Mirrored, S+D at exactly 135 degrees is a
    /// retreat with a lean rather than a sidestep, and giving it to strafe would hand a player who wants to flee fast
    /// a strictly better key combination than the one that means flee.</para>
    /// <para>An IDLE command (a zero axis) satisfies the forward predicate and reads as
    /// <see cref="MoveSector.Forward"/>. That is the harmless answer: it is the sector that scales nothing, and an
    /// idle tick commands no speed for a scale to apply to anyway.</para></summary>
    /// <param name="cmd">The movement command. Only its <see cref="MoveCommand.Move"/> axis is read.</param>
    /// <returns>The sector the command axis lies in.</returns>
    public static MoveSector Sector(in MoveCommand cmd)
    {
        float lateral = MathF.Abs(cmd.Move.X);
        if (cmd.Move.Y >= lateral) return MoveSector.Forward;
        if (cmd.Move.Y <= -lateral) return MoveSector.Reverse;
        return MoveSector.Strafe;
    }

    /// <summary>Resolve a camera-relative <see cref="MoveCommand"/> into everything the shared core needs from it: the
    /// unit world-space move direction, the speed fraction, and the EFFECTIVE run bit. This is the player entry
    /// points' one resolver, and the only place the directional scale is applied.
    /// <para>WHERE THE SCALE LANDS, and why here. It multiplies the SPEED FRACTION - the [0,1] "how much of the
    /// available speed is this command asking for" that <c>ResolveCameraRelative</c> already produces and that every
    /// downstream consumer of a resolved command reads: the grounded/airborne <c>DesiredHorizontalCore</c>, the
    /// airborne-momentum steer target, the slide's in-plane input steer, and the swim step. Applying it there means it
    /// composes with <see cref="MoveState.SpeedScale"/>, the wade ramp, the zone scale, and
    /// <see cref="MoveTuning.AirControl"/> exactly once, by multiplication, in whatever combination the tick happens
    /// to be in, with no per-path edit and nothing to keep in sync as a path is added. It also reaches the
    /// anti-cheat for free: <see cref="MoveState.CommandedVelocity"/> is built from the same product, so the server's
    /// intended-target shrinks with the scale and a backpedalling player is denied nothing rather than reading as a
    /// full-speed correction on every tick (the swimmer lesson in <c>MovementAnomaly</c>, one term later).</para>
    /// <para>Momentum is untouched by construction: the carried <see cref="MoveState.HorizontalVelocity"/> an
    /// airborne arc flies is not a command, so a scaled command steers it (with whatever authority
    /// <see cref="MoveTuning.AirControl"/> grants) but never scales the arc itself.</para>
    /// <para>The run bit is the one thing a fraction cannot carry, since refusing a sprint changes the BASE speed the
    /// fraction multiplies rather than the fraction, so it rides back alongside it. Forward and strafe pass it
    /// through untouched.</para></summary>
    private static (Vector2 dir, float fraction, bool run) ResolveCameraCommand(in MoveCommand cmd, in MoveTuning tuning)
    {
        (Vector2 dir, float fraction) = ResolveCameraRelative(cmd);
        if (!cmd.FaceCamera) return (dir, fraction, cmd.Run);   // no front, no sectors, nothing to charge
        return Sector(cmd) switch
        {
            MoveSector.Strafe => (dir, fraction * SpeedScaleOf(tuning.StrafeSpeedScale), cmd.Run),
            MoveSector.Reverse => (dir, fraction * SpeedScaleOf(tuning.BackpedalSpeedScale),
                cmd.Run && tuning.BackpedalAllowsRun),
            _ => (dir, fraction, cmd.Run),
        };
    }

    /// <summary>A directional speed scale as the resolve may use it: itself when it is zero or positive, and 0 for a
    /// negative or NaN one. A negative multiplier would REVERSE travel (the character would walk forwards while the
    /// player held back), and a NaN would put a NaN in the position, which replicates and permanently strands the
    /// entity - the same harmless-degradation direction the other tuning guards take. A tuning of exactly 1 (the
    /// default, and every game that never opts in) passes through this untouched and multiplies the fraction by
    /// exactly 1, which is the identity for every float, so the neutral path is bit-identical rather than
    /// nearly so.</summary>
    private static float SpeedScaleOf(float scale) => scale >= 0f ? scale : 0f;
}
