using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public sealed class RadialMenuSurfaceFeedbackGpuTests
    {
        const int Width = 512;
        const int Height = 430;
        static readonly Vector2 Center = new(256f, 215f);

        [GpuFact]
        public void Normal_active_and_disabled_wedges_have_continuous_radial_shading()
        {
            byte[] rgba = Capture(SurfaceTheme());

            AssertSmoothRedRamp(rgba, new Vector2(1f, 0f), "normal");
            AssertSmoothRedRamp(rgba, new Vector2(0f, -1f), "active");
            AssertSmoothRedRamp(rgba, new Vector2(0f, 1f), "disabled");
        }

        [GpuFact]
        public void Sheen_fades_continuously_from_the_inner_to_outer_edge()
        {
            RadialMenuTheme theme = TransparentTheme();
            theme.Sheen = new Vector4(1f, 1f, 1f, 1f);
            byte[] rgba = Capture(theme);

            byte a70 = AlphaAtRadius(rgba, new Vector2(0f, -1f), 70);
            byte a90 = AlphaAtRadius(rgba, new Vector2(0f, -1f), 90);
            byte a110 = AlphaAtRadius(rgba, new Vector2(0f, -1f), 110);
            byte a130 = AlphaAtRadius(rgba, new Vector2(0f, -1f), 130);

            Assert.True(a70 < a90 && a90 < a110 && a110 < a130,
                $"expected a continuous sheen ramp, got {a70}, {a90}, {a110}, {a130}");
        }

        static byte[] Capture(RadialMenuTheme theme) =>
            Render2DSnapshot.Capture(Width, Height, Color.Transparent, ctx =>
            {
                Texture2D white = ctx.CreateTexture([255, 255, 255, 255], 1, 1);
                SpriteFont font = ctx.LoadDefaultFont(18f);
                var menu = new RadialMenu
                {
                    SafeBounds = new Rect(0f, 0f, Width, Height),
                    Theme = theme,
                };
                menu.Open(
                    default,
                    [
                        new RadialMenuEntry(default, 1),
                        new RadialMenuEntry(default, 2),
                        new RadialMenuEntry(default, 3, Enabled: false),
                        new RadialMenuEntry(default, 4),
                    ],
                    Center);
                menu.Update(new Pointer(), 0f);

                var viewport = new DesignViewport(Width, Height, ScaleMode.Fit);
                viewport.Update(Width, Height);
                ctx.Batch.Begin(viewport);
                menu.Draw(ctx.Batch, white, font);
                ctx.Batch.End();
            });

        static RadialMenuTheme SurfaceTheme()
        {
            RadialMenuTheme theme = TransparentTheme();
            theme.Surface = new Vector4(0.08f, 0.08f, 0.08f, 1f);
            theme.SurfaceHighlight = new Vector4(1f, 1f, 1f, 0.75f);
            theme.Accent = new Vector4(0.16f, 0.16f, 0.16f, 1f);
            theme.Disabled = new Vector4(0.5f, 0.5f, 0.5f, 0.85f);
            return theme;
        }

        static RadialMenuTheme TransparentTheme() => new()
        {
            Shadow = Vector4.Zero,
            Surface = Vector4.Zero,
            SurfaceHighlight = Vector4.Zero,
            Border = Vector4.Zero,
            BorderActive = Vector4.Zero,
            Accent = Vector4.Zero,
            Text = Vector4.Zero,
            TextMuted = Vector4.Zero,
            Disabled = Vector4.Zero,
            DisabledDetail = Vector4.Zero,
            Sheen = Vector4.Zero,
        };

        static void AssertSmoothRedRamp(byte[] rgba, Vector2 direction, string state)
        {
            byte r70 = RedAtRadius(rgba, direction, 70);
            byte r90 = RedAtRadius(rgba, direction, 90);
            byte r110 = RedAtRadius(rgba, direction, 110);
            byte r130 = RedAtRadius(rgba, direction, 130);
            Assert.True(r70 < r90 && r90 < r110 && r110 < r130,
                $"expected a continuous {state} ramp, got {r70}, {r90}, {r110}, {r130}");
        }

        static byte RedAtRadius(byte[] rgba, Vector2 direction, int radius) =>
            PixelChannel(rgba, direction, radius, 0);

        static byte AlphaAtRadius(byte[] rgba, Vector2 direction, int radius) =>
            PixelChannel(rgba, direction, radius, 3);

        static byte PixelChannel(byte[] rgba, Vector2 direction, int radius, int channel)
        {
            int x = (int)(Center.X + direction.X * radius);
            int y = (int)(Center.Y + direction.Y * radius);
            return rgba[(y * Width + x) * 4 + channel];
        }
    }
}
