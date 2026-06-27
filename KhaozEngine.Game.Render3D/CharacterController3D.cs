using System;
using System.Numerics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Game
{
    /// <summary>
    /// Terrain-agnostic third-person locomotion for the walkable slice. WASD moves the character on the XZ plane
    /// relative to a camera yaw (forward = the camera's look direction projected onto the ground); diagonals are
    /// normalized; left/right shift runs. Each frame the Y is clamped onto a caller-supplied ground-height
    /// delegate (plus a capsule half-height so the feet sit on the ground), and an optional ground-normal delegate
    /// rejects a step onto terrain steeper than <see cref="MaxSlopeRadians"/>. Pure System.Numerics + the input
    /// snapshot: no reference to KhaozEngine.Terrain, no physics beyond ground-clamp (no jump/gravity). The speeds
    /// and half-height are public fields, feel-tuned later.
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
        /// ground-normal delegate is supplied. Default ~50 deg.</summary>
        public float MaxSlopeRadians = MathF.PI * 50f / 180f;

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

            // Camera-relative ground basis (matches FollowCamera3D's yaw convention).
            float sY = MathF.Sin(cameraYaw), cY = MathF.Cos(cameraYaw);
            Vector3 forward = new(-sY, 0f, -cY);
            Vector3 right = new(cY, 0f, -sY);

            Vector3 move = Vector3.Zero;
            if (input.IsDown(Key.W)) move += forward;
            if (input.IsDown(Key.S)) move -= forward;
            if (input.IsDown(Key.D)) move += right;
            if (input.IsDown(Key.A)) move -= right;
            if (move.LengthSquared() > 1e-6f)
            {
                move = Vector3.Normalize(move);   // normalized diagonals
                float speed = (input.IsDown(Key.LeftShift) || input.IsDown(Key.RightShift)) ? RunSpeed : WalkSpeed;
                float nx = _position.X + move.X * speed * dt;
                float nz = _position.Z + move.Z * speed * dt;

                bool blocked = false;
                if (groundNormal is not null)
                {
                    float ny = Math.Clamp(groundNormal(nx, nz).Y, 0f, 1f);
                    if (MathF.Acos(ny) > MaxSlopeRadians) blocked = true;
                }
                if (!blocked) { _position.X = nx; _position.Z = nz; }
            }

            _position.Y = groundHeight(_position.X, _position.Z) + CapsuleHalfHeight;
        }

        /// <summary>Teleport the character; Y is recomputed from the ground delegate on the next <see cref="Update"/>.</summary>
        public void SetXZ(float x, float z) { _position.X = x; _position.Z = z; }
    }
}
