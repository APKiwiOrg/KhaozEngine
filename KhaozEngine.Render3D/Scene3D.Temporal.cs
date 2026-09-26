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
            _previousFrameView = null;
            if (active && _historyActive && _historyView is FrameView last)
            {
                TemporalHistory.MarkValidAfterFrame();   // the last frame rendered, so the history now holds it
                TemporalResetReason reason = DetectTemporalReset(last, view, key);
                if (reason == TemporalResetReason.None) _previousFrameView = last.RebasedTo(view.RenderOrigin);
                else TemporalHistory.Invalidate(reason);
            }
            else
                TemporalHistory.Invalidate(TemporalResetReason.FirstFrame);   // outranks every trigger, so none is compared
            _historyActive = active;
            _historyView = view;
            _historyKey = key;
        }

        /// <summary>
        /// The reason this frame cannot continue the last one, or <see cref="TemporalResetReason.None"/>. Every trigger
        /// is checked and folded through <see cref="TemporalResetPrecedence.Higher"/>, so one reason is reported, the
        /// root cause over the size change it brings, whatever order the checks run in.
        /// </summary>
        TemporalResetReason DetectTemporalReset(in FrameView last, in FrameView view, in TemporalFrameKey key)
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
            return reason;
        }
    }
}
