using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// Pure sky-shading math shared between the C# host (settings plumbing + headless tests) and the GLSL
    /// <c>SkyFrag</c>, which mirrors <see cref="Shade"/> exactly (keep in sync, like <c>OutlineMath</c> mirrors
    /// <c>EdgeFrag</c> and <c>SurfaceShading</c> mirrors <c>ModelFrag</c>). The sky is drawn in SCREEN space so it
    /// reads identically under an orthographic iso camera (parallel view rays - a world view-ray sky degenerates to a
    /// flat colour there) AND a perspective follow camera: a vertical screen gradient (top = zenith, bottom = horizon)
    /// plus a sun disc + halo placed at the screen projection of the sun direction. No GPU state, no allocations.
    /// </summary>
    internal static class SkyMath
    {
        /// <summary>The sun direction the sky faces INTO, derived from the key-light travel direction. A directional
        /// light TRAVELS along <paramref name="lightDirection"/> (from the sun toward the scene), so the sun sits in
        /// the OPPOSITE direction from the viewer: <c>-normalize(lightDirection)</c>. Sky and lighting agree by
        /// default because both read the same key-light vector. A degenerate (near-zero) light direction falls back
        /// to straight up so the disc still resolves to a stable point.</summary>
        public static Vector3 SunDirectionFromLight(Vector3 lightDirection)
        {
            if (lightDirection.LengthSquared() < 1e-12f) return Vector3.UnitY;
            return -Vector3.Normalize(lightDirection);
        }

        /// <summary>
        /// Place the sun (a DIRECTIONAL light, so a direction, not a position) at a screen NDC point by rotating the
        /// sun direction into VIEW space with <paramref name="view"/> and reading off its right/up components. View
        /// space is right(+x)/up(+y)/-forward(-z), so a NORMALIZED direction's (x,y) are already screen-relative and
        /// in [-1,1]; the disc sits at that screen position (right maps to +ndc.x, up to +ndc.y). This is a stylized
        /// backdrop placement (not a physical point-at-infinity projection, which blows up under the orthographic iso
        /// camera): it keeps the disc agreeing with the light AZIMUTH for BOTH the ortho iso camera and the
        /// perspective follow camera. The sun is "visible" when it is above the view horizon (<c>viewDir.y &gt; 0</c>,
        /// i.e. in the upper sky) - the shader suppresses the disc otherwise, so a sun below the horizon (behind/under
        /// the camera) does not paint into the sky.
        /// </summary>
        public static bool ProjectSunToNdc(Matrix4x4 view, Vector3 sunDir, out Vector2 ndc)
        {
            // Rotate the direction into view space (w=0: ignore the view translation). Row-vector convention, matching
            // the rest of the engine (clip = worldRow * matrix).
            Vector3 d = Vector3.Normalize(sunDir);
            var vd = new Vector3(
                d.X * view.M11 + d.Y * view.M21 + d.Z * view.M31,
                d.X * view.M12 + d.Y * view.M22 + d.Z * view.M32,
                d.X * view.M13 + d.Y * view.M23 + d.Z * view.M33);
            if (vd.Y <= 1e-4f) { ndc = Vector2.Zero; return false; }   // sun at/below the view horizon: not in the sky

            // The normalized view-space (right, up) are already in [-1,1] and are the sun's screen direction.
            ndc = new Vector2(vd.X, vd.Y);
            return true;
        }

        /// <summary>
        /// Sky colour for one background pixel, in screen space. <paramref name="ndc"/> is the pixel's NDC (x,y in
        /// [-1,1], y up). <paramref name="sunNdc"/> is the screen projection of the sun (see
        /// <see cref="ProjectSunToNdc"/>); <paramref name="sunVisible"/> gates the disc (false when the sun is behind
        /// the camera). <paramref name="aspect"/> is width/height, so the disc stays round on a non-square target.
        /// </summary>
        /// <param name="ndc">Pixel NDC (x,y in [-1,1], y up).</param>
        /// <param name="sunNdc">Sun's screen NDC position.</param>
        /// <param name="sunVisible">Whether the sun is in front of the camera.</param>
        /// <param name="aspect">Viewport width / height (keeps the disc round).</param>
        /// <param name="horizon">Horizon (bottom) gradient colour RGB.</param>
        /// <param name="zenith">Zenith (top) gradient colour RGB.</param>
        /// <param name="sunColor">Sun disc + halo colour RGB.</param>
        /// <param name="sunEnabled">Whether the sun disc + halo contribute.</param>
        /// <param name="sunRadius">Screen-space (NDC-y units) radius of the solid disc.</param>
        /// <param name="haloStrength">Peak intensity of the soft halo around the disc (0 = disc only).</param>
        /// <param name="haloFalloff">Screen-space (NDC-y units) width of the halo falloff.</param>
        public static Vector3 Shade(Vector2 ndc, Vector2 sunNdc, bool sunVisible, float aspect,
            Vector3 horizon, Vector3 zenith, Vector3 sunColor,
            bool sunEnabled, float sunRadius, float haloStrength, float haloFalloff)
        {
            // Vertical screen gradient: NDC.y in [-1,1] -> [0,1] (bottom -> top), smoothstep for a soft ramp.
            float up = Math.Clamp(ndc.Y * 0.5f + 0.5f, 0f, 1f);
            float t = Smoothstep(0f, 1f, up);
            Vector3 col = Vector3.Lerp(horizon, zenith, t);

            if (sunEnabled && sunVisible)
            {
                // Aspect-correct the horizontal delta so the disc is round in pixels, then measure the screen-space
                // distance from this pixel to the sun's projected position.
                float dx = (ndc.X - sunNdc.X) * aspect;
                float dy = ndc.Y - sunNdc.Y;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                float feather = MathF.Max(haloFalloff * 0.25f, 1e-4f);
                float disc = 1f - Smoothstep(sunRadius, sunRadius + feather, d);
                float halo = 0f;
                if (haloStrength > 0f && haloFalloff > 0f)
                {
                    float beyond = MathF.Max(0f, d - sunRadius);
                    halo = haloStrength * MathF.Exp(-beyond / haloFalloff);
                }
                float sun = Math.Clamp(disc + halo, 0f, 1f);
                col = Vector3.Lerp(col, sunColor, sun);
            }
            return col;
        }

        /// <summary>GLSL-identical smoothstep (Hermite) so the mirrored shader matches this host math. Returns 0 for
        /// x&lt;=edge0, 1 for x&gt;=edge1, a smooth cubic between.</summary>
        static float Smoothstep(float edge0, float edge1, float x)
        {
            if (edge0 == edge1) return x < edge0 ? 0f : 1f;
            float u = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
            return u * u * (3f - 2f * u);
        }
    }
}
