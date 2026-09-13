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
    public sealed class RadialMenuCenterTextGpuTests
    {
        const int Width = 512;
        const int Height = 430;
        static readonly Vector2 Center = new(256f, 215f);

        [GpuFact]
        public void Initial_title_wraps_inside_the_padded_center_plate()
        {
            byte[] rgba = Capture(locked: false);

            Assert.True(CountVisible(rgba, _ => true) > 40, "the initial title must remain readable");
            Assert.Equal(0, CountVisible(rgba, point =>
                Vector2.DistanceSquared(point, Center) > 48f * 48f));
        }

        [GpuFact]
        public void Locked_recipe_caption_and_localized_choice_prompt_are_distinct_and_padded()
        {
            byte[] rgba = Capture(locked: true);

            int recipePixels = CountVisible(rgba, point =>
                point.Y < Center.Y && Green(rgba, point) > 40);
            int promptPixels = CountVisible(rgba, point =>
                point.Y >= Center.Y && Red(rgba, point) > Green(rgba, point) * 2);
            int escapedCenterPixels = CountVisible(rgba, point =>
            {
                float distanceSquared = Vector2.DistanceSquared(point, Center);
                return distanceSquared > 48f * 48f && distanceSquared < 58f * 58f;
            });

            Assert.True(recipePixels > 20, $"expected a readable locked recipe caption, got {recipePixels} pixels");
            Assert.True(promptPixels > 20, $"expected a visible localized choice prompt, got {promptPixels} pixels");
            Assert.Equal(0, escapedCenterPixels);
        }

        static byte[] Capture(bool locked) =>
            Render2DSnapshot.Capture(Width, Height, Color.Transparent, ctx =>
            {
                Texture2D white = ctx.CreateTexture([255, 255, 255, 255], 1, 1);
                SpriteFont font = ctx.LoadFont(
                    System.IO.Path.Combine(System.AppContext.BaseDirectory, "Assets", "Roboto-Regular.ttf"),
                    18f,
                    oversample: 1);
                var menu = new RadialMenu
                {
                    SafeBounds = new Rect(0f, 0f, Width, Height),
                    InteractionMode = RadialMenuInteractionMode.EntryThenChoice,
                    Theme = TextTheme(),
                };
                menu.Open(
                    LocalizedText.Raw("Choose recipe"),
                    [new RadialMenuEntry(
                        locked ? LocalizedText.Raw("Wooden light bow stave") : default,
                        1,
                        InitialChoiceTag: 1)],
                    Center,
                    [new RadialMenuChoice(LocalizedText.Raw("1"), 1)],
                    LocalizedText.Raw("Choose quantity"));
                if (locked)
                {
                    var input = new InputManager();
                    input.Update(KeyFrame(Key.Enter));
                    menu.Update(input, 1f / 60f, focused: true);
                }

                var viewport = new DesignViewport(Width, Height, ScaleMode.Fit);
                viewport.Update(Width, Height);
                ctx.Batch.Begin(viewport);
                menu.Draw(ctx.Batch, white, font);
                ctx.Batch.End();
            });

        static InputState KeyFrame(Key key) => new(
            new HashSet<Key> { key },
            new HashSet<Key> { key },
            new HashSet<Key>(),
            new HashSet<MouseButton>(),
            new HashSet<MouseButton>(),
            Vector2.Zero,
            Vector2.Zero,
            0f,
            Width,
            Height);

        static RadialMenuTheme TextTheme() => new()
        {
            Shadow = Vector4.Zero,
            Surface = Vector4.Zero,
            SurfaceHighlight = Vector4.Zero,
            Border = Vector4.Zero,
            BorderActive = Vector4.Zero,
            Accent = Vector4.Zero,
            Text = Vector4.One,
            TextMuted = new Vector4(1f, 0f, 0f, 1f),
            Disabled = Vector4.Zero,
            DisabledDetail = Vector4.Zero,
            Sheen = Vector4.Zero,
        };

        static int CountVisible(byte[] rgba, System.Func<Vector2, bool> predicate)
        {
            int count = 0;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    if (rgba[(y * Width + x) * 4 + 3] == 0)
                        continue;
                    var point = new Vector2(x + 0.5f, y + 0.5f);
                    if (predicate(point)) count++;
                }
            }
            return count;
        }

        static byte Red(byte[] rgba, Vector2 point) =>
            rgba[((int)point.Y * Width + (int)point.X) * 4];

        static byte Green(byte[] rgba, Vector2 point) =>
            rgba[((int)point.Y * Width + (int)point.X) * 4 + 1];
    }
}
