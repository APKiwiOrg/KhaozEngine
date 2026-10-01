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
    /// this controller only adds deltas. The tuning fields below are feel-tuned, not hardcoded deep.
    /// </summary>
    public sealed class FollowCameraController
    {
        /// <summary>The camera this controller drives.</summary>
        public FollowCamera3D Camera { get; }

        /// <summary>Mouse button that, while held, orbits the camera. Default <see cref="MouseButton.Right"/>
        /// (right-drag to orbit, matching the sibling <see cref="FlyCameraController"/> and the walkable-slice
        /// games, so left-drag stays free for gameplay). Assign <see cref="MouseButton.Left"/> to restore left-drag
        /// orbit. Ignored while <see cref="OrbitGesture"/> or <see cref="LookGesture"/> is set.</summary>
        public MouseButton OrbitButton = MouseButton.Right;
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

        /// <summary>Optional orbit gesture: its drag orbits the camera only, and its quick click is a tap read from
        /// <see cref="PointerGesture.TapThisFrame"/> and <see cref="PointerGesture.TapPosition"/> after
        /// <see cref="Update"/>. Null by default. Setting this or <see cref="LookGesture"/> replaces
        /// <see cref="OrbitButton"/>, which is then ignored.</summary>
        public PointerGesture? OrbitGesture;
        /// <summary>Optional look gesture: its drag orbits the camera and sets <see cref="TurnBodyActive"/>, so the
        /// game turns the body to face the camera. Its quick click is a tap, read as for
        /// <see cref="OrbitGesture"/>. Null by default.</summary>
        public PointerGesture? LookGesture;
        /// <summary>Whether the UI owns the pointer this frame. The game sets it before each <see cref="Update"/>.
        /// A press that begins while blocked never orbits and never taps. Read only while a gesture is set.</summary>
        public bool UiBlocked;

        /// <summary>True while <see cref="LookGesture"/> is dragging: the game turns the body to face the camera.
        /// </summary>
        public bool TurnBodyActive => LookGesture?.Phase == PointerGesturePhase.Dragging;

        /// <summary>True while either gesture is dragging. The game forwards it to the pointer capture request so
        /// the cursor hides and holds for the drag.</summary>
        public bool WantsPointerCapture => TurnBodyActive || OrbitGesture?.Phase == PointerGesturePhase.Dragging;

        public FollowCameraController(FollowCamera3D camera)
        {
            Camera = camera ?? throw new ArgumentNullException(nameof(camera));
        }

        /// <summary>
        /// Apply this frame's drag-orbit and scroll-zoom. While <see cref="OrbitButton"/> is held, the mouse delta
        /// swings <see cref="FollowCamera3D.Yaw"/> (horizontal) and tilts <see cref="FollowCamera3D.Pitch"/>
        /// (vertical), and the wheel scales <see cref="FollowCamera3D.Distance"/>. The default mapping turns the view
        /// the way the hand pulls (drag right turns left, drag down looks up). Flip either axis with
        /// <see cref="InvertX"/> / <see cref="InvertY"/>. Pitch and distance are clamped by the camera. The orbit/zoom
        /// gestures are delta-based, but <paramref name="dt"/> drives the camera's optional target damping
        /// (<see cref="FollowCamera3D.AdvanceTarget"/>) and eased boom recovery
        /// (<see cref="FollowCamera3D.AdvanceBoom"/>), so pass the real frame time. Each is a no-op unless
        /// <see cref="FollowCamera3D.EnableTargetDamping"/> or <see cref="FollowCamera3D.BoomRecoveryRate"/> turns
        /// it on.
        /// <para>With <see cref="OrbitGesture"/> or <see cref="LookGesture"/> set, each set gesture advances once with
        /// <see cref="UiBlocked"/>, <see cref="OrbitButton"/> is ignored, and the orbit comes from the dragging
        /// gestures' <see cref="PointerGesture.DragDelta"/>, summed when both drag, with the same speed, invert and
        /// sign as above. Read each gesture's tap after this call, in the same frame.</para>
        /// </summary>
        public void Update(in InputState input, float dt)
        {
            if (OrbitGesture is null && LookGesture is null)
            {
                if (input.IsDown(OrbitButton))
                    Orbit(input.MouseDelta);
            }
            else
            {
                AdvanceGestures(input);
            }

            float scroll = input.ScrollDelta;
            if (scroll != 0f)
                Camera.Distance *= MathF.Pow(ZoomStep, -scroll);   // setter clamps, and +scroll -> closer

            Camera.AdvanceTarget(dt);   // drives the opt-in target damping (no-op while disabled)
            Camera.AdvanceBoom(dt);     // eases the boom back out after an obstruction clears (no-op at rate 0)
        }

        // Advances each set gesture once and orbits by the sum of the dragging ones' deltas.
        void AdvanceGestures(in InputState input)
        {
            bool dragging = false;
            Vector2 drag = Vector2.Zero;
            if (OrbitGesture is not null)
            {
                OrbitGesture.Advance(input, UiBlocked);
                dragging |= OrbitGesture.Phase == PointerGesturePhase.Dragging;
                drag += OrbitGesture.DragDelta;
            }
            if (LookGesture is not null)
            {
                LookGesture.Advance(input, UiBlocked);
                dragging |= LookGesture.Phase == PointerGesturePhase.Dragging;
                drag += LookGesture.DragDelta;
            }
            if (dragging)
                Orbit(drag);
        }

        void Orbit(Vector2 d)
        {
            float yaw = d.X * OrbitYawSpeed;
            float pitch = d.Y * OrbitPitchSpeed;
            if (InvertX) yaw = -yaw;
            if (InvertY) pitch = -pitch;
            Camera.Yaw -= yaw;
            Camera.Pitch += pitch;   // setter clamps
        }
    }
}
