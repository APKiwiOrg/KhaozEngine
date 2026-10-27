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
        /// <see cref="OrbitTap"/> after <see cref="UpdateInput"/>, with the press origin in
        /// <see cref="PointerGesture.TapPosition"/>. Null by default. Setting this or <see cref="LookGesture"/>
        /// replaces <see cref="OrbitButton"/>, which is then ignored. A gesture with
        /// <see cref="PointerGesture.LivePendingTravel"/> also orbits while pending without losing its own tap.</summary>
        public PointerGesture? OrbitGesture;
        /// <summary>Optional look gesture: its drag orbits the camera and sets <see cref="TurnBodyActive"/>, so the
        /// game turns the body to face the camera. Its quick click is a tap read from <see cref="LookTap"/>, with
        /// the press origin in <see cref="PointerGesture.TapPosition"/>. Null by default. Live pending travel
        /// orbits the camera alone, without turning the body or starting pointer capture.</summary>
        public PointerGesture? LookGesture;
        /// <summary>Whether the UI owns the pointer this frame. The game sets it before each <see cref="UpdateInput"/>.
        /// A press that begins while blocked never taps, and never orbits while the gesture's threshold is above zero.
        /// At a zero or negative threshold a held button orbits again on the first unblocked frame. Read only while a
        /// gesture is set.</summary>
        public bool UiBlocked;

        /// <summary>True while <see cref="LookGesture"/> is dragging: the game turns the body to face the camera.
        /// </summary>
        public bool TurnBodyActive => LookGesture?.Phase == PointerGesturePhase.Dragging;

        /// <summary>True on the frame of the last <see cref="UpdateInput"/> when <see cref="OrbitGesture"/> tapped and the
        /// camera did not turn during that press except by its own live pending travel, its release frame included.
        /// A turn from <see cref="LookGesture"/> voids the tap, including when both live gestures deliver pending
        /// motion together. A simultaneous release suppresses both taps when either gesture is live, even without
        /// motion. Default-off stationary taps retain their existing behavior. Read taps here rather than from
        /// <see cref="PointerGesture.TapThisFrame"/>, which knows nothing of the other gesture. False while no
        /// gesture is set.</summary>
        public bool OrbitTap { get; private set; }

        /// <summary>The <see cref="LookGesture"/> twin of <see cref="OrbitTap"/>: its tap, unless the camera turned
        /// during that press except by its own live pending travel. A simultaneous release suppresses both taps
        /// when either gesture is live.</summary>
        public bool LookTap { get; private set; }

        /// <summary>True while either gesture is dragging. The game forwards it to the pointer capture request so
        /// the cursor hides and holds for the drag.</summary>
        public bool WantsPointerCapture => TurnBodyActive || OrbitGesture?.Phase == PointerGesturePhase.Dragging;

        // Whether the camera has orbited since each gesture's latest press began. A gesture that crosses its
        // threshold replays its pending travel, which is only fresh movement if nothing orbited the camera during
        // that travel. Live pending travel counts here too, even when it preserves the gesture's tap.
        bool _orbitedSinceOrbitPress;
        bool _orbitedSinceLookPress;
        // Live pending travel suppresses later replay but preserves its own tap, so tap invalidation is separate.
        bool _orbitTapVoided;
        bool _lookTapVoided;

        public FollowCameraController(FollowCamera3D camera)
        {
            Camera = camera ?? throw new ArgumentNullException(nameof(camera));
        }

        /// <summary>
        /// Apply this frame's drag-orbit and scroll-zoom, then advance the camera clocks. This is
        /// <see cref="UpdateInput"/> with <paramref name="dt"/>, followed by the camera's optional target damping
        /// (<see cref="FollowCamera3D.AdvanceTarget"/>) and eased boom recovery
        /// (<see cref="FollowCamera3D.AdvanceBoom"/>), so pass the real frame time. Each clock is a no-op unless
        /// <see cref="FollowCamera3D.EnableTargetDamping"/> or <see cref="FollowCamera3D.BoomRecoveryRate"/> turns
        /// it on. The orbit, zoom, gesture and tap rules are on <see cref="UpdateInput"/>.
        /// </summary>
        public void Update(in InputState input, float dt)
        {
            UpdateInput(input, dt);
            Camera.AdvanceTarget(dt);   // drives the opt-in target damping (no-op while disabled)
            Camera.AdvanceBoom(dt);     // eases the boom back out after an obstruction clears (no-op at rate 0)
        }

        /// <summary>
        /// Apply this frame's drag-orbit and scroll-zoom without advancing the camera clocks. For a consumer that
        /// reads input early in its frame and calls <see cref="FollowCamera3D.AdvanceTarget"/> and
        /// <see cref="FollowCamera3D.AdvanceBoom"/> itself later, after the subject moves. Every other caller wants
        /// <see cref="Update"/>. While <see cref="OrbitButton"/> is held, the mouse delta swings
        /// <see cref="FollowCamera3D.Yaw"/> (horizontal) and tilts <see cref="FollowCamera3D.Pitch"/> (vertical), and
        /// the wheel scales <see cref="FollowCamera3D.Distance"/>. The default mapping turns the view the way the hand
        /// pulls (drag right turns left, drag down looks up). Flip either axis with <see cref="InvertX"/> /
        /// <see cref="InvertY"/>. Pitch and distance are clamped by the camera.
        /// <para>With <see cref="OrbitGesture"/> or <see cref="LookGesture"/> set, each set gesture advances once with
        /// <see cref="UiBlocked"/> and <paramref name="elapsedSeconds"/>, and <see cref="OrbitButton"/> is ignored. A
        /// gesture with a <see cref="PointerGesture.TapTolerance"/> is timed by <paramref name="elapsedSeconds"/>, so
        /// pass the real frame time here even when the camera clocks advance later. A zero freezes the grace.
        /// The camera orbits while a gesture drags or delivers live pending travel, by at most one delta a frame,
        /// however many buttons are held. Live motion takes precedence over a default crossing's replay. Without
        /// live motion, a default gesture crossing its threshold with no orbit since its press began
        /// applies its <see cref="PointerGesture.DragDelta"/>, the replay of its pending travel
        /// (<see cref="LookGesture"/> first if both cross together). Otherwise the frame's
        /// <see cref="InputState.MouseDeltaPoints"/> applies. Speed, invert and sign are as above. Read
        /// <see cref="OrbitTap"/> and <see cref="LookTap"/> after this call, in the same frame. A tap counts only if
        /// the camera did not turn during that press except by that same gesture's own live pending travel.
        /// Live gestures use their own <see cref="PointerGesture.DragDelta"/> without replay.</para>
        /// </summary>
        public void UpdateInput(in InputState input, float elapsedSeconds)
        {
            if (OrbitGesture is null && LookGesture is null)
            {
                OrbitTap = LookTap = false;
                if (input.IsDown(OrbitButton))
                    Orbit(input.MouseDelta);
            }
            else
            {
                AdvanceGestures(input, elapsedSeconds);
                // After this frame's orbit, so a turn on the release frame also voids the tap.
                bool simultaneousLiveTaps = OrbitGesture is { TapThisFrame: true } && LookGesture is { TapThisFrame: true }
                    && (OrbitGesture.LivePendingTravel || LookGesture.LivePendingTravel);
                OrbitTap = OrbitGesture?.TapThisFrame == true && !_orbitTapVoided && !simultaneousLiveTaps;
                LookTap = LookGesture?.TapThisFrame == true && !_lookTapVoided && !simultaneousLiveTaps;
            }

            float scroll = input.ScrollDelta;
            if (scroll != 0f)
                Camera.Distance *= MathF.Pow(ZoomStep, -scroll);   // setter clamps, and +scroll -> closer
        }

        // Live motion wins over replay. A live press frame has zero delta and must not swallow motion from the
        // other gesture. With no live motion, keep the default replay precedence.
        void AdvanceGestures(in InputState input, float dt)
        {
            bool orbitCrossed = Step(OrbitGesture, input, dt, ref _orbitedSinceOrbitPress, ref _orbitTapVoided);
            bool lookCrossed = Step(LookGesture, input, dt, ref _orbitedSinceLookPress, ref _lookTapVoided);
            bool orbitDrives = DrivesOrbit(OrbitGesture);
            bool lookDrives = DrivesOrbit(LookGesture);
            if (!(orbitDrives || lookDrives))
                return;

            if (LookGesture is { LivePendingTravel: true } && LookGesture.DragDelta != Vector2.Zero)
                Orbit(LookGesture.DragDelta);
            else if (OrbitGesture is { LivePendingTravel: true } && OrbitGesture.DragDelta != Vector2.Zero)
                Orbit(OrbitGesture.DragDelta);
            else if (lookCrossed && !LookGesture!.LivePendingTravel && !_orbitedSinceLookPress)
                Orbit(LookGesture!.DragDelta);
            else if (orbitCrossed && !OrbitGesture!.LivePendingTravel && !_orbitedSinceOrbitPress)
                Orbit(OrbitGesture!.DragDelta);
            else if (IsDefaultDrag(LookGesture) || IsDefaultDrag(OrbitGesture))
                Orbit(input.MouseDeltaPoints);
            _orbitedSinceOrbitPress = true;
            _orbitedSinceLookPress = true;
            _orbitTapVoided |= !IsLivePending(OrbitGesture) || lookDrives;
            _lookTapVoided |= !IsLivePending(LookGesture) || orbitDrives;
        }

        // A new press resets both replay history and tap invalidation before this frame's orbit can set them.
        // Returns whether the gesture crossed into a drag this frame.
        bool Step(PointerGesture? gesture, in InputState input, float dt, ref bool orbitedSincePress, ref bool tapVoided)
        {
            if (gesture is null)
                return false;
            PointerGesturePhase before = gesture.Phase;
            gesture.Advance(input, UiBlocked, dt);
            if (before == PointerGesturePhase.Idle && gesture.Phase != PointerGesturePhase.Idle)
            {
                orbitedSincePress = false;
                tapVoided = false;
            }
            return before != PointerGesturePhase.Dragging && gesture.Phase == PointerGesturePhase.Dragging;
        }

        static bool IsLivePending(PointerGesture? gesture) =>
            gesture is { LivePendingTravel: true, Phase: PointerGesturePhase.Pending };

        static bool IsDefaultDrag(PointerGesture? gesture) =>
            gesture is { LivePendingTravel: false, Phase: PointerGesturePhase.Dragging };

        static bool DrivesOrbit(PointerGesture? gesture) => gesture?.Phase == PointerGesturePhase.Dragging
            || (IsLivePending(gesture) && gesture!.DragDelta != Vector2.Zero);

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
