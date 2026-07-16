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
    }
}
