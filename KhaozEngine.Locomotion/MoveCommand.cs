using System.Numerics;

namespace KhaozEngine.Locomotion;

/// <summary>
/// One frame/tick of locomotion intent. <see cref="Move"/> is the camera-relative input axis
/// (X = right/strafe, Y = forward, each nominally in [-1,1]), <see cref="Run"/> selects run speed,
/// <see cref="CameraYaw"/> is the follow-camera yaw used to resolve the axis into a world direction, and
/// <see cref="FaceCamera"/> makes the character turn to that yaw instead of to where it is walking.
/// The timestep is NOT carried on the command (it is passed to <see cref="CharacterMovement"/>'s step),
/// so a hostile client cannot dilate time and the authoritative server and client prediction step the
/// same fixed dt. <c>default</c> is a no-input (idle) command.
/// </summary>
public readonly struct MoveCommand
{
    public MoveCommand(Vector2 move, bool run, float cameraYaw, bool jump = false, bool faceCamera = false)
    {
        Move = move;
        Run = run;
        CameraYaw = cameraYaw;
        Jump = jump;
        FaceCamera = faceCamera;
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

    /// <summary>True while the player is asking the character to FACE the camera (the held right mouse button in the
    /// usual binding) rather than to face where it is walking. It changes <see cref="MoveState.FacingYaw"/> only and
    /// never the position: with it set the facing target is <see cref="CameraYaw"/> whatever the
    /// <see cref="Move"/> axis is doing, so a strafing character keeps its body pointed at the camera and - the case
    /// that is impossible without it - a character with NO movement input can turn on the spot.
    /// <para><c>false</c> (the default, and what every pre-facing construction site produces) is the pre-facing
    /// behaviour exactly: the character faces the direction it is commanded to travel, and holds its heading when
    /// idle. It rides bit 1 of the move frame's flags byte, which was the bare run bool through wire generation 9.</para></summary>
    public bool FaceCamera { get; }

    /// <summary>A no-input command (zero move).</summary>
    public static MoveCommand Idle => default;
}
