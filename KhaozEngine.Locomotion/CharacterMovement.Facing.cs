using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

// The FACING half of the movement step: the canonical yaw range, the world-direction-to-yaw conversion that defines
// the convention, and the per-tick shortest-arc turn StepCore and SwimStep stamp onto MoveState.FacingYaw. One
// concern (which way the character is pointing), split out of the main CharacterMovement.cs so that file - already
// the engine's largest, and frozen by the file-size ratchet - does not grow, exactly as CharacterMovement.Fluid.cs,
// CharacterMovement.Momentum.cs and CharacterMovement.Landing.cs did. Same partial type, same shared private core.
//
// Facing is an OUTPUT and nothing else: ResolveFacing reads the carried heading, the resolved move direction and the
// command's camera yaw, and returns the new heading. No position, velocity or grounded value is derived from it
// anywhere, which is what makes every existing game bit-identical on position across this feature - a claim the
// stepper tests pin directly rather than inferring from a green build.
public static partial class CharacterMovement
{
    // One full turn. MathF.PI is a const, so this is a compile-time constant and both heads fold the identical value.
    private const float FacingTau = MathF.PI * 2f;

    /// <summary>An angle in radians reduced to <see cref="MoveState.FacingYaw"/>'s canonical range <c>[-pi, pi)</c>,
    /// low end inclusive. An angle already inside that range is returned BIT-IDENTICAL (no arithmetic runs on it at
    /// all), which is what makes the "the heading converges to <see cref="MoveCommand.CameraYaw"/> exactly" contract
    /// true rather than approximately: a camera producing a canonical yaw sees its own float land in
    /// <see cref="MoveState.FacingYaw"/> unchanged, and both heads therefore hold the same bits.
    /// <para>A non-finite angle is not an angle, and returns 0 (the default heading, -Z). Facing is CARRIED state, so
    /// a NaN reaching it would not corrupt one frame: it would strand the heading for the rest of the session, and
    /// every later comparison against it (including the shortest-arc step) would be false. An absurd but finite angle
    /// (past roughly 1e7 radians, where one float step of the value already spans more than a full turn, so no
    /// reduction can recover a heading from it) lands on the same 0 rather than on arithmetic noise.</para></summary>
    /// <param name="yaw">Any angle in radians.</param>
    /// <returns>The same heading, expressed in <c>[-pi, pi)</c>.</returns>
    public static float WrapYaw(float yaw)
    {
        if (yaw >= -MathF.PI && yaw < MathF.PI) return yaw;   // already canonical: returned untouched, bit for bit
        if (!float.IsFinite(yaw)) return 0f;
        float wrapped = yaw - FacingTau * MathF.Round(yaw * (1f / FacingTau));
        // The reduction lands inside the range up to one float rounding at its edges, so close the two edges
        // explicitly rather than trusting it. Both heads run the identical branches on the identical operand.
        if (wrapped >= MathF.PI) wrapped -= FacingTau;
        if (wrapped < -MathF.PI) wrapped += FacingTau;
        return wrapped >= MathF.PI || wrapped < -MathF.PI ? 0f : wrapped;
    }

    /// <summary>The heading (in <see cref="MoveState.FacingYaw"/>'s convention, canonical range) of a world-space XZ
    /// direction: <c>X</c> = world +X, <c>Y</c> = world +Z, matching <see cref="CameraRelativeDir"/>'s output. It is
    /// the exact inverse of the camera basis the step resolves a command in (forward is <c>(-sin yaw, 0, -cos yaw)</c>),
    /// so travelling along the camera forward under yaw <c>y</c> yields exactly <c>y</c> and the two halves of the
    /// facing rule - the <see cref="MoveCommand.FaceCamera"/> target and the commanded-direction target - cannot
    /// disagree by a constant offset nobody can see. Cardinal readings: <c>-Z</c> is 0, <c>-X</c> is <c>+pi/2</c>,
    /// <c>+Z</c> is <c>pi</c>. A zero vector has no direction and yields 0.</summary>
    /// <param name="dirXz">A world-space XZ direction (need not be normalized).</param>
    /// <returns>Its heading in radians, in <c>[-pi, pi)</c>.</returns>
    public static float FacingYawOf(Vector2 dirXz) =>
        dirXz == Vector2.Zero ? 0f : WrapYaw(MathF.Atan2(-dirXz.X, -dirXz.Y));

