using System;
using System.Numerics;
using KhaozEngine.Physics;   // the Step cref below names IPhysicsWorld in its signature

namespace KhaozEngine.Locomotion;

// The INTENT half of the movement step: where a command WOULD have reached this tick with nothing denying it.
// Distinct from the step itself, which is about where the capsule actually lands. Nothing in the step calls
// these - they exist for the server-side movement-anomaly check, which subtracts the two to measure exactly
// what the slope gate, static collision, or the play-area bound denied. Same partial type, same camera basis
// the step resolves commands with, so the comparison can never drift from the thing it is comparing against.
public static partial class CharacterMovement
{
    /// <summary>The unconstrained horizontal target the camera-relative move would reach in one step, before the
    /// slope gate, static collision, or play-area clamp deny any of it. The XZ distance from this to the position a
    /// constrained <see cref="Step(in MoveState, in MoveCommand, float, Func{float, float, float}, in MoveTuning, Func{float, float, Vector3}?, IPhysicsWorld?, Func{float, float, Vector2}?, Func{float, float, float, MovementMedium}?)"/>
    /// actually produced is the authoritative "correction" the server applied this tick - a server-side anti-cheat
    /// signal: a client repeatedly driving into a wall, slope, or boundary keeps this large. Pass
    /// <paramref name="speedScale"/> = the value the step used (1 grounded, <see cref="MoveTuning.AirControl"/>
    /// airborne, times the entity's <see cref="MoveState.SpeedScale"/>) so the comparison isolates only the denial,
    /// not the scaling. Getting that product wrong is not a rounding error but an inverted signal: a legitimately
    /// hasted player steps far beyond an unscaled "intended" target, so every boosted tick reads as a large
    /// correction and the anti-cheat streak flags them as a speed hacker. Mirrors the basis + speed of
    /// <see cref="DesiredHorizontalCore"/> (pre-gate).</summary>
    public static Vector2 IntendedHorizontalTarget(Vector3 position, in MoveCommand cmd, float dt,
        in MoveTuning tuning, float speedScale = 1f)
        => IntendedHorizontalTargetAtSpeed(position, cmd, dt,
            (cmd.Run ? tuning.RunSpeed : tuning.WalkSpeed) * speedScale);

    /// <summary>The unconstrained horizontal target a command would reach in one step at an EXPLICIT speed (m/s),
    /// rather than one rebuilt from <see cref="MoveTuning"/>. Same camera basis and same pre-gate geometry as the
    /// overload above, which now delegates here so the two can never diverge.
    /// <para>The direction comes from the COMMAND and only the magnitude from the caller, which is what makes this
    /// the scalar form. It is correct for any caller whose travel direction is its input direction, which is every
    /// grounded step and every airborne one without <see cref="MoveTuning.AirMomentum"/>. It is NOT what the movement
    /// anomaly check uses any more: under momentum the direction of travel is the conserved velocity rather than the
    /// input, so that check moved to <see cref="IntendedHorizontalTargetAtVelocity"/>. See its summary for why.</para>
    /// <para>Pass <paramref name="speed"/> as the speed the step actually resolved (walk/run or swim, times air
    /// control, the wade ramp, the zone scale, the per-entity <see cref="MoveState.SpeedScale"/>, and the command's
    /// speed fraction) rather than rebuilding the product from tuning, which is what made a swimming or wading player
    /// read as a speed hacker. It is the FULL speed including any speed fraction, so the direction here is normalised
    /// and the magnitude comes entirely from the caller.</para></summary>
    /// <param name="position">The pre-step capsule-centre position.</param>
    /// <param name="cmd">The command whose camera-relative axis gives the travel direction (idle = no movement).</param>
    /// <param name="dt">Timestep in seconds.</param>
    /// <param name="speed">The unconstrained horizontal speed in m/s the step commanded.</param>
    public static Vector2 IntendedHorizontalTargetAtSpeed(Vector3 position, in MoveCommand cmd, float dt, float speed)
    {
        float sY = MathF.Sin(cmd.CameraYaw), cY = MathF.Cos(cmd.CameraYaw);
        Vector3 forward = new(-sY, 0f, -cY);
        Vector3 right = new(cY, 0f, -sY);
        Vector3 move = right * cmd.Move.X + forward * cmd.Move.Y;
        float x = position.X, z = position.Z;
        if (move.LengthSquared() > 1e-6f)
        {
            move = Vector3.Normalize(move);
            x += move.X * speed * dt;
            z += move.Z * speed * dt;
        }
        return new Vector2(x, z);
    }

    /// <summary>The unconstrained horizontal target the step's own commanded VELOCITY would reach in one step:
    /// <c>position.XZ + velocity * dt</c>. The direction comes from the velocity, so no command is needed and no
    /// camera basis is rebuilt.
    /// <para>This is the form the movement anomaly check uses, paired with <see cref="MoveState.CommandedVelocity"/>.
    /// It replaced the scalar <see cref="IntendedHorizontalTargetAtSpeed"/> there because under
    /// <see cref="MoveTuning.AirMomentum"/> the direction of travel is the conserved
    /// <see cref="MoveState.HorizontalVelocity"/>, not the input direction. A player who releases input mid-flight at
    /// 30 m/s keeps travelling at 30 m/s, while the command direction collapses to zero and puts the scalar form's
    /// intended target back at the capsule: the whole legitimate arc then measures as a full-speed denial on EVERY
    /// airborne tick, and the streak reports an ordinary jump as speed hacking. Reading the vector the step exported
    /// is exact under both models, because with momentum off <see cref="MoveState.CommandedVelocity"/> is exactly
    /// <c>moveDir * CommandedSpeed</c> and this builds the same target the scalar form does.</para>
    /// It grants a client nothing: the velocity is entirely server-derived (tuning, the medium the server samples, the
    /// server-authored <see cref="MoveState.SpeedScale"/>, and an arc the server itself flew), and the only
    /// client-supplied inputs to it are the direction and the run bit.</summary>
    /// <param name="position">The pre-step capsule-centre position.</param>
    /// <param name="velocity">The unconstrained horizontal velocity in m/s the step commanded (XZ, so Y is world Z).</param>
    /// <param name="dt">Timestep in seconds.</param>
    public static Vector2 IntendedHorizontalTargetAtVelocity(Vector3 position, Vector2 velocity, float dt)
        => new(position.X + velocity.X * dt, position.Z + velocity.Y * dt);
}
