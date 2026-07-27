using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Headless coverage of the FFT ocean's CPU half (<see cref="OceanSpectrum"/>): the TMA spectrum, the
    /// finite-depth dispersion relation and its derivative, the directional spreading and its normalization, the
    /// cascade band split, and the seeded initial-amplitude field.
    /// <para>
    /// Everything here has a closed form with a property that can be checked without a reference image or a
    /// device, which is the whole reason the spectrum lives on the CPU: the parts of an FFT ocean most likely to
    /// be silently wrong (a spreading function that does not integrate to 1, a dispersion derivative off by a
    /// factor, bands that overlap and double-count energy, a Hermitian symmetry that does not hold) are all
    /// checkable arithmetic.
    /// </para>
    /// </summary>
    public sealed class OceanSpectrumTests
    {
        static WaterSeaState Sea() => new()
        {
            WindSpeed = 11f,
            WindDirectionDegrees = 30f,
            FetchKilometres = 120f,
            DepthMetres = 60f,
            DirectionalSpread = 0.75f,
            SwellAmount = 0.4f,
            SwellDirectionDegrees = 30f,
            SmallWaveCutoffMetres = 0.02f,
            Seed = 7,
            CascadeCount = 3,
            CascadeTileMetres = 250f,
            CascadeTileRatio = 4.2f,
            CascadeResolution = 64,
        };

        // ---- JONSWAP / TMA ---------------------------------------------------------------------------------

        [Fact]
        public void PeakFrequencyFallsWithWindAndWithFetch()
        {
            float calm = OceanSpectrum.PeakAngularFrequency(5f, 100_000f);
            float windy = OceanSpectrum.PeakAngularFrequency(20f, 100_000f);
            float shortFetch = OceanSpectrum.PeakAngularFrequency(11f, 10_000f);
            float longFetch = OceanSpectrum.PeakAngularFrequency(11f, 500_000f);

            Assert.True(windy < calm, $"a stronger wind must give longer waves: {windy} vs {calm}");
            Assert.True(longFetch < shortFetch, $"a longer fetch must give longer waves: {longFetch} vs {shortFetch}");
        }

        [Fact]
        public void JonswapPeaksAtItsPeakFrequencyAndDecaysBothWays()
        {
            const float wp = 0.9f;
            float alpha = OceanSpectrum.JonswapAlpha(11f, 120_000f);
            float atPeak = OceanSpectrum.Jonswap(wp, wp, alpha);

            Assert.True(atPeak > 0f);
            Assert.True(OceanSpectrum.Jonswap(wp * 0.6f, wp, alpha) < atPeak, "below the peak must be smaller");
            Assert.True(OceanSpectrum.Jonswap(wp * 1.8f, wp, alpha) < atPeak, "above the peak must be smaller");
            // The high-frequency tail is the Pierson-Moskowitz omega^-5 law, so an octave up costs a factor 32.
            float a = OceanSpectrum.Jonswap(wp * 4f, wp, alpha);
            float b = OceanSpectrum.Jonswap(wp * 8f, wp, alpha);
            Assert.InRange(a / b, 25f, 40f);
        }

        [Fact]
        public void KitaigorodskiiIsOneInDeepWaterAndSquareLawInShallow()
        {
            Assert.Equal(1f, OceanSpectrum.KitaigorodskiiDepth(1f, 0f));          // 0 depth means "deep", not "dry"
            Assert.Equal(1f, OceanSpectrum.KitaigorodskiiDepth(0.5f, 5000f), 4);  // deep at any sane frequency

            // omega_h = omega sqrt(h/g); at omega_h = 0.5 the factor is 0.5 * 0.25.
            float h = 1f;
            float omega = 0.5f * MathF.Sqrt(OceanSpectrum.Gravity / h);
            Assert.Equal(0.125f, OceanSpectrum.KitaigorodskiiDepth(omega, h), 4);
            // Continuous across both breakpoints.
            float w1 = 1f * MathF.Sqrt(OceanSpectrum.Gravity / h);
            float w2 = 2f * MathF.Sqrt(OceanSpectrum.Gravity / h);
            Assert.Equal(0.5f, OceanSpectrum.KitaigorodskiiDepth(w1, h), 3);
            Assert.Equal(1f, OceanSpectrum.KitaigorodskiiDepth(w2, h), 3);
        }

        [Fact]
        public void TmaIsJonswapCutDownByDepth()
        {
            float alpha = OceanSpectrum.JonswapAlpha(11f, 120_000f);
            float deep = OceanSpectrum.Tma(0.9f, 0.9f, alpha, 0f);
            float shallow = OceanSpectrum.Tma(0.9f, 0.9f, alpha, 3f);
            Assert.True(shallow < deep, $"shallow water must carry less energy: {shallow} vs {deep}");
            Assert.True(shallow > 0f);
        }

        // ---- Dispersion ------------------------------------------------------------------------------------

        [Fact]
        public void DispersionMatchesDeepWaterWhenDepthIsLarge()
        {
            const float k = 0.4f;
            float deep = MathF.Sqrt(OceanSpectrum.Gravity * k);
            Assert.Equal(deep, OceanSpectrum.Dispersion(k, 0f), 4);
            Assert.Equal(deep, OceanSpectrum.Dispersion(k, 500f), 4);
            // Shallow water slows the same wave down.
            Assert.True(OceanSpectrum.Dispersion(k, 1.5f) < deep);
        }

        [Theory]
        [InlineData(0.05f, 0f)]
        [InlineData(0.4f, 0f)]
        [InlineData(0.05f, 12f)]
        [InlineData(0.4f, 12f)]
        [InlineData(2.5f, 3f)]
        public void DispersionDerivativeMatchesANumericalDerivative(float k, float depth)
        {
            // Step and tolerance are set by the FLOAT central difference, not by the analytic form: omega is around
            // 5 here, so a 1e-7 relative representation error over a step of 2h is about 2.5e-4 of noise in the
            // quotient at h = 1e-3. Anything tighter would be pinning the test harness's precision, not the maths.
            const float h = 1e-3f;
            float numeric = (OceanSpectrum.Dispersion(k + h, depth) - OceanSpectrum.Dispersion(k - h, depth)) / (2f * h);
            float analytic = OceanSpectrum.DispersionDerivative(k, depth);
            Assert.True(MathF.Abs(numeric - analytic) < 2e-3f * MathF.Max(1f, MathF.Abs(numeric)),
                $"d(omega)/dk at k={k}, depth={depth}: analytic {analytic}, numeric {numeric}");
        }

        // ---- Directional spreading -------------------------------------------------------------------------

        [Theory]
        [InlineData(0f)]
        [InlineData(0.5f)]
        [InlineData(3f)]
        [InlineData(20f)]
        public void TheLonguetHigginsLobeIntegratesToOne(float s)
        {
            Assert.Equal(1.0, IntegrateOverATurn(t => OceanSpectrum.LonguetHiggins(t, s)), 3);
        }

        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(1f, 0f)]
        [InlineData(0.75f, 0.4f)]
        [InlineData(1f, 1f)]
        public void TheMixedSpreadingIntegratesToOneAtEverySetting(float spread, float swell)
        {
            const float omegaPeak = 0.9f, omega = 1.2f;
            Assert.Equal(1.0, IntegrateOverATurn(t => OceanSpectrum.DirectionalSpread(
                t, omega, omegaPeak, 11f, 0.5f, 2.1f, spread, swell)), 3);
        }

        [Fact]
        public void ZeroSpreadIsIsotropicAndFullSpreadFavoursTheWind()
        {
            const float omegaPeak = 0.9f, omega = 1.2f, wind = 0.5f;
            float flat = 1f / (2f * MathF.PI);
            Assert.Equal(flat, OceanSpectrum.DirectionalSpread(wind, omega, omegaPeak, 11f, wind, wind, 0f, 0f), 5);
            Assert.Equal(flat, OceanSpectrum.DirectionalSpread(wind + 2f, omega, omegaPeak, 11f, wind, wind, 0f, 0f), 5);

            float downwind = OceanSpectrum.DirectionalSpread(wind, omega, omegaPeak, 11f, wind, wind, 1f, 0f);
            float crosswind = OceanSpectrum.DirectionalSpread(wind + MathF.PI / 2f, omega, omegaPeak, 11f, wind, wind, 1f, 0f);
            float upwind = OceanSpectrum.DirectionalSpread(wind + MathF.PI, omega, omegaPeak, 11f, wind, wind, 1f, 0f);
            Assert.True(downwind > crosswind && crosswind > upwind,
                $"full spread must be strongly directional: {downwind} / {crosswind} / {upwind}");
        }

        [Fact]
        public void SwellSharpensTheLongWavesAndItsEffectDecaysWithFrequency()
        {
            const float omegaPeak = 0.9f, wind = 0.5f, swellDir = 0.5f;
            float LobeContrast(float omega, float swell)
            {
                float on = OceanSpectrum.DirectionalSpread(swellDir, omega, omegaPeak, 11f, wind, swellDir, 0.75f, swell);
                float off = OceanSpectrum.DirectionalSpread(swellDir + 0.6f, omega, omegaPeak, 11f, wind, swellDir, 0.75f, swell);
                return on / MathF.Max(off, 1e-9f);
            }

            // How much sharper the swell makes the lobe at this frequency, against the same sea with no swell.
            float Sharpening(float omega) => LobeContrast(omega, 0.9f) / LobeContrast(omega, 0f);

            float low = Sharpening(omegaPeak * 0.5f);
            float mid = Sharpening(omegaPeak * 2f);
            float high = Sharpening(omegaPeak * 8f);

            Assert.True(low > 2f, $"swell must visibly sharpen the long waves, got {low}x");
            // The swell term is scaled by tanh(omega_p / omega), so its reach falls away toward the short waves.
            // It decays rather than vanishing, which is the model's actual behaviour and worth pinning as such.
            Assert.True(low > mid && mid > high,
                $"swell sharpening must decay with frequency: {low} -> {mid} -> {high}");
        }

        // ---- Cascades --------------------------------------------------------------------------------------

        [Fact]
        public void TileSizesLadderDownByTheRatio()
        {
            Assert.Equal(250f, OceanSpectrum.TileMetres(0, 250f, 4.2f), 3);
            Assert.Equal(250f / 4.2f, OceanSpectrum.TileMetres(1, 250f, 4.2f), 3);
            Assert.Equal(250f / (4.2f * 4.2f), OceanSpectrum.TileMetres(2, 250f, 4.2f), 3);
        }

        [Fact]
        public void CascadeBandsPartitionWaveNumberSpaceWithNoGapAndNoOverlap()
        {
            const int n = 128;
            float previousHigh = 0f;
            for (int i = 0; i < 3; i++)
            {
                OceanSpectrum.CascadeBand(i, 3, 250f, 4.2f, n, out float low, out float high);
                Assert.Equal(previousHigh, low, 3);            // meets the previous band exactly: no gap, no overlap
                Assert.True(high > low, $"cascade {i} band is empty: [{low}, {high})");
                previousHigh = high;
            }
            Assert.True(float.IsPositiveInfinity(previousHigh), "the finest cascade must have an open upper bound");
        }

        [Fact]
        public void ASingleCascadeCoversEverything()
        {
            OceanSpectrum.CascadeBand(0, 1, 250f, 4.2f, 128, out float low, out float high);
            Assert.Equal(0f, low);
            Assert.True(float.IsPositiveInfinity(high));
        }

        // ---- Initial spectrum ------------------------------------------------------------------------------

        [Fact]
        public void TheInitialFieldIsHermitianSoTheTransformStaysReal()
        {
            const int n = 64;
            var h0 = new Vector4[n * n];
            OceanSpectrum.BuildInitialSpectrum(Sea(), 0, n, h0);

            for (int row = 1; row < n; row++)
            {
                for (int col = 1; col < n; col++)
                {
                    Vector4 here = h0[row * n + col];
                    Vector4 mirror = h0[((n - row) % n) * n + ((n - col) % n)];
                    // here.zw is conj(h0(-k)); the mirrored texel's xy IS h0(-k).
                    Assert.Equal(mirror.X, here.Z, 6);
                    Assert.Equal(-mirror.Y, here.W, 6);
                }
            }
        }

        [Fact]
        public void TheNyquistRowAndColumnAreZeroed()
        {
            const int n = 32;
            var h0 = new Vector4[n * n];
            OceanSpectrum.BuildInitialSpectrum(Sea(), 0, n, h0);
            for (int i = 0; i < n; i++)
            {
                Assert.Equal(Vector4.Zero, h0[i]);           // row 0
                Assert.Equal(Vector4.Zero, h0[i * n]);       // column 0
            }
        }

        [Fact]
        public void TheFieldIsDeterministicInTheSeedAndChangesWithIt()
        {
            const int n = 32;
            var a = new Vector4[n * n];
            var b = new Vector4[n * n];
            var c = new Vector4[n * n];
            WaterSeaState sea = Sea();
            OceanSpectrum.BuildInitialSpectrum(sea, 0, n, a);
            OceanSpectrum.BuildInitialSpectrum(sea, 0, n, b);
            sea.Seed = 8;
            OceanSpectrum.BuildInitialSpectrum(sea, 0, n, c);

            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
        }

        [Fact]
        public void EachCascadeOnlyCarriesItsOwnBand()
        {
            const int n = 64;
            WaterSeaState sea = Sea();
            var h0 = new Vector4[n * n];

            for (int cascade = 0; cascade < 3; cascade++)
            {
                OceanSpectrum.BuildInitialSpectrum(sea, cascade, n, h0);
                float tile = OceanSpectrum.TileMetres(cascade, sea.CascadeTileMetres, sea.CascadeTileRatio);
                OceanSpectrum.CascadeBand(cascade, 3, sea.CascadeTileMetres, sea.CascadeTileRatio, n,
                    out float low, out float high);
                float dk = 2f * MathF.PI / tile;

                for (int row = 1; row < n; row++)
                {
                    for (int col = 1; col < n; col++)
                    {
                        float kx = (col - n * 0.5f) * dk, kz = (row - n * 0.5f) * dk;
                        float k = MathF.Sqrt(kx * kx + kz * kz);
                        if (k >= low && k < high) continue;
                        Vector4 v = h0[row * n + col];
                        Assert.True(v.X == 0f && v.Y == 0f,
                            $"cascade {cascade} has energy at k={k}, outside its band [{low}, {high})");
                    }
                }
            }
        }

        [Fact]
        public void StrongerWindRaisesTheSurfaceVariance()
        {
            const int n = 64;
            WaterSeaState sea = Sea();
            float calm = TotalVariance(sea, n);
            sea.WindSpeed = 18f;
            float windy = TotalVariance(sea, n);
            Assert.True(windy > calm * 1.5f, $"wind must raise the sea: {windy} vs {calm}");
        }

        [Fact]
        public void ShallowWaterCutsTheSurfaceVariance()
        {
            const int n = 64;
            WaterSeaState sea = Sea();
            float deep = TotalVariance(sea, n);
            sea.DepthMetres = 4f;
            float shallow = TotalVariance(sea, n);
            Assert.True(shallow < deep, $"a shallow shelf must cut the sea down: {shallow} vs {deep}");
        }

        [Fact]
        public void SlopePerUnitOfHeightEnergyRisesWithEveryFinerCascade()
        {
            const int n = 64;
            WaterSeaState sea = Sea();
            var h0 = new Vector4[n * n];

            // The reported slope variance is a k^2-weighted integral of the same density the heights come from, so
            // its ratio to the height variance is a mean k^2 over the cascade's band. The bands are ordered and
            // disjoint, so that mean must rise with every step down the tile ladder - which is precisely why the
            // finest cascade is the one whose loss to the footprint band-limit has to become glint roughness.
            float previous = 0f;
            for (int cascade = 0; cascade < 3; cascade++)
            {
                float slope = OceanSpectrum.BuildInitialSpectrum(sea, cascade, n, h0).SlopeVariance;
                float height = HeightVariance(h0);
                Assert.True(slope > 0f && height > 0f, $"cascade {cascade} carries no energy at all");
                float meanKSquared = slope / height;
                Assert.True(meanKSquared > previous,
                    $"cascade {cascade} mean k^2 {meanKSquared} did not exceed cascade {cascade - 1}'s {previous}");
                previous = meanKSquared;
            }
        }

        // ---- helpers ---------------------------------------------------------------------------------------

        /// <summary>Trapezoidal integral of a directional function over a full turn. 4096 samples is far more than
        /// the sharpest lobe here needs, and it keeps the assertions on 3 decimals honest.</summary>
        static double IntegrateOverATurn(Func<float, float> f)
        {
            const int samples = 4096;
            double step = 2.0 * Math.PI / samples;
            double sum = 0;
            for (int i = 0; i < samples; i++) sum += f((float)(-Math.PI + i * step)) * step;
            return sum;
        }

        /// <summary>Total surface variance the baked field carries, summed over every cascade: by Parseval this is
        /// the mean square of the height map the transform produces.</summary>
        static float TotalVariance(WaterSeaState sea, int n)
        {
            var h0 = new Vector4[n * n];
            float total = 0f;
            for (int c = 0; c < sea.CascadeCount; c++)
            {
                OceanSpectrum.BuildInitialSpectrum(sea, c, n, h0);
                total += HeightVariance(h0);
            }
            return total;
        }

        /// <summary>Surface height variance one baked cascade carries: <c>sum |h0|^2 + |conj h0(-k)|^2</c>, which
        /// by Parseval is the mean square of the height map its transform produces.</summary>
        static float HeightVariance(ReadOnlySpan<Vector4> h0)
        {
            float total = 0f;
            foreach (Vector4 v in h0) total += v.X * v.X + v.Y * v.Y + v.Z * v.Z + v.W * v.W;
            return total;
        }
    }
}
