using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Game
{
    /// <summary>
    /// Terrain-agnostic third-person locomotion for the walkable slice. WASD moves the character on the XZ
    /// plane relative to a camera yaw (forward = the camera's look direction projected onto the ground);
    /// diagonals are normalized; left/right shift runs; Space jumps. A thin input adapter over the shared
    /// vertical <see cref="CharacterMovement.Step(in MoveState, in MoveCommand, float, Func{float, float, float}, in MoveTuning, Func{float, float, Vector3}?, IPhysicsWorld?, Func{float, float, Vector2}?, Func{float, float, float, MovementMedium}?)"/>
    /// core (KhaozEngine.Locomotion): the same code runs the local feel and the networked authoritative/predicted
    /// movement, with one <see cref="MoveTuning"/> source of truth. Reads only the immutable input snapshot; no
    /// terrain dependency, no physics beyond gravity + ground contact. Speeds, half-height, and the vertical-feel
    /// constants are public fields, feel-tuned later.
    /// </summary>
    public sealed class CharacterController3D
    {
        MoveState _state;

        /// <summary>Current world position (the capsule centre: ground height + <see cref="CapsuleHalfHeight"/> while grounded).</summary>
        public Vector3 Position => _state.Position;

        /// <summary>True while the capsule is resting on the ground (false while jumping or falling).</summary>
        public bool Grounded => _state.Grounded;

        /// <summary>Current vertical velocity (m/s, positive up).</summary>
        public float VerticalVelocity => _state.VerticalVelocity;

        /// <summary>True while the capsule is surface-swimming: submersion has crossed the swim-enter threshold via a
        /// fluid-medium provider (see the <c>medium</c> parameter of <see cref="Update"/>). Always false when no medium
        /// is supplied (dry land). Feed this to an <see cref="AnimatedCharacter"/> so a swimming character plays the
        /// swim/tread clips instead of walk/fall.</summary>
        public bool Swimming => _state.Swimming;

        /// <summary>Exact signed step-climb rate in m/s this tick (see <see cref="MoveState.ClimbRate"/>): positive while
        /// ascending a continuous paced stair run, negative while descending stepped risers, 0 when not on a step climb.
        /// The sim's OWN fact, not a position-delta estimate - feed it straight to a presentation smoother (e.g.
        /// <see cref="CharacterSample.ClimbRate"/> for <see cref="ReplicatedCharacterAnimators"/>'s signal-driven stair
        /// glide) instead of deriving a climb rate from successive <see cref="Position"/> samples.</summary>
        public float ClimbRate => _state.ClimbRate;

        /// <summary>Signed vertical delta (metres) a DISCRETE step committed THIS tick (see <see cref="MoveState.StepDeltaY"/>):
        /// an isolated step-up seat or a step-down grounded-hold, 0 on every other tick. Mutually exclusive with
        /// <see cref="ClimbRate"/> per tick. A caller driving the UE-style step-event mesh smoothing accumulates this into
        /// a running sum (mirroring <c>ClientPrediction.StepCumulativeY</c>) and feeds it as
        /// <see cref="CharacterSample.StepCumulativeY"/> so isolated steps (a doorstep, a curb, the first riser of a run)
        /// ease instead of popping.</summary>
        public float StepDeltaY => _state.StepDeltaY;

        /// <summary>Metres per second while walking. Default 6.</summary>
        public float WalkSpeed = 6f;
        /// <summary>Metres per second while running (shift held). Default 12.</summary>
        public float RunSpeed = 12f;
        /// <summary>Half the capsule height, added to the ground so the feet sit on the ground. Default 0.9 (a 1.8 m capsule).</summary>
        public float CapsuleHalfHeight = 0.9f;
        /// <summary>Reject a step onto ground steeper than this (angle between surface normal and +Y), when a
        /// ground-normal delegate is supplied. Default 45 deg (matches <see cref="MoveTuning.Default"/>: walkable
        /// for normal hills, low enough that a RimFeature mountain wall is rejected).</summary>
        public float MaxSlopeRadians = MathF.PI * 45f / 180f;

        /// <summary>Capsule footprint radius for static-world collision (metres). Default 0.4.</summary>
        public float CapsuleRadius = 0.4f;

        /// <summary>Gravity acceleration magnitude (m/s^2). Default 25.</summary>
        public float Gravity = 25f;
        /// <summary>Jump launch velocity (m/s). Default 9.79796 (= 8 * sqrt(1.5), +50% apex vs the old 8f: apex
        /// ~1.92 m at <see cref="Gravity"/> 25), matching Ruinborne's deliberate jump-height value.</summary>
        public float JumpSpeed = 9.79796f; // = 8 * sqrt(1.5), +50% apex vs the old 8f, matches Ruinborne's deliberate value
        /// <summary>Terminal fall speed (m/s). Default 50.</summary>
        public float MaxFallSpeed = 50f;
        /// <summary>Coyote-time window (seconds): jump still fires shortly after leaving the ground. Default 0.1.</summary>
        public float CoyoteTime = 0.1f;
        /// <summary>Jump-buffer window (seconds): a jump pressed just before landing fires on contact. Default 0.1.</summary>
        public float JumpBuffer = 0.1f;
        /// <summary>Horizontal control while airborne (1 = full). Default 1.</summary>
        public float AirControl = 1f;
        /// <summary>Grounded skin (metres) so a downhill run does not jitter grounded/airborne. Default 0.3.</summary>
        public float GroundedEpsilon = 0.3f;
        /// <summary>Max upward support rise (metres) auto-mounted while grounded without a jump (a low rock/curb).
        /// Default 0.4.</summary>
        public float StepHeight = 0.4f;
        /// <summary>Max vertical climb speed (m/s) a step-up mount rises at, so a stair run ascends at a steady
        /// walking pace instead of snapping up a whole riser per tick (see <see cref="MoveTuning.MaxStepClimbSpeed"/>).
        /// Default 3.5. A single low curb still mounts in one tick; a value &lt;= 0 disables the limit.</summary>
        public float MaxStepClimbSpeed = 3.5f;
        /// <summary>Opt in to AIRBORNE horizontal momentum: a jump travels its whole arc at the speed it launched at,
        /// and <see cref="AirControl"/> steers that arc rather than scaling it (see
        /// <see cref="MoveTuning.AirMomentum"/>). Default false, which is the pre-momentum jump exactly. Grounded
        /// motion is untouched either way.</summary>
        public bool AirMomentum = false;
        /// <summary>Rate (m/s^2) at which a conserved airborne speed bleeds toward a slower commanded speed while
        /// <see cref="AirMomentum"/> is on, stopping there and never going below it (see
        /// <see cref="MoveTuning.AirBrakeAccel"/>). Default 0 (pure conservation).</summary>
        public float AirBrakeAccel = 0f;
        /// <summary>Maximum rate (radians per second) the heading turns toward the commanded travel direction, taking
        /// the shortest arc (see <see cref="MoveTuning.FacingTurnSpeed"/>). Default
        /// <see cref="float.PositiveInfinity"/>, which snaps in one tick and is the presentation feel every
        /// pre-facing consumer already had. A finite value (2-10 rad/s is the usual range) leans the body into its
        /// turns. 0 freezes the heading.</summary>
        public float FacingTurnSpeed = float.PositiveInfinity;
        /// <summary>How far past <see cref="MaxSlopeRadians"/> a character that ALREADY has footing keeps it (see
        /// <see cref="MoveTuning.TractionHysteresisRadians"/>). Default 3 deg, so a walk across ground that straddles
        /// the gate holds one continuous footing decision instead of flipping grip and slide every tick. 0 restores
        /// the bare per-tick threshold of every release before 17.30.0.</summary>
        public float TractionHysteresisRadians = MathF.PI * 3f / 180f;
        /// <summary>The band past <see cref="MaxSlopeRadians"/> over which a slide's fall-line acceleration ramps in
        /// from nothing to full gravity (see <see cref="MoveTuning.SlideFrictionRampRadians"/>). Default 8 deg, so a
        /// face a degree too steep to stand on slides gently and only a genuinely steep one slides hard. 0 restores
        /// the full-strength slide of 17.28.0 and 17.29.0.</summary>
        public float SlideFrictionRampRadians = MathF.PI * 8f / 180f;
        /// <summary>Speed multiplier while strafing with the character pinned to the camera (see
        /// <see cref="MoveTuning.StrafeSpeedScale"/>). Default 1 (no scaling). Inert on this controller as it stands,
        /// which drives movement without <see cref="MoveCommand.FaceCamera"/>: it is mirrored because every
        /// <see cref="MoveTuning"/> feel knob is, and because a consumer reads these defaults as the engine's
        /// answer to "what is neutral".</summary>
        public float StrafeSpeedScale = 1f;
        /// <summary>Speed multiplier while backing up with the character pinned to the camera (see
        /// <see cref="MoveTuning.BackpedalSpeedScale"/>). Default 1 (no scaling).</summary>
        public float BackpedalSpeedScale = 1f;
        /// <summary>Whether the run bit is honoured while backing up (see
        /// <see cref="MoveTuning.BackpedalAllowsRun"/>). Default true, which honours it.</summary>
        public bool BackpedalAllowsRun = true;

        /// <summary>
        /// Advance the character for one frame. <paramref name="cameraYaw"/> is the follow camera's yaw (radians);
        /// <paramref name="groundHeight"/> returns terrain height at (x, z); <paramref name="groundNormal"/> is
        /// optional and, when given, gates moves by slope; <paramref name="physics"/> is optional and, when given,
        /// resolves the capsule against static props/buildings via the <see cref="IPhysicsWorld"/> seam (the same
        /// world the authoritative server and client prediction run, so feel and collision match). Space (just
        /// pressed) requests a jump. <paramref name="medium"/> is optional and, when given, scales horizontal speed by
        /// the submersion-depth wade ramp (a lake/river/swamp the game reads), matching the networked wade; null =
        /// dry land everywhere. Touches no input statics.
        /// </summary>
        public void Update(in InputState input, float dt, float cameraYaw,
                           Func<float, float, float> groundHeight,
                           Func<float, float, Vector3>? groundNormal = null,
                           IPhysicsWorld? physics = null,
                           Func<float, float, float, MovementMedium>? medium = null)
        {
            if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));

            // Map the input snapshot to a camera-relative move axis + jump and run the shared movement step. The WASD
            // axis comes from CharacterFacing.MoveAxis - the same mapping the facing helper reads - so the direction
            // the capsule MOVES and the direction a CharacterAvatar FACES are built from one source and cannot drift.
            Vector2 move = CharacterFacing.MoveAxis(input);
            bool run = input.IsDown(Key.LeftShift) || input.IsDown(Key.RightShift);
            bool jump = input.WasPressed(Key.Space);   // edge-triggered: one jump per press (buffer handles timing)

            var cmd = new MoveCommand(move, run, cameraYaw, jump);
            var tuning = new MoveTuning(WalkSpeed, RunSpeed, CapsuleHalfHeight, MaxSlopeRadians, CapsuleRadius)
            {
                Gravity = Gravity, JumpSpeed = JumpSpeed, MaxFallSpeed = MaxFallSpeed,
                CoyoteTime = CoyoteTime, JumpBuffer = JumpBuffer, AirControl = AirControl,
                GroundedEpsilon = GroundedEpsilon, StepHeight = StepHeight,
                MaxStepClimbSpeed = MaxStepClimbSpeed,
                AirMomentum = AirMomentum, AirBrakeAccel = AirBrakeAccel,
                FacingTurnSpeed = FacingTurnSpeed,
                TractionHysteresisRadians = TractionHysteresisRadians,
                SlideFrictionRampRadians = SlideFrictionRampRadians,
                StrafeSpeedScale = StrafeSpeedScale,
                BackpedalSpeedScale = BackpedalSpeedScale,
                BackpedalAllowsRun = BackpedalAllowsRun,
            };
            _state = CharacterMovement.Step(_state, cmd, dt, groundHeight, tuning, groundNormal, world: physics, medium: medium);
        }

        /// <summary>Teleport the character; Y/vertical state re-settle from the ground delegate on the next <see cref="Update"/>.</summary>
        public void SetXZ(float x, float z) { _state.Position.X = x; _state.Position.Z = z; }
    }
}
