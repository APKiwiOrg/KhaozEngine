using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    [Collection("AllocSensitive")]
    public sealed class RadialMenuGpuTests
    {
        const int Width = 512;
        const int Height = 430;
        static readonly Vector2 Center = new(256f, 220f);
        static readonly string FontPath = System.IO.Path.Combine(
            AppContext.BaseDirectory, "Assets", "Roboto-Regular.ttf");

        [GpuFact]
        public void Four_wedges_and_footer_render_distinct_enabled_disabled_active_and_selected_states()
        {
            byte[] rgba = Capture(unknownIcon: false);

            Rgba active = Pixel(rgba, 256, 140);
            Rgba enabled = Pixel(rgba, 336, 220);
            Rgba disabled = Pixel(rgba, 256, 300);
            Rgba selected = Pixel(rgba, 157, 385);
            Rgba centerPlate = Pixel(rgba, 256, 220);

            Assert.True(CountNonTransparent(rgba) > 20000, "the radial composition must paint visible coverage");
            Assert.True(active.A > 0 && enabled.A > 0 && disabled.A > 0 && selected.A > 0,
                "each sampled state must paint a nontransparent pixel");
            Assert.True(centerPlate.A > 0, "the centre plate must paint behind its retained text");
            Assert.NotEqual(enabled, disabled);
            Assert.NotEqual(enabled, active);
            Assert.NotEqual(enabled, selected);
            Assert.NotEqual(active, selected);
        }

        [GpuFact]
        public void An_unknown_icon_uses_the_same_centred_label_layout_as_no_icon()
        {
            Assert.Equal(Capture(unknownIcon: false), Capture(unknownIcon: true));
        }

        [GpuFact]
        public void Reopening_the_same_layout_refreshes_cached_icon_and_label_placement()
        {
            Assert.Equal(Capture(unknownIcon: true), CaptureAfterReopen());
        }

        [GpuFact]
        public void SafeBounds_change_between_update_and_draw_matches_a_fresh_current_layout()
        {
            Assert.Equal(CaptureWithCurrentSafeBounds(liveChange: false), CaptureWithCurrentSafeBounds(liveChange: true));
        }

        [GpuFact]
        public void Disabled_alpha_applies_to_every_wedge_visual_channel()
        {
            byte[] rgba = CaptureFullyDisabled();
            Rgba surface = Pixel(rgba, 256, 220);
            Rgba highlight = Pixel(rgba, 306, 220);
            Rgba border = Pixel(rgba, 308, 220);
            Rgba shadow = Pixel(rgba, 296, 260);

            Assert.True(CountNonTransparentInsideCircle(rgba, Center, 60f) > 5000,
                "the independent centre plate must remain visible");
            Assert.Equal(0, CountNonTransparentOutsideCircle(rgba, Center, 60f));
            Assert.True(surface.A > 0 && highlight.A > 0 && border.A > 0 && shadow.A > 0,
                $"centre layers must paint, got {surface}, {highlight}, {border}, and {shadow}");
            Assert.NotEqual(surface, highlight);
            Assert.NotEqual(highlight, border);
            Assert.NotEqual(surface, shadow);
        }

        [GpuFact]
        public void Warm_draw_allocates_zero_bytes()
        {
            Render2DSnapshot.Capture(Width, Height, Color.Transparent, ctx =>
            {
                SpriteFont font = ctx.LoadFont(FontPath, 18f, oversample: 1);
                Texture2D white = ctx.CreateTexture([255, 255, 255, 255], 1, 1);
                IconAtlas icons = IconAtlas.Bake(ctx, cell: 32);
                RadialMenu menu = OpenMenu(unknownIcon: false);
                var viewport = new DesignViewport(Width, Height, ScaleMode.Fit);
                viewport.Update(Width, Height);

                ctx.Batch.Begin(viewport);
                // AllocAssert may execute the measured 32-call workload twice. Warm both passes into the retained
                // run-list capacity so an unrelated first-pass allocation cannot make the retry grow that list.
                for (int i = 0; i < 64; i++) menu.Draw(ctx.Batch, white, font, icons);
                ctx.Batch.End();

                ctx.Batch.Begin(viewport);
                AllocAssert.NoPerCallAllocation("32 warmed RadialMenu.Draw calls", () =>
                {
                    for (int i = 0; i < 32; i++) menu.Draw(ctx.Batch, white, font, icons);
                });
                ctx.Batch.End();
            });
        }

        static byte[] Capture(bool unknownIcon) =>
            Render2DSnapshot.Capture(Width, Height, Color.Transparent, ctx =>
            {
                SpriteFont font = ctx.LoadFont(FontPath, 18f, oversample: 1);
                Texture2D white = ctx.CreateTexture([255, 255, 255, 255], 1, 1);
                IconAtlas icons = IconAtlas.Bake(ctx, cell: 32);
                RadialMenu menu = OpenMenu(unknownIcon);
                var viewport = new DesignViewport(Width, Height, ScaleMode.Fit);
                viewport.Update(Width, Height);
                ctx.Batch.Begin(viewport);
                menu.Draw(ctx.Batch, white, font, icons);
                ctx.Batch.End();
            });

        static byte[] CaptureAfterReopen() =>
            Render2DSnapshot.Capture(Width, Height, Color.Transparent, ctx =>
            {
                SpriteFont font = ctx.LoadFont(FontPath, 18f, oversample: 1);
                Texture2D white = ctx.CreateTexture([255, 255, 255, 255], 1, 1);
                IconAtlas icons = IconAtlas.Bake(ctx, cell: 32);
                RadialMenu menu = OpenMenu(unknownIcon: false);
                menu = ReopenMenu(menu, Icons.Heart);
                menu.Theme = TransparentTheme();
                var viewport = new DesignViewport(Width, Height, ScaleMode.Fit);
                viewport.Update(Width, Height);
                ctx.Batch.Begin(viewport);
                menu.Draw(ctx.Batch, white, font, icons);
                ctx.Batch.End();

                menu.Theme = TestTheme();
                menu = ReopenMenu(menu, "unknown");
                ctx.Batch.Begin(viewport);
                menu.Draw(ctx.Batch, white, font, icons);
                ctx.Batch.End();
            });

        static byte[] CaptureWithCurrentSafeBounds(bool liveChange) =>
            Render2DSnapshot.Capture(Width, Height, Color.Transparent, ctx =>
            {
                SpriteFont font = ctx.LoadFont(FontPath, 18f, oversample: 1);
                Texture2D white = ctx.CreateTexture([255, 255, 255, 255], 1, 1);
                IconAtlas icons = IconAtlas.Bake(ctx, cell: 32);
                var currentSafeBounds = new Rect(0f, 0f, 360f, Height);
                var menu = new RadialMenu
                {
                    SafeBounds = liveChange
                        ? new Rect(0f, 0f, Width, Height)
                        : currentSafeBounds,
                    Theme = TestTheme(),
                };
                ReopenMenu(menu, westIcon: null);
                menu.Update(new Pointer(), 2f);
                if (liveChange)
                    menu.SafeBounds = currentSafeBounds;

                var viewport = new DesignViewport(Width, Height, ScaleMode.Fit);
                viewport.Update(Width, Height);
                ctx.Batch.Begin(viewport);
                menu.Draw(ctx.Batch, white, font, icons);
                ctx.Batch.End();
            });

        static byte[] CaptureFullyDisabled() =>
            Render2DSnapshot.Capture(Width, Height, Color.Transparent, ctx =>
            {
                SpriteFont font = ctx.LoadFont(FontPath, 18f, oversample: 1);
                Texture2D white = ctx.CreateTexture([255, 255, 255, 255], 1, 1);
                IconAtlas icons = IconAtlas.Bake(ctx, cell: 32);
                RadialMenuTheme theme = TestTheme();
                theme.Disabled = new Vector4(1f, 1f, 1f, 0f);
                var menu = new RadialMenu
                {
                    SafeBounds = new Rect(0f, 0f, Width, Height),
                    Theme = theme,
                };
                menu.Open(
                    LocalizedText.Raw(""),
                    [new RadialMenuEntry(
                        LocalizedText.Raw("Disabled"),
                        1,
                        Icons.Heart,
                        Enabled: false,
                        Detail: LocalizedText.Raw("Unavailable"))],
                    Center);
                menu.Update(new Pointer(), 2f);
                var viewport = new DesignViewport(Width, Height, ScaleMode.Fit);
                viewport.Update(Width, Height);
                ctx.Batch.Begin(viewport);
                menu.Draw(ctx.Batch, white, font, icons);
                ctx.Batch.End();
            });

        static RadialMenu OpenMenu(bool unknownIcon)
        {
            var menu = new RadialMenu
            {
                SafeBounds = new Rect(0f, 0f, Width, Height),
                Theme = TestTheme(),
            };
            return ReopenMenu(menu, unknownIcon ? "unknown" : null);
        }

        static RadialMenu ReopenMenu(RadialMenu menu, string? westIcon)
        {
            menu.Open(
                LocalizedText.Raw("Actions"),
                [
                    new RadialMenuEntry(LocalizedText.Raw("North"), 1, Icons.Heart, Detail: LocalizedText.Raw("Active detail"), InitialChoiceTag: 10),
                    new RadialMenuEntry(LocalizedText.Raw("East"), 2, InitialChoiceTag: 11),
                    new RadialMenuEntry(LocalizedText.Raw("South"), 3, Enabled: false, InitialChoiceTag: 10),
                    new RadialMenuEntry(LocalizedText.Raw("West"), 4, westIcon, InitialChoiceTag: 13),
                ],
                Center,
                [
                    new RadialMenuChoice(LocalizedText.Raw("One"), 10),
                    new RadialMenuChoice(LocalizedText.Raw("Two"), 11),
                    new RadialMenuChoice(LocalizedText.Raw("Three"), 12),
                    new RadialMenuChoice(LocalizedText.Raw("Four"), 13),
                ]);
            menu.Update(new Pointer(), 2f);
            return menu;
        }

        static RadialMenuTheme TestTheme() => new()
        {
            Shadow = new Vector4(0.02f, 0.03f, 0.05f, 0.8f),
            Surface = new Vector4(0.16f, 0.24f, 0.34f, 0.9f),
            SurfaceHighlight = new Vector4(0.30f, 0.70f, 0.85f, 0.5f),
            Border = new Vector4(0.7f, 0.8f, 0.9f, 0.9f),
            BorderActive = new Vector4(0.2f, 0.9f, 1f, 1f),
            Accent = new Vector4(0.9f, 0.35f, 0.08f, 0.8f),
            Text = Vector4.One,
            TextMuted = new Vector4(0.65f, 0.68f, 0.72f, 1f),
            Disabled = new Vector4(1f, 1f, 1f, 0.28f),
            Sheen = new Vector4(0.8f, 0.95f, 1f, 0.24f),
        };

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
            Sheen = Vector4.Zero,
        };

        static int CountNonTransparent(byte[] rgba)
        {
            int count = 0;
            for (int i = 3; i < rgba.Length; i += 4)
                if (rgba[i] != 0) count++;
            return count;
        }

        static int CountNonTransparentInsideCircle(byte[] rgba, Vector2 center, float radius) =>
            CountNonTransparentByCircle(rgba, center, radius, inside: true);

        static int CountNonTransparentOutsideCircle(byte[] rgba, Vector2 center, float radius) =>
            CountNonTransparentByCircle(rgba, center, radius, inside: false);

        static int CountNonTransparentByCircle(byte[] rgba, Vector2 center, float radius, bool inside)
        {
            int count = 0;
            float radiusSquared = radius * radius;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    float distanceSquared = Vector2.DistanceSquared(new Vector2(x, y), center);
                    if ((distanceSquared <= radiusSquared) != inside)
                        continue;
                    if (rgba[(y * Width + x) * 4 + 3] != 0)
                        count++;
                }
            }
            return count;
        }

        static Rgba Pixel(byte[] rgba, int x, int y)
        {
            int i = (y * Width + x) * 4;
            return new Rgba(rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
        }

        readonly record struct Rgba(byte R, byte G, byte B, byte A);
    }
}
