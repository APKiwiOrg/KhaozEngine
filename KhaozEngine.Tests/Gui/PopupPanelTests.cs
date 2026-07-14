using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Windowing;
using Xunit;
using KhaozEngine.Primitives;

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

        static InputState KeyFrame(Key key)
        {
            var pressed = new HashSet<Key> { key };
            return new InputState(new HashSet<Key>(), pressed, new HashSet<Key>(),
                new HashSet<MouseButton>(), new HashSet<MouseButton>(), Vector2.Zero, Vector2.Zero, 0, 960, 540);
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
            few.SetRows(new[] { PopupRow.Stat(LocalizedText.Raw("a"), LocalizedText.Raw("1"), Vector4.One) });
            var many = new PopupPanel { Viewport = View };
            var rows = new List<PopupRow>();
            for (int i = 0; i < 8; i++) rows.Add(PopupRow.Stat(LocalizedText.Raw("x"), LocalizedText.Raw("y"), Vector4.One));
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
            for (int i = 0; i < 200; i++) rows.Add(PopupRow.Stat(LocalizedText.Raw("x"), LocalizedText.Raw("y"), Vector4.One));
            panel.SetRows(rows);
            Assert.True(panel.PanelRect().Height <= View.Y * 0.85f + 0.5f);
        }

        [Fact]
        public void Dismiss_tap_returns_true()
        {
            var panel = new PopupPanel { Viewport = View };
            panel.SetRows(new[] { PopupRow.Stat(LocalizedText.Raw("a"), LocalizedText.Raw("1"), Vector4.One) });
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
            panel.SetRows(new[] { PopupRow.Stat(LocalizedText.Raw("a"), LocalizedText.Raw("1"), Vector4.One) });
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
            panel.SetRows(new[] { PopupRow.Stat(LocalizedText.Raw("a"), LocalizedText.Raw("1"), Vector4.One) });
            var p = new Pointer();
            p.Update(Frame(Center(panel.PanelRect()), false));
            panel.Update(p);
            Assert.True(p.IsBlocked(Center(panel.PanelRect())));
        }

        [Fact]
        public void Viewport_defaults_to_zero_and_layout_throws_until_it_is_set()
        {
            var panel = new PopupPanel();
            Assert.Equal(Vector2.Zero, panel.Viewport);                       // unset by default
            Assert.Throws<System.InvalidOperationException>(() => panel.PanelRect());

            panel.Viewport = View;
            var rect = panel.PanelRect();                                     // no throw once set
            Assert.True(rect.Width > 0f && rect.Height > 0f);
        }

        [Fact]
        public void FooterButtons_LayoutRightToLeft_ClickFires()
        {
            var panel = new PopupPanel { Viewport = View };
            bool fired0 = false, fired1 = false, fired2 = false;
            panel.SetFooterButtons(new[]
            {
                new PopupAction(LocalizedText.Raw("First"), () => fired0 = true),
                new PopupAction(LocalizedText.Raw("Second"), () => fired1 = true),
                new PopupAction(LocalizedText.Raw("Third"), () => fired2 = true),
            });

            var bounds = panel.FooterButtonBounds();
            Assert.Equal(3, bounds.Count);
            Assert.True(bounds[0].X > bounds[1].X);   // index 0 (rightmost) sits right of index 1
            Assert.True(bounds[1].X > bounds[2].X);   // index 1 sits right of index 2 (leftmost)

            var p = new Pointer();
            Tap(panel, p, Center(bounds[1]));

            Assert.False(fired0);
            Assert.True(fired1);
            Assert.False(fired2);
        }

        [Fact]
        public void FooterButtons_DisabledButtonIgnoresClicks()
        {
            var panel = new PopupPanel { Viewport = View };
            bool fired = false;
            panel.SetFooterButtons(new[]
            {
                new PopupAction(LocalizedText.Raw("Disabled"), () => fired = true, enabled: false),
            });

            var p = new Pointer();
            Tap(panel, p, Center(panel.FooterButtonBounds()[0]));
            Assert.False(fired);

            // A disabled action also ignores its keyboard trigger (here index 0 is both the Enter default and the
            // CancelIndex default, since it is the only, and therefore last, footer action).
            panel.HandleKeys(KeyFrame(Key.Enter));
            Assert.False(fired);
            panel.HandleKeys(KeyFrame(Key.Escape));
            Assert.False(fired);
        }

        [Fact]
        public void FooterButtons_EmptyKeepsDismissPrimaryCompat()
        {
            var panel = new PopupPanel { Viewport = View, ShowPrimaryAction = true };
            panel.SetRows(new[] { PopupRow.Stat(LocalizedText.Raw("a"), LocalizedText.Raw("1"), Vector4.One) });
            Assert.Empty(panel.FooterButtons);
            Assert.Empty(panel.FooterButtonBounds());

            var p = new Pointer();
            Vector2 primaryAt = Center(panel.PrimaryBounds());
            p.Update(Frame(primaryAt, false)); panel.Update(p);
            p.Update(Frame(primaryAt, true)); panel.Update(p);
            p.Update(Frame(primaryAt, false));
            bool dismissedByPrimaryTap = panel.Update(p);
            Assert.False(dismissedByPrimaryTap);
            Assert.True(panel.WasPrimaryActionClicked);

            p = new Pointer();
            p.Update(Frame(Center(panel.DismissBounds()), false)); panel.Update(p);
            p.Update(Frame(Center(panel.DismissBounds()), true)); panel.Update(p);
            p.Update(Frame(Center(panel.DismissBounds()), false));
            Assert.True(panel.Update(p));
        }

        [Fact]
        public void FooterButtons_EscTriggersCancelIndex_EnterTriggersDefault()
        {
            var panel = new PopupPanel { Viewport = View };
            bool fired0 = false, fired1 = false, fired2 = false;
            panel.SetFooterButtons(new[]
            {
                new PopupAction(LocalizedText.Raw("First"), () => fired0 = true),
                new PopupAction(LocalizedText.Raw("Second"), () => fired1 = true),
                new PopupAction(LocalizedText.Raw("Third"), () => fired2 = true),
            });

            // Default CancelIndex (-1) resolves to the last footer action.
            panel.HandleKeys(KeyFrame(Key.Escape));
            Assert.False(fired0);
            Assert.False(fired1);
            Assert.True(fired2);

            fired2 = false;
            panel.HandleKeys(KeyFrame(Key.Enter));
            Assert.True(fired0);
            Assert.False(fired1);
            Assert.False(fired2);

            fired0 = false;
            panel.CancelIndex = 1;
            panel.HandleKeys(KeyFrame(Key.Escape));
            Assert.False(fired0);
            Assert.True(fired1);
            Assert.False(fired2);
        }

        [Fact]
        public void FooterButtons_CallbackGrowingTheListMidFireDoesNotThrow()
        {
            var panel = new PopupPanel { Viewport = View };
            int firedCount = 0;
            panel.SetFooterButtons(new[]
            {
                new PopupAction(LocalizedText.Raw("First"), () =>
                {
                    firedCount++;
                    // Reentrant SetFooterButtons from inside the callback, with a LARGER list than the one
                    // UpdateFooterButtons is currently iterating: this used to IndexOutOfRange because the loop
                    // bound re-read the live (now-larger) _footerButtons.Count against the smaller bounds
                    // snapshot taken before the callback ran.
                    panel.SetFooterButtons(new[]
                    {
                        new PopupAction(LocalizedText.Raw("A"), () => { }),
                        new PopupAction(LocalizedText.Raw("B"), () => { }),
                        new PopupAction(LocalizedText.Raw("C"), () => { }),
                        new PopupAction(LocalizedText.Raw("D"), () => { }),
                    });
                }),
                new PopupAction(LocalizedText.Raw("Second"), () => { }),
            });

            var bounds = panel.FooterButtonBounds();
            var p = new Pointer();
            var exception = Record.Exception(() => Tap(panel, p, Center(bounds[0])));

            Assert.Null(exception);
            Assert.Equal(1, firedCount);
            Assert.Equal(4, panel.FooterButtons.Count);   // the reentrant SetFooterButtons call took effect
        }

        [Fact]
        public void PanelRect_WidensWhenButtonsNeedMoreRoomThanWidthFractionGives()
        {
            var narrow = new PopupPanel { Viewport = View };
            narrow.SetRows(new[] { PopupRow.Stat(LocalizedText.Raw("a"), LocalizedText.Raw("1"), Vector4.One) });

            var wide = new PopupPanel { Viewport = View };
            wide.SetRows(new[] { PopupRow.Stat(LocalizedText.Raw("a"), LocalizedText.Raw("1"), Vector4.One) });
            wide.SetFooterButtons(new[]
            {
                new PopupAction(LocalizedText.Raw("A"), () => { }),
                new PopupAction(LocalizedText.Raw("B"), () => { }),
                new PopupAction(LocalizedText.Raw("C"), () => { }),
                new PopupAction(LocalizedText.Raw("D"), () => { }),
                new PopupAction(LocalizedText.Raw("E"), () => { }),
                new PopupAction(LocalizedText.Raw("F"), () => { }),
            });

            // 6 fixed-width buttons need more room than WidthFraction of the viewport gives, so the panel widens
            // to fit them instead of shrinking the buttons.
            Assert.True(wide.PanelRect().Width > View.X * wide.WidthFraction);
            Assert.True(wide.PanelRect().Width > narrow.PanelRect().Width);
        }

        [Fact]
        public void FooterButtons_NonEmptySuppressesClassicDismissPrimaryHitTest()
        {
            var panel = new PopupPanel { Viewport = View, ShowPrimaryAction = true };
            panel.SetRows(new[] { PopupRow.Stat(LocalizedText.Raw("a"), LocalizedText.Raw("1"), Vector4.One) });
            panel.SetFooterButtons(new[]
            {
                new PopupAction(LocalizedText.Raw("A"), () => { }),
                new PopupAction(LocalizedText.Raw("B"), () => { }),
            });

            // Tap the classic primary-button location: with a non-empty footer, Update never reaches the
            // ShowPrimaryAction/DismissBounds branch, so neither WasPrimaryActionClicked nor a dismiss fires.
            var p = new Pointer();
            Vector2 classicPrimaryAt = Center(panel.PrimaryBounds());
            p.Update(Frame(classicPrimaryAt, false)); panel.Update(p);
            p.Update(Frame(classicPrimaryAt, true)); panel.Update(p);
            p.Update(Frame(classicPrimaryAt, false));
            bool dismissed = panel.Update(p);

            Assert.False(dismissed);
            Assert.False(panel.WasPrimaryActionClicked);
        }

        [Fact]
        public void FooterButtons_PointerBlockBehindStaysIntact()
        {
            var panel = new PopupPanel { Viewport = View };
            panel.SetFooterButtons(new[]
            {
                new PopupAction(LocalizedText.Raw("A"), () => { }),
                new PopupAction(LocalizedText.Raw("B"), () => { }),
            });

            var p = new Pointer();
            p.Update(Frame(Center(panel.PanelRect()), false));
            panel.Update(p);

            Assert.True(p.IsBlocked(Center(panel.PanelRect())));
            Assert.True(p.IsBlocked(Center(panel.FooterButtonBounds()[0])));
            Assert.False(p.IsBlocked(new Vector2(2, 2)));   // top-left corner outside the panel: a background control there is not blocked
        }
    }
}
