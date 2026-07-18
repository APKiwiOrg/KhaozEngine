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
    }
}
