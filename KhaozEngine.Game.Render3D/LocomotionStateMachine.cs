namespace KhaozEngine.Game
{
    /// <summary>The locomotion clip a character plays, chosen each frame from its movement state. Ground states
    /// (<see cref="Idle"/>/<see cref="Walk"/>/<see cref="Run"/>) are picked by horizontal speed; air states
    /// (<see cref="Jump"/>/<see cref="Fall"/>) by the vertical velocity sign while airborne.</summary>
    public enum LocomotionState { Idle, Walk, Run, Jump, Fall }

    /// <summary>Speed thresholds (m/s) that split idle/walk/run. <see cref="WalkSpeed"/> is the dead-zone above which
    /// the character is considered moving; at or above <see cref="RunSpeed"/> it runs. Defaults match the
    /// CharacterController3D walk/run feel (walk ~3, run ~6).</summary>
    public struct LocomotionThresholds
    {
        public float WalkSpeed;
        public float RunSpeed;

        public LocomotionThresholds(float walkSpeed, float runSpeed)
        {
            WalkSpeed = walkSpeed;
            RunSpeed = runSpeed;
        }

        /// <summary>Walk above 0.1 m/s (a small dead-zone so a near-zero residual speed stays Idle), run at/above
        /// 4.5 m/s (between the default 3 m/s walk and 6 m/s run speeds).</summary>
        public static LocomotionThresholds Default => new LocomotionThresholds(0.1f, 4.5f);
    }

    /// <summary>Maps a character's movement state to its locomotion clip. Air state wins over ground speed: while
    /// airborne the character jumps (rising) or falls (descending), regardless of horizontal speed. Pure + headless;
    /// the crossfade between successive states is the <see cref="KhaozEngine.Render3D.AnimationPlayer"/>'s job.</summary>
    public static class LocomotionStateMachine
    {
        public static LocomotionState Evaluate(float horizontalSpeed, bool grounded, float verticalVelocity, LocomotionThresholds thresholds)
        {
            if (!grounded) return verticalVelocity > 0f ? LocomotionState.Jump : LocomotionState.Fall;
            if (horizontalSpeed >= thresholds.RunSpeed) return LocomotionState.Run;
            if (horizontalSpeed >= thresholds.WalkSpeed) return LocomotionState.Walk;
            return LocomotionState.Idle;
        }
    }
}
