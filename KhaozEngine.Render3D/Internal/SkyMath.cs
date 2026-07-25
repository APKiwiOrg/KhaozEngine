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
        /// Place the sun disc at a screen NDC point according to <paramref name="anchor"/>: the world-anchored
        /// point-at-infinity projection (<see cref="SunAnchor.World"/>, the default) or the legacy stylized backdrop
        /// (<see cref="SunAnchor.StylizedBackdrop"/>). Returns <c>false</c> (disc suppressed) when the sun has no
        /// on-screen position for that anchor (behind the camera, below the horizon, or a directional sun under an
        /// orthographic projection). Called once per frame by <see cref="Rendering.SkyRenderer.PackUbo"/>. The shader
        /// reads only the resulting NDC, so both anchors share the same GLSL <c>SkyFrag</c> unchanged.
        /// </summary>
        /// <param name="anchor">World (physical projection) or StylizedBackdrop (camera-relative placement).</param>
        /// <param name="view">The camera view matrix (rotation used, translation ignored, w=0).</param>
        /// <param name="projection">The camera projection matrix (used by <see cref="SunAnchor.World"/> only).</param>
        /// <param name="sunDir">World-space direction TO the sun.</param>
        /// <param name="ndc">Sun's screen NDC (x,y in [-1,1], y up) when visible, else <see cref="Vector2.Zero"/>.</param>
        public static bool ProjectSunToNdc(SunAnchor anchor, Matrix4x4 view, Matrix4x4 projection, Vector3 sunDir, out Vector2 ndc)
            => anchor == SunAnchor.StylizedBackdrop
                ? ProjectSunStylizedToNdc(view, sunDir, out ndc)
                : ProjectSunWorldToNdc(view, projection, sunDir, out ndc);

        /// <summary>
        /// World-anchored placement (<see cref="SunAnchor.World"/>): a true point-at-infinity projection. Rotate the
        /// world sun direction into view space (rotation only, so a pure camera MOVE never shifts the disc, only a
        /// camera ROTATION does), reject it when it is not in FRONT of the camera, then project the view-space
        /// direction as a homogeneous point at infinity (<c>w=0</c>) through <paramref name="projection"/> and
        /// perspective-divide. Orbiting the camera keeps the disc fixed over the world direction the sun really lies
        /// in.
        /// <para>
        /// Handedness: the engine's cameras build <c>view</c> with <see cref="Matrix4x4.CreateLookAt"/> (right-handed,
        /// looking down <c>-Z</c> in view space), row-vector convention (<c>clip = worldRow * matrix</c>). So a
        /// direction IN FRONT of the camera has view-space <c>z &lt; 0</c>, and for a perspective projection its clip
        /// <c>w = -viewZ &gt; 0</c> gives a finite NDC. For an ORTHOGRAPHIC projection the clip <c>w</c> collapses to
        /// <c>0</c> (a directional sun has no finite screen position under parallel view rays), so the disc is
        /// suppressed - use <see cref="SunAnchor.StylizedBackdrop"/> for the ortho iso look.
        /// </para>
        /// </summary>
        public static bool ProjectSunWorldToNdc(Matrix4x4 view, Matrix4x4 projection, Vector3 sunDir, out Vector2 ndc)
        {
            // Rotate the direction into view space (w=0: ignore the view translation). Row-vector convention, matching
            // the rest of the engine (clip = worldRow * matrix).
            Vector3 d = Vector3.Normalize(sunDir);
            var vd = new Vector3(
                d.X * view.M11 + d.Y * view.M21 + d.Z * view.M31,
                d.X * view.M12 + d.Y * view.M22 + d.Z * view.M32,
                d.X * view.M13 + d.Y * view.M23 + d.Z * view.M33);
            // Right-handed view (CreateLookAt looks down -Z): in front of the camera is view-space z < 0. A sun at or
            // behind the camera plane has no place in the sky.
            if (vd.Z >= -1e-4f) { ndc = Vector2.Zero; return false; }

            // Project the view-space direction as a point at infinity (w-row = 0) through the projection, then
            // perspective-divide. Perspective: clip.w = -vd.z > 0 -> finite NDC. Orthographic: clip.w = 0 -> no finite
            // screen position for a directional sun (parallel rays), so suppress the disc.
            Vector4 clip = Vector4.Transform(new Vector4(vd, 0f), projection);
            if (clip.W <= 1e-6f) { ndc = Vector2.Zero; return false; }
            ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
            return true;
        }

        /// <summary>
        /// Legacy stylized backdrop placement (<see cref="SunAnchor.StylizedBackdrop"/>): place the sun (a DIRECTIONAL
        /// light, so a direction, not a position) at a screen NDC point by rotating the sun direction into VIEW space
        /// with <paramref name="view"/> and reading off its right/up components. View space is right(+x)/up(+y)/
        /// -forward(-z), so a NORMALIZED direction's (x,y) are already screen-relative and in [-1,1]. The disc sits at
        /// that screen position (right maps to +ndc.x, up to +ndc.y). This is NOT a physical point-at-infinity
        /// projection (which degenerates under the orthographic iso camera): it keeps the disc agreeing with the light
        /// AZIMUTH for BOTH the ortho iso camera and the perspective follow camera. The sun is "visible" when it is
        /// above the view horizon (<c>viewDir.y &gt; 0</c>, i.e. in the upper sky) - a sun below the horizon
        /// (behind/under the camera) does not paint into the sky.
        /// </summary>
        public static bool ProjectSunStylizedToNdc(Matrix4x4 view, Vector3 sunDir, out Vector2 ndc)
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

        /// <summary>
        /// The same sky, evaluated along a world-space DIRECTION instead of a screen pixel. Mirrored by the GLSL
        /// <c>skyAlongDirection</c> in <c>WaterFrag</c>, which uses it for the water surface's reflection: a water
        /// fragment needs to know what the sky looks like where its reflected view ray points, and that has no
        /// screen position (it is usually a direction that is nowhere on screen at all).
        /// <para>
        /// Same gradient, same disc-plus-halo shape and the same <see cref="SkySettings"/> numbers as
        /// <see cref="Shade"/>, with two necessary reinterpretations. The gradient runs off the direction's
        /// ELEVATION (<c>y</c> 0 = horizon, 1 = zenith) rather than off screen height. And the sun distance is the
        /// CHORD between the two unit directions rather than a screen-space NDC distance, so
        /// <paramref name="sunRadius"/> and <paramref name="haloFalloff"/> read as angular sizes here (chord and
        /// angle agree to within a percent over the range a sun disc covers). Keeping one set of knobs for both
        /// evaluations is the point: the sky the water reflects and the sky the camera sees stay the same sky.
        /// </para>
        /// </summary>
        /// <param name="direction">Unit direction to sample (normally the reflected view ray).</param>
        /// <param name="sunDirection">Unit direction TO the sun (see <see cref="SunDirectionFromLight"/>).</param>
        /// <param name="horizon">Horizon gradient colour RGB.</param>
        /// <param name="zenith">Zenith gradient colour RGB.</param>
        /// <param name="sunColor">Sun disc + halo colour RGB.</param>
        /// <param name="sunEnabled">Whether the sun disc + halo contribute at all.</param>
        /// <param name="sunRadius">Angular radius of the solid disc (chord units).</param>
        /// <param name="haloStrength">Peak intensity of the halo around the disc.</param>
        /// <param name="haloFalloff">Angular width of the halo falloff (chord units).</param>
        /// <param name="sunStrength">How much of the disc + halo this evaluation carries, 0..1. The water
        /// reflection scales it down because the sharp part of the reflected sun is already supplied by its own
        /// specular lobe, and carrying both at full strength double-counts the sun.</param>
        public static Vector3 ShadeDirection(Vector3 direction, Vector3 sunDirection,
            Vector3 horizon, Vector3 zenith, Vector3 sunColor,
            bool sunEnabled, float sunRadius, float haloStrength, float haloFalloff, float sunStrength)
        {
            float up = Math.Clamp(direction.Y, 0f, 1f);
            float t = Smoothstep(0f, 1f, up);
            Vector3 col = Vector3.Lerp(horizon, zenith, t);

            float strength = Math.Clamp(sunStrength, 0f, 1f);
            if (sunEnabled && strength > 0f)
            {
                float d = (direction - sunDirection).Length();
                float feather = MathF.Max(haloFalloff * 0.25f, 1e-4f);
                float disc = 1f - Smoothstep(sunRadius, sunRadius + feather, d);
                float halo = 0f;
                if (haloStrength > 0f && haloFalloff > 0f)
                {
                    float beyond = MathF.Max(0f, d - sunRadius);
                    halo = haloStrength * MathF.Exp(-beyond / haloFalloff);
                }
                float sun = Math.Clamp((disc + halo) * strength, 0f, 1f);
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
