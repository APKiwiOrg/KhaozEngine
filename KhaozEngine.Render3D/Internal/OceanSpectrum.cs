using System;
using System.Numerics;

namespace KhaozEngine.Render3D.Internal
{
    /// <summary>
    /// The CPU half of the FFT ocean (<see cref="WaterWaveSource.FftOcean"/>): the TMA directional wave spectrum,
    /// the finite-depth dispersion relation, the cascade band split, and the seeded initial-amplitude field
    /// <c>h0(k)</c> that the compute producer evolves and inverse-transforms every frame.
    /// <para>
    /// It is pure and allocation-light on purpose. Everything here is headless-testable: the spectrum shape, the
    /// spreading normalization, the dispersion derivative and the band split all have closed forms with known
    /// properties, and <c>OceanSpectrumTests</c> pins them. The GPU never re-derives any of it - it reads the
    /// baked <c>h0</c> field this class produces - so there is no second copy of the maths to drift.
    /// </para>
    /// <para>
    /// Adapted from the approach in GodotOceanWaves (https://github.com/2Retr0/GodotOceanWaves, MIT), which is in
    /// turn a compute-shader reading of Tessendorf's "Simulating Ocean Water" and Horvath's "Empirical Directional
    /// Wave Spectra for Computer Graphics" (2015). See <c>NOTICE.md</c>.
    /// </para>
    /// </summary>
    internal static class OceanSpectrum
    {
        /// <summary>Standard gravity, m/s^2. Matches the Gerstner path's <c>KE_GRAVITY</c> so the two wave sources
        /// agree on how fast a wave of a given length travels.</summary>
        public const float Gravity = 9.81f;

        /// <summary>Hard ceiling on cascades, mirrored by <c>KE_MAX_CASCADES</c> in the water shaders and by the
        /// <c>Cascade[]</c> array in the compute parameter block. Raising it is a shader change, not a knob.</summary>
        public const int MaxCascades = 3;

        /// <summary>Smallest FFT resolution the producer will build.</summary>
        public const int MinResolution = 32;

        /// <summary>Largest FFT resolution the producer will build. 256 already quadruples the transform work and
        /// the map memory against the 128 default.</summary>
        public const int MaxResolution = 256;

        /// <summary>
        /// Ceiling on the argument handed to <c>tanh</c> in the dispersion relation, mirrored by
        /// <c>KE_TANH_LIMIT</c> in the compute kernels. It exists for the GPU side: a hardware tanh is commonly
        /// <c>(exp(2x) - 1) / (exp(2x) + 1)</c>, which overflows to <c>inf/inf</c> = NaN long before the argument
        /// itself does, and the finest cascade reaches <c>k * depth</c> above 140 in a 60 metre sea. That produced
        /// a NaN surface for one cascade and a perfect one for the next, on Metal, with nothing wrong in the maths.
        /// tanh is 1 to well under a float ULP past about 10, so this changes no observable value; it is clamped on
        /// the CPU too purely so the two sides stay literally the same function.
        /// </summary>
        public const float TanhArgumentLimit = 20f;

        const float TwoPi = 6.2831853071795862f;

        // ---- Sea-state derived scalars ---------------------------------------------------------------------

        /// <summary>
        /// JONSWAP peak angular frequency for a wind speed (m/s) and fetch (metres):
        /// <c>omega_p = 22 (g^2 / (U F))^(1/3)</c>. Falls as either grows, which is why a long fetch or a strong
        /// wind gives long waves.
        /// </summary>
        public static float PeakAngularFrequency(float windSpeed, float fetchMetres)
        {
            float u = MathF.Max(windSpeed, 0.1f);
            float f = MathF.Max(fetchMetres, 1f);
            return 22f * MathF.Cbrt(Gravity * Gravity / (u * f));
        }

