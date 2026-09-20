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
            var rising = SunCycle.Evaluate(0.251f, s);
            Assert.True(rising.SunColor.A < 0.05f, $"disc should be nearly transparent, got alpha {rising.SunColor.A}");
            Assert.False(SunCycle.Evaluate(0.249f, s).SunEnabled);
        }

        [Fact]
        public void Fading_sun_disc_keeps_its_colour_and_fades_in_alpha()
        {
            // The sky replace-blends toward SunColor, so a disc faded by darkening its RGB paints a black hole in the
            // sky (#396). The fade rides in alpha, which the sky uses as the blend weight.
            var s = new SunCycleSettings { LatitudeDegrees = 0f, SolarDeclinationDegrees = 0f };
            var rising = SunCycle.Evaluate(0.251f, s);
            Assert.True(rising.SunColor.R > 0.9f, $"a fading disc must not darken, got {rising.SunColor}");
            Assert.Equal(1f, SunCycle.Evaluate(0.5f, s).SunColor.A, 4);
        }

        // ---- Disc set elevation: a disc that sets THROUGH a world horizon (#396) ------------------------------------

        // lat 0, dec 0: the sun crosses the horizon at t = 0.25 / 0.75 and climbs 360 degrees per day.
        static SunCycleSettings Equatorial(NightKeyMode mode, float discSet) => new()
        {
            LatitudeDegrees = 0f, SolarDeclinationDegrees = 0f, NightKey = mode,
            DiscSetElevationDegrees = discSet, SunDiscFadeElevationDegrees = 4f,
        };

        static float TimeAtEveningElevation(float degrees) => 0.75f - degrees / 360f;

        [Theory]
        [InlineData(NightKeyMode.AntiSolarMoon)]
        [InlineData(NightKeyMode.None)]
        [InlineData(NightKeyMode.Moon)]
        public void A_setting_sun_crosses_the_horizon_at_full_strength_and_still_pointed_at_the_sun(NightKeyMode mode)
        {
            var s = Equatorial(mode, discSet: 6f);
            foreach (float el in new[] { 1f, -0.5f, -1.5f })
            {
                float t = TimeAtEveningElevation(el);
                var st = SunCycle.Evaluate(t, s);
                Assert.True(st.SunEnabled, $"{mode}: disc should hold the slot at {el} degrees");
                Assert.Equal(1f, st.SunColor.A, 3);

                // Whatever the key did at the crossing (flip, moon, black), the disc is still where the sun is.
                var post = new PixelPostProcessSettings();
                SunCycle.Apply(st, post);
                Vector3 toward = post.Sky.ResolveSunDirection(post.LightDirection);
                Vector3 sun = SunCycle.SolarDirection(t, 0f, 0f, s.HeadingDegrees, out _);
                Assert.True(Vector3.Dot(toward, sun) > 0.9999f, $"{mode}: disc drifted off the sun at {el} degrees: {toward} vs {sun}");
            }
        }

        [Theory]
        [InlineData(NightKeyMode.AntiSolarMoon)]
        [InlineData(NightKeyMode.None)]
        public void The_disc_fades_out_below_the_horizon_and_is_gone_at_the_set_elevation(NightKeyMode mode)
        {
            var s = Equatorial(mode, discSet: 6f);
            var fading = SunCycle.Evaluate(TimeAtEveningElevation(-4f), s);   // mid band: -6 .. -2
            Assert.True(fading.SunEnabled);
            Assert.InRange(fading.SunColor.A, 0.3f, 0.7f);
            Assert.False(SunCycle.Evaluate(TimeAtEveningElevation(-6.5f), s).SunEnabled);
        }

        [Fact]
        public void The_key_light_still_dips_at_elevation_zero_whatever_the_disc_does()
        {
            var with = Equatorial(NightKeyMode.None, discSet: 6f);
            var without = Equatorial(NightKeyMode.None, discSet: 0f);
            for (float el = -8f; el <= 8f; el += 0.5f)
            {
                float t = TimeAtEveningElevation(el);
                var a = SunCycle.Evaluate(t, with);
                var b = SunCycle.Evaluate(t, without);
                AssertColorEqual(b.LightColor, a.LightColor);
                Assert.Equal(b.LightDirection, a.LightDirection);
                Assert.Equal(b.ActiveSource, a.ActiveSource);
            }
        }

        [Fact]
        public void Under_a_moon_the_setting_sun_keeps_the_disc_until_it_is_down_then_the_moon_takes_it()
        {
            var s = Equatorial(NightKeyMode.Moon, discSet: 6f);   // moon at opposition: up exactly when the sun is down
            var sinking = SunCycle.Evaluate(TimeAtEveningElevation(-3f), s);
            Assert.Equal(KeyLightSource.Moon, sinking.ActiveSource);
            AssertColorEqual(SunCycle.Evaluate(TimeAtEveningElevation(-3f), Equatorial(NightKeyMode.None, 6f)).SunColor, sinking.SunColor);

            var night = SunCycle.Evaluate(TimeAtEveningElevation(-20f), s);
            Assert.True(night.SunEnabled);
            AssertColorEqual(s.MoonDiscColor, night.SunColor);
            Assert.Equal(night.MoonDirection, -night.DiscDirectionOverride!.Value);
        }

        // ---- Extra discs: more than one body in the sky at once (#1041) -------------------------------------------

        [Fact]
        public void A_moon_rising_as_the_sun_sets_is_an_extra_disc_and_never_pops_in()
        {
            var s = Equatorial(NightKeyMode.Moon, discSet: 6f);   // opposition: the moon rises exactly as the sun sets

            // Sun 3 degrees down and still holding the primary slot: the moon (3 degrees up) rides as the extra.
            var handover = SunCycle.Evaluate(TimeAtEveningElevation(-3f), s);
            Assert.Equal(1, handover.ExtraDiscCount);
            SunCycleDisc moon = handover.GetExtraDisc(0);
            Assert.Equal(-handover.MoonDirection, moon.Direction);
            Assert.Equal(1f, moon.Color.A, 3);

            // The moon's direction and color are continuous across the moment it takes the primary slot.
            float tBefore = TimeAtEveningElevation(-5.99f), tAfter = TimeAtEveningElevation(-6.01f);
            var before = SunCycle.Evaluate(tBefore, s);
            var after = SunCycle.Evaluate(tAfter, s);
            Assert.Equal(1, before.ExtraDiscCount);
            Assert.Equal(0, after.ExtraDiscCount);
            AssertColorEqual(before.GetExtraDisc(0).Color, after.SunColor);
            Assert.True(Vector3.Dot(before.GetExtraDisc(0).Direction, after.DiscDirectionOverride!.Value) > 0.9999f);
        }

        [Theory]
        [InlineData(NightKeyMode.AntiSolarMoon)]
        [InlineData(NightKeyMode.None)]
        public void Modes_without_a_moon_disc_never_emit_an_extra(NightKeyMode mode)
        {
            var s = Equatorial(mode, discSet: 6f);
            for (int i = 0; i <= 200; i++)
                Assert.Equal(0, SunCycle.Evaluate(i / 200f, s).ExtraDiscCount);
        }

        [Fact]
        public void Apply_replaces_the_extra_discs_and_gives_them_the_primary_shape()
        {
            var s = Equatorial(NightKeyMode.Moon, discSet: 6f);
            var post = new PixelPostProcessSettings();
            post.Sky.SunRadius = 0.07f;
            post.Sky.HaloStrength = 0.3f;
            post.Sky.HaloFalloff = 0.11f;
            post.Sky.ExtraDiscs.Add(new SkyDisc { Direction = Vector3.UnitX, Color = new Color(1f, 0f, 0f, 1f), Radius = 0.5f });

            var handover = SunCycle.Evaluate(TimeAtEveningElevation(-3f), s);
            SunCycle.Apply(handover, post);
            SkyDisc moon = Assert.Single(post.Sky.ExtraDiscs);
            Assert.Equal(handover.GetExtraDisc(0).Direction, moon.Direction);
            Assert.Equal(0.07f, moon.Radius);
            Assert.Equal(0.3f, moon.HaloStrength);
            Assert.Equal(0.11f, moon.HaloFalloff);

            SunCycle.Apply(SunCycle.Evaluate(0.5f, s), post);   // noon at opposition: the moon is far below
            Assert.Empty(post.Sky.ExtraDiscs);
        }

        [Fact]
        public void Ground_color_blends_across_the_palettes_and_reaches_the_sky()
        {
            var s = new SunCycleSettings();
            AssertColorEqual(s.DayPalette.GroundColor, SunCycle.Evaluate(0.5f, s).GroundColor);
            AssertColorEqual(s.NightPalette.GroundColor, SunCycle.Evaluate(0f, s).GroundColor);

            var post = new PixelPostProcessSettings();
            var noon = SunCycle.Evaluate(0.5f, s);
            SunCycle.Apply(noon, post);
            Assert.Equal(noon.GroundColor, post.Sky.GroundColor);
        }

        [Fact]
        public void Fading_moon_disc_keeps_its_colour_and_fades_in_alpha()
        {
            var s = new SunCycleSettings { LatitudeDegrees = 0f, SolarDeclinationDegrees = 0f, NightKey = NightKeyMode.Moon };
            bool sawFade = false;
            for (int i = 0; i <= 2000; i++)
            {
                var st = SunCycle.Evaluate(i / 2000f, s);
                if (st.ActiveSource != KeyLightSource.Moon || st.SunColor.A > 0.5f) continue;
                sawFade = true;
                AssertColorEqual(s.MoonDiscColor.WithAlpha(st.SunColor.A), st.SunColor);
            }
            Assert.True(sawFade, "the sweep never caught the moon inside its fade band");
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

        // The first dusk handover in a full-day sweep: the sample where the key stops being the sun and starts
        // being the moon, plus the sample just before it. Fine enough (0.0002 of a day, about 17 seconds of game
        // clock) that a pair straddling the flip is right at the crossing rather than either side of the dip.
        static (SunCycleState Before, SunCycleState After, float T) FirstSunToMoonHandover(SunCycleSettings s)
        {
            var prev = SunCycle.Evaluate(0f, s);
            for (float t = 0.0002f; t <= 1f; t += 0.0002f)
            {
                var cur = SunCycle.Evaluate(t, s);
                if (prev.ActiveSource == KeyLightSource.Sun && cur.ActiveSource == KeyLightSource.Moon)
                {
                    return (prev, cur, t);
                }
                prev = cur;
            }
            Assert.Fail("no sun-to-moon handover in a full day");
            return default;
        }

        [Fact]
        public void Moon_mode_source_changes_are_through_black_for_unequal_days_and_offsets()
        {
            SunCycleSettings[] cases =
            {
                new() { NightKey = NightKeyMode.Moon },
                new() { NightKey = NightKeyMode.Moon, LatitudeDegrees = 55f, SolarDeclinationDegrees = 20f },
                new() { NightKey = NightKeyMode.Moon, LatitudeDegrees = 55f, SolarDeclinationDegrees = -20f },
                new() { NightKey = NightKeyMode.Moon, MoonHourOffset = 8f },
                new() { NightKey = NightKeyMode.Moon, MoonHourOffset = 16f },
            };

            foreach (SunCycleSettings settings in cases)
            {
                AssertSourceChangesDark(settings);
            }
        }

        static void AssertSourceChangesDark(SunCycleSettings settings)
        {
            SunCycleState previous = SunCycle.Evaluate(0f, settings);
            int changes = 0;
            for (float t = 0.0001f; t <= 1f; t += 0.0001f)
            {
                SunCycleState current = SunCycle.Evaluate(t, settings);
                if (current.ActiveSource != previous.ActiveSource)
                {
                    changes++;
                    Assert.True(
                        MathF.Max(KeyMagnitude(previous), KeyMagnitude(current)) < 0.02f,
                        $"source changed {previous.ActiveSource} to {current.ActiveSource} while lit at t={t}: "
                        + $"{KeyMagnitude(previous)} to {KeyMagnitude(current)}");
                }

                previous = current;
            }

            Assert.True(changes >= 2);
        }

        [Fact]
        public void Coincident_moon_and_handover_fades_form_an_envelope_instead_of_squaring()
        {
            SunCycleSettings settings = OppositionMoon();
            SunCycleState state = SunCycle.Evaluate(0.24f, settings);
            Assert.Equal(KeyLightSource.Moon, state.ActiveSource);

            float x = Math.Clamp(MathF.Abs(state.SunElevationDegrees) / settings.HorizonKeyDipDegrees, 0f, 1f);
            float expectedFade = x * x * (3f - 2f * x);
            float full = settings.MoonKeyColor.R + settings.MoonKeyColor.G + settings.MoonKeyColor.B;
            Assert.Equal(expectedFade, KeyMagnitude(state) / full, 4);
        }

        [Fact]
        public void Zero_solar_dip_keeps_only_the_numerical_floor_at_the_exact_crossing()
        {
            SunCycleSettings settings = OppositionMoon();
            settings.HorizonKeyDipDegrees = 0f;
            settings.MoonHorizonKeyDipDegrees = 0f;

            Assert.True(KeyMagnitude(SunCycle.Evaluate(0.25f, settings)) < 1e-4f);
            Assert.True(KeyMagnitude(SunCycle.Evaluate(0.249f, settings)) > 0.1f);
            Assert.True(KeyMagnitude(SunCycle.Evaluate(0.251f, settings)) > 0.1f);
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
