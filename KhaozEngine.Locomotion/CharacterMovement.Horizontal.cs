using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

// The HORIZONTAL-INTENT half of the movement step: how a command becomes a desired XZ position, before any collision
// runs. Three pieces, one concern - resolve the caller's input to the shared shape (a unit direction plus a speed
// fraction), then turn that into a slope-gated advance. The camera-relative player path and the world-space AI path
// differ ONLY in which resolver they enter through, which is what makes their parity structural rather than a thing
// two copies of the same arithmetic have to keep agreeing about.
//
// Split out of the main CharacterMovement.cs so that file - already the engine's largest, and frozen by the file-size
// ratchet - does not grow, exactly as CharacterMovement.Fluid.cs, CharacterMovement.Momentum.cs,
// CharacterMovement.Landing.cs and CharacterMovement.Facing.cs did. Same partial type, same shared private core: the
// advance and the gate themselves are AdvanceSlopeGated (CharacterMovement.Momentum.cs), shared with the airborne
// momentum path, and the public seam over the camera resolver is CharacterMovement.CameraRelativeDir.cs.
public static partial class CharacterMovement
{
    /// <summary>Resolve a camera-relative <see cref="MoveCommand"/> into the unit world-space move direction (XZ) and
    /// a speed fraction the shared core consumes. The player always moves at full speed (the axis is normalized), so
    /// the fraction is exactly 1 when there is input and 0 when idle - preserving the pre-refactor behaviour
    /// bit-for-bit (the same <see cref="Vector3.Normalize(Vector3)"/> over the same camera basis, gated by the same
    /// 1e-6 length-squared threshold).</summary>
    private static (Vector2 dir, float fraction) ResolveCameraRelative(in MoveCommand cmd)
    {
        float sY = MathF.Sin(cmd.CameraYaw), cY = MathF.Cos(cmd.CameraYaw);
        Vector3 forward = new(-sY, 0f, -cY);
        Vector3 right = new(cY, 0f, -sY);
        Vector3 move = right * cmd.Move.X + forward * cmd.Move.Y;
        if (move.LengthSquared() > 1e-6f)
        {
            Vector3 n = Vector3.Normalize(move);
            return (new Vector2(n.X, n.Z), 1f);
        }
        return (Vector2.Zero, 0f);
    }

    /// <summary>Resolve a world-space steering direction into the unit move direction (XZ) and a speed fraction: the
    /// vector's length scales speed in [0,1] (unit = full speed, shorter = slower, longer clamped to 1), and a
    /// length below the same 1e-6 length-squared dead-zone the player path uses is treated as idle. This is the only
    /// difference between the AI and player entry points - once resolved, both drive the identical
    /// <c>StepCore</c>.</summary>
    private static (Vector2 dir, float fraction) ResolveWorldDir(Vector2 worldDir)
    {
        float lenSq = worldDir.LengthSquared();
        if (lenSq > 1e-6f)
        {
            float len = MathF.Sqrt(lenSq);
            float fraction = len > 1f ? 1f : len;
            return (worldDir / len, fraction);
        }
        return (Vector2.Zero, 0f);
    }

    // Desired world XZ position after applying the resolved horizontal move (unit direction + speed fraction) and the slope gate,
    // WITHOUT collision. The direction + speed fraction are resolved upstream from either a camera-relative MoveCommand
    // (ResolveCameraRelative) or a world-space steering direction (ResolveWorldDir), so player and AI share this exact input/slope
    // section. Prop collision is resolved separately by the swept collide-and-slide block in StepCore. moveDir is a unit vector when
    // speedFraction > 0. The advance + gate itself is AdvanceSlopeGated (CharacterMovement.Momentum.cs), shared with the airborne
    // momentum path, and it is DIRECTION-AWARE: a too-steep destination refuses the move only while its ground rises above the LOWER
    // of feetY (the capsule centre minus the half-height) and the ground under the current column, faster than the gate's own gradient
    // does over this tick's travel. So walking off a cliff falls through to gravity, climbing one is refused at every speed and every
    // tick rate, and no amount of vertical motion (a jump, a fall) discounts the ascent.
    // Returns the resolved speed alongside the position so StepCore can export it as MoveState.CommandedVelocity: it is reported
    // UNCONDITIONALLY, including on a slope-gate block, because the anomaly check needs the ask, not what survived. 0 when idle.
    private static (float x, float z, float speed) DesiredHorizontalCore(float x, float z, Vector2 moveDir,
        float speedFraction, bool run, float dt, in MoveTuning tuning, Func<float, float, Vector3>? groundNormal,
        Func<float, float, float> groundHeight, float feetY, float speedScale)
    {
        bool moving = speedFraction > 0f;
        float speed = moving ? (run ? tuning.RunSpeed : tuning.WalkSpeed) * speedScale * speedFraction : 0f;
        (x, z) = AdvanceSlopeGated(x, z, moveDir * speed, moving, dt, tuning, groundNormal, groundHeight, feetY);
        return (x, z, speed);
    }
}
