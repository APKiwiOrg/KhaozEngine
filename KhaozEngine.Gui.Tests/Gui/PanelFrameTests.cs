using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gui
{
    /// <summary>
    /// <see cref="PanelFrame"/>'s geometry: the frame inset, the strip / title / content / footer stack, the close
    /// square and the centred title origin. Written against the shipped numbers in
    /// <see cref="PanelFrameMetrics.Default"/> rather than a re-derivation of them, because the point of a shared
    /// frame is that one panel's frame is every panel's frame: moving one of these has to be a deliberate edit
    /// here too.
    /// </summary>
    public class PanelFrameTests
    {
        // An arbitrary panel rect. Nothing here is a real window's placement rule: the frame is pure geometry
        // over whatever bounds a caller hands it, and each window's natural-bounds rule stays in the game.
        static readonly Rect Bounds = new(120f, 80f, 308f, 460f);

        // A two-row tab strip's worth of band, and a footer, as literals: this is about the bands stacking, not
        // about who fills them.
        const float StripHeight = 56f;
        const float FooterHeight = 74f;

        [Fact]
        public void The_frame_insets_the_inner_rect_on_all_four_sides()
        {
            Rect inner = PanelFrame.Inner(Bounds);

            Assert.Equal(Bounds.X + 6f, inner.X, 3);
            Assert.Equal(Bounds.Y + 6f, inner.Y, 3);
            Assert.Equal(308f - 12f, inner.Width, 3);
            Assert.Equal(460f - 12f, inner.Height, 3);

            // A panel narrower than its own frame collapses to zero rather than to a negative extent.
            Rect tiny = PanelFrame.Inner(new Rect(0f, 0f, 4f, 4f));
            Assert.Equal(0f, tiny.Width, 3);
            Assert.Equal(0f, tiny.Height, 3);
        }

        [Fact]
        public void The_strip_the_title_the_content_and_the_footer_stack_with_no_gap_and_no_overlap()
        {
            Rect inner = PanelFrame.Inner(Bounds);
            Rect strip = PanelFrame.StripRect(Bounds, StripHeight);
            Rect title = PanelFrame.TitleRect(Bounds, StripHeight);
            Rect content = PanelFrame.ContentRect(Bounds, StripHeight, FooterHeight);
            Rect footer = PanelFrame.FooterRect(Bounds, FooterHeight);

            Assert.Equal(StripHeight, strip.Height, 3);
            Assert.Equal(inner.Y, strip.Y, 3);
            Assert.Equal(strip.Bottom, title.Y, 3);
            Assert.Equal(26f, title.Height, 3);
            Assert.Equal(title.Bottom, content.Y, 3);
            Assert.Equal(footer.Y, content.Bottom, 3);
            Assert.Equal(inner.Bottom, footer.Bottom, 3);
            Assert.Equal(FooterHeight, footer.Height, 3);

            // Every band spans the inner width, so nothing is drawn over the frame.
            foreach (Rect band in new[] { strip, title, content, footer })
            {
                Assert.Equal(inner.X, band.X, 3);
                Assert.Equal(inner.Width, band.Width, 3);
            }
        }

        [Fact]
        public void An_absent_band_collapses_to_zero_height_instead_of_leaving_a_hole()
        {
            Rect inner = PanelFrame.Inner(Bounds);

            // No footer: the content runs to the frame, and it gains exactly the footer's height.
            Rect withFooter = PanelFrame.ContentRect(Bounds, StripHeight, FooterHeight);
            Rect without = PanelFrame.ContentRect(Bounds, StripHeight);
            Assert.Equal(withFooter.Y, without.Y, 3);
            Assert.Equal(inner.Bottom, without.Bottom, 3);
            Assert.Equal(FooterHeight, without.Height - withFooter.Height, 3);
            Assert.Equal(0f, PanelFrame.FooterRect(Bounds, 0f).Height, 3);

            // No strip: the title row starts at the top of the inner rect, with nothing left above it.
            Rect strip = PanelFrame.StripRect(Bounds, 0f);
            Rect title = PanelFrame.TitleRect(Bounds, 0f);
            Assert.Equal(0f, strip.Height, 3);
            Assert.Equal(inner.Y, title.Y, 3);
            Assert.Equal(strip.Bottom, title.Y, 3);

            // A negative band is read as an absent one rather than eating into its neighbour.
            Assert.Equal(title.Y, PanelFrame.TitleRect(Bounds, -40f).Y, 3);
            Assert.Equal(inner.Bottom, PanelFrame.FooterRect(Bounds, -40f).Y, 3);
        }

        [Fact]
        public void The_close_square_sits_inside_the_title_row_at_the_right()
        {
            Rect title = PanelFrame.TitleRect(Bounds, StripHeight);
            Rect close = PanelFrame.CloseRect(Bounds, StripHeight);

            Assert.Equal(20f, close.Width, 3);
            Assert.Equal(20f, close.Height, 3);
            Assert.Equal(title.Right - 8f - 20f, close.X, 3);
            Assert.True(close.Y >= title.Y && close.Bottom <= title.Bottom, "the close button left the row");
            // Vertically centred in the row, so it reads as part of it rather than as a badge on the edge.
            Assert.Equal(title.Y + (title.Height - close.Height) * 0.5f, close.Y, 3);
        }

        [Fact]
        public void A_title_is_centred_in_its_row_on_both_axes()
        {
            Rect row = PanelFrame.TitleRect(Bounds, StripHeight);
            Vector2 origin = PanelFrame.TitleTextOrigin(row, new Vector2(120f, 18f));

            Assert.Equal(row.X + (row.Width - 120f) * 0.5f, origin.X, 3);
            Assert.Equal(row.Y + (row.Height - 18f) * 0.5f, origin.Y, 3);

            // Text wider than the row starts at the row's edge rather than outside it.
            Vector2 wide = PanelFrame.TitleTextOrigin(row, new Vector2(row.Width + 40f, 18f));
            Assert.Equal(row.X, wide.X, 3);
        }

        [Fact]
        public void The_callers_metrics_drive_every_band_so_two_themes_can_share_the_frame()
        {
            var metrics = new PanelFrameMetrics(
                FrameThickness: 2f, Bevel: 1f, TitleHeight: 40f, CloseSize: 12f, TextPad: 4f);

            Rect inner = PanelFrame.Inner(Bounds, metrics);
            Assert.Equal(Bounds.X + 2f, inner.X, 3);
            Assert.Equal(308f - 4f, inner.Width, 3);

            Rect title = PanelFrame.TitleRect(Bounds, StripHeight, metrics);
            Assert.Equal(40f, title.Height, 3);

            Rect close = PanelFrame.CloseRect(Bounds, StripHeight, metrics);
            Assert.Equal(12f, close.Width, 3);
            Assert.Equal(title.Right - 4f - 12f, close.X, 3);

            // The stack still has no gap under the caller's own numbers.
            Rect content = PanelFrame.ContentRect(Bounds, StripHeight, FooterHeight, metrics);
            Assert.Equal(title.Bottom, content.Y, 3);
            Assert.Equal(PanelFrame.FooterRect(Bounds, FooterHeight, metrics).Y, content.Bottom, 3);

            // Passing nothing is the shipped shape, so a consumer that wants today's look writes no numbers.
            Assert.Equal(PanelFrame.Inner(Bounds, PanelFrameMetrics.Default), PanelFrame.Inner(Bounds));
            Assert.Equal(6f, PanelFrameMetrics.Default.FrameThickness, 3);
            Assert.Equal(2f, PanelFrameMetrics.Default.Bevel, 3);
            Assert.Equal(26f, PanelFrameMetrics.Default.TitleHeight, 3);
            Assert.Equal(20f, PanelFrameMetrics.Default.CloseSize, 3);
            Assert.Equal(8f, PanelFrameMetrics.Default.TextPad, 3);
        }
    }
}
