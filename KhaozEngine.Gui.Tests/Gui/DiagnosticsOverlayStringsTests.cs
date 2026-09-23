using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Tests.App;      // DictionaryCatalog
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// The Build section's title is player-facing text: it resolves through the ambient
    /// <see cref="LocalizationContext.Catalog"/> against <see cref="DiagnosticsOverlayStrings.BuildTitle"/> and falls
    /// back to the built-in English when no catalog carries the key. The name and version under it are raw tokens and
    /// never pass through the catalog. These mutate the process-wide catalog, so they share the serialized
    /// AmbientLocalization collection.
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
