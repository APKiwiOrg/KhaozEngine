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

        [GpuFact]
        public void Quick_select_preview_changes_with_hover_and_restores_locked_prompt_on_release()
        {
            byte[] locked = CaptureQuickPreview(PreviewState.Locked);
            byte[] firstHover = CaptureQuickPreview(PreviewState.FirstHover);
            byte[] changedHover = CaptureQuickPreview(PreviewState.ChangedHover);
            byte[] disabledHover = CaptureQuickPreview(PreviewState.DisabledHover);
            byte[] released = CaptureQuickPreview(PreviewState.Released);

            Assert.False(BuffersEqual(locked, firstHover), "Shift must replace the locked prompt with a preview");
            Assert.False(BuffersEqual(firstHover, changedHover), "the preview must follow the hovered recipe");
            Assert.False(BuffersEqual(firstHover, disabledHover), "an unavailable recipe must show its requirement");
            Assert.True(BuffersEqual(locked, released), "releasing Shift must restore the locked prompt exactly");
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

        static byte[] CaptureQuickPreview(PreviewState state) =>
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
                    QuickSelectLabel = LocalizedText.Raw("Quick craft last"),
                    Theme = TransparentTheme(),
                };
                menu.Open(
                    LocalizedText.Raw("Choose recipe"),
                    [
                        new RadialMenuEntry(LocalizedText.Raw("Arrow shafts"), 1, InitialChoiceTag: 1),
                        new RadialMenuEntry(LocalizedText.Raw("Joinery pegs"), 2, InitialChoiceTag: 5),
                        new RadialMenuEntry(
                            LocalizedText.Raw("Wooden handle"),
                            3,
                            Enabled: false,
                            Detail: LocalizedText.Raw("Requires level 4"),
                            InitialChoiceTag: 1),
                    ],
                    Center,
                    [
                        new RadialMenuChoice(LocalizedText.Raw("1"), 1),
                        new RadialMenuChoice(LocalizedText.Raw("5"), 5),
                    ],
                    LocalizedText.Raw("Choose quantity"));

                var pointer = new Pointer();
                var mouse = new MouseFrames();
                Tap(menu, pointer, mouse, EntryPoint(0));
                var viewport = new DesignViewport(Width, Height, ScaleMode.Fit);
                viewport.Update(Width, Height);

                // Prime the retained centre cache invisibly. Every following state differs only by the modifier
                // or hover supplied to Update, so the visible pass proves those changes invalidate the cache.
                ctx.Batch.Begin(viewport);
                menu.Draw(ctx.Batch, white, font);
                ctx.Batch.End();

                switch (state)
                {
                    case PreviewState.FirstHover:
                        Update(menu, pointer, mouse, EntryPoint(1), quickSelect: true);
                        break;
                    case PreviewState.ChangedHover:
                        Update(menu, pointer, mouse, EntryPoint(1), quickSelect: true);
                        DrawInvisible(menu, ctx, viewport, white, font);
                        Update(menu, pointer, mouse, EntryPoint(0), quickSelect: true);
                        break;
                    case PreviewState.DisabledHover:
                        Update(menu, pointer, mouse, EntryPoint(2), quickSelect: true);
                        break;
                    case PreviewState.Released:
                        Update(menu, pointer, mouse, EntryPoint(1), quickSelect: true);
                        DrawInvisible(menu, ctx, viewport, white, font);
                        Update(menu, pointer, mouse, EntryPoint(1), quickSelect: false);
                        break;
                }

                menu.Theme = TextTheme();
                ctx.Batch.Begin(viewport);
                menu.Draw(ctx.Batch, white, font);
                ctx.Batch.End();
            });

        static void DrawInvisible(
            RadialMenu menu,
            Render2DContext ctx,
            DesignViewport viewport,
            Texture2D white,
            SpriteFont font)
        {
            ctx.Batch.Begin(viewport);
            menu.Draw(ctx.Batch, white, font);
            ctx.Batch.End();
        }

        static void Tap(RadialMenu menu, Pointer pointer, MouseFrames mouse, Vector2 position)
        {
            Update(menu, pointer, mouse, position, quickSelect: false, down: false);
            Update(menu, pointer, mouse, position, quickSelect: false, down: true);
            Update(menu, pointer, mouse, position, quickSelect: false, down: false);
        }

        static void Update(
            RadialMenu menu,
            Pointer pointer,
            MouseFrames mouse,
            Vector2 position,
            bool quickSelect,
            bool down = false)
        {
            var held = new HashSet<MouseButton>();
            if (down) held.Add(MouseButton.Left);
            (HashSet<MouseButton> pressed, HashSet<MouseButton> released) = mouse.Advance(held);
            pointer.Update(new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                held, pressed, position, Vector2.Zero, 0f, Width, Height,
                mouseReleased: released));
            menu.Update(pointer, 1f / 60f, quickSelect);
        }

        static Vector2 EntryPoint(int index) =>
            RadialMenu.LabelPoint(Center, index, 3, RadialMenuMetrics.Default);

        static bool BuffersEqual(byte[] left, byte[] right)
        {
            if (left.Length != right.Length)
                return false;
            for (int i = 0; i < left.Length; i++)
                if (left[i] != right[i])
                    return false;
            return true;
        }

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
            DisabledDetail = new Vector4(0f, 0.35f, 1f, 1f),
            Sheen = Vector4.Zero,
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
            DisabledDetail = Vector4.Zero,
            Sheen = Vector4.Zero,
        };

        enum PreviewState
        {
            Locked,
            FirstHover,
            ChangedHover,
            DisabledHover,
            Released,
        }

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