        /// <summary>
        /// JONSWAP scale parameter, Hasselmann's fetch relation <c>alpha = 0.076 (g F / U^2)^-0.22</c>. This is
        /// what makes the surface height follow from wind and fetch instead of from a hand-set amplitude.
        /// </summary>
        public static float JonswapAlpha(float windSpeed, float fetchMetres)
        {
            float u = MathF.Max(windSpeed, 0.1f);
            float f = MathF.Max(fetchMetres, 1f);
            return 0.076f * MathF.Pow(Gravity * f / (u * u), -0.22f);
        }

        /// <summary>
        /// The JONSWAP frequency spectrum <c>S(omega)</c> in m^2 s: a Pierson-Moskowitz tail with a peak
        /// enhancement of <c>gamma = 3.3</c> and the usual asymmetric sigma (0.07 below the peak, 0.09 above).
        /// </summary>
        public static float Jonswap(float omega, float omegaPeak, float alpha)
        {
            if (omega <= 1e-6f || omegaPeak <= 1e-6f) return 0f;
            const float gamma = 3.3f;
            float sigma = omega <= omegaPeak ? 0.07f : 0.09f;
            float d = (omega - omegaPeak) / (sigma * omegaPeak);
            float r = MathF.Exp(-0.5f * d * d);
            float wp4 = omegaPeak / omega;
            wp4 *= wp4; wp4 *= wp4;                       // (omega_p / omega)^4
            float w5 = omega * omega;
            w5 = w5 * w5 * omega;                          // omega^5
            return alpha * Gravity * Gravity / w5 * MathF.Exp(-1.25f * wp4) * MathF.Pow(gamma, r);
        }

        /// <summary>
        /// The Kitaigorodskii depth attenuation <c>phi(omega, h)</c> that turns JONSWAP into TMA: 1 in deep water,
        /// falling toward <c>0.5 omega_h^2</c> in shallow water, where <c>omega_h = omega sqrt(h/g)</c>. This is
        /// the term that makes <see cref="WaterSeaState.DepthMetres"/> mean something, and it is why TMA was
        /// chosen over a plain Phillips spectrum.
        /// </summary>
        public static float KitaigorodskiiDepth(float omega, float depthMetres)
        {
            if (depthMetres <= 0f) return 1f;              // treat "no depth given" as deep water
            float wh = omega * MathF.Sqrt(depthMetres / Gravity);
            if (wh <= 0f) return 0f;
            if (wh >= 2f) return 1f;
            if (wh <= 1f) return 0.5f * wh * wh;
            float t = 2f - wh;
            return 1f - 0.5f * t * t;
        }

        /// <summary>The TMA spectrum: JONSWAP shaped by the Kitaigorodskii depth factor.</summary>
        public static float Tma(float omega, float omegaPeak, float alpha, float depthMetres)
            => Jonswap(omega, omegaPeak, alpha) * KitaigorodskiiDepth(omega, depthMetres);

        // ---- Dispersion ------------------------------------------------------------------------------------

        /// <summary>
        /// Finite-depth dispersion <c>omega = sqrt(g k tanh(k h))</c>. Reduces to the deep-water
        /// <c>sqrt(g k)</c> as <c>k h</c> grows (and when <paramref name="depthMetres"/> is 0 or less, which means
        /// "deep").
        /// </summary>
        public static float Dispersion(float k, float depthMetres)
        {
            if (k <= 0f) return 0f;
            float t = depthMetres <= 0f ? 1f : MathF.Tanh(MathF.Min(k * depthMetres, TanhArgumentLimit));
            return MathF.Sqrt(Gravity * k * t);
        }

        /// <summary>
        /// <c>d omega / d k</c> for <see cref="Dispersion"/>, i.e. the group velocity. Needed to convert the
        /// frequency spectrum <c>S(omega)</c> into the wave-number spectrum the FFT grid samples:
        /// <c>S2D(k) = S(omega) D(theta) (domega/dk) / k</c>.
        /// </summary>
        public static float DispersionDerivative(float k, float depthMetres)
        {
            if (k <= 0f) return 0f;
            float omega = Dispersion(k, depthMetres);
            if (omega <= 1e-8f) return 0f;
            if (depthMetres <= 0f) return 0.5f * Gravity / omega;         // deep water: g / (2 omega)
            float kh = MathF.Min(k * depthMetres, TanhArgumentLimit);
            float th = MathF.Tanh(kh);
            float sech2 = 1f - th * th;
            return (Gravity * th + Gravity * kh * sech2) / (2f * omega);
        }

