using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Pure headless coverage for the water surface math (scrolling-normal perturbation, Schlick fresnel tint,
    /// Blinn-Phong sun glint, depth-sampled shore fade) and the settings / grid / UBO plumbing. No GPU; WaterMath is
    /// the single source both this test and the GLSL WaterFrag follow (see the in-source mirror comment).
    /// </summary>
    public class WaterMathTests
    {
        // ---- Wave normal perturbation --------------------------------------------------------------------------

        [Fact]
        public void WaveNormal_zero_strength_is_flat_up()
        {
            var n = WaterMath.WaveNormal(3f, -2f, timeSeconds: 1.5f, waveScale: 2f, waveSpeed: 0.5f, normalStrength: 0f);
            Assert.Equal(Vector3.UnitY, n);
        }

        [Fact]
        public void WaveNormal_is_always_unit_length()
        {
            var rnd = new Random(7);
            for (int i = 0; i < 50; i++)
            {
                float x = (float)(rnd.NextDouble() * 20 - 10);
                float z = (float)(rnd.NextDouble() * 20 - 10);
                float t = (float)(rnd.NextDouble() * 30);
                var n = WaterMath.WaveNormal(x, z, t, waveScale: 2.5f, waveSpeed: 0.35f, normalStrength: 0.35f);
                Assert.Equal(1f, n.Length(), 3);
            }
        }

        [Fact]
        public void WaveNormal_stronger_perturbation_tilts_further_from_up()
        {
            const float x = 1.3f, z = 0.7f, t = 2f, scale = 2f, speed = 0.4f;
            var weak = WaterMath.WaveNormal(x, z, t, scale, speed, normalStrength: 0.1f);
            var strong = WaterMath.WaveNormal(x, z, t, scale, speed, normalStrength: 0.6f);
            float weakTilt = Vector3.Distance(weak, Vector3.UnitY);
            float strongTilt = Vector3.Distance(strong, Vector3.UnitY);
            Assert.True(strongTilt > weakTilt, $"stronger normalStrength should tilt further from up: weak={weakTilt}, strong={strongTilt}");
        }

        [Fact]
        public void WaveNormal_animates_over_time()
        {
            var n0 = WaterMath.WaveNormal(2f, 2f, timeSeconds: 0f, waveScale: 2f, waveSpeed: 1f, normalStrength: 0.4f);
            var n1 = WaterMath.WaveNormal(2f, 2f, timeSeconds: 5f, waveScale: 2f, waveSpeed: 1f, normalStrength: 0.4f);
            Assert.NotEqual(n0, n1);
        }

        [Fact]
        public void WaveNormal_frozen_time_is_deterministic()
        {
            // The golden freezes EffectTimeSeconds; same inputs must reproduce bit-identical output every call.
            var a = WaterMath.WaveNormal(4f, -1f, 0f, 2.5f, 0.35f, 0.35f);
            var b = WaterMath.WaveNormal(4f, -1f, 0f, 2.5f, 0.35f, 0.35f);
            Assert.Equal(a, b);
        }

        [Fact]
        public void WaveNormal_degenerate_scale_does_not_throw_or_nan()
        {
            var n = WaterMath.WaveNormal(1f, 1f, 1f, waveScale: 0f, waveSpeed: 1f, normalStrength: 0.5f);
            Assert.False(float.IsNaN(n.X) || float.IsNaN(n.Y) || float.IsNaN(n.Z));
            Assert.Equal(1f, n.Length(), 3);
        }

        // ---- Fresnel tint ---------------------------------------------------------------------------------------

        [Fact]
        public void Fresnel_zero_at_normal_incidence()
        {
            Assert.Equal(0f, WaterMath.Fresnel(1f), 5);
        }

        [Fact]
        public void Fresnel_one_at_grazing_angle()
        {
            Assert.Equal(1f, WaterMath.Fresnel(0f), 5);
        }

        [Fact]
        public void Fresnel_is_monotonic_increasing_as_angle_grazes()
        {
            float prev = -1f;
            for (float ndotv = 1f; ndotv >= 0f; ndotv -= 0.1f)
            {
                float f = WaterMath.Fresnel(ndotv);
                Assert.True(f >= prev - 1e-5f, $"fresnel should increase toward grazing, {f} !>= {prev} at ndotv={ndotv}");
                prev = f;
            }
        }

        [Fact]
        public void FresnelTint_straight_down_is_deep_color()
        {
            var deep = new Vector3(0.05f, 0.18f, 0.28f);
            var horizon = new Vector3(0.62f, 0.70f, 0.80f);
            var tint = WaterMath.FresnelTint(deep, horizon, ndotv: 1f);
            Assert.Equal(deep, tint);
        }

        [Fact]
        public void FresnelTint_grazing_is_horizon_color()
        {
            var deep = new Vector3(0.05f, 0.18f, 0.28f);
            var horizon = new Vector3(0.62f, 0.70f, 0.80f);
            var tint = WaterMath.FresnelTint(deep, horizon, ndotv: 0f);
            Assert.Equal(horizon, tint);
        }

        // ---- Sun glint -------------------------------------------------------------------------------------------

        [Fact]
        public void SunGlint_zero_strength_disables_glint()
        {
            float g = WaterMath.SunGlint(Vector3.UnitY, Vector3.UnitY, Vector3.UnitY, strength: 0f, exponent: 100f);
            Assert.Equal(0f, g);
        }

        [Fact]
        public void SunGlint_peaks_when_view_and_light_mirror_the_normal()
        {
            // Straight-up normal, straight-up view, straight-up light => half-vector == normal => ndotH == 1 => peak.
            float peak = WaterMath.SunGlint(Vector3.UnitY, Vector3.UnitY, Vector3.UnitY, strength: 0.6f, exponent: 140f);
            Assert.Equal(0.6f, peak, 4);
        }

        [Fact]
        public void SunGlint_falls_off_away_from_the_reflection_peak()
        {
            Vector3 n = Vector3.UnitY;
            Vector3 view = Vector3.UnitY;
            var atPeak = WaterMath.SunGlint(n, view, Vector3.UnitY, 0.6f, 140f);
            var offPeak = WaterMath.SunGlint(n, view, Vector3.Normalize(new Vector3(0.5f, 1f, 0f)), 0.6f, 140f);
            Assert.True(offPeak < atPeak, $"glint should fall off away from the reflection direction: {offPeak} !< {atPeak}");
        }

        [Fact]
        public void SunGlint_tighter_exponent_narrows_the_highlight()
        {
            Vector3 n = Vector3.UnitY;
            Vector3 view = Vector3.UnitY;
            Vector3 offAxisLight = Vector3.Normalize(new Vector3(0.3f, 1f, 0f));
            var loose = WaterMath.SunGlint(n, view, offAxisLight, 0.6f, exponent: 8f);
            var tight = WaterMath.SunGlint(n, view, offAxisLight, 0.6f, exponent: 200f);
            Assert.True(tight < loose, $"a higher exponent should narrow (dim off-axis) the highlight: {tight} !< {loose}");
        }

        [Fact]
        public void SunGlint_never_negative_or_nan()
        {
            var g = WaterMath.SunGlint(Vector3.UnitY, -Vector3.UnitY, Vector3.UnitY, 0.6f, 140f);   // opposite view/light
            Assert.False(float.IsNaN(g));
            Assert.True(g >= 0f);
        }

        // ---- Shore fade ------------------------------------------------------------------------------------------

        [Fact]
        public void ShoreFade_deep_water_is_fully_opaque()
        {
            Assert.Equal(1f, WaterMath.ShoreFade(depthBelowSurface: 10f, fadeDistance: 0.6f), 4);
        }

        [Fact]
        public void ShoreFade_at_the_waterline_is_zero()
        {
            Assert.Equal(0f, WaterMath.ShoreFade(depthBelowSurface: 0f, fadeDistance: 0.6f), 4);
        }

        [Fact]
        public void ShoreFade_negative_depth_clamps_to_zero()
        {
            // Ground ABOVE the surface: shouldn't happen (the water pass's own depth test would reject the pixel
            // first), but the pure function must not go negative/NaN if called directly with an out-of-range input.
            Assert.Equal(0f, WaterMath.ShoreFade(depthBelowSurface: -5f, fadeDistance: 0.6f), 4);
        }

        [Fact]
        public void ShoreFade_is_monotonic_increasing_with_depth()
        {
            float prev = -1f;
            for (float d = 0f; d <= 1f; d += 0.1f)
            {
                float fade = WaterMath.ShoreFade(d, fadeDistance: 0.6f);
                Assert.True(fade >= prev - 1e-5f, $"shore fade should increase with depth, {fade} !>= {prev} at d={d}");
                prev = fade;
            }
        }

        [Fact]
        public void ShoreFade_zero_distance_disables_fade()
        {
            Assert.Equal(1f, WaterMath.ShoreFade(depthBelowSurface: 0f, fadeDistance: 0f));
            Assert.Equal(1f, WaterMath.ShoreFade(depthBelowSurface: -1f, fadeDistance: 0f));
        }

        [Fact]
        public void ShoreFade_negative_distance_disables_fade()
        {
            Assert.Equal(1f, WaterMath.ShoreFade(depthBelowSurface: 0f, fadeDistance: -1f));
        }

        // ---- Grid tessellation -----------------------------------------------------------------------------------

        [Fact]
        public void BuildGridPositions_covers_the_requested_extent()
        {
            var plane = new WaterPlane(centerX: 2f, surfaceY: 1.5f, centerZ: -3f, halfExtentX: 4f, halfExtentZ: 2f);
            Span<Vector3> verts = stackalloc Vector3[WaterMath.GridResolution * WaterMath.GridResolution];
            int n = WaterMath.BuildGridPositions(plane, verts);
            Assert.Equal(WaterMath.GridResolution * WaterMath.GridResolution, n);

            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                Assert.Equal(1.5f, verts[i].Y, 5);   // every vertex sits exactly at the surface height
                minX = MathF.Min(minX, verts[i].X); maxX = MathF.Max(maxX, verts[i].X);
                minZ = MathF.Min(minZ, verts[i].Z); maxZ = MathF.Max(maxZ, verts[i].Z);
            }
            Assert.Equal(2f - 4f, minX, 4); Assert.Equal(2f + 4f, maxX, 4);
            Assert.Equal(-3f - 2f, minZ, 4); Assert.Equal(-3f + 2f, maxZ, 4);
        }

        [Fact]
        public void BuildGridIndices_produces_the_documented_count_and_valid_range()
        {
            Span<uint> indices = stackalloc uint[WaterMath.GridIndexCount];
            int n = WaterMath.BuildGridIndices(indices);
            Assert.Equal(WaterMath.GridIndexCount, n);
            const uint maxVertex = WaterMath.GridResolution * WaterMath.GridResolution - 1;
            foreach (var idx in indices) Assert.True(idx <= maxVertex, $"index {idx} out of the {maxVertex + 1}-vertex grid");
            // (n-1)^2 quads * 2 triangles * 3 indices.
            Assert.Equal((WaterMath.GridResolution - 1) * (WaterMath.GridResolution - 1) * 6, n);
        }

        [Fact]
        public void WaterPlane_square_footprint_defaults_HalfExtentZ_to_HalfExtentX()
        {
            var square = new WaterPlane(0f, 0f, 0f, halfExtentX: 5f);
            Assert.Equal(5f, square.HalfExtentZ);
            var rect = new WaterPlane(0f, 0f, 0f, halfExtentX: 5f, halfExtentZ: 3f);
            Assert.Equal(3f, rect.HalfExtentZ);
        }

        // ---- Settings defaults / opt-in ---------------------------------------------------------------------------

        [Fact]
        public void Settings_defaults_are_sensible_and_scene_default_is_no_request()
        {
            var s = new WaterSettings();
            Assert.True(s.Opacity > 0f);
            Assert.True(s.WaveScale > 0f);
            Assert.True(s.GlintStrength > 0f);
            Assert.True(s.ShoreFadeDistance > 0f);
        }

        // ---- UBO packing -------------------------------------------------------------------------------------------

        [Fact]
        public void PackUbo_carries_colors_light_camera_and_wave_params()
        {
            var settings = new WaterSettings
            {
                DeepColor = new Color(0.05f, 0.2f, 0.3f, 0.9f),
                HorizonColor = new Color(0.6f, 0.7f, 0.8f, 0.7f),
                WaveScale = 3f,
                WaveSpeed = 0.4f,
                NormalStrength = 0.3f,
                ShoreFadeDistance = 0.8f,
                GlintStrength = 0.5f,
                GlintExponent = 120f,
                Opacity = 0.95f,
            };
            var clipVp = Matrix4x4.CreateLookAt(new Vector3(0, 5, 5), Vector3.Zero, Vector3.UnitY);
            var rawVp = clipVp;   // identical here; the test only checks each field lands, not the clip-correction path
            var light = new Vector3(-0.5f, -0.85f, -0.35f);
            var lightColor = new Color(1f, 0.95f, 0.86f, 1f);
            var camPos = new Vector3(1f, 2f, 3f);

            var u = WaterRenderer.PackUbo(clipVp, rawVp, light, lightColor, camPos, settings, timeSeconds: 2.5f, renderWidth: 480, renderHeight: 320);

            Assert.Equal(clipVp, u.ViewProj);
            Matrix4x4.Invert(rawVp, out var expectedInv);
            Assert.Equal(expectedInv, u.InvViewProj);
            Assert.Equal(light, new Vector3(u.LightDir.X, u.LightDir.Y, u.LightDir.Z));
            Assert.Equal(lightColor.R, u.LightColor.X, 4);
            Assert.Equal(camPos, new Vector3(u.CameraPos.X, u.CameraPos.Y, u.CameraPos.Z));
            Assert.Equal(settings.DeepColor.R, u.DeepColor.X, 4);
            Assert.Equal(settings.DeepColor.A, u.DeepColor.W, 4);
            Assert.Equal(settings.HorizonColor.B, u.HorizonColor.Z, 4);
            Assert.Equal(settings.WaveScale, u.WaveParams.X, 4);
            Assert.Equal(settings.WaveSpeed, u.WaveParams.Y, 4);
            Assert.Equal(settings.NormalStrength, u.WaveParams.Z, 4);
            Assert.Equal(2.5f, u.WaveParams.W, 4);
            Assert.Equal(settings.ShoreFadeDistance, u.ShoreGlint.X, 4);
            Assert.Equal(settings.GlintStrength, u.ShoreGlint.Y, 4);
            Assert.Equal(settings.GlintExponent, u.ShoreGlint.Z, 4);
            Assert.Equal(settings.Opacity, u.ShoreGlint.W, 4);
            Assert.Equal(1f / 480f, u.Res.X, 6);
            Assert.Equal(1f / 320f, u.Res.Y, 6);
        }
    }
}
