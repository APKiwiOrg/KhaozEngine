using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Windowing;

namespace KhaozEngine.Game
{
    /// <summary>
    /// Terrain-agnostic third-person locomotion for the walkable slice. WASD moves the character on the XZ
    /// plane relative to a camera yaw (forward = the camera's look direction projected onto the ground);
    /// diagonals are normalized; left/right shift runs. A thin input adapter over the shared
    /// <see cref="CharacterMovement.Step"/> core (KhaozEngine.Locomotion): the same code runs the local feel
    /// and the networked authoritative/predicted movement, with one <see cref="MoveTuning"/> source of truth.
    /// Reads only the immutable input snapshot; no terrain dependency, no physics beyond ground-clamp.
    /// The speeds and half-height are public fields, feel-tuned later.
    /// </summary>
    public sealed class CharacterController3D
    {
        Vector3 _position;

        /// <summary>Current world position (the capsule centre: ground height + <see cref="CapsuleHalfHeight"/>).</summary>
        public Vector3 Position => _position;

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

        /// <summary>
        /// Advance the character for one frame. <paramref name="cameraYaw"/> is the follow camera's yaw (radians);
        /// <paramref name="groundHeight"/> returns terrain height at (x, z); <paramref name="groundNormal"/> is
        /// optional and, when given, gates moves by slope. Touches no input statics.
        /// </summary>
        public void Update(in InputState input, float dt, float cameraYaw,
                           Func<float, float, float> groundHeight,
                           Func<float, float, Vector3>? groundNormal = null)
        {
            if (groundHeight is null) throw new ArgumentNullException(nameof(groundHeight));

            // Map the input snapshot to a camera-relative move axis and run the shared movement step.
            Vector2 move = Vector2.Zero;
            if (input.IsDown(Key.W)) move.Y += 1f;
            if (input.IsDown(Key.S)) move.Y -= 1f;
            if (input.IsDown(Key.D)) move.X += 1f;
            if (input.IsDown(Key.A)) move.X -= 1f;
            bool run = input.IsDown(Key.LeftShift) || input.IsDown(Key.RightShift);

            var cmd = new MoveCommand(move, run, cameraYaw);
            var tuning = new MoveTuning(WalkSpeed, RunSpeed, CapsuleHalfHeight, MaxSlopeRadians);
            _position = CharacterMovement.Step(_position, cmd, dt, groundHeight, tuning, groundNormal);
        }

        /// <summary>Teleport the character; Y is recomputed from the ground delegate on the next <see cref="Update"/>.</summary>
        public void SetXZ(float x, float z) { _position.X = x; _position.Z = z; }
    }
}
