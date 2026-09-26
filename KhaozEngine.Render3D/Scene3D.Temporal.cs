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
            _previousFrameView = null;
            if (active && _historyActive && _historyView is FrameView last)
            {
                TemporalHistory.MarkValidAfterFrame();   // the last frame rendered, so the history now holds it
                _previousFrameView = last.RebasedTo(view.RenderOrigin);
            }
            else
                TemporalHistory.Invalidate(TemporalResetReason.FirstFrame);
            _historyActive = active;
            _historyView = view;
        }
    }
}
