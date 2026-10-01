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
        /// <see cref="OrbitTap"/> after <see cref="Update"/>, with the press origin in
        /// <see cref="PointerGesture.TapPosition"/>. Null by default. Setting this or <see cref="LookGesture"/>
        /// replaces <see cref="OrbitButton"/>, which is then ignored.</summary>
        public PointerGesture? OrbitGesture;
        /// <summary>Optional look gesture: its drag orbits the camera and sets <see cref="TurnBodyActive"/>, so the
        /// game turns the body to face the camera. Its quick click is a tap read from <see cref="LookTap"/>, with
        /// the press origin in <see cref="PointerGesture.TapPosition"/>. Null by default.</summary>
        public PointerGesture? LookGesture;
        /// <summary>Whether the UI owns the pointer this frame. The game sets it before each <see cref="Update"/>.
        /// A press that begins while blocked never taps, and never orbits while the gesture's threshold is above zero.
        /// At a zero or negative threshold a held button orbits again on the first unblocked frame. Read only while a
        /// gesture is set.</summary>
        public bool UiBlocked;

        /// <summary>True while <see cref="LookGesture"/> is dragging: the game turns the body to face the camera.
        /// </summary>
        public bool TurnBodyActive => LookGesture?.Phase == PointerGesturePhase.Dragging;

        /// <summary>True on the frame of the last <see cref="Update"/> when <see cref="OrbitGesture"/> tapped and its
        /// press did not begin while <see cref="LookGesture"/> was dragging. Read taps here rather than from
        /// <see cref="PointerGesture.TapThisFrame"/>, which knows nothing of the other gesture. False while no
        /// gesture is set.</summary>
        public bool OrbitTap { get; private set; }

        /// <summary>The <see cref="LookGesture"/> twin of <see cref="OrbitTap"/>: its tap, unless its press began
        /// while <see cref="OrbitGesture"/> was dragging.</summary>
        public bool LookTap { get; private set; }

        /// <summary>True while either gesture is dragging. The game forwards it to the pointer capture request so
        /// the cursor hides and holds for the drag.</summary>
        public bool WantsPointerCapture => TurnBodyActive || OrbitGesture?.Phase == PointerGesturePhase.Dragging;

        // Whether the camera has orbited since each gesture's live press began. A gesture that crosses its threshold
        // replays its pending travel, which is only fresh movement if nothing orbited the camera during that travel.
        bool _orbitedSinceOrbitPress;
        bool _orbitedSinceLookPress;
        // Whether each gesture's live press began while the other gesture was dragging. Such a press never taps:
        // in a both-buttons run, letting go of the second button is not a select.
        bool _orbitPressDuringLookDrag;
        bool _lookPressDuringOrbitDrag;

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
        /// and sign are as above. Read <see cref="OrbitTap"/> and <see cref="LookTap"/> after this call, in the
        /// same frame. A press that began while the other gesture was dragging never taps.</para>
        /// </summary>
        public void Update(in InputState input, float dt)
        {
            if (OrbitGesture is null && LookGesture is null)
            {
                OrbitTap = LookTap = false;
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

        // Advances each set gesture once and reports its tap, then orbits by at most one delta: a crossing gesture's
        // replay when nothing has orbited since its press began, else this frame's mouse delta while any gesture
        // drags.
        void AdvanceGestures(in InputState input)
        {
            bool orbitWasDragging = IsDragging(OrbitGesture);
            bool lookWasDragging = IsDragging(LookGesture);
            PointerGesturePhase orbitBefore = OrbitGesture?.Phase ?? PointerGesturePhase.Idle;
            PointerGesturePhase lookBefore = LookGesture?.Phase ?? PointerGesturePhase.Idle;
            bool orbitCrossed = Step(OrbitGesture, input, ref _orbitedSinceOrbitPress);
            bool lookCrossed = Step(LookGesture, input, ref _orbitedSinceLookPress);
            OrbitTap = Tap(OrbitGesture, orbitBefore, lookWasDragging || IsDragging(LookGesture),
                ref _orbitPressDuringLookDrag);
            LookTap = Tap(LookGesture, lookBefore, orbitWasDragging || IsDragging(OrbitGesture),
                ref _lookPressDuringOrbitDrag);
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

        // On the frame a gesture's press begins, latches whether the other gesture was dragging that frame. Returns
        // the gesture's tap unless its press began during the other's drag.
        static bool Tap(PointerGesture? gesture, PointerGesturePhase before, bool otherDragging,
            ref bool pressDuringOtherDrag)
        {
            if (gesture is null)
                return false;
            if (before == PointerGesturePhase.Idle && gesture.Phase != PointerGesturePhase.Idle)
                pressDuringOtherDrag = otherDragging;
            return gesture.TapThisFrame && !pressDuringOtherDrag;
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
