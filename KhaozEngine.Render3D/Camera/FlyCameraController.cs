using System;
using System.Numerics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Editor fly-cam gesture policy over the per-frame <see cref="InputState"/> snapshot, mirroring
    /// <see cref="FollowCameraController"/>: hold <see cref="LookButton"/> to mouselook (plain per-frame
    /// <see cref="InputState.MouseDelta"/> scaled by <see cref="LookSpeed"/>, the engine has no pointer lock),
    /// WASD to fly along the view direction (W/S follow <see cref="FlyCamera3D.Forward"/> with pitch, so it is
    /// true flight; A/D strafe along the horizontal right vector), E/Q to rise/sink on world +Y, hold
    /// <see cref="Key.LeftShift"/> to sprint, and the wheel to scale <see cref="MoveSpeed"/>. Look convention is
    /// standard first-person editor feel: drag right looks right (toward the A/D strafe-right vector) and drag up
    /// looks up, with <see cref="InvertX"/> / <see cref="InvertY"/> to flip either axis. Touches no input
    /// statics (the snapshot is handed in), owns no smoothing: dt-scaled direct integration, deterministic, and
    /// allocation-free per frame.
    /// </summary>
    public sealed class FlyCameraController
    {
        /// <summary>The camera this controller drives.</summary>
        public FlyCamera3D Camera { get; }

        /// <summary>Mouse button that, while held, enables mouselook. Default <see cref="MouseButton.Right"/>.</summary>
        public MouseButton LookButton { get; set; } = MouseButton.Right;
        /// <summary>Radians of look applied per pixel of mouse movement. Default 0.005.</summary>
        public float LookSpeed { get; set; } = 0.005f;
        /// <summary>Flight speed in world units per second, scaled by <see cref="SpeedWheelStep"/> on the wheel and
        /// clamped to [<see cref="MinMoveSpeed"/>, <see cref="MaxMoveSpeed"/>]. Default 12.</summary>
        public float MoveSpeed { get; set; } = 12f;
        /// <summary>Lower clamp for <see cref="MoveSpeed"/>. Default 0.5.</summary>
        public float MinMoveSpeed { get; set; } = 0.5f;
        /// <summary>Upper clamp for <see cref="MoveSpeed"/>. Default 200.</summary>
        public float MaxMoveSpeed { get; set; } = 200f;
        /// <summary>Multiplicative factor applied to <see cref="MoveSpeed"/> per wheel notch. Default 1.25.</summary>
        public float SpeedWheelStep { get; set; } = 1.25f;
        /// <summary>Speed multiplier while <see cref="Key.LeftShift"/> is held. Default 3.</summary>
        public float SprintMultiplier { get; set; } = 3f;
        /// <summary>Invert the horizontal look axis (yaw), mirroring <see cref="FollowCameraController.InvertX"/>.
        /// Default false (drag right looks right).</summary>
        public bool InvertX { get; set; }
        /// <summary>Invert the vertical look axis (pitch). Default false (drag up looks up).</summary>
        public bool InvertY { get; set; }

        public FlyCameraController(FlyCamera3D camera)
        {
            Camera = camera ?? throw new ArgumentNullException(nameof(camera));
        }

        /// <summary>
        /// Apply this frame's mouselook, wheel speed scaling, and WASD/EQ flight. While
        /// <see cref="LookButton"/> is held the mouse delta swings <see cref="FlyCamera3D.Yaw"/> (horizontal,
        /// drag right looks right, toward the strafe-right vector) and tilts <see cref="FlyCamera3D.Pitch"/>
        /// (vertical, drag up looks up, clamped by the camera); flip either axis with <see cref="InvertX"/> /
        /// <see cref="InvertY"/>. The wheel multiplies <see cref="MoveSpeed"/> by <see cref="SpeedWheelStep"/> per
        /// notch, clamped. Held movement keys translate <see cref="FlyCamera3D.Position"/> by
        /// <see cref="MoveSpeed"/> times <paramref name="dt"/> (times <see cref="SprintMultiplier"/> while shift
        /// is down) along the view basis. No input means no change.
        /// </summary>
        public void Update(in InputState input, float dt)
        {
            // Mouselook while the look button is held (no pointer lock: plain per-frame delta).
            if (input.IsDown(LookButton))
            {
                Vector2 d = input.MouseDelta;
                float yawDelta = d.X * LookSpeed;
                float pitchDelta = -d.Y * LookSpeed;   // mouse up (negative y) looks up
                if (InvertX) yawDelta = -yawDelta;
                if (InvertY) pitchDelta = -pitchDelta;
                Camera.Yaw -= yawDelta;                // yaw decreasing turns toward strafe right (+Z-forward basis)
                Camera.Pitch += pitchDelta;            // setter clamps
            }

            // Wheel scales the flight speed multiplicatively per notch, clamped.
            float scroll = input.ScrollDelta;
            if (scroll != 0f)
                MoveSpeed = Math.Clamp(MoveSpeed * MathF.Pow(SpeedWheelStep, scroll), MinMoveSpeed, MaxMoveSpeed);

            // Accumulate the flight direction from held keys, then integrate once (no per-frame allocation).
            Vector3 fwd = Camera.Forward;
            Vector3 right = new Vector3(-fwd.Z, 0f, fwd.X);   // horizontal right = cross(Forward, +Y)
            Vector3 move = Vector3.Zero;
            if (input.IsDown(Key.W)) move += fwd;
            if (input.IsDown(Key.S)) move -= fwd;
            if (input.IsDown(Key.D)) move += right;
            if (input.IsDown(Key.A)) move -= right;
            if (input.IsDown(Key.E)) move += Vector3.UnitY;
            if (input.IsDown(Key.Q)) move -= Vector3.UnitY;

            if (move != Vector3.Zero)
            {
                float speed = MoveSpeed;
                if (input.IsDown(Key.LeftShift)) speed *= SprintMultiplier;
                Camera.Position += Vector3.Normalize(move) * (speed * dt);
            }
        }
    }
}
