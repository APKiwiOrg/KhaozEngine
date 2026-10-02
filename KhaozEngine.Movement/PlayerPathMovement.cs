using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>Converts world range steering to ordinary precise player movement commands.</summary>
public static class PlayerPathMovement
{
    /// <summary>Projects Following input into the camera basis, preserving magnitudes up to one.
    /// Other statuses and nonfinite yaw or direction request idle motion. Invalid yaw is stored as zero.
    /// Run remains caller-owned, with no jump or camera-facing request.</summary>
    public static MoveCommand Command(in RangeSteering steering, bool run, float cameraYaw)
    {
        bool validYaw = float.IsFinite(cameraYaw);
        Vector2 direction = steering.WorldDirection;
        Vector2 move = Vector2.Zero;
        if (steering.Status == RangeMoveStatus.Following && validYaw &&
            float.IsFinite(direction.X) && float.IsFinite(direction.Y))
        {
            double x = direction.X, z = direction.Y;
            double length = Math.Sqrt(x * x + z * z);
            if (length > 1d)
            {
                x /= length;
                z /= length;
            }

            float sin = MathF.Sin(cameraYaw), cos = MathF.Cos(cameraYaw);
            move = new Vector2((float)(cos * x - sin * z), (float)(-sin * x - cos * z));
        }

        return new MoveCommand(move, run, validYaw ? cameraYaw : 0f,
            jump: false, faceCamera: false, scaleSpeedByAxis: true);
    }
}
