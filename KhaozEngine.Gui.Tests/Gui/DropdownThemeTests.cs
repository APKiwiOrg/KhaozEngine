using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// The selected row and the keyboard-cursor row of a <see cref="Dropdown"/> come off
    /// <see cref="GuiTheme.SelectionFill"/> / <see cref="GuiTheme.FocusFill"/> at construction, like its other
    /// eight colours (#830). Both were hardcoded blue before, so a game that rebranded through the theme still
    /// got a blue selected row. The literals are pinned here as well as in the presets: the point of the change
    /// is that nobody's look moves until they theme the pair.
    /// </summary>
    // Writes the process-global GuiTheme.Default, so it belongs to the serial collection (see
    // GuiThemeGlobalCollection) and restores the previous value in a finally.
    [Collection("gui-theme-global")]
    public class DropdownThemeTests
    {
        static readonly Rect Trigger = new(100, 100, 160, 30);

        static readonly List<DropdownOption> Opts = new()
        {
            new(LocalizedText.Raw("Low"), 1), new(LocalizedText.Raw("High"), 2),
        };

        [Fact]
        public void The_default_theme_still_gives_the_old_hardcoded_blues()
        {
            var d = new Dropdown(Opts, Trigger);
            Assert.Equal(new Vector4(0.157f, 0.235f, 0.353f, 1f), d.SelectedColor);
            Assert.Equal(new Vector4(0.137f, 0.216f, 0.353f, 1f), d.FocusColor);
        }

        [Fact]
        public void Both_presets_carry_the_same_pair_so_a_legacy_revert_does_not_move_a_row()
        {
            Assert.Equal(new Vector4(0.157f, 0.235f, 0.353f, 1f), GuiTheme.Crisp.SelectionFill);
            Assert.Equal(new Vector4(0.137f, 0.216f, 0.353f, 1f), GuiTheme.Crisp.FocusFill);
            Assert.Equal(GuiTheme.Crisp.SelectionFill, GuiTheme.Legacy.SelectionFill);
            Assert.Equal(GuiTheme.Crisp.FocusFill, GuiTheme.Legacy.FocusFill);
        }

        [Fact]
        public void A_themed_pair_reaches_a_newly_built_dropdown()
        {
            var saved = GuiTheme.Default;
            try
            {
                var bronze = new Vector4(0.42f, 0.28f, 0.11f, 1f);
                var bronzeDim = new Vector4(0.31f, 0.20f, 0.08f, 1f);
                GuiTheme.Default = GuiTheme.Default with { SelectionFill = bronze, FocusFill = bronzeDim };

                var d = new Dropdown(Opts, Trigger);
                Assert.Equal(bronze, d.SelectedColor);
                Assert.Equal(bronzeDim, d.FocusColor);
            }
            finally { GuiTheme.Default = saved; }
        }

        [Fact]
        public void A_per_instance_override_still_wins_over_the_theme()
        {
            var saved = GuiTheme.Default;
            try
            {
                GuiTheme.Default = GuiTheme.Default with
                {
                    SelectionFill = new Vector4(0.42f, 0.28f, 0.11f, 1f),
                    FocusFill = new Vector4(0.31f, 0.20f, 0.08f, 1f),
                };

                var green = new Vector4(0.10f, 0.40f, 0.20f, 1f);
                var greenDim = new Vector4(0.07f, 0.30f, 0.15f, 1f);
                var d = new Dropdown(Opts, Trigger) { SelectedColor = green, FocusColor = greenDim };
                Assert.Equal(green, d.SelectedColor);
                Assert.Equal(greenDim, d.FocusColor);
            }
            finally { GuiTheme.Default = saved; }
        }
    }
}
