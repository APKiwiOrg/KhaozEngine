using System;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Game
{
    /// <summary>Opt-in speed-synced locomotion playback for an <see cref="AnimatedCharacter"/>. When
    /// <see cref="Enabled"/>, a MOVE clip (Walk/Run on the ground, or the forward <see cref="LocomotionState.Swim"/>
    /// stroke in water) advances in proportion to how fast the character is actually moving instead of at the clip's
    /// authored rate, so its feet (or its stroke) stop sliding ("gliding") whenever the world speed differs from the
    /// speed the clip was authored to move at. Idle, the tread <see cref="LocomotionState.SwimIdle"/>, and the air
    /// states (Jump/Fall) always play at 1x.
    ///
    /// <para>The multiplier for a move state is <c>clamp(horizontalSpeed / referenceSpeedForState,
    /// <see cref="MinMultiplier"/>, <see cref="MaxMultiplier"/>)</c>, where the reference speed is the m/s the clip
    /// was authored to move the character at (<see cref="WalkClipSpeed"/> for Walk, <see cref="RunClipSpeed"/> for
    /// Run). The clamp keeps a near-stationary entity from freezing the clip and a teleporting one from
    /// fast-forwarding it.</para>
    ///
    /// <para>Default (<c>default</c> / <see cref="Disabled"/>) is OFF, so every existing consumer plays exactly as
    /// before until it opts in. Build an enabled config with <see cref="Enable"/>.</para></summary>
    public struct LocomotionSpeedSync
    {
        /// <summary>Default lower bound on the playback multiplier so a near-stationary entity does not freeze the
        /// clip. Used when <see cref="MinMultiplier"/> is left at 0.</summary>
        public const float DefaultMinMultiplier = 0.25f;

        /// <summary>Default upper bound on the playback multiplier so a teleporting entity does not fast-forward the
        /// clip. Used when <see cref="MaxMultiplier"/> is left at 0.</summary>
        public const float DefaultMaxMultiplier = 3.0f;

        /// <summary>Master opt-in. When false (the default) <see cref="RateFor(LocomotionState, float)"/> always
        /// returns 1 and playback is byte-identical to the pre-speed-sync behaviour.</summary>
        public bool Enabled;

        /// <summary>World speed (m/s) the Walk clip was authored to move the character at. The Walk clip advances at
        /// <c>horizontalSpeed / WalkClipSpeed</c> (clamped). 0 (unset) plays Walk at 1x even when enabled.</summary>
        public float WalkClipSpeed;

        /// <summary>World speed (m/s) the Run clip was authored to move the character at. The Run clip advances at
        /// <c>horizontalSpeed / RunClipSpeed</c> (clamped). 0 (unset) plays Run at 1x even when enabled.</summary>
        public float RunClipSpeed;

        /// <summary>World speed (m/s) the forward <see cref="LocomotionState.Swim"/> clip was authored to move the
        /// character at. The Swim clip advances at <c>horizontalSpeed / SwimClipSpeed</c> (clamped). 0 (unset) plays
        /// Swim at 1x even when enabled. The tread <see cref="LocomotionState.SwimIdle"/> always plays at 1x.</summary>
        public float SwimClipSpeed;

        /// <summary>Opt-in: play the move clip BACKWARDS when the sample's movement falls in
        /// <see cref="MoveSector.Reverse"/>. Off by default, so every existing consumer is byte-identical until it opts
        /// in (and a consumer that never classifies a sector sees <see cref="MoveSector.Forward"/> on every sample, so
        /// this changes nothing for it even when set). See the sign rule on <see cref="RateFor(LocomotionState, float,
        /// MoveSector)"/>.</summary>
        public bool ReverseOnReverseSector;

        /// <summary>Lower clamp on the multiplier. 0 (unset) uses <see cref="DefaultMinMultiplier"/>.</summary>
        public float MinMultiplier;

        /// <summary>Upper clamp on the multiplier. 0 (unset) uses <see cref="DefaultMaxMultiplier"/>.</summary>
        public float MaxMultiplier;

        /// <summary>The disabled config (playback unchanged). Same as <c>default</c>.</summary>
        public static LocomotionSpeedSync Disabled => default;

        /// <summary>Build an enabled config from the Walk/Run (and optional forward-Swim) clips' authored move speeds
        /// (m/s). <paramref name="swimClipSpeed"/> defaults to 0 (Swim plays at 1x) so a pre-swim caller is unchanged.
        /// The clamp bounds default to <see cref="DefaultMinMultiplier"/>..<see cref="DefaultMaxMultiplier"/>.
        /// <paramref name="reverseOnReverseSector"/> defaults to false (no reverse playback) so a pre-reverse caller is
        /// unchanged.</summary>
        public static LocomotionSpeedSync Enable(float walkClipSpeed, float runClipSpeed,
            float minMultiplier = DefaultMinMultiplier, float maxMultiplier = DefaultMaxMultiplier,
            float swimClipSpeed = 0f, bool reverseOnReverseSector = false) =>
            new LocomotionSpeedSync
            {
                Enabled = true,
                WalkClipSpeed = walkClipSpeed,
                RunClipSpeed = runClipSpeed,
                SwimClipSpeed = swimClipSpeed,
                MinMultiplier = minMultiplier,
                MaxMultiplier = maxMultiplier,
                ReverseOnReverseSector = reverseOnReverseSector,
            };

        /// <summary>The playback rate multiplier for <paramref name="state"/> at <paramref name="horizontalSpeed"/>
        /// m/s, for a forward-sector move. Returns 1 when disabled, for Idle/SwimIdle/Jump/Fall, or when the state's
        /// reference speed is unset (&lt;= 0), and otherwise <c>clamp(horizontalSpeed / referenceSpeed, min, max)</c>.
        /// The pre-sector overload, kept so existing callers compile bit-identically.</summary>
        public readonly float RateFor(LocomotionState state, float horizontalSpeed) =>
            RateFor(state, horizontalSpeed, MoveSector.Forward);

        /// <summary>The playback rate multiplier for <paramref name="state"/> at <paramref name="horizontalSpeed"/> m/s
        /// (a MAGNITUDE) moving in <paramref name="sector"/>. The magnitude rule is unchanged: 1 when disabled, for
        /// Idle/SwimIdle/Jump/Fall, or when the state's reference speed is unset (&lt;= 0), and otherwise
        /// <c>clamp(horizontalSpeed / referenceSpeed, min, max)</c>.
        ///
        /// <para>The SIGN is applied last, and only to a move state that actually syncs: with
        /// <see cref="ReverseOnReverseSector"/> set and <paramref name="sector"/> == <see cref="MoveSector.Reverse"/>
        /// the clamped magnitude is NEGATED, which runs the clip backwards through
        /// <c>AnimationPlayer.Update(dt, rate)</c> (a looping clip wraps cleanly through zero). Order matters: the
        /// clamp bounds the MAGNITUDE, so <see cref="MinMultiplier"/> still floors a near-stationary backpedal at
        /// -0.25x rather than freezing it, and <see cref="MaxMultiplier"/> still ceilings a fast one at -3x. Every
        /// state that plays at 1x plays at +1x whatever the sector: idle, the tread, and the air states have no
        /// direction to reverse.</para></summary>
        public readonly float RateFor(LocomotionState state, float horizontalSpeed, MoveSector sector)
        {
            if (!Enabled) return 1f;
            float reference = state switch
            {
                LocomotionState.Walk => WalkClipSpeed,
                LocomotionState.Run => RunClipSpeed,
                LocomotionState.Swim => SwimClipSpeed,
                _ => 0f,   // Idle + tread (SwimIdle) + air states (Jump/Fall) always play at 1x
            };
            if (reference <= 0f) return 1f;   // unset reference -> avoid divide-by-zero, play at 1x
            float min = MinMultiplier > 0f ? MinMultiplier : DefaultMinMultiplier;
            float max = MaxMultiplier > 0f ? MaxMultiplier : DefaultMaxMultiplier;
            if (max < min) max = min;
            float rate = Math.Clamp(horizontalSpeed / reference, min, max);
            // Sign LAST, on the already-clamped magnitude: the bounds are a rate-of-play contract, not a direction one,
            // so a crawling backpedal still floors at -min instead of being clamped up to +min (a frozen clip), and a
            // fast one still ceilings at -max. Only a syncing move state reaches here, so idle/tread/air keep +1.
            return ReverseOnReverseSector && sector == MoveSector.Reverse ? -rate : rate;
        }
    }
}
