using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Pure headless coverage for the 14.23.0 water surface additions: the camera-focused grid warp, per-channel
    /// depth absorption, the reflected-sky blend, the GGX glint and its roughness widening, and the two foam
    /// sources. No GPU; <see cref="WaterMath"/> and <see cref="SkyMath.ShadeDirection"/> are the single source
    /// both this test and the GLSL <c>WaterFrag</c> follow.
    /// </summary>
    public class WaterSurfaceMathTests
    {
        // ---- Surface grid focus warp ---------------------------------------------------------------------------

        [Fact]
        public void FocusWarp_is_the_identity_at_or_below_bias_one()
        {
            // Bit-for-bit, not approximately: GridFocusBias 1 is the documented restore of the uniform 14.22.0
            // grid, so the early return must be exact rather than a pow(x, 1) round trip.
            for (int i = 0; i <= 16; i++)
            {
                float u = i / 16f;
                Assert.Equal(u, WaterMath.FocusWarp(u, 0.25f, 1f));
                Assert.Equal(u, WaterMath.FocusWarp(u, 0.25f, 0.4f));
            }
        }

        [Fact]
        public void FocusWarp_pins_the_ends_and_the_focus_and_stays_monotone()
        {
            foreach (float focus in new[] { 0f, 0.15f, 0.5f, 0.87f, 1f })
            {
                Assert.Equal(0f, WaterMath.FocusWarp(0f, focus, 2f), 5);
                Assert.Equal(1f, WaterMath.FocusWarp(1f, focus, 2f), 5);
                Assert.Equal(focus, WaterMath.FocusWarp(focus, focus, 2f), 5);

                float previous = -1f;
                for (int i = 0; i <= 64; i++)
                {
                    float warped = WaterMath.FocusWarp(i / 64f, focus, 2f);
                    Assert.InRange(warped, 0f, 1f);
                    Assert.True(warped >= previous, $"focus {focus} went non-monotone at u={i / 64f}");
                    previous = warped;
                }
            }
        }

        [Fact]
        public void FocusWarp_concentrates_samples_near_the_focus()
        {
            // The claim the knob makes: at a higher bias the cell straddling the focus is smaller and the cell at
            // the far end is larger. That is the whole trade - near-field resolution paid for out of the far field.
            const int n = 97;
            float NearCell(float bias) => MathF.Abs(WaterMath.FocusWarp(0.5f + 1f / (n - 1), 0.5f, bias) - 0.5f);
            float FarCell(float bias) => 1f - WaterMath.FocusWarp(1f - 1f / (n - 1), 0.5f, bias);

            Assert.True(NearCell(1.8f) < NearCell(1f), "a biased grid must sample more finely at the focus");
            Assert.True(FarCell(1.8f) > FarCell(1f), "a biased grid must stretch its far cells to pay for that");
        }

        [Fact]
        public void BuildGridPositions_clamps_an_outside_focus_and_still_covers_the_plane()
        {
            // A camera off the edge of the water (looking back at the coast from inland) must not push the grid's
            // dense region outside the plane, and must not shrink the plane's coverage either.
            var plane = new WaterPlane(centerX: 0f, surfaceY: 2f, centerZ: 0f, halfExtentX: 50f);
            var verts = new Vector3[WaterMath.GridResolution * WaterMath.GridResolution];
            var axes = new float[2 * WaterMath.GridResolution];
            int n = WaterMath.BuildGridPositions(plane, focusX: 900f, focusZ: -900f, bias: 2.2f, verts, axes);

            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                Assert.Equal(2f, verts[i].Y, 5);
                minX = MathF.Min(minX, verts[i].X); maxX = MathF.Max(maxX, verts[i].X);
                minZ = MathF.Min(minZ, verts[i].Z); maxZ = MathF.Max(maxZ, verts[i].Z);
            }
            Assert.Equal(-50f, minX, 3); Assert.Equal(50f, maxX, 3);
            Assert.Equal(-50f, minZ, 3); Assert.Equal(50f, maxZ, 3);
        }

        [Fact]
        public void BuildGridPositions_at_bias_one_is_the_uniform_grid()
        {
            var plane = new WaterPlane(centerX: 4f, surfaceY: 0f, centerZ: -1f, halfExtentX: 8f, halfExtentZ: 6f);
            var verts = new Vector3[WaterMath.GridResolution * WaterMath.GridResolution];
            var axes = new float[2 * WaterMath.GridResolution];
            WaterMath.BuildGridPositions(plane, focusX: 7f, focusZ: 3f, bias: 1f, verts, axes);

            const int n = WaterMath.GridResolution;
            float step = 16f / (n - 1);
            for (int x = 0; x < n; x++)
                Assert.Equal(-4f + x * step, verts[x].X, 3);
        }

        // ---- Depth grading -------------------------------------------------------------------------------------

        [Fact]
        public void Transmittance_is_one_at_the_waterline_and_falls_per_channel()
        {
            var absorb = new Vector3(0.55f, 0.24f, 0.14f);
            Assert.Equal(Vector3.One, WaterMath.Transmittance(absorb, 0f));
            Assert.Equal(Vector3.One, WaterMath.Transmittance(absorb, -3f));   // clamped: a negative depth is noise

            var t = WaterMath.Transmittance(absorb, 4f);
            Assert.True(t.X < t.Y && t.Y < t.Z, "red must be absorbed fastest and blue slowest");
            Assert.InRange(t.X, 0f, 1f);
            for (float d = 0.5f; d < 40f; d += 0.5f)
            {
                var deeper = WaterMath.Transmittance(absorb, d + 0.5f);
                var shallower = WaterMath.Transmittance(absorb, d);
                Assert.True(deeper.X <= shallower.X && deeper.Y <= shallower.Y && deeper.Z <= shallower.Z);
            }
            Assert.True(WaterMath.Transmittance(absorb, 40f).Z < 0.01f, "even blue must be gone in deep water");
        }

        [Fact]
        public void AbsorbTint_walks_from_shallow_to_deep_and_bends_rather_than_running_straight()
        {
            var deep = new Vector3(0.05f, 0.18f, 0.28f);
            var shallow = new Vector3(0.24f, 0.62f, 0.62f);
            var absorb = new Vector3(0.55f, 0.24f, 0.14f);

            Assert.Equal(shallow, WaterMath.AbsorbTint(deep, shallow, absorb, 0f));
            var far = WaterMath.AbsorbTint(deep, shallow, absorb, 120f);
            Assert.Equal(deep.X, far.X, 4); Assert.Equal(deep.Y, far.Y, 4); Assert.Equal(deep.Z, far.Z, 4);

            // The muddy-midtone failure this replaced: a scalar lerp puts every midpoint exactly on the straight
            // line between the two colours. A per-channel exponential must leave that line, and the direction it
            // leaves in is what reads as turquoise -> teal -> blue instead of a wash.
            var mid = WaterMath.AbsorbTint(deep, shallow, absorb, 2.5f);
            float tOnLine = (mid.X - deep.X) / (shallow.X - deep.X);
            var straight = Vector3.Lerp(deep, shallow, tOnLine);
            Assert.True((mid - straight).Length() > 0.05f,
                $"absorption tracked the straight lerp too closely ({(mid - straight).Length()}): the per-channel curve is not doing anything");
            Assert.True(mid.Z > straight.Z, "blue should survive past the straight line, which is what keeps it clean");
        }

        [Fact]
        public void AbsorbWeight_grades_alpha_on_the_same_curve()
        {
            var absorb = new Vector3(0.55f, 0.24f, 0.14f);
            Assert.Equal(1f, WaterMath.AbsorbWeight(absorb, 0f), 5);
            Assert.True(WaterMath.AbsorbWeight(absorb, 3f) < WaterMath.AbsorbWeight(absorb, 1f));
            Assert.InRange(WaterMath.AbsorbWeight(absorb, 50f), 0f, 0.01f);
        }

        // ---- Reflection ----------------------------------------------------------------------------------------

        [Fact]
        public void ReflectionColor_blends_between_the_flat_tint_and_the_reflected_sky()
        {
            var flat = new Vector3(0.62f, 0.70f, 0.80f);
            var sky = new Vector3(0.20f, 0.42f, 0.72f);
            Assert.Equal(flat, WaterMath.ReflectionColor(flat, sky, 0f));
            Assert.Equal(flat, WaterMath.ReflectionColor(flat, sky, -1f));   // clamped
            Assert.Equal(sky, WaterMath.ReflectionColor(flat, sky, 1f));
            Assert.Equal(sky, WaterMath.ReflectionColor(flat, sky, 4f));     // clamped
            var half = WaterMath.ReflectionColor(flat, sky, 0.5f);
            Assert.Equal((flat.X + sky.X) * 0.5f, half.X, 5);
        }

        [Fact]
        public void ShadeDirection_gives_the_horizon_at_grazing_and_the_zenith_overhead()
        {
            var horizon = new Vector3(0.62f, 0.70f, 0.80f);
            var zenith = new Vector3(0.20f, 0.42f, 0.72f);
            var sun = new Vector3(1f, 0.96f, 0.85f);
            var sunDir = Vector3.Normalize(new Vector3(0.45f, 0.75f, 0.4f));

            var level = SkyMath.ShadeDirection(new Vector3(1f, 0f, 0f), sunDir, horizon, zenith, sun,
                sunEnabled: false, 0.05f, 0.5f, 0.18f, sunStrength: 1f);
            Assert.Equal(horizon, level);

            var up = SkyMath.ShadeDirection(Vector3.UnitY, sunDir, horizon, zenith, sun,
                sunEnabled: false, 0.05f, 0.5f, 0.18f, sunStrength: 1f);
            Assert.Equal(zenith, up);

            // Below the horizon clamps to the horizon colour rather than extrapolating: a reflected ray CAN dip
            // under the horizon off a steep crest, and there is no sky down there to sample.
            var down = SkyMath.ShadeDirection(new Vector3(0f, -0.8f, 0.6f), sunDir, horizon, zenith, sun,
                sunEnabled: false, 0.05f, 0.5f, 0.18f, sunStrength: 1f);
            Assert.Equal(horizon, down);
        }

        [Fact]
        public void ShadeDirection_paints_the_sun_where_the_ray_points_at_it_and_scales_by_strength()
        {
            var horizon = new Vector3(0.6f, 0.7f, 0.8f);
            var zenith = new Vector3(0.2f, 0.4f, 0.7f);
            var sunColor = new Vector3(1f, 0.96f, 0.85f);
            var sunDir = Vector3.Normalize(new Vector3(0.3f, 0.7f, 0.2f));

            var onSun = SkyMath.ShadeDirection(sunDir, sunDir, horizon, zenith, sunColor, true, 0.06f, 0.5f, 0.2f, 1f);
            Assert.Equal(sunColor.X, onSun.X, 4);   // dead centre of the disc is the sun colour

            var half = SkyMath.ShadeDirection(sunDir, sunDir, horizon, zenith, sunColor, true, 0.06f, 0.5f, 0.2f, 0.5f);
            Assert.True(half.X < onSun.X && half.X > zenith.X,
                "a partial sun strength must land between the gradient and the full disc");

            var none = SkyMath.ShadeDirection(sunDir, sunDir, horizon, zenith, sunColor, true, 0.06f, 0.5f, 0.2f, 0f);
            var gradientOnly = SkyMath.ShadeDirection(sunDir, sunDir, horizon, zenith, sunColor, false, 0.06f, 0.5f, 0.2f, 1f);
            Assert.Equal(gradientOnly, none);

            // Away from the sun the halo has decayed and the gradient is back.
            var away = SkyMath.ShadeDirection(Vector3.Normalize(new Vector3(-0.3f, 0.7f, -0.2f)), sunDir,
                horizon, zenith, sunColor, true, 0.06f, 0.5f, 0.2f, 1f);
            Assert.True(away.X < 0.5f * (onSun.X + zenith.X));
        }

        // ---- Glint ---------------------------------------------------------------------------------------------

        [Fact]
        public void GgxGlint_peaks_on_the_mirror_direction_and_is_peak_normalized()
        {
            // Peak normalization is what keeps GlintStrength meaning the same brightness as the legacy Blinn-Phong
            // lobe. An un-normalized GGX at these roughnesses peaks in the thousands, which in an HDR scene is a
            // bloom the size of the screen.
            var n = Vector3.UnitY;
            var v = Vector3.Normalize(new Vector3(0f, 1f, 1f));
            var l = Vector3.Normalize(new Vector3(0f, 1f, -1f));   // half vector is exactly +Y
            float peak = WaterMath.GgxGlint(n, v, l, roughness: 0.25f, strength: 1f);
            Assert.InRange(peak, 0.5f, 1.0f);

            // Off the mirror direction it must fall away.
            var lOff = Vector3.Normalize(new Vector3(0.6f, 1f, -1f));
            Assert.True(WaterMath.GgxGlint(n, v, lOff, 0.25f, 1f) < peak);
        }

        [Fact]
        public void GgxGlint_is_off_at_zero_strength_and_scales_linearly_with_it()
        {
            var n = Vector3.UnitY;
            var v = Vector3.Normalize(new Vector3(0f, 1f, 1f));
            var l = Vector3.Normalize(new Vector3(0f, 1f, -1f));
            Assert.Equal(0f, WaterMath.GgxGlint(n, v, l, 0.25f, 0f));
            Assert.Equal(0f, WaterMath.GgxGlint(n, v, l, 0.25f, -1f));
            float one = WaterMath.GgxGlint(n, v, l, 0.25f, 1f);
            Assert.Equal(one * 0.4f, WaterMath.GgxGlint(n, v, l, 0.25f, 0.4f), 5);
        }

        [Fact]
        public void GgxGlint_widens_with_roughness()
        {
            // The property the whole anti-aliasing scheme rests on: at a given angular offset from the mirror
            // direction, a rougher lobe is BRIGHTER (it has spread out), while the peak stays 1 either way. That is
            // what turns sub-pixel normal detail into a soft sheen instead of a crawling sparkle.
            var n = Vector3.UnitY;
            var v = Vector3.Normalize(new Vector3(0f, 1f, 1f));
            var lOff = Vector3.Normalize(new Vector3(0.25f, 1f, -1f));
            float tight = WaterMath.GgxGlint(n, v, lOff, 0.12f, 1f);
            float wide = WaterMath.GgxGlint(n, v, lOff, 0.5f, 1f);
            Assert.True(wide > tight, $"roughness 0.5 ({wide}) did not spread wider than 0.12 ({tight})");
        }

        [Fact]
        public void GgxGlint_is_dark_where_the_sun_is_below_the_surface()
        {
            var n = Vector3.UnitY;
            var v = Vector3.Normalize(new Vector3(0f, 1f, 1f));
            var below = Vector3.Normalize(new Vector3(0f, -1f, 0f));
            Assert.Equal(0f, WaterMath.GgxGlint(n, v, below, 0.25f, 1f), 6);
        }

        [Fact]
        public void GlintRoughnessAt_widens_by_distance_and_by_footprint_whichever_is_worse()
        {
            const float near = 0.22f, far = 0.5f;

            // Fully sampled: near the camera with a tiny footprint, nothing widens.
            Assert.Equal(near, WaterMath.GlintRoughnessAt(near, far, 0f, 60f, 0.001f, 15.7f), 4);
            // Far away: the distance measure saturates.
            Assert.Equal(far, WaterMath.GlintRoughnessAt(near, far, 200f, 60f, 0.001f, 15.7f), 4);
            // Close but under-sampled (a wide FOV, a low resolution, or the ortho camera, where distance is a lie):
            // the footprint measure catches what distance misses. This is the case a distance-only fade gets wrong.
            Assert.Equal(far, WaterMath.GlintRoughnessAt(near, far, 1f, 60f, 40f, 15.7f), 4);
            // Distance fade disabled still leaves the footprint measure live.
            Assert.Equal(near, WaterMath.GlintRoughnessAt(near, far, 500f, 0f, 0.001f, 15.7f), 4);
            Assert.Equal(far, WaterMath.GlintRoughnessAt(near, far, 500f, 0f, 40f, 15.7f), 4);
            // An inverted pair cannot SHARPEN the far field.
            Assert.Equal(0.4f, WaterMath.GlintRoughnessAt(0.4f, 0.1f, 500f, 60f, 40f, 15.7f), 4);
        }

        // ---- Foam ----------------------------------------------------------------------------------------------

        [Fact]
        public void Whitecap_needs_a_folded_crest_and_never_fires_in_a_trough()
        {
            Assert.Equal(0f, WaterMath.Whitecap(fold: 0f, coverage: 0.55f));
            Assert.Equal(0f, WaterMath.Whitecap(fold: 0f, coverage: 1f));       // troughs stay clean at any coverage
            Assert.Equal(0f, WaterMath.Whitecap(fold: 1f, coverage: 0f));       // coverage 0 is off
            Assert.Equal(1f, WaterMath.Whitecap(fold: 1f, coverage: 1f));
            Assert.True(WaterMath.Whitecap(0.6f, 0.7f) > WaterMath.Whitecap(0.6f, 0.4f),
                "raising coverage must foam more of the same field");
        }

        [Fact]
        public void ShoreFoam_bands_the_waterline_and_zero_width_disables_it()
        {
            Assert.Equal(1f, WaterMath.ShoreFoam(0f, 1.6f), 5);
            Assert.Equal(0f, WaterMath.ShoreFoam(1.6f, 1.6f), 5);
            Assert.Equal(0f, WaterMath.ShoreFoam(40f, 1.6f), 5);
            Assert.True(WaterMath.ShoreFoam(0.4f, 1.6f) > WaterMath.ShoreFoam(1.2f, 1.6f));
            Assert.Equal(0f, WaterMath.ShoreFoam(0f, 0f));
            Assert.Equal(0f, WaterMath.ShoreFoam(0f, -2f));
        }

        [Fact]
        public void FoamPattern_stays_in_range_and_does_not_tile_on_the_world_axes()
        {
            // The failure mode this construction exists to avoid is the one 14.22.0 fixed for the ripple field: a
            // product of axis-aligned sines paints a visible grid of foam blobs. Sampling one world axis a long way
            // out must not come back to the same value.
            float first = WaterMath.FoamPattern(0f, 0f, 0f, 2.2f);
            bool differs = false;
            for (int i = 1; i <= 400; i++)
            {
                float v = WaterMath.FoamPattern(i * 2.2f * MathF.Tau, 0f, 0f, 2.2f);
                Assert.InRange(v, 0f, 1f);
                if (MathF.Abs(v - first) > 0.15f) differs = true;
            }
            Assert.True(differs, "the foam pattern repeated exactly along +X: it has a finite tile");
        }

        [Fact]
        public void FoamPattern_covers_a_useful_fraction_of_the_surface()
        {
            // Not a wash and not a sprinkle: the mask has to break foam into shapes, so it must be somewhere near
            // half on. Both ends are real failures - all-on paints solid rings at the shore, all-off means the foam
            // knobs do nothing.
            int on = 0, total = 0;
            for (int i = 0; i < 200; i++)
                for (int j = 0; j < 200; j++)
                {
                    if (WaterMath.FoamPattern(i * 0.37f, j * 0.41f, 0f, 2.2f) > 0.5f) on++;
                    total++;
                }
            Assert.InRange((float)on / total, 0.25f, 0.75f);
        }

        [Fact]
        public void FoamAmount_combines_the_two_sources_without_adding_past_white()
        {
            Assert.Equal(1f, WaterMath.FoamAmount(1f, 1f, 1f, 1f), 5);
            Assert.Equal(1f, WaterMath.FoamAmount(1f, 0f, 1f, 1f), 5);
            Assert.Equal(0f, WaterMath.FoamAmount(1f, 1f, 0f, 1f), 5);   // masked out
            Assert.Equal(0f, WaterMath.FoamAmount(1f, 1f, 1f, 0f), 5);   // switched off
            Assert.Equal(0.5f, WaterMath.FoamAmount(0.5f, 0.2f, 1f, 1f), 5);   // max, not sum
        }

        [Fact]
        public void Default_settings_put_whitecaps_on_some_of_the_sea_but_not_most_of_it()
        {
            // The tuning guard behind the "foam present but not everywhere" default. This is the assertion that
            // catches a coverage default (or a fold normalization) that silently produces a foam-free ocean, which
            // the golden's weak anti-degeneracy check would sail straight past.
            var s = new WaterSettings();
            Span<GerstnerWaves.Component> scratch = stackalloc GerstnerWaves.Component[GerstnerWaves.MaxComponents];
            int n = GerstnerWaves.BuildComponents(s.SwellAmplitude, s.SwellWavelength,
                GerstnerWaves.DegreesToRadians(s.SwellDirectionDegrees),
                GerstnerWaves.DegreesToRadians(s.SwellSpreadDegrees),
                s.SwellSteepness, s.SwellSpeed, s.SwellSeed, s.SwellComponents, scratch);
            var comps = (ReadOnlySpan<GerstnerWaves.Component>)scratch.Slice(0, n);

            int foamy = 0, total = 0;
            for (int i = 0; i < 260; i++)
                for (int j = 0; j < 260; j++)
                {
                    float x = i * 1.9f, z = j * 2.3f;
                    var sample = GerstnerWaves.Evaluate(x, z, 0f, s.SwellSteepness, comps);
                    float crest = WaterMath.Whitecap(sample.Fold, s.FoamCrestCoverage);
                    float pattern = WaterMath.FoamPattern(x, z, 0f, s.FoamPatternScale);
                    if (WaterMath.FoamAmount(crest, 0f, pattern, s.FoamStrength) > 0.4f) foamy++;
                    total++;
                }
            float fraction = (float)foamy / total;
            Assert.InRange(fraction, 0.01f, 0.30f);
        }
    }
}
