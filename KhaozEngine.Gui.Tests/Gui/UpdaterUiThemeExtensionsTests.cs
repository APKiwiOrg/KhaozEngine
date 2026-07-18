using System;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Updates;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// <see cref="UpdaterUiThemeExtensions.ToUpdaterUiOptions"/> derives the shim window palette from an
    /// <see cref="UpdateOverlayTheme"/> (accent = ProgressFill, background = PanelFill, text = BodyText), so a
    /// game configures the in-game overlay and the native apply window from one palette.
    /// </summary>
    public sealed class UpdaterUiThemeExtensionsTests
    {
        [Fact]
        public void Derives_palette_from_theme_colours()
        {
            var theme = new UpdateOverlayTheme
            {
                ProgressFill = Color.FromBytes(80, 160, 255, 230),
                PanelFill = Color.FromBytes(12, 16, 28, 230),
                BodyText = Color.FromBytes(180, 190, 210),
            };

            UpdaterUiOptions ui = theme.ToUpdaterUiOptions();

            Assert.Equal<(byte, byte, byte)?>((80, 160, 255), ui.AccentColor);   // ProgressFill, alpha dropped
            Assert.Equal<(byte, byte, byte)?>((12, 16, 28), ui.BackgroundColor); // PanelFill
            Assert.Equal<(byte, byte, byte)?>((180, 190, 210), ui.TextColor);    // BodyText
        }

        [Fact]
        public void Reproduces_ruinborne_hand_synced_palette()
        {
            // Ruinborne's RuinborneUpdateTheme + its Program.cs UpdaterUiOptions block used to be hand-synced.
            // The derived palette must match the values the game wrote by hand.
            var accent = Color.FromBytes(153, 179, 217);
            var theme = new UpdateOverlayTheme
            {
                ProgressFill = accent,
                PanelFill = Color.FromBytes(13, 16, 22, 236),
                BodyText = Color.FromBytes(230, 235, 242),
            };

            UpdaterUiOptions ui = theme.ToUpdaterUiOptions(
                windowTitle: "Ruinborne",
                logoPath: "Content/WindowIcon/icon_128.png",
                installingText: "Installing update",
                finishingText: "Finishing up");

            Assert.Equal<(byte, byte, byte)?>((153, 179, 217), ui.AccentColor);
            Assert.Equal<(byte, byte, byte)?>((13, 16, 22), ui.BackgroundColor);
            Assert.Equal<(byte, byte, byte)?>((230, 235, 242), ui.TextColor);
            Assert.Equal("Ruinborne", ui.WindowTitle);
            Assert.Equal("Content/WindowIcon/icon_128.png", ui.LogoPath);
            Assert.Equal("Installing update", ui.InstallingText);
            Assert.Equal("Finishing up", ui.FinishingText);
        }

        [Fact]
        public void Text_fields_default_to_null_when_not_supplied()
        {
            UpdaterUiOptions ui = UpdateOverlayTheme.Default.ToUpdaterUiOptions();
            Assert.Null(ui.WindowTitle);
            Assert.Null(ui.Heading);
            Assert.Null(ui.LogoPath);
            Assert.Null(ui.InstallingText);
            Assert.Null(ui.FinishingText);
            Assert.Null(ui.DownloadingText);
            // The default theme still yields a fully-populated palette.
            Assert.NotNull(ui.AccentColor);
            Assert.NotNull(ui.BackgroundColor);
            Assert.NotNull(ui.TextColor);
        }

        [Fact]
        public void ToRgb_rounds_and_clamps_each_channel()
        {
            Assert.Equal((0, 128, 255), ToTuple(UpdaterUiThemeExtensions.ToRgb(new Vector4(0f, 0.5f, 1f, 1f))));
            // Out-of-range channels clamp rather than overflow the byte cast.
            Assert.Equal((0, 255, 255), ToTuple(UpdaterUiThemeExtensions.ToRgb(new Vector4(-1f, 2f, 1f, 1f))));
        }

        [Fact]
        public void Null_theme_throws()
            => Assert.Throws<ArgumentNullException>(() => ((UpdateOverlayTheme)null!).ToUpdaterUiOptions());

        static (int, int, int) ToTuple((byte R, byte G, byte B) c) => (c.R, c.G, c.B);
    }
}
