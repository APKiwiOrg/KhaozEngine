using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// Pure, GPU-free mirror of the water fragment shader's domain-warped procedural-normal, distance detail fade,
    /// shallow-water blend, fresnel-tint, specular-glint, and shore-fade math (<c>WaterFrag</c> in
    /// <see cref="ShaderSources"/> MUST mirror this exactly, like
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

        // ---- Wave layer table (mirrored verbatim by the GLSL waterNormal) ---------------------------------------
        // Three directional wave layers. Their directions are unit vectors at 20, 115 and -51 degrees: none is
        // axis-aligned, and no two are parallel, so no layer lines up with the world grid. Their frequency
        // multipliers are mutually irrational (1, the golden ratio, sqrt 7), so the layers' repeat periods never
        // come back into phase and their sum has no finite tile.
        //
        // This replaced a two-octave field whose octaves were both AXIS-SEPARABLE (dHdx depended only on x, dHdz
        // only on z) and exactly 2*pi*waveScale-periodic in each axis. That separability plus that short shared
        // period IS the checkerboard repeat players reported at distance; it is not a knob and cannot be tuned out,
        // which is why the layer set is unconditional while every other addition here has an off switch.
        const float D1X = 0.93969262f, D1Z = 0.34202014f;    // 20 deg
        const float D2X = -0.42261826f, D2Z = 0.90630779f;   // 115 deg
        const float D3X = 0.62932039f, D3Z = -0.77714596f;   // -51 deg
        const float F1 = 1f, F2 = 1.61803399f, F3 = 2.64575131f;   // 1, phi, sqrt 7 (mutually irrational)
        const float A1 = 1f, A2 = 0.62f, A3 = 0.32f;               // amplitudes, chosen so the summed max slope
                                                                   // (~1.9/waveScale per axis, near-isotropic) lands
                                                                   // close to the old field's 2/waveScale: same chop
                                                                   // character at the same NormalStrength.
        const float S1 = 1f, S2 = -1.37f, S3 = 0.83f;              // per-layer scroll rate (sign = drift direction)

        // Domain-warp constants. WarpFrequency is ~1/5 of the base layer's, so the warp bends the field over a much
        // longer distance than the waves repeat over; WarpFrequencyB is an incommensurate second frequency so the
        // warp itself does not tile either. WarpSpeed is well below the wave scroll so the warp reads as a slow
        // current rather than extra chop.
        const float WarpFrequency = 0.21f, WarpFrequencyB = 0.57f, WarpSpeed = 0.23f;

        /// <summary>
        /// Slow, large-scale displacement of the wave sample position, applied BEFORE the wave layers so their
        /// (quasi-periodic) pattern is bent over a distance several times their own wavelength. Mirrors the GLSL
        /// <c>domainWarp</c> exactly. <paramref name="warpStrength"/> is in multiples of
        /// <paramref name="waveScale"/>; 0 or less returns the input position unchanged.
        /// <para>
        /// The warp's Jacobian is deliberately NOT folded into <see cref="WaveNormal"/>'s analytic slope: the warp
        /// is low-frequency next to the layers it feeds, so treating it as a slowly varying reparametrization costs
        /// a few percent of gradient accuracy on a normal-only stylized surface and saves four more transcendentals
        /// per pixel. The result is still a smooth, well-formed, unit-length normal field.
        /// </para>
        /// </summary>
        /// <param name="worldX">World-space X of the shaded point.</param>
        /// <param name="worldZ">World-space Z of the shaded point.</param>
        /// <param name="scrollTime">Already-scaled clock (<c>timeSeconds * waveSpeed</c>), so the warp drifts with
        /// the waves it displaces.</param>
        /// <param name="waveScale">Base wave wavelength; sets both the warp's own wavelength and its amplitude.</param>
        /// <param name="warpStrength">Displacement in multiples of <paramref name="waveScale"/> (0 = no warp).</param>
        public static Vector2 DomainWarp(float worldX, float worldZ, float scrollTime, float waveScale, float warpStrength)
        {
            if (warpStrength <= 0f) return new Vector2(worldX, worldZ);
            float scale = MathF.Max(waveScale, 1e-4f);
            float k = WarpFrequency / scale;
            float wt = scrollTime * WarpSpeed;
            float ax = MathF.Sin(worldZ * k + wt) + 0.7f * MathF.Sin(worldX * k * WarpFrequencyB - wt * 1.31f);
            float az = MathF.Cos(worldX * k - wt * 0.79f) + 0.7f * MathF.Cos(worldZ * k * WarpFrequencyB + wt * 1.17f);
            float amp = warpStrength * scale;
            return new Vector2(worldX + ax * amp, worldZ + az * amp);
        }

        /// <summary>
        /// Distance attenuation for the two FINE wave layers: 1 at the camera, falling to
        /// <paramref name="distantScale"/> (clamped 0..1) at and beyond <paramref name="fadeDistance"/>. Mirrors the
        /// GLSL <c>detailScaleFor</c> exactly. <paramref name="fadeDistance"/> 0 or less disables the fade
        /// (returns 1 everywhere). Without this the fine layers keep their full amplitude out to the horizon, where
        /// their wavelength falls below a pixel and they alias into a crawling moire that a tight glint exponent
        /// then turns into sparkle.
        /// </summary>
        public static float DetailScale(float cameraDistance, float fadeDistance, float distantScale)
        {
            if (fadeDistance <= 0f) return 1f;
            float s = Smoothstep(0f, fadeDistance, cameraDistance);
            float far = Math.Clamp(distantScale, 0f, 1f);
            return (1f - s) + far * s;   // mix(1, far, s)
        }

        /// <summary>
        /// Three-layer scrolling perturbation of a flat-up normal (0,1,0), in world XZ, time-driven, over a
        /// domain-warped sample position. Mirrors the GLSL <c>waterNormal</c> function exactly (same op order, same
        /// constants) so C# and the shader agree bit-for-bit modulo float rounding. Layer 1 is the broad base swell
        /// and always runs at full amplitude; layers 2 and 3 are the fine detail and are scaled by
        /// <paramref name="detailScale"/> (see <see cref="DetailScale"/>).
        /// </summary>
        /// <param name="worldX">World-space X of the shaded point.</param>
        /// <param name="worldZ">World-space Z of the shaded point.</param>
        /// <param name="timeSeconds">Animation clock (frozen for a deterministic golden).</param>
        /// <param name="waveScale">World-space wavelength of the base layer (larger = broader swell).</param>
        /// <param name="waveSpeed">Scroll speed.</param>
        /// <param name="normalStrength">Perturbation amplitude (0 = flat mirror normal).</param>
        /// <param name="warpStrength">Domain-warp displacement in multiples of <paramref name="waveScale"/>.</param>
        /// <param name="detailScale">Amplitude multiplier for the two fine layers (1 = full, 0 = base swell only).</param>
        public static Vector3 WaveNormal(float worldX, float worldZ, float timeSeconds,
            float waveScale, float waveSpeed, float normalStrength, float warpStrength, float detailScale)
        {
            float scale = MathF.Max(waveScale, 1e-4f);
            float invScale = 1f / scale;
            float t = timeSeconds * waveSpeed;

            Vector2 p = DomainWarp(worldX, worldZ, t, waveScale, warpStrength);

            // Each layer is a plane wave h_i = A_i * sin(k_i * (d_i . p) + s_i * t), so its analytic slope is
            // A_i * k_i * d_i * cos(same phase). cos() is the derivative of sin(); one cos per layer is the whole
            // per-pixel cost. The heights themselves are never sampled (normal-only surface).
            float k1 = invScale * F1, k2 = invScale * F2, k3 = invScale * F3;
            float g1 = A1 * k1 * MathF.Cos((D1X * p.X + D1Z * p.Y) * k1 + t * S1);
            float g2 = A2 * k2 * MathF.Cos((D2X * p.X + D2Z * p.Y) * k2 + t * S2) * detailScale;
            float g3 = A3 * k3 * MathF.Cos((D3X * p.X + D3Z * p.Y) * k3 + t * S3) * detailScale;

            float dHdx = g1 * D1X + g2 * D2X + g3 * D3X;
            float dHdz = g1 * D1Z + g2 * D2Z + g3 * D3Z;

            // Tilt the flat-up normal by that slope, scaled by normalStrength, then renormalize.
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

        /// <summary>
        /// Shallow-water blend weight: 1 where the ground touches the surface, falling to 0 at
        /// <paramref name="shallowDepth"/> below it. Same <paramref name="depthBelowSurface"/> measurement
        /// <see cref="ShoreFade"/> uses (<c>surfaceY - groundY</c>), a different and much longer distance: the
        /// shallows TINT reads over metres while the waterline ALPHA feather reads over centimetres, so the two
        /// have separate knobs. <paramref name="shallowDepth"/> &lt;= 0 disables the blend (always 0).
        /// </summary>
        public static float ShallowWeight(float depthBelowSurface, float shallowDepth)
        {
            if (shallowDepth <= 0f) return 0f;
            float t = Math.Clamp(depthBelowSurface / shallowDepth, 0f, 1f);
            return 1f - Smoothstep(0f, 1f, t);
        }

        /// <summary>Blend the BODY colour from <paramref name="deep"/> toward <paramref name="shallow"/> by the
        /// shallow weight (see <see cref="ShallowWeight"/>). Runs BEFORE <see cref="FresnelTint"/>, so a grazing
        /// view of the shallows still picks up the horizon/sky tint on top rather than losing it to the shore
        /// colour.</summary>
        public static Vector3 ShallowTint(Vector3 deep, Vector3 shallow, float depthBelowSurface, float shallowDepth) =>
            Vector3.Lerp(deep, shallow, ShallowWeight(depthBelowSurface, shallowDepth));

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
