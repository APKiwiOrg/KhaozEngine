using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gui
{
    // Covers the 10.13.0 widget-parity additions: Toggle.WasToggled, Slider.WasChanged, TextInput.SetText /
    // Focus / Unfocus / PlaceholderContent, and PopupPanel scroll + adaptive footer buttons + row icons.
    public class GuiWidgetParityTests
    {
        static InputState Frame(Vector2 pos, bool leftDown)
        {
            var down = new HashSet<MouseButton>();
            if (leftDown) down.Add(MouseButton.Left);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                down, new HashSet<MouseButton>(), pos, Vector2.Zero, 0, 960, 540);
        }

        static Vector2 Center(Rect r) => new(r.X + r.Width / 2f, r.Y + r.Height / 2f);

        // --- Toggle.WasToggled -------------------------------------------------

        [Fact]
        public void Toggle_WasToggled_is_true_only_on_the_frame_a_tap_flips_it()
        {
            var t = new Toggle(new Rect(100, 100, 60, 30));
            var p = new Pointer();
            p.Update(Frame(new Vector2(130, 115), false));
            t.Update(p);
            Assert.False(t.WasToggled);                       // no tap yet
            p.Update(Frame(new Vector2(130, 115), true));
            t.Update(p);
            Assert.False(t.WasToggled);                       // still pressing
            p.Update(Frame(new Vector2(130, 115), false));
            t.Update(p);
            Assert.True(t.WasToggled);                        // release completes the tap
            // A quiet frame clears the flag.
            p.Update(Frame(new Vector2(130, 115), false));
            t.Update(p);
            Assert.False(t.WasToggled);
        }

        // --- Slider.WasChanged -------------------------------------------------

        [Fact]
        public void Slider_WasChanged_tracks_value_changes_per_frame()
        {
            var s = new Slider(new Rect(100, 100, 200, 20));
            var p = new Pointer();
            p.Update(Frame(new Vector2(200, 110), false));
            p.Update(Frame(new Vector2(200, 110), true));     // press at center
            s.Update(p);
            Assert.True(s.WasChanged);                        // value moved to 0.5
            s.Update(p);                                      // held, no movement
            Assert.False(s.WasChanged);
        }

        // --- TextInput programmatic + focus API --------------------------------

        [Fact]
        public void TextInput_SetText_clamps_to_MaxLength()
        {
            var f = new TextInput(new Rect(0, 0, 200, 30)) { MaxLength = 3 };
            f.SetText("abcdef");
            Assert.Equal("abc", f.Text);
        }

        [Fact]
        public void TextInput_SetText_is_seen_as_a_change_by_the_next_Update()
        {
            var f = new TextInput(new Rect(0, 0, 200, 30));
            f.SetText("hello");
            var p = new Pointer();
            p.Update(Frame(new Vector2(9000, 9000), false));  // outside; stays unfocused
            f.Update(p, InputState.Empty, 0f);
            Assert.True(f.TextChanged);
            f.Update(p, InputState.Empty, 0f);
            Assert.False(f.TextChanged);                      // no further change
        }

        [Fact]
        public void TextInput_Focus_and_Unfocus_are_public_and_idempotent()
        {
            var f = new TextInput(new Rect(0, 0, 200, 30));
            f.Focus();
            Assert.True(f.IsFocused);
            f.Focus();                                        // no-op
            Assert.True(f.IsFocused);
            f.Unfocus();
            Assert.False(f.IsFocused);
            f.Unfocus();                                      // no-op
            Assert.False(f.IsFocused);
        }

        [Fact]
        public void TextInput_PlaceholderContent_resolves_localized_text()
        {
            var f = new TextInput(new Rect(0, 0, 200, 30))
            {
                PlaceholderContent = LocalizedText.Raw("type here"),
            };
            Assert.Equal("type here", f.PlaceholderContent.Resolve());
        }

        // --- PopupPanel adaptive footer buttons --------------------------------

        static PopupPanel OneRowPanel(Vector2 view, bool primary) =>
            Configure(new PopupPanel { Viewport = view, ShowPrimaryAction = primary });

        static PopupPanel Configure(PopupPanel panel)
        {
            panel.SetRows(new[] { PopupRow.Stat(LocalizedText.Raw("a"), LocalizedText.Raw("1"), Vector4.One) });
            return panel;
        }

        [Fact]
        public void PopupPanel_dual_buttons_keep_full_width_on_a_wide_panel()
        {
            var panel = OneRowPanel(new Vector2(960, 540), primary: true);
            Assert.Equal(130f, panel.DismissBounds().Width, 3);
            Assert.Equal(130f, panel.PrimaryBounds().Width, 3);
        }

        [Fact]
        public void PopupPanel_dual_buttons_shrink_to_fit_a_narrow_panel()
        {
            var panel = OneRowPanel(new Vector2(200, 540), primary: true);
            float w = panel.DismissBounds().Width;
            Assert.True(w < 130f);
            Assert.Equal(w, panel.PrimaryBounds().Width, 3);           // both share the shrunk width
            // Both buttons plus the gap stay inside the panel.
            Assert.True(panel.PrimaryBounds().Right <= panel.PanelRect().Right + 0.5f);
        }

        // --- PopupRow icon -----------------------------------------------------

        [Fact]
        public void PopupRow_Stat_carries_an_optional_icon_colour()
        {
            var withIcon = PopupRow.Stat(LocalizedText.Raw("a"), LocalizedText.Raw("1"), Vector4.One, new Vector4(1, 0, 0, 1));
            Assert.True(withIcon.IconColor.HasValue);
            var noIcon = PopupRow.Stat(LocalizedText.Raw("a"), LocalizedText.Raw("1"), Vector4.One);
            Assert.False(noIcon.IconColor.HasValue);
        }

        // --- PopupPanel scrolling ---------------------------------------------

        static PopupPanel OverflowPanel()
        {
            var panel = new PopupPanel { Viewport = new Vector2(960, 540) };
            var rows = new List<PopupRow>();
            for (int i = 0; i < 200; i++)
                rows.Add(PopupRow.Stat(LocalizedText.Raw("x"), LocalizedText.Raw("y"), Vector4.One));
            panel.SetRows(rows);
            return panel;
        }

        static Pointer HoveringContent(PopupPanel panel)
        {
            var p = new Pointer();
            p.Update(Frame(Center(panel.ContentRect()), false));      // in content, not pressed
            return p;
        }

        [Fact]
        public void PopupPanel_wheel_scrolls_and_clamps_within_the_overflow()
        {
            var panel = OverflowPanel();
            var p = HoveringContent(panel);
            Assert.Equal(0f, panel.ScrollOffset);

            panel.Update(p, -1f);                                     // one notch down
            float after1 = panel.ScrollOffset;
            Assert.True(after1 > 0f);

            panel.Update(p, -100000f);                                // scroll far past the bottom
            float max = panel.ScrollOffset;
            Assert.True(max > after1);

            panel.Update(p, -100000f);                                // clamped, no further growth
            Assert.Equal(max, panel.ScrollOffset, 3);

            panel.Update(p, 100000f);                                 // back up past the top
            Assert.Equal(0f, panel.ScrollOffset, 3);
        }

        [Fact]
        public void PopupPanel_does_not_scroll_when_the_content_fits()
        {
            var panel = OneRowPanel(new Vector2(960, 540), primary: false);
            var p = HoveringContent(panel);
            panel.Update(p, -1f);
            Assert.Equal(0f, panel.ScrollOffset);
        }
    }
}