        // ---- Directional spreading -------------------------------------------------------------------------

        /// <summary>
        /// Hasselmann's directional exponent <c>s(omega)</c>: the wind sea is broad-banded well away from the peak
        /// and narrow near it, and the tail narrows with wind speed. Feeds the <c>cos^2s</c> lobe below.
        /// </summary>
        public static float HasselmannExponent(float omega, float omegaPeak, float windSpeed)
        {
            if (omega <= 1e-6f || omegaPeak <= 1e-6f) return 0f;
            float ratio = omega / omegaPeak;
            if (ratio < 1f) return 6.97f * MathF.Pow(ratio, 4.06f);
            float u = MathF.Max(windSpeed, 0.1f);
            float e = -2.33f - 1.45f * (u * omegaPeak / Gravity - 1.17f);
            return 9.77f * MathF.Pow(ratio, e);
        }

        /// <summary>
        /// The normalized Longuet-Higgins lobe <c>Q(s) cos^2s(dTheta / 2)</c>, which integrates to exactly 1 over
        /// a full turn. <c>Q(s) = Gamma(s+1) / (2 sqrt(pi) Gamma(s+1/2))</c>; the gamma ratio is evaluated through
        /// <see cref="LogGamma"/> so large <c>s</c> (a sharp swell lobe) does not overflow.
        /// </summary>
        public static float LonguetHiggins(float deltaTheta, float s)
        {
            float sc = MathF.Max(s, 0f);
            float c = MathF.Cos(0.5f * WrapAngle(deltaTheta));
            float q = MathF.Exp(LogGamma(sc + 1f) - LogGamma(sc + 0.5f)) / (2f * MathF.Sqrt(MathF.PI));
            // cos can be 0 at the exact anti-wind heading; pow(0, positive) is 0, which is the right answer.
            return q * MathF.Pow(MathF.Max(c, 0f), 2f * sc);
        }

        /// <summary>
        /// The mixed directional spreading actually used, <c>D(theta, omega)</c>, normalized to integrate to 1
        /// over a full turn at any setting.
        /// <para>
        /// Two blends, in order. First the WIND lobe is mixed against a FLAT (isotropic) distribution by
        /// <see cref="WaterSeaState.DirectionalSpread"/>, which is what lets a confused sea and a long-crested one
        /// be the same model with one knob. Then a SWELL lobe - the same Longuet-Higgins form with an extra
        /// <c>16 tanh(omega_p / omega) swell^2</c> added to the exponent, so it sharpens the long waves and leaves
        /// the short ones alone - is mixed in by <see cref="WaterSeaState.SwellAmount"/> on its own heading. At
        /// swell 0 the second blend is the identity, so a pure wind sea costs nothing extra and reads exactly as
        /// the first blend.
        /// </para>
        /// </summary>
        public static float DirectionalSpread(float theta, float omega, float omegaPeak, float windSpeed,
            float windTheta, float swellTheta, float spread, float swell)
        {
            float flat = 1f / TwoPi;
            float s = HasselmannExponent(omega, omegaPeak, windSpeed);
            float wind = LonguetHiggins(theta - windTheta, s);
            float mixed = flat + (wind - flat) * Math.Clamp(spread, 0f, 1f);

            float sw = Math.Clamp(swell, 0f, 1f);
            if (sw <= 0f) return mixed;
            float sSwell = s + 16f * MathF.Tanh(omegaPeak / MathF.Max(omega, 1e-6f)) * sw * sw;
            float swellLobe = LonguetHiggins(theta - swellTheta, sSwell);
            return mixed + (swellLobe - mixed) * sw;
        }

        // ---- Cascade layout --------------------------------------------------------------------------------

