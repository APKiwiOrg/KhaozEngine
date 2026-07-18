using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Pure (device-free) coverage of the day/night mapping: the sun-arc geometry (elevation, azimuth,
    /// key-light direction), the elevation-keyed palette blend, the night handling (disc hidden, key dip,
    /// virtual moon), and the settings write-through in <see cref="SunCycle.Apply"/>.
    /// </summary>
    public sealed class SunCycleTests
    {
        static void AssertFinite(SunCycleState st)
        {
            Assert.True(float.IsFinite(st.LightDirection.X) && float.IsFinite(st.LightDirection.Y) && float.IsFinite(st.LightDirection.Z), $"LightDirection not finite: {st.LightDirection}");
            Assert.True(float.IsFinite(st.SunElevationDegrees), $"SunElevationDegrees not finite: {st.SunElevationDegrees}");
            AssertFiniteColor(st.HorizonColor);
            AssertFiniteColor(st.ZenithColor);
            AssertFiniteColor(st.SunColor);
            AssertFiniteColor(st.LightColor);
            AssertFiniteColor(st.AmbientColor);
            AssertFiniteColor(st.FillLightColor);
        }

        static void AssertFiniteColor(Color c) =>
            Assert.True(float.IsFinite(c.R) && float.IsFinite(c.G) && float.IsFinite(c.B) && float.IsFinite(c.A), $"color not finite: {c}");

        static void AssertColorEqual(Color expected, Color actual)
        {
            Assert.Equal(expected.R, actual.R, 1e-4);
            Assert.Equal(expected.G, actual.G, 1e-4);
            Assert.Equal(expected.B, actual.B, 1e-4);
            Assert.Equal(expected.A, actual.A, 1e-4);
        }

        [Fact]
        public void Noon_places_the_sun_at_peak_elevation()
        {
            var s = new SunCycleSettings();
            var noon = SunCycle.Evaluate(0.5f, s);
            // Peak elevation for lat 35, dec 15 is 90 - |35 - 15| = 70.
            Assert.Equal(70f, noon.SunElevationDegrees, 0.1);
        }

        [Fact]
        public void Time_of_day_wraps()
        {
            var s = new SunCycleSettings();
            var a = SunCycle.Evaluate(0.25f, s);
            var b = SunCycle.Evaluate(1.25f, s);
            var c = SunCycle.Evaluate(-0.75f, s);
            Assert.Equal(a.LightDirection, b.LightDirection);
            Assert.Equal(a.SunElevationDegrees, c.SunElevationDegrees, 3);
            Assert.Equal(a.AmbientColor, b.AmbientColor);
        }

        [Fact]
        public void Morning_sun_rises_east_of_the_meridian()
        {
            var s = new SunCycleSettings();
            // Sun toward +X (east) in the morning, so the light TRAVELS toward -X (west).
            Assert.True(SunCycle.Evaluate(0.3f, s).LightDirection.X < 0f);
            Assert.True(SunCycle.Evaluate(0.7f, s).LightDirection.X > 0f);
        }

        [Fact]
        public void Heading_rotates_the_sun_path()
        {
            var baseline = SunCycle.Evaluate(0.3f, new SunCycleSettings());
            var rotated = SunCycle.Evaluate(0.3f, new SunCycleSettings { HeadingDegrees = 180f });
            Assert.Equal(-baseline.LightDirection.X, rotated.LightDirection.X, 1e-4);
            Assert.Equal(-baseline.LightDirection.Z, rotated.LightDirection.Z, 1e-4);
            Assert.Equal(baseline.LightDirection.Y, rotated.LightDirection.Y, 1e-4);
        }

        [Fact]
        public void Equatorial_noon_zenith_is_finite()
        {
            var s = new SunCycleSettings { LatitudeDegrees = 0f, SolarDeclinationDegrees = 0f };
            var noon = SunCycle.Evaluate(0.5f, s);
            AssertFinite(noon);
            Assert.Equal(0f, noon.LightDirection.X, 1e-3);
            Assert.Equal(-1f, noon.LightDirection.Y, 1e-3);
            Assert.Equal(0f, noon.LightDirection.Z, 1e-3);
            Assert.Equal(90f, noon.SunElevationDegrees, 0.1);
        }

        [Fact]
        public void Polar_latitude_stays_finite()
        {
            var s = new SunCycleSettings { LatitudeDegrees = 90f, SolarDeclinationDegrees = 15f };
            for (float t = 0f; t <= 1f; t += 0.01f)
            {
                var st = SunCycle.Evaluate(t, s);
                AssertFinite(st);
                Assert.Equal(15f, st.SunElevationDegrees, 0.5);
            }
        }

        [Fact]
        public void Light_always_travels_downward_or_horizontal()
        {
            var s = new SunCycleSettings();
            for (float t = 0f; t <= 1f; t += 0.005f)
            {
                var st = SunCycle.Evaluate(t, s);
                Assert.True(st.LightDirection.Y <= 1e-4f, $"light should never travel upward at t={t}, Y={st.LightDirection.Y}");
                Assert.Equal(1f, st.LightDirection.Length(), 1e-3);
            }
        }

        [Fact]
        public void Midnight_sun_is_below_the_horizon_and_the_disc_is_hidden()
        {
            var s = new SunCycleSettings();
            var midnight = SunCycle.Evaluate(0f, s);
            Assert.True(midnight.SunElevationDegrees < 0f, $"midnight elevation should be negative, got {midnight.SunElevationDegrees}");
            Assert.False(midnight.SunEnabled);
            Assert.True(midnight.SunColor.R < 1e-4f && midnight.SunColor.G < 1e-4f && midnight.SunColor.B < 1e-4f, $"night disc should be black, got {midnight.SunColor}");
        }

        [Fact]
        public void Night_key_light_is_the_virtual_moon()
        {
            var s = new SunCycleSettings();
            var midnight = SunCycle.Evaluate(0f, s);
            Assert.True(midnight.LightDirection.Y < -0.3f, $"night key should point well downward, got {midnight.LightDirection}");
            var night = s.NightPalette.LightColor;
            Assert.Equal(night.R, midnight.LightColor.R, 1e-3);
            Assert.Equal(night.G, midnight.LightColor.G, 1e-3);
            Assert.Equal(night.B, midnight.LightColor.B, 1e-3);
        }

        [Fact]
        public void High_noon_with_default_palettes_reproduces_the_engine_default_look()
        {
            var s = new SunCycleSettings();
            var noon = SunCycle.Evaluate(0.5f, s);
            Assert.True(noon.SunEnabled);
            AssertColorEqual(new Color(0.62f, 0.70f, 0.80f, 1f), noon.HorizonColor);
            AssertColorEqual(new Color(0.22f, 0.42f, 0.72f, 1f), noon.ZenithColor);
            AssertColorEqual(new Color(1f, 0.96f, 0.85f, 1f), noon.SunColor);
            AssertColorEqual(new Color(1f, 0.95f, 0.86f, 1f), noon.LightColor);
            AssertColorEqual(new Color(0.16f, 0.19f, 0.30f, 1f), noon.AmbientColor);
            AssertColorEqual(new Color(0.20f, 0.24f, 0.34f, 1f), noon.FillLightColor);
        }

        [Fact]
        public void Night_is_not_pitch_black()
        {
            var midnight = SunCycle.Evaluate(0f, new SunCycleSettings());
            Assert.True(midnight.AmbientColor.R >= 0.05f, $"ambient R too dark: {midnight.AmbientColor.R}");
            Assert.True(midnight.AmbientColor.G >= 0.05f, $"ambient G too dark: {midnight.AmbientColor.G}");
            Assert.True(midnight.AmbientColor.B >= 0.05f, $"ambient B too dark: {midnight.AmbientColor.B}");
            float keyMag = midnight.LightColor.R + midnight.LightColor.G + midnight.LightColor.B;
            Assert.True(keyMag > 0f, "night key light should not be fully black");
        }

        [Fact]
        public void Sun_disc_fades_to_nothing_at_the_horizon()
        {
            // lat 0, dec 0 puts the exact horizon crossing at t = 0.25.
            var s = new SunCycleSettings { LatitudeDegrees = 0f, SolarDeclinationDegrees = 0f };
            Assert.True(SunCycle.Evaluate(0.251f, s).SunColor.R < 0.05f);
            Assert.False(SunCycle.Evaluate(0.249f, s).SunEnabled);
        }

        [Fact]
        public void Key_light_dips_to_zero_across_the_horizon_flip()
        {
            var s = new SunCycleSettings { LatitudeDegrees = 0f, SolarDeclinationDegrees = 0f };
            var atCrossing = SunCycle.Evaluate(0.25f, s);
            Assert.True(MathF.Abs(atCrossing.LightColor.R) < 1e-3f);
            Assert.True(MathF.Abs(atCrossing.LightColor.G) < 1e-3f);
            Assert.True(MathF.Abs(atCrossing.LightColor.B) < 1e-3f);
            var prev = SunCycle.Evaluate(0.245f, s);
            for (float t = 0.2451f; t <= 0.255f; t += 0.0001f)
            {
                var cur = SunCycle.Evaluate(t, s);
                Assert.True(MathF.Abs(cur.LightColor.R - prev.LightColor.R) < 0.05f);
                Assert.True(MathF.Abs(cur.LightColor.G - prev.LightColor.G) < 0.05f);
                Assert.True(MathF.Abs(cur.LightColor.B - prev.LightColor.B) < 0.05f);
                prev = cur;
            }
        }

        [Fact]
        public void Color_output_is_continuous_over_the_full_day()
        {
            var s = new SunCycleSettings();
            var prev = SunCycle.Evaluate(0f, s);
            for (float t = 0.001f; t <= 1f; t += 0.001f)
            {
                var cur = SunCycle.Evaluate(t, s);
                AssertSmooth(prev.HorizonColor, cur.HorizonColor, 0.03f, t);
                AssertSmooth(prev.ZenithColor, cur.ZenithColor, 0.03f, t);
                AssertSmooth(prev.AmbientColor, cur.AmbientColor, 0.03f, t);
                AssertSmooth(prev.FillLightColor, cur.FillLightColor, 0.03f, t);
                AssertSmooth(prev.LightColor, cur.LightColor, 0.25f, t);
                AssertSmooth(prev.SunColor, cur.SunColor, 0.25f, t);
                prev = cur;
            }
        }

        static void AssertSmooth(Color a, Color b, float bound, float t)
        {
            Assert.True(MathF.Abs(a.R - b.R) < bound, $"R jump at t={t}: {a.R}->{b.R}");
            Assert.True(MathF.Abs(a.G - b.G) < bound, $"G jump at t={t}: {a.G}->{b.G}");
            Assert.True(MathF.Abs(a.B - b.B) < bound, $"B jump at t={t}: {a.B}->{b.B}");
        }

        [Fact]
        public void Same_elevation_gives_the_same_palette()
        {
            var s = new SunCycleSettings();
            var morning = SunCycle.Evaluate(0.3f, s);
            var evening = SunCycle.Evaluate(0.7f, s);
            AssertColorEqual(morning.HorizonColor, evening.HorizonColor);
            AssertColorEqual(morning.ZenithColor, evening.ZenithColor);
            AssertColorEqual(morning.SunColor, evening.SunColor);
            AssertColorEqual(morning.LightColor, evening.LightColor);
            AssertColorEqual(morning.AmbientColor, evening.AmbientColor);
            AssertColorEqual(morning.FillLightColor, evening.FillLightColor);
            Assert.True(MathF.Sign(morning.LightDirection.X) != MathF.Sign(evening.LightDirection.X), "morning and evening key light should point to opposite sides");
        }

        [Fact]
        public void Apply_writes_the_lighting_fields_and_nothing_else()
        {
            var post = new PixelPostProcessSettings();
            post.Sky.Enabled = true;
            var anchorBefore = post.Sky.Anchor;
            var radiusBefore = post.Sky.SunRadius;
            var haloBefore = post.Sky.HaloStrength;
            var fillDirBefore = post.FillLightDirection;
            var state = SunCycle.Evaluate(0.5f, new SunCycleSettings());
            SunCycle.Apply(state, post);
            Assert.Equal(state.LightDirection, post.LightDirection);
            Assert.Equal(state.LightColor, post.LightColor);
            Assert.Equal(state.AmbientColor, post.AmbientColor);
            Assert.Equal(state.FillLightColor, post.FillLightColor);
            Assert.Equal(state.HorizonColor, post.Sky.HorizonColor);
            Assert.Equal(state.ZenithColor, post.Sky.ZenithColor);
            Assert.Equal(state.SunColor, post.Sky.SunColor);
            Assert.Equal(state.SunEnabled, post.Sky.SunEnabled);
            Assert.True(post.Sky.Enabled);
            Assert.Equal(anchorBefore, post.Sky.Anchor);
            Assert.Equal(radiusBefore, post.Sky.SunRadius);
            Assert.Equal(haloBefore, post.Sky.HaloStrength);
            Assert.Equal(fillDirBefore, post.FillLightDirection);
        }

        // ---- Night-key modes (NightKeyMode: the real decoupled moon track) ---------------------------------------

        static float KeyMagnitude(SunCycleState st) => st.LightColor.R + st.LightColor.G + st.LightColor.B;

        [Fact]
        public void Default_night_key_mode_is_the_legacy_anti_solar_moon()
        {
            // The out-of-box night track is the historical virtual moon (byte-stable). All the pre-existing tests run
            // on this default; this pins that the enum default did not shift.
            Assert.Equal(NightKeyMode.AntiSolarMoon, new SunCycleSettings().NightKey);

            var midnight = SunCycle.Evaluate(0f, new SunCycleSettings());
            Assert.True(midnight.LightDirection.Y < -0.3f, $"legacy midnight key should point downward, got {midnight.LightDirection}");
            Assert.False(midnight.SunEnabled);
            Assert.Equal(KeyLightSource.None, midnight.ActiveSource);   // no real body owns the disc at legacy night
            Assert.Null(midnight.DiscDirectionOverride);

            var noon = SunCycle.Evaluate(0.5f, new SunCycleSettings());
            Assert.Equal(KeyLightSource.Sun, noon.ActiveSource);
        }

        [Fact]
        public void None_mode_is_keyless_below_the_horizon_and_matches_legacy_above_it()
        {
            var none = new SunCycleSettings { NightKey = NightKeyMode.None };
            var legacy = new SunCycleSettings();   // AntiSolarMoon

            // Below the horizon: the key is black (no cast key at night), the disc is hidden.
            var midnight = SunCycle.Evaluate(0f, none);
            Assert.True(KeyMagnitude(midnight) < 1e-4f, $"None-mode night key should be black, got {midnight.LightColor}");
            Assert.False(midnight.SunEnabled);
            Assert.Equal(KeyLightSource.None, midnight.ActiveSource);

            // Above the horizon: identical to the legacy path (same key + direction + disc).
            foreach (float t in new[] { 0.35f, 0.5f, 0.65f })
            {
                var n = SunCycle.Evaluate(t, none);
                var l = SunCycle.Evaluate(t, legacy);
                Assert.Equal(l.LightDirection, n.LightDirection);
                AssertColorEqual(l.LightColor, n.LightColor);
                AssertColorEqual(l.SunColor, n.SunColor);
                Assert.Equal(l.SunEnabled, n.SunEnabled);
            }
        }

        [Fact]
        public void None_mode_key_direction_never_reverses_while_lit()
        {
            // Default lat/dec: the sun peaks at 70 degrees (no zenith singularity). None mode holds the sun's TRUE
            // direction all day (no anti-solar flip), so whenever the key is lit the horizontal direction never
            // reverses between adjacent samples.
            var s = new SunCycleSettings { NightKey = NightKeyMode.None };
            var prev = SunCycle.Evaluate(0f, s);
            for (float t = 0.001f; t <= 1f; t += 0.001f)
            {
                var cur = SunCycle.Evaluate(t, s);
                if (KeyMagnitude(prev) > 1e-3f && KeyMagnitude(cur) > 1e-3f)
                {
                    var a = new Vector2(prev.LightDirection.X, prev.LightDirection.Z);
                    var b = new Vector2(cur.LightDirection.X, cur.LightDirection.Z);
                    Assert.True(Vector2.Dot(a, b) > 0f, $"None-mode key direction reversed while lit at t={t}: {prev.LightDirection} -> {cur.LightDirection}");
                }
                prev = cur;
            }
        }

        // A clean opposition config: at the equator with matching declinations and a 12h offset the moon is the exact
        // anti-phase of the sun (MoonElevation == -SunElevation), the two share their horizon crossings (t=0.25/0.75),
        // and the 75-degree peak keeps both bodies clear of the zenith azimuth singularity.
        static SunCycleSettings OppositionMoon() => new()
        {
            NightKey = NightKeyMode.Moon,
            LatitudeDegrees = 0f,
            SolarDeclinationDegrees = 15f,
            MoonDeclinationDegrees = 15f,
            MoonHourOffset = 12f,
        };

        [Fact]
        public void Moon_mode_moon_opposes_the_sun_and_owns_the_night()
        {
            var s = OppositionMoon();
            for (float t = 0f; t <= 1f; t += 0.01f)
            {
                var st = SunCycle.Evaluate(t, s);
                Assert.Equal(-st.SunElevationDegrees, st.MoonElevationDegrees, 1e-2);
            }

            // Midnight: sun down, moon up and owning the key + disc, disc pointed at the moon.
            var midnight = SunCycle.Evaluate(0f, s);
            Assert.True(midnight.SunElevationDegrees < 0f && midnight.MoonElevationDegrees > 0f);
            Assert.Equal(KeyLightSource.Moon, midnight.ActiveSource);
            Assert.True(midnight.SunEnabled, "moon disc should be up at midnight");
            Assert.NotNull(midnight.DiscDirectionOverride);

            // Noon: sun up and owning; moon down; no disc override (the disc derives from the key light).
            var noon = SunCycle.Evaluate(0.5f, s);
            Assert.Equal(KeyLightSource.Sun, noon.ActiveSource);
            Assert.True(noon.MoonElevationDegrees < 0f);
            Assert.Null(noon.DiscDirectionOverride);
        }

        [Fact]
        public void Moon_mode_source_switch_happens_through_black_and_direction_never_reverses_while_lit()
        {
            var s = OppositionMoon();

            // At the shared crossing (t=0.25) the sun sets as the moon rises: both keys are dipped to black, so the
            // handover is through black.
            var atCrossing = SunCycle.Evaluate(0.25f, s);
            Assert.True(KeyMagnitude(atCrossing) < 1e-4f, $"sun/moon handover should be through a black key, got {atCrossing.LightColor}");

            // The source flips from moon (just before sunrise) to sun (just after) around the crossing.
            Assert.Equal(KeyLightSource.Moon, SunCycle.Evaluate(0.24f, s).ActiveSource);
            Assert.Equal(KeyLightSource.Sun, SunCycle.Evaluate(0.26f, s).ActiveSource);

            // Full-day invariant: no adjacent pair with BOTH keys visibly lit straddles an azimuth reversal. The
            // direction only ever reverses at a sun<->moon handover, and the key is black there (fine sampling lands
            // any straddling pair deep in the shared dip), so it is skipped.
            var prev = SunCycle.Evaluate(0f, s);
            for (float t = 0.0002f; t <= 1f; t += 0.0002f)
            {
                var cur = SunCycle.Evaluate(t, s);
                if (KeyMagnitude(prev) > 0.05f && KeyMagnitude(cur) > 0.05f)
                {
                    var a = new Vector2(prev.LightDirection.X, prev.LightDirection.Z);
                    var b = new Vector2(cur.LightDirection.X, cur.LightDirection.Z);
                    Assert.True(Vector2.Dot(a, b) > 0f, $"key direction reversed while lit at t={t}: {prev.LightDirection} -> {cur.LightDirection}");
                }
                prev = cur;
            }
        }

        [Fact]
        public void Moon_mode_decorative_moon_shows_a_disc_with_a_black_key()
        {
            // A game can have a moon that casts nothing but still hangs in the sky: black key, bright independent disc.
            var s = OppositionMoon();
            s.MoonKeyColor = new Color(0f, 0f, 0f, 1f);
            s.MoonDiscColor = new Color(0.9f, 0.9f, 1f, 1f);

            var midnight = SunCycle.Evaluate(0f, s);   // moon high, sun down
            Assert.Equal(KeyLightSource.Moon, midnight.ActiveSource);
            Assert.True(KeyMagnitude(midnight) < 1e-4f, $"decorative moon key should be black, got {midnight.LightColor}");

            // The disc slot is visible with the moon's own color, pointed at the moon.
            Assert.True(midnight.SunEnabled);
            Assert.True(midnight.SunColor.R + midnight.SunColor.G + midnight.SunColor.B > 1f, $"decorative moon disc should be visible, got {midnight.SunColor}");
            Assert.NotNull(midnight.DiscDirectionOverride);

            // Apply routes the disc override + color to the sky's single disc slot, and writes a black key light.
            var post = new PixelPostProcessSettings();
            SunCycle.Apply(midnight, post);
            Assert.Equal(midnight.DiscDirectionOverride, post.Sky.SunDirectionOverride);
            Assert.Equal(midnight.SunColor, post.Sky.SunColor);
            Assert.True(post.Sky.SunEnabled);
            Assert.True(post.LightColor.R + post.LightColor.G + post.LightColor.B < 1e-4f);
        }

        [Fact]
        public void Apply_clears_the_sun_direction_override_when_the_sun_owns_the_disc()
        {
            // A stale moon override from a previous frame must be cleared once the sun owns the disc, so the disc
            // derives from the key light again.
            var post = new PixelPostProcessSettings();
            post.Sky.SunDirectionOverride = new Vector3(1f, 2f, 3f);
            var noon = SunCycle.Evaluate(0.5f, new SunCycleSettings { NightKey = NightKeyMode.Moon });
            Assert.Equal(KeyLightSource.Sun, noon.ActiveSource);
            SunCycle.Apply(noon, post);
            Assert.Null(post.Sky.SunDirectionOverride);
        }
    }
}
