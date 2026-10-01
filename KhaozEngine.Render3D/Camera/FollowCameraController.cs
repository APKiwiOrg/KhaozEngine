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

        // Whether the camera has orbited since each gesture's live press began. A gesture that crosses its threshold
        // replays its pending travel, which is only fresh movement if nothing orbited the camera during that travel.
        bool _orbitedSinceOrbitPress;
        bool _orbitedSinceLookPress;

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
        /// <see cref="UiBlocked"/> and <see cref="OrbitButton"/> is ignored. The camera orbits only while a gesture
        /// drags, by one delta a frame, so each mouse movement turns it once however many buttons are held. A gesture
        /// that crosses its threshold with no orbit since its press began applies its
        /// <see cref="PointerGesture.DragDelta"/>, the replay of its pending travel (<see cref="LookGesture"/> first
        /// if both cross together). Otherwise the frame's <see cref="InputState.MouseDelta"/> applies. Speed, invert
        /// and sign are as above. Read each gesture's tap after this call, in the same frame.</para>
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

        // Advances each set gesture once, then orbits by at most one delta: a crossing gesture's replay when nothing
        // has orbited since its press began, else this frame's mouse delta while any gesture drags.
        void AdvanceGestures(in InputState input)
        {
            bool orbitCrossed = Step(OrbitGesture, input, ref _orbitedSinceOrbitPress);
            bool lookCrossed = Step(LookGesture, input, ref _orbitedSinceLookPress);
            if (!(IsDragging(OrbitGesture) || IsDragging(LookGesture)))
                return;

            if (lookCrossed && !_orbitedSinceLookPress)
                Orbit(LookGesture!.DragDelta);
            else if (orbitCrossed && !_orbitedSinceOrbitPress)
                Orbit(OrbitGesture!.DragDelta);
            else
                Orbit(input.MouseDelta);
            _orbitedSinceOrbitPress = true;
            _orbitedSinceLookPress = true;
        }

        // Advances one gesture. A press that began this frame clears its orbited-since flag, before this frame's
        // orbit can set it. Returns whether the gesture crossed into a drag this frame.
        bool Step(PointerGesture? gesture, in InputState input, ref bool orbitedSincePress)
        {
            if (gesture is null)
                return false;
            PointerGesturePhase before = gesture.Phase;
            gesture.Advance(input, UiBlocked);
            if (before == PointerGesturePhase.Idle && gesture.Phase != PointerGesturePhase.Idle)
                orbitedSincePress = false;
            return before != PointerGesturePhase.Dragging && gesture.Phase == PointerGesturePhase.Dragging;
        }

        static bool IsDragging(PointerGesture? gesture) => gesture?.Phase == PointerGesturePhase.Dragging;

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
