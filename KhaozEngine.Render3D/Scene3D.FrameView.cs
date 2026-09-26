using System.Numerics;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The frame view snapshot (docs/design/TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24.md, section 1): the one place a
    /// render reads the camera's matrices. <see cref="LatchFrameView"/> runs immediately after <c>EnsureSize</c> in
    /// <c>RenderInternal</c>, when the camera override, the render origin and the internal size are all final, and
    /// every pass after it reads <see cref="CurrentFrameView"/> instead of the camera.
    /// <para>
    /// The jitter is zero unless <see cref="TemporalActive"/>. A zero jitter leaves every jittered matrix
    /// bit-identical to its unjittered twin, which is what keeps every committed golden unchanged with temporal
    /// rendering off.
    /// </para>
    /// </summary>
    public sealed partial class Scene3D
    {
        FrameView _currentFrameView;
        long _frameIndex;
        bool _frameViewLatchedThisFrame;
        // The frame's temporal state, fixed by its first render (see TemporalActive).
        bool _frameTemporalActive;

        /// <summary>The display size over the internal size per axis, which sets the jitter sequence length. Native
        /// in round 1. Round 2's upscaler replaces this with its ratio.</summary>
        const float DisplayOverInternalRatio = 1f;

        /// <summary>This render's view snapshot. Default-valued before the first render.</summary>
        internal FrameView CurrentFrameView => _currentFrameView;

        /// <summary>Test seam: request temporal rendering without selecting a debug view.</summary>
        internal bool ForceTemporalForTests { get; set; }

        /// <summary>
        /// Whether this frame renders temporally, which is what turns the jitter on. This is the one read point for the
        /// temporal state. <see cref="TemporalRequested"/> lists the requesters.
        /// <para>
        /// A frame fixes the value at its first render. <see cref="LatchFrameView"/> takes the requesters as they stand
        /// then, and every later render and every draw of the same frame reads that value, so the jitter, the latch,
        /// the history advance and anything recorded at draw time agree. A requester changed after the frame's first
        /// render takes effect on the next frame.
        /// </para>
        /// <para>
        /// Before the frame's first render this reads the live requesters, by design. A change made between
        /// <see cref="Begin"/> and that render counts for the frame. Draw-time work such as motion key recording runs
        /// before the render and reads the live value, which the render then fixes unless a requester changes in
        /// between.
        /// </para>
        /// </summary>
        internal bool TemporalActive => _frameViewLatchedThisFrame ? _frameTemporalActive : TemporalRequested;

        /// <summary>Whether a temporal consumer asks for temporal rendering right now: a <see cref="DebugView"/> other
        /// than <see cref="SceneDebugView.None"/>, or the test seam. Round 2 adds the temporal anti-aliasing mode. Read
        /// only through <see cref="TemporalActive"/>.</summary>
        bool TemporalRequested => ForceTemporalForTests || _debugView != SceneDebugView.None;

        /// <summary>Called from <see cref="Begin"/>: one frame index per Begin, however many renders follow, and the
        /// next render is the frame's first, which fixes the frame's temporal state.</summary>
        internal void BeginFrameView()
        {
            _frameIndex++;
            _frameViewLatchedThisFrame = false;
            _frameTemporalActive = false;
        }

        /// <summary>
        /// Latch this render's snapshot. Runs after <c>EnsureSize</c>, so the internal size and the built-in camera's
        /// aspect are final, and before any pass reads a matrix. <see cref="FrameViewProjection"/> re-asserts the
        /// latched origin on an origin-aware camera first, so the <c>View</c> read after it is in the same render
        /// frame. A camera that cannot take an origin but was swapped in after <see cref="Begin"/> latched one gets the
        /// translation composed onto its view, as the fallback in <see cref="FrameViewProjection"/> does for its
        /// view-projection. The frame's first render also fixes its temporal state (<see cref="TemporalActive"/>).
        /// </summary>
        internal void LatchFrameView()
        {
            IIsoCamera3D cam = ActiveCamera;
            Matrix4x4 viewProjection = FrameViewProjection();
            Matrix4x4 view = cam is not IRenderOriginAware && _frameOriginActive
                ? Matrix4x4.CreateTranslation(_frameOrigin) * cam.View
                : cam.View;
            int phaseCount = TemporalJitter.PhaseCount(DisplayOverInternalRatio);
            // The live requesters on the frame's first render, the value that render fixed on any later one.
            bool temporal = TemporalActive;
            Vector2 jitter = temporal ? TemporalJitter.Offset(_frameIndex, phaseCount) : Vector2.Zero;
            _currentFrameView = new FrameView(view, cam.Projection, viewProjection, FrameAbsoluteViewProjection(),
                _frameOrigin, _res.Width, _res.Height, _frameIndex, jitter);
            // History moves on a frame's first render only. A second render inside the same frame (an offscreen
            // capture, possibly at another size) latches matrices for its own viewport but keeps the frame's index,
            // temporal state, jitter, previous view and history state (Scene3D.Temporal.cs).
            if (_frameViewLatchedThisFrame) return;
            _frameViewLatchedThisFrame = true;
            _frameTemporalActive = temporal;
            AdvanceTemporalHistory();
        }
    }
}
