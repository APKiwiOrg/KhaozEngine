using System;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// The central <see cref="GuiTheme"/> palette + the crisp default and semantic <see cref="GuiStyle"/> presets
    /// (10.11.0). The default look is now crisp (subtle 3px corners, 1px hairline, accent-tinted), driven from
    /// <see cref="GuiTheme.Default"/>; <see cref="GuiTheme.Legacy"/> / <see cref="GuiStyle.Legacy"/> reproduce the
    /// pre-change flat blue-grey look for a one-line revert. These are headless value/contract tests.
    /// </summary>
    // The writer: Setting_the_ambient_theme_reskins_newly_built_widgets assigns GuiTheme.Default. The
    // collection had no definition until #349, so this attribute grouped nothing and the swap window stayed
    // open to every other class. GuiThemeGlobalCollection now declares it non-parallel.
    [Collection("gui-theme-global")]
    public class GuiThemeTests
    {
        [Fact]
        public void Default_theme_is_the_crisp_neutral_dark_palette()
        {
            var t = GuiTheme.Default;
            Assert.Equal(3f, t.CornerRadius);
            Assert.Equal(1f, t.BorderThickness);
            // Accent is the electric blue; surfaces are near-black.
            Assert.Equal(0.392f, t.AccentBright.X, 3);
            Assert.Equal(0.784f, t.AccentBright.Y, 3);
            Assert.True(t.Surface.X < 0.15f && t.Surface.Y < 0.15f);   // dark surface
        }

        [Fact]
        public void Default_style_is_crisp_not_flat_and_has_no_bloom()
        {
            var s = GuiStyle.Default;
            Assert.Equal(3f, s.CornerRadius);      // subtle radius
            Assert.Equal(0f, s.ShadowSize);        // no shadow
            Assert.Equal(0f, s.GlowSize);          // no glow
            Assert.Equal(GuiFill.Solid, s.FillMode); // no gradient
            Assert.Equal(1f, s.BorderThickness);   // hairline
            Assert.False(s.IsFlat);                // radius > 0, so off the old flat path
        }

        [Fact]
        public void Modern_style_is_unchanged_so_its_gpu_goldens_do_not_move()
        {
            // gui_button_glow / scene2d_modern read these exact fields; they must stay put.
            var m = GuiStyle.Modern;
            Assert.Equal(7f, m.CornerRadius);
            Assert.Equal(8f, m.ShadowSize);
            Assert.True(m.GlowSize > 0f);
            Assert.Equal(GuiFill.VerticalGradient, m.FillMode);
            // The palette Modern renders with is the legacy blue-grey (decoupled from the new crisp Default).
            Assert.Equal(GuiStyle.Legacy.Hover, m.Hover);
            Assert.Equal(GuiStyle.Legacy.Border, m.Border);
        }

        [Fact]
        public void Legacy_style_reproduces_the_old_flat_default()
        {
            var l = GuiStyle.Legacy;
            Assert.True(l.IsFlat);                 // old default was flat
            Assert.Equal(0f, l.CornerRadius);
            Assert.Equal(1.5f, l.BorderThickness); // old default thickness
            Assert.Equal(new Vector4(0.18f, 0.30f, 0.42f, 1f), l.Fill);
        }

        [Fact]
        public void Semantic_presets_are_distinct()
        {
            Assert.Equal(GuiStyle.Default.Fill, GuiStyle.Primary.Fill);   // Primary == Default
            Assert.NotEqual(GuiStyle.Primary.Fill, GuiStyle.Secondary.Fill);
            Assert.NotEqual(GuiStyle.Primary.Fill, GuiStyle.Danger.Fill);
            Assert.NotEqual(GuiStyle.Primary.Fill, GuiStyle.Active.Fill);
            // Danger reads red; Active reads bright accent text.
            Assert.True(GuiStyle.Danger.Border.X > 0.5f && GuiStyle.Danger.Border.Y < 0.4f);
            Assert.True(GuiStyle.Active.Text.Z > 0.9f);   // bright blue text
            // Presets share the crisp shape.
            Assert.Equal(3f, GuiStyle.Secondary.CornerRadius);
            Assert.False(GuiStyle.Danger.IsFlat);
        }

        [Fact]
        public void Setting_the_ambient_theme_reskins_newly_built_widgets()
        {
            var saved = GuiTheme.Default;
            try
            {
                GuiTheme.Default = GuiTheme.Legacy;
                var toggle = new Toggle(new Rect(0, 0, 40, 20));
                // Under the legacy theme a fresh widget uses the legacy off-surface, not the crisp one.
                Assert.Equal(GuiTheme.Legacy.Surface, toggle.OffColor);
            }
            finally { GuiTheme.Default = saved; }
        }

        [Fact]
        public void Primary_and_active_presets_read_every_palette_colour_from_the_theme()
        {
            GuiTheme saved = GuiTheme.Default;
            GuiStyle legacyBefore = GuiStyle.Legacy;
            GuiStyle modernBefore = GuiStyle.Modern;
            try
            {
                GuiTheme.Default = GuiTheme.Crisp with
                {
                    PrimaryFill = new Vector4(0.01f, 0.02f, 0.03f, 1f),
                    PrimaryHover = new Vector4(0.04f, 0.05f, 0.06f, 1f),
                    PrimaryPress = new Vector4(0.07f, 0.08f, 0.09f, 1f),
                    PrimaryBorder = new Vector4(0.10f, 0.11f, 0.12f, 1f),
                    PrimarySelectedFill = new Vector4(0.13f, 0.14f, 0.15f, 1f),
                    ActiveFill = new Vector4(0.16f, 0.17f, 0.18f, 1f),
                    ActiveHover = new Vector4(0.19f, 0.20f, 0.21f, 1f),
                    ActivePress = new Vector4(0.22f, 0.23f, 0.24f, 1f),
                    ActiveBorder = new Vector4(0.25f, 0.26f, 0.27f, 1f),
                    ActiveText = new Vector4(0.28f, 0.29f, 0.30f, 1f),
                    ActiveSelectedFill = new Vector4(0.31f, 0.32f, 0.33f, 1f),
                };

                GuiStyle primary = GuiStyle.Primary;
                Assert.Equal(GuiTheme.Default.PrimaryFill, primary.Fill);
                Assert.Equal(GuiTheme.Default.PrimaryHover, primary.Hover);
                Assert.Equal(GuiTheme.Default.PrimaryPress, primary.Press);
                Assert.Equal(GuiTheme.Default.PrimaryBorder, primary.Border);
                Assert.Equal(GuiTheme.Default.PrimarySelectedFill, primary.SelectedFill);

                GuiStyle active = GuiStyle.Active;
                Assert.Equal(GuiTheme.Default.ActiveFill, active.Fill);
                Assert.Equal(GuiTheme.Default.ActiveHover, active.Hover);
                Assert.Equal(GuiTheme.Default.ActivePress, active.Press);
                Assert.Equal(GuiTheme.Default.ActiveBorder, active.Border);
                Assert.Equal(GuiTheme.Default.ActiveText, active.Text);
                Assert.Equal(GuiTheme.Default.ActiveSelectedFill, active.SelectedFill);
                Assert.Equal(legacyBefore, GuiStyle.Legacy);
                Assert.Equal(modernBefore, GuiStyle.Modern);
            }
            finally
            {
                GuiTheme.Default = saved;
            }
        }

        [Fact]
        public void Primary_and_active_theme_defaults_match_the_previous_hardcoded_palettes()
        {
            foreach (GuiTheme theme in new[] { GuiTheme.Crisp, GuiTheme.Legacy })
            {
                Assert.Equal(new Vector4(0.137f, 0.216f, 0.353f, 1f), theme.PrimaryFill);
                Assert.Equal(new Vector4(0.176f, 0.275f, 0.451f, 1f), theme.PrimaryHover);
                Assert.Equal(new Vector4(0.098f, 0.157f, 0.275f, 1f), theme.PrimaryPress);
                Assert.Equal(new Vector4(0.235f, 0.353f, 0.588f, 1f), theme.PrimaryBorder);
                Assert.Equal(new Vector4(0.157f, 0.235f, 0.353f, 1f), theme.PrimarySelectedFill);
                Assert.Equal(new Vector4(0.157f, 0.235f, 0.353f, 1f), theme.ActiveFill);
                Assert.Equal(new Vector4(0.196f, 0.294f, 0.431f, 1f), theme.ActiveHover);
                Assert.Equal(new Vector4(0.118f, 0.176f, 0.275f, 1f), theme.ActivePress);
                Assert.Equal(new Vector4(0.314f, 0.549f, 0.863f, 1f), theme.ActiveBorder);
                Assert.Equal(new Vector4(0.549f, 0.784f, 1f, 1f), theme.ActiveText);
                Assert.Equal(new Vector4(0.196f, 0.294f, 0.431f, 1f), theme.ActiveSelectedFill);
            }
        }
    }
}
