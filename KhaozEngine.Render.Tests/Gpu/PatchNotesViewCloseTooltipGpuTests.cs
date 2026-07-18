using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // Feature proof: PatchNotesStrings.Close is wired as the close button's hover tooltip, driven off the
    // same _closeHover state Update() already tracks for the button's own highlight. Renders the panel twice
    // (pointer parked over the close button, then parked far away) and asserts the region Tooltip.ComputeBounds
    // says the bubble occupies actually changed - not just "some pixel differs somewhere" (the button's own
    // hover highlight already does that), but specifically the tooltip's own footprint.
    public sealed class PatchNotesViewCloseTooltipGpuTests
    {
        const int W = 480, H = 480;

        static readonly string FontPath = System.IO.Path.Combine(
            System.AppContext.BaseDirectory, "Assets", "Roboto-Regular.ttf");

        static InputState Frame(Vector2 pos) =>
            new(new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                new HashSet<MouseButton>(), new HashSet<MouseButton>(), pos, Vector2.Zero, 0f, W, H);

        [GpuFact]
        public void Hovering_the_close_button_shows_its_tooltip_and_not_hovering_does_not()
        {
            var viewport = new Rect(0, 0, W, H);
            var probe = new PatchNotesView(PatchNotesDocument.Empty);
            Rect closeButton = probe.CloseButtonRect(viewport);
            var hoverAt = new Vector2(closeButton.X + closeButton.Width * 0.5f, closeButton.Y + closeButton.Height * 0.5f);
            var farAway = new Vector2(-1000f, -1000f); // off the panel and off the window: no hover anywhere

            Rect? tooltipRect = null;

            byte[] Render(Vector2 pointerAt)
            {
                var view = new PatchNotesView(PatchNotesDocument.Empty);
                return Render2DSnapshot.Capture(W, H, new Color(0, 0, 0, 1), ctx =>
                {
                    Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
                    SpriteFont font = ctx.LoadFont(FontPath, 16f, oversample: 1);

                    var pointer = new Pointer();
                    InputState frame = Frame(pointerAt);
                    pointer.Update(frame);
                    view.Update(pointer, frame, 0.016f, viewport, font);

                    // The exact bubble footprint Tooltip itself would compute for this anchor/content -
                    // the same anchor DrawCloseTooltip uses (button top-centre): captured once, from the
                    // hovered render, since the close string and font are identical either way.
                    var anchor = new Vector2(closeButton.X + closeButton.Width * 0.5f, closeButton.Y);
                    tooltipRect ??= Tooltip.ComputeBounds(font, "Close", font, Array.Empty<TooltipLine>(),
                        anchor, new Vector2(W, H), TooltipMetrics.Default);

                    var vp = new DesignViewport(W, H, ScaleMode.Fit);
                    vp.Update(W, H);
                    ctx.Batch.Begin(vp);
                    view.Draw(ctx.Batch, font, white, viewport);
                    ctx.Batch.End();
                });
            }

            byte[] hovered = Render(hoverAt);
            byte[] idle = Render(farAway);

            Assert.NotNull(tooltipRect);
            Rect r = tooltipRect!.Value;
            int x0 = Math.Max(0, (int)r.X), y0 = Math.Max(0, (int)r.Y);
            int x1 = Math.Min(W, (int)r.Right), y1 = Math.Min(H, (int)r.Bottom);
            Assert.True(x1 > x0 && y1 > y0, "the computed tooltip bounds must fall inside the capture");

            bool anyDifference = false;
            for (int y = y0; y < y1 && !anyDifference; y++)
            for (int x = x0; x < x1 && !anyDifference; x++)
            {
                int i = (y * W + x) * 4;
                if (hovered[i] != idle[i] || hovered[i + 1] != idle[i + 1] || hovered[i + 2] != idle[i + 2])
                    anyDifference = true;
            }

            Assert.True(anyDifference, "the tooltip's own footprint should render differently while hovering the close button");
        }
    }
}
