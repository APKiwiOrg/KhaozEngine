using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Temporal state carried from one rendered frame to the next (docs/design/TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24.md,
    /// section 2): the previous frame's view rebased to this frame's render origin, and whether the history can be
    /// trusted this frame, with the reason it was last reset. <see cref="LatchFrameView"/> advances it on a frame's
    /// first render only, so a second render inside the same frame reads the same state and moves nothing.
    /// <para>
    /// While nothing requests temporal rendering the history is invalid, and the first temporal frame after that
    /// starts from scratch with <see cref="TemporalResetReason.FirstFrame"/>.
    /// </para>
    /// </summary>
    public sealed partial class Scene3D
    {
        // The last frame's first-render snapshot, in the origin it was latched against.
        FrameView? _historyView;
        // That snapshot rebased onto this frame's origin, or null while the history is invalid.
        FrameView? _previousFrameView;
        // Whether the last rendered frame had temporal rendering active, so its snapshot can seed this frame's history.
        bool _historyActive;
        // The reset-relevant settings the last frame rendered with.
        TemporalFrameKey _historyKey;
        // The absolute camera eye and look direction the last frame rendered with, for the automatic cut. Read from the
        // camera only on a frame with temporal rendering active, the only kind the detector compares against.
        Vector3 _historyEye, _historyForward;
        // Set by CameraCut and cleared by the next frame's first render.
        bool _cameraCutRequested;

        /// <summary>
        /// Tell the scene the next rendered frame does not continue this one: a teleport, a loading screen, a cutscene
        /// cut. Temporal history is dropped for that frame, so nothing from before the cut reprojects into it. Call it
        /// any time before that frame renders, including between <see cref="Begin"/> and the render. Calling it more
        /// than once before a frame renders is the same as calling it once, and it changes nothing while temporal
        /// rendering is off.
        /// <para>
        /// A camera move past <see cref="TemporalSettings.CutDistanceMetres"/> or a turn past
        /// <see cref="TemporalSettings.CutAngleDegrees"/> in one frame is a cut without this call. Both drop history
        /// the same way. This call reports <see cref="TemporalResetReason.CameraCutRequested"/>, the automatic cut
        /// reports <see cref="TemporalResetReason.CameraCutDetected"/>, and a frame with both reports the call.
        /// </para>
        /// </summary>
        public void CameraCut() => _cameraCutRequested = true;

        /// <summary>The settings whose change resets history, as one frame saw them.</summary>
        readonly record struct TemporalFrameKey(AntiAliasing AntiAliasing, RenderScale RenderScale, float Supersample,
            bool HdrColor);

        /// <summary>Whether this frame's history can be read, and why it was last reset.</summary>
        internal TemporalHistory TemporalHistory { get; } = new();

        /// <summary>The previous frame's view rebased to this frame's render origin (<c>T(d) * M</c>, see
        /// <see cref="FrameView.RebasedTo"/>), or null when the history is invalid: the first temporal frame, every frame
        /// a reset fired on, and every frame with temporal rendering off. A consumer reads null as no previous state,
        /// which for motion means zero motion.</summary>
        internal FrameView? PreviousFrameView => _previousFrameView;

        /// <summary>Advance the history by one frame, from <see cref="LatchFrameView"/> on the frame's first render.</summary>
        void AdvanceTemporalHistory()
        {
            FrameView view = _currentFrameView;
            bool active = TemporalActive;
            var key = new TemporalFrameKey(ResolvedAa(), Post.EffectiveRenderScale, Post.EffectiveSupersample, _res.HdrColor);
            // The camera is read only while temporal rendering is active, so a frame with it off does no added work.
            Vector3 eye = default, forward = default;
            if (active)
            {
                IIsoCamera3D cam = ActiveCamera;
                eye = cam.Eye;
                forward = cam.Forward;
            }
            _previousFrameView = null;
            if (active && _historyActive && _historyView is FrameView last)
            {
                TemporalHistory.MarkValidAfterFrame();   // the last frame rendered, so the history now holds it
                TemporalResetReason reason = DetectTemporalReset(last, view, key, eye, forward);
                if (reason == TemporalResetReason.None) _previousFrameView = last.RebasedTo(view.RenderOrigin);
                else TemporalHistory.Invalidate(reason);
            }
            else
                TemporalHistory.Invalidate(TemporalResetReason.FirstFrame);   // outranks every trigger, so none is compared
            _cameraCutRequested = false;   // consumed by the frame it lands on, whatever that frame reports
            _historyActive = active;
            _historyView = view;
            _historyKey = key;
            _historyEye = eye;
            _historyForward = forward;
        }

        /// <summary>
        /// The reason this frame cannot continue the last one, or <see cref="TemporalResetReason.None"/>. Every trigger
        /// is checked and folded through <see cref="TemporalResetPrecedence.Higher"/>, so one reason is reported, the
        /// root cause over the size change it brings and an explicit cut over a detected one, whatever order the checks
        /// run in. <paramref name="eye"/> and <paramref name="forward"/> are this frame's absolute camera eye and look
        /// direction.
        /// </summary>
        TemporalResetReason DetectTemporalReset(in FrameView last, in FrameView view, in TemporalFrameKey key,
            Vector3 eye, Vector3 forward)
        {
            TemporalResetReason reason = TemporalResetReason.None;
            if (key.HdrColor != _historyKey.HdrColor)
                reason = TemporalResetPrecedence.Higher(reason, TemporalResetReason.DeviceReset);
            if (key.AntiAliasing != _historyKey.AntiAliasing)
                reason = TemporalResetPrecedence.Higher(reason, TemporalResetReason.AntiAliasing);
            if (key.RenderScale != _historyKey.RenderScale || key.Supersample != _historyKey.Supersample)
                reason = TemporalResetPrecedence.Higher(reason, TemporalResetReason.RenderScale);
            if (view.Width != last.Width || view.Height != last.Height)
                reason = TemporalResetPrecedence.Higher(reason, TemporalResetReason.Resize);
            if (_cameraCutRequested)
                reason = TemporalResetPrecedence.Higher(reason, TemporalResetReason.CameraCutRequested);
            if (CameraMovedPastCutThresholds(eye, forward) || RenderOriginJumped(last.RenderOrigin, view.RenderOrigin))
                reason = TemporalResetPrecedence.Higher(reason, TemporalResetReason.CameraCutDetected);
            return reason;
        }

        /// <summary>
        /// Whether the camera eye moved further, or its forward direction turned further, since the last frame than
        /// <see cref="PixelPostProcessSettings.Temporal"/> allows. Both limits are exclusive, so a move of exactly the
        /// limit continues the frame. The distance is between absolute eyes, so a render origin step is never a cut by
        /// itself. The turn is measured and compared in degrees, because a limit compared as a cosine would fold 200
        /// onto 160.
        /// </summary>
        bool CameraMovedPastCutThresholds(Vector3 eye, Vector3 forward)
        {
            TemporalSettings limits = Post.Temporal;
            if (Vector3.Distance(eye, _historyEye) > limits.CutDistanceMetres) return true;
            // Rounding can put the dot of two opposite unit vectors just below -1, where acos is NaN.
            double cosine = Math.Clamp(Vector3.Dot(Vector3.Normalize(forward), Vector3.Normalize(_historyForward)), -1f, 1f);
            // Acos returns at most pi, so dividing by pi, rather than multiplying by 180 / pi, keeps every turn at or
            // below 180 and an exact about-turn at exactly 180, which a limit of 180 must not cut.
            double degrees = Math.Acos(cosine) / Math.PI * 180.0;
            return degrees > limits.CutAngleDegrees;
        }

        /// <summary>
        /// Whether the render origin moved by a step the last frame cannot be rebased across. A step of at most one
        /// 128 m cell per axis on X and Z, the step the automatic origin takes under ordinary camera motion, is carried
        /// by <see cref="FrameView.RebasedTo"/> within its motion bound. <see cref="RenderOrigin"/> accepts any value,
        /// and a jump off the grid rounds the step itself, while a jump of more than one cell grows the rounding in the
        /// rebased translation row past that bound. An explicit origin can jump while the eye stays still, so the
        /// distance check cannot be relied on to catch either, and each is a detected cut in its own right.
        /// </summary>
        static bool RenderOriginJumped(Vector3 from, Vector3 to) => !IsCellStep(to.X - from.X) || !IsCellStep(to.Z - from.Z);

        /// <summary>A whole number of 128 m cells and at most one: zero, or one cell either way.</summary>
        static bool IsCellStep(float step) => step == 0f || MathF.Abs(step) == WorldFrame.Grid;
    }
}
