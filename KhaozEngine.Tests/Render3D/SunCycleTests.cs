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
    }
}
