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
    /// Pure headless coverage for the water surface math that predates 14.24.0 (Schlick fresnel tint, the legacy
    /// Blinn-Phong sun glint, depth-sampled shore fade, the domain warp and the detail fade) plus the settings /
    /// grid / UBO plumbing. The ripple SLOPE FIELD moved to <see cref="RippleSpectrumTests"/> in 14.25.0 when the
    /// three fixed cosines were replaced by a generated spectrum. No GPU; WaterMath is the single source both this test and the GLSL WaterFrag follow (see the
    /// in-source mirror comment). The 14.24.0 additions live in <see cref="WaterSurfaceMathTests"/> (grid focus
    /// warp, absorption, reflection, GGX glint, foam) and <see cref="GerstnerWaveTests"/> (the swell).
    /// </summary>
    public class WaterMathTests
    {
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
            // Heap, not stackalloc: the grid is 9,409 vertices (113 KB) since the swell made the mesh the wave.
            var verts = new Vector3[WaterMath.GridResolution * WaterMath.GridResolution];
            var axes = new float[2 * WaterMath.GridResolution];
            int n = WaterMath.BuildGridPositions(plane, focusX: 2f, focusZ: -3f, bias: 1f, verts, axes);
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
            var indices = new uint[WaterMath.GridIndexCount];
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
            // The three 14.22.0 additions ship ON, so a consumer that only calls DrawWater gets the fixed surface.
            Assert.True(s.WaveWarpStrength > 0f);
            Assert.True(s.DetailFadeDistance > 0f);
            Assert.True(s.ShallowDepth > 0f);
            Assert.InRange(s.DistantDetailScale, 0f, 1f);
            // The shallow body colour is a lift off the deep one (lighter, more transparent), not an unrelated hue.
            Assert.True(s.ShallowColor.R > s.DeepColor.R && s.ShallowColor.G > s.DeepColor.G && s.ShallowColor.B > s.DeepColor.B);
            Assert.True(s.ShallowColor.A < s.DeepColor.A);
            // The shallows tint reads over metres; the waterline alpha feather over centimetres. Conflating them is
            // the mistake this pair of knobs exists to prevent.
            Assert.True(s.ShallowDepth > s.ShoreFadeDistance);

            // The 14.24.0 additions also ship ON, so DrawWater alone gets the stylized ocean, not the flat sheet.
            Assert.True(s.SwellAmplitude > 0f);
            Assert.True(s.SwellWavelength > 0f);
            Assert.InRange(s.SwellSteepness, 0f, 1f);          // above 1 the surface folds through itself
            Assert.InRange(s.SwellComponents, 1, GerstnerWaves.MaxComponents);
            Assert.True(s.GridFocusBias > 1f);                 // 1 would be the uniform grid
            Assert.InRange(s.SkyReflectionStrength, 0f, 1f);
            Assert.InRange(s.SkyReflectionSunStrength, 0f, 1f);
            Assert.True(s.GlintRoughness > 0f);                // > 0 selects GGX over the legacy Blinn-Phong lobe
            Assert.True(s.GlintDistantRoughness > s.GlintRoughness);   // the far field must widen, never sharpen
            Assert.True(s.FoamStrength > 0f);
            Assert.InRange(s.FoamCrestCoverage, 0f, 1f);
            Assert.True(s.FoamShoreWidth > 0f);
            Assert.True(s.FoamPatternScale > 0f);
            // Absorption is per-channel and red must die fastest, or the gradient runs straight instead of bending
            // through green-teal, which is the entire reason it is not a scalar lerp.
            Assert.True(s.AbsorptionPerMetre.R > s.AbsorptionPerMetre.G);
            Assert.True(s.AbsorptionPerMetre.G > s.AbsorptionPerMetre.B);
            Assert.True(s.AbsorptionPerMetre.B > 0f);
        }

        [Fact]
        public void Settings_legacy_look_switches_are_all_reachable_by_zero()
        {
            // The documented pre-14.22.0 restore: every added behaviour is off at 0 (WaterSettings' own remarks and
            // the CHANGELOG both name these three). The wave FIELD itself is deliberately not reversible - it was
            // the tiling defect.
            var s = new WaterSettings { WaveWarpStrength = 0f, DetailFadeDistance = 0f, ShallowDepth = 0f };
            Assert.Equal(1f, WaterMath.DetailScale(999f, s.DetailFadeDistance, s.DistantDetailScale));
            Assert.Equal(0f, WaterMath.ShallowWeight(0f, s.ShallowDepth));
            Assert.Equal(new Vector2(7f, 8f), WaterMath.DomainWarp(7f, 8f, 3f, s.WaveScale, s.WaveWarpStrength));
        }

        [Fact]
        public void Settings_1422_look_is_reachable_knob_by_knob()
        {
            // The 14.24.0 restore, one knob per feature, INDEPENDENTLY (this is the standing A/B rule: a new look
            // must be comparable against the one it replaced without rebuilding the engine). Each assertion below
            // is the point at which that feature stops contributing anything.
            var s = new WaterSettings
            {
                SwellAmplitude = 0f,        // flat plane again: no displacement, and no fold for whitecaps to read
                GridFocusBias = 1f,         // the uniform surface grid
                SkyReflectionStrength = 0f, // fresnel blends toward the flat HorizonColor again
                GlintRoughness = 0f,        // the Blinn-Phong lobe on GlintExponent
                AbsorptionPerMetre = new Color(0f, 0f, 0f, 0f),   // the two-stop ShallowDepth blend
                FoamStrength = 0f,          // no foam at all
            };

            // Swell off: no components are generated at all, so the vertex stage's whole loop is skipped.
            Span<GerstnerWaves.Component> comps = stackalloc GerstnerWaves.Component[GerstnerWaves.MaxComponents];
            Assert.Equal(0, GerstnerWaves.BuildComponents(s.SwellAmplitude, s.SwellWavelength, 0.5f, 0.9f,
                s.SwellSteepness, s.SwellSpeed, s.SwellSeed, s.SwellComponents, comps));
            var flat = GerstnerWaves.Evaluate(3f, -7f, 1.25f, s.SwellSteepness, ReadOnlySpan<GerstnerWaves.Component>.Empty);
            Assert.Equal(Vector3.Zero, flat.Offset);
            Assert.Equal(Vector3.UnitY, flat.Normal);
            Assert.Equal(0f, flat.Fold);

            // Uniform grid: the focus warp is the identity, bit-for-bit (an early return, not a near-miss).
            for (int i = 0; i <= 8; i++)
            {
                float u = i / 8f;
                Assert.Equal(u, WaterMath.FocusWarp(u, focus: 0.3f, bias: s.GridFocusBias));
            }

            // Reflection off: whatever the sky is doing, the surface uses exactly the flat horizon tint.
            var horizon = new Vector3(0.6f, 0.7f, 0.8f);
            Assert.Equal(horizon, WaterMath.ReflectionColor(horizon, new Vector3(1f, 0f, 0f), s.SkyReflectionStrength));

            // Glint: a non-positive roughness is the documented selector for the legacy lobe. The shader branches on
            // the same comparison, so this pins the selector itself, not just the value.
            Assert.False(s.GlintRoughness > 0f);

            // Absorption off: an all-zero coefficient is the documented fallback to the two-stop blend.
            var absorb = new Vector3(s.AbsorptionPerMetre.R, s.AbsorptionPerMetre.G, s.AbsorptionPerMetre.B);
            Assert.Equal(0f, absorb.X + absorb.Y + absorb.Z);

            // Foam off: zero strength zeroes the combined amount whatever the two sources say.
            Assert.Equal(0f, WaterMath.FoamAmount(whitecap: 1f, shoreFoam: 1f, pattern: 1f, strength: s.FoamStrength));
        }

        // ---- UBO packing -------------------------------------------------------------------------------------------

        [Fact]
        public void PackUbo_carries_colors_light_camera_and_wave_params()
        {
            var settings = new WaterSettings
            {
                DeepColor = new Color(0.05f, 0.2f, 0.3f, 0.9f),
                ShallowColor = new Color(0.15f, 0.35f, 0.4f, 0.8f),
                HorizonColor = new Color(0.6f, 0.7f, 0.8f, 0.7f),
                WaveScale = 3f,
                WaveSpeed = 0.4f,
                NormalStrength = 0.3f,
                ShoreFadeDistance = 0.8f,
                GlintStrength = 0.5f,
                GlintExponent = 120f,
                Opacity = 0.95f,
                WaveWarpStrength = 0.6f,
                DetailFadeDistance = 45f,
                DistantDetailScale = 0.2f,
                ShallowDepth = 3.5f,
                SkyReflectionStrength = 0.8f,
                SkyReflectionSunStrength = 0.3f,
                GlintRoughness = 0.25f,
                GlintDistantRoughness = 0.55f,
                SwellAmplitude = 0.7f,
                SwellWavelength = 33f,
                SwellDirectionDegrees = 90f,
                SwellSpreadDegrees = 45f,
                SwellSteepness = 0.4f,
                SwellSpeed = 0.9f,
                SwellSeed = 2.5f,
                SwellComponents = 5,
                AbsorptionPerMetre = new Color(0.5f, 0.2f, 0.1f, 0f),
                FoamColor = new Color(0.9f, 0.95f, 1f, 0.85f),
                FoamStrength = 0.7f,
                FoamCrestCoverage = 0.6f,
                FoamShoreWidth = 2f,
                FoamPatternScale = 1.5f,
            };
            var sky = new SkySettings
            {
                HorizonColor = new Color(0.7f, 0.75f, 0.85f, 1f),
                ZenithColor = new Color(0.2f, 0.4f, 0.7f, 1f),
                SunColor = new Color(1f, 0.9f, 0.7f, 1f),
                SunEnabled = true,
                SunRadius = 0.07f,
                HaloStrength = 0.4f,
                HaloFalloff = 0.2f,
            };
            var clipVp = Matrix4x4.CreateLookAt(new Vector3(0, 5, 5), Vector3.Zero, Vector3.UnitY);
            var rawVp = clipVp;   // identical here; the test only checks each field lands, not the clip-correction path
            var light = new Vector3(-0.5f, -0.85f, -0.35f);
            var lightColor = new Color(1f, 0.95f, 0.86f, 1f);
            var camPos = new Vector3(1f, 2f, 3f);

            var u = WaterRenderer.PackUbo(clipVp, rawVp, light, lightColor, camPos, settings, sky, timeSeconds: 2.5f);

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
            Assert.Equal(settings.ShallowColor.G, u.ShallowColor.Y, 4);
            Assert.Equal(settings.ShallowColor.A, u.ShallowColor.W, 4);
            Assert.Equal(settings.WaveWarpStrength, u.DetailParams.X, 4);
            Assert.Equal(settings.DetailFadeDistance, u.DetailParams.Y, 4);
            Assert.Equal(settings.DistantDetailScale, u.DetailParams.Z, 4);
            Assert.Equal(settings.ShallowDepth, u.DetailParams.W, 4);

            // Sky palette: taken from SkySettings, not duplicated on WaterSettings, so the water reflects the same
            // sky the background pass paints.
            Assert.Equal(sky.HorizonColor.R, u.SkyHorizon.X, 4);
            Assert.Equal(sky.ZenithColor.B, u.SkyZenith.Z, 4);
            Assert.Equal(sky.SunColor.G, u.SkySunColor.Y, 4);
            Assert.Equal(1f, u.SkyParams.X, 4);
            Assert.Equal(sky.SunRadius, u.SkyParams.Y, 4);
            Assert.Equal(sky.HaloStrength, u.SkyParams.Z, 4);
            Assert.Equal(sky.HaloFalloff, u.SkyParams.W, 4);

            Assert.Equal(settings.SkyReflectionStrength, u.ReflectGlint.X, 4);
            Assert.Equal(settings.SkyReflectionSunStrength, u.ReflectGlint.Y, 4);
            Assert.Equal(settings.GlintRoughness, u.ReflectGlint.Z, 4);
            Assert.Equal(settings.GlintDistantRoughness, u.ReflectGlint.W, 4);

            Assert.Equal(settings.SwellAmplitude, u.SwellParams.X, 4);
            Assert.Equal(settings.SwellWavelength, u.SwellParams.Y, 4);
            // Degrees in the settings, radians in the UBO: the shader only ever sees radians.
            Assert.Equal(MathF.PI / 2f, u.SwellParams.Z, 4);
            Assert.Equal(MathF.PI / 4f, u.SwellParams.W, 4);
            Assert.Equal(settings.SwellSteepness, u.SwellShape.X, 4);
            Assert.Equal(settings.SwellSpeed, u.SwellShape.Y, 4);
            Assert.Equal(5f, u.SwellShape.Z, 4);
            Assert.Equal(settings.SwellSeed, u.SwellShape.W, 4);

            Assert.Equal(settings.AbsorptionPerMetre.R, u.Absorption.X, 4);
            Assert.Equal(settings.AbsorptionPerMetre.B, u.Absorption.Z, 4);
            Assert.Equal(settings.FoamColor.A, u.FoamColor.W, 4);
            Assert.Equal(settings.FoamStrength, u.FoamParams.X, 4);
            Assert.Equal(settings.FoamCrestCoverage, u.FoamParams.Y, 4);
            Assert.Equal(settings.FoamShoreWidth, u.FoamParams.Z, 4);
            Assert.Equal(settings.FoamPatternScale, u.FoamParams.W, 4);
        }

        [Fact]
        public void PackUbo_clamps_the_component_count_into_the_shader_loop_bound()
        {
            // The GLSL loop is bounded by a compile-time 6 with an early break on this value. An out-of-range count
            // reaching the shader would silently drop components (too high) or run none (too low), so the clamp
            // lives here, at the one place the value crosses into the UBO.
            var high = new WaterSettings { SwellComponents = 99 };
            var low = new WaterSettings { SwellComponents = -3 };
            var vp = Matrix4x4.Identity;
            var sky = new SkySettings();
            Assert.Equal(GerstnerWaves.MaxComponents,
                (int)WaterRenderer.PackUbo(vp, vp, -Vector3.UnitY, Color.White, Vector3.Zero, high, sky, 0f).SwellShape.Z);
            Assert.Equal(1,
                (int)WaterRenderer.PackUbo(vp, vp, -Vector3.UnitY, Color.White, Vector3.Zero, low, sky, 0f).SwellShape.Z);
        }
    }
}
