using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Pure headless coverage for the water surface's Gerstner swell: the wind-driven component generator, the
    /// trochoidal displacement, its analytic normal, and the fold factor that drives whitecap foam. No GPU;
    /// <see cref="GerstnerWaves"/> is the single source both this test and the GLSL <c>WaterVert</c> follow (see
    /// the in-source mirror comment).
    /// </summary>
    public class GerstnerWaveTests
    {
        static Span<GerstnerWaves.Component> Scratch(GerstnerWaves.Component[] backing) => backing.AsSpan();

        static int Build(WaterSettings s, GerstnerWaves.Component[] backing) =>
            GerstnerWaves.BuildComponents(s.SwellAmplitude, s.SwellWavelength,
                GerstnerWaves.DegreesToRadians(s.SwellDirectionDegrees),
                GerstnerWaves.DegreesToRadians(s.SwellSpreadDegrees),
                s.SwellSteepness, s.SwellSpeed, s.SwellSeed, s.SwellComponents, Scratch(backing));

        // ---- Component generation ------------------------------------------------------------------------------

        [Fact]
        public void BuildComponents_clamps_the_count_into_the_supported_range()
        {
            var backing = new GerstnerWaves.Component[GerstnerWaves.MaxComponents];
            Assert.Equal(1, GerstnerWaves.BuildComponents(0.5f, 40f, 0f, 1f, 0.5f, 1f, 0f, count: 0, Scratch(backing)));
            Assert.Equal(1, GerstnerWaves.BuildComponents(0.5f, 40f, 0f, 1f, 0.5f, 1f, 0f, count: -9, Scratch(backing)));
            Assert.Equal(GerstnerWaves.MaxComponents,
                GerstnerWaves.BuildComponents(0.5f, 40f, 0f, 1f, 0.5f, 1f, 0f, count: 99, Scratch(backing)));
        }

        [Fact]
        public void BuildComponents_returns_nothing_when_the_swell_is_off()
        {
            var backing = new GerstnerWaves.Component[GerstnerWaves.MaxComponents];
            // Amplitude 0 is the documented "flat plane" switch, and a non-positive wavelength is nonsense input;
            // both must produce an empty stack rather than a division by a degenerate wave number.
            Assert.Equal(0, GerstnerWaves.BuildComponents(0f, 40f, 0f, 1f, 0.5f, 1f, 0f, 4, Scratch(backing)));
            Assert.Equal(0, GerstnerWaves.BuildComponents(0.5f, 0f, 0f, 1f, 0.5f, 1f, 0f, 4, Scratch(backing)));
            Assert.Equal(0, GerstnerWaves.BuildComponents(0.5f, -3f, 0f, 1f, 0.5f, 1f, 0f, 4, Scratch(backing)));
        }

        [Fact]
        public void BuildComponents_amplitudes_sum_to_the_requested_total()
        {
            // The whole point of the closed-form lambda sum: the knob is the SUMMED amplitude, so a consumer that
            // raises the component count gets a more detailed sea at the same wave height, not a taller one.
            var backing = new GerstnerWaves.Component[GerstnerWaves.MaxComponents];
            for (int count = 1; count <= GerstnerWaves.MaxComponents; count++)
            {
                int n = GerstnerWaves.BuildComponents(0.75f, 40f, 0.4f, 0.9f, 0.6f, 1f, 0f, count, Scratch(backing));
                float sum = 0f;
                for (int i = 0; i < n; i++) sum += backing[i].Amplitude;
                Assert.Equal(0.75f, sum, 4);
            }
        }

        [Fact]
        public void BuildComponents_summed_steepness_equals_the_knob()
        {
            // sum(Q_i * k_i * A_i) == steepness is the no-self-intersection condition AND what makes the fold
            // factor normalizable, so it is an invariant of the generator, not an accident of the defaults.
            var backing = new GerstnerWaves.Component[GerstnerWaves.MaxComponents];
            foreach (float steepness in new[] { 0.15f, 0.6f, 1f })
            {
                int n = GerstnerWaves.BuildComponents(0.5f, 40f, 0.4f, 0.9f, steepness, 1f, 0f, 4, Scratch(backing));
                float sum = 0f;
                for (int i = 0; i < n; i++) sum += backing[i].Steepness * backing[i].WaveNumber * backing[i].Amplitude;
                Assert.Equal(steepness, sum, 4);
            }
        }

        [Fact]
        public void BuildComponents_wavelengths_ladder_down_and_stay_incommensurate()
        {
            var backing = new GerstnerWaves.Component[GerstnerWaves.MaxComponents];
            int n = GerstnerWaves.BuildComponents(0.5f, 42f, 0f, 0.9f, 0.6f, 1f, 0f, GerstnerWaves.MaxComponents, Scratch(backing));
            float previous = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                float lambda = MathF.Tau / backing[i].WaveNumber;
                Assert.True(lambda < previous, $"component {i} wavelength {lambda} did not shorten from {previous}");
                previous = lambda;
            }
            // Longest is the knob itself; every ratio is the same non-halving decay, so no component is a harmonic
            // of another and the stack has no short shared repeat.
            Assert.Equal(42f, MathF.Tau / backing[0].WaveNumber, 3);
            float ratio = (MathF.Tau / backing[1].WaveNumber) / (MathF.Tau / backing[0].WaveNumber);
            Assert.InRange(ratio, 0.6f, 0.75f);
            Assert.NotEqual(0.5f, ratio, 2);
        }

        [Fact]
        public void BuildComponents_fans_directions_around_the_wind_axis_within_the_spread()
        {
            const float dir = 0.6f, spread = 0.8f;
            var backing = new GerstnerWaves.Component[GerstnerWaves.MaxComponents];
            int n = GerstnerWaves.BuildComponents(0.5f, 40f, dir, spread, 0.6f, 1f, 0f, 4, Scratch(backing));

            float minAngle = float.MaxValue, maxAngle = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                Assert.Equal(1f, new Vector2(backing[i].DirX, backing[i].DirZ).Length(), 4);   // unit directions
                float angle = MathF.Atan2(backing[i].DirZ, backing[i].DirX);
                minAngle = MathF.Min(minAngle, angle); maxAngle = MathF.Max(maxAngle, angle);
            }
            Assert.InRange(minAngle, dir - spread - 1e-4f, dir);
            Assert.InRange(maxAngle, dir, dir + spread + 1e-4f);
            // The fan really spans: a stack that collapsed onto one heading would read as a corrugated sheet.
            Assert.True(maxAngle - minAngle > spread, $"fan spanned only {maxAngle - minAngle} rad of a {2 * spread} rad spread");
        }

        [Fact]
        public void BuildComponents_zero_spread_puts_every_component_on_the_wind_axis()
        {
            var backing = new GerstnerWaves.Component[GerstnerWaves.MaxComponents];
            int n = GerstnerWaves.BuildComponents(0.5f, 40f, 0.75f, 0f, 0.6f, 1f, 0f, 4, Scratch(backing));
            for (int i = 0; i < n; i++)
            {
                Assert.Equal(MathF.Cos(0.75f), backing[i].DirX, 4);
                Assert.Equal(MathF.Sin(0.75f), backing[i].DirZ, 4);
            }
        }

        [Fact]
        public void BuildComponents_long_components_travel_faster_than_short_ones()
        {
            // Deep-water dispersion (omega = sqrt(g*k)), not a uniform scroll rate. It is what makes the swell read
            // as an ocean rather than a scrolling texture: the long rollers overtake the chop.
            var backing = new GerstnerWaves.Component[GerstnerWaves.MaxComponents];
            int n = GerstnerWaves.BuildComponents(0.5f, 42f, 0f, 0.9f, 0.6f, 1f, 0f, 4, Scratch(backing));
            float previousSpeed = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                float phaseSpeed = backing[i].AngularSpeed / backing[i].WaveNumber;
                Assert.True(phaseSpeed < previousSpeed,
                    $"component {i} phase speed {phaseSpeed} did not fall below {previousSpeed}");
                previousSpeed = phaseSpeed;
            }
        }

        [Fact]
        public void BuildComponents_speed_scale_is_linear_and_zero_freezes_without_flattening()
        {
            var backing = new GerstnerWaves.Component[GerstnerWaves.MaxComponents];
            GerstnerWaves.BuildComponents(0.5f, 40f, 0f, 0.9f, 0.6f, 1f, 0f, 2, Scratch(backing));
            float full = backing[0].AngularSpeed;
            GerstnerWaves.BuildComponents(0.5f, 40f, 0f, 0.9f, 0.6f, 0.5f, 0f, 2, Scratch(backing));
            Assert.Equal(full * 0.5f, backing[0].AngularSpeed, 4);
            GerstnerWaves.BuildComponents(0.5f, 40f, 0f, 0.9f, 0.6f, 0f, 0f, 2, Scratch(backing));
            Assert.Equal(0f, backing[0].AngularSpeed, 6);
            Assert.True(backing[0].Amplitude > 0f, "a frozen swell must still have shape, not collapse to flat");
        }

        [Fact]
        public void BuildComponents_seed_shifts_phases_only()
        {
            var a = new GerstnerWaves.Component[GerstnerWaves.MaxComponents];
            var b = new GerstnerWaves.Component[GerstnerWaves.MaxComponents];
            int n = GerstnerWaves.BuildComponents(0.5f, 40f, 0.4f, 0.9f, 0.6f, 1f, seed: 0f, 4, Scratch(a));
            GerstnerWaves.BuildComponents(0.5f, 40f, 0.4f, 0.9f, 0.6f, 1f, seed: 3.7f, 4, Scratch(b));
            for (int i = 0; i < n; i++)
            {
                // Wind, ladder and shape identical: only the crest positions move, which is what decorrelating two
                // water bodies means (a seed that also rotated the fan would be a different SEA, not the same one
                // sampled elsewhere).
                Assert.Equal(a[i].DirX, b[i].DirX, 5);
                Assert.Equal(a[i].DirZ, b[i].DirZ, 5);
                Assert.Equal(a[i].WaveNumber, b[i].WaveNumber, 5);
                Assert.Equal(a[i].Amplitude, b[i].Amplitude, 5);
                Assert.Equal(a[i].AngularSpeed, b[i].AngularSpeed, 5);
                Assert.Equal(0f, a[i].Phase, 6);
                Assert.NotEqual(a[i].Phase, b[i].Phase, 3);
            }
        }

        // ---- Evaluation ----------------------------------------------------------------------------------------

        [Fact]
        public void Evaluate_with_no_components_is_the_flat_plane()
        {
            var flat = GerstnerWaves.Evaluate(11f, -4f, 3.25f, 0.6f, ReadOnlySpan<GerstnerWaves.Component>.Empty);
            Assert.Equal(Vector3.Zero, flat.Offset);
            Assert.Equal(Vector3.UnitY, flat.Normal);
            Assert.Equal(0f, flat.Fold);
        }

        [Fact]
        public void Evaluate_height_stays_within_the_summed_amplitude_and_the_normal_stays_unit()
        {
            var s = new WaterSettings();
            var backing = new GerstnerWaves.Component[GerstnerWaves.MaxComponents];
            int n = Build(s, backing);
            var comps = (ReadOnlySpan<GerstnerWaves.Component>)backing.AsSpan(0, n);

            for (int i = 0; i < 400; i++)
            {
                float x = -180f + i * 0.9f, z = 55f - i * 1.3f, t = i * 0.11f;
                var sample = GerstnerWaves.Evaluate(x, z, t, s.SwellSteepness, comps);
                Assert.InRange(sample.Offset.Y, -s.SwellAmplitude - 1e-3f, s.SwellAmplitude + 1e-3f);
                Assert.Equal(1f, sample.Normal.Length(), 4);
                Assert.True(sample.Normal.Y > 0f, "the swell normal must stay in the upper hemisphere");
                Assert.True(sample.Fold >= 0f);
            }
        }

        [Fact]
        public void Evaluate_zero_steepness_is_a_pure_vertical_sum_of_sines()
        {
            // Steepness 0 removes the horizontal orbital motion entirely, which is the degenerate case a plain
            // height-field water shader is stuck in. Nothing folds, so nothing whitecaps.
            var backing = new GerstnerWaves.Component[GerstnerWaves.MaxComponents];
            int n = GerstnerWaves.BuildComponents(0.5f, 40f, 0.4f, 0.9f, steepness: 0f, 1f, 0f, 4, Scratch(backing));
            var comps = (ReadOnlySpan<GerstnerWaves.Component>)backing.AsSpan(0, n);
            for (int i = 0; i < 60; i++)
            {
                var sample = GerstnerWaves.Evaluate(i * 3.1f, i * -1.7f, i * 0.2f, 0f, comps);
                Assert.Equal(0f, sample.Offset.X, 5);
                Assert.Equal(0f, sample.Offset.Z, 5);
                Assert.Equal(0f, sample.Fold, 5);
            }
        }

        [Fact]
        public void Evaluate_crests_travel_along_the_wind_direction()
        {
            // A single component, so the crest is unambiguous: after one quarter period the whole profile must have
            // shifted a quarter wavelength ALONG its direction, not against it. A sign slip here sends the entire
            // ocean backwards, which is exactly the kind of thing that looks subtly wrong and is never diagnosed.
            var backing = new GerstnerWaves.Component[GerstnerWaves.MaxComponents];
            int n = GerstnerWaves.BuildComponents(0.5f, 40f, directionRadians: 0f, spreadRadians: 0f,
                steepness: 0f, speedScale: 1f, seed: 0f, count: 1, Scratch(backing));
            var comps = (ReadOnlySpan<GerstnerWaves.Component>)backing.AsSpan(0, n);

            float k = backing[0].WaveNumber, omega = backing[0].AngularSpeed;
            float quarterPeriod = MathF.PI / (2f * omega);
            float quarterWavelength = MathF.PI / (2f * k);

            float atOrigin = GerstnerWaves.Evaluate(0f, 0f, 0f, 0f, comps).Offset.Y;
            float shifted = GerstnerWaves.Evaluate(quarterWavelength, 0f, quarterPeriod, 0f, comps).Offset.Y;
            Assert.Equal(atOrigin, shifted, 4);
        }

        [Fact]
        public void Evaluate_is_periodic_in_time_for_a_single_component()
        {
            var backing = new GerstnerWaves.Component[GerstnerWaves.MaxComponents];
            int n = GerstnerWaves.BuildComponents(0.5f, 40f, 0.3f, 0f, 0.6f, 1f, 0f, count: 1, Scratch(backing));
            var comps = (ReadOnlySpan<GerstnerWaves.Component>)backing.AsSpan(0, n);
            float period = MathF.Tau / backing[0].AngularSpeed;
            var a = GerstnerWaves.Evaluate(6f, -2f, 1.3f, 0.6f, comps);
            var b = GerstnerWaves.Evaluate(6f, -2f, 1.3f + period, 0.6f, comps);
            Assert.Equal(a.Offset.X, b.Offset.X, 3);
            Assert.Equal(a.Offset.Y, b.Offset.Y, 3);
            Assert.Equal(a.Offset.Z, b.Offset.Z, 3);
        }

        [Fact]
        public void Evaluate_never_folds_the_surface_through_itself_at_full_steepness()
        {
            // Steepness 1 is the documented ceiling. The invariant it buys is that the horizontal Jacobian
            // determinant stays non-negative everywhere, i.e. the sheet compresses but never turns inside out. The
            // fold factor is 1 - determinant normalized, so a determinant that went negative would also mean a fold
            // above 1 and permanent foam.
            var backing = new GerstnerWaves.Component[GerstnerWaves.MaxComponents];
            int n = GerstnerWaves.BuildComponents(0.6f, 30f, 0.4f, 0.9f, steepness: 1f, 1f, 0f, 4, Scratch(backing));
            var comps = (ReadOnlySpan<GerstnerWaves.Component>)backing.AsSpan(0, n);
            float maxFold = 0f;
            for (int i = 0; i < 120; i++)
                for (int j = 0; j < 120; j++)
                {
                    var sample = GerstnerWaves.Evaluate(i * 0.73f, j * 0.61f, 0f, 1f, comps);
                    maxFold = MathF.Max(maxFold, sample.Fold);
                }
            Assert.True(maxFold <= 1.0001f, $"fold reached {maxFold}: the surface folded through itself at steepness 1");
        }

        [Fact]
        public void Evaluate_fold_is_normalized_so_coverage_means_the_same_at_any_steepness()
        {
            // Halving the steepness halves the raw compression, so an un-normalized driver would silently halve
            // whitecap coverage as a side effect of a shape knob. Normalizing by the steepness is what decouples
            // them: the peak fold across the field should land in the same band either way.
            float PeakFold(float steepness)
            {
                var backing = new GerstnerWaves.Component[GerstnerWaves.MaxComponents];
                int n = GerstnerWaves.BuildComponents(0.45f, 42f, 0.5f, 0.96f, steepness, 0.6f, 0f, 4, Scratch(backing));
                var comps = (ReadOnlySpan<GerstnerWaves.Component>)backing.AsSpan(0, n);
                float peak = 0f;
                for (int i = 0; i < 150; i++)
                    for (int j = 0; j < 150; j++)
                        peak = MathF.Max(peak, GerstnerWaves.Evaluate(i * 1.7f, j * 1.3f, 0f, steepness, comps).Fold);
                return peak;
            }

            float low = PeakFold(0.3f), high = PeakFold(0.9f);
            Assert.InRange(low, 0.55f, 1.0001f);
            Assert.InRange(high, 0.55f, 1.0001f);
            Assert.True(MathF.Abs(high - low) < 0.3f,
                $"peak fold moved from {low} to {high} across the steepness range: the normalization is not holding");
        }

        [Fact]
        public void EvaluateSettings_matches_building_and_evaluating_by_hand()
        {
            var s = new WaterSettings { SwellSeed = 1.25f, SwellComponents = 5 };
            var backing = new GerstnerWaves.Component[GerstnerWaves.MaxComponents];
            int n = Build(s, backing);
            var expected = GerstnerWaves.Evaluate(13f, -8f, 2.5f, s.SwellSteepness, backing.AsSpan(0, n));

            Span<GerstnerWaves.Component> scratch = stackalloc GerstnerWaves.Component[GerstnerWaves.MaxComponents];
            var actual = GerstnerWaves.EvaluateSettings(s, 13f, -8f, 2.5f, scratch);

            Assert.Equal(expected.Offset.X, actual.Offset.X, 5);
            Assert.Equal(expected.Offset.Y, actual.Offset.Y, 5);
            Assert.Equal(expected.Offset.Z, actual.Offset.Z, 5);
            Assert.Equal(expected.Fold, actual.Fold, 5);
        }

        [Fact]
        public void DegreesToRadians_matches_the_settings_contract()
        {
            Assert.Equal(0f, GerstnerWaves.DegreesToRadians(0f), 6);
            Assert.Equal(MathF.PI / 2f, GerstnerWaves.DegreesToRadians(90f), 5);
            Assert.Equal(-MathF.PI, GerstnerWaves.DegreesToRadians(-180f), 5);
        }
    }
}