        /// <summary>Tile size in metres of cascade <paramref name="index"/>: the largest tile divided by the ratio
        /// once per step down.</summary>
        public static float TileMetres(int index, float largestTile, float ratio)
        {
            float l = MathF.Max(largestTile, 1f);
            float r = MathF.Max(ratio, 1.05f);
            for (int i = 0; i < index; i++) l /= r;
            return MathF.Max(l, 0.05f);
        }

        /// <summary>
        /// The DISJOINT wave-number band cascade <paramref name="index"/> owns, in rad/m. Cascade <c>i</c> covers
        /// everything from the previous (larger) tile's Nyquist wave number up to its own, and the last cascade's
        /// upper bound is open, so the cascades PARTITION wave-number space rather than overlapping it.
        /// <para>
        /// This is what makes summing the cascades additively correct: no wave number is represented twice, so
        /// there is no energy to weight away and no double counting to compensate for. It is also why the cascades
        /// cannot be re-ordered - the split is defined by the tile ladder.
        /// </para>
        /// </summary>
        public static void CascadeBand(int index, int count, float largestTile, float ratio, int resolution,
            out float kLow, out float kHigh)
        {
            int n = Math.Clamp(count, 1, MaxCascades);
            int i = Math.Clamp(index, 0, n - 1);
            float nyq(int c) => MathF.PI * resolution / TileMetres(c, largestTile, ratio);
            kLow = i == 0 ? 0f : nyq(i - 1);
            kHigh = i == n - 1 ? float.PositiveInfinity : nyq(i);
        }

        // ---- Initial spectrum ------------------------------------------------------------------------------

        /// <summary>
        /// What one cascade's bake knows about the spectrum it just laid down, beyond the amplitudes themselves.
        /// All three fall out of the SAME loop that walks the band, so none of them costs an extra pass, and all
        /// three are properties of the spectrum rather than of the one random draw - which is what makes them
        /// stable across a reseed and safe to key shading on.
        /// </summary>
        /// <param name="SlopeVariance">Expected slope variance, <c>sum k^2 S2D dk^2</c> over the band. The water
        /// fragment feeds it to the Toksvig transfer: when the pixel footprint band-limits a cascade out of the
        /// normal, this is the variance that has to reappear as glint-lobe width instead of being lost to a glassy
        /// far field.</param>
        /// <param name="HeightVariance">Expected height variance, <c>sum S2D dk^2</c> over the band. Summed across
        /// the cascades it is <c>m0</c>, and <c>4 sqrt(m0)</c> is the significant wave height the breaking
        /// criterion measures the local depth against (<see cref="WaterShoaling.SignificantHeight"/>).</param>
        /// <param name="MeanWavenumber">Energy-weighted mean wave number over the band, rad/m:
        /// <c>sum k S2D dk^2 / sum S2D dk^2</c>. This is the <c>k</c> the shoaling taper uses, and weighting it by
        /// energy rather than taking the band's midpoint is what makes it mean something: cascade 0's band runs
        /// from 0 to its Nyquist and nearly all of its energy sits near the spectral peak at the bottom of that,
        /// so a midpoint would put the swell's <c>k</c> an order of magnitude too high and the swell would never
        /// feel the bottom at all.</param>
        internal readonly record struct CascadeStatistics(float SlopeVariance, float HeightVariance,
            float MeanWavenumber);

