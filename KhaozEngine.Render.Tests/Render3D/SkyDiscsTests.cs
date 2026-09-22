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
    /// Headless coverage of the sky's disc LIST (#1041): which discs a frame draws and in what order, several bodies
    /// in one sky and one reflection, every disc setting through the world horizon, and both uniform blocks carrying
    /// each disc along its own direction. <c>SkyFrag</c> and the water's <c>skyAlongDirection</c> mirror these loops.
    /// </summary>
    public class SkyDiscsTests
    {
        static readonly Vector3 HorizonRgb = new(0.6f, 0.7f, 0.8f);
        static readonly Vector3 ZenithRgb = new(0.2f, 0.4f, 0.7f);
        static readonly Color SunColor = new(1f, 0.95f, 0.8f, 1f);
        static readonly Color MoonColor = new(0.7f, 0.75f, 0.9f, 1f);
        static readonly Color SecondSunColor = new(1f, 0.4f, 0.2f, 1f);

        static SkyDisc Disc(Vector3 direction, Color color, float radius = 0.05f) => new()
        {
            Direction = direction, Color = color, Radius = radius, HaloStrength = 0f, HaloFalloff = 0.04f,
        };

        static Vector3 Rgb(Color c) => new(c.R, c.G, c.B);

        [Fact]
        public void The_primary_disc_comes_first_then_the_extras_in_list_order()
        {
            var sky = new SkySettings { SunColor = SunColor };
            sky.ExtraDiscs.Add(Disc(new Vector3(0f, 3f, 0f), MoonColor));
            sky.ExtraDiscs.Add(Disc(new Vector3(2f, 0f, 0f), SecondSunColor));

            Span<SkyDisc> discs = stackalloc SkyDisc[SkySettings.MaxDiscs];
            int count = SkyDiscs.Resolve(sky, new Vector3(0f, -1f, 0f), discs);

            Assert.Equal(3, count);
            Assert.Equal(SunColor, discs[0].Color);
            Assert.Equal(Vector3.UnitY, discs[0].Direction);   // the primary follows the key light
            Assert.Equal(MoonColor, discs[1].Color);
            Assert.Equal(Vector3.UnitY, discs[1].Direction);   // extras are normalized
            Assert.Equal(SecondSunColor, discs[2].Color);
            Assert.Equal(Vector3.UnitX, discs[2].Direction);
        }

        [Fact]
        public void A_disabled_sun_leaves_the_extras_and_an_invisible_extra_is_dropped()
        {
            var sky = new SkySettings { SunEnabled = false };
            sky.ExtraDiscs.Add(Disc(Vector3.UnitY, MoonColor.WithAlpha(0f)));
            sky.ExtraDiscs.Add(Disc(Vector3.Zero, SecondSunColor));

            Span<SkyDisc> discs = stackalloc SkyDisc[SkySettings.MaxDiscs];
            int count = SkyDiscs.Resolve(sky, new Vector3(0f, -1f, 0f), discs);

            Assert.Equal(1, count);
            Assert.Equal(SecondSunColor, discs[0].Color);
            Assert.Equal(Vector3.UnitY, discs[0].Direction);   // a zero direction reads as straight up
        }

        [Fact]
        public void Discs_past_the_cap_are_ignored()
        {
            var sky = new SkySettings();
            for (int i = 0; i < SkySettings.MaxDiscs + 4; i++) sky.ExtraDiscs.Add(Disc(Vector3.UnitY, MoonColor));

            Span<SkyDisc> discs = stackalloc SkyDisc[SkySettings.MaxDiscs];
            Assert.Equal(SkySettings.MaxDiscs, SkyDiscs.Resolve(sky, new Vector3(0f, -1f, 0f), discs));
        }

        [Fact]
        public void Two_bodies_share_one_sky_and_a_later_disc_goes_over_an_earlier_one()
        {
            var left = new SkyMath.ScreenDisc(new Vector2(-0.5f, 0.4f), Disc(default, SunColor));
            var right = new SkyMath.ScreenDisc(new Vector2(0.5f, 0.4f), Disc(default, MoonColor));
            var eclipsing = new SkyMath.ScreenDisc(new Vector2(-0.5f, 0.4f), Disc(default, SecondSunColor, radius: 0.02f));
            var discs = new[] { left, right, eclipsing };

            Vector3 At(Vector2 ndc) => SkyMath.ShadeDiscs(ndc, 1f, HorizonRgb, ZenithRgb, discs);

            AssertRgb(Rgb(MoonColor), At(right.Ndc));
            AssertRgb(Rgb(SecondSunColor), At(left.Ndc));                         // the small later disc is in front
            AssertRgb(Rgb(SunColor), At(left.Ndc + new Vector2(0.035f, 0f)));     // and the first shows around it
        }

        [Fact]
        public void Every_disc_sets_through_the_world_horizon()
        {
            var sky = new SkySettings
            {
                Horizon = SkyHorizon.World, GroundColor = new Color(0.2f, 0.3f, 0.15f, 1f), HorizonSoftness = 0.002f,
            };
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 2, 0), new Vector3(0, 2, -10), Vector3.UnitY);
            var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 500f);
            SkyHorizonFrame frame = SkyHorizonMath.ResolveFrame(sky, view, projection);
            var below = new Vector2(0.4f, -0.3f);
            var discs = new[]
            {
                new SkyMath.ScreenDisc(new Vector2(-0.4f, -0.3f), Disc(default, SunColor)),
                new SkyMath.ScreenDisc(below, Disc(default, MoonColor)),
            };
            AssertRgb(Rgb(sky.GroundColor), SkyMath.ShadeDiscs(below, 1f, HorizonRgb, ZenithRgb, discs, frame));
            AssertRgb(Rgb(sky.GroundColor), SkyMath.ShadeDiscs(discs[0].Ndc, 1f, HorizonRgb, ZenithRgb, discs, frame));
        }

        [Fact]
        public void The_reflected_sky_carries_every_disc_along_its_own_direction()
        {
            Vector3 sunDir = Vector3.Normalize(new Vector3(0.3f, 0.6f, -0.5f));
            Vector3 moonDir = Vector3.Normalize(new Vector3(-0.5f, 0.4f, 0.2f));
            var discs = new[] { Disc(sunDir, SunColor), Disc(moonDir, MoonColor) };

            AssertRgb(Rgb(SunColor), SkyMath.ShadeDirectionDiscs(sunDir, HorizonRgb, ZenithRgb, discs, 1f));
            AssertRgb(Rgb(MoonColor), SkyMath.ShadeDirectionDiscs(moonDir, HorizonRgb, ZenithRgb, discs, 1f));
        }

        [Fact]
        public void Sky_ubo_packs_only_the_discs_that_are_on_screen_in_order()
        {
            var view = Matrix4x4.CreateLookAt(new Vector3(0, 0, 5), new Vector3(0, 0, 4), Vector3.UnitY);
            var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 4f / 3f, 0.1f, 100f);
            var sky = new SkySettings { SunColor = SunColor };
            sky.ExtraDiscs.Add(Disc(new Vector3(0f, 0.2f, 1f), MoonColor));                       // behind the camera
            sky.ExtraDiscs.Add(Disc(new Vector3(0.3f, 0.2f, -1f), SecondSunColor, radius: 0.09f)); // ahead

            var u = SkyRenderer.PackUbo(sky, view, projection, new Vector3(0f, -1f, 0.2f), 800, 600);

            Assert.Equal(2f, u.Res.W);
            Assert.Equal((Vector4)SunColor, u.DiscColor[0]);
            Assert.Equal((Vector4)SecondSunColor, u.DiscColor[1]);
            Assert.Equal(0.09f, u.DiscPlace[1].Z);
            Assert.True(u.DiscPlace[1].X > 0f, "the second sun is to the right of centre");
        }

        [Fact]
        public void Water_ubo_reflects_the_primary_disc_where_the_sky_draws_it_not_where_the_key_light_is()
        {
            // The old block reflected the disc along -LightDir and ignored SunDirectionOverride. A SunCycle sun that has
            // set keeps the disc pointed at the sun while the default night key flips anti-solar, so the sea reflected
            // a ghost sun opposite the real one.
            var vp = Matrix4x4.CreateLookAt(new Vector3(0, 5, 10), Vector3.Zero, Vector3.UnitY)
                     * Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3f, 4f / 3f, 0.1f, 100f);
            Vector3 realSun = Vector3.Normalize(new Vector3(0.6f, -0.05f, -0.8f));
            var sky = new SkySettings { SunColor = SunColor, SunDirectionOverride = realSun };
            sky.ExtraDiscs.Add(Disc(Vector3.UnitY, MoonColor, radius: 0.03f));
            Vector3 flippedKey = realSun;   // the anti-solar key travels TOWARD where the sun is

            var u = WaterRenderer.PackUbo(vp, vp, flippedKey, new Color(1f, 1f, 1f, 1f), Vector3.Zero,
                new WaterSettings(), sky, 0f);

            Assert.Equal(2f, u.SkyParams.X);
            Assert.Equal(new Vector4(realSun, sky.SunRadius), u.SkyDiscDir[0]);
            Assert.Equal(new Vector4(Vector3.UnitY, 0.03f), u.SkyDiscDir[1]);
            Assert.Equal((Vector4)MoonColor, u.SkyDiscColor[1]);
        }

        static void AssertRgb(Vector3 expected, Vector3 actual)
        {
            Assert.Equal(expected.X, actual.X, 3);
            Assert.Equal(expected.Y, actual.Y, 3);
            Assert.Equal(expected.Z, actual.Z, 3);
        }
    }
}
