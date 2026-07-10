using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// <see cref="PatchNotesTheme"/> derives every color from <see cref="GuiTheme.Default"/> (no hard-coded
    /// literals), mirroring <see cref="UpdateOverlayTheme"/>'s injectable-look shape. Headless value/contract
    /// tests against the crisp default palette.
    /// </summary>
    public sealed class PatchNotesThemeTests
    {
        [Fact]
        public void Chrome_colors_derive_from_the_default_gui_theme()
        {
            var theme = PatchNotesTheme.Default;
            var t = GuiTheme.Default;
            Assert.Equal((Color)t.Surface, theme.PanelFill);
            Assert.Equal((Color)t.SurfaceHover, theme.HeaderFill);
            Assert.Equal((Color)t.Text, theme.HeaderText);
            Assert.Equal((Color)t.Text, theme.BodyText);
            Assert.Equal((Color)t.TextMuted, theme.MutedText);
            Assert.Equal((Color)t.AccentBright, theme.CodeText);
        }

        [Fact]
        public void CategoryColor_is_distinct_across_the_substantive_categories()
        {
            var theme = PatchNotesTheme.Default;
            var colors = new[]
            {
                theme.CategoryColor(PatchNoteCategory.New),
                theme.CategoryColor(PatchNoteCategory.Major),
                theme.CategoryColor(PatchNoteCategory.Minor),
                theme.CategoryColor(PatchNoteCategory.Rebalance),
                theme.CategoryColor(PatchNoteCategory.Bug),
            };
            // New/Major/Minor/Rebalance/Bug are pairwise distinct. Other intentionally shares Minor's muted
            // tone (both are "no strong category" styling per the brief), so it is checked separately below
            // rather than folded into this pairwise sweep.
            for (int i = 0; i < colors.Length; i++)
            {
                for (int j = i + 1; j < colors.Length; j++)
                {
                    Assert.NotEqual(colors[i], colors[j]);
                }
            }
        }

        [Fact]
        public void Other_shares_the_muted_tone_with_minor()
        {
            var theme = PatchNotesTheme.Default;
            Assert.Equal(theme.CategoryColor(PatchNoteCategory.Minor), theme.CategoryColor(PatchNoteCategory.Other));
        }

        [Fact]
        public void CategoryColor_matches_the_expected_theme_source_per_category()
        {
            var theme = PatchNotesTheme.Default;
            var t = GuiTheme.Default;
            Assert.Equal((Color)t.Accent, theme.CategoryColor(PatchNoteCategory.New));
            Assert.Equal((Color)t.AccentBright, theme.CategoryColor(PatchNoteCategory.Major));
            Assert.Equal((Color)t.TextMuted, theme.CategoryColor(PatchNoteCategory.Minor));
            Assert.Equal((Color)t.Danger, theme.CategoryColor(PatchNoteCategory.Bug));
            Assert.Equal((Color)t.TextMuted, theme.CategoryColor(PatchNoteCategory.Other));

            // Rebalance must read as a warm tone (amber/orange family), distinct from the cool New tag and
            // from the Bug tag it sits beside. Assert the perceptual property, not a formula: red is the
            // strongest channel, blue the weakest, and green sits between them (the hallmark of a warm hue).
            Color rebalance = theme.CategoryColor(PatchNoteCategory.Rebalance);
            Assert.True(rebalance.R > rebalance.B, "Rebalance should have more red than blue to read warm.");
            Assert.True(rebalance.G > rebalance.B, "Rebalance's green should exceed its blue to read warm.");
            Assert.True(rebalance.G < rebalance.R, "Rebalance's green should stay below its red to read warm.");
            Assert.NotEqual(theme.CategoryColor(PatchNoteCategory.Bug), rebalance);
            Assert.NotEqual(theme.CategoryColor(PatchNoteCategory.New), rebalance);
        }
    }
}
