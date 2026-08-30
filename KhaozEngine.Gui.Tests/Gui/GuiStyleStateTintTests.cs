using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// Headless coverage for the per-state border / text tints and the shared style fade added for #252
    /// (<see cref="GuiStyle.ResolveBorder"/>, <see cref="GuiStyle.ResolveText"/>, <see cref="GuiStyle.Faded"/>) plus
    /// <see cref="Button.Opacity"/>. The resolvers are pure, so the precedence and the fall-back-to-unset behaviour
    /// are all device-free. The forward from <see cref="Button.Draw"/> through the fade has its own on-device net
    /// (<c>ButtonStateTintGpuTests</c>).
    /// </summary>
    public class GuiStyleStateTintTests
    {
        static readonly Vector4 BaseBorder = new(0.1f, 0.2f, 0.3f, 1f);
        static readonly Vector4 BaseText = new(0.9f, 0.9f, 0.9f, 1f);
        static readonly Vector4 SelBorder = new(0.5f, 0.8f, 1f, 1f);
        static readonly Vector4 DisText = new(0.4f, 0.4f, 0.4f, 1f);

        static GuiStyle Plain() => new()
        {
            Border = BaseBorder,
            Text = BaseText,
            SelectedBorder = SelBorder,
            DisabledText = DisText,
        };

        // --- defaults leave today's behaviour exactly in place ------------------

        [Fact]
        public void The_per_state_tints_default_to_unset()
        {
            var s = new GuiStyle();
            Assert.Null(s.HoverBorder);
            Assert.Null(s.PressBorder);
            Assert.Null(s.DisabledBorder);
            Assert.Null(s.HoverText);
            Assert.Null(s.PressText);

            // And none of the shipped presets sets one, so no preset's look moves.
            foreach (GuiStyle p in new[] { GuiStyle.Default, GuiStyle.Primary, GuiStyle.Secondary,
                                           GuiStyle.Danger, GuiStyle.Active, GuiStyle.Legacy, GuiStyle.Modern })
            {
                Assert.Null(p.HoverBorder);
                Assert.Null(p.PressBorder);
                Assert.Null(p.DisabledBorder);
                Assert.Null(p.HoverText);
                Assert.Null(p.PressText);
            }
        }

        [Fact]
        public void With_no_tints_set_the_resolvers_reproduce_the_old_expressions()
        {
            GuiStyle s = Plain();
            foreach (bool enabled in new[] { true, false })
                foreach (bool selected in new[] { true, false })
                    foreach (bool hover in new[] { true, false })
                        foreach (bool press in new[] { true, false })
                        {
                            // border used to be: selected ? SelectedBorder : Border
                            Assert.Equal(selected ? SelBorder : BaseBorder,
                                s.ResolveBorder(enabled, selected, hover, press));
                            // text used to be: enabled ? Text : DisabledText
                            Assert.Equal(enabled ? BaseText : DisText, s.ResolveText(enabled, hover, press));
                        }
        }

        // --- the tints, and their precedence ------------------------------------

        [Fact]
        public void Border_tints_apply_in_selected_disabled_press_hover_order()
        {
            GuiStyle s = Plain();
            var hoverB = new Vector4(1f, 0f, 0f, 1f);
            var pressB = new Vector4(0f, 1f, 0f, 1f);
            var disB = new Vector4(0f, 0f, 1f, 1f);
            s.HoverBorder = hoverB;
            s.PressBorder = pressB;
            s.DisabledBorder = disB;

            Assert.Equal(BaseBorder, s.ResolveBorder(true, false, false, false));
            Assert.Equal(hoverB, s.ResolveBorder(true, false, hover: true, press: false));
            Assert.Equal(pressB, s.ResolveBorder(true, false, hover: true, press: true));   // press beats hover
            Assert.Equal(disB, s.ResolveBorder(enabled: false, false, hover: true, press: true));
            // Selected still wins outright, exactly as before, so a selected disabled button is unchanged.
            Assert.Equal(SelBorder, s.ResolveBorder(enabled: false, selected: true, hover: true, press: true));
        }

        [Fact]
        public void Text_tints_apply_in_disabled_press_hover_order()
        {
            GuiStyle s = Plain();
            var hoverT = new Vector4(1f, 1f, 0f, 1f);
            var pressT = new Vector4(0f, 1f, 1f, 1f);
            s.HoverText = hoverT;
            s.PressText = pressT;

            Assert.Equal(BaseText, s.ResolveText(true, false, false));
            Assert.Equal(hoverT, s.ResolveText(true, hover: true, press: false));
            Assert.Equal(pressT, s.ResolveText(true, hover: true, press: true));            // press beats hover
            Assert.Equal(DisText, s.ResolveText(enabled: false, hover: true, press: true)); // disabled beats both
        }

        [Fact]
        public void An_unset_tint_falls_back_while_its_siblings_are_set()
        {
            GuiStyle s = Plain();
            s.PressBorder = new Vector4(0f, 1f, 0f, 1f);        // press set, hover left unset
            Assert.Equal(BaseBorder, s.ResolveBorder(true, false, hover: true, press: false));

            s.PressText = new Vector4(0f, 1f, 1f, 1f);
            Assert.Equal(BaseText, s.ResolveText(true, hover: true, press: false));
        }

        // --- the shared fade -----------------------------------------------------

        [Fact]
        public void Faded_at_one_or_more_returns_the_style_unchanged()
        {
            GuiStyle s = Plain();
            s.HoverBorder = new Vector4(1f, 0f, 0f, 0.8f);
            Assert.Equal(s.Border, s.Faded(1f).Border);
            Assert.Equal(s.HoverBorder, s.Faded(1f).HoverBorder);
            Assert.Equal(s.Text, s.Faded(2f).Text);
        }

        [Fact]
        public void Faded_scales_every_alpha_including_the_optional_tints()
        {
            GuiStyle s = Plain();
            s.HoverBorder = new Vector4(1f, 0f, 0f, 0.8f);
            s.PressText = new Vector4(0f, 1f, 1f, 1f);
            s.ShadowColor = new Vector4(0f, 0f, 0f, 0.5f);
            s.GlowColor = new Vector4(0.5f, 0.8f, 1f, 0.5f);

            GuiStyle f = s.Faded(0.5f);
            Assert.Equal(BaseBorder.W * 0.5f, f.Border.W, 4);
            Assert.Equal(BaseText.W * 0.5f, f.Text.W, 4);
            Assert.Equal(SelBorder.W * 0.5f, f.SelectedBorder.W, 4);
            Assert.Equal(0.4f, f.HoverBorder!.Value.W, 4);
            Assert.Equal(0.5f, f.PressText!.Value.W, 4);
            Assert.Equal(0.25f, f.ShadowColor.W, 4);
            Assert.Equal(0.25f, f.GlowColor.W, 4);

            // RGB is untouched, and an unset tint stays unset (so it keeps falling back rather than
            // materializing as a faded copy of Border).
            Assert.Equal(BaseBorder.X, f.Border.X, 4);
            Assert.Null(f.PressBorder);
            Assert.Null(f.HoverText);
        }

        // --- Button.Opacity -------------------------------------------------------

        [Fact]
        public void Button_Opacity_defaults_to_one()
        {
            var btn = new Button(new Rect(0, 0, 100, 30), LocalizedText.Raw("Go"), null!);
            Assert.Equal(1f, btn.Opacity);
        }
    }
}
