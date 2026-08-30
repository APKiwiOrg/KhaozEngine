using System;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // The FORWARDS for #252 get their own regression net, since neither is visible to a pure test:
    //   - Button.Draw must run its Style through GuiStyle.Faded(Opacity). Under the standard src-alpha blend a
    //     fully transparent button must paint nothing at all, which fails the moment one element (the fill, the
    //     border, the label, the drop shadow, the hover glow) skips the fade. Opacity 1 must be byte-identical
    //     to the untouched button, which is what keeps every existing caller unchanged.
    //   - GuiDraw.DrawButton must resolve its border and text through the per-state tints. A hovered button with
    //     a hover border and hover text set has to differ from the same button with them unset, and a style that
    //     sets none has to render byte-identically to today's call.
    // No golden image (self-relative only). Skipped unless KE_GPU_TESTS=1.
    public sealed class ButtonStateTintGpuTests
    {
        const int W = 320, H = 120;

        static readonly string FontPath = System.IO.Path.Combine(
            System.AppContext.BaseDirectory, "Assets", "Roboto-Regular.ttf");

        static readonly Rect ButtonRect = new(40, 20, 240, 80);

        [GpuFact]
        public void Button_Opacity_zero_paints_nothing_and_the_default_matches_an_untouched_button()
        {
            byte[] empty = CaptureNothing();
            byte[] transparent = Capture(b => b.Opacity = 0f);
            byte[] defaulted = Capture(configure: null);
            byte[] opaque = Capture(b => b.Opacity = 1f);
            byte[] half = Capture(b => b.Opacity = 0.5f);

            Assert.Equal(empty, transparent);
            Assert.Equal(defaulted, opaque);
            Assert.False(opaque.AsSpan().SequenceEqual(empty), "the button must draw something at full opacity");
            Assert.False(half.AsSpan().SequenceEqual(opaque), "a mid opacity must differ from the opaque draw");
            Assert.False(half.AsSpan().SequenceEqual(empty), "a mid opacity must still draw something");
        }

        [GpuFact]
        public void A_modern_style_fades_its_shadow_and_glow_with_the_button_too()
        {
            // The bloom passes are drawn outside FillStyled's body path, so they are the two easiest colours for a
            // fade to miss. A hovered Modern button draws both.
            byte[] empty = CaptureNothing();
            byte[] transparent = Capture(b => b.Opacity = 0f, GuiStyle.Modern, hover: true);
            byte[] opaque = Capture(configure: null, GuiStyle.Modern, hover: true);

            Assert.Equal(empty, transparent);
            Assert.False(opaque.AsSpan().SequenceEqual(empty), "a hovered Modern button must draw its bloom");
        }

        [GpuFact]
        public void The_per_state_tints_reach_the_draw_and_an_untinted_style_is_unchanged()
        {
            GuiStyle plain = GuiStyle.Legacy;   // flat: no bloom in the way of the comparison

            GuiStyle hoverTinted = plain;
            hoverTinted.HoverBorder = new Vector4(1f, 0f, 0f, 1f);
            hoverTinted.HoverText = new Vector4(1f, 0.4f, 0f, 1f);

            GuiStyle pressTinted = plain;
            pressTinted.PressBorder = new Vector4(0f, 1f, 0f, 1f);
            pressTinted.PressText = new Vector4(0.2f, 1f, 0.2f, 1f);

            GuiStyle disabledTinted = plain;
            disabledTinted.DisabledBorder = new Vector4(0f, 0f, 1f, 1f);

            byte[] hoverPlain = CaptureDirect(plain, enabled: true, hover: true, press: false);
            byte[] hoverTint = CaptureDirect(hoverTinted, enabled: true, hover: true, press: false);
            byte[] pressPlain = CaptureDirect(plain, enabled: true, hover: true, press: true);
            byte[] pressTint = CaptureDirect(pressTinted, enabled: true, hover: true, press: true);
            byte[] disPlain = CaptureDirect(plain, enabled: false, hover: false, press: false);
            byte[] disTint = CaptureDirect(disabledTinted, enabled: false, hover: false, press: false);

            Assert.False(hoverTint.AsSpan().SequenceEqual(hoverPlain), "a hover tint must reach the draw");
            Assert.False(pressTint.AsSpan().SequenceEqual(pressPlain), "a press tint must reach the draw");
            Assert.False(disTint.AsSpan().SequenceEqual(disPlain), "a disabled border tint must reach the draw");

            // A style with the tints unset renders identically in the state they would have applied to and in the
            // resting state's own border/text, which is what makes the addition a no-op for every existing caller.
            Assert.Equal(hoverPlain, CaptureDirect(plain, enabled: true, hover: true, press: false));

            // And an untinted style's hover state is unaffected by tints that belong to OTHER states.
            GuiStyle pressOnly = plain;
            pressOnly.PressBorder = new Vector4(0f, 1f, 0f, 1f);
            Assert.Equal(hoverPlain, CaptureDirect(pressOnly, enabled: true, hover: true, press: false));
        }

        // A retained Button drawn through Button.Draw. `configure` null leaves every field at its default, so the
        // "no-op default" capture exercises the untouched field rather than an explicitly assigned 1f.
        static byte[] Capture(Action<Button>? configure, GuiStyle? style = null, bool hover = false) =>
            Render2DSnapshot.Capture(W, H, new Color(0, 0, 0, 1), ctx =>
            {
                SpriteFont font = ctx.LoadFont(FontPath, 20f, oversample: 1);
                Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
                var vp = new DesignViewport(W, H, ScaleMode.Fit);
                vp.Update(W, H);
                ctx.Batch.Begin(vp);

                var button = new Button(ButtonRect, LocalizedText.Raw("Go"), font);
                if (style is { } s) button.Style = s;
                if (hover) button.Update(HoveringPointer());
                configure?.Invoke(button);
                button.Draw(ctx.Batch, white);

                ctx.Batch.End();
            });

        // The same scene with no button drawn at all: the "nothing painted" baseline a fully faded button must match.
        static byte[] CaptureNothing() =>
            Render2DSnapshot.Capture(W, H, new Color(0, 0, 0, 1), ctx =>
            {
                var vp = new DesignViewport(W, H, ScaleMode.Fit);
                vp.Update(W, H);
                ctx.Batch.Begin(vp);
                ctx.Batch.End();
            });

        // The internal DrawButton call directly, so an exact interaction state can be pinned without driving input.
        static byte[] CaptureDirect(GuiStyle style, bool enabled, bool hover, bool press) =>
            Render2DSnapshot.Capture(W, H, new Color(0, 0, 0, 1), ctx =>
            {
                SpriteFont font = ctx.LoadFont(FontPath, 20f, oversample: 1);
                Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
                var vp = new DesignViewport(W, H, ScaleMode.Fit);
                vp.Update(W, H);
                ctx.Batch.Begin(vp);
                GuiDraw.DrawButton(ctx.Batch, white, font, ButtonRect, LocalizedText.Raw("Go"), style,
                    enabled, selected: false, hover, press);
                ctx.Batch.End();
            });

        // A pointer parked over the button with no button held: hover on, press off.
        static Pointer HoveringPointer()
        {
            var pointer = new Pointer();
            pointer.Update(new InputState(
                new System.Collections.Generic.HashSet<Key>(), new System.Collections.Generic.HashSet<Key>(),
                new System.Collections.Generic.HashSet<Key>(), new System.Collections.Generic.HashSet<MouseButton>(),
                new System.Collections.Generic.HashSet<MouseButton>(),
                new Vector2(ButtonRect.X + ButtonRect.Width * 0.5f, ButtonRect.Y + ButtonRect.Height * 0.5f),
                Vector2.Zero, 0f, W, H));
            return pointer;
        }
    }
}