        /// <summary>
        /// Bake one cascade's initial amplitude field into <paramref name="destination"/>, one
        /// <see cref="Vector4"/> per texel in row-major <c>(m + n * resolution)</c> order:
        /// <c>xy = h0(k)</c> and <c>zw = conj(h0(-k))</c>, the two halves the per-frame time evolution needs.
        /// Storing both at the texel keeps the evolution kernel to one coalesced read instead of a mirrored fetch
        /// on a different row.
        /// <para>
        /// The randomness is a position HASH, not a stream, so the value at a texel depends only on
        /// <see cref="WaterSeaState.Seed"/>, the cascade and the texel coordinates. That is what makes
        /// <c>conj(h0(-k))</c> here and <c>h0</c> at the mirrored texel the same draw, without a second pass and
        /// without any ordering assumption.
        /// </para>
        /// <para>
        /// Index 0 on either axis is forced to zero. It is the Nyquist row/column, whose mirror is itself, so it
        /// is the one place the Hermitian symmetry cannot hold; leaving it in would leak one packed field into the
        /// other. It carries the least energy of any row in the cascade, so dropping it costs nothing visible.
        /// </para>
        /// </summary>
        /// <returns>The cascade's <see cref="CascadeStatistics"/>: the expected slope variance, height variance and
        /// energy-weighted mean wave number of the band it just baked.</returns>
        public static CascadeStatistics BuildInitialSpectrum(WaterSeaState sea, int cascadeIndex, int resolution,
            Span<Vector4> destination)
        {
            int n = resolution;
            if (destination.Length < n * n)
                throw new ArgumentException($"destination holds {destination.Length} texels, need {n * n}", nameof(destination));

            float tile = TileMetres(cascadeIndex, sea.CascadeTileMetres, sea.CascadeTileRatio);
            CascadeBand(cascadeIndex, sea.CascadeCount, sea.CascadeTileMetres, sea.CascadeTileRatio, n,
                out float kLow, out float kHigh);

            float fetch = MathF.Max(sea.FetchKilometres, 0.01f) * 1000f;
            float omegaPeak = PeakAngularFrequency(sea.WindSpeed, fetch);
            float alpha = JonswapAlpha(sea.WindSpeed, fetch);
            float windTheta = sea.WindDirectionDegrees * (MathF.PI / 180f);
            float swellTheta = sea.SwellDirectionDegrees * (MathF.PI / 180f);
            float cutoff = MathF.Max(sea.SmallWaveCutoffMetres, 0f);
            float dk = TwoPi / tile;
            float cellArea = dk * dk;

            float slopeVariance = 0f, heightVariance = 0f, weightedK = 0f;
            for (int row = 0; row < n; row++)
            {
                for (int col = 0; col < n; col++)
                {
                    int index = row * n + col;
                    if (row == 0 || col == 0) { destination[index] = default; continue; }

                    Vector2 h0 = Amplitude(col, row);
                    Vector2 mirror = Amplitude((n - col) % n, (n - row) % n);
                    destination[index] = new Vector4(h0.X, h0.Y, mirror.X, -mirror.Y);   // zw = conj(h0(-k))
                    Accumulate(col, row);
                }
            }
            // No energy at all in this cascade's band (a band the sea state simply does not reach) leaves the mean
            // wave number at 0, which every consumer reads as "nothing here to attenuate".
            return new CascadeStatistics(slopeVariance, heightVariance,
                heightVariance > 0f ? weightedK / heightVariance : 0f);

            void Accumulate(int col, int row)
            {
                float kx = (col - n * 0.5f) * dk;
                float kz = (row - n * 0.5f) * dk;
                float k2 = kx * kx + kz * kz;
                float k = MathF.Sqrt(k2);
                float density = SpectralDensity(k, MathF.Atan2(kz, kx));
                if (density <= 0f) return;
                float energy = density * cellArea;
                slopeVariance += k2 * energy;
                heightVariance += energy;
                weightedK += k * energy;
            }

            // The 2D wave-number spectrum S2D(k) at one grid point, or 0 outside this cascade's band. Shared by
            // the amplitude draw and the variance accumulation so the two can never disagree about the band.
            float SpectralDensity(float k, float theta)
            {
                if (k <= 1e-6f || k < kLow || k >= kHigh) return 0f;
                float omega = Dispersion(k, sea.DepthMetres);
                float s = Tma(omega, omegaPeak, alpha, sea.DepthMetres);
                if (s <= 0f) return 0f;
                float d = DirectionalSpread(theta, omega, omegaPeak, sea.WindSpeed,
                    windTheta, swellTheta, sea.DirectionalSpread, sea.SwellAmount);
                // S(omega) -> S2D(k): the Jacobian of the change of variables is (domega/dk) / k.
                float s2d = s * d * DispersionDerivative(k, sea.DepthMetres) / k;
                if (cutoff > 0f) s2d *= MathF.Exp(-k * k * cutoff * cutoff);
                return s2d > 0f ? s2d : 0f;
            }

            Vector2 Amplitude(int col, int row)
            {
                float kx = (col - n * 0.5f) * dk;
                float kz = (row - n * 0.5f) * dk;
                float k = MathF.Sqrt(kx * kx + kz * kz);
                float s2d = SpectralDensity(k, MathF.Atan2(kz, kx));
                if (s2d <= 0f) return Vector2.Zero;

                // E[|h0|^2] = S2D * dkx * dkz / 2, so that h~ = h0 + conj(h0(-k)) carries the full cell variance
                // and Parseval over the grid reproduces the spectrum's integral. OceanSpectrumTests pins this.
                float amp = MathF.Sqrt(0.5f * s2d * cellArea);
                Gaussian(sea.Seed, cascadeIndex, col, row, out float gr, out float gi);
                float inv = amp * 0.70710678f;                 // 1 / sqrt(2)
                return new Vector2(gr * inv, gi * inv);
            }
        }

