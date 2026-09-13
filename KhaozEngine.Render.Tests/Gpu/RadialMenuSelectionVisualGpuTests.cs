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
    public sealed class RadialMenuSelectionVisualGpuTests
    {
        const int Width = 512;
        const int Height = 430;
        static readonly Vector2 Center = new(256f, 220f);

        readonly MouseFrames _mouse = new();

        [GpuFact]
        public void Locked_entry_stays_highlighted_and_named_after_hover_moves()
        {
            byte[] highlight = Capture(HighlightOnlyTheme());
            byte[] text = Capture(TextOnlyTheme());

            Rgba lockedWedge = Pixel(highlight, 256, 140);
            Rgba hoveredWedge = Pixel(highlight, 336, 220);

            Assert.True(lockedWedge.A > 0, $"locked wedge should retain its highlight, got {lockedWedge}");
            Assert.Equal(0, hoveredWedge.A);
            Assert.Equal(0, CountNonTransparent(highlight, 360, 185, 410, 255));
            Assert.Equal(0, CountBright(text, 305, 195, 410, 245));
            Assert.True(CountInsideCircle(text, Center, 50f) > 20,
                "the centre plate should name the locked recipe");
        }

        [GpuFact]
        public void Warm_locked_draw_allocates_nothing()
        {
            Render2DSnapshot.Capture(Width, Height, Color.Transparent, ctx =>
            {
                Texture2D white = ctx.CreateTexture([255, 255, 255, 255], 1, 1);
                SpriteFont font = ctx.LoadFont(
                    System.IO.Path.Combine(System.AppContext.BaseDirectory, "Assets", "Roboto-Regular.ttf"),
                    18f,
                    oversample: 1);
                RadialMenu menu = OpenMenu(HighlightOnlyTheme());
                var viewport = new DesignViewport(Width, Height, ScaleMode.Fit);
                viewport.Update(Width, Height);

                ctx.Batch.Begin(viewport);
                // AllocAssert may run the 32-call workload twice. Warm that full retained-list capacity so the
                // retry measures Draw rather than a capacity increase caused by its own accumulated first pass.
                for (int i = 0; i < 64; i++) menu.Draw(ctx.Batch, white, font);
                ctx.Batch.End();

                ctx.Batch.Begin(viewport);
                AllocAssert.NoPerCallAllocation("32 warmed locked RadialMenu.Draw calls", () =>
                {
                    for (int i = 0; i < 32; i++) menu.Draw(ctx.Batch, white, font);
                });
                ctx.Batch.End();
            });
        }

        byte[] Capture(RadialMenuTheme theme) =>
            Render2DSnapshot.Capture(Width, Height, Color.Transparent, ctx =>
            {
                Texture2D white = ctx.CreateTexture([255, 255, 255, 255], 1, 1);
                SpriteFont font = ctx.LoadFont(
                    System.IO.Path.Combine(System.AppContext.BaseDirectory, "Assets", "Roboto-Regular.ttf"),
                    18f,
                    oversample: 1);
                RadialMenu menu = OpenMenu(theme);

                var viewport = new DesignViewport(Width, Height, ScaleMode.Fit);
                viewport.Update(Width, Height);
                ctx.Batch.Begin(viewport);
                menu.Draw(ctx.Batch, white, font);
                ctx.Batch.End();
            });

        RadialMenu OpenMenu(RadialMenuTheme theme)
        {
            var menu = new RadialMenu
            {
                SafeBounds = new Rect(0f, 0f, Width, Height),
                InteractionMode = RadialMenuInteractionMode.EntryThenChoice,
                Theme = theme,
            };
            menu.Open(
                LocalizedText.Raw(""),
                [
                    new RadialMenuEntry(LocalizedText.Raw("Arrow shafts"), 1, InitialChoiceTag: 1),
                    new RadialMenuEntry(LocalizedText.Raw("Joinery pegs"), 2, InitialChoiceTag: 1),
                    new RadialMenuEntry(LocalizedText.Raw("Wooden handle"), 3, InitialChoiceTag: 1),
                    new RadialMenuEntry(LocalizedText.Raw("Wooden light bow stave"), 4, InitialChoiceTag: 1),
                ],
                Center,
                [
                    new RadialMenuChoice(LocalizedText.Raw("1"), 1),
                    new RadialMenuChoice(LocalizedText.Raw("5"), 5),
                    new RadialMenuChoice(LocalizedText.Raw("10"), 10),
                    new RadialMenuChoice(LocalizedText.Raw("All"), -1),
                ]);
            var pointer = new Pointer();
            Tap(menu, pointer, RadialMenu.LabelPoint(Center, 0, 4, RadialMenuMetrics.Default));
            Update(menu, pointer, RadialMenu.LabelPoint(Center, 1, 4, RadialMenuMetrics.Default), false);
            return menu;
        }

        void Tap(RadialMenu menu, Pointer pointer, Vector2 position)
        {
            Update(menu, pointer, position, false);
            Update(menu, pointer, position, true);
            Update(menu, pointer, position, false);
        }

        void Update(RadialMenu menu, Pointer pointer, Vector2 position, bool down)
        {
            var held = new HashSet<MouseButton>();
            if (down) held.Add(MouseButton.Left);
            (HashSet<MouseButton> pressed, HashSet<MouseButton> released) = _mouse.Advance(held);
            pointer.Update(new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                held, pressed, position, Vector2.Zero, 0f, Width, Height,
                mouseReleased: released));
            menu.Update(pointer, 1f / 60f);
        }

        static RadialMenuTheme HighlightOnlyTheme() => new()
        {
            Shadow = Vector4.Zero,
            Surface = Vector4.Zero,
            SurfaceHighlight = Vector4.Zero,
            Border = Vector4.Zero,
            BorderActive = new Vector4(1f, 1f, 1f, 1f),
            Accent = new Vector4(206f / 255f, 150f / 255f, 70f / 255f, 0.62f),
            Text = Vector4.Zero,
            TextMuted = Vector4.Zero,
            Disabled = Vector4.Zero,
            DisabledDetail = Vector4.Zero,
            Sheen = Vector4.Zero,
        };

        static RadialMenuTheme TextOnlyTheme() => new()
        {
            Shadow = Vector4.Zero,
            Surface = Vector4.Zero,
            SurfaceHighlight = Vector4.Zero,
            Border = Vector4.Zero,
            BorderActive = Vector4.Zero,
            Accent = Vector4.Zero,
            Text = Vector4.One,
            TextMuted = new Vector4(0.25f, 0.25f, 0.25f, 1f),
            Disabled = Vector4.Zero,
            DisabledDetail = Vector4.Zero,
            Sheen = Vector4.Zero,
        };

        static int CountInsideCircle(byte[] rgba, Vector2 center, float radius)
        {
            int count = 0;
            float radiusSquared = radius * radius;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    if (Vector2.DistanceSquared(new Vector2(x + 0.5f, y + 0.5f), center) > radiusSquared)
                        continue;
                    if (rgba[(y * Width + x) * 4 + 3] > 0)
                        count++;
                }
            }
            return count;
        }

        static int CountNonTransparent(byte[] rgba, int left, int top, int right, int bottom)
        {
            int count = 0;
            for (int y = top; y < bottom; y++)
                for (int x = left; x < right; x++)
                    if (rgba[(y * Width + x) * 4 + 3] > 0)
                        count++;
            return count;
        }

        static int CountBright(byte[] rgba, int left, int top, int right, int bottom)
        {
            int count = 0;
            for (int y = top; y < bottom; y++)
                for (int x = left; x < right; x++)
                    if (rgba[(y * Width + x) * 4] > 160)
                        count++;
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
