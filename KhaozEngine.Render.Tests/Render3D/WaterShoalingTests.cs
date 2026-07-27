using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Headless coverage for the depth-driven half of the water surface: the shoaling taper, the breaker
    /// criterion, the surf band's ramp and the crest-phase surge (<see cref="WaterShoaling"/>), the consumer seam
    /// that feeds them (<see cref="WaterBathymetry"/>), and the spectrum statistics they key on. GPU-free by
    /// construction - all of it is closed-form maths over a depth in metres.
    /// </summary>
    public sealed class WaterShoalingTests
    {
        const float Strength = 1f, Scale = 1f;

        // ---- The taper ---------------------------------------------------------------------------------------

        [Fact]
        public void DeepWaterIsNotAttenuatedAtAll()
        {
            // 10 km of water under a 40 metre wave: tanh's argument is in the hundreds and the factor is 1 to
            // well under a float ULP. This is the case every open-ocean fragment takes.
            float k = MathF.Tau / 40f;
            Assert.Equal(1f, WaterShoaling.Attenuation(WaterShoaling.DeepMetres, k, Scale, Strength, 0f, 0f), 6);
        }

        [Fact]
        public void NoStrengthReturnsExactlyOneWhateverTheDepth()
        {
            // Not "close to 1": an early return, so a consumer who never set a depth field multiplies by a literal
            // 1.0 and its ocean is bit-identical to one built before shoaling existed.
            foreach (float depth in new[] { 0f, 0.1f, 3f, 50f })
                Assert.Equal(1f, WaterShoaling.Attenuation(depth, 0.15f, Scale, 0f, 1f, 1f));
        }

        [Fact]
        public void TheSurfaceFlattensCompletelyAtTheWaterline()
        {
            float k = MathF.Tau / 40f;
            Assert.Equal(0f, WaterShoaling.Attenuation(0f, k, Scale, Strength, 0f, 0f), 6);
            // Land reads as zero depth rather than as a negative one, so nothing goes through the taper backwards.
            Assert.Equal(0f, WaterShoaling.Attenuation(-8f, k, Scale, Strength, 0f, 0f), 6);
        }

        [Fact]
        public void AttenuationRisesMonotonicallyWithDepth()
        {
            float k = MathF.Tau / 40f;
            float previous = -1f;
            for (float d = 0f; d <= 40f; d += 0.5f)
            {
                float a = WaterShoaling.Attenuation(d, k, Scale, Strength, 0f, 0f);
                Assert.True(a > previous, $"attenuation fell from {previous} to {a} going deeper, at {d} m");
                previous = a;
            }
        }

        /// <summary>
        /// The property the whole per-cascade design exists for, and the reason a single scalar taper would not
        /// do: at one depth the LONG swell is calmed and the short chop is not. A wave feels the bottom at about
        /// half its own wavelength, so a 40 metre swell in 6 metres of water is well into it while 2 metre chop
        /// has not noticed.
        /// </summary>
        [Fact]
        public void LongSwellCalmsWellBeforeShortChopDoes()
        {
            const float depth = 6f;
            float swell = WaterShoaling.Attenuation(depth, MathF.Tau / 40f, Scale, Strength, 0f, 0f);
            float chop = WaterShoaling.Attenuation(depth, MathF.Tau / 2f, Scale, Strength, 0f, 0f);
            Assert.True(swell < 0.8f, $"a 40 m swell in {depth} m of water kept {swell:P0} of its amplitude");
            Assert.True(chop > 0.99f, $"2 m chop in {depth} m of water lost {1f - chop:P0} of its amplitude");
        }

        [Fact]
        public void ADepthScaleBelowOneWidensTheCalmShelf()
        {
            float k = MathF.Tau / 40f;
            float physical = WaterShoaling.Attenuation(12f, k, 1f, Strength, 0f, 0f);
            float wider = WaterShoaling.Attenuation(12f, k, 0.4f, Strength, 0f, 0f);
            Assert.True(wider < physical,
                $"a depth scale of 0.4 left more amplitude ({wider}) at 12 m than the physical 1.0 did ({physical})");
        }

        [Fact]
        public void PartialStrengthBlendsTowardTheUntouchedSurface()
        {
            float k = MathF.Tau / 40f;
            float full = WaterShoaling.Attenuation(3f, k, Scale, 1f, 0f, 0f);
            float half = WaterShoaling.Attenuation(3f, k, Scale, 0.5f, 0f, 0f);
            Assert.Equal(1f + (full - 1f) * 0.5f, half, 6);
        }

        [Fact]
        public void TheBreakCollapseAppliesFlatAcrossEveryWaveNumber()
        {
            // Unlike the taper, which is per wave number by construction: a broken wave is whitewater at every
            // scale, so the collapse hits the chop the taper barely touched.
            const float band = 1f, collapse = 0.6f;
            float chopClean = WaterShoaling.Attenuation(6f, MathF.Tau / 2f, Scale, Strength, 0f, 0f);
            float chopBroken = WaterShoaling.Attenuation(6f, MathF.Tau / 2f, Scale, Strength, band, collapse);
            Assert.Equal(chopClean * (1f - collapse), chopBroken, 5);
        }

        // ---- Breaking ----------------------------------------------------------------------------------------

        [Fact]
        public void TheBreakLineFollowsTheClassicHOverDCriterion()
        {
            Assert.Equal(2.5f / 0.78f, WaterShoaling.BreakDepth(2.5f, 0.78f), 4);
            // A bigger sea breaks further out; a higher index pulls the break into shallower water.
            Assert.True(WaterShoaling.BreakDepth(4f, 0.78f) > WaterShoaling.BreakDepth(2.5f, 0.78f));
            Assert.True(WaterShoaling.BreakDepth(2.5f, 1.2f) < WaterShoaling.BreakDepth(2.5f, 0.78f));
        }

        [Fact]
        public void SignificantHeightIsFourRootM0()
        {
            Assert.Equal(4f * MathF.Sqrt(0.5f), WaterShoaling.SignificantHeight(0.5f), 5);
            Assert.Equal(0f, WaterShoaling.SignificantHeight(-1f));
        }

        [Fact]
        public void TheSurfBandRunsFromNothingAtTheBreakLineToEverythingAtTheWaterline()
        {
            const float breakDepth = 3.2f;
            Assert.Equal(0f, WaterShoaling.SurfBand(breakDepth, breakDepth, 1f), 5);
            Assert.Equal(0f, WaterShoaling.SurfBand(20f, breakDepth, 1f), 5);
            Assert.Equal(1f, WaterShoaling.SurfBand(0f, breakDepth, 1f), 5);
            Assert.Equal(1f, WaterShoaling.SurfBand(-5f, breakDepth, 1f), 5);

            float previous = 2f;
            for (float d = 0f; d <= breakDepth; d += 0.1f)
            {
                float t = WaterShoaling.SurfBand(d, breakDepth, 1f);
                Assert.True(t <= previous + 1e-6f, $"the band rose again going deeper, at {d} m");
                previous = t;
            }
        }

        [Fact]
        public void ANarrowRampReachesFullSurfSoonerBelowTheBreakLine()
        {
            // The knob is the ramp's SPAN, not where the band starts (that is the breaker index): both still
            // begin foaming at the break line, and the narrow one saturates first.
            const float breakDepth = 3.2f;
            Assert.Equal(0f, WaterShoaling.SurfBand(breakDepth, breakDepth, 0.3f), 5);
            float gradual = WaterShoaling.SurfBand(2f, breakDepth, 1f);
            float hard = WaterShoaling.SurfBand(2f, breakDepth, 0.3f);
            Assert.True(hard > gradual,
                $"a 0.3 ramp only reached {hard} at 2 m where the full-span ramp reached {gradual}");
            Assert.Equal(1f, hard, 5);
        }

        [Fact]
        public void NoBreakDepthDisablesTheBandEntirely()
            => Assert.Equal(0f, WaterShoaling.SurfBand(0.1f, 0f, 1f));

        // ---- The surge ---------------------------------------------------------------------------------------

        /// <summary>
        /// The difference between a wave crashing and a band glowing: foam exists on the CREST and not in the
        /// trough, so as the wave rolls in the white moves with it.
        /// </summary>
        [Fact]
        public void FoamIsOnTheCrestAndNotInTheTrough()
        {
            Assert.Equal(1f, WaterShoaling.Surge(1.2f, 0.25f, 0.8f, 0f), 5);
            Assert.Equal(0f, WaterShoaling.Surge(-1f, 0.25f, 0.8f, 0f), 5);
            Assert.Equal(0f, WaterShoaling.Surge(0.2f, 0.25f, 0f, 0f), 5);
        }

        [Fact]
        public void TheTrailOnlyExistsOnTheSeawardFace()
        {
            // Just below the crest gate, so only the trail term can contribute.
            const float rise = 0.1f;
            Assert.Equal(0f, WaterShoaling.Surge(rise, 0.25f, 0.8f, 0f), 5);
            Assert.True(WaterShoaling.Surge(rise, 0.25f, 0.8f, 1f) > 0.3f,
                "the seaward face behind the crest carries no trail, so the foam blinks off with the wave");
        }

        [Fact]
        public void ABiasOfOneCannotBlackOutTheBand()
        {
            // smoothstep with equal edges is degenerate, so the bias is hard-limited just under 1.
            Assert.True(WaterShoaling.Surge(1.5f, 1f, 0.8f, 0f) > 0.9f);
            Assert.True(WaterShoaling.Surge(1.5f, 5f, 0.8f, 0f) > 0.9f);
        }

        [Fact]
        public void TheBackFaceGateSaturates()
        {
            Assert.Equal(0f, WaterShoaling.BackFace(-0.5f));
            Assert.Equal(1f, WaterShoaling.BackFace(2f));
            Assert.Equal(0.6f, WaterShoaling.BackFace(0.1f), 5);
        }

        // ---- The consumer seam -------------------------------------------------------------------------------

        [Fact]
        public void FillFromGroundWritesDepthBelowTheSurfaceAndBumpsTheRevision()
        {
            var field = new WaterBathymetry(8, centerX: 0f, centerZ: 0f, halfExtentX: 40f);
            int before = field.Revision;
            // A beach running up along +X: ground rises from -20 to +20 across the rect.
            field.FillFromGround((x, _) => x * 0.5f, surfaceY: 3f);

            Assert.NotEqual(before, field.Revision);
            for (int z = 0; z < field.Resolution; z++)
            {
                for (int x = 0; x < field.Resolution; x++)
                    Assert.Equal(3f - field.WorldX(x) * 0.5f, field.Depths[z * field.Resolution + x], 4);
            }
            // Deep at the seaward end, dry land at the shoreward end: the sign convention the shaders rely on.
            Assert.True(field.Depths[0] > 0f);
            Assert.True(field.Depths[field.Resolution - 1] < 0f);
        }

        [Fact]
        public void TheRectangleIsSquareUnlessASecondHalfExtentIsGiven()
        {
            var square = new WaterBathymetry(4, 10f, -6f, 25f);
            Assert.Equal(25f, square.HalfExtentZ);
            Assert.Equal(2f * 25f / 4f, square.TexelSizeX, 5);

            var oblong = new WaterBathymetry(4, 0f, 0f, 100f, 25f);
            Assert.Equal(25f, oblong.HalfExtentZ);
        }

        [Theory]
        [InlineData(0, WaterBathymetry.MinResolution)]
        [InlineData(4096, WaterBathymetry.MaxResolution)]
        [InlineData(64, 64)]
        public void ResolutionIsClampedRatherThanRejected(int requested, int expected)
            => Assert.Equal(expected, new WaterBathymetry(requested, 0f, 0f, 10f).Resolution);

        [Fact]
        public void PackingRoundTripsADepthThroughHalfPrecision()
        {
            var depths = new float[] { 0f, 0.25f, -3.5f, 42f };
            var bytes = new byte[depths.Length * 8];
            WaterBathymetryMap.Pack(depths, bytes, 2);
            for (int i = 0; i < depths.Length; i++)
            {
                float back = (float)BitConverter.Int16BitsToHalf(
                    (short)(bytes[i * 8] | (bytes[i * 8 + 1] << 8)));
                Assert.Equal(depths[i], back, 2);
                // Only the red channel carries anything.
                for (int c = 2; c < 8; c++) Assert.Equal(0, bytes[i * 8 + c]);
            }
        }

        // ---- What the spectrum has to supply -----------------------------------------------------------------

        /// <summary>
        /// The taper's <c>k</c> has to be ENERGY-weighted, not the band's midpoint, and this is the check that
        /// says so: cascade 0's band runs from 0 up to its Nyquist with nearly all of its energy at the spectral
        /// peak near the bottom, so its mean wave number has to land near the peak's, an order of magnitude below
        /// the midpoint. A midpoint would put the swell's k so high that the swell never felt the bottom.
        /// </summary>
        [Fact]
        public void TheMeanWavenumberTracksTheSpectralPeakAndRisesPerCascade()
        {
            var sea = new WaterSeaState { CascadeCount = 3, CascadeResolution = 64 };
            var h0 = new Vector4[64 * 64];
            float previous = 0f;
            float total = 0f;
            for (int c = 0; c < 3; c++)
            {
                OceanSpectrum.CascadeStatistics stats = OceanSpectrum.BuildInitialSpectrum(sea, c, 64, h0);
                Assert.True(stats.MeanWavenumber > previous,
                    $"cascade {c} mean k {stats.MeanWavenumber} did not exceed cascade {c - 1}'s {previous}");
                Assert.True(stats.HeightVariance > 0f, $"cascade {c} carries no height energy");
                previous = stats.MeanWavenumber;
                total += stats.HeightVariance;
            }

            OceanSpectrum.CascadeStatistics swell = OceanSpectrum.BuildInitialSpectrum(sea, 0, 64, h0);
            float peakOmega = OceanSpectrum.PeakAngularFrequency(sea.WindSpeed, sea.FetchKilometres * 1000f);
            // Deep-water dispersion inverted: k = omega^2 / g at the peak.
            float peakK = peakOmega * peakOmega / OceanSpectrum.Gravity;
            Assert.InRange(swell.MeanWavenumber, 0.5f * peakK, 4f * peakK);

            // And the sea state's own significant height is a believable metres-scale number for a fresh breeze,
            // which is what the breaker criterion measures a depth against.
            Assert.InRange(WaterShoaling.SignificantHeight(total), 0.5f, 8f);
        }

        // ---- The shaders mirror this -------------------------------------------------------------------------

        /// <summary>
        /// The GLSL is a hand-written mirror, so the constants and the entry points are pinned by name here, the
        /// same contract <c>WaterMathTests</c> holds for the fragment's own maths. A drift in either direction is
        /// a surface that shades differently from the geometry it was displaced by.
        /// </summary>
        [Fact]
        public void BothStagesCarryTheMirroredShoreConstantsAndHelpers()
        {
            foreach (string source in new[]
                { ShaderSources.WaterFrag, ShaderSources.WaterVert, ShaderSources.WaterClipmapVert })
            {
                Assert.Contains($"const float KE_SHOAL_TANH_LIMIT = {WaterShoaling.TanhArgumentLimit:0.0};", source);
                Assert.Contains($"const float KE_SURF_BACK_GAIN = {WaterShoaling.BackFaceGain:0.0};", source);
                Assert.Contains($"const float KE_SURF_MAX_BIAS = {WaterShoaling.MaxCrestBias:0.00};", source);
                Assert.Contains("float oceanShoal(float depth, float band, int cascade)", source);
                Assert.Contains("float oceanSurfBand(float depth)", source);
                Assert.Contains("uniform texture2D BathyTex;", source);
            }
            // The surge and its two extra depth taps are FRAGMENT-only: the vertex stage collapses amplitude, it
            // does not paint foam.
            Assert.Contains("float oceanSurge(float riseN, float backFace)", ShaderSources.WaterFrag);
            Assert.Contains("surf = clamp(surfBand * oceanSurge(riseN, back) * SurfParams.x, 0.0, 1.0);",
                ShaderSources.WaterFrag);
        }

        /// <summary>
        /// The bathymetry pair must be bound AHEAD of the ocean maps and the scene depth, and sampled in that
        /// order inside each stage's <c>main</c>. Both halves are Metal-only failure modes that produce a
        /// perfectly correct picture on Vulkan and Direct3D11, so they are pinned in the GPU-free lane where they
        /// are cheap to catch.
        /// </summary>
        [Fact]
        public void TheBathymetryBindingLeadsTheOceanInBothDeclarationAndFirstUse()
        {
            foreach (string source in new[]
                { ShaderSources.WaterFrag, ShaderSources.WaterVert, ShaderSources.WaterClipmapVert })
            {
                Assert.True(source.IndexOf("binding=0) uniform texture2D BathyTex", StringComparison.Ordinal)
                            < source.IndexOf("binding=2) uniform texture2DArray OceanMap", StringComparison.Ordinal),
                    "the depth field must be declared before the ocean maps");
                int firstBathy = source.IndexOf("sampler2D(BathyTex, BathySamp)", StringComparison.Ordinal);
                int firstOcean = source.IndexOf("sampler2DArray(OceanMap, OceanSamp)", StringComparison.Ordinal);
                Assert.True(firstBathy > 0 && firstBathy < firstOcean,
                    "the depth field must be SAMPLED before the ocean maps: the Metal cross-compiler numbers a " +
                    "stage's textures by first reference, so the wrong order swaps them silently.");
            }
            int depthTex = ShaderSources.WaterFrag.IndexOf("sampler2D(DepthTex, Samp)", StringComparison.Ordinal);
            Assert.True(depthTex > ShaderSources.WaterFrag.IndexOf("sampler2DArray(OceanMap, OceanSamp)",
                StringComparison.Ordinal), "the scene depth is binding 4 and must be sampled last");
        }
    }
}
