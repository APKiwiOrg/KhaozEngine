using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>
/// One frame/tick of locomotion intent. <see cref="Move"/> is the camera-relative input axis
/// (X = right/strafe, Y = forward, each nominally in [-1,1]); <see cref="Run"/> selects run speed;
/// <see cref="CameraYaw"/> is the follow-camera yaw used to resolve the axis into a world direction.
/// The timestep is NOT carried on the command (it is passed to <see cref="CharacterMovement.Step"/>),
/// so a hostile client cannot dilate time and the authoritative server and client prediction step the
/// same fixed dt. <c>default</c> is a no-input (idle) command.
/// </summary>
public readonly struct MoveCommand
{
    public MoveCommand(Vector2 move, bool run, float cameraYaw, bool jump = false)
    {
        Move = move;
        Run = run;
        CameraYaw = cameraYaw;
        Jump = jump;
    }

    /// <summary>Camera-relative input axis: X = right/strafe, Y = forward (each nominally in [-1,1]).</summary>
    public Vector2 Move { get; }

    /// <summary>True to use run speed instead of walk speed.</summary>
    public bool Run { get; }

    /// <summary>Follow-camera yaw (radians) used to resolve <see cref="Move"/> into a world direction.</summary>
    public float CameraYaw { get; }

    /// <summary>True when a jump is requested this tick. The vertical <see cref="CharacterMovement"/> step launches
    /// only when grounded (or within coyote-time); a press just before landing fires on contact (jump-buffer).</summary>
    public bool Jump { get; }

    /// <summary>A no-input command (zero move).</summary>
    public static MoveCommand Idle => default;
}
