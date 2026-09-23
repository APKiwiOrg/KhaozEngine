using System;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The sun glint's final GGX alpha (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/308">#308</see>):
    /// the distance and footprint widening is a FLOOR under the Toksvig lobe rather than a second widening stacked on
    /// it. Both respond to the same ripple detail falling below the pixel, so the sum counted that detail twice.
    /// <para>
    /// The measured table is the one the issue records, taken at the shipped defaults with the distance ramp off
    /// (the orthographic or near-camera regime) and the swell off, so the ripple spectrum is the only source of
    /// removed slope variance. "The surface" is the Toksvig lobe alone, <c>sqrt(near^2 + 2 * lost)</c>, whose
    /// variance is the near lobe plus exactly what the band-limit removed.
    /// </para>
    /// </summary>
    public class WaterGlintFloorTests
    {
        const float Near = 0.22f, Far = 0.5f;           // GlintRoughness / GlintDistantRoughness defaults
        const float NormalStrength = 0.35f, FadeDistance = 60f, DistantDetail = 0.18f;
        const float Lambda = 2.5f * 6.28318531f;        // base ripple wavelength at the default WaveScale

        /// <summary>Ripple slope variance the band-limit and the detail fade remove at the shipped spectrum,
        /// scaled by NormalStrength squared exactly as the fragment scales it.</summary>
        static float Lost(float footprint, float samples, float detail)
        {
            var comps = new RippleSpectrum.Component[RippleSpectrum.MaxComponents];
            int n = RippleSpectrum.Build(2.5f, 1.48f, 0.66f, 0f, 10, comps);
            RippleSpectrum.SlopeSample s = RippleSpectrum.Slope(0f, 0f, 0f, comps.AsSpan(0, n), footprint, samples, detail);
            return s.LostVariance * NormalStrength * NormalStrength;
        }

        static float Toksvig(float lost, float gain) => RippleSpectrum.AlphaFromVariance(Near * Near, lost, gain);

        [Theory]
        [InlineData(1f, 0.156f)]
        [InlineData(3.93f, 0.210f)]
        [InlineData(7.85f, 0.250f)]
        [InlineData(31.4f, 0.250f)]
        public void The_widening_is_a_floor_under_the_toksvig_lobe(float footprint, float expected)
        {
            float lost = Lost(footprint, 4f, 1f);
            float wide = WaterMath.GlintRoughnessAt(Near, Far, 0f, FadeDistance, footprint, Lambda);
            float alpha = WaterMath.GlintAlpha(Near, wide, lost, 1f);
            Assert.Equal(expected, alpha, 3);

            // Before #308 the transfer stacked on the widened alpha: 0.162, 0.242, 0.337 and 0.340 here, which is
            // 1.08, 1.33, 2.12 and 2.08 times the surface's slope variance. Now the lobe IS the surface until the
            // GlintDistantRoughness floor takes over, and the floor overshoots it by 1.12 to 1.17.
            float surface = Toksvig(lost, 1f);
            float ratio = alpha * alpha / (surface * surface);
            if (footprint < 7f) Assert.Equal(1f, ratio, 4);
            else Assert.InRange(ratio, 1f, 1.18f);
        }

        [Fact]
        public void The_floor_never_narrows_below_either_term_and_never_widens_past_the_old_sum()
        {
            foreach (float footprint in new[] { 0f, 0.01f, 0.5f, 1f, 3.93f, 7.85f, 15.7f, 31.4f, 200f })
            foreach (float distance in new[] { 0f, 10f, 30f, 60f, 500f })
            foreach (float gain in new[] { 0f, 0.5f, 1f, 3f })
            {
                float detail = WaterMath.DetailScale(distance, FadeDistance, DistantDetail);
                float lost = Lost(footprint, 4f, detail);
                float wide = WaterMath.GlintRoughnessAt(Near, Far, distance, FadeDistance, footprint, Lambda);
                float alpha = WaterMath.GlintAlpha(Near, wide, lost, gain);

                string at = $"footprint {footprint}, distance {distance}, gain {gain}";
                Assert.True(alpha >= MathF.Min(wide * wide, 1f) - 1e-6f, $"narrower than the widening at {at}");
                Assert.True(alpha >= Toksvig(lost, gain) - 1e-6f, $"narrower than the Toksvig lobe at {at}");
                float sum = RippleSpectrum.AlphaFromVariance(wide * wide, lost, gain);
                Assert.True(alpha <= sum + 1e-6f, $"wider than the old summed lobe at {at}");
            }
        }

        [Fact]
        public void The_14_24_lobe_stays_reachable_through_either_knob()
        {
            foreach (float footprint in new[] { 0.01f, 1f, 3.93f, 7.85f, 31.4f })
            foreach (float distance in new[] { 0f, 30f, 500f })
            {
                float wide = WaterMath.GlintRoughnessAt(Near, Far, distance, FadeDistance, footprint, Lambda);
                float detail = WaterMath.DetailScale(distance, FadeDistance, DistantDetail);

                // VarianceToRoughness = 0: nothing transfers, whatever was removed.
                Assert.Equal(wide * wide, WaterMath.GlintAlpha(Near, wide, Lost(footprint, 4f, detail), 0f), 6);
                // FootprintSamples = 0: the band-limit removes nothing. The detail fade is a separate artistic
                // removal, so it is held off here (DistantDetailScale = 1).
                Assert.Equal(0f, Lost(footprint, 0f, 1f), 6);
                Assert.Equal(wide * wide, WaterMath.GlintAlpha(Near, wide, Lost(footprint, 0f, 1f), 1f), 6);
            }
        }

        [Fact]
        public void Alpha_stays_clamped_to_one()
        {
            Assert.Equal(1f, WaterMath.GlintAlpha(Near, 0.5f, 500f, 1f), 6);
            Assert.Equal(1f, WaterMath.GlintAlpha(Near, 3f, 0f, 1f), 6);
            Assert.Equal(1f, WaterMath.GlintAlpha(2f, 0.5f, 0f, 1f), 6);
            Assert.True(WaterMath.GlintAlpha(Near, Far, 0.02f, 1f) < 1f);
        }
    }
}
