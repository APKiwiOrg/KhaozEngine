using System;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// Pure, GPU-free mirror of the water fragment shader's RIPPLE SLOPE SPECTRUM: the component generator, the
    /// per-component footprint band-limit, and the slope-variance-to-roughness transfer that catches the energy the
    /// band-limit removes (<c>waterSlope</c> in <c>ShaderSources.Water.cs</c> MUST mirror this exactly, the same
    /// contract <see cref="GerstnerWaves"/> has with the vertex stage). No GPU state, no allocations.
    /// <para>
    /// This replaced a field of three fixed cosines. Three coherent cosines do not make a surface, they make a
    /// ruled pattern: their summed slope is constant along a family of parallel lines, so the eye reads parallel
    /// ribbons, the domain warp only bends those ribbons rather than breaking them, and at distance the ribbons
    /// beat against the pixel grid into moire. That is the same coherence defect the 14.22.0 checkerboard was, one
    /// level up. A real water surface has slope energy spread over many octaves and every heading, and the two
    /// artifacts it is responsible for (distance banding, and the absent sun glitter of issue #299) are both
    /// symptoms of not having it.
    /// </para>
    /// </summary>
    internal static class RippleSpectrum
    {
        /// <summary>Largest component count the generator (and the mirrored GLSL loop) supports.</summary>
        public const int MaxComponents = 12;

        /// <summary>
        /// The golden angle in radians. Successive components are separated by this heading, which is the standard
        /// low-discrepancy way to place N directions on a circle: no two are ever parallel, no subset lines up, and
        /// the set stays near-uniform at EVERY count rather than only at the one it was tuned for. Picking headings
        /// by hand (as the three-cosine field did) is what allows a dominant ribbon direction to exist.
        /// </summary>
        const float GoldenAngle = 2.39996323f;

        /// <summary>Irrational per-component phase stride. Without it every component peaks together at the world
        /// origin at t=0, which puts one absurd spike in the field at seed 0.</summary>
        const float PhaseStride = 4.74311f;

        /// <summary>
        /// Summed slope variance of the legacy three-cosine field, in units of <c>1/waveScale^2</c>:
        /// <c>1.0^2 + (0.62*phi)^2 + (0.32*sqrt7)^2</c>. The generated spectrum is normalized to this, which is
        /// what keeps <see cref="WaterSettings.NormalStrength"/> meaning the same amount of visible chop as it did
        /// before, at any component count, lacunarity or gain. Without the normalization every one of those three
        /// knobs would silently double as a chop-strength knob.
        /// </summary>
        const float LegacySlopeVariance = 2.72317f;

        const float TwoPi = 6.28318531f;

        /// <summary>One generated ripple component, in the form the per-pixel loop wants.</summary>
        public readonly struct Component
        {
            /// <summary>Unit travel direction, world X.</summary>
            public readonly float DirX;
            /// <summary>Unit travel direction, world Z.</summary>
            public readonly float DirZ;
            /// <summary>Wave number k = 2*pi / wavelength (radians per world unit).</summary>
            public readonly float WaveNumber;
            /// <summary>SLOPE amplitude (already the height amplitude times the wave number), normalized so the
            /// whole set matches <see cref="LegacySlopeVariance"/>.</summary>
            public readonly float SlopeAmplitude;
            /// <summary>Scroll rate multiplier applied to the <c>time * waveSpeed</c> clock.</summary>
            public readonly float ScrollRate;
            /// <summary>Constant phase offset (radians).</summary>
            public readonly float Phase;

            /// <summary>Build a component from its resolved terms.</summary>
            public Component(float dirX, float dirZ, float waveNumber, float slopeAmplitude, float scrollRate, float phase)
            {
                DirX = dirX; DirZ = dirZ; WaveNumber = waveNumber;
                SlopeAmplitude = slopeAmplitude; ScrollRate = scrollRate; Phase = phase;
            }
        }

        /// <summary>Slope, and the slope variance the band-limit removed, at one point.</summary>
        public readonly struct SlopeSample
        {
            /// <summary>d(height)/dx of the surviving (resolved) components.</summary>
            public readonly float DhDx;
            /// <summary>d(height)/dz of the surviving (resolved) components.</summary>
            public readonly float DhDz;
            /// <summary>Slope variance carried by the components the pixel cannot resolve. It is NOT discarded:
            /// <see cref="AlphaFromVariance"/> puts it back as specular lobe width.</summary>
            public readonly float LostVariance;

            /// <summary>Build a sample.</summary>
            public SlopeSample(float dhdx, float dhdz, float lostVariance)
            {
                DhDx = dhdx; DhDz = dhdz; LostVariance = lostVariance;
            }
        }

        /// <summary>
        /// Generate the ripple spectrum from four scalars, mirroring the GLSL generator exactly. Nothing is
        /// uploaded per component: the shader rebuilds the identical set, so the whole spectrum costs one vec4 of
        /// UBO however many components it has.
        /// <para>
        /// Shape: headings step by the <see cref="GoldenAngle"/> from a seeded start, wave numbers climb
        /// geometrically by <paramref name="lacunarity"/>, height amplitudes fall geometrically by
        /// <paramref name="gain"/> (so slope amplitude scales as <c>(gain*lacunarity)^i</c>, i.e. a gain*lacunarity
        /// near 1 spreads slope energy evenly across the octaves, which is roughly what a real wind-sea slope
        /// spectrum does), and scroll rate rises as <c>sqrt(k)</c> from the deep-water dispersion relation, so short
        /// ripples travel across the long ones instead of the whole field sliding rigidly.
        /// </para>
        /// </summary>
        /// <param name="waveScale">Base ripple scale (<see cref="WaterSettings.WaveScale"/>); the longest
        /// component's wave number is its reciprocal.</param>
        /// <param name="lacunarity">Wave-number ratio between successive components (&gt; 1 widens the band).</param>
        /// <param name="gain">Height-amplitude ratio between successive components.</param>
        /// <param name="seed">Rotates the heading fan and offsets the phases; decorrelates two water bodies.</param>
        /// <param name="count">Requested component count, clamped to 1..<see cref="MaxComponents"/>.</param>
        /// <param name="destination">Receives the components; must hold at least the clamped count.</param>
        /// <returns>Number of components written.</returns>
        public static int Build(float waveScale, float lacunarity, float gain, float seed, int count,
            Span<Component> destination)
        {
            int n = Math.Clamp(count, 1, MaxComponents);
            float scale = MathF.Max(waveScale, 1e-4f);
            float k0 = 1f / scale;
            float lac = MathF.Max(lacunarity, 1.01f);
            float g = Math.Clamp(gain, 0.05f, 1.5f);

            // Normalize to the legacy slope variance. r is the per-component slope-amplitude ratio; the sum of its
            // squares is a closed-form geometric series, matched literally by the GLSL so the two round alike.
            float r = g * lac;
            float rr = r * r;
            float sumSq = MathF.Abs(1f - rr) < 1e-6f ? n : (1f - MathF.Pow(rr, n)) / (1f - rr);
            float norm = MathF.Sqrt(LegacySlopeVariance / MathF.Max(sumSq, 1e-6f));

            for (int i = 0; i < n; i++)
            {
                float angle = seed + i * GoldenAngle;
                float k = k0 * MathF.Pow(lac, i);
                float slopeAmp = norm * k0 * MathF.Pow(r, i);
                float scroll = MathF.Sqrt(MathF.Pow(lac, i));   // omega ~ sqrt(k), normalized to 1 at i = 0
                float phase = i * PhaseStride + seed * (i + 1) * 1.61803399f;
                destination[i] = new Component(MathF.Cos(angle), MathF.Sin(angle), k, slopeAmp, scroll, phase);
            }
            return n;
        }

        /// <summary>
        /// How much of a component survives at this pixel: 1 while its wavelength is comfortably wider than
        /// <paramref name="samplesPerWavelength"/> pixel footprints, falling smoothly to 0 as it drops below.
        /// <para>
        /// This is the half of band-limiting that 14.24.0 left out. That release widened the specular LOBE by
        /// footprint, which fixed sparkle, but it left the normal FIELD oscillating at frequencies the pixel could
        /// not resolve, and an unresolvable normal oscillation is exactly what moire is. Fading the component out
        /// of the normal is the fix; the energy is not thrown away, it goes to <see cref="AlphaFromVariance"/>.
        /// </para>
        /// </summary>
        /// <param name="wavelength">The component's world-space wavelength.</param>
        /// <param name="footprint">World units this pixel spans on the surface (the shader's <c>fwidth</c>).</param>
        /// <param name="samplesPerWavelength">Footprints per wavelength below which the component fades out. The
        /// Nyquist floor is 2; a little above that fades before aliasing rather than during. 0 or less disables the
        /// band-limit entirely.</param>
        public static float Resolve(float wavelength, float footprint, float samplesPerWavelength)
        {
            if (footprint <= 0f || samplesPerWavelength <= 0f) return 1f;
            float need = footprint * samplesPerWavelength;
            if (need <= 1e-8f) return 1f;
            return WaterMath.Smoothstep(0f, 1f, Math.Clamp(wavelength / need, 0f, 1f));
        }

        /// <summary>
        /// Evaluate the spectrum's slope at one world position, band-limited to the pixel footprint, accumulating
        /// the variance the band-limit removed. Mirrors the GLSL <c>waterSlope</c> exactly.
        /// </summary>
        /// <param name="worldX">World X of the shaded point (already domain-warped by the caller).</param>
        /// <param name="worldZ">World Z of the shaded point.</param>
        /// <param name="scrollTime">Already-scaled clock (<c>timeSeconds * waveSpeed</c>).</param>
        /// <param name="components">The set from <see cref="Build"/>.</param>
        /// <param name="footprint">World units this pixel spans on the surface.</param>
        /// <param name="samplesPerWavelength">See <see cref="Resolve"/>.</param>
        /// <param name="detailScale">Extra artistic attenuation applied to every component ABOVE the first (the
        /// <see cref="WaterSettings.DetailFadeDistance"/> ramp). Its variance is transferred too, so turning it up
        /// or down cannot reintroduce banding.</param>
        public static SlopeSample Slope(float worldX, float worldZ, float scrollTime,
            ReadOnlySpan<Component> components, float footprint, float samplesPerWavelength, float detailScale)
        {
            float dhdx = 0f, dhdz = 0f, lost = 0f;
            for (int i = 0; i < components.Length; i++)
            {
                Component c = components[i];
                float wavelength = TwoPi / MathF.Max(c.WaveNumber, 1e-8f);
                float keep = Resolve(wavelength, footprint, samplesPerWavelength);
                if (i > 0) keep *= detailScale;

                float g = c.SlopeAmplitude * keep
                    * MathF.Cos((c.DirX * worldX + c.DirZ * worldZ) * c.WaveNumber + scrollTime * c.ScrollRate + c.Phase);
                dhdx += g * c.DirX;
                dhdz += g * c.DirZ;

                // Variance of a cosine of amplitude A is A^2/2. What the fade removed is the difference.
                float amp = c.SlopeAmplitude;
                lost += amp * amp * (1f - keep * keep) * 0.5f;
            }
            return new SlopeSample(dhdx, dhdz, lost);
        }

        /// <summary>
        /// Fold removed slope variance into the specular lobe: the Toksvig-style transfer that keeps the surface's
        /// total energy constant as detail is band-limited away. Returns the widened GGX alpha.
        /// <para>
        /// A patch of water whose ripples are too small to resolve does not become a mirror, it becomes a rougher
        /// surface: the sub-pixel slope distribution that used to be geometry is now lobe width. Doing the transfer
        /// is what makes distant water settle into a smooth fresnel gradient with a believable sheen rather than
        /// either stripes (no band-limit) or glass (band-limit without transfer).
        /// </para>
        /// </summary>
        /// <param name="alpha">GGX alpha before the transfer (i.e. perceptual roughness squared).</param>
        /// <param name="slopeVariance">Removed slope variance, already scaled by
        /// <see cref="WaterSettings.NormalStrength"/> squared.</param>
        /// <param name="gain">How much of it to transfer (<see cref="WaterSettings.VarianceToRoughness"/>);
        /// 0 disables the transfer.</param>
        public static float AlphaFromVariance(float alpha, float slopeVariance, float gain)
        {
            float v = MathF.Max(slopeVariance, 0f) * MathF.Max(gain, 0f);
            return MathF.Min(MathF.Sqrt(alpha * alpha + 2f * v), 1f);
        }

        /// <summary>
        /// Average resolvability of the Gerstner swell's component ladder at this pixel, plus the slope variance
        /// the shortfall represents. Used to soften the swell's SHADING contrast at range without touching its
        /// geometry: the crests keep their silhouette, they just stop drawing parallel rules across the horizon.
        /// <para>
        /// Every swell component carries the SAME slope amplitude by construction (the generator makes height
        /// amplitude proportional to wavelength, so <c>k*A</c> is constant across the ladder), which is why a plain
        /// mean over the ladder is the correct attenuation here rather than a weighted one.
        /// </para>
        /// </summary>
        /// <param name="wavelength">Longest swell component (<see cref="WaterSettings.SwellWavelength"/>).</param>
        /// <param name="amplitude">Summed swell amplitude (<see cref="WaterSettings.SwellAmplitude"/>).</param>
        /// <param name="count">Swell component count.</param>
        /// <param name="lambdaDecay">The ladder ratio the swell generator uses.</param>
        /// <param name="footprint">World units this pixel spans on the surface.</param>
        /// <param name="samplesPerWavelength">See <see cref="Resolve"/>.</param>
        /// <param name="attenuation">Receives the 0..1 factor to scale the swell normal's horizontal tilt by.</param>
        /// <returns>The slope variance the attenuation removed.</returns>
        public static float SwellAttenuation(float wavelength, float amplitude, int count, float lambdaDecay,
            float footprint, float samplesPerWavelength, out float attenuation)
        {
            int n = Math.Clamp(count, 1, GerstnerWaves.MaxComponents);
            if (amplitude <= 0f || wavelength <= 0f) { attenuation = 1f; return 0f; }

            float lambdaSum = wavelength * (1f - MathF.Pow(lambdaDecay, n)) / (1f - lambdaDecay);
            float slopeAmp = TwoPi * amplitude / MathF.Max(lambdaSum, 1e-6f);   // == k_i * A_i, same for every i

            float keepSum = 0f;
            for (int i = 0; i < n; i++)
                keepSum += Resolve(wavelength * MathF.Pow(lambdaDecay, i), footprint, samplesPerWavelength);

            attenuation = keepSum / n;
            float totalVariance = n * slopeAmp * slopeAmp * 0.5f;
            return totalVariance * (1f - attenuation * attenuation);
        }
    }
}
