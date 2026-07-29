using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Game
{
    // GameApp3D.OnRenderWorld drives Scene3D.EnableTiming from the built-in diagnostics overlay, but the render loop
    // itself needs a real window (mirrors GameAppResumeTests' note on GameApp.Run), so the DECISION is factored into
    // the pure GameApp3D.DesiredEnableTiming helper and tested headlessly here, exactly like GameApp.ShouldRaiseResume.
    // Issue #404: with the overlay opted out (DisableDiagnosticsOverlay = true), OnRenderWorld's hud is null, and the
    // old unconditional `EnableTiming = hud is { Visible: true }` forced the flag false every frame, silently
    // overwriting whatever a consumer with no hud set on EnableTiming itself.
    public sealed class GameApp3DTimingTests
    {
        static InputState KeyFrame(params Key[] pressed) => new(
            new HashSet<Key>(pressed), new HashSet<Key>(pressed), new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton>(),
            Vector2.Zero, Vector2.Zero, 0, 1280, 720);

        static DiagnosticsOverlayTheme InstantTheme() =>
            new() { FadeSpeed = 0f, ToggleKey = Key.F1 };   // no fade so Visible == drawable immediately

        [Fact]
        public void No_hud_leaves_the_decision_alone()
        {
            // hud is null exactly when DisableDiagnosticsOverlay opted the built-in overlay out (GameApp.Diagnostics).
            Assert.Null(GameApp3D.DesiredEnableTiming(null));
        }

        [Fact]
        public void Visible_hud_drives_timing_on()
        {
            var hud = new DiagnosticsHud(InstantTheme(), withPassTimings: true, refreshSeconds: 0f);
            hud.Update(KeyFrame(Key.F1), 0.016f);   // toggle on
            Assert.True(hud.Visible);

            Assert.Equal(true, GameApp3D.DesiredEnableTiming(hud));
        }

        [Fact]
        public void Hidden_hud_drives_timing_off()
        {
            var hud = new DiagnosticsHud(InstantTheme(), withPassTimings: true, refreshSeconds: 0f);
            Assert.False(hud.Visible);   // never toggled on

            Assert.Equal(false, GameApp3D.DesiredEnableTiming(hud));
        }

        // The regression itself: with no hud, an externally set EnableTiming must survive the decision (a consumer
        // driving the scene's own timing while the built-in overlay is disabled must not be silently overwritten,
        // which is exactly what `if (DesiredEnableTiming(hud) is { } enableTiming) Scene.EnableTiming = enableTiming;`
        // in OnRenderWorld relies on).
        [Fact]
        public void With_no_hud_an_externally_set_flag_survives_the_decision()
        {
            bool? decision = GameApp3D.DesiredEnableTiming(null);

            bool externallySetEnableTiming = true;
            if (decision is { } d) externallySetEnableTiming = d;   // OnRenderWorld's exact gating

            Assert.True(externallySetEnableTiming);
        }
    }
}
