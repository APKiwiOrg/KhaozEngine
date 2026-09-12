using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Offscreen color assertions for disabled radial entries. The palette is deliberately unlike the source
    /// channels so retaining their RGB is observable without a backend-specific golden.
    /// </summary>
    public sealed class RadialMenuDisabledVisualGpuTests
    {
        const int Width = 360;
        const int Height = 360;
        static readonly Vector2 Center = new(180f, 180f);

        [GpuFact]
        public void Disabled_tint_replaces_source_rgb_across_the_wedge()
        {
            byte[] rgba = Capture(DisabledTheme(),
                new RadialMenuEntry(LocalizedText.Raw(""), 1, Enabled: false));

            Rgba surface = Pixel(rgba, 180, 75);

            Assert.True(surface.A > 0, $"disabled surface should remain visible, got {surface}");
            Assert.True(surface.B > surface.R,
                $"disabled blue-grey tint should replace the warm source RGB, got {surface}");
        }

        [GpuFact]
        public void Disabled_entry_draws_its_compact_detail_in_the_explicit_detail_color()
        {
            RadialMenuTheme theme = DisabledTheme();
            theme.Surface = Vector4.Zero;
            theme.Disabled = new Vector4(0.35f, 0.35f, 0.35f, 1f);
            theme.DisabledDetail = new Vector4(0.72f, 0.06f, 0.04f, 1f);
            byte[] rgba = Capture(theme,
                new RadialMenuEntry(
                    LocalizedText.Raw("Build"),
                    1,
                    Enabled: false,
                    Detail: LocalizedText.Raw("Level 4")));

            int darkRedPixels = 0;
            for (int y = 88; y < 115; y++)
            {
                for (int x = 135; x < 225; x++)
                {
                    Rgba pixel = Pixel(rgba, x, y);
                    if (pixel.A > 0 && pixel.R > pixel.G * 2 && pixel.R > pixel.B * 2)
                        darkRedPixels++;
                }
            }

            Assert.True(darkRedPixels > 20,
                $"disabled detail should draw beneath its label in the explicit dark red, got {darkRedPixels} pixels");
        }

        [GpuFact]
        public void Disabled_label_uses_the_explicit_disabled_color()
        {
            RadialMenuTheme theme = DisabledTheme();
            theme.Surface = Vector4.Zero;
            theme.Text = Vector4.One;
            theme.TextMuted = Vector4.One;
            theme.Disabled = new Vector4(0.5f, 0.5f, 0.5f, 0.9f);
            byte[] disabled = Capture(theme,
                new RadialMenuEntry(LocalizedText.Raw("IIII"), 1, Enabled: false));

            theme.Text = theme.Disabled;
            theme.TextMuted = theme.Disabled;
            byte[] explicitColor = Capture(theme,
                new RadialMenuEntry(LocalizedText.Raw("IIII"), 1));

            Assert.Equal(explicitColor, disabled);
        }

        [GpuFact]
        public void Long_entry_label_stays_inside_the_radial_band()
        {
            RadialMenuTheme theme = DisabledTheme();
            theme.Shadow = Vector4.Zero;
            theme.Surface = Vector4.Zero;
            theme.SurfaceHighlight = Vector4.Zero;
            theme.Border = Vector4.Zero;
            theme.BorderActive = Vector4.Zero;
            theme.Accent = Vector4.Zero;
            theme.Text = Vector4.One;
            theme.TextMuted = Vector4.One;
            theme.Sheen = Vector4.Zero;

            byte[] rgba = Capture(theme,
                [
                    new RadialMenuEntry(LocalizedText.Raw(""), 1),
                    new RadialMenuEntry(LocalizedText.Raw(""), 2),
                    new RadialMenuEntry(LocalizedText.Raw(""), 3),
                    new RadialMenuEntry(LocalizedText.Raw("Wooden light bow stave"), 4),
                ]);

            int visible = 0;
            int escaped = 0;
            RadialMenuMetrics metrics = RadialMenuMetrics.Default;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    if (Pixel(rgba, x, y).A == 0)
                        continue;
                    visible++;
                    float radius = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), Center);
                    if (radius < metrics.InnerRadius - 1f || radius > metrics.OuterRadius + 1f)
                        escaped++;
                }
            }

            Assert.True(visible > 20, "the long label must remain readable");
            Assert.Equal(0, escaped);
        }

        static byte[] Capture(RadialMenuTheme theme, RadialMenuEntry entry) =>
            Capture(theme, [entry]);

        static byte[] Capture(RadialMenuTheme theme, RadialMenuEntry[] entries) =>
            Render2DSnapshot.Capture(Width, Height, Color.Transparent, ctx =>
            {
                Texture2D white = ctx.CreateTexture([255, 255, 255, 255], 1, 1);
                SpriteFont font = ctx.LoadDefaultFont(18f);
                var menu = new RadialMenu
                {
                    SafeBounds = new Rect(0f, 0f, Width, Height),
                    Theme = theme,
                };
                menu.Open(LocalizedText.Raw(""), entries, Center);
                menu.Update(new Pointer(), 2f);

                var viewport = new DesignViewport(Width, Height, ScaleMode.Fit);
                viewport.Update(Width, Height);
                ctx.Batch.Begin(viewport);
                menu.Draw(ctx.Batch, white, font);
                ctx.Batch.End();
            });

        static RadialMenuTheme DisabledTheme() => new()
        {
            Shadow = Vector4.Zero,
            Surface = new Vector4(0.85f, 0.55f, 0.08f, 0.9f),
            SurfaceHighlight = Vector4.Zero,
            Border = Vector4.Zero,
            BorderActive = Vector4.Zero,
            Accent = Vector4.Zero,
            Text = Vector4.One,
            TextMuted = Vector4.One,
            Disabled = new Vector4(0.18f, 0.32f, 0.55f, 0.75f),
            DisabledDetail = new Vector4(0.72f, 0.06f, 0.04f, 1f),
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
