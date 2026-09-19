using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// The flat draw mode: which colour each tab state picks and where its label lands. A
    /// <see cref="KhaozEngine.Render2D.SpriteBatch"/> needs a GPU device, so the queued quads themselves are not
    /// headless evidence in this assembly. Both halves the draw can get wrong are therefore factored into pure
    /// members the draw itself calls, the way <c>TabBar.ResolveVisual</c> already was for the button draw, and
    /// asserted here.
    /// </summary>
    // Writes the process-global GuiTheme.Default in one fact, so it belongs to the serial collection (see
    // GuiThemeGlobalCollection) and restores the previous value in a finally.
    [Collection("gui-theme-global")]
    public class TabBarFlatDrawTests
    {
        static readonly Rect Band = new(100f, 100f, 400f, 60f);

        // A palette of its own rather than the ambient one, so a colour assertion says which slot was read
        // instead of re-deriving whatever the default happens to be.
        static readonly GuiTheme Palette = GuiTheme.Crisp with
        {
            TabFill = new Vector4(0.20f, 0.10f, 0.05f, 1f),
            TabActiveFill = new Vector4(0.42f, 0.28f, 0.11f, 1f),
            SurfaceHover = new Vector4(0.31f, 0.20f, 0.08f, 1f),
            SurfacePress = new Vector4(0.11f, 0.06f, 0.03f, 1f),
            SurfaceDisabled = new Vector4(0.09f, 0.09f, 0.10f, 1f),
            BorderShadow = new Vector4(0.05f, 0.04f, 0.02f, 1f),
            BorderHover = new Vector4(0.62f, 0.45f, 0.20f, 1f),
            BorderDisabled = new Vector4(0.15f, 0.15f, 0.17f, 1f),
            Text = new Vector4(0.95f, 0.93f, 0.88f, 1f),
            TextMuted = new Vector4(0.60f, 0.56f, 0.50f, 1f),
            TextDisabled = new Vector4(0.30f, 0.29f, 0.27f, 1f),
        };

        readonly MouseFrames _mouse = new();

        InputState Frame(Vector2 pos, bool down)
        {
            var buttons = new HashSet<MouseButton>();
            if (down) buttons.Add(MouseButton.Left);
            var (edgePressed, edgeReleased) = _mouse.Advance(buttons);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                buttons, edgePressed, pos, Vector2.Zero, 0, 960, 540, mouseReleased: edgeReleased);
        }

        static Vector2 CentreOf(Rect r) => new(r.X + r.Width * 0.5f, r.Y + r.Height * 0.5f);

        static TabBar NewStrip()
        {
            var items = new[]
            {
                new TabBarItem(LocalizedText.Raw("One")),
                new TabBarItem(LocalizedText.Raw("Two")),
                new TabBarItem(LocalizedText.Raw("Soon"), Enabled: false),
            };
            return new TabBar(items, font: null, Band)
            {
                TabWidth = 56f,
                TabHeight = 26f,
                Columns = 5,
                Spacing = 4f,
                DrawMode = TabBarDrawMode.Flat,
                FlatTheme = Palette,
            };
        }

        [Fact]
        public void A_bar_built_the_old_way_still_draws_buttons()
        {
            var even = new TabBar(new[] { LocalizedText.Raw("A"), LocalizedText.Raw("B") }, font: null, Band);
            Assert.Equal(TabBarDrawMode.Button, even.DrawMode);
            Assert.False(even.DrawsFlat);

            var wrapped = new TabBar(new[] { LocalizedText.Raw("A"), LocalizedText.Raw("B") }, font: null, Band)
            {
                TabWidth = 56f, TabHeight = 26f,
            };
            Assert.False(wrapped.DrawsFlat);
        }

        [Fact]
        public void The_flat_mode_is_read_by_the_fixed_size_layout_only()
        {
            // An even-split bar abuts its tabs with no gutter, so a per-tab border would double every seam.
            var even = new TabBar(new[] { LocalizedText.Raw("A"), LocalizedText.Raw("B") }, font: null, Band)
            {
                DrawMode = TabBarDrawMode.Flat,
            };
            Assert.False(even.DrawsFlat);

            var strip = NewStrip();
            Assert.True(strip.DrawsFlat);
        }

        [Fact]
        public void The_active_tab_takes_the_theme_tab_colours_and_a_resting_one_the_other()
        {
            var strip = NewStrip();
            strip.ActiveIndex = 1;

            Assert.Equal(Palette.TabActiveFill, strip.ResolveFlatVisual(1).Fill);
            Assert.Equal(Palette.BorderHover, strip.ResolveFlatVisual(1).Border);
            Assert.Equal(Palette.Text, strip.ResolveFlatVisual(1).Text);

            Assert.Equal(Palette.TabFill, strip.ResolveFlatVisual(0).Fill);
            Assert.Equal(Palette.BorderShadow, strip.ResolveFlatVisual(0).Border);
            Assert.Equal(Palette.TextMuted, strip.ResolveFlatVisual(0).Text);
        }

        [Fact]
        public void A_disabled_tab_is_greyed_rather_than_resting()
        {
            var strip = NewStrip();

            Assert.Equal(Palette.SurfaceDisabled, strip.ResolveFlatVisual(2).Fill);
            Assert.Equal(Palette.BorderDisabled, strip.ResolveFlatVisual(2).Border);
            Assert.Equal(Palette.TextDisabled, strip.ResolveFlatVisual(2).Text);
        }

        [Fact]
        public void Hover_and_press_move_a_live_tab_and_leave_a_dead_one_alone()
        {
            var strip = NewStrip();
            var pointer = new Pointer();

            pointer.Update(Frame(CentreOf(strip.TabRect(1)), false));
            strip.Update(pointer);
            Assert.Equal(Palette.SurfaceHover, strip.ResolveFlatVisual(1).Fill);

            pointer.Update(Frame(CentreOf(strip.TabRect(1)), true));
            strip.Update(pointer);
            Assert.Equal(Palette.SurfacePress, strip.ResolveFlatVisual(1).Fill);

            // The dead tab under a hover stays dead.
            var second = new Pointer();
            second.Update(Frame(CentreOf(strip.TabRect(2)), false));
            strip.Update(second);
            Assert.Equal(Palette.SurfaceDisabled, strip.ResolveFlatVisual(2).Fill);
        }

        [Fact]
        public void With_no_active_tab_every_tab_draws_at_rest()
        {
            var strip = NewStrip();
            strip.AllowNoActiveTab = true;
            strip.ActiveIndex = TabBar.NoActiveTab;

            Assert.Equal(Palette.TabFill, strip.ResolveFlatVisual(0).Fill);
            Assert.Equal(Palette.TabFill, strip.ResolveFlatVisual(1).Fill);
            Assert.Equal(Palette.BorderShadow, strip.ResolveFlatVisual(0).Border);
        }

        [Fact]
        public void Opacity_fades_the_flat_colours_and_nothing_else()
        {
            var strip = NewStrip();
            strip.Opacity = 0.5f;

            // Tab 1 is a resting one: tab 0 is the active tab a fresh bar starts on.
            (Vector4 fill, Vector4 border, Vector4 text) = strip.ResolveFlatVisual(1);
            Assert.Equal(Palette.TabFill.W * 0.5f, fill.W, 3);
            Assert.Equal(Palette.TabFill.X, fill.X, 3);
            Assert.Equal(Palette.BorderShadow.W * 0.5f, border.W, 3);
            Assert.Equal(Palette.TextMuted.W * 0.5f, text.W, 3);
        }

        [Fact]
        public void The_flat_palette_comes_off_the_ambient_theme_at_construction()
        {
            GuiTheme saved = GuiTheme.Default;
            try
            {
                var bronze = new Vector4(0.42f, 0.28f, 0.11f, 1f);
                GuiTheme.Default = GuiTheme.Default with { TabActiveFill = bronze };

                var strip = new TabBar(new[] { LocalizedText.Raw("A") }, font: null, Band)
                {
                    TabWidth = 56f, TabHeight = 26f, DrawMode = TabBarDrawMode.Flat,
                };

                Assert.Equal(bronze, strip.FlatTheme.TabActiveFill);
                Assert.Equal(bronze, strip.ResolveFlatVisual(0).Fill);
            }
            finally { GuiTheme.Default = saved; }
        }

        // --- The label half: fitted to the tab, centred in it, and scaled ---

        // Ten units a character, so a width reads straight off the string length.
        static float Measure(string s) => s.Length * 10f;

        [Fact]
        public void A_label_is_centred_in_its_tab_on_both_axes()
        {
            var tab = new Rect(200f, 300f, 56f, 26f);

            var (text, at) = TabBar.FlatLabel(tab, "Two", Measure, lineHeight: 12f, scale: 1f);

            Assert.Equal("Two", text);
            Assert.Equal(tab.X + (56f - 30f) * 0.5f, at.X, 3);
            Assert.Equal(tab.Y + (26f - 12f) * 0.5f, at.Y, 3);
        }

        [Fact]
        public void A_label_too_wide_for_its_tab_ellipsises_inside_the_border()
        {
            var tab = new Rect(200f, 300f, 56f, 26f);

            var (text, at) = TabBar.FlatLabel(tab, "Inventory", Measure, lineHeight: 12f, scale: 1f);

            // 56 less the 6 units kept clear leaves 50, so two characters and the three dots fit and no more.
            Assert.Equal("In...", text);
            Assert.True(Measure(text) <= 50f);
            Assert.True(at.X >= tab.X && at.X + Measure(text) <= tab.Right);
        }

        [Fact]
        public void The_kept_clear_margin_is_what_decides_a_label_on_the_edge_of_fitting()
        {
            // A 60 unit tab less the 6 kept clear leaves 54: three characters and the dots (60 units) do not fit,
            // so the answer is "In...". With no margin the budget would be the whole 60 and it would be "Inv...",
            // which is the label drawn hard against both borders.
            var tab = new Rect(0f, 0f, 60f, 26f);
            var (text, _) = TabBar.FlatLabel(tab, "Inventory", Measure, lineHeight: 12f, scale: 1f);
            Assert.Equal("In...", text);
        }

        [Fact]
        public void A_scale_of_zero_or_less_fits_the_label_at_its_natural_size()
        {
            // TextScale is a plain public field, so zero is reachable. Dividing the budget by it would make the
            // budget infinite and the over-long label would be drawn whole, straight through the border.
            var tab = new Rect(200f, 300f, 56f, 26f);
            foreach (float scale in new[] { 0f, -2f })
            {
                var (text, at) = TabBar.FlatLabel(tab, "Inventory", Measure, lineHeight: 12f, scale);
                Assert.Equal("In...", text);
                Assert.True(float.IsFinite(at.X) && float.IsFinite(at.Y));
            }
        }

        [Fact]
        public void The_scale_widens_the_budget_and_moves_the_origin()
        {
            var tab = new Rect(200f, 300f, 56f, 26f);

            var (text, at) = TabBar.FlatLabel(tab, "Inventory", Measure, lineHeight: 12f, scale: 0.5f);

            // Half-size text fits where full-size text did not, because the budget is measured unscaled.
            Assert.Equal("Inventory", text);
            Assert.Equal(tab.X + (56f - 90f * 0.5f) * 0.5f, at.X, 3);
            Assert.Equal(tab.Y + (26f - 12f * 0.5f) * 0.5f, at.Y, 3);
        }

        [Fact]
        public void An_empty_label_draws_nothing_at_the_tabs_corner()
        {
            var tab = new Rect(200f, 300f, 56f, 26f);

            var (text, at) = TabBar.FlatLabel(tab, string.Empty, Measure, lineHeight: 12f, scale: 1f);

            Assert.Equal(string.Empty, text);
            Assert.Equal(new Vector2(tab.X, tab.Y), at);
        }
    }
}
