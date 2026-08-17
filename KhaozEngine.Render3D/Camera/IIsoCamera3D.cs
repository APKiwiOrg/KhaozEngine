using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Render3D
{
    /// <summary>Read-only orthographic iso camera surface (headless; fakeable in tests/consumers).</summary>
    public interface IIsoCamera3D
    {
        Matrix4x4 View { get; }
        Matrix4x4 Projection { get; }
        Matrix4x4 ViewProjection { get; }
        Vector3 Eye { get; }
        Vector3 Forward { get; }

        /// <summary>
        /// Tell the camera a new frame has started, so anything it cached for the previous one is dropped.
        /// <see cref="Scene3D.Begin"/> calls it on the active camera before anything reads <see cref="Eye"/>, which
        /// is what makes a camera whose eye depends on something OUTSIDE itself (a physics world that can slide a
        /// wall in, a terrain that can deform) recompute once a frame instead of holding a value nothing about the
        /// camera invalidates.
        /// <para>
        /// The default body does nothing, which is exactly right for a camera that is pure arithmetic over its own
        /// fields: <see cref="IsoCamera3D"/>, <see cref="FlyCamera3D"/>, and every consumer camera written before
        /// this member existed. <see cref="FollowCamera3D"/> is the only implementation that overrides it today, to
        /// drop the eye its occlusion sweep produced. It is a DEFAULT interface member rather than the separate
        /// opt-in interface <see cref="IRenderOriginAware"/> had to be, because a no-op needs no backing storage:
        /// adding it here breaks no existing implementer and costs a camera that ignores it nothing at all.
        /// </para>
        /// <para>
        /// Calling it more than once in a frame is harmless, since the worst it can cost is one recompute. NOT
        /// calling it is the failure worth knowing about, and it belongs to a consumer that drives a camera with no
        /// <see cref="Scene3D"/> anywhere: call it once per frame yourself there.
        /// </para>
        /// </summary>
        void BeginFrame() { }

        /// <summary>
        /// Project a world point to a screen pixel (the forward inverse of <c>ScreenToRay</c>; top-left origin,
        /// y-down, matching the displayed image). Returns <c>false</c> with <paramref name="screenPixel"/> = default
        /// when the point is not in front of the camera (behind it, or outside the near/far depth range), so a caller
        /// can skip drawing a label for it; <c>true</c> with the pixel otherwise. Pure math; headless-testable.
        /// <paramref name="viewportWidth"/>/<paramref name="viewportHeight"/> are FRAMEBUFFER pixels for a
        /// framebuffer-space drawing pass. A design-space HUD pass (a <c>SpriteBatch.Begin</c> with a design
        /// viewport) must use the <see cref="WorldToScreen(Vector3, IDesignViewport, out Vector2)"/> overload
        /// instead, or the anchor drifts on any window whose aspect differs from the design aspect.
        /// </summary>
        bool WorldToScreen(Vector3 world, int viewportWidth, int viewportHeight, out Vector2 screenPixel);

        /// <summary>
        /// The DESIGN-SPACE projection for a HUD overlay drawn through a <c>SpriteBatch.Begin(IDesignViewport)</c>
        /// pass. The <see cref="WorldToScreen(Vector3, int, int, out Vector2)"/> overload maps NDC onto the given
        /// rect directly, which lines up with the visible 3D scene only when those ints are the real framebuffer
        /// size and the drawing pass is framebuffer-space. Calling it with the design viewport's own
        /// <see cref="IDesignViewport.Width"/>/<see cref="IDesignViewport.Height"/> instead is only correct when
        /// the window happens to be exactly the design aspect: on any other window shape (a letterbox or
        /// pillarbox bar under <see cref="IDesignViewport"/>'s fit-style scaling) the anchor drifts by ndc times
        /// the letterbox offset on the loose axis. This overload remaps NDC onto
        /// <see cref="IDesignViewport.WindowBounds"/> (the whole window expressed in design space) instead, which
        /// is exact for every scale mode, so a design-space pass must call this one rather than the int overload
        /// with the design dims. Requires the camera's aspect ratio to be driven from the real framebuffer, which
        /// is what makes the int-with-design-dims call wrong in the first place.
        /// </summary>
        /// <param name="world">The world point to project.</param>
        /// <param name="designViewport">The design viewport the HUD pass is drawing through.</param>
        /// <param name="designPixel">The projected point in design space, or <c>default</c> when culled.</param>
        /// <returns><c>true</c> if the point is drawable (in front of the camera, within depth range), <c>false</c>
        /// otherwise.</returns>
        bool WorldToScreen(Vector3 world, IDesignViewport designViewport, out Vector2 designPixel)
        {
            if (!WorldToScreen(world, designViewport.Width, designViewport.Height, out Vector2 raw))
            {
                designPixel = default;
                return false;
            }

            Rect window = designViewport.WindowBounds;
            designPixel = new Vector2(
                window.X + raw.X / designViewport.Width * window.Width,
                window.Y + raw.Y / designViewport.Height * window.Height);
            return true;
        }
    }
}
