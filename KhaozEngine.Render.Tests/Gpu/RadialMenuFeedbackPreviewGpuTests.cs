using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Imaging;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// Writes a side-by-side offscreen proof of the game-style radial menu before recipe lock and after lock with
    /// the pointer over a different amount. Output dir: <c>KE_RADIAL_PREVIEW_DIR</c> or a temp folder.
    /// </summary>
    public sealed class RadialMenuFeedbackPreviewGpuTests
    {
        const int PanelWidth = 512;
        const int Height = 430;
        static readonly Vector2 Center = new(256f, 215f);

        [GpuFact]
        public void Captures_initial_and_locked_hover_states()
        {
            byte[] initial = Capture(lockedHover: false);
            byte[] lockedHover = Capture(lockedHover: true);
            byte[] contextMenu = CaptureContextMenu();
            byte[] combined = Combine(initial, lockedHover);
            string dir = Environment.GetEnvironmentVariable("KE_RADIAL_PREVIEW_DIR")
                ?? Path.Combine(Path.GetTempPath(), "radial-surface-feedback");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "radial-surface-feedback-preview.png");
            string contextPath = Path.Combine(dir, "radial-context-shortcuts-preview.png");

            PngWriter.Save(path, combined, PanelWidth * 2, Height);
            PngWriter.Save(contextPath, contextMenu, PanelWidth, Height);

            Assert.True(new FileInfo(path).Length > 0, $"expected a radial preview at {path}");
            Assert.True(new FileInfo(contextPath).Length > 0, $"expected a context preview at {contextPath}");
        }

        static byte[] Capture(bool lockedHover) =>
            Render2DSnapshot.Capture(PanelWidth, Height, new Color(0.055f, 0.06f, 0.07f, 1f), ctx =>
            {
                Texture2D white = ctx.CreateTexture([255, 255, 255, 255], 1, 1);
                SpriteFont font = ctx.LoadFont(
                    Path.Combine(AppContext.BaseDirectory, "Assets", "Roboto-Regular.ttf"),
                    18f,
                    oversample: 1);
                var menu = new RadialMenu
                {
                    SafeBounds = new Rect(0f, 0f, PanelWidth, Height),
                    InteractionMode = RadialMenuInteractionMode.EntryThenChoice,
                    Theme = GameTheme(),
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
                        new RadialMenuEntry(LocalizedText.Raw("Wooden light bow stave"), 4, InitialChoiceTag: -1),
                    ],
                    Center,
                    [
                        new RadialMenuChoice(LocalizedText.Raw("1"), 1),
                        new RadialMenuChoice(LocalizedText.Raw("5"), 5),
                        new RadialMenuChoice(LocalizedText.Raw("10"), 10),
                        new RadialMenuChoice(LocalizedText.Raw("All"), -1),
                    ],
                    LocalizedText.Raw("Choose quantity"));

                var pointer = new Pointer();
                var mouse = new MouseFrames();
                if (lockedHover)
                {
                    Tap(menu, pointer, mouse, RadialMenu.LabelPoint(Center, 0, 4, RadialMenuMetrics.Default));
                    Rect hover = RadialMenu.ChoiceBounds(Center, 4, 2, RadialMenuMetrics.Default);
                    Update(menu, pointer, mouse, CenterOf(hover), down: false);
                }
                else
                {
                    Update(menu, pointer, mouse, Center, down: false);
                }

                var viewport = new DesignViewport(PanelWidth, Height, ScaleMode.Fit);
                viewport.Update(PanelWidth, Height);
                ctx.Batch.Begin(viewport);
                menu.Draw(ctx.Batch, white, font);
                ctx.Batch.End();
            });

        static byte[] CaptureContextMenu() =>
            Render2DSnapshot.Capture(PanelWidth, Height, new Color(0.055f, 0.06f, 0.07f, 1f), ctx =>
            {
                Texture2D white = ctx.CreateTexture([255, 255, 255, 255], 1, 1);
                SpriteFont font = ctx.LoadFont(
                    Path.Combine(AppContext.BaseDirectory, "Assets", "Roboto-Regular.ttf"),
                    18f,
                    oversample: 1);
                RadialMenuTheme theme = GameTheme();
                var context = new ContextMenu(font, font)
                {
                    Viewport = new Vector2(PanelWidth, Height),
                    Background = theme.Surface,
                    Border = theme.Border,
                    TitleColor = theme.TextMuted,
                    TextColor = theme.Text,
                    DetailColor = theme.Text,
                    HoverColor = theme.SurfaceHighlight,
                    DisabledColor = theme.Disabled,
                };
                var menu = new RadialMenu
                {
                    SafeBounds = new Rect(0f, 0f, PanelWidth, Height),
                    InteractionMode = RadialMenuInteractionMode.EntryThenChoice,
                    Theme = theme,
                    EntryContextMenu = context,
                    QuickSelectLabel = LocalizedText.Raw("Quick craft last"),
                };
                menu.Open(
                    LocalizedText.Raw("Choose recipe"),
                    [
                        new RadialMenuEntry(LocalizedText.Raw("Arrow shafts"), 1, InitialChoiceTag: 1),
                        new RadialMenuEntry(LocalizedText.Raw("Joinery pegs"), 2, InitialChoiceTag: 5),
                        new RadialMenuEntry(LocalizedText.Raw("Wooden handle"), 3, InitialChoiceTag: 10),
                        new RadialMenuEntry(LocalizedText.Raw("Wooden light bow stave"), 4, InitialChoiceTag: -1),
                    ],
                    Center,
                    [
                        new RadialMenuChoice(LocalizedText.Raw("1"), 1),
                        new RadialMenuChoice(LocalizedText.Raw("5"), 5),
                        new RadialMenuChoice(LocalizedText.Raw("10"), 10),
                        new RadialMenuChoice(LocalizedText.Raw("All"), -1),
                    ],
                    LocalizedText.Raw("Choose quantity"));

                var pointer = new Pointer();
                var mouse = new MouseFrames();
                RightTap(menu, pointer, mouse, RadialMenu.LabelPoint(Center, 0, 4, RadialMenuMetrics.Default));

                var viewport = new DesignViewport(PanelWidth, Height, ScaleMode.Fit);
                viewport.Update(PanelWidth, Height);
                ctx.Batch.Begin(viewport);
                menu.Draw(ctx.Batch, white, font);
                ctx.Batch.End();
            });

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
                held, pressed, position, Vector2.Zero, 0f, PanelWidth, Height,
                mouseReleased: released));
            menu.Update(pointer, 1f / 60f);
        }

        static void RightTap(RadialMenu menu, Pointer pointer, MouseFrames mouse, Vector2 position)
        {
            UpdateRight(menu, pointer, mouse, position, down: false);
            UpdateRight(menu, pointer, mouse, position, down: true);
            UpdateRight(menu, pointer, mouse, position, down: false);
        }

        static void UpdateRight(RadialMenu menu, Pointer pointer, MouseFrames mouse, Vector2 position, bool down)
        {
            var held = new HashSet<MouseButton>();
            if (down) held.Add(MouseButton.Right);
            (HashSet<MouseButton> pressed, HashSet<MouseButton> released) = mouse.Advance(held);
            pointer.Update(new InputState(
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                held, pressed, position, Vector2.Zero, 0f, PanelWidth, Height,
                mouseReleased: released));
            menu.Update(pointer, 1f / 60f);
        }

        static byte[] Combine(byte[] left, byte[] right)
        {
            var combined = new byte[PanelWidth * 2 * Height * 4];
            int rowBytes = PanelWidth * 4;
            int combinedRowBytes = rowBytes * 2;
            for (int y = 0; y < Height; y++)
            {
                Buffer.BlockCopy(left, y * rowBytes, combined, y * combinedRowBytes, rowBytes);
                Buffer.BlockCopy(right, y * rowBytes, combined, y * combinedRowBytes + rowBytes, rowBytes);
            }
            return combined;
        }

        static RadialMenuTheme GameTheme() => new()
        {
            Shadow = new Vector4(0f, 0f, 0f, 0.38f),
            Surface = new Vector4(45f / 255f, 43f / 255f, 42f / 255f, 0.96f),
            SurfaceHighlight = new Vector4(45f / 255f, 43f / 255f, 42f / 255f, 0.52f),
            Border = new Vector4(150f / 255f, 108f / 255f, 62f / 255f, 1f),
            BorderActive = new Vector4(1f, 205f / 255f, 120f / 255f, 1f),
            Accent = new Vector4(206f / 255f, 150f / 255f, 70f / 255f, 0.62f),
            Text = Vector4.One,
            TextMuted = Vector4.One,
            Disabled = new Vector4(128f / 255f, 128f / 255f, 128f / 255f, 0.85f),
            DisabledDetail = new Vector4(170f / 255f, 64f / 255f, 60f / 255f, 1f),
            Sheen = new Vector4(1f, 1f, 1f, 0.08f),
        };

        static Vector2 CenterOf(Rect bounds) =>
            new(bounds.X + bounds.Width * 0.5f, bounds.Y + bounds.Height * 0.5f);
    }
}
