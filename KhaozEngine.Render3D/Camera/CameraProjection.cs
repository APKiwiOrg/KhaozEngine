using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// Shared world-to-screen projection math, the forward inverse of the cameras' <c>ScreenToRay</c> unprojection,
    /// so <see cref="FollowCamera3D"/> and <see cref="IsoCamera3D"/> implement
    /// <see cref="IIsoCamera3D.WorldToScreen"/> identically (same pixel convention: top-left origin, y-down).
    /// </summary>
    internal static class CameraProjection
    {
        public static bool WorldToScreen(Matrix4x4 viewProjection, Vector3 world, int viewportWidth, int viewportHeight,
            out Vector2 screenPixel)
        {
            Vector4 clip = Vector4.Transform(new Vector4(world, 1f), viewProjection);
            // Perspective: w <= 0 means the point is at or behind the eye plane. Orthographic: w is always 1, so the
            // depth-range check below is what rejects a behind-camera (or out-of-frustum-depth) point there.
            if (clip.W <= 1e-6f) { screenPixel = default; return false; }
            Vector3 ndc = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
            // Clip-space depth is [0,1] here (matches ScreenToRay, which unprojects near at z=0 and far at z=1).
            // Outside that band is behind the near plane (e.g. behind an ortho camera) or past the far plane.
            if (ndc.Z < 0f || ndc.Z > 1f) { screenPixel = default; return false; }
            float px = (ndc.X * 0.5f + 0.5f) * viewportWidth;
            float py = (1f - (ndc.Y * 0.5f + 0.5f)) * viewportHeight;   // NDC y-up -> pixel y-down
            screenPixel = new Vector2(px, py);
            return true;
        }
    }
}
