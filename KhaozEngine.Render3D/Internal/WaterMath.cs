using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// Pure, GPU-free mirror of the water fragment shader's procedural-normal, fresnel-tint, specular-glint, and
    /// shore-fade math (<c>WaterFrag</c> in <see cref="ShaderSources"/> MUST mirror this exactly, like
    /// <see cref="SkyMath"/> mirrors <c>SkyFrag</c> and <see cref="SurfaceShading"/> mirrors <c>ModelFrag</c>).
    /// Documents the intended math and makes it headless-unit-testable. No GPU state, no allocations.
    /// </summary>
    internal static class WaterMath
    {
        /// <summary>Grid resolution (vertices per side) for a queued <see cref="WaterPlane"/>: fixed at a
        /// screen-space-sufficient tessellation. The wave animation is entirely per-pixel (fragment), so the CPU
        /// grid only needs enough vertices for the depth test / shore fade to interpolate smoothly across the
        /// plane; it is not a simulation mesh. 17x17 vertices (16x16 quads) is comfortably above what a
        /// low-poly-style scene needs while staying a small, fixed-cost upload.</summary>
        public const int GridResolution = 17;

        /// <summary>
        /// Two-octave scrolling sine perturbation of a flat-up normal (0,1,0), in world XZ, time-driven. Mirrors
        /// the GLSL <c>waterNormal</c> function exactly (same op order, same constants) so C# and the shader agree
        /// bit-for-bit modulo float rounding. Octave 2 runs at double the frequency, half the amplitude, and a
        /// perpendicular scroll direction, so the two octaves don't simply add in phase (avoids a flat-looking
        /// single sine ripple).
        /// </summary>
        /// <param name="worldX">World-space X of the shaded point.</param>
        /// <param name="worldZ">World-space Z of the shaded point.</param>
        /// <param name="timeSeconds">Animation clock (frozen for a deterministic golden).</param>
        /// <param name="waveScale">World-space tiling size of octave 1 (larger = broader swell).</param>
        /// <param name="waveSpeed">Scroll speed.</param>
        /// <param name="normalStrength">Perturbation amplitude (0 = flat mirror normal).</param>
        public static Vector3 WaveNormal(float worldX, float worldZ, float timeSeconds,
            float waveScale, float waveSpeed, float normalStrength)
        {
            float scale = MathF.Max(waveScale, 1e-4f);
            float invScale = 1f / scale;
            float t = timeSeconds * waveSpeed;

            // Octave 1: axis-aligned scroll along +X, +Z.
            float p1X = worldX * invScale + t;
            float p1Z = worldZ * invScale + t * 0.7f;
            float h1 = MathF.Sin(p1X) + MathF.Sin(p1Z);

            // Octave 2: double frequency, half amplitude, scrolling along the perpendicular diagonal - breaks the
            // single-direction symmetry of octave 1 so the surface reads as choppy water, not a plain sine grate.
            float p2X = (worldX - worldZ) * invScale * 2f - t * 1.3f;
            float p2Z = (worldX + worldZ) * invScale * 2f + t * 0.9f;
            float h2 = (MathF.Sin(p2X) + MathF.Sin(p2Z)) * 0.5f;

            // Analytic partial derivatives of the height field w.r.t. world X/Z give the slope; tilt the flat-up
            // normal by that slope, scaled by normalStrength, then renormalize. cos() is the derivative of sin().
            float dHdx = (MathF.Cos(p1X) * invScale + MathF.Cos(p2X) * invScale * 2f * 0.5f);
            float dHdz = (MathF.Cos(p1Z) * invScale + MathF.Cos(p2Z) * invScale * 2f * 0.5f);
            _ = h1; _ = h2;   // height itself is not sampled (normal-only surface), kept for clarity/readability

            Vector3 n = new(-dHdx * normalStrength, 1f, -dHdz * normalStrength);
            float len = n.Length();
            return len > 1e-8f ? n / len : Vector3.UnitY;
        }

        /// <summary>Schlick-style fresnel term: 0 at normal incidence (looking straight down, deep tint dominates),
        /// rising toward 1 at grazing angles (horizon tint dominates). <paramref name="ndotv"/> is
        /// <c>dot(normal, viewDir)</c>, clamped to [0,1] by the caller's normalize.</summary>
        public static float Fresnel(float ndotv)
        {
            float x = Math.Clamp(1f - ndotv, 0f, 1f);
            float x2 = x * x;
            return x2 * x2 * x;   // (1-ndotv)^5, the standard Schlick approximation at F0=0
        }

        /// <summary>Blend <paramref name="deep"/> toward <paramref name="horizon"/> by the fresnel weight
        /// (see <see cref="Fresnel"/>).</summary>
        public static Vector3 FresnelTint(Vector3 deep, Vector3 horizon, float ndotv) =>
            Vector3.Lerp(deep, horizon, Fresnel(ndotv));

        /// <summary>Blinn-Phong-style specular sun glint: a small, bright highlight from the key light's reflection
        /// off the (perturbed) water normal. Mirrors the model pass's Blinn-Phong term (half-vector between view and
        /// light) but is intentionally NOT routed through <c>computeLighting</c> (LightingCommonGlsl): water needs
        /// its own small, tight, strength/exponent pair distinct from any mesh's material, and duplicating one
        /// `pow(max(dot(N,H),0),exp)*strength` line is cheaper and clearer than threading a new call shape through
        /// the shared block.</summary>
        /// <param name="normal">Shaded (perturbed) surface normal, normalized.</param>
        /// <param name="viewDir">Unit vector from the surface point toward the camera.</param>
        /// <param name="lightDirToSun">Unit vector from the surface point toward the light (i.e.
        /// <c>-normalize(lightTravelDirection)</c>).</param>
        /// <param name="strength">Overall glint strength (<see cref="WaterSettings.GlintStrength"/>).</param>
        /// <param name="exponent">Specular tightness (<see cref="WaterSettings.GlintExponent"/>).</param>
        public static float SunGlint(Vector3 normal, Vector3 viewDir, Vector3 lightDirToSun, float strength, float exponent)
        {
            if (strength <= 0f) return 0f;
            Vector3 h = viewDir + lightDirToSun;
            float hLen = h.Length();
            if (hLen < 1e-8f) return 0f;
            h /= hLen;
            float ndoth = MathF.Max(Vector3.Dot(normal, h), 0f);
            return MathF.Pow(ndoth, MathF.Max(exponent, 1f)) * strength;
        }

        /// <summary>
        /// Shore-fade alpha multiplier: 1 in open water (the ground is far below the surface), softening to 0 as
        /// the reconstructed ground approaches the surface height (the waterline). <paramref name="depthBelowSurface"/>
        /// is <c>surfaceY - groundY</c> (world units; positive when the ground is below the surface, as it must be
        /// for the water pass's own depth test to have passed). <paramref name="fadeDistance"/> &lt;= 0 disables the
        /// fade (always 1, a hard-edged plane).
        /// </summary>
        public static float ShoreFade(float depthBelowSurface, float fadeDistance)
        {
            if (fadeDistance <= 0f) return 1f;
            float t = Math.Clamp(depthBelowSurface / fadeDistance, 0f, 1f);
            return Smoothstep(0f, 1f, t);
        }

        /// <summary>GLSL-identical smoothstep (Hermite), matching <see cref="SkyMath"/>'s copy so every mirrored
        /// pass agrees on the same curve.</summary>
        public static float Smoothstep(float edge0, float edge1, float x)
        {
            if (edge0 == edge1) return x < edge0 ? 0f : 1f;
            float u = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
            return u * u * (3f - 2f * u);
        }

        /// <summary>Build the <see cref="GridResolution"/>x<see cref="GridResolution"/> XZ vertex grid for a
        /// <see cref="WaterPlane"/> request into <paramref name="destination"/> (must be at least
        /// <see cref="GridResolution"/>*<see cref="GridResolution"/> long), returning the vertex count. Pure CPU
        /// layout math (no GPU calls) so the tessellation sizing is headless-testable.</summary>
        public static int BuildGridPositions(in WaterPlane plane, Span<Vector3> destination)
        {
            const int n = GridResolution;
            int count = 0;
            for (int z = 0; z < n; z++)
            {
                float tz = n > 1 ? (float)z / (n - 1) : 0f;
                float wz = plane.CenterZ - plane.HalfExtentZ + tz * (2f * plane.HalfExtentZ);
                for (int x = 0; x < n; x++)
                {
                    float tx = n > 1 ? (float)x / (n - 1) : 0f;
                    float wx = plane.CenterX - plane.HalfExtentX + tx * (2f * plane.HalfExtentX);
                    destination[count++] = new Vector3(wx, plane.SurfaceY, wz);
                }
            }
            return count;
        }

        /// <summary>Number of triangle-list indices for the <see cref="GridResolution"/> grid: (n-1)*(n-1) quads,
        /// 2 triangles/quad, 3 indices/triangle.</summary>
        public const int GridIndexCount = (GridResolution - 1) * (GridResolution - 1) * 6;

        /// <summary>Build the triangle-list indices for the fixed <see cref="GridResolution"/> grid (matching
        /// <see cref="BuildGridPositions"/>'s row-major vertex order) into <paramref name="destination"/> (must be
        /// at least <see cref="GridIndexCount"/> long), returning the index count.</summary>
        public static int BuildGridIndices(Span<uint> destination)
        {
            const int n = GridResolution;
            int count = 0;
            for (int z = 0; z < n - 1; z++)
            {
                for (int x = 0; x < n - 1; x++)
                {
                    uint i0 = (uint)(z * n + x);
                    uint i1 = (uint)(z * n + x + 1);
                    uint i2 = (uint)((z + 1) * n + x);
                    uint i3 = (uint)((z + 1) * n + x + 1);
                    // Clockwise winding (matches the engine's GpuFrontFace.Clockwise convention elsewhere).
                    destination[count++] = i0; destination[count++] = i2; destination[count++] = i1;
                    destination[count++] = i1; destination[count++] = i2; destination[count++] = i3;
                }
            }
            return count;
        }
    }
}
