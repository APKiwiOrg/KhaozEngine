using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// Pure, GPU-free mirror of the water FRAGMENT shader's domain warp, distance detail fade,
    /// depth grading (both the Beer-Lambert and the legacy two-stop path), fresnel/reflection tint, GGX and legacy
    /// specular glint, foam, and shore-fade math, plus the CPU-side surface-grid layout (<c>WaterFrag</c> in
    /// <see cref="ShaderSources"/> MUST mirror this exactly, like
    /// <see cref="SkyMath"/> mirrors <c>SkyFrag</c> and <see cref="SurfaceShading"/> mirrors <c>ModelFrag</c>).
    /// The VERTEX shader's Gerstner swell is the sibling mirror <see cref="GerstnerWaves"/>, and the ripple slope
    /// spectrum is <see cref="RippleSpectrum"/>.
    /// Documents the intended math and makes it headless-unit-testable. No GPU state, no allocations.
    /// </summary>
    internal static class WaterMath
    {
        /// <summary>
        /// Grid resolution (vertices per side) for a queued <see cref="WaterPlane"/>: a fixed vertex budget, spread
        /// non-uniformly by <see cref="FocusWarp"/>. 97x97 vertices = 9,409 vertices, 18,432 triangles and 55,296
        /// indices per plane, still ONE draw and one 113 KB vertex upload per plane per frame.
        /// <para>
        /// It was 17x17 while the surface was flat and every wave was a per-pixel normal: the grid only had to
        /// interpolate the depth test and the shore fade. Once the Gerstner swell displaces real geometry the grid
        /// IS the wave, and 17x17 across a 1200-unit plane puts vertices 75 units apart, which cannot carry a
        /// 42-unit wavelength at all. The budget is fixed rather than derived from the plane size so the vertex and
        /// index buffers stay allocate-once and a large plane costs a large plane's worth of resolution, not a
        /// quadratic blowup; <see cref="WaterSettings.GridFocusBias"/> is what puts that budget where it reads.
        /// </para>
        /// </summary>
        public const int GridResolution = 97;

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
        /// The warp's Jacobian is deliberately NOT folded into the ripple spectrum's analytic slope: the warp
        /// is low-frequency next to the components it feeds, so treating it as a slowly varying reparametrization costs
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

        // ---- Depth grading: per-channel Beer-Lambert absorption ---------------------------------------------

        /// <summary>
        /// Per-channel transmittance <c>exp(-coefficient * depth)</c> through <paramref name="depthBelowSurface"/>
        /// world units of water. 1 at the waterline, falling to 0 with depth, and falling at a DIFFERENT rate per
        /// channel, which is the whole point: red dies within a couple of metres while blue survives, so the
        /// gradient bends through green-teal on its way to the deep colour instead of running straight down the
        /// line between two colours the way a scalar lerp does. Negative depths (which the reconstruction can
        /// produce for a fragment at the very waterline) clamp to 0.
        /// </summary>
        public static Vector3 Transmittance(Vector3 absorptionPerMetre, float depthBelowSurface)
        {
            float d = MathF.Max(depthBelowSurface, 0f);
            return new Vector3(
                MathF.Exp(-absorptionPerMetre.X * d),
                MathF.Exp(-absorptionPerMetre.Y * d),
                MathF.Exp(-absorptionPerMetre.Z * d));
        }

        /// <summary>Grade the body colour from <paramref name="shallow"/> at the waterline down to
        /// <paramref name="deep"/>, per channel, by <see cref="Transmittance"/>. Replaces
        /// <see cref="ShallowTint"/>'s two-stop smoothstep when <see cref="WaterSettings.AbsorptionPerMetre"/> is
        /// non-zero; an all-zero coefficient makes every channel's transmittance 1, so the caller must switch to
        /// the legacy path rather than calling this (a transmittance of 1 everywhere would paint the whole surface
        /// the shallow colour).</summary>
        public static Vector3 AbsorbTint(Vector3 deep, Vector3 shallow, Vector3 absorptionPerMetre, float depthBelowSurface)
        {
            Vector3 t = Transmittance(absorptionPerMetre, depthBelowSurface);
            return new Vector3(
                deep.X + (shallow.X - deep.X) * t.X,
                deep.Y + (shallow.Y - deep.Y) * t.Y,
                deep.Z + (shallow.Z - deep.Z) * t.Z);
        }

        /// <summary>Scalar weight for grading a non-colour quantity (the body ALPHA) on the same absorption curve:
        /// the mean of the three channels' transmittance, so alpha follows the colour it belongs to instead of
        /// needing its own curve.</summary>
        public static float AbsorbWeight(Vector3 absorptionPerMetre, float depthBelowSurface)
        {
            Vector3 t = Transmittance(absorptionPerMetre, depthBelowSurface);
            return (t.X + t.Y + t.Z) * (1f / 3f);
        }

        // ---- Reflection + GGX glint ------------------------------------------------------------------------

        /// <summary>Blend the flat fallback horizon tint toward the sky colour sampled along the reflected view
        /// ray. <paramref name="strength"/> 0 returns <paramref name="flatHorizon"/> exactly (the 14.22.0
        /// behaviour, one colour for the whole surface), 1 returns the reflected sky.</summary>
        public static Vector3 ReflectionColor(Vector3 flatHorizon, Vector3 skyAlongReflectedRay, float strength) =>
            Vector3.Lerp(flatHorizon, skyAlongReflectedRay, Math.Clamp(strength, 0f, 1f));

        /// <summary>
        /// GGX / Trowbridge-Reitz specular sun glint, PEAK-NORMALIZED (the lobe is 1 when the half-vector aligns
        /// with the normal and falls off with the GGX tail), times a Smith-Schlick visibility term and N.L. Peak
        /// normalization is deliberate: it drops the <c>1/(pi*alpha^2)</c> factor that would send a tight lobe into
        /// the thousands, and it makes <see cref="WaterSettings.GlintStrength"/> mean the same brightness here as
        /// it does in the legacy <see cref="SunGlint"/> path, so the two are directly A/B-able.
        /// <para>
        /// Why GGX rather than Blinn-Phong: the tail. A Phong lobe falls off far too fast, so the sun path is a
        /// hard-edged blob; GGX's long tail is what makes the glitter read as thousands of individual facets
        /// fading into a haze, and its roughness parameter is the handle that lets the far field widen into that
        /// haze instead of aliasing (see <see cref="GlintRoughnessAt"/>).
        /// </para>
        /// </summary>
        /// <param name="normal">Shaded (perturbed) surface normal, normalized.</param>
        /// <param name="viewDir">Unit vector from the surface point toward the camera.</param>
        /// <param name="lightDirToSun">Unit vector from the surface point toward the light.</param>
        /// <param name="roughness">Perceptual roughness; alpha is its square, per the usual remap.</param>
        /// <param name="strength">Overall glint strength (<see cref="WaterSettings.GlintStrength"/>).</param>
        public static float GgxGlint(Vector3 normal, Vector3 viewDir, Vector3 lightDirToSun, float roughness, float strength)
        {
            if (strength <= 0f) return 0f;
            Vector3 h = viewDir + lightDirToSun;
            float hLen = h.Length();
            if (hLen < 1e-8f) return 0f;
            h /= hLen;

            float a = MathF.Max(roughness, 1e-3f);
            a *= a;                       // alpha = roughness^2
            float a2 = a * a;
            float ndoth = MathF.Max(Vector3.Dot(normal, h), 0f);
            float denom = ndoth * ndoth * (a2 - 1f) + 1f;
            float lobe = a2 / MathF.Max(denom, 1e-6f);
            lobe *= lobe;                 // peak 1 at ndoth == 1

            float ndotl = MathF.Max(Vector3.Dot(normal, lightDirToSun), 0f);
            float ndotv = MathF.Max(Vector3.Dot(normal, viewDir), 0f);
            float k = a * 0.5f;           // Smith-Schlick visibility
            float gv = ndotv / (ndotv * (1f - k) + k);
            float gl = ndotl / (ndotl * (1f - k) + k);
            return lobe * gv * gl * ndotl * strength;
        }

        /// <summary>
        /// Effective glint roughness at a shaded point: <paramref name="nearRoughness"/> widened toward
        /// <paramref name="distantRoughness"/> by whichever of two under-sampling measures is worse.
        /// <list type="number">
        /// <item>Camera distance over <paramref name="fadeDistance"/>, the same curve the ripple detail fade uses,
        /// so the two stay in step and one knob tunes both.</item>
        /// <item>The pixel's world-space FOOTPRINT against the ripple wavelength. This is the measure that is
        /// actually correct: what aliases is a wave whose wavelength has fallen below a pixel, and distance is only
        /// a proxy for that. It is the wrong proxy under a wide field of view, under an orthographic camera (where
        /// the footprint barely changes with distance at all), and at any resolution other than the one the
        /// distance default was tuned at.</item>
        /// </list>
        /// Widening the LOBE rather than fading the normals keeps the sub-pixel detail as variance instead of
        /// throwing it away, which is why the far field ends up as a soft sheen rather than either a crawling
        /// sparkle or a dead mirror.
        /// </summary>
        /// <param name="nearRoughness">Roughness at full sampling.</param>
        /// <param name="distantRoughness">Roughness where the surface is fully under-sampled (raised to at least
        /// <paramref name="nearRoughness"/>, so an inverted pair cannot sharpen the far field).</param>
        /// <param name="cameraDistance">Distance from the eye to the shaded point.</param>
        /// <param name="fadeDistance">Distance over which measure 1 ramps; 0 or less disables it.</param>
        /// <param name="pixelFootprint">World units this pixel spans on the surface (the shader's
        /// <c>fwidth</c>); 0 or less disables measure 2.</param>
        /// <param name="rippleWavelength">World-space wavelength of the base ripple layer.</param>
        public static float GlintRoughnessAt(float nearRoughness, float distantRoughness, float cameraDistance,
            float fadeDistance, float pixelFootprint, float rippleWavelength)
        {
            float far = MathF.Max(distantRoughness, nearRoughness);
            float distanceT = fadeDistance <= 0f ? 0f : Smoothstep(0f, fadeDistance, cameraDistance);
            float aliasT = 0f;
            if (pixelFootprint > 0f && rippleWavelength > 1e-6f)
                aliasT = Math.Clamp(pixelFootprint / (rippleWavelength * 0.5f), 0f, 1f);
            float t = MathF.Max(distanceT, aliasT);
            return nearRoughness + (far - nearRoughness) * t;
        }

        /// <summary>
        /// Tilt a flat-up normal by a slope, scaled by <paramref name="normalStrength"/>, then renormalize. Split
        /// out of the retired three-cosine <c>WaveNormal</c> so the ripple spectrum (see
        /// <see cref="RippleSpectrum"/>, which produces a slope and a variance alongside it) can share the same
        /// tail and stay bit-comparable with the field it replaced.
        /// </summary>
        public static Vector3 SlopeToNormal(float dhdx, float dhdz, float normalStrength)
        {
            Vector3 n = new(-dhdx * normalStrength, 1f, -dhdz * normalStrength);
            float len = n.Length();
            return len > 1e-8f ? n / len : Vector3.UnitY;
        }

        /// <summary>Combine the swell's smooth analytic normal with the ripple field's perturbation of a flat-up
        /// normal: take the ripple's HORIZONTAL tilt and add it into the swell normal, then renormalize. Both
        /// terms are near-vertical by construction, so this cheap additive blend is well behaved and needs no
        /// tangent frame - which the water surface does not have, since the grid carries position only.</summary>
        public static Vector3 CombineNormals(Vector3 swellNormal, Vector3 rippleNormal)
        {
            var n = new Vector3(swellNormal.X + rippleNormal.X, swellNormal.Y, swellNormal.Z + rippleNormal.Z);
            float len = n.Length();
            return len > 1e-8f ? n / len : Vector3.UnitY;
        }

        // ---- Foam ------------------------------------------------------------------------------------------

        // Foam break-up layer directions (30, 110 and -40 degrees) and frequency multipliers (1, sqrt 2, sqrt 5).
        // Same construction as the ripple layers above and for the same reason: no layer is axis-aligned and the
        // multipliers are mutually irrational, so the mask has no finite tile. A product of axis-aligned sines
        // would put a visible grid of foam blobs across the whole ocean.
        const float FoamD1X = 0.86602540f, FoamD1Z = 0.5f;
        const float FoamD2X = -0.34202014f, FoamD2Z = 0.93969262f;
        const float FoamD3X = 0.76604444f, FoamD3Z = -0.64278761f;
        const float FoamF1 = 1f, FoamF2 = 1.41421356f, FoamF3 = 2.23606798f;
        const float FoamA1 = 1f, FoamA2 = 0.75f, FoamA3 = 0.55f;
        const float FoamS1 = 0.90f, FoamS2 = -1.27f, FoamS3 = 0.61f;
        // 1 / (FoamA1 + FoamA2 + FoamA3), maps the summed layers back into [-1,1]. Written as a literal, not as
        // the division, so the GLSL mirror can carry the same literal and the two round identically.
        const float FoamNorm = 0.43478261f;
        const float FoamEdge0 = -0.30f, FoamEdge1 = 0.42f;        // hard-ish threshold: graphic lobes, not scum

        /// <summary>
        /// Procedural foam break-up mask in 0..1: three non-axis-aligned scrolling layers summed and thresholded
        /// tightly, so the result is clean graphic shapes with hard-ish edges rather than the soft photoreal scum a
        /// gentle threshold gives. Both foam sources are multiplied by this, which is what stops the shoreline band
        /// reading as a painted-on ring and the whitecaps as an analytic contour of the wave field.
        /// </summary>
        /// <param name="worldX">World X of the shaded point.</param>
        /// <param name="worldZ">World Z of the shaded point.</param>
        /// <param name="scrollTime">Already-scaled clock (<c>timeSeconds * waveSpeed</c>), so foam drifts with the
        /// water rather than on a clock of its own.</param>
        /// <param name="scale">World-space size of the pattern (smaller = finer, busier foam).</param>
        public static float FoamPattern(float worldX, float worldZ, float scrollTime, float scale)
        {
            float s = MathF.Max(scale, 1e-4f);
            float px = worldX / s, pz = worldZ / s;
            float a = FoamA1 * MathF.Sin((FoamD1X * px + FoamD1Z * pz) * FoamF1 + scrollTime * FoamS1);
            float b = FoamA2 * MathF.Sin((FoamD2X * px + FoamD2Z * pz) * FoamF2 + scrollTime * FoamS2);
            float c = FoamA3 * MathF.Sin((FoamD3X * px + FoamD3Z * pz) * FoamF3 + scrollTime * FoamS3);
            return Smoothstep(FoamEdge0, FoamEdge1, (a + b + c) * FoamNorm);
        }

        /// <summary>Softness of the whitecap threshold, in normalized-fold units. Narrow on purpose: a wide ramp
        /// turns whitecaps into a haze over the whole swell instead of a crest that breaks.</summary>
        const float WhitecapSoftness = 0.18f;

        /// <summary>
        /// Whitecap weight from the Gerstner fold factor (see <c>GerstnerWaves.Evaluate</c>): foam appears where
        /// the surface is compressed past a threshold set by <paramref name="coverage"/>. Because the fold factor
        /// is already normalized by the swell's steepness, a given coverage means the same fraction of the sea at
        /// any steepness. <paramref name="coverage"/> 0 puts the threshold at a fold the field never reaches (no
        /// whitecaps); 1 puts it at 0, so anything compressed at all foams. Troughs have a fold of 0 and never
        /// foam at any coverage, which is correct: a wave breaks at its crest.
        /// </summary>
        public static float Whitecap(float fold, float coverage)
        {
            float c = Math.Clamp(coverage, 0f, 1f);
            float threshold = 1f - c;
            return Smoothstep(threshold, threshold + WhitecapSoftness, fold);
        }

        /// <summary>Shoreline foam band weight: 1 where the ground touches the surface, falling to 0 at
        /// <paramref name="width"/> below it. Measured on the same reconstructed depth
        /// (<c>surfaceY - groundY</c>) the shore fade and the absorption use - but that surface Y is the DISPLACED
        /// one, so a passing crest deepens the water under it and the foam line runs up and down the beach with
        /// the swell for free. <paramref name="width"/> 0 or less disables the band.</summary>
        public static float ShoreFoam(float depthBelowSurface, float width)
        {
            if (width <= 0f) return 0f;
            float t = Math.Clamp(depthBelowSurface / width, 0f, 1f);
            return 1f - Smoothstep(0f, 1f, t);
        }

        /// <summary>Combine both foam sources with the break-up mask and the overall strength, clamped to 0..1.
        /// The two sources take a max rather than a sum so a whitecap breaking inside the shore band does not add
        /// up past white.</summary>
        public static float FoamAmount(float whitecap, float shoreFoam, float pattern, float strength) =>
            Math.Clamp(MathF.Max(whitecap, shoreFoam) * pattern * MathF.Max(strength, 0f), 0f, 1f);

        /// <summary>Span of <see cref="FftFoamBreakup"/>'s ramp: <c>oceanFoam</c> values above this are fully
        /// broken up, below it partially or not at all. Wider than <see cref="WhitecapSoftness"/> on purpose -
        /// <c>oceanFoam</c> is a compute-pass accumulator (<c>KhaozEngine#343</c>) whose typical peaks sit well
        /// under 1, so a narrow ramp would leave most of the sea with no break-up structure at all.</summary>
        const float FftBreakupSpan = 0.5f;

        /// <summary>
        /// FFT-mode foam break-up mask in 0..1, replacing <see cref="FoamPattern"/> for both the crest and the
        /// shoreline band whenever <c>WaterWaveSource.FftOcean</c> is active (<c>KhaozEngine#343</c>). Sourced
        /// from the ocean compute pass's own foam/Jacobian accumulator (<paramref name="oceanFoam"/>, the same
        /// value <c>crest</c> already reads) rather than a fixed world-space lattice: <c>oceanFoam</c> has
        /// already been through the vertex stage's focus/warp/de-tile sampling frame, so it carries genuine
        /// wave-scale structure and can never re-tile, unlike <see cref="FoamPattern"/>'s fixed period, which
        /// just stacks a second, unrelated repeat on top of the FFT cascades' own. Warping <c>FoamPattern</c>'s
        /// input coordinates instead was tried and does not work: the domain warp's wavelength is on the order
        /// of a kilometre, far too coarse to break an 8-ish metre lattice period.
        /// <para>
        /// A self-modulated contrast curve rather than a plain pass-through: thresholding <c>oceanFoam</c>
        /// against itself sharpens it into the same clean graphic lobes <c>FoamPattern</c> gave the procedural
        /// surface, but sourced from the real wave field, so the break-up moves with the actual waves instead of
        /// scrolling on <see cref="WaterSettings.WaveSpeed"/>'s own clock.
        /// </para>
        /// </summary>
        public static float FftFoamBreakup(float oceanFoam) =>
            Smoothstep(0f, FftBreakupSpan, Math.Clamp(oceanFoam, 0f, 1f));

        /// <summary>GLSL-identical smoothstep (Hermite), matching <see cref="SkyMath"/>'s copy so every mirrored
        /// pass agrees on the same curve.</summary>
        public static float Smoothstep(float edge0, float edge1, float x)
        {
            if (edge0 == edge1) return x < edge0 ? 0f : 1f;
            float u = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
            return u * u * (3f - 2f * u);
        }

        /// <summary>
        /// Redistribute one grid parameter so samples bunch around <paramref name="focus"/>. A monotone power warp
        /// applied on each side of the focus separately: it maps [0,1] onto [0,1], pins 0, <paramref name="focus"/>
        /// and 1 exactly, and at <paramref name="bias"/> &lt;= 1 returns the input untouched (so the uniform grid is
        /// bit-identical, not merely close).
        /// <para>
        /// Higher bias packs vertices toward the focus. The near field is where a displaced swell needs samples: a
        /// crest 500 units away is a couple of pixels tall no matter how finely it is tessellated, while the one
        /// breaking at the player's feet is the whole silhouette. See
        /// <see cref="WaterSettings.GridFocusBias"/> for the trade-off this buys.
        /// </para>
        /// </summary>
        /// <param name="u">Grid parameter in [0,1].</param>
        /// <param name="focus">Where to concentrate, in the same [0,1] parameter space (clamped).</param>
        /// <param name="bias">Power applied to each side; 1 or less is the identity (uniform).</param>
        public static float FocusWarp(float u, float focus, float bias)
        {
            if (bias <= 1f) return u;
            float c = Math.Clamp(focus, 0f, 1f);
            if (u <= c)
            {
                if (c <= 1e-6f) return u;
                float s = (c - u) / c;
                return c - c * MathF.Pow(s, bias);
            }
            float r = 1f - c;
            if (r <= 1e-6f) return u;
            float t = (u - c) / r;
            return c + r * MathF.Pow(t, bias);
        }

        /// <summary>
        /// Build the <see cref="GridResolution"/>x<see cref="GridResolution"/> XZ vertex grid for a
        /// <see cref="WaterPlane"/> request into <paramref name="destination"/> (must be at least
        /// <see cref="GridResolution"/>*<see cref="GridResolution"/> long), returning the vertex count. Positions
        /// are STILL-WATER positions at the plane's surface height: the Gerstner swell displaces them in the vertex
        /// shader, not here. Pure CPU layout math (no GPU calls) so the tessellation sizing is headless-testable.
        /// <para>
        /// The two axes are warped independently by <see cref="FocusWarp"/> toward the focus point (normally the
        /// camera, projected onto the plane and clamped inside it), and each axis' 1-D warp is evaluated once and
        /// reused down the row/column rather than per vertex.
        /// </para>
        /// </summary>
        /// <param name="plane">The queued plane (centre, surface height, half-extents).</param>
        /// <param name="focusX">World X to concentrate vertices around (clamped into the plane).</param>
        /// <param name="focusZ">World Z to concentrate vertices around (clamped into the plane).</param>
        /// <param name="bias">Concentration power; 1 or less produces the uniform grid.</param>
        /// <param name="destination">Receives the vertex positions.</param>
        /// <param name="axisScratch">Scratch for the two 1-D warped axes; must hold at least
        /// 2 * <see cref="GridResolution"/> floats.</param>
        public static int BuildGridPositions(in WaterPlane plane, float focusX, float focusZ, float bias,
            Span<Vector3> destination, Span<float> axisScratch)
        {
            const int n = GridResolution;
            float minX = plane.CenterX - plane.HalfExtentX, spanX = 2f * plane.HalfExtentX;
            float minZ = plane.CenterZ - plane.HalfExtentZ, spanZ = 2f * plane.HalfExtentZ;
            float fx = spanX > 1e-6f ? Math.Clamp((focusX - minX) / spanX, 0f, 1f) : 0.5f;
            float fz = spanZ > 1e-6f ? Math.Clamp((focusZ - minZ) / spanZ, 0f, 1f) : 0.5f;

            Span<float> xs = axisScratch.Slice(0, n);
            Span<float> zs = axisScratch.Slice(n, n);
            for (int i = 0; i < n; i++)
            {
                float t = n > 1 ? (float)i / (n - 1) : 0f;
                xs[i] = minX + FocusWarp(t, fx, bias) * spanX;
                zs[i] = minZ + FocusWarp(t, fz, bias) * spanZ;
            }

            int count = 0;
            for (int z = 0; z < n; z++)
            {
                float wz = zs[z];
                for (int x = 0; x < n; x++)
                    destination[count++] = new Vector3(xs[x], plane.SurfaceY, wz);
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
