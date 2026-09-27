using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public sealed class RadialMenuEnabledDetailGpuTests
    {
        const int Width = 360;
        const int Height = 360;
        static readonly Vector2 Center = new(180f, 180f);

        [GpuFact]
        public void Enabled_detail_draws_in_its_color_after_the_option_changes_on_an_open_cached_menu()
        {
            RadialMenuTheme theme = TextOnlyTheme();
            theme.Detail = new Vector4(0.05f, 0.75f, 0.12f, 1f);
            byte[] rgba = Capture(
                theme,
                [new RadialMenuEntry(
                    LocalizedText.Raw("Drop"),
                    1,
                    Detail: LocalizedText.Raw("(4)"))],
                showEnabledDetails: true,
                warmWithoutEnabledDetails: true);

            int greenPixels = 0;
            for (int y = 72; y < 112; y++)
            {
                for (int x = 135; x < 225; x++)
                {
                    Rgba pixel = Pixel(rgba, x, y);
                    if (pixel.A > 0 && pixel.G > pixel.R * 2 && pixel.G > pixel.B * 2)
                        greenPixels++;
                }
            }

            Assert.True(greenPixels > 10,
                $"enabled detail should draw beneath its label in the explicit green, got {greenPixels} pixels");
        }

        [GpuFact]
        public void Option_off_keeps_an_enabled_detail_out_of_the_wedge()
        {
            RadialMenuTheme theme = TextOnlyTheme();
            RadialMenuEntry[] withDetail =
            [
                new(LocalizedText.Raw(""), 1),
                new(LocalizedText.Raw("Drop"), 2, Detail: LocalizedText.Raw("(4)")),
                new(LocalizedText.Raw(""), 3),
                new(LocalizedText.Raw(""), 4),
            ];
            RadialMenuEntry[] withoutDetail =
            [
                new(LocalizedText.Raw(""), 1),
                new(LocalizedText.Raw("Drop"), 2),
                new(LocalizedText.Raw(""), 3),
                new(LocalizedText.Raw(""), 4),
            ];

            Assert.Equal(
                Capture(theme, withoutDetail, showEnabledDetails: false),
                Capture(theme, withDetail, showEnabledDetails: false));
        }

        [GpuFact]
        public void Default_disabled_detail_spacing_matches_the_existing_two_pixel_gap()
        {
            RadialMenuTheme theme = TextOnlyTheme();
            theme.Disabled = Vector4.One;
            theme.DisabledDetail = new Vector4(0.8f, 0.1f, 0.05f, 1f);
            RadialMenuEntry[] entries =
            [
                new(
                    LocalizedText.Raw("Build"),
                    1,
                    Enabled: false,
                    Detail: LocalizedText.Raw("Level 4")),
            ];

            Assert.Equal(
                Capture(theme, entries, showEnabledDetails: false, RadialMenuMetrics.Default with { DetailGap = 2f }),
                Capture(theme, entries, showEnabledDetails: false, RadialMenuMetrics.Default));
        }

        static byte[] Capture(
            RadialMenuTheme theme,
            RadialMenuEntry[] entries,
            bool showEnabledDetails,
            RadialMenuMetrics? metrics = null,
            bool warmWithoutEnabledDetails = false) =>
            Render2DSnapshot.Capture(Width, Height, Color.Transparent, ctx =>
            {
                Texture2D white = ctx.CreateTexture([255, 255, 255, 255], 1, 1);
                SpriteFont font = ctx.LoadDefaultFont(18f);
                var menu = new RadialMenu
                {
                    SafeBounds = new Rect(0f, 0f, Width, Height),
                    Theme = theme,
                    Metrics = metrics ?? RadialMenuMetrics.Default,
                    ShowEnabledEntryDetails = warmWithoutEnabledDetails ? false : showEnabledDetails,
                };
                menu.Open(LocalizedText.Raw(""), entries, Center);
                menu.Update(new Pointer(), 2f);

                var viewport = new DesignViewport(Width, Height, ScaleMode.Fit);
                viewport.Update(Width, Height);
                if (warmWithoutEnabledDetails)
                {
                    ctx.Batch.Begin(viewport);
                    menu.Draw(ctx.Batch, white, font);
                    ctx.Batch.End();
                    menu.ShowEnabledEntryDetails = showEnabledDetails;
                }
                ctx.Batch.Begin(viewport);
                menu.Draw(ctx.Batch, white, font);
                ctx.Batch.End();
            });

        static RadialMenuTheme TextOnlyTheme() => new()
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

        static Rgba Pixel(byte[] rgba, int x, int y)
        {
            int i = (y * Width + x) * 4;
            return new Rgba(rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
        }

        readonly record struct Rgba(byte R, byte G, byte B, byte A);
    }
}
