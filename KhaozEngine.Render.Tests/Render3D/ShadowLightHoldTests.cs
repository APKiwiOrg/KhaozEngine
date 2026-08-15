using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Pure (device-free) tests for the shadow fit's light-movement epsilon (<see cref="ShadowLightHold"/>, issue
    /// #410, design section 3.3). These pin the arithmetic the whole feature rests on: the threshold is the design's
    /// elevation-corrected rule and nothing else, it tightens the way the design says it does as the sun drops, and a
    /// budget of 0 can never hold a single frame.
    /// <para>
    /// The GPU half (does a held frame actually skip the pass, and does the receiver still agree with the atlas) is
    /// KhaozEngine.Tests.Gpu.ShadowLightHoldGpuTests.
    /// </para>
    /// </summary>
    public sealed class ShadowLightHoldTests
    {
        const int Resolution = 2048;
        const float Radius = 12f;          // a plausible cascade 0 fitted slice-sphere radius
        const float CasterHeight = 12f;    // the shipped ShadowLightHoldCasterHeight default

        /// <summary>A key light travelling away from a sun <paramref name="elevationDegrees"/> above the horizon, at
        /// azimuth <paramref name="azimuthDegrees"/>. Matches the sun helper the shadow benches use.</summary>
        static Vector3 SunAt(float elevationDegrees, float azimuthDegrees)
        {
            float e = elevationDegrees * MathF.PI / 180f;
            float a = azimuthDegrees * MathF.PI / 180f;
            return Vector3.Normalize(new Vector3(MathF.Cos(a) * MathF.Cos(e), -MathF.Sin(e), MathF.Sin(a) * MathF.Cos(e)));
        }

        /// <summary>The same sun, stepped in DOUBLE before it is rounded to a float direction. <c>35f + 0.001f</c>
        /// quantizes the step itself (a float near 35 has an ulp of 3.8e-6), which would measure the test's own
        /// rounding rather than the angle helper's.</summary>
        static Vector3 SunAtD(double elevationDegrees)
        {
            double e = elevationDegrees * Math.PI / 180.0;
            return Vector3.Normalize(new Vector3((float)Math.Cos(e), (float)(-Math.Sin(e)), 0f));
        }

        /// <summary>The design's rule, written out independently of the implementation:
        /// <c>h*dTheta/sin^2(e) &lt; budget * 2r/res</c> solved for dTheta.</summary>
        static double ExpectedThreshold(double radius, int resolution, double elevationDegrees, double casterHeight, double budget)
        {
            double sinE = Math.Sin(elevationDegrees * Math.PI / 180.0);
            return budget * (2.0 * radius / resolution) * sinE * sinE / casterHeight;
        }

        [Theory]
        [InlineData(90f)]
        [InlineData(60f)]
        [InlineData(35f)]   // the shadow benches' sun
        [InlineData(15f)]
        [InlineData(5f)]    // a dusk Ruinborne's 30 minute day passes through twice a cycle
        [InlineData(1f)]
        public void Threshold_is_the_designs_elevation_corrected_rule(float elevationDegrees)
        {
            float sinE = ShadowLightHold.SinElevation(SunAt(elevationDegrees, 0f));
            float actual = ShadowLightHold.ThresholdRadians(Radius, Resolution, sinE, CasterHeight, texelBudget: 1f);
            double expected = ExpectedThreshold(Radius, Resolution, elevationDegrees, CasterHeight, 1.0);
            // Relative, not absolute: the reference is evaluated in double while the implementation reads sin(e) off
            // a float-normalized direction, so the two agree to float precision and not beyond it.
            Assert.Equal(expected, actual, expected * 1e-5);
        }

        [Fact]
        public void A_five_degree_sun_holds_about_43_times_tighter_than_a_thirty_five_degree_one()
        {
            // The design's own number, and the reason the elevation cannot be baked into a constant: sin^2(35)/sin^2(5).
            float high = ShadowLightHold.ThresholdRadians(Radius, Resolution,
                ShadowLightHold.SinElevation(SunAt(35f, 0f)), CasterHeight, 1f);
            float low = ShadowLightHold.ThresholdRadians(Radius, Resolution,
                ShadowLightHold.SinElevation(SunAt(5f, 0f)), CasterHeight, 1f);
            Assert.True(low > 0f && high > 0f);
            Assert.Equal(43.3, high / low, 1);
        }

        [Fact]
        public void Threshold_scales_with_the_budget_the_radius_and_the_caster_height()
        {
            float sinE = ShadowLightHold.SinElevation(SunAt(35f, 0f));
            float baseline = ShadowLightHold.ThresholdRadians(Radius, Resolution, sinE, CasterHeight, 1f);
            Assert.Equal(2f * baseline, ShadowLightHold.ThresholdRadians(Radius, Resolution, sinE, CasterHeight, 2f), 9);
            Assert.Equal(2f * baseline, ShadowLightHold.ThresholdRadians(2f * Radius, Resolution, sinE, CasterHeight, 1f), 9);
            Assert.Equal(0.5f * baseline, ShadowLightHold.ThresholdRadians(Radius, Resolution, sinE, 2f * CasterHeight, 1f), 9);
            Assert.Equal(0.5f * baseline, ShadowLightHold.ThresholdRadians(Radius, 2 * Resolution, sinE, CasterHeight, 1f), 9);
        }

        [Theory]
        [InlineData(0f)]        // the off switch
        [InlineData(-1f)]       // and anything below it
        public void A_budget_of_zero_or_less_never_holds(float budget)
        {
            float sinE = ShadowLightHold.SinElevation(SunAt(35f, 0f));
            Assert.Equal(0f, ShadowLightHold.ThresholdRadians(Radius, Resolution, sinE, CasterHeight, budget));
            // Including for a light that did not move at all, which is what makes the disabled path byte-for-byte the
            // pre-epsilon fit rather than merely close to it.
            Vector3 sun = SunAt(35f, 0f);
            Assert.True(ShadowLightHold.ShouldAdopt(sun, sun, Radius, Resolution, CasterHeight, budget));
        }

        [Fact]
        public void Degenerate_inputs_all_re_fit_rather_than_holding()
        {
            float sinE = ShadowLightHold.SinElevation(SunAt(35f, 0f));
            Assert.Equal(0f, ShadowLightHold.ThresholdRadians(Radius, Resolution, sinE, maxCasterHeight: 0f, texelBudget: 1f));
            Assert.Equal(0f, ShadowLightHold.ThresholdRadians(Radius, Resolution, sinE, maxCasterHeight: -3f, texelBudget: 1f));
            Assert.Equal(0f, ShadowLightHold.ThresholdRadians(minCascadeRadius: 0f, Resolution, sinE, CasterHeight, 1f));
            Assert.Equal(0f, ShadowLightHold.ThresholdRadians(Radius, resolution: 0, sinE, CasterHeight, 1f));
            // A sun exactly on the horizon throws an infinitely long shadow, so nothing may be held.
            Assert.Equal(0f, ShadowLightHold.ThresholdRadians(Radius, Resolution, sinElevation: 0f, CasterHeight, 1f));
            Assert.Equal(0f, ShadowLightHold.ThresholdRadians(Radius, Resolution, float.NaN, CasterHeight, 1f));
            // And a degenerate live direction adopts, so it reaches BuildLightViewProj's own straight-down fallback.
            Assert.True(ShadowLightHold.ShouldAdopt(SunAt(35f, 0f), new Vector3(float.NaN, float.NaN, float.NaN),
                Radius, Resolution, CasterHeight, 1f));
        }

        // Radius is paired with elevation so every row's threshold clears MinResolvableRadians: the low-sun rows use a
        // wider cascade, which is the situation a real low sun is in anyway (the far cascades hold when cascade 0
        // cannot). The floor itself gets its own test below.
        [Theory]
        [InlineData(35f, Radius)]
        [InlineData(15f, Radius)]
        [InlineData(5f, 60f)]
        [InlineData(2f, 250f)]
        public void Adoption_flips_exactly_at_the_threshold(float elevationDegrees, float radius)
        {
            Vector3 held = SunAt(elevationDegrees, 0f);
            float sinE = ShadowLightHold.SinElevation(held);
            float threshold = ShadowLightHold.ThresholdRadians(radius, Resolution, sinE, CasterHeight, 1f);
            Assert.True(threshold > ShadowLightHold.MinResolvableRadians,
                $"this row must clear the resolvability floor to be testing the threshold ({threshold})");

            // An azimuth step of dPhi turns the direction by dPhi*cos(e), so step in the elevation plane instead,
            // where the angle between the two directions IS the step and the compare can be aimed at the threshold.
            float justUnder = threshold * 0.9f * 180f / MathF.PI;
            float justOver = threshold * 1.1f * 180f / MathF.PI;
            Vector3 under = SunAt(elevationDegrees + justUnder, 0f);
            Vector3 over = SunAt(elevationDegrees + justOver, 0f);

            Assert.False(ShadowLightHold.ShouldAdopt(held, under, radius, Resolution, CasterHeight, 1f),
                "a sub-threshold sun step must keep the held direction, so the fit reproduces its matrices");
            Assert.True(ShadowLightHold.ShouldAdopt(held, over, radius, Resolution, CasterHeight, 1f),
                "a supra-threshold sun step must adopt, so the fit moves and the pass re-records");
        }

        [Fact]
        public void A_threshold_finer_than_a_float_direction_can_carry_re_fits_instead_of_guessing()
        {
            // A near-horizon sun. The rule itself still returns a threshold, and it is a real number, but it is finer
            // than the angular resolution a Vector3 of floats has, so acting on it would be acting on rounding error.
            Vector3 held = SunAt(1f, 0f);
            float sinE = ShadowLightHold.SinElevation(held);
            float threshold = ShadowLightHold.ThresholdRadians(Radius, Resolution, sinE, CasterHeight, 1f);
            Assert.True(threshold > 0f, "the rule is still evaluated");
            Assert.True(threshold < ShadowLightHold.MinResolvableRadians, "and it lands under the floor");

            // So the decision degrades to today's behaviour: re-fit, even for a sun that has not moved at all.
            Assert.True(ShadowLightHold.ShouldAdopt(held, held, Radius, Resolution, CasterHeight, 1f));
            Assert.True(ShadowLightHold.ShouldAdopt(held, SunAt(1.0001f, 0f), Radius, Resolution, CasterHeight, 1f));
        }

        [Fact]
        public void The_elevation_read_is_the_lower_of_the_two_directions()
        {
            // Rising out of a dusk: the drift per radian is worst at the LOW end of the interval, so the threshold
            // must be the low sun's, not the high one's. A step that a 35 degree threshold would hold must adopt when
            // it starts at 5 degrees. A wide cascade keeps both thresholds clear of the resolvability floor, so this
            // is testing the elevation read and not the floor.
            const float wide = 60f;
            float highThreshold = ShadowLightHold.ThresholdRadians(wide, Resolution,
                ShadowLightHold.SinElevation(SunAt(35f, 0f)), CasterHeight, 1f);
            float stepDegrees = highThreshold * 0.5f * 180f / MathF.PI;
            Vector3 low = SunAt(5f, 0f);
            Vector3 raised = SunAt(5f + stepDegrees, 0f);
            Assert.True(ShadowLightHold.ThresholdRadians(wide, Resolution,
                ShadowLightHold.SinElevation(low), CasterHeight, 1f) > ShadowLightHold.MinResolvableRadians);
            Assert.True(ShadowLightHold.ShouldAdopt(low, raised, wide, Resolution, CasterHeight, 1f),
                "the threshold must be sized by the LOWER elevation in the interval, where a shadow drifts furthest");
            // The same step at a steady 35 degrees is comfortably held, which is what makes the row above a compare
            // of the two elevations rather than a step that was simply too large.
            Assert.False(ShadowLightHold.ShouldAdopt(SunAt(35f, 0f), SunAt(35f + stepDegrees, 0f),
                wide, Resolution, CasterHeight, 1f));
        }

        [Fact]
        public void A_sun_that_wanders_and_returns_has_not_moved_its_shadow()
        {
            // The compare is the total angle between held and live, not an accumulated arc length. A sun that steps
            // away and comes back sits at zero displacement, and holding is correct.
            Vector3 held = SunAt(35f, 0f);
            float threshold = ShadowLightHold.ThresholdRadians(Radius, Resolution,
                ShadowLightHold.SinElevation(held), CasterHeight, 1f);
            float stepDegrees = threshold * 0.8f * 180f / MathF.PI;
            Vector3 away = SunAt(35f + stepDegrees, 0f);
            Assert.False(ShadowLightHold.ShouldAdopt(held, away, Radius, Resolution, CasterHeight, 1f));
            Assert.False(ShadowLightHold.ShouldAdopt(held, held, Radius, Resolution, CasterHeight, 1f));
        }

        [Fact]
        public void Angle_between_is_accurate_at_the_tiny_angles_this_decision_lives_at()
        {
            // acos(dot) is worthless here: at 0.001 degrees the dot product is 1 - 1.5e-10, which a float cannot even
            // represent as distinct from 1, so the angle would read as exactly 0 and every sun step would be held
            // forever. The chord form has no such collapse (subtracting two nearby floats is exact), and it must land
            // on the right answer all the way down to the resolvability floor.
            foreach (double degrees in new[] { 1.0, 0.1, 0.01, 0.001 })
            {
                Vector3 a = SunAtD(35.0);
                Vector3 b = SunAtD(35.0 + degrees);
                double expected = degrees * Math.PI / 180.0;
                Assert.True(expected > ShadowLightHold.MinResolvableRadians, "the sweep stays above the floor");
                Assert.Equal(expected, ShadowLightHold.AngleBetween(a, b), expected * 0.01);
            }
            Assert.Equal(0f, ShadowLightHold.AngleBetween(Vector3.UnitY, Vector3.UnitY), 7);
            // Below the floor the INPUT runs out, not the formula: a float unit vector's components round at about
            // 6e-8, which is a large fraction of the chord at 1e-6 radians. That is what MinResolvableRadians is for,
            // and it is why the hold refuses to act on a threshold that fine rather than trusting this number.
            Assert.True(ShadowLightHold.AngleBetween(SunAtD(35.0), SunAtD(35.000001)) < ShadowLightHold.MinResolvableRadians);
        }

        [Fact]
        public void Sin_elevation_reads_the_travel_direction_and_is_sign_free()
        {
            Assert.Equal(1f, ShadowLightHold.SinElevation(SunAt(90f, 0f)), 5);   // straight down
            Assert.Equal(0f, ShadowLightHold.SinElevation(SunAt(0f, 0f)), 5);    // along the horizon
            Assert.Equal(MathF.Sin(35f * MathF.PI / 180f), ShadowLightHold.SinElevation(SunAt(35f, 140f)), 5);
            // A light travelling UPWARD is the mirror elevation, never a negative one.
            Assert.Equal(0.5f, ShadowLightHold.SinElevation(Vector3.Normalize(new Vector3(0f, 0.5f, -0.866f))), 3);
        }

        [Fact]
        public void The_shipped_defaults_are_the_ones_the_design_justifies()
        {
            var s = new ShadowSettings();
            Assert.Equal(1f, s.ShadowLightHoldTexels);          // one texel, the snap's own existing discontinuity
            Assert.Equal(12f, s.ShadowLightHoldCasterHeight);   // the design's tall tree
        }
    }
}
