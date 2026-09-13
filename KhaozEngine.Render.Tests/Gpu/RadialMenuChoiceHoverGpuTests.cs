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
    public sealed class RadialMenuChoiceHoverGpuTests
    {
        const int Width = 512;
        const int Height = 430;
        static readonly Vector2 Center = new(256f, 215f);
        static readonly RadialMenuMetrics Metrics = RadialMenuMetrics.Default;

        [GpuFact]
        public void Actual_hover_is_brighter_than_both_the_normal_and_remembered_amounts()
        {
            byte[] normal = Capture(FooterState.Normal);
            byte[] hover = Capture(FooterState.PointerHover);

            Rgba normalTen = ButtonFill(normal, 2);
            Rgba hoveredTen = ButtonFill(hover, 2);
            Rgba rememberedOne = ButtonFill(hover, 0);
            Rgba normalBorder = ButtonBorder(hover, 1);
            Rgba rememberedBorder = ButtonBorder(hover, 0);
            Rgba hoveredBorder = ButtonBorder(hover, 2);

            Assert.NotEqual(normalTen, hoveredTen);
            Assert.True(Brightness(hoveredTen) > Brightness(normalTen),
                $"hovered amount should brighten, got {hoveredTen} over {normalTen}");
            Assert.True(Brightness(hoveredTen) > Brightness(rememberedOne),
                $"hovered amount should be stronger than remembered amount, got {hoveredTen} and {rememberedOne}");
            Assert.NotEqual(normalBorder, rememberedBorder);
            Assert.NotEqual(rememberedBorder, hoveredBorder);
        }

        [GpuFact]
        public void Keyboard_focus_uses_the_same_amount_emphasis_as_pointer_hover()
        {
            Rgba hovered = ButtonFill(Capture(FooterState.PointerHover), 2);
            Rgba focused = ButtonFill(Capture(FooterState.KeyboardFocus), 2);

            Assert.Equal(hovered, focused);
        }

        static byte[] Capture(FooterState state) =>
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
                    Theme = GameTheme(),
                };
                menu.Open(
                    LocalizedText.Raw("Choose recipe"),
                    [new RadialMenuEntry(LocalizedText.Raw("Arrow shafts"), 1, InitialChoiceTag: 1)],
                    Center,
                    Choices(),
                    LocalizedText.Raw("Choose quantity"));

                if (state == FooterState.KeyboardFocus)
                {
                    var input = new InputManager();
                    Send(menu, input, Key.Enter);
                    Send(menu, input, Key.Down);
                    Send(menu, input, Key.Right);
                    Send(menu, input, Key.Right);
                }
                else
                {
                    var pointer = new Pointer();
                    var mouse = new MouseFrames();
                    Tap(menu, pointer, mouse, RadialMenu.LabelPoint(Center, 0, 1, Metrics));
                    Vector2 point = state == FooterState.PointerHover
                        ? CenterOf(RadialMenu.ChoiceBounds(Center, 4, 2, Metrics))
                        : Center;
                    Update(menu, pointer, mouse, point, down: false);
                }

                var viewport = new DesignViewport(Width, Height, ScaleMode.Fit);
                viewport.Update(Width, Height);
                ctx.Batch.Begin(viewport);
                menu.Draw(ctx.Batch, white, font);
                ctx.Batch.End();
            });

        static RadialMenuChoice[] Choices() =>
        [
            new(LocalizedText.Raw("1"), 1),
            new(LocalizedText.Raw("5"), 5),
            new(LocalizedText.Raw("10"), 10),
            new(LocalizedText.Raw("All"), -1),
        ];

        static void Send(RadialMenu menu, InputManager input, Key key)
        {
            input.Update(KeyFrame(key));
            menu.Update(input, 1f / 60f, focused: true);
            input.Update(KeyFrame(null));
            menu.Update(input, 1f / 60f, focused: true);
        }

        static InputState KeyFrame(Key? key)
        {
            var held = new HashSet<Key>();
            if (key is { } value) held.Add(value);
            return new InputState(
                held,
                key is null ? new HashSet<Key>() : new HashSet<Key> { key.Value },
                new HashSet<Key>(),
                new HashSet<MouseButton>(),
                new HashSet<MouseButton>(),
                Vector2.Zero,
                Vector2.Zero,
                0f,
                Width,
                Height);
        }

        static void Tap(RadialMenu menu, Pointer pointer, MouseFrames mouse, Vector2 position)
        {
            Update(menu, pointer, mouse, position, down: false);
            Update(menu, pointer, mouse, position, down: true);
            Update(menu, pointer, mouse, position, down: false);
        }

        static void Update(RadialMenu menu, Pointer pointer, MouseFrames mouse, Vector2 position, bool down)
        {
            var held = new HashSet<MouseButton>();
            if (down) held.Add(MouseButton.Left);
            (HashSet<MouseButton> pressed, HashSet<MouseButton> released) = mouse.Advance(held);
            pointer.Update(new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                held, pressed, position, Vector2.Zero, 0f, Width, Height,
                mouseReleased: released));
            menu.Update(pointer, 1f / 60f);
        }

        static RadialMenuTheme GameTheme() => new()
        {
            Shadow = Vector4.Zero,
            Surface = new Vector4(45f / 255f, 43f / 255f, 42f / 255f, 0.96f),
            SurfaceHighlight = new Vector4(45f / 255f, 43f / 255f, 42f / 255f, 0.52f),
            Border = new Vector4(150f / 255f, 108f / 255f, 62f / 255f, 1f),
            BorderActive = new Vector4(1f, 205f / 255f, 120f / 255f, 1f),
            Accent = new Vector4(206f / 255f, 150f / 255f, 70f / 255f, 0.62f),
            Text = Vector4.One,
            TextMuted = Vector4.One,
            Disabled = new Vector4(128f / 255f, 128f / 255f, 128f / 255f, 0.85f),
            DisabledDetail = new Vector4(170f / 255f, 64f / 255f, 60f / 255f, 1f),
            Sheen = Vector4.Zero,
        };

        static Rgba ButtonFill(byte[] rgba, int index)
        {
            Rect bounds = RadialMenu.ChoiceBounds(Center, 4, index, Metrics);
            int x = (int)bounds.X + 7;
            int y = (int)bounds.Y + 7;
            int offset = (y * Width + x) * 4;
            return new Rgba(rgba[offset], rgba[offset + 1], rgba[offset + 2], rgba[offset + 3]);
        }

        static Rgba ButtonBorder(byte[] rgba, int index)
        {
            Rect bounds = RadialMenu.ChoiceBounds(Center, 4, index, Metrics);
            int x = (int)bounds.X;
            int y = (int)(bounds.Y + bounds.Height * 0.5f);
            int offset = (y * Width + x) * 4;
            return new Rgba(rgba[offset], rgba[offset + 1], rgba[offset + 2], rgba[offset + 3]);
        }

        static int Brightness(Rgba color) => color.R + color.G + color.B;

        static Vector2 CenterOf(Rect bounds) =>
            new(bounds.X + bounds.Width * 0.5f, bounds.Y + bounds.Height * 0.5f);

        enum FooterState { Normal, PointerHover, KeyboardFocus }

        readonly record struct Rgba(byte R, byte G, byte B, byte A);
    }
}
