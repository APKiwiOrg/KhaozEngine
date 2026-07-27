using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Headless coverage for the environment presets (sky + lighting bundles applied to
    /// <see cref="PixelPostProcessSettings"/>) and the sun-angle helper backing a map-editor slider pair. Pure
    /// settings mutation, no GPU.
    /// </summary>
    public class EnvironmentPresetsTests
    {
        static void AssertFiniteUnit01(Color c)
        {
            Assert.True(float.IsFinite(c.R) && c.R >= 0f && c.R <= 1f, $"R out of range: {c.R}");
            Assert.True(float.IsFinite(c.G) && c.G >= 0f && c.G <= 1f, $"G out of range: {c.G}");
            Assert.True(float.IsFinite(c.B) && c.B >= 0f && c.B <= 1f, $"B out of range: {c.B}");
            Assert.True(float.IsFinite(c.A) && c.A >= 0f && c.A <= 1f, $"A out of range: {c.A}");
        }

        static void AssertNormalizedDownward(Vector3 v)
        {
            Assert.Equal(1f, v.Length(), 3);
            Assert.True(v.Y < 0f, $"expected a downward-pointing (negative Y) direction, got {v}");
        }

        [Theory]
        [InlineData(EnvironmentPresetKind.Day)]
        [InlineData(EnvironmentPresetKind.Sunset)]
        [InlineData(EnvironmentPresetKind.Night)]
        [InlineData(EnvironmentPresetKind.Starfield)]
        public void Every_preset_produces_finite_normalized_lighting(EnvironmentPresetKind kind)
        {
            var post = new PixelPostProcessSettings();
            EnvironmentPresets.Apply(kind, post);

            AssertFiniteUnit01(post.Sky.HorizonColor);
            AssertFiniteUnit01(post.Sky.ZenithColor);
            AssertFiniteUnit01(post.Sky.SunColor);
            AssertFiniteUnit01(post.BackgroundColor);
            AssertFiniteUnit01(post.LightColor);
            AssertFiniteUnit01(post.AmbientColor);
            AssertFiniteUnit01(post.FillLightColor);

            AssertNormalizedDownward(post.LightDirection);
            AssertNormalizedDownward(post.FillLightDirection);
        }

        [Fact]
        public void Day_enables_sky_and_disables_starfield()
        {
            var post = new PixelPostProcessSettings();
            EnvironmentPresets.Apply(EnvironmentPresetKind.Day, post);
            Assert.True(post.Sky.Enabled);
            Assert.False(post.Starfield);
        }

        [Fact]
        public void Starfield_disables_sky_enables_starfield_and_matches_background_to_avoid_the_water_seam()
        {
            var post = new PixelPostProcessSettings();
            EnvironmentPresets.Apply(EnvironmentPresetKind.Starfield, post);

            Assert.False(post.Sky.Enabled);
            Assert.True(post.Starfield);

            // WaterRenderer.PackUbo reads SkySettings.HorizonColor/ZenithColor UNCONDITIONALLY (the water surface
            // reflects the sky palette whether or not the sky pass itself is enabled), so a starfield background
            // needs the sky palette pulled down to match it or the water reads a bright day-sky horizon against a
            // near-black background: the jagged seam this preset exists to remove.
            const float tolerance = 0.01f;
            Assert.True(MathF.Abs(post.Sky.HorizonColor.R - post.BackgroundColor.R) < tolerance);
            Assert.True(MathF.Abs(post.Sky.HorizonColor.G - post.BackgroundColor.G) < tolerance);
            Assert.True(MathF.Abs(post.Sky.HorizonColor.B - post.BackgroundColor.B) < tolerance);
            Assert.True(MathF.Abs(post.Sky.ZenithColor.R - post.BackgroundColor.R) < tolerance);
            Assert.True(MathF.Abs(post.Sky.ZenithColor.G - post.BackgroundColor.G) < tolerance);
            Assert.True(MathF.Abs(post.Sky.ZenithColor.B - post.BackgroundColor.B) < tolerance);
        }

        [Fact]
        public void Night_is_dark_and_sunset_is_warm_at_the_horizon()
        {
            var night = new PixelPostProcessSettings();
            EnvironmentPresets.Apply(EnvironmentPresetKind.Night, night);
            Assert.True(night.Sky.Enabled);
            // Night reads dark: the zenith barely lifts off black.
            Assert.True(night.Sky.ZenithColor.R < 0.1f && night.Sky.ZenithColor.G < 0.1f && night.Sky.ZenithColor.B < 0.15f);

            var sunset = new PixelPostProcessSettings();
            EnvironmentPresets.Apply(EnvironmentPresetKind.Sunset, sunset);
            // Warm horizon: red channel clearly ahead of blue.
            Assert.True(sunset.Sky.HorizonColor.R > sunset.Sky.HorizonColor.B);
        }

        // ---- Sun angle helper --------------------------------------------------------------------------------

        [Fact]
        public void SunLightDirection_straight_overhead_points_straight_down()
        {
            var dir = EnvironmentPresets.SunLightDirection(azimuthDegrees: 0f, elevationDegrees: 90f);
            Assert.Equal(0f, dir.X, 4);
            Assert.Equal(-1f, dir.Y, 4);
            Assert.Equal(0f, dir.Z, 4);
        }

        [Fact]
        public void SunLightDirection_on_the_horizon_to_the_east_points_west()
        {
            // East is +X (SunCycle.SolarDirection's convention: north = -Z, east = +X). A sun sitting on the
            // eastern horizon casts light travelling west (-X).
            var dir = EnvironmentPresets.SunLightDirection(azimuthDegrees: 90f, elevationDegrees: 0f);
            Assert.Equal(-1f, dir.X, 4);
            Assert.Equal(0f, dir.Y, 4);
            Assert.Equal(0f, dir.Z, 4);
        }

        [Fact]
        public void SunLightDirection_is_always_unit_length()
        {
            foreach (var az in new[] { 0f, 45f, 90f, 180f, 270f, 359f })
            foreach (var el in new[] { -10f, 0f, 15f, 45f, 89f })
            {
                var dir = EnvironmentPresets.SunLightDirection(az, el);
                Assert.Equal(1f, dir.Length(), 4);
            }
        }

        [Fact]
        public void SunLightDirection_round_trips_against_SkySettings_ResolveSunDirection()
        {
            // ResolveSunDirection derives the direction TO the sun from the key light (SkyMath.SunDirectionFromLight):
            // -normalize(lightDirection). Feeding SunLightDirection's output back through it must recover the same
            // TOWARD-the-sun direction the azimuth/elevation pair describes.
            var sky = new SkySettings();
            const float az = 40f, el = 25f;
            var light = EnvironmentPresets.SunLightDirection(az, el);
            var resolved = sky.ResolveSunDirection(light);

            float azRad = az * MathF.PI / 180f, elRad = el * MathF.PI / 180f;
            var expectedToward = new Vector3(
                MathF.Sin(azRad) * MathF.Cos(elRad),
                MathF.Sin(elRad),
                -MathF.Cos(azRad) * MathF.Cos(elRad));

            Assert.Equal(expectedToward.X, resolved.X, 4);
            Assert.Equal(expectedToward.Y, resolved.Y, 4);
            Assert.Equal(expectedToward.Z, resolved.Z, 4);
        }
    }
}
