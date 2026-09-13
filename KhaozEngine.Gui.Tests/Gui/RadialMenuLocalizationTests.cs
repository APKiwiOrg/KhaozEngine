using System.Globalization;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    [Collection("AmbientLocalization")]
    public sealed class RadialMenuLocalizationTests
    {
        [Fact]
        public void Default_theme_uses_the_gui_palette_with_the_glass_alpha_contract()
        {
            GuiTheme palette = GuiTheme.Default;
            RadialMenuTheme theme = RadialMenuTheme.Default;

            Assert.Equal(new Vector3(palette.Background.X, palette.Background.Y, palette.Background.Z),
                new Vector3(theme.Shadow.X, theme.Shadow.Y, theme.Shadow.Z));
            Assert.Equal(new Vector3(palette.Surface.X, palette.Surface.Y, palette.Surface.Z),
                new Vector3(theme.Surface.X, theme.Surface.Y, theme.Surface.Z));
            Assert.Equal(new Vector3(palette.SurfaceHover.X, palette.SurfaceHover.Y, palette.SurfaceHover.Z),
                new Vector3(theme.SurfaceHighlight.X, theme.SurfaceHighlight.Y, theme.SurfaceHighlight.Z));
            Assert.Equal(new Vector3(palette.Border.X, palette.Border.Y, palette.Border.Z),
                new Vector3(theme.Border.X, theme.Border.Y, theme.Border.Z));
            Assert.Equal(palette.AccentBright, theme.BorderActive);
            Assert.Equal(new Vector3(palette.Accent.X, palette.Accent.Y, palette.Accent.Z),
                new Vector3(theme.Accent.X, theme.Accent.Y, theme.Accent.Z));
            Assert.Equal(palette.Text, theme.Text);
            Assert.Equal(palette.TextMuted, theme.TextMuted);
            Assert.Equal(palette.Danger, theme.DisabledDetail);

            Assert.Equal(0.38f, theme.Shadow.W);
            Assert.Equal(0.78f, theme.Surface.W);
            Assert.Equal(0.34f, theme.SurfaceHighlight.W);
            Assert.Equal(0.72f, theme.Border.W);
            Assert.Equal(0.32f, theme.Accent.W);
            Assert.Equal(0.08f, theme.Sheen.W);
        }

        [Fact]
        public void Open_resolves_player_facing_text_once_and_resolved_reads_use_the_cache()
        {
            IStringCatalog? saved = LocalizationContext.Catalog;
            var catalog = new CountingCatalog();
            LocalizationContext.Catalog = catalog;
            try
            {
                var menu = new RadialMenu { SafeBounds = new Rect(0f, 0f, 960f, 540f) };
                menu.Open(
                    new StringId("radial.title"),
                    [new RadialMenuEntry(
                        new StringId("radial.entry"),
                        101,
                        Detail: new StringId("radial.detail"),
                        InitialChoiceTag: 10)],
                    new Vector2(480f, 270f),
                    [new RadialMenuChoice(new StringId("radial.choice"), 10)]);

                Assert.Equal(4, catalog.ResolveCount);
                for (int i = 0; i < 20; i++)
                {
                    Assert.Equal("Crafting", menu.ResolvedTitle);
                    Assert.Equal("Cook", menu.ResolvedEntryLabel(0));
                    Assert.Equal("Prepare a meal", menu.ResolvedEntryDetail(0));
                    Assert.Equal("One", menu.ResolvedChoiceLabel(0));
                }
                Assert.Equal(4, catalog.ResolveCount);
            }
            finally
            {
                LocalizationContext.Catalog = saved;
            }
        }

        [Fact]
        public void Open_resolves_choice_prompt_once_and_retains_the_resolved_value()
        {
            IStringCatalog? saved = LocalizationContext.Catalog;
            var catalog = new CountingCatalog();
            LocalizationContext.Catalog = catalog;
            try
            {
                var menu = new RadialMenu
                {
                    SafeBounds = new Rect(0f, 0f, 960f, 540f),
                    QuickSelectLabel = new StringId("radial.quickSelect"),
                };
                menu.Open(
                    new StringId("radial.title"),
                    [new RadialMenuEntry(new StringId("radial.entry"), 101, InitialChoiceTag: 10)],
                    new Vector2(480f, 270f),
                    [new RadialMenuChoice(new StringId("radial.choice"), 10)],
                    new StringId("radial.choicePrompt"));

                Assert.Equal(5, catalog.ResolveCount);
                Assert.Equal("Choose quantity", menu.ResolvedChoicePrompt);
                Assert.Equal("Quick craft last", menu.ResolvedQuickSelectLabel);

                LocalizationContext.Catalog = null;
                Assert.Equal("Choose quantity", menu.ResolvedChoicePrompt);
                Assert.Equal("Quick craft last", menu.ResolvedQuickSelectLabel);
                Assert.Equal(5, catalog.ResolveCount);
            }
            finally
            {
                LocalizationContext.Catalog = saved;
            }
        }

        sealed class CountingCatalog : IStringCatalog
        {
            public int ResolveCount { get; private set; }

            public string Get(string key)
            {
                ResolveCount++;
                return key switch
                {
                    "radial.title" => "Crafting",
                    "radial.entry" => "Cook",
                    "radial.detail" => "Prepare a meal",
                    "radial.choice" => "One",
                    "radial.choicePrompt" => "Choose quantity",
                    "radial.quickSelect" => "Quick craft last",
                    _ => key,
                };
            }

            public string Format(string key, params object?[] args) =>
                IStringCatalog.SafeFormat(CultureInfo.InvariantCulture, Get(key), args);

            public bool TryGet(string key, out string value)
            {
                value = Get(key);
                return value != key;
            }
        }
    }
}
