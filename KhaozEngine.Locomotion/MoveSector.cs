namespace KhaozEngine.Locomotion;

/// <summary>
/// Which DIRECTIONAL SECTOR a <see cref="MoveCommand"/>'s camera-relative axis falls in, relative to the direction the
/// character is facing. Only meaningful while <see cref="MoveCommand.FaceCamera"/> is held: that is what gives the
/// character a fixed front for the axis to be measured against. Without it the character turns to face wherever it is
/// walking, so every command is a <see cref="Forward"/> one by construction and the sim never consults this.
/// <para>The three sectors partition the circle into a closed 90 degree wedge ahead, a closed 90 degree wedge behind,
/// and the two open wedges left over. Where each boundary RAY lands is a documented, deterministic choice rather than a
/// rounding accident, because a keyboard puts commands exactly on those rays: see
/// <see cref="CharacterMovement.Sector"/> for the predicates and the reasoning.</para>
/// <para>The sim uses it to charge <see cref="MoveTuning.StrafeSpeedScale"/> and
/// <see cref="MoveTuning.BackpedalSpeedScale"/>. It is public because a consumer needs the same answer for
/// presentation - which locomotion animation to play, whether to show a retreat stance - and deriving that from a
/// hand-copied predicate is how the two drift apart. Same reason <see cref="CharacterMovement.CameraRelativeDir"/>
/// is public.</para>
/// </summary>
public enum MoveSector
{
    /// <summary>Ahead: the command axis is within 45 degrees of the facing direction, inclusive. Full speed, run
    /// honoured. An IDLE command reads as this too - it has no direction, and this is the sector that scales
    /// nothing.</summary>
    Forward,

    /// <summary>Sideways: more than 45 and less than 135 degrees off the facing direction, either side. Scaled by
    /// <see cref="MoveTuning.StrafeSpeedScale"/>, run honoured.</summary>
    Strafe,

    /// <summary>Backwards: 135 degrees or more off the facing direction. Scaled by
    /// <see cref="MoveTuning.BackpedalSpeedScale"/>, and run honoured only when
    /// <see cref="MoveTuning.BackpedalAllowsRun"/> permits it.</summary>
    Reverse,
}