        /// <summary>Two independent standard normals from a position hash, via Box-Muller. Deterministic in
        /// (seed, cascade, texel) and independent of evaluation order.</summary>
        internal static void Gaussian(int seed, int cascade, int col, int row, out float real, out float imaginary)
        {
            ulong h = Mix((ulong)(uint)seed * 0x9E3779B97F4A7C15UL
                        ^ (ulong)(uint)cascade * 0xBF58476D1CE4E5B9UL
                        ^ (ulong)(uint)col * 0x94D049BB133111EBUL
                        ^ (ulong)(uint)row * 0xD6E8FEB86659FD93UL);
            ulong h2 = Mix(h ^ 0xA24BAED4963EE407UL);

            // (0, 1]: never 0, so the log is finite.
            double u1 = ((h >> 11) + 1UL) * (1.0 / 9007199254740993.0);
            double u2 = (h2 >> 11) * (1.0 / 9007199254740992.0);
            double r = Math.Sqrt(-2.0 * Math.Log(u1));
            double a = 2.0 * Math.PI * u2;
            real = (float)(r * Math.Cos(a));
            imaginary = (float)(r * Math.Sin(a));
        }

        static ulong Mix(ulong x)
        {
            x ^= x >> 30; x *= 0xBF58476D1CE4E5B9UL;
            x ^= x >> 27; x *= 0x94D049BB133111EBUL;
            x ^= x >> 31;
            return x;
        }

        // Closed [-pi, pi], including the negative endpoint. Preserve this seeded spectrum convention rather
        // than replacing it with MathUtil.WrapAngle's half-open (-pi, pi] interval.
        static float WrapAngle(float a)
        {
            float t = a % TwoPi;
            if (t > MathF.PI) t -= TwoPi;
            else if (t < -MathF.PI) t += TwoPi;
            return t;
        }

        /// <summary>Lanczos log-gamma (g = 7, 9 coefficients), accurate to well under a float ULP over the range
        /// the spreading exponents reach. Only used to normalize <see cref="LonguetHiggins"/>.</summary>
        internal static float LogGamma(float x)
        {
            if (x <= 0f) return float.PositiveInfinity;
            double[] c = LanczosCoefficients;
            double z = x - 1.0;
            double a = c[0];
            for (int i = 1; i < c.Length; i++) a += c[i] / (z + i);
            double t = z + 7.5;
            return (float)(0.5 * Math.Log(2.0 * Math.PI) + (z + 0.5) * Math.Log(t) - t + Math.Log(a));
        }

        static readonly double[] LanczosCoefficients =
        {
            0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313,
            -176.61502916214059, 12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6,
            1.5056327351493116e-7,
        };
    }
}
