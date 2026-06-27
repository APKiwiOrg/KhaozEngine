using System;
using System.Numerics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Drives a <see cref="FollowCamera3D"/> from the per-frame <see cref="InputState"/> snapshot: drag the
    /// <see cref="OrbitButton"/> to orbit (yaw/pitch), scroll the wheel to zoom (distance). Touches no input
    /// statics (the snapshot is handed in), so it stays headless-testable. Mirrors
    /// <see cref="IsoCameraController"/>'s role for the iso camera. The camera clamps pitch/distance itself, so
    /// this controller only adds deltas; the tuning fields below are feel-tuned, not hardcoded deep.
    /// </summary>
    public sealed class FollowCameraController
    {
        /// <summary>The camera this controller drives.</summary>
        public FollowCamera3D Camera { get; }

        /// <summary>Mouse button that, while held, orbits the camera. Default <see cref="MouseButton.Left"/>.</summary>
        public MouseButton OrbitButton = MouseButton.Left;
        /// <summary>Radians of yaw applied per pixel of horizontal drag. Default 0.01.</summary>
        public float OrbitYawSpeed = 0.01f;
        /// <summary>Radians of pitch applied per pixel of vertical drag. Default 0.01.</summary>
        public float OrbitPitchSpeed = 0.01f;
        /// <summary>Multiplicative distance factor per unit of scroll. Default 1.1 (scroll up zooms in).</summary>
        public float ZoomStep = 1.1f;
        /// <summary>Invert the horizontal drag axis (yaw). Default false.</summary>
        public bool InvertX = false;
        /// <summary>Invert the vertical drag axis (pitch). Default false.</summary>
        public bool InvertY = false;

        public FollowCameraController(FollowCamera3D camera)
        {
            Camera = camera ?? throw new ArgumentNullException(nameof(camera));
        }

        /// <summary>
        /// Apply this frame's drag-orbit and scroll-zoom. While <see cref="OrbitButton"/> is held, the mouse delta
        /// swings <see cref="FollowCamera3D.Yaw"/> (horizontal) and tilts <see cref="FollowCamera3D.Pitch"/>
        /// (vertical); the wheel scales <see cref="FollowCamera3D.Distance"/>. The default mapping turns the view
        /// the way the hand pulls (drag right turns left, drag down looks up); flip either axis with
        /// <see cref="InvertX"/> / <see cref="InvertY"/>. Pitch and distance are clamped by the camera.
        /// <paramref name="dt"/> is unused (gestures are delta-based) and kept for a uniform controller signature.
        /// </summary>
        public void Update(in InputState input, float dt)
        {
            if (input.IsDown(OrbitButton))
            {
                Vector2 d = input.MouseDelta;
                float yaw = d.X * OrbitYawSpeed;
                float pitch = d.Y * OrbitPitchSpeed;
                if (InvertX) yaw = -yaw;
                if (InvertY) pitch = -pitch;
                Camera.Yaw -= yaw;
                Camera.Pitch += pitch;   // setter clamps
            }

            float scroll = input.ScrollDelta;
            if (scroll != 0f)
                Camera.Distance *= MathF.Pow(ZoomStep, -scroll);   // setter clamps; +scroll -> closer
        }
    }
}
