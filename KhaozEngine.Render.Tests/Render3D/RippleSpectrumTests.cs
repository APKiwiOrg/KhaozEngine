using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Pure headless coverage for the water ripple SLOPE SPECTRUM that replaced the three fixed cosines in
    /// 14.26.0: the generator, the per-component footprint band-limit, the slope-variance-to-roughness transfer,
    /// and the swell shading attenuation. No GPU; <see cref="RippleSpectrum"/> is the single source both this test
    /// and the GLSL <c>waterSlope</c> follow. The field-shape invariants the old three-cosine tests asserted
    /// (unit normals, no repeat at the legacy period, not axis-separable, warp behaviour, determinism) are carried
    /// over here against the new field, since they are properties of the water surface, not of that field.
    /// </summary>
    public class RippleSpectrumTests
    {
        static RippleSpectrum.Component[] Build(WaterSettings s)
        {
            var backing = new RippleSpectrum.Component[RippleSpectrum.MaxComponents];
            int n = RippleSpectrum.Build(s.WaveScale, s.RippleLacunarity, s.RippleGain, s.RippleSeed,
                s.RippleComponents, backing);
            return backing[..n];
        }

        static Vector3 Normal(WaterSettings s, float x, float z, float t, float footprint = 0f)
        {
            var comps = Build(s);
            var warped = WaterMath.DomainWarp(x, z, t * s.WaveSpeed, s.WaveScale, s.WaveWarpStrength);
            var slope = RippleSpectrum.Slope(warped.X, warped.Y, t * s.WaveSpeed, comps,
                footprint, s.FootprintSamples, detailScale: 1f);
            return WaterMath.SlopeToNormal(slope.DhDx, slope.DhDz, s.NormalStrength);
        }

        // ---- Generator ------------------------------------------------------------------------------------------

        [Fact]
        public void Build_clamps_the_count_into_the_supported_range()
        {
            var backing = new RippleSpectrum.Component[RippleSpectrum.MaxComponents];
            Assert.Equal(1, RippleSpectrum.Build(2.5f, 1.48f, 0.66f, 0f, 0, backing));
            Assert.Equal(1, RippleSpectrum.Build(2.5f, 1.48f, 0.66f, 0f, -7, backing));
            Assert.Equal(RippleSpectrum.MaxComponents, RippleSpectrum.Build(2.5f, 1.48f, 0.66f, 0f, 99, backing));
        }

        [Fact]
        public void Build_holds_total_slope_variance_constant_across_every_spectrum_knob()
        {
            // The invariant that lets NormalStrength keep its meaning. Without it, RippleComponents, Lacunarity and
            // Gain would each silently double as a chop-strength knob and no consumer tune would survive a change
            // to any of them.
            static float Variance(int count, float lac, float gain)
            {
                var backing = new RippleSpectrum.Component[RippleSpectrum.MaxComponents];
                int n = RippleSpectrum.Build(2.5f, lac, gain, 0f, count, backing);
                float v = 0f;
                for (int i = 0; i < n; i++) v += backing[i].SlopeAmplitude * backing[i].SlopeAmplitude * 0.5f;
                return v;
            }

            float reference = Variance(10, 1.48f, 0.66f);
            Assert.True(reference > 0f);
            foreach (int count in new[] { 1, 3, 5, 8, 12 })
                Assert.Equal(reference, Variance(count, 1.48f, 0.66f), 3);
            foreach (float lac in new[] { 1.2f, 1.48f, 1.9f, 2.4f })
                Assert.Equal(reference, Variance(10, lac, 0.66f), 3);
            foreach (float gain in new[] { 0.4f, 0.66f, 0.9f, 1.1f })
                Assert.Equal(reference, Variance(10, gain: gain, lac: 1.48f), 3);
        }

        [Fact]
        public void Build_spreads_headings_around_the_whole_circle_with_no_two_parallel()
        {
            // The property that stops the field being a ruled pattern. Hand-picked headings (what the three-cosine
            // field had) always leave a dominant direction; a golden-angle walk never does, at any count.
            var comps = Build(new WaterSettings());
            Assert.Equal(10, comps.Length);

            for (int i = 0; i < comps.Length; i++)
            {
                Assert.Equal(1f, new Vector2(comps[i].DirX, comps[i].DirZ).Length(), 4);
                for (int j = i + 1; j < comps.Length; j++)
                {
                    // |cross| near 0 means parallel or antiparallel; either is a shared ribbon direction.
                    float cross = MathF.Abs(comps[i].DirX * comps[j].DirZ - comps[i].DirZ * comps[j].DirX);
                    Assert.True(cross > 0.02f, $"components {i} and {j} are near-parallel (|cross| {cross})");
                }
            }
        }

        [Fact]
        public void Build_spans_several_octaves_of_wave_number()
        {
            var comps = Build(new WaterSettings());
            float lo = comps[0].WaveNumber, hi = comps[^1].WaveNumber;
            float octaves = MathF.Log2(hi / lo);
            // The three-cosine field spanned log2(2.64575) = 1.4 octaves, which is what made a tight specular lobe
            // trace continuous contours instead of sampling a random facet per pixel (issue #299).
            Assert.True(octaves > 4f, $"spectrum spans only {octaves:F2} octaves");
        }

        [Fact]
        public void Build_scroll_rate_follows_deep_water_dispersion()
        {
            var comps = Build(new WaterSettings());
            Assert.Equal(1f, comps[0].ScrollRate, 4);   // normalized at the base component
            for (int i = 1; i < comps.Length; i++)
            {
                Assert.True(comps[i].ScrollRate > comps[i - 1].ScrollRate);
                // omega ~ sqrt(k): the ratio of rates must track the square root of the ratio of wave numbers.
                float expected = MathF.Sqrt(comps[i].WaveNumber / comps[0].WaveNumber);
                Assert.Equal(expected, comps[i].ScrollRate, 3);
            }
        }

        [Fact]
        public void Build_seed_changes_the_field_without_changing_its_energy()
        {
            var a = Build(new WaterSettings());
            var b = Build(new WaterSettings { RippleSeed = 2.5f });
            float va = 0f, vb = 0f;
            for (int i = 0; i < a.Length; i++)
            {
                va += a[i].SlopeAmplitude * a[i].SlopeAmplitude;
                vb += b[i].SlopeAmplitude * b[i].SlopeAmplitude;
                Assert.Equal(a[i].WaveNumber, b[i].WaveNumber, 4);
                Assert.NotEqual(a[i].DirX, b[i].DirX, 3);
            }
            Assert.Equal(va, vb, 4);
        }

        // ---- Field shape (carried over from the retired three-cosine tests) --------------------------------------

        [Fact]
        public void Normal_is_always_unit_length_and_upper_hemisphere()
        {
            var s = new WaterSettings();
            for (int i = 0; i < 300; i++)
            {
                var n = Normal(s, -40f + i * 0.7f, 25f - i * 1.1f, i * 0.13f);
                Assert.Equal(1f, n.Length(), 4);
                Assert.True(n.Y > 0f);
            }
        }

        [Fact]
        public void Normal_zero_strength_is_flat_up()
        {
            var n = Normal(new WaterSettings { NormalStrength = 0f }, 3f, -2f, 1.5f);
            Assert.Equal(Vector3.UnitY, n);
        }

        [Fact]
        public void Normal_stronger_perturbation_tilts_further_from_up()
        {
            float weak = Normal(new WaterSettings { NormalStrength = 0.1f }, 1.3f, 0.7f, 2f).Y;
            float strong = Normal(new WaterSettings { NormalStrength = 0.6f }, 1.3f, 0.7f, 2f).Y;
            Assert.True(strong < weak, $"stronger perturbation did not tilt further ({strong} vs {weak})");
        }

        [Fact]
        public void Normal_animates_over_time_and_is_deterministic_at_a_frozen_clock()
        {
            var s = new WaterSettings();
            Assert.NotEqual(Normal(s, 2f, 2f, 0f), Normal(s, 2f, 2f, 5f));
            Assert.Equal(Normal(s, 4f, -1f, 0f), Normal(s, 4f, -1f, 0f));
        }

        [Fact]
        public void Normal_degenerate_scale_does_not_throw_or_nan()
        {
            var n = Normal(new WaterSettings { WaveScale = 0f }, 1f, 1f, 1f);
            Assert.False(float.IsNaN(n.X) || float.IsNaN(n.Y) || float.IsNaN(n.Z));
            Assert.Equal(1f, n.Length(), 4);
        }

        [Fact]
        public void Normal_does_not_repeat_at_the_legacy_octave_period()
        {
            // The 14.22.0 regression guard, re-pointed at the new field: the pre-14.22.0 two-octave field matched
            // itself exactly at 2*pi*WaveScale, which WAS the checkerboard.
            var s = new WaterSettings();
            float period = MathF.Tau * s.WaveScale;
            for (int i = 0; i < 40; i++)
            {
                float x = i * 0.83f, z = -i * 0.51f, t = i * 0.2f;
                var baseN = Normal(s, x, z, t);
                // The longest component alone does repeat at this period (its wavelength IS the period); the
                // other nine are incommensurate with it, so the SUM never matches. Measured worst case over these
                // samples is 0.0078, against exactly 0.000 for the pre-14.22.0 field this guard was written for.
                Assert.True((Normal(s, x + period, z, t) - baseN).Length() > 0.002f);
                Assert.True((Normal(s, x, z + period, t) - baseN).Length() > 0.002f);
            }
        }

        [Fact]
        public void Normal_is_not_axis_separable()
        {
            var s = new WaterSettings();
            var a = Normal(s, 0.4f, 0.9f, 1.1f);
            var b = Normal(s, 0.4f, 3.9f, 1.1f);
            Assert.True(MathF.Abs(a.X - b.X) > 1e-4f,
                "dH/dx did not respond to a change in z: the field is axis-separable, which is what tiles");
        }

        // ---- Footprint band-limit -------------------------------------------------------------------------------

        [Fact]
        public void Resolve_keeps_well_sampled_components_and_drops_unresolvable_ones()
        {
            Assert.Equal(1f, RippleSpectrum.Resolve(wavelength: 20f, footprint: 0.1f, samplesPerWavelength: 4f));
            // Asymptotically zero rather than exactly zero: the smoothstep only reaches 0 at a zero ratio, and a
            // component contributing 3 parts in 10,000 of its slope is gone for every purpose that matters.
            Assert.True(RippleSpectrum.Resolve(wavelength: 0.2f, footprint: 5f, samplesPerWavelength: 4f) < 1e-3f);
            float mid = RippleSpectrum.Resolve(wavelength: 2f, footprint: 0.25f, samplesPerWavelength: 4f);
            Assert.InRange(mid, 0f, 1f);
            // Monotone in footprint: a coarser pixel can never resolve more.
            float finer = RippleSpectrum.Resolve(2f, 0.2f, 4f), coarser = RippleSpectrum.Resolve(2f, 0.6f, 4f);
            Assert.True(coarser <= finer);
        }

        [Fact]
        public void Resolve_is_disabled_by_a_non_positive_samples_or_footprint()
        {
            // The documented legacy switch: FootprintSamples = 0 restores 14.24.0's unbounded normal oscillation.
            Assert.Equal(1f, RippleSpectrum.Resolve(0.001f, 100f, 0f));
            Assert.Equal(1f, RippleSpectrum.Resolve(0.001f, 100f, -1f));
            Assert.Equal(1f, RippleSpectrum.Resolve(0.001f, 0f, 4f));
        }

        [Fact]
        public void Slope_collapses_to_flat_as_the_footprint_grows_and_hands_the_energy_over()
        {
            // The whole point of the release, as one assertion: at a footprint that cannot resolve anything, the
            // normal field goes flat (so there is nothing left to alias into stripes) AND the variance it dropped
            // is reported so the lobe can take it, rather than being silently lost.
            var s = new WaterSettings();
            var comps = Build(s);

            var near = RippleSpectrum.Slope(12f, -7f, 1.3f, comps, footprint: 0.002f, s.FootprintSamples, 1f);
            var far = RippleSpectrum.Slope(12f, -7f, 1.3f, comps, footprint: 400f, s.FootprintSamples, 1f);

            Assert.True(MathF.Abs(near.DhDx) + MathF.Abs(near.DhDz) > 0.01f, "the near field should have real slope");
            Assert.Equal(0f, near.LostVariance, 5);
            Assert.True(MathF.Abs(far.DhDx) < 1e-3f, $"far slope did not collapse ({far.DhDx})");
            Assert.True(MathF.Abs(far.DhDz) < 1e-3f, $"far slope did not collapse ({far.DhDz})");

            float totalVariance = 0f;
            foreach (var c in comps) totalVariance += c.SlopeAmplitude * c.SlopeAmplitude * 0.5f;
            Assert.Equal(totalVariance, far.LostVariance, 4);
        }

        [Fact]
        public void Slope_drops_the_fine_components_first()
        {
            // Band-limiting must be per-component, not one global fade: at a mid footprint the long ripples must
            // survive while the short ones go, which is what leaves a readable surface instead of flat water.
            var s = new WaterSettings();
            var comps = Build(s);
            float footprint = 0.35f;
            float firstKeep = RippleSpectrum.Resolve(MathF.Tau / comps[0].WaveNumber, footprint, s.FootprintSamples);
            float lastKeep = RippleSpectrum.Resolve(MathF.Tau / comps[^1].WaveNumber, footprint, s.FootprintSamples);
            Assert.Equal(1f, firstKeep, 4);
            Assert.True(lastKeep < 0.4f, $"the shortest component still contributed {lastKeep}");
            Assert.True(lastKeep < firstKeep);
        }

        // ---- Variance to roughness ------------------------------------------------------------------------------

        [Fact]
        public void AlphaFromVariance_widens_the_lobe_and_is_disabled_at_zero_gain()
        {
            const float alpha = 0.05f;
            Assert.Equal(alpha, RippleSpectrum.AlphaFromVariance(alpha, slopeVariance: 0.4f, gain: 0f), 5);
            Assert.Equal(alpha, RippleSpectrum.AlphaFromVariance(alpha, slopeVariance: 0f, gain: 1f), 5);

            float widened = RippleSpectrum.AlphaFromVariance(alpha, 0.02f, 1f);
            Assert.True(widened > alpha, $"variance did not widen the lobe ({widened} vs {alpha})");
            Assert.True(RippleSpectrum.AlphaFromVariance(alpha, 0.2f, 1f) > widened);
            // Never past a fully rough lobe, whatever the variance.
            Assert.Equal(1f, RippleSpectrum.AlphaFromVariance(alpha, 500f, 1f), 5);
        }

        // ---- Swell shading attenuation ---------------------------------------------------------------------------

        [Fact]
        public void SwellAttenuation_is_full_near_and_fades_the_shading_contrast_at_range()
        {
            var s = new WaterSettings();
            float lostNear = RippleSpectrum.SwellAttenuation(s.SwellWavelength, s.SwellAmplitude, s.SwellComponents,
                GerstnerWaves.LambdaDecay, footprint: 0.01f, s.FootprintSamples, out float attenNear);
            Assert.Equal(1f, attenNear, 4);
            Assert.Equal(0f, lostNear, 5);

            float lostFar = RippleSpectrum.SwellAttenuation(s.SwellWavelength, s.SwellAmplitude, s.SwellComponents,
                GerstnerWaves.LambdaDecay, footprint: 300f, s.FootprintSamples, out float attenFar);
            Assert.True(attenFar < 0.01f, $"the swell shading did not fade out at range ({attenFar})");
            Assert.True(lostFar > 0f, "a fully attenuated swell must hand its variance to the lobe");
        }

        [Fact]
        public void SwellAttenuation_partially_fades_when_only_the_short_components_are_unresolved()
        {
            // The ladder must degrade gracefully rather than switching off wholesale: at a footprint between the
            // shortest and longest component the attenuation has to land strictly inside 0..1.
            var s = new WaterSettings();
            float shortest = s.SwellWavelength * MathF.Pow(GerstnerWaves.LambdaDecay, s.SwellComponents - 1);
            float footprint = shortest * 3f / s.FootprintSamples;   // buries the shortest, keeps the longest
            RippleSpectrum.SwellAttenuation(s.SwellWavelength, s.SwellAmplitude, s.SwellComponents,
                GerstnerWaves.LambdaDecay, footprint, s.FootprintSamples, out float atten);
            Assert.InRange(atten, 0.05f, 0.95f);
        }

        [Fact]
        public void SwellAttenuation_is_inert_when_the_swell_is_off()
        {
            float lost = RippleSpectrum.SwellAttenuation(42f, amplitude: 0f, 4, GerstnerWaves.LambdaDecay,
                footprint: 500f, 4f, out float atten);
            Assert.Equal(1f, atten);
            Assert.Equal(0f, lost);
        }

        // ---- Settings -------------------------------------------------------------------------------------------

        [Fact]
        public void Spectrum_defaults_are_sensible()
        {
            var s = new WaterSettings();
            Assert.InRange(s.RippleComponents, 1, RippleSpectrum.MaxComponents);
            Assert.True(s.RippleComponents > 3, "the defect being fixed was a three-component field");
            Assert.True(s.RippleLacunarity > 1f);
            Assert.NotEqual(2f, s.RippleLacunarity);   // an exact octave ladder re-introduces a shared repeat
            Assert.True(s.RippleGain > 0f && s.RippleGain <= 1f);
            Assert.True(s.FootprintSamples >= 2f, "below Nyquist the band-limit fades a component only once it is already aliasing");
            Assert.True(s.VarianceToRoughness > 0f);
        }
    }
}
