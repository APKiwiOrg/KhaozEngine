using KhaozEngine.Game;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Game
{
    // The GameAppOptions knob that governs the built-in diagnostics HUD: enabled by default with F1, opt-out flag,
    // and a custom toggle key. BuildDiagnosticsTheme is the pure decision, so the precedence is headless-testable
    // without standing up a window.
    public sealed class GameAppDiagnosticsTests
    {
        [Fact]
        public void Enabled_by_default_with_f1()
        {
            var opts = GameAppOptions.For("t", 640, 480);
            DiagnosticsOverlayTheme? theme = GameApp.BuildDiagnosticsTheme(opts);
            Assert.NotNull(theme);
            Assert.Equal(Key.F1, theme!.ToggleKey);
        }

        [Fact]
        public void Default_constructed_options_also_default_to_f1()
        {
            // A raw `new GameAppOptions { ... }` (no For) must still enable the HUD on F1 - the disable flag and the
            // nullable toggle key are both chosen so the default-zero struct keeps the on-with-F1 behaviour.
            DiagnosticsOverlayTheme? theme = GameApp.BuildDiagnosticsTheme(default);
            Assert.NotNull(theme);
            Assert.Equal(Key.F1, theme!.ToggleKey);
        }

        [Fact]
        public void Disable_flag_opts_out()
        {
            var opts = GameAppOptions.For("t", 640, 480);
            opts.DisableDiagnosticsOverlay = true;
            Assert.Null(GameApp.BuildDiagnosticsTheme(opts));
        }

        [Fact]
        public void Custom_toggle_key_overrides_the_default()
        {
            var opts = GameAppOptions.For("t", 640, 480);
            opts.DiagnosticsToggleKey = Key.F3;
            DiagnosticsOverlayTheme? theme = GameApp.BuildDiagnosticsTheme(opts);
            Assert.NotNull(theme);
            Assert.Equal(Key.F3, theme!.ToggleKey);
        }
    }
}
