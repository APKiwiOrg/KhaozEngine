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
    /// Headless coverage of the world-horizon sky (<see cref="SkyHorizon.World"/>, #396): which frames get one, the
    /// per-pixel view-ray elevation against an independent unproject, the ground band going over the sun, and the
    /// camera sky agreeing with the water's reflected sky. <c>SkyFrag</c> mirrors this math.
    /// </summary>
    public class SkyHorizonMathTests
    {
        static readonly Vector3 HorizonRgb = new(0.6f, 0.7f, 0.8f);
        static readonly Vector3 ZenithRgb = new(0.2f, 0.4f, 0.7f);
        static readonly Vector3 SunRgb = new(1f, 0.95f, 0.85f);
        static readonly Vector3 GroundRgb = new(0.25f, 0.3f, 0.2f);

        static SkySettings WorldSky(float softness = 0.02f) => new()
        {
            Horizon = SkyHorizon.World,
            GroundColor = new Color(GroundRgb.X, GroundRgb.Y, GroundRgb.Z, 1f),
            HorizonSoftness = softness,
        };

        static Matrix4x4 Perspective(float fovDegrees = 60f, float aspect = 16f / 9f) =>
            Matrix4x4.CreatePerspectiveFieldOfView(fovDegrees * MathF.PI / 180f, aspect, 0.1f, 500f);

        /// <summary>The reference the closed form is checked against: unproject the pixel through the full inverse
        /// view-projection and read the world ray's Y.</summary>
        static float UnprojectedSinElevation(Matrix4x4 view, Matrix4x4 projection, Vector2 ndc)
        {
            Assert.True(Matrix4x4.Invert(view * projection, out var inv));
            Vector4 near = Vector4.Transform(new Vector4(ndc.X, ndc.Y, 0.1f, 1f), inv);
            Vector4 far = Vector4.Transform(new Vector4(ndc.X, ndc.Y, 0.9f, 1f), inv);
            Vector3 ray = Vector3.Normalize(
                new Vector3(far.X, far.Y, far.Z) / far.W - new Vector3(near.X, near.Y, near.Z) / near.W);
            return ray.Y;
        }

        [Fact]
        public void Default_sky_has_no_world_horizon()
        {
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 5, 10), Vector3.Zero, Vector3.UnitY);
            Assert.False(SkyHorizonMath.ResolveFrame(new SkySettings(), view, Perspective()).Live);
            Assert.False(SkyHorizonMath.ResolveGround(new SkySettings(), view * Perspective()).Live);
        }

        [Fact]
        public void World_horizon_needs_a_perspective_camera_and_a_world_anchored_sun()
        {
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 5, 10), Vector3.Zero, Vector3.UnitY);
            var ortho = Matrix4x4.CreateOrthographic(20f, 12f, 0.1f, 500f);
            Assert.True(SkyHorizonMath.ResolveFrame(WorldSky(), view, Perspective()).Live);
            Assert.True(SkyHorizonMath.ResolveGround(WorldSky(), view * Perspective()).Live);
            Assert.False(SkyHorizonMath.ResolveFrame(WorldSky(), view, ortho).Live);
            Assert.False(SkyHorizonMath.ResolveGround(WorldSky(), view * ortho).Live);

            var stylized = WorldSky();
            stylized.Anchor = SunAnchor.StylizedBackdrop;
            Assert.False(SkyHorizonMath.ResolveFrame(stylized, view, Perspective()).Live);
        }

        [Fact]
        public void Level_camera_puts_the_horizon_across_screen_centre()
        {
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 2, 0), new Vector3(0, 2, -10), Vector3.UnitY);
            var frame = SkyHorizonMath.ResolveFrame(WorldSky(), view, Perspective(60f));
            Assert.Equal(0f, frame.SinElevation(new Vector2(0f, 0f)), 5);
            Assert.Equal(0f, frame.SinElevation(new Vector2(0.8f, 0f)), 5);
            Assert.Equal(MathF.Sin(30f * MathF.PI / 180f), frame.SinElevation(new Vector2(0f, 1f)), 4);
        }

        [Fact]
        public void Pitched_down_camera_lifts_the_horizon_by_its_pitch()
        {
            // 30 degrees down under a 60 degree vertical fov: the horizon sits exactly on the top edge of the screen,
            // far above the edge of any finite world, which is why a low sun used to hang in mid-sky.
            float pitch = 30f * MathF.PI / 180f;
            var forward = new Vector3(0f, -MathF.Sin(pitch), -MathF.Cos(pitch));
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 8, 0), new Vector3(0, 8, 0) + forward, Vector3.UnitY);
            var frame = SkyHorizonMath.ResolveFrame(WorldSky(), view, Perspective(60f));
            Assert.Equal(0f, frame.SinElevation(new Vector2(0f, 1f)), 4);
            Assert.Equal(-MathF.Sin(pitch), frame.SinElevation(new Vector2(0f, 0f)), 4);
        }

        [Fact]
        public void Pixel_elevation_matches_a_full_unproject_under_roll_and_an_off_centre_projection()
        {
            var cameras = new[]
            {
                Matrix4x4.CreateLookAt(new Vector3(3, 9, 12), new Vector3(-2, 1, -4), Vector3.UnitY),
                Matrix4x4.CreateLookAt(new Vector3(-7, 4, 2), new Vector3(5, 6, -9), Vector3.UnitY),
                // Rolled: the horizon is a tilted line on screen.
                Matrix4x4.CreateLookAt(new Vector3(0, 6, 8), new Vector3(1, 2, -3), Vector3.Normalize(new Vector3(0.35f, 1f, 0.1f))),
            };
            var projections = new[]
            {
                Perspective(60f),
                Perspective(38f, 4f / 3f),
                Matrix4x4.CreatePerspectiveOffCenter(-0.03f, 0.09f, -0.02f, 0.05f, 0.1f, 500f),
            };
            foreach (var view in cameras)
                foreach (var projection in projections)
                {
                    var frame = SkyHorizonMath.ResolveFrame(WorldSky(), view, projection);
                    Assert.True(frame.Live);
                    for (float x = -1f; x <= 1f; x += 0.5f)
                        for (float y = -1f; y <= 1f; y += 0.5f)
                        {
                            var ndc = new Vector2(x, y);
                            Assert.Equal(UnprojectedSinElevation(view, projection, ndc), frame.SinElevation(ndc), 2e-4f);
                        }
                }
        }

        [Fact]
        public void Ground_covers_nothing_at_the_horizon_and_everything_a_softness_below_it()
        {
            var ground = new SkyGround(GroundRgb, 0.02f);
            Assert.Equal(0f, ground.Weight(0.3f));
            Assert.Equal(0f, ground.Weight(0f));
            Assert.InRange(ground.Weight(-0.01f), 0.4f, 0.6f);
            Assert.Equal(1f, ground.Weight(-0.02f));
            Assert.Equal(1f, ground.Weight(-0.5f));
            Assert.Equal(1f, new SkyGround(GroundRgb, 0f).Weight(-0.001f));   // zero softness is a hard line, not NaN
            Assert.Equal(0f, default(SkyGround).Weight(-0.5f));               // no ground on the screen-space sky
        }

        [Fact]
        public void A_sun_on_the_horizon_shows_its_top_half_and_loses_its_bottom_half_to_the_ground()
        {
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 2, 0), new Vector3(0, 2, -10), Vector3.UnitY);
            var frame = SkyHorizonMath.ResolveFrame(WorldSky(softness: 0.002f), view, Perspective(60f));
            var sunNdc = new Vector2(0f, 0f);   // level camera: screen centre is on the horizon

            Vector3 At(float y) => SkyMath.Shade(new Vector2(0f, y), sunNdc, true, 1f, HorizonRgb, ZenithRgb, SunRgb,
                true, sunRadius: 0.06f, haloStrength: 0f, haloFalloff: 0.04f, sunOpacity: 1f, worldHorizon: frame);

            AssertRgb(SunRgb, At(0.03f));       // inside the disc, above the line
            AssertRgb(GroundRgb, At(-0.03f));   // inside the disc, below the line: the ground is in front
        }

        [Fact]
        public void A_sun_below_the_horizon_is_entirely_behind_the_ground()
        {
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 2, 0), new Vector3(0, 2, -10), Vector3.UnitY);
            var frame = SkyHorizonMath.ResolveFrame(WorldSky(softness: 0.002f), view, Perspective(60f));
            var sunNdc = new Vector2(0.2f, -0.3f);
            var c = SkyMath.Shade(sunNdc, sunNdc, true, 1f, HorizonRgb, ZenithRgb, SunRgb,
                true, 0.06f, 0.5f, 0.18f, 1f, frame);
            AssertRgb(GroundRgb, c);
        }

        [Fact]
        public void The_camera_sky_and_the_reflected_sky_are_one_function_of_direction()
        {
            var view = Matrix4x4.CreateLookAt(new Vector3(3, 9, 12), new Vector3(-2, 6, -4), Vector3.UnitY);
            var projection = Perspective(60f);
            var frame = SkyHorizonMath.ResolveFrame(WorldSky(), view, projection);
            Assert.True(Matrix4x4.Invert(view * projection, out var inv));
            for (float y = -1f; y <= 1f; y += 0.25f)
            {
                var ndc = new Vector2(0.3f, y);
                Vector4 near = Vector4.Transform(new Vector4(ndc.X, ndc.Y, 0.1f, 1f), inv);
                Vector4 far = Vector4.Transform(new Vector4(ndc.X, ndc.Y, 0.9f, 1f), inv);
                Vector3 ray = Vector3.Normalize(
                    new Vector3(far.X, far.Y, far.Z) / far.W - new Vector3(near.X, near.Y, near.Z) / near.W);

                var seen = SkyMath.Shade(ndc, Vector2.Zero, false, 16f / 9f, HorizonRgb, ZenithRgb, SunRgb,
                    false, 0.05f, 0.5f, 0.18f, 1f, frame);
                var reflected = SkyMath.ShadeDirection(ray, Vector3.UnitY, HorizonRgb, ZenithRgb, SunRgb,
                    false, 0.05f, 0.5f, 0.18f, 1f, frame.Ground);
                Assert.Equal(reflected.X, seen.X, 3);
                Assert.Equal(reflected.Y, seen.Y, 3);
                Assert.Equal(reflected.Z, seen.Z, 3);
            }
        }

        [Fact]
        public void Sky_ubo_carries_the_horizon_only_when_it_is_live()
        {
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 5, 10), Vector3.Zero, Vector3.UnitY);
            var light = new Vector3(-0.4f, -0.8f, -0.3f);

            var off = SkyRenderer.PackUbo(new SkySettings(), view, Perspective(), light, 1280, 720);
            Assert.Equal(0f, off.HorizonUp.W);

            var on = SkyRenderer.PackUbo(WorldSky(0.05f), view, Perspective(), light, 1280, 720);
            Assert.Equal(1f, on.HorizonUp.W);
            Assert.Equal(new Vector4(GroundRgb, 0.05f), on.Ground);
            Assert.Equal(new Vector3(view.M21, view.M22, view.M23), new Vector3(on.HorizonUp.X, on.HorizonUp.Y, on.HorizonUp.Z));
            Assert.Equal(1f / Perspective().M22, on.HorizonRay.Y, 5);
        }

        [Fact]
        public void Water_ubo_reflects_the_ground_only_when_the_horizon_is_live()
        {
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 5, 10), Vector3.Zero, Vector3.UnitY);
            var vp = view * Perspective();
            var light = new Vector3(-0.4f, -0.8f, -0.3f);
            var lightColor = new Color(1f, 1f, 1f, 1f);

            var off = WaterRenderer.PackUbo(vp, vp, light, lightColor, Vector3.Zero, new WaterSettings(), new SkySettings(), 0f);
            Assert.True(off.SkyGround.W < 0f, "a screen-space sky must flag no ground to the water shader");

            var on = WaterRenderer.PackUbo(vp, vp, light, lightColor, Vector3.Zero, new WaterSettings(), WorldSky(0.05f), 0f);
            Assert.Equal(new Vector4(GroundRgb, 0.05f), on.SkyGround);
        }

        static void AssertRgb(Vector3 expected, Vector3 actual)
        {
            Assert.Equal(expected.X, actual.X, 3);
            Assert.Equal(expected.Y, actual.Y, 3);
            Assert.Equal(expected.Z, actual.Z, 3);
        }
    }
}
