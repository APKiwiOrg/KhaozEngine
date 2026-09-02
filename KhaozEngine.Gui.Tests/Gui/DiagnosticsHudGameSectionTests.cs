using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// The composition seam on the turn-key HUD: a game adds its OWN section without losing the built-in
    /// Performance / Draw-stats / Pass-timings / Network ones. Reaching for
    /// <see cref="DiagnosticsOverlay.SetSectionsProvider"/> from a game replaces the engine provider outright,
    /// which is an override rather than a seam, and is why a game ended up drawing a second HUD beside this one.
    /// Also covers the boot-visible knob, since a playtest handoff asks a tester to read a line without first
    /// pressing F1.
    /// </summary>
    public sealed class DiagnosticsHudGameSectionTests
    {
        static InputState KeyFrame(params Key[] pressed) => new(
            new HashSet<Key>(pressed), new HashSet<Key>(pressed), new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton>(),
            Vector2.Zero, Vector2.Zero, 0, 1280, 720);

        static DiagnosticsOverlayTheme InstantTheme() =>
            new() { FadeSpeed = 0f, ToggleKey = Key.F1 };

        static OverlaySection Section(string title) =>
            new(title, new[] { new OverlayRow("tile", "3200, 3200") });

        static string[] Titles(DiagnosticsHud hud) => hud.Overlay.Sections.Select(s => s.Title).ToArray();

        [Fact]
        public void Added_section_composes_after_the_built_in_ones()
        {
            var hud = new DiagnosticsHud(InstantTheme(), withPassTimings: false, refreshSeconds: 0f);
            hud.AddSection(() => Section("World"));

            hud.Update(KeyFrame(Key.F1), 0.016f);

            Assert.Equal(new[] { "Performance", "Draw stats", "World" }, Titles(hud));
        }

        [Fact]
        public void Added_sections_keep_their_registration_order()
        {
            var hud = new DiagnosticsHud(InstantTheme(), withPassTimings: false, refreshSeconds: 0f);
            hud.AddSection(() => Section("World"));
            hud.AddSection(() => Section("Connection"));

            hud.Update(KeyFrame(Key.F1), 0.016f);

            Assert.Equal(new[] { "Performance", "Draw stats", "World", "Connection" }, Titles(hud));
        }

        [Fact]
        public void A_null_return_contributes_nothing_that_refresh()
        {
            OverlaySection? live = null;
            var hud = new DiagnosticsHud(InstantTheme(), withPassTimings: false, refreshSeconds: 0f);
            hud.AddSection(() => live);

            hud.Update(KeyFrame(Key.F1), 0.016f);
            Assert.DoesNotContain("World", Titles(hud));

            live = Section("World");
            hud.Update(KeyFrame(), 0.016f);
            Assert.Contains("World", Titles(hud));
        }

        [Fact]
        public void Added_sections_are_not_polled_while_hidden()
        {
            int polls = 0;
            var hud = new DiagnosticsHud(InstantTheme(), withPassTimings: false, refreshSeconds: 0f);
            hud.AddSection(() => { polls++; return Section("World"); });

            hud.Update(KeyFrame(), 0.016f);   // stays hidden: the provider short-circuits before any section work
            Assert.Equal(0, polls);
            Assert.Empty(hud.Overlay.Sections);

            hud.Update(KeyFrame(Key.F1), 0.016f);
            Assert.Equal(1, polls);
        }

        [Fact]
        public void ClearSections_drops_the_game_sections_and_keeps_the_built_ins()
        {
            var hud = new DiagnosticsHud(InstantTheme(), withPassTimings: false, refreshSeconds: 0f);
            hud.AddSection(() => Section("World"));
            hud.Update(KeyFrame(Key.F1), 0.016f);
            Assert.Contains("World", Titles(hud));

            hud.ClearSections();
            hud.Update(KeyFrame(), 0.016f);

            Assert.Equal(new[] { "Performance", "Draw stats" }, Titles(hud));
        }

        [Fact]
        public void Hud_stays_hidden_at_boot_by_default()
        {
            var hud = new DiagnosticsHud(InstantTheme(), withPassTimings: false);
            Assert.False(hud.Visible);
        }

        [Fact]
        public void Hud_can_boot_visible()
        {
            var hud = new DiagnosticsHud(InstantTheme(), withPassTimings: false, visibleAtBoot: true);
            Assert.True(hud.Visible);

            hud.Update(KeyFrame(), 0.016f);       // no toggle press: it stays up
            Assert.True(hud.Visible);

            hud.Update(KeyFrame(Key.F1), 0.016f); // and the toggle key still hides it
            Assert.False(hud.Visible);
        }
    }
}