    /// <summary>One tick of the facing update, shared by every step path (the grounded/airborne
    /// <c>StepCore</c>, the swim <c>SwimStep</c>, and the world-space NPC <c>StepTowards</c>, which reaches it through
    /// <c>StepCore</c> with no camera). Pure scalar arithmetic in a fixed operation order over its arguments, so the
    /// authoritative server, client prediction and every reconciliation replay produce the identical heading.
    /// <para>Target selection, in order. First <paramref name="faceYaw"/>, when the command asked to face the camera:
    /// the heading then converges on it whatever the move axis is doing, which is the whole feature, since a
    /// STATIONARY character can turn and a strafing one keeps its body pointed at the camera. Failing that, the yaw
    /// of the commanded world-space move direction while there is input. Failing that, the current heading, so an
    /// idle character holds what it had rather than snapping back to some default the instant its player stops
    /// walking.</para>
    /// <para>The turn is the SHORTEST ARC at <see cref="MoveTuning.FacingTurnSpeed"/> radians per second. Wrapping the
    /// difference is what makes the seam behave: 3.0 to -3.0 is 0.28 rad the short way and 6.0 the long way, and a
    /// naive clamp on the raw difference sends the character the long way round - a full spin on the spot every time
    /// the camera crosses due-north. When the remaining gap fits inside one tick's budget the heading lands EXACTLY on
    /// the target rather than a float step beside it, so "converges exactly" holds at every turn speed and not only at
    /// the infinite default.</para></summary>
    /// <param name="current">The heading carried in from the previous tick (<see cref="MoveState.FacingYaw"/>).</param>
    /// <param name="moveDir">The resolved unit command direction (XZ), or zero when there is no input.</param>
    /// <param name="faceYaw">The camera yaw to face, or <c>null</c> when the command did not ask to face the camera
    /// (the NPC path always passes <c>null</c>: there is no camera on it).</param>
    /// <param name="dt">Timestep in seconds. A non-positive dt turns nothing.</param>
    /// <param name="t">Carries <see cref="MoveTuning.FacingTurnSpeed"/>.</param>
    private static float ResolveFacing(float current, Vector2 moveDir, float? faceYaw, float dt, in MoveTuning t)
    {
        float held = WrapYaw(current);
        float target = faceYaw.HasValue ? WrapYaw(faceYaw.Value)
            : moveDir != Vector2.Zero ? FacingYawOf(moveDir)
            : held;
        // A caller reaching the step directly (a single-player controller, a server-side NPC driver) can hand in a
        // non-finite camera yaw the wire decode would have rejected. WrapYaw maps that to 0, which would SWING the
        // character to due -Z. Holding the heading is the honest reading of "there is no target this tick".
        if (!float.IsFinite(faceYaw ?? 0f)) return held;

        float maxStep = t.FacingTurnSpeed * dt;
        // The default is +infinity, whose product with dt is +infinity (or NaN at dt 0), so the snap is the branch
        // that does no arithmetic on the target at all: it returns the wrapped target's own bits.
        if (float.IsPositiveInfinity(t.FacingTurnSpeed) && dt > 0f) return target;
        if (!(maxStep > 0f)) return held;   // a zero/negative rate, a non-positive dt, or a NaN: hold the heading

        float delta = WrapYaw(target - held);
        if (delta >= -maxStep && delta <= maxStep) return target;   // in reach this tick: land exactly on it
        return WrapYaw(held + (delta > 0f ? maxStep : -maxStep));
    }
}
