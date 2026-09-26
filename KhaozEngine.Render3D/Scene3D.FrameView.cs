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

        /// <summary>The display size over the internal size per axis, which sets the jitter sequence length. Native
        /// in round 1. Round 2's upscaler replaces this with its ratio.</summary>
        const float DisplayOverInternalRatio = 1f;

        /// <summary>This render's view snapshot. Default-valued before the first render.</summary>
        internal FrameView CurrentFrameView => _currentFrameView;

        /// <summary>Test seam: request temporal rendering with no public requester. The debug view and the temporal
        /// anti-aliasing mode are the real requesters.</summary>
        internal bool ForceTemporalForTests { get; set; }

        /// <summary>Whether a temporal consumer asked for this frame, which is what turns the jitter on.</summary>
        internal bool TemporalActive => ForceTemporalForTests;

        /// <summary>Called from <see cref="Begin"/>: one frame index per Begin, however many renders follow.</summary>
        internal void BeginFrameView() => _frameIndex++;

        /// <summary>
        /// Latch this render's snapshot. Runs after <c>EnsureSize</c>, so the internal size and the built-in camera's
        /// aspect are final, and before any pass reads a matrix. <see cref="FrameViewProjection"/> re-asserts the
        /// latched origin on an origin-aware camera first, so the <c>View</c> read after it is in the same render
        /// frame. A camera that cannot take an origin but was swapped in after <see cref="Begin"/> latched one gets the
        /// translation composed onto its view, the fallback <see cref="FrameViewProjection"/> applies to its
        /// view-projection.
        /// </summary>
        internal void LatchFrameView()
        {
            IIsoCamera3D cam = ActiveCamera;
            Matrix4x4 viewProjection = FrameViewProjection();
            Matrix4x4 view = cam is not IRenderOriginAware && _frameOriginActive
                ? Matrix4x4.CreateTranslation(_frameOrigin) * cam.View
                : cam.View;
            int phaseCount = TemporalJitter.PhaseCount(DisplayOverInternalRatio);
            Vector2 jitter = TemporalActive ? TemporalJitter.Offset(_frameIndex, phaseCount) : Vector2.Zero;
            _currentFrameView = new FrameView(view, cam.Projection, viewProjection, FrameAbsoluteViewProjection(),
                _frameOrigin, _res.Width, _res.Height, _frameIndex, jitter);
        }
    }
}
