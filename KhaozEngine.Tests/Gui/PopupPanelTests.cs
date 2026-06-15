using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    public class PopupPanelTests
    {
        static readonly Vector2 View = new(960, 540);

        static InputState Frame(Vector2 pos, bool down)
        {
            var b = new HashSet<MouseButton>();
            if (down) b.Add(MouseButton.Left);
            return new InputState(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                b, new HashSet<MouseButton>(), pos, Vector2.Zero, 0, 960, 540);
        }

        static void Tap(PopupPanel panel, Pointer p, Vector2 at)
        {
            p.Update(Frame(at, false)); panel.Update(p);
            p.Update(Frame(at, true)); panel.Update(p);
            p.Update(Frame(at, false)); panel.Update(p);
        }

        static Vector2 Center(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

        [Fact]
        public void Panel_grows_with_more_rows()
        {
            var few = new PopupPanel { Viewport = View };
            few.SetRows(new[] { PopupRow.Stat("a", "1", Vector4.One) });
            var many = new PopupPanel { Viewport = View };
            var rows = new List<PopupRow>();
            for (int i = 0; i < 8; i++) rows.Add(PopupRow.Stat("x", "y", Vector4.One));
            many.SetRows(rows);
            Assert.True(many.PanelRect().Height > few.PanelRect().Height);
        }

        [Fact]
        public void Panel_respects_minimum_height_when_empty()
        {
            var panel = new PopupPanel { Viewport = View };
            panel.SetRows(System.Array.Empty<PopupRow>());
            Assert.True(panel.PanelRect().Height >= 150f);   // default MinHeight
        }

        [Fact]
        public void Panel_is_clamped_to_the_max_height_fraction()
        {
            var panel = new PopupPanel { Viewport = View };
            var rows = new List<PopupRow>();
            for (int i = 0; i < 200; i++) rows.Add(PopupRow.Stat("x", "y", Vector4.One));
            panel.SetRows(rows);
            Assert.True(panel.PanelRect().Height <= View.Y * 0.85f + 0.5f);
        }

        [Fact]
        public void Dismiss_tap_returns_true()
        {
            var panel = new PopupPanel { Viewport = View };
            panel.SetRows(new[] { PopupRow.Stat("a", "1", Vector4.One) });
            var p = new Pointer();
            Tap(panel, p, Center(panel.DismissBounds()));
            // re-run a full tap, asserting the final Update result:
            p = new Pointer();
            p.Update(Frame(Center(panel.DismissBounds()), false)); panel.Update(p);
            p.Update(Frame(Center(panel.DismissBounds()), true)); panel.Update(p);
            p.Update(Frame(Center(panel.DismissBounds()), false));
            Assert.True(panel.Update(p));
        }

        [Fact]
        public void Primary_action_tap_sets_the_flag_and_does_not_dismiss()
        {
            var panel = new PopupPanel { Viewport = View, ShowPrimaryAction = true };
            panel.SetRows(new[] { PopupRow.Stat("a", "1", Vector4.One) });
            var p = new Pointer();
            Vector2 at = Center(panel.PrimaryBounds());
            p.Update(Frame(at, false)); panel.Update(p);
            p.Update(Frame(at, true)); panel.Update(p);
            p.Update(Frame(at, false));
            bool dismissed = panel.Update(p);
            Assert.False(dismissed);
            Assert.True(panel.WasPrimaryActionClicked);
        }

        [Fact]
        public void Update_blocks_the_pointer_over_the_panel()
        {
            var panel = new PopupPanel { Viewport = View };
            panel.SetRows(new[] { PopupRow.Stat("a", "1", Vector4.One) });
            var p = new Pointer();
            p.Update(Frame(Center(panel.PanelRect()), false));
            panel.Update(p);
            Assert.True(p.IsBlocked(Center(panel.PanelRect())));
        }
    }
}
