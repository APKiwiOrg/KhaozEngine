using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    // Headless coverage for the turn-key frame-cost HUD: the Draw-stats section content, the overlay toggle, and the
    // throttled sections provider (short-circuit while hidden, and which sections it assembles when shown). Drawing
    // needs a GPU and lives in a GpuFact. Everything here is device-free.
    public sealed class DiagnosticsHudTests
    {
        static InputState KeyFrame(params Key[] pressed) => new(
            new HashSet<Key>(pressed), new HashSet<Key>(pressed), new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton>(),
            Vector2.Zero, Vector2.Zero, 0, 1280, 720);

        static DiagnosticsOverlayTheme InstantTheme() =>
            new() { FadeSpeed = 0f, ToggleKey = Key.F1 };   // no fade so Visible == drawable immediately

        static string Value(OverlaySection s, string label) => s.Rows.First(r => r.Label == label).Value;

        [Fact]
        public void DrawStatsSection_formats_every_counter()
        {
            var stats = new RenderFrameStats
            {
                DrawCalls = 5, Instances = 40, Triangles = 1234,
                Quads = 12, Flushes = 3, TextureSwitches = 2,
            };
            // Through the recording helpers, so the section is fed a tally whose split really does sum to the total
            // (1024 + 3072 + 512 + 512 = 5120 bytes = 5 KB).
            stats.AddInstanceUpload(1024);
            stats.AddSkinnedUpload(3072);
            stats.AddSkinnedUniformUpload(512);
            stats.AddSpriteUpload(512);

            OverlaySection sec = DiagnosticsOverlay.DrawStatsSection(stats);

            Assert.Equal("Draw stats", sec.Title);
            Assert.Equal("5", Value(sec, "draw calls"));
            Assert.Equal("40", Value(sec, "instances"));
            Assert.Equal("1,234", Value(sec, "triangles"));   // thousands separator
            Assert.Equal("12", Value(sec, "quads"));
            Assert.Equal("3", Value(sec, "flushes"));
            Assert.Equal("2", Value(sec, "tex switches"));
            Assert.Equal("5.0", Value(sec, "upload KB"));      // 5120 / 1024
            Assert.Equal("1.0", Value(sec, "  instances KB"));
            Assert.Equal("3.0", Value(sec, "  skinned KB"));
            Assert.Equal("0.5", Value(sec, "  skin ubo KB"));
            Assert.Equal("0.5", Value(sec, "  sprites KB"));
        }

        [Fact]
        public void Overlay_toggles_on_the_configured_key()
        {
            var overlay = new DiagnosticsOverlay(InstantTheme());
            Assert.False(overlay.Visible);

            overlay.Update(KeyFrame(Key.F1), 0.016f);
            Assert.True(overlay.Visible);

            overlay.Update(KeyFrame(), 0.016f);       // no press -> stays
            Assert.True(overlay.Visible);

            overlay.Update(KeyFrame(Key.F1), 0.016f); // press again -> hide
            Assert.False(overlay.Visible);
        }

        [Fact]
        public void Provider_builds_no_sections_while_hidden()
        {
            var hud = new DiagnosticsHud(InstantTheme(), withPassTimings: false, refreshSeconds: 0f);

            hud.Update(KeyFrame(), 0.016f);   // stays hidden, provider polls but must short-circuit

            Assert.False(hud.Visible);
            Assert.Empty(hud.Overlay.Sections);
        }

        [Fact]
        public void Provider_builds_performance_and_draw_sections_when_shown()
        {
            var hud = new DiagnosticsHud(InstantTheme(), withPassTimings: false, refreshSeconds: 0f);
            hud.SetDrawStats(new RenderFrameStats { DrawCalls = 7, Quads = 100 });

            hud.Update(KeyFrame(Key.F1), 0.016f);   // show + poll the provider this frame

            Assert.True(hud.Visible);
            var titles = hud.Overlay.Sections.Select(s => s.Title).ToArray();
            Assert.Equal(new[] { "Performance", "Draw stats" }, titles);
            Assert.Equal("7", Value(hud.Overlay.Sections[1], "draw calls"));
        }

        [Fact]
        public void Pass_timings_section_appears_only_after_the_meter_is_sampled()
        {
            var hud = new DiagnosticsHud(InstantTheme(), withPassTimings: true, refreshSeconds: 0f);

            hud.Update(KeyFrame(Key.F1), 0.016f);   // shown, but the pass meter has no samples yet
            Assert.DoesNotContain("Pass timings", hud.Overlay.Sections.Select(s => s.Title));

            hud.PassTimings!.Sample("model", 1.5f);
            hud.Update(KeyFrame(), 0.016f);          // re-poll (still visible)
            Assert.Contains("Pass timings", hud.Overlay.Sections.Select(s => s.Title));
        }

        [Fact]
        public void Network_section_tracks_the_registered_source()
        {
            ClientNetStats? net = null;
            var hud = new DiagnosticsHud(InstantTheme(), withPassTimings: false, refreshSeconds: 0f);
            hud.SetNetStatsSource(() => net);

            hud.Update(KeyFrame(Key.F1), 0.016f);    // shown, source returns null -> no Network section
            Assert.DoesNotContain("Network", hud.Overlay.Sections.Select(s => s.Title));

            net = new ClientNetStats { Connected = true, RttMs = 42f };
            hud.Update(KeyFrame(), 0.016f);
            Assert.Contains("Network", hud.Overlay.Sections.Select(s => s.Title));
        }

        [Fact]
        public void Update_samples_fps_from_the_raw_delta()
        {
            var hud = new DiagnosticsHud(InstantTheme(), withPassTimings: false);
            for (int i = 0; i < 120; i++) hud.Update(KeyFrame(), 1f / 60f);
            Assert.InRange(hud.FrameStats.Fps, 55f, 65f);
        }
    }
}
