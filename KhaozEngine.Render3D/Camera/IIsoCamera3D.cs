using System.Numerics;

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
        /// Project a world point to a screen pixel (the forward inverse of <c>ScreenToRay</c>; top-left origin,
        /// y-down, matching the displayed image). Returns <c>false</c> with <paramref name="screenPixel"/> = default
        /// when the point is not in front of the camera (behind it, or outside the near/far depth range), so a caller
        /// can skip drawing a label for it; <c>true</c> with the pixel otherwise. Pure math; headless-testable.
        /// </summary>
        bool WorldToScreen(Vector3 world, int viewportWidth, int viewportHeight, out Vector2 screenPixel);
    }
}
