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
    /// vertical <see cref="CharacterMovement.Step(in MoveState, in MoveCommand, float, Func{float, float, float}, in MoveTuning, Func{float, float, Vector3}?, IPhysicsWorld?, Func{float, float, Vector2}?)"/>
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

        /// <summary>Metres per second while walking. Default 3.</summary>
        public float WalkSpeed = 3f;
        /// <summary>Metres per second while running (shift held). Default 6.</summary>
        public float RunSpeed = 6f;
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
        /// <summary>Jump launch velocity (m/s). Default 8 (apex ~1.28 m).</summary>
        public float JumpSpeed = 8f;
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

        /// <summary>
        /// Advance the character for one frame. <paramref name="cameraYaw"/> is the follow camera's yaw (radians);
        /// <paramref name="groundHeight"/> returns terrain height at (x, z); <paramref name="groundNormal"/> is
        /// optional and, when given, gates moves by slope; <paramref name="physics"/> is optional and, when given,
        /// resolves the capsule against static props/buildings via the <see cref="IPhysicsWorld"/> seam (the same
        /// world the authoritative server and client prediction run, so feel and collision match). Space (just
        /// pressed) requests a jump. Touches no input statics.
        /// </summary>
        public void Update(in InputState input, float dt, float cameraYaw,
                           Func<float, float, float> groundHeight,
                           Func<float, float, Vector3>? groundNormal = null,
                           IPhysicsWorld? physics = null)
        {
            if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));

            // Map the input snapshot to a camera-relative move axis + jump and run the shared movement step.
            Vector2 move = Vector2.Zero;
            if (input.IsDown(Key.W)) move.Y += 1f;
            if (input.IsDown(Key.S)) move.Y -= 1f;
            if (input.IsDown(Key.D)) move.X += 1f;
            if (input.IsDown(Key.A)) move.X -= 1f;
            bool run = input.IsDown(Key.LeftShift) || input.IsDown(Key.RightShift);
            bool jump = input.WasPressed(Key.Space);   // edge-triggered: one jump per press (buffer handles timing)

            var cmd = new MoveCommand(move, run, cameraYaw, jump);
            var tuning = new MoveTuning(WalkSpeed, RunSpeed, CapsuleHalfHeight, MaxSlopeRadians, CapsuleRadius)
            {
                Gravity = Gravity, JumpSpeed = JumpSpeed, MaxFallSpeed = MaxFallSpeed,
                CoyoteTime = CoyoteTime, JumpBuffer = JumpBuffer, AirControl = AirControl,
                GroundedEpsilon = GroundedEpsilon, StepHeight = StepHeight,
            };
            _state = CharacterMovement.Step(_state, cmd, dt, groundHeight, tuning, groundNormal, world: physics);
        }

        /// <summary>Teleport the character; Y/vertical state re-settle from the ground delegate on the next <see cref="Update"/>.</summary>
        public void SetXZ(float x, float z) { _state.Position.X = x; _state.Position.Z = z; }
    }
}
