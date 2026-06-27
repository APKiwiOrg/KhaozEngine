using System;
using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>
/// Pure XZ-plane character locomotion: the single movement step run by the local controller, the
/// authoritative server sim, and client-side prediction alike. <see cref="Step"/> resolves a
/// camera-relative <see cref="MoveCommand"/> into a world move, normalizes diagonals, applies walk/run
/// speed over <paramref name="dt"/>, optionally rejects a step onto too-steep ground, then clamps Y onto
/// the ground delegate plus the capsule half-height. No input, render, physics, or netcode dependency.
/// </summary>
public static class CharacterMovement
{
    /// <param name="position">Current capsule-centre world position.</param>
    /// <param name="cmd">Movement intent (camera-relative axis + run + camera yaw).</param>
    /// <param name="dt">Timestep in seconds.</param>
    /// <param name="groundHeight">Terrain height at (x, z).</param>
    /// <param name="tuning">Speed/half-height/slope constants.</param>
    /// <param name="groundNormal">Optional ground normal at (x, z); when given, gates a step by slope.</param>
    /// <returns>The advanced position (Y on the ground + half-height).</returns>
    public static Vector3 Step(Vector3 position, in MoveCommand cmd, float dt,
        Func<float, float, float> groundHeight, in MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null)
    {
        if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));

        // Camera-relative ground basis (matches FollowCamera3D's yaw convention).
        float sY = MathF.Sin(cmd.CameraYaw), cY = MathF.Cos(cmd.CameraYaw);
        Vector3 forward = new(-sY, 0f, -cY);
        Vector3 right = new(cY, 0f, -sY);

        Vector3 move = right * cmd.Move.X + forward * cmd.Move.Y;
        if (move.LengthSquared() > 1e-6f)
        {
            move = Vector3.Normalize(move);   // normalized diagonals
            float speed = cmd.Run ? tuning.RunSpeed : tuning.WalkSpeed;
            float nx = position.X + move.X * speed * dt;
            float nz = position.Z + move.Z * speed * dt;

            bool blocked = false;
            if (groundNormal is not null)
            {
                float ny = Math.Clamp(groundNormal(nx, nz).Y, 0f, 1f);
                if (MathF.Acos(ny) > tuning.MaxSlopeRadians) blocked = true;
            }
            if (!blocked) { position.X = nx; position.Z = nz; }
        }

        position.Y = groundHeight(position.X, position.Z) + tuning.CapsuleHalfHeight;
        return position;
    }
}
