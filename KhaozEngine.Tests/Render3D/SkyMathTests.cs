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
    /// Pure headless coverage for the screen-space sky math (vertical gradient interpolation, sun-disc + halo
    /// falloff, sun-direction defaulting to the key light, sun projection to screen NDC) and the settings / UBO
    /// plumbing. No GPU; SkyMath is the single source both this test and the GLSL SkyFrag follow.
    /// </summary>
    public class SkyMathTests
    {
        static readonly Vector3 Horizon = new(0.6f, 0.7f, 0.8f);
        static readonly Vector3 Zenith = new(0.2f, 0.4f, 0.7f);
        static readonly Vector3 Sun = new(1f, 0.95f, 0.85f);

        // ---- Sun direction defaults to the key light --------------------------------------------------------------

        [Fact]
        public void Sun_direction_is_opposite_the_light_travel_direction()
        {
            var dir = new Vector3(-0.5f, -0.85f, -0.35f);
            var sun = SkyMath.SunDirectionFromLight(dir);
            Assert.Equal(-Vector3.Normalize(dir), sun);
            Assert.Equal(1f, sun.Length(), 3);   // normalized
        }

        [Fact]
        public void Sun_direction_degenerate_light_falls_back_to_up()
        {
            Assert.Equal(Vector3.UnitY, SkyMath.SunDirectionFromLight(Vector3.Zero));
        }

        // ---- Vertical gradient interpolation ----------------------------------------------------------------------

        [Fact]
        public void Gradient_top_of_screen_is_zenith()
        {
            var c = SkyMath.Shade(new Vector2(0f, 1f), Vector2.Zero, sunVisible: false, aspect: 1.5f,
                Horizon, Zenith, Sun, sunEnabled: false, 0.05f, 0f, 0.18f);
            Assert.Equal(Zenith.X, c.X, 3);
            Assert.Equal(Zenith.Z, c.Z, 3);
        }

        [Fact]
        public void Gradient_bottom_of_screen_is_horizon()
        {
            var c = SkyMath.Shade(new Vector2(0f, -1f), Vector2.Zero, sunVisible: false, aspect: 1.5f,
                Horizon, Zenith, Sun, sunEnabled: false, 0.05f, 0f, 0.18f);
            Assert.Equal(Horizon.X, c.X, 3);
            Assert.Equal(Horizon.Z, c.Z, 3);
        }

        [Fact]
        public void Gradient_is_monotonic_bottom_to_top()
        {
            float prev = float.NaN;
            for (int i = 0; i <= 10; i++)
            {
                float y = i / 10f * 2f - 1f;   // -1 (bottom) .. +1 (top)
                var c = SkyMath.Shade(new Vector2(0.5f, y), new Vector2(-2f, -2f) /* sun off-screen */, sunVisible: false,
                    aspect: 1.5f, Horizon, Zenith, Sun, sunEnabled: false, 0.05f, 0f, 0.18f);
                if (!float.IsNaN(prev))
                {
                    // Zenith.X (0.2) < Horizon.X (0.6): red DECREASES going up. Assert monotonic in the right direction.
                    Assert.True(c.X <= prev + 1e-4f, $"gradient R should decrease upward, {c.X} !<= {prev}");
                }
                prev = c.X;
            }
        }

        // ---- Sun disc + halo falloff (screen-space) ---------------------------------------------------------------

        [Fact]
        public void Sun_disc_center_is_sun_color()
        {
            var sunNdc = new Vector2(0.3f, 0.4f);
            var c = SkyMath.Shade(sunNdc, sunNdc, sunVisible: true, aspect: 1f,
                Horizon, Zenith, Sun, sunEnabled: true, sunRadius: 0.05f, haloStrength: 0.5f, haloFalloff: 0.18f);
            Assert.Equal(Sun.X, c.X, 3);
            Assert.Equal(Sun.Y, c.Y, 3);
            Assert.Equal(Sun.Z, c.Z, 3);
        }

        [Fact]
        public void Sun_falls_off_with_screen_distance_disc_then_halo_then_gradient()
        {
            var sunNdc = new Vector2(0f, 0f);
            const float radius = 0.05f, halo = 0.5f, falloff = 0.18f;

            float SunTerm(Vector2 ndc)
            {
                var c = SkyMath.Shade(ndc, sunNdc, true, 1f, Horizon, Zenith, Sun, true, radius, halo, falloff);
                var grad = SkyMath.Shade(ndc, sunNdc, false, 1f, Horizon, Zenith, Sun, false, radius, halo, falloff);
                return (c.X - grad.X) / (Sun.X - grad.X);   // recovered sun blend on R (Sun.R != grad.R at y=0)
            }

            float atCenter = SunTerm(new Vector2(0f, 0f));
            float justOutside = SunTerm(new Vector2(radius + 0.06f, 0f));
            float farAway = SunTerm(new Vector2(0.8f, 0f));

            Assert.True(atCenter > 0.99f, $"disc center should be full sun, got {atCenter}");
            Assert.True(justOutside > 0.01f && justOutside < atCenter, $"halo should be partial, got {justOutside}");
            Assert.True(farAway < 0.02f, $"far from the sun should be pure gradient, got {farAway}");
        }

        [Fact]
        public void Sun_not_drawn_when_disabled_or_behind_camera()
        {
            var sunNdc = new Vector2(0f, 0f);
            var withSun = SkyMath.Shade(sunNdc, sunNdc, sunVisible: true, 1f, Horizon, Zenith, Sun, true, 0.05f, 0.5f, 0.18f);
            var disabled = SkyMath.Shade(sunNdc, sunNdc, sunVisible: true, 1f, Horizon, Zenith, Sun, false, 0.05f, 0.5f, 0.18f);
            var behind = SkyMath.Shade(sunNdc, sunNdc, sunVisible: false, 1f, Horizon, Zenith, Sun, true, 0.05f, 0.5f, 0.18f);
            Assert.NotEqual(withSun.X, disabled.X, 3);   // enabled + visible differs
            Assert.Equal(disabled.X, behind.X, 3);       // behind-camera == sun off == pure gradient
        }

        [Fact]
        public void Disc_is_aspect_corrected_round_in_pixels()
        {
            // At the same NDC distance horizontally vs vertically, a wide aspect makes the horizontal reach stop
            // sooner (the disc stays round in pixels): a point one radius to the RIGHT is outside, one radius UP is
            // still inside (with a >1 aspect the x-delta is magnified).
            var sunNdc = new Vector2(0f, 0f);
            const float radius = 0.05f, aspect = 2f;
            float RightTerm(float dx)
            {
                var c = SkyMath.Shade(new Vector2(dx, 0f), sunNdc, true, aspect, Horizon, Zenith, Sun, true, radius, 0f, 0.18f);
                var g = SkyMath.Shade(new Vector2(dx, 0f), sunNdc, false, aspect, Horizon, Zenith, Sun, false, radius, 0f, 0.18f);
                return (c.X - g.X) / (Sun.X - g.X);
            }
            float UpTerm(float dy)
            {
                var c = SkyMath.Shade(new Vector2(0f, dy), sunNdc, true, aspect, Horizon, Zenith, Sun, true, radius, 0f, 0.18f);
                var g = SkyMath.Shade(new Vector2(0f, dy), sunNdc, false, aspect, Horizon, Zenith, Sun, false, radius, 0f, 0.18f);
                return (c.X - g.X) / (Sun.X - g.X);
            }
            // 0.04 NDC to the right * aspect 2 = 0.08 > radius 0.05 => outside; 0.04 up = 0.04 < 0.05 => inside.
            Assert.True(RightTerm(0.04f) < 0.5f, "horizontal reach should be compressed by aspect");
            Assert.True(UpTerm(0.04f) > 0.5f, "vertical reach is not aspect-scaled");
        }

        // ---- Sun placement in screen NDC (from the view matrix) ---------------------------------------------------

        [Fact]
        public void ProjectSun_straight_up_is_visible_top_center()
        {
            // Camera at +Z looking at origin (forward = -Z, up = +Y). Sun straight up (+Y) sits top-centre.
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, 5), Vector3.Zero, Vector3.UnitY);
            var sunDir = new Vector3(0, 1, 0);
            bool vis = SkyMath.ProjectSunToNdc(view, sunDir, out var ndc);
            Assert.True(vis);
            Assert.True(MathF.Abs(ndc.X) < 0.02f, $"sun straight up should be horizontally centred, got {ndc}");
            Assert.True(ndc.Y > 0.9f, $"sun straight up should be near the top, got {ndc}");
        }

        [Fact]
        public void ProjectSun_below_the_view_horizon_is_not_visible()
        {
            // Sun straight down (-Y): below the horizon, not in the sky.
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, 5), Vector3.Zero, Vector3.UnitY);
            bool vis = SkyMath.ProjectSunToNdc(view, new Vector3(0, -1, 0), out _);
            Assert.False(vis);
            // And a sun exactly at the horizon (level, dead ahead) is also not "in the sky".
            Assert.False(SkyMath.ProjectSunToNdc(view, new Vector3(0, 0, -1), out _));
        }

        [Fact]
        public void ProjectSun_up_and_right_lands_upper_right()
        {
            // Sun up + right (in view space) => +x, +y NDC (upper-right of the screen).
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, 5), Vector3.Zero, Vector3.UnitY);
            var sunDir = Vector3.Normalize(new Vector3(0.4f, 0.6f, -0.3f));
            bool vis = SkyMath.ProjectSunToNdc(view, sunDir, out var ndc);
            Assert.True(vis);
            Assert.True(ndc.X > 0f, $"sun to the right should be +x NDC, got {ndc}");
            Assert.True(ndc.Y > 0f, $"sun above should be +y NDC, got {ndc}");
            Assert.True(MathF.Abs(ndc.X) <= 1f && MathF.Abs(ndc.Y) <= 1f, $"stays on screen, got {ndc}");
        }

        [Fact]
        public void ProjectSun_lands_on_screen_for_the_iso_golden_framing()
        {
            // Regression for the ortho iso camera: projecting a point at infinity blew up the NDC (huge off-screen
            // values); the direction-into-view-space placement must land inside the screen for the golden framing.
            var cam = new IsoCamera3D { AspectRatio = 480f / 320f };
            cam.Frame(new Vector3(0.2f, 0.4f, 0f), new Vector3(6f, 4.5f, 6f));
            var sun = SkyMath.SunDirectionFromLight(new Vector3(-0.55f, -0.8f, -0.25f));
            bool vis = SkyMath.ProjectSunToNdc(cam.View, sun, out var ndc);
            Assert.True(vis, "the golden's sun is above/ahead, so it should be visible");
            Assert.True(MathF.Abs(ndc.X) <= 1f && MathF.Abs(ndc.Y) <= 1f, $"sun must land on-screen, got {ndc}");
        }

        // ---- Settings plumbing / sun-direction resolution ---------------------------------------------------------

        [Fact]
        public void Settings_default_is_off_and_sun_defaults_to_key_light()
        {
            var s = new SkySettings();
            Assert.False(s.Enabled);           // default OFF: existing scenes byte-stable
            var light = new Vector3(-0.5f, -0.85f, -0.35f);
            Assert.Equal(SkyMath.SunDirectionFromLight(light), s.ResolveSunDirection(light));
        }

        [Fact]
        public void Settings_override_wins_and_is_normalized()
        {
            var s = new SkySettings { SunDirectionOverride = new Vector3(0f, 0f, 4f) };
            var resolved = s.ResolveSunDirection(new Vector3(-1f, -1f, 0f));
            Assert.Equal(Vector3.UnitZ, resolved);        // override direction, normalized
        }

        // ---- UBO packing -------------------------------------------------------------------------------------------

        [Fact]
        public void PackUbo_carries_colors_projected_sun_params_and_render_size()
        {
            var sky = new SkySettings
            {
                Enabled = true,
                HorizonColor = new Color(0.6f, 0.7f, 0.8f, 1f),
                ZenithColor = new Color(0.2f, 0.4f, 0.7f, 1f),
                SunEnabled = true,
                SunColor = new Color(1f, 0.96f, 0.85f, 1f),
                SunRadius = 0.05f,
                HaloStrength = 0.6f,
                HaloFalloff = 0.2f,
            };
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, 5), Vector3.Zero, Vector3.UnitY);
            var light = new Vector3(0, -1, -0.2f);   // travels DOWN, so the sun is UP (visible in the sky)

            var u = SkyRenderer.PackUbo(sky, view, light, renderWidth: 800, renderHeight: 600);

            Assert.Equal(sky.HorizonColor.R, u.Horizon.X, 4);
            Assert.Equal(sky.ZenithColor.B, u.Zenith.Z, 4);
            Assert.Equal(sky.SunColor.G, u.SunColor.Y, 4);
            Assert.Equal(1f, u.SunNdc.Z, 4);                        // sun ahead: visible
            Assert.Equal(800f / 600f, u.SunNdc.W, 4);              // aspect
            Assert.Equal(1f, u.Params.X, 4);                       // sunEnabled
            Assert.Equal(sky.SunRadius, u.Params.Y, 4);
            Assert.Equal(sky.HaloStrength, u.Params.Z, 4);
            Assert.Equal(sky.HaloFalloff, u.Params.W, 4);
            Assert.Equal(1f / 800f, u.Res.X, 6);
            Assert.Equal(1f / 600f, u.Res.Y, 6);
        }

        [Fact]
        public void PackUbo_sun_disabled_sets_param_zero()
        {
            var sky = new SkySettings { SunEnabled = false };
            var u = SkyRenderer.PackUbo(sky, Matrix4x4.Identity, new Vector3(0, -1, 0), 100, 100);
            Assert.Equal(0f, u.Params.X, 4);
        }
    }
}
