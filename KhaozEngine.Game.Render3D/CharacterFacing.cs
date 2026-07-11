using System;
using System.Numerics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Game
{
    /// <summary>
    /// Canonical third-person facing helper: derives which way a character should FACE from the player's INTENDED
    /// move direction (camera-relative WASD), not from the collision-resolved velocity. Steering off intent is what
    /// keeps a wall/prop the capsule slides along from swinging or spinning the model - in a tight space, velocity-
    /// steered facing whips around as the capsule is nudged sideways, while intent-steered facing points wherever the
    /// player is pushing regardless of what the collide-and-slide does to the actual motion. The pieces mirror the
    /// exact camera-relative basis <see cref="KhaozEngine.Locomotion.CharacterMovement"/> builds the move from (and
    /// that <see cref="CharacterController3D"/> reads via <see cref="MoveAxis"/>), so facing and travel never diverge.
    /// Pure and headless: no input statics, no render, no physics. Used standalone or inside <see cref="CharacterAvatar"/>.
    /// </summary>
    public static class CharacterFacing
    {
        /// <summary>The raw WASD move axis from the input snapshot: <c>X</c> = D minus A (strafe right), <c>Y</c> = W
        /// minus S (forward). Zero when no move key is held. This is the single source of the WASD mapping shared by
        /// <see cref="CharacterController3D"/> (the move it feeds the sim) and <see cref="IntendedMoveDirection(in InputState, float)"/>
        /// (the direction the model faces), so the two can never drift apart.</summary>
        public static Vector2 MoveAxis(in InputState input)
        {
            Vector2 axis = Vector2.Zero;
            if (input.IsDown(Key.W)) axis.Y += 1f;
            if (input.IsDown(Key.S)) axis.Y -= 1f;
            if (input.IsDown(Key.D)) axis.X += 1f;
            if (input.IsDown(Key.A)) axis.X -= 1f;
            return axis;
        }

        /// <summary>The player's intended horizontal move direction in world XZ: the WASD <see cref="MoveAxis"/>
        /// rotated into the camera basis (forward = the camera look projected onto the ground, right = its right),
        /// exactly the basis the movement sim uses. <see cref="Vector3.Zero"/> when no move key is held. Not
        /// normalized when a diagonal is held (its magnitude is up to sqrt 2), which does not matter to
        /// <see cref="TurnTowards"/> or <see cref="YawOf"/> (both use only its direction); normalize it if a caller
        /// needs a unit vector.</summary>
        public static Vector3 IntendedMoveDirection(in InputState input, float cameraYaw) =>
            IntendedMoveDirection(MoveAxis(input), cameraYaw);

        /// <summary>As <see cref="IntendedMoveDirection(in InputState, float)"/> for a caller that already has (or
        /// synthesizes) a move axis: rotates <paramref name="axis"/> (X = strafe right, Y = forward) into the
        /// camera-relative world XZ basis. Zero axis yields <see cref="Vector3.Zero"/>.</summary>
        public static Vector3 IntendedMoveDirection(Vector2 axis, float cameraYaw)
        {
            if (axis.LengthSquared() <= 1e-6f) return Vector3.Zero;
            float s = MathF.Sin(cameraYaw), c = MathF.Cos(cameraYaw);
            Vector3 forward = new(-s, 0f, -c);
            Vector3 right = new(c, 0f, -s);
            return right * axis.X + forward * axis.Y;
        }

        /// <summary>The facing yaw (radians) that points a model along <paramref name="direction"/> in world XZ, for
        /// building a <c>Matrix4x4.CreateRotationY(yaw)</c> model transform. <c>0</c> for a zero/degenerate direction
        /// (no facing information). The Y component of <paramref name="direction"/> is ignored (facing is planar).</summary>
        public static float YawOf(Vector3 direction)
        {
            if (direction.X * direction.X + direction.Z * direction.Z <= 1e-12f) return 0f;
            return MathF.Atan2(direction.X, direction.Z);
        }

        /// <summary>Turn <paramref name="currentYaw"/> toward facing <paramref name="intendedDirection"/> by at most
        /// <paramref name="maxTurnRate"/> radians per second (times <paramref name="dt"/>), taking the shortest way
        /// round. Returns <paramref name="currentYaw"/> unchanged when the intended direction is zero (no move key), so
        /// a stationary character holds its last facing instead of snapping to a default. Bounding the step means a
        /// one-frame jitter in the intended direction cannot pop the model to a new heading - it eases there. A
        /// non-positive <paramref name="maxTurnRate"/> snaps instantly to the intended yaw.</summary>
        public static float TurnTowards(float currentYaw, Vector3 intendedDirection, float maxTurnRate, float dt)
        {
            if (intendedDirection.X * intendedDirection.X + intendedDirection.Z * intendedDirection.Z <= 1e-6f)
                return currentYaw;
            float target = YawOf(intendedDirection);
            float delta = WrapAngle(target - currentYaw);           // shortest signed angle, in (-pi, pi]
            if (maxTurnRate <= 0f) return WrapAngle(target);        // no cap: snap
            float maxStep = maxTurnRate * dt;
            return WrapAngle(currentYaw + Math.Clamp(delta, -maxStep, maxStep));
        }

        /// <summary>Normalize an angle (radians) to (-pi, pi], so a facing turn always takes the shortest path and a
        /// yaw accumulated over many frames never grows without bound.</summary>
        public static float WrapAngle(float angle)
        {
            angle %= MathF.Tau;
            if (angle > MathF.PI) angle -= MathF.Tau;
            else if (angle <= -MathF.PI) angle += MathF.Tau;
            return angle;
        }
    }
}
