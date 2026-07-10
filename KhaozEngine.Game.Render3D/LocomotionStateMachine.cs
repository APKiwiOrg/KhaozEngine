namespace KhaozEngine.Game
{
    /// <summary>The locomotion clip a character plays, chosen each frame from its movement state. Ground states
    /// (<see cref="Idle"/>/<see cref="Walk"/>/<see cref="Run"/>) are picked by horizontal speed; air states
    /// (<see cref="Jump"/>/<see cref="Fall"/>) by the vertical velocity sign while airborne; the water states
    /// (<see cref="SwimIdle"/> tread / <see cref="Swim"/> forward) are picked by horizontal speed while the movement
    /// swim flag is set, and win over both ground and air just like air wins over ground. The enum names match the
    /// clip names a consumer bakes (name-based mapping), so the two new water clips are named <c>Swim</c> and
    /// <c>SwimIdle</c>.</summary>
    public enum LocomotionState { Idle, Walk, Run, Jump, Fall, SwimIdle, Swim }

    /// <summary>Speed thresholds (m/s) that split idle/walk/run and, while swimming, tread/forward-swim.
    /// <see cref="WalkSpeed"/> is the dead-zone above which the character is considered moving; at or above
    /// <see cref="RunSpeed"/> it runs. <see cref="SwimForwardThreshold"/> is the dead-zone above which a swimming
    /// character swims forward (<see cref="LocomotionState.Swim"/>) instead of treading water (<see cref="LocomotionState.SwimIdle"/>).
    /// Defaults match the CharacterController3D walk/run feel (walk ~6, run ~12) and a gentle swim dead-zone.</summary>
    public struct LocomotionThresholds
    {
        public float WalkSpeed;
        public float RunSpeed;

        /// <summary>Planar speed (m/s) above which a swimming character plays the forward <see cref="LocomotionState.Swim"/>
        /// clip instead of the tread <see cref="LocomotionState.SwimIdle"/> clip. A single dead-zone (no fast/slow split:
        /// swim has one forward clip, speed-synced) mirroring how <see cref="WalkSpeed"/> gates Idle vs Walk. This is the
        /// ANIMATION tread-vs-forward dead-zone (default 0.1), NOT the swim travel speed - that is the separate
        /// <c>MoveTuning.SwimSpeed</c> (2.5 m/s) in KhaozEngine.Locomotion.</summary>
        public float SwimForwardThreshold;

        public LocomotionThresholds(float walkSpeed, float runSpeed)
            : this(walkSpeed, runSpeed, DefaultSwimForwardThreshold)
        {
        }

        public LocomotionThresholds(float walkSpeed, float runSpeed, float swimForwardThreshold)
        {
            WalkSpeed = walkSpeed;
            RunSpeed = runSpeed;
            SwimForwardThreshold = swimForwardThreshold;
        }

        /// <summary>Default swim-forward dead-zone (m/s): tread below, swim forward at/above. 0.1 matches the walk
        /// dead-zone so a near-zero residual planar speed treads water rather than flickering into the forward stroke.</summary>
        public const float DefaultSwimForwardThreshold = 0.1f;

        /// <summary>Walk above 0.1 m/s (a small dead-zone so a near-zero residual speed stays Idle), run at/above
        /// 9 m/s (between the default 6 m/s walk and 12 m/s run speeds); swim forward above 0.1 m/s.</summary>
        public static LocomotionThresholds Default => new LocomotionThresholds(0.1f, 9f, DefaultSwimForwardThreshold);
    }

    /// <summary>Maps a character's movement state to its locomotion clip. Swim wins over both ground and air (a
    /// swimming character is neither walking nor falling): while the swim flag is set the character swims forward
    /// (<see cref="LocomotionState.Swim"/>) above <see cref="LocomotionThresholds.SwimForwardThreshold"/> or treads water
    /// (<see cref="LocomotionState.SwimIdle"/>) below it, regardless of grounded/vertical. Otherwise air state wins
    /// over ground speed: while airborne the character jumps (rising) or falls (descending), regardless of horizontal
    /// speed. Pure + headless; the crossfade between successive states is the
    /// <see cref="KhaozEngine.Render3D.AnimationPlayer"/>'s job. The swim FLAG is threaded from the movement medium
    /// (the replicated <c>MovementState.Swimming</c> bit), never re-queried from water here.</summary>
    public static class LocomotionStateMachine
    {
        /// <summary>Overload without the swim flag: never swims. Kept so pre-swim callers compile bit-identically
        /// (swimming defaults to false).</summary>
        public static LocomotionState Evaluate(float horizontalSpeed, bool grounded, float verticalVelocity, LocomotionThresholds thresholds) =>
            Evaluate(horizontalSpeed, grounded, verticalVelocity, swimming: false, thresholds);

        public static LocomotionState Evaluate(float horizontalSpeed, bool grounded, float verticalVelocity, bool swimming, LocomotionThresholds thresholds)
        {
            if (swimming) return horizontalSpeed >= thresholds.SwimForwardThreshold ? LocomotionState.Swim : LocomotionState.SwimIdle;
            if (!grounded) return verticalVelocity > 0f ? LocomotionState.Jump : LocomotionState.Fall;
            if (horizontalSpeed >= thresholds.RunSpeed) return LocomotionState.Run;
            if (horizontalSpeed >= thresholds.WalkSpeed) return LocomotionState.Walk;
            return LocomotionState.Idle;
        }
    }
}
