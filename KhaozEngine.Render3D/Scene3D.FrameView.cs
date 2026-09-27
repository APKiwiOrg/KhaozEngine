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
    /// The jitter is zero unless <see cref="TemporalActive"/>, and zero on a later render of a frame that runs the temporal
    /// resolve, which is never resolved (Scene3D.TemporalResolve.cs). A zero jitter leaves every jittered matrix
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
        // The frame's jitter sequence length, fixed by its first render beside the temporal state, so a later render
        // inside the frame jitters by the same offset whatever its size or a settings change since.
        int _framePhaseCount = TemporalJitter.NativePhaseCount;
        // The display over internal scale that sequence length came from, fixed with it, which the frame's material
        // mip bias reads (Scene3D.TemporalFinish.cs).
        float _frameDisplayOverInternal = 1f;
        // The frame's debug view, fixed by its first render beside the temporal state (see DebugView). The
        // MotionVectors view makes its frame temporal, and the resolve views draw only on the render that ran the
        // resolve, so a view's data exists whenever it is drawn.
        SceneDebugView _frameDebugView;
        // Whether the frame's first render was given a display size, so the diagnostics never pair the last nonzero
        // display size a zero-size render keeps with its internal size (Scene3D.TemporalFinish.cs).
        bool _frameHasDisplayArea;

        /// <summary>This render's view snapshot. Default-valued before the first render.</summary>
        internal FrameView CurrentFrameView => _currentFrameView;

        /// <summary>Test seam: request temporal rendering without selecting a debug view.</summary>
        internal bool ForceTemporalForTests { get; set; }

        /// <summary>
        /// Whether this frame renders temporally, which is what turns the jitter on. This is the one read point for the
        /// temporal state. <see cref="TemporalRequested"/> lists the requesters.
        /// <para>
        /// A frame fixes the value at its first render. <see cref="LatchFrameView"/> takes the requesters as they stand
        /// then, and every later render and every draw made after that render in the same frame reads that value, so
        /// the jitter, the latch and the history advance agree. A requester changed after the frame's first render
        /// takes effect on the next frame.
        /// </para>
        /// <para>
        /// Before the frame's first render this reads the live requesters, by design. A change made between
        /// <see cref="Begin"/> and that render counts for the frame. Draw-time work such as motion key recording runs
        /// before the render and reads the live value, which the render then fixes unless a requester changes in
        /// between.
        /// </para>
        /// </summary>
        internal bool TemporalActive => _frameViewLatchedThisFrame ? _frameTemporalActive : TemporalRequested;

        /// <summary>Whether a temporal consumer asks for temporal rendering right now: temporal anti-aliasing
        /// (<see cref="TemporalResolveActive"/>), the main requester, the <see cref="SceneDebugView.MotionVectors"/>
        /// debug view, or the test seam. The three resolve views take effect only under temporal anti-aliasing, so
        /// they request nothing. Read only through <see cref="TemporalActive"/>.</summary>
        bool TemporalRequested => ForceTemporalForTests || _debugView == SceneDebugView.MotionVectors
            || TemporalResolveActive;

        /// <summary>Called from <see cref="Begin"/>: one frame index per Begin, however many renders follow, and the
        /// next render is the frame's first, which fixes the frame's temporal state.</summary>
        internal void BeginFrameView()
        {
            _frameIndex++;
            _frameViewLatchedThisFrame = false;
            _frameTemporalActive = false;
            _framePhaseCount = TemporalJitter.NativePhaseCount;
            _frameDisplayOverInternal = 1f;
            _frameDebugView = SceneDebugView.None;
        }

        /// <summary>
        /// Latch this render's snapshot. Runs after <c>EnsureSize</c>, so the internal size and the built-in camera's
        /// aspect are final, and before any pass reads a matrix. <see cref="FrameViewProjection"/> re-asserts the
        /// latched origin on an origin-aware camera first, so the <c>View</c> read after it is in the same render
        /// frame. A camera that cannot take an origin but was swapped in after <see cref="Begin"/> latched one gets the
        /// translation composed onto its view, as the fallback in <see cref="FrameViewProjection"/> does for its
        /// view-projection. The frame's first render also fixes its temporal state (<see cref="TemporalActive"/>), its
        /// debug view (<see cref="DebugView"/>), its display size, <paramref name="displayWidth"/> by
        /// <paramref name="displayHeight"/>, which joins the history key, and its jitter cycle, which that display size
        /// sets under temporal anti-aliasing (<see cref="DisplayOverInternalRatio"/>).
        /// </summary>
        internal void LatchFrameView(int displayWidth = 0, int displayHeight = 0)
        {
            // A later render inside the frame keeps the frame's display size. A call without a size keeps the last one.
            if (!_frameViewLatchedThisFrame && displayWidth > 0 && displayHeight > 0)
                (_latchedDisplayWidth, _latchedDisplayHeight) = (displayWidth, displayHeight);
            IIsoCamera3D cam = ActiveCamera;
            Matrix4x4 viewProjection = FrameViewProjection();
            Matrix4x4 view = cam is not IRenderOriginAware && _frameOriginActive
                ? Matrix4x4.CreateTranslation(_frameOrigin) * cam.View
                : cam.View;
            // The live requesters and settings on the first render, the values that render fixed on any later one.
            float displayOverInternal = _frameViewLatchedThisFrame
                ? _frameDisplayOverInternal
                : DisplayOverInternalRatio;
            int phaseCount = _frameViewLatchedThisFrame
                ? _framePhaseCount
                : TemporalJitter.PhaseCount(displayOverInternal);
            bool temporal = TemporalActive;
            // A later render of a resolving frame is shown unresolved, so it renders unjittered. EnsureSize fixed
            // _frameResolves before this latch.
            bool jittered = temporal && !(_frameViewLatchedThisFrame && _frameResolves);
            Vector2 jitter = jittered ? TemporalJitter.Offset(_frameIndex, phaseCount) : Vector2.Zero;
            _currentFrameView = new FrameView(view, cam.Projection, viewProjection, FrameAbsoluteViewProjection(),
                _frameOrigin, _res.Width, _res.Height, _frameIndex, jitter);
            // History moves on a frame's first render only. A second render inside the same frame (an offscreen
            // capture, possibly at another size) latches matrices for its own viewport but keeps the frame's index,
            // temporal state, jitter, previous view and history state (Scene3D.Temporal.cs). Under the resolve it keeps
            // no jitter, since it is never resolved.
            if (_frameViewLatchedThisFrame) return;
            _frameViewLatchedThisFrame = true;
            _frameTemporalActive = temporal;
            _framePhaseCount = phaseCount;
            _frameDisplayOverInternal = displayOverInternal;
            _frameDebugView = _debugView;
            _frameHasDisplayArea = displayWidth > 0 && displayHeight > 0;
            AdvanceTemporalHistory();
        }
    }
}
