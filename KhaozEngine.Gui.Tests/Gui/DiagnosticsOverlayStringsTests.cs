using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Diagnostics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.App;      // DictionaryCatalog
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// The built-in overlay text resolves through the ambient <see cref="LocalizationContext.Catalog"/> and falls
    /// back to English when no catalog carries a key. Dynamic pass names, numbers, units, app names, and versions
    /// remain raw tokens. These mutate the process-wide catalog, so they share the serialized AmbientLocalization
    /// collection.
    /// </summary>
    [Collection("AmbientLocalization")]
    public sealed class DiagnosticsOverlayStringsTests
    {
        static InputState KeyFrame(params Key[] pressed) => new(
            new HashSet<Key>(pressed), new HashSet<Key>(pressed), new HashSet<Key>(),
            new HashSet<MouseButton>(), new HashSet<MouseButton>(),
            Vector2.Zero, Vector2.Zero, 0, 1280, 720);

        static DiagnosticsHud ShownHud()
        {
            var hud = new DiagnosticsHud(new DiagnosticsOverlayTheme { FadeSpeed = 0f, ToggleKey = Key.F1 },
                false, 0f, false, () => new BuildIdentity("Game", "1.0.0"));
            hud.Update(KeyFrame(Key.F1), 0.016f);
            return hud;
        }

        [Fact]
        public void English_default_is_Build()
        {
            Assert.Equal("Build", DiagnosticsOverlayStrings.EnglishDefaults.Get(DiagnosticsOverlayStrings.BuildTitle.Key));
        }

        [Fact]
        public void English_defaults_cover_every_built_in_title_label_and_status()
        {
            IStringCatalog en = DiagnosticsOverlayStrings.EnglishDefaults;

            Assert.Equal("Performance", en.Get("diagnostics.overlay.performance.title"));
            Assert.Equal("fps", en.Get("diagnostics.overlay.performance.fps"));
            Assert.Equal("frame ms", en.Get("diagnostics.overlay.performance.frame-ms"));
            Assert.Equal("managed MB", en.Get("diagnostics.overlay.performance.managed-mb"));
            Assert.Equal("Pass timings", en.Get("diagnostics.overlay.pass-timings.title"));
            Assert.Equal("Draw stats", en.Get("diagnostics.overlay.draw-stats.title"));
            Assert.Equal("draw calls", en.Get("diagnostics.overlay.draw-stats.draw-calls"));
            Assert.Equal("instances", en.Get("diagnostics.overlay.draw-stats.instances"));
            Assert.Equal("triangles", en.Get("diagnostics.overlay.draw-stats.triangles"));
            Assert.Equal("quads", en.Get("diagnostics.overlay.draw-stats.quads"));
            Assert.Equal("flushes", en.Get("diagnostics.overlay.draw-stats.flushes"));
            Assert.Equal("tex switches", en.Get("diagnostics.overlay.draw-stats.texture-switches"));
            Assert.Equal("upload KB", en.Get("diagnostics.overlay.draw-stats.upload-kb"));
            Assert.Equal("  instances KB", en.Get("diagnostics.overlay.draw-stats.instances-kb"));
            Assert.Equal("  skinned KB", en.Get("diagnostics.overlay.draw-stats.skinned-kb"));
            Assert.Equal("  skin ubo KB", en.Get("diagnostics.overlay.draw-stats.skin-ubo-kb"));
            Assert.Equal("  sprites KB", en.Get("diagnostics.overlay.draw-stats.sprites-kb"));
            Assert.Equal("Network", en.Get("diagnostics.overlay.network.title"));
            Assert.Equal("status", en.Get("diagnostics.overlay.network.status"));
            Assert.Equal("not connected", en.Get("diagnostics.overlay.network.not-connected"));
            Assert.Equal("ping", en.Get("diagnostics.overlay.network.ping"));
            Assert.Equal("loss", en.Get("diagnostics.overlay.network.loss"));
            Assert.Equal("in/out", en.Get("diagnostics.overlay.network.in-out"));
            Assert.Equal("snapshots", en.Get("diagnostics.overlay.network.snapshots"));
            Assert.Equal("correction", en.Get("diagnostics.overlay.network.correction"));
        }

        [Fact]
        public void Wired_catalog_localizes_every_built_in_title_label_and_status()
        {
            IStringCatalog? prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = new DictionaryCatalog()
                    .Add("diagnostics.overlay.performance.title", "Rendement")
                    .Add("diagnostics.overlay.performance.fps", "ips")
                    .Add("diagnostics.overlay.performance.frame-ms", "trame ms")
                    .Add("diagnostics.overlay.performance.managed-mb", "memoire MB")
                    .Add("diagnostics.overlay.pass-timings.title", "Temps des passes")
                    .Add("diagnostics.overlay.draw-stats.title", "Stats de rendu")
                    .Add("diagnostics.overlay.draw-stats.draw-calls", "appels")
                    .Add("diagnostics.overlay.draw-stats.instances", "objets")
                    .Add("diagnostics.overlay.draw-stats.triangles", "triangles fr")
                    .Add("diagnostics.overlay.draw-stats.quads", "quads fr")
                    .Add("diagnostics.overlay.draw-stats.flushes", "vidages")
                    .Add("diagnostics.overlay.draw-stats.texture-switches", "textures")
                    .Add("diagnostics.overlay.draw-stats.upload-kb", "envoi KB")
                    .Add("diagnostics.overlay.draw-stats.instances-kb", "  objets KB")
                    .Add("diagnostics.overlay.draw-stats.skinned-kb", "  animes KB")
                    .Add("diagnostics.overlay.draw-stats.skin-ubo-kb", "  peau ubo KB")
                    .Add("diagnostics.overlay.draw-stats.sprites-kb", "  sprites fr KB")
                    .Add("diagnostics.overlay.network.title", "Reseau")
                    .Add("diagnostics.overlay.network.status", "etat")
                    .Add("diagnostics.overlay.network.not-connected", "deconnecte")
                    .Add("diagnostics.overlay.network.ping", "latence")
                    .Add("diagnostics.overlay.network.loss", "perte")
                    .Add("diagnostics.overlay.network.in-out", "entrant/sortant")
                    .Add("diagnostics.overlay.network.snapshots", "instantanes")
                    .Add("diagnostics.overlay.network.correction", "correction fr")
                    .Add("shadow", "ombre")
                    .Add("42", "quarante-deux")
                    .Add("42 ms", "quarante-deux ms");

                var frame = new FrameStats();
                frame.Sample(1f / 60f);
                OverlaySection performance = DiagnosticsOverlay.PerformanceSection(frame);
                Assert.Equal("Rendement", performance.Title);
                Assert.Equal(new[] { "ips", "trame ms", "memoire MB" }, performance.Rows.Select(r => r.Label));

                var timings = new PassTimings();
                timings.Sample("shadow", 1.25f);
                OverlaySection passes = DiagnosticsOverlay.PassTimingsSection(timings);
                Assert.Equal("Temps des passes", passes.Title);
                Assert.Equal("shadow", passes.Rows[0].Label);
                Assert.Equal("1.25/1.25/1.25", passes.Rows[0].Value);

                OverlaySection draw = DiagnosticsOverlay.DrawStatsSection(new RenderFrameStats { DrawCalls = 42 });
                Assert.Equal("Stats de rendu", draw.Title);
                Assert.Equal(new[]
                {
                    "appels", "objets", "triangles fr", "quads fr", "vidages", "textures", "envoi KB",
                    "  objets KB", "  animes KB", "  peau ubo KB", "  sprites fr KB",
                }, draw.Rows.Select(r => r.Label));
                Assert.Equal("42", draw.Rows[0].Value);

                OverlaySection disconnected = DiagnosticsOverlay.NetworkSection(new ClientNetStats());
                Assert.Equal("Reseau", disconnected.Title);
                Assert.Equal(new OverlayRow("etat", "deconnecte"), disconnected.Rows[0]);

                OverlaySection network = DiagnosticsOverlay.NetworkSection(new ClientNetStats
                {
                    Connected = true,
                    RttMs = 42f,
                });
                Assert.Equal(new[] { "latence", "perte", "entrant/sortant", "instantanes", "correction fr" },
                    network.Rows.Select(r => r.Label));
                Assert.Equal("42 ms", network.Rows[0].Value);
            }
            finally { LocalizationContext.Catalog = prev; }
        }

        [Fact]
        public void No_catalog_resolves_the_english_title()
        {
            IStringCatalog? prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = null;
                Assert.Equal("Build", ShownHud().Overlay.Sections.Last().Title);
            }
            finally { LocalizationContext.Catalog = prev; }
        }

        [Fact]
        public void Wired_catalog_localizes_the_title_and_leaves_the_tokens_raw()
        {
            IStringCatalog? prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = new DictionaryCatalog()
                    .Add("diagnostics.overlay.build.title", "Compilation")
                    .Add("Game", "Jeu")
                    .Add("1.0.0", "un");

                OverlaySection build = ShownHud().Overlay.Sections.Last();

                Assert.Equal("Compilation", build.Title);
                Assert.Equal(new OverlayRow("Game", "1.0.0"), build.Rows[0]);
            }
            finally { LocalizationContext.Catalog = prev; }
        }

        [Fact]
        public void Wired_catalog_missing_the_key_falls_back_to_english()
        {
            IStringCatalog? prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = new DictionaryCatalog().Add("menu.play", "Jouer");
                Assert.Equal("Build", ShownHud().Overlay.Sections.Last().Title);
            }
            finally { LocalizationContext.Catalog = prev; }
        }

        [Fact]
        public void A_locale_switch_rebuilds_the_title_on_the_next_refresh()
        {
            IStringCatalog? prev = LocalizationContext.Catalog;
            try
            {
                LocalizationContext.Catalog = null;
                DiagnosticsHud hud = ShownHud();
                Assert.Equal("Build", hud.Overlay.Sections.Last().Title);

                LocalizationContext.Catalog = new DictionaryCatalog().Add("diagnostics.overlay.build.title", "Compilation");
                hud.Update(KeyFrame(), 0.016f);

                Assert.Equal("Compilation", hud.Overlay.Sections.Last().Title);
            }
            finally { LocalizationContext.Catalog = prev; }
        }
    }
}
