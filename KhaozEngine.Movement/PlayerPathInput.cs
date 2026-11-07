using System;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

/// <summary>One tick's player controller input basis, shared by Drop prediction and host command submission.
/// Default uses camera yaw zero. Create a new value each tick when the camera rotates.</summary>
public readonly record struct PlayerPathInput
{
    /// <summary>Captures a finite camera yaw in radians.</summary>
    public PlayerPathInput(float cameraYaw)
    {
        if (!float.IsFinite(cameraYaw)) throw new ArgumentOutOfRangeException(nameof(cameraYaw));
        CameraYaw = cameraYaw;
    }

    /// <summary>The camera yaw used by both prediction and the submitted command.</summary>
    public float CameraYaw { get; }

    /// <summary>Creates the ordinary precise player command through the shared adapter.
    /// Submit this command for the steering returned by the tick that received this input value.</summary>
    public MoveCommand Command(in RangeSteering steering, bool run)
        => PlayerPathMovement.Command(steering, run, CameraYaw);
}
