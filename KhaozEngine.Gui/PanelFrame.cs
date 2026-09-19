using System;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// A NON-MODAL titled window frame: a bevelled border band, an optional tab-strip band, a title row carrying a
    /// centred localized title with an optional right-aligned readout and close button, and the content rect that
    /// is left over. Use it instead of <see cref="PopupPanel"/> whenever the world underneath must stay visible and
    /// live - a side panel, a bank window, an inventory window. <see cref="PopupPanel"/> is modal: it paints a scrim
    /// and reserves the whole viewport on the pointer.
    /// </summary>
    /// <remarks>
    /// A pure geometry-plus-draw static, deliberately not a retained widget. Every band is a pure rect function a
    /// consumer can call with no instance to hit-test with, and the draws walk exactly those functions, so the rect
    /// a click-through guard tests and the rect the player sees cannot drift. Nothing here holds state, because the
    /// frame is the same every frame and only what is written in it changes.
    /// <para>The bands STACK: <see cref="Inner"/>, then <see cref="StripRect"/>, then <see cref="TitleRect"/>, then
    /// <see cref="ContentRect"/>, then <see cref="FooterRect"/>, each derived from the one above it with no gap and
    /// no overlap. An absent band collapses to zero height rather than leaving a hole, so a panel with no strip and
    /// no footer runs its content from the title row straight to the frame.</para>
    /// <para>It reserves NOTHING on the <see cref="KhaozEngine.Windowing.Pointer"/>. Which taps a window swallows
    /// is the window's call, not the frame's: a caller that wants the world ignored under its panel calls
    /// <see cref="KhaozEngine.Windowing.Pointer.BlockRegion"/> with its OWN bounds, and one that wants a
    /// click-through stripe leaves it alone. Blocking here would make every consumer modal over its own rect.</para>
    /// <para>Built on the public <see cref="GuiDraw"/> primitives rather than a <see cref="GuiSkin"/> nine-slice, so
    /// the procedural bevel is the whole of the frame and no skin texture has to ship. Sizes come from a caller's
    /// <see cref="PanelFrameMetrics"/> and colours from a <see cref="GuiTheme"/>, so two games with different looks
    /// share this code.</para>
    /// </remarks>
    public static class PanelFrame
    {
        /// <summary>The rect inside the frame band: the whole panel less the frame on all four sides.</summary>
        /// <param name="bounds">The panel's outer bounds.</param>
        /// <param name="metrics">The frame's sizes, or null for <see cref="PanelFrameMetrics.Default"/>.</param>
        public static Rect Inner(in Rect bounds, PanelFrameMetrics? metrics = null)
        {
            float thickness = (metrics ?? PanelFrameMetrics.Default).FrameThickness;
            return new Rect(
                bounds.X + thickness, bounds.Y + thickness,
                MathF.Max(0f, bounds.Width - thickness * 2f),
                MathF.Max(0f, bounds.Height - thickness * 2f));
        }

        /// <summary>The tab strip's band, at the top of the inner rect. Zero height when the panel has no
        /// strip.</summary>
        /// <param name="bounds">The panel's outer bounds.</param>
        /// <param name="stripHeight">How tall the strip is, or zero for no strip.</param>
        /// <param name="metrics">The frame's sizes, or null for <see cref="PanelFrameMetrics.Default"/>.</param>
        public static Rect StripRect(in Rect bounds, float stripHeight, PanelFrameMetrics? metrics = null)
        {
            Rect inner = Inner(bounds, metrics);
            return new Rect(inner.X, inner.Y, inner.Width, MathF.Max(0f, stripHeight));
        }

        /// <summary>The title row, under the strip.</summary>
        /// <param name="bounds">The panel's outer bounds.</param>
        /// <param name="stripHeight">How tall the strip is, or zero for no strip.</param>
        /// <param name="metrics">The frame's sizes, or null for <see cref="PanelFrameMetrics.Default"/>.</param>
        public static Rect TitleRect(in Rect bounds, float stripHeight, PanelFrameMetrics? metrics = null)
        {
            Rect inner = Inner(bounds, metrics);
            return new Rect(inner.X, inner.Y + MathF.Max(0f, stripHeight), inner.Width,
                (metrics ?? PanelFrameMetrics.Default).TitleHeight);
        }

        /// <summary>The close button's square, right-aligned in the title row and inset from the frame by the same
        /// margin the title is.</summary>
        /// <param name="bounds">The panel's outer bounds.</param>
        /// <param name="stripHeight">How tall the strip is, or zero for no strip.</param>
        /// <param name="metrics">The frame's sizes, or null for <see cref="PanelFrameMetrics.Default"/>.</param>
        public static Rect CloseRect(in Rect bounds, float stripHeight, PanelFrameMetrics? metrics = null)
        {
            PanelFrameMetrics m = metrics ?? PanelFrameMetrics.Default;
            Rect title = TitleRect(bounds, stripHeight, m);
            float side = m.CloseSize;
            return new Rect(title.Right - m.TextPad - side, title.Y + (title.Height - side) * 0.5f, side, side);
        }

        /// <summary>The content rect: everything under the title row and above the footer.</summary>
        /// <param name="bounds">The panel's outer bounds.</param>
        /// <param name="stripHeight">How tall the strip is, or zero for no strip.</param>
        /// <param name="footerHeight">How tall the footer is, or zero for no footer.</param>
        /// <param name="metrics">The frame's sizes, or null for <see cref="PanelFrameMetrics.Default"/>.</param>
        public static Rect ContentRect(in Rect bounds, float stripHeight, float footerHeight = 0f,
            PanelFrameMetrics? metrics = null)
        {
            Rect inner = Inner(bounds, metrics);
            Rect title = TitleRect(bounds, stripHeight, metrics);
            float top = title.Bottom;
            float bottom = inner.Bottom - MathF.Max(0f, footerHeight);
            return new Rect(inner.X, top, inner.Width, MathF.Max(0f, bottom - top));
        }

        /// <summary>The footer rect: the bottom band of the inner rect. Zero height when the panel has no
        /// footer.</summary>
        /// <param name="bounds">The panel's outer bounds.</param>
        /// <param name="footerHeight">How tall the footer is, or zero for no footer.</param>
        /// <param name="metrics">The frame's sizes, or null for <see cref="PanelFrameMetrics.Default"/>.</param>
        public static Rect FooterRect(in Rect bounds, float footerHeight, PanelFrameMetrics? metrics = null)
        {
            Rect inner = Inner(bounds, metrics);
            float height = MathF.Max(0f, footerHeight);
            return new Rect(inner.X, inner.Bottom - height, inner.Width, height);
        }

        /// <summary>Where a title starts so it is centred in its row on both axes.</summary>
        /// <param name="row">The title row, from <see cref="TitleRect"/>.</param>
        /// <param name="measured">The title's measured size.</param>
        public static Vector2 TitleTextOrigin(in Rect row, Vector2 measured) => new(
            row.X + MathF.Max(0f, (row.Width - measured.X) * 0.5f),
            row.Y + MathF.Max(0f, (row.Height - measured.Y) * 0.5f));

        /// <summary>Queue the frame: the border band, its lit lip, the dark hairline where the band meets the body,
        /// and the body fill inside it. Draws no scrim, so whatever is behind the panel stays visible.</summary>
        /// <param name="batch">An in-progress sprite batch.</param>
        /// <param name="white">A one by one white texture for the fills.</param>
        /// <param name="bounds">The panel's outer bounds.</param>
        /// <param name="metrics">The frame's sizes, or null for <see cref="PanelFrameMetrics.Default"/>.</param>
        /// <param name="theme">The palette, or null for <see cref="GuiTheme.Default"/>.</param>
        public static void DrawFrame(SpriteBatch batch, Texture2D white, in Rect bounds,
            PanelFrameMetrics? metrics = null, GuiTheme? theme = null)
        {
            if (batch is null || white is null) return;
            PanelFrameMetrics m = metrics ?? PanelFrameMetrics.Default;
            GuiTheme t = theme ?? GuiTheme.Default;
            // Outside in: the LIT lip first, on the outermost ring of the band, then the band itself, then one dark
            // hairline where the band meets the body, then the body. The other order (lit ring inside the band, dark
            // line outermost) lights the frame from the panel rather than from the room, and reads as a groove
            // instead of a raised edge.
            GuiDraw.Fill(batch, white, bounds, t.Border);
            GuiDraw.Border(batch, white, bounds, m.Bevel, t.BorderHover);
            Rect inner = Inner(bounds, m);
            var hairline = new Rect(inner.X - 1f, inner.Y - 1f, inner.Width + 2f, inner.Height + 2f);
            GuiDraw.Border(batch, white, hairline, 1f, t.BorderShadow);
            GuiDraw.Fill(batch, white, inner, t.Surface);
        }

        /// <summary>Queue the title row: its band, the centred title, an optional right-aligned readout, and an
        /// optional close button right of that.</summary>
        /// <param name="batch">An in-progress sprite batch.</param>
        /// <param name="font">The font the row renders with.</param>
        /// <param name="white">A one by one white texture for the fills.</param>
        /// <param name="bounds">The panel's outer bounds.</param>
        /// <param name="stripHeight">How tall the tab strip is, or zero for no strip.</param>
        /// <param name="title">The panel's title.</param>
        /// <param name="readout">A right-aligned readout, or <c>default</c> for none. Usually a template resolved
        /// with numbers in it, so build it with <c>LocalizedText.Of(id, args)</c>.</param>
        /// <param name="showClose">Whether to draw the close button.</param>
        /// <param name="closeHovered">Whether the pointer is over the close button.</param>
        /// <param name="metrics">The frame's sizes, or null for <see cref="PanelFrameMetrics.Default"/>.</param>
        /// <param name="theme">The palette, or null for <see cref="GuiTheme.Default"/>.</param>
        public static void DrawTitle(SpriteBatch batch, SpriteFont font, Texture2D white, in Rect bounds,
            float stripHeight, LocalizedText title, LocalizedText readout = default, bool showClose = false,
            bool closeHovered = false, PanelFrameMetrics? metrics = null, GuiTheme? theme = null)
        {
            if (batch is null || font is null || white is null) return;
            PanelFrameMetrics m = metrics ?? PanelFrameMetrics.Default;
            GuiTheme t = theme ?? GuiTheme.Default;
            Rect row = TitleRect(bounds, stripHeight, m);
            // No band rule under the row: the title row is the body colour with a line of text on it, and a border
            // plus a rule here reads as a second frame inside the frame.
            GuiDraw.Fill(batch, white, row, t.TitleFill);

            string resolvedTitle = title.Resolve();
            Vector2 titleAt = TitleTextOrigin(row, font.Measure(resolvedTitle));
            batch.DrawString(font, resolvedTitle, titleAt, (Color)t.Text);

            float right = row.Right - m.TextPad;
            if (showClose)
            {
                Rect close = CloseRect(bounds, stripHeight, m);
                DrawClose(batch, white, close, closeHovered, t);
                right = close.X - m.TextPad;
            }
            string resolvedReadout = readout.Resolve();
            if (string.IsNullOrEmpty(resolvedReadout)) return;
            float width = font.Measure(resolvedReadout).X;
            float textY = row.Y + (row.Height - font.LineHeight) * 0.5f;
            batch.DrawString(font, resolvedReadout, new Vector2(right - width, textY), (Color)t.TextMuted);
        }

        // How far the cross's arms stop short of the close square's corners, as a share of the square. A quarter
        // is the shipped 5 units at the default 20 unit square, and it scales with a caller's own CloseSize: a
        // fixed inset turns the cross into a blob on a 12 unit square and erases it on a 10 unit one.
        const float CrossInsetShare = 0.25f;

        /// <summary>Queue the close button: a bordered square with a cross in it. The cross is two lines rather than
        /// a glyph, so it needs no font and no catalog entry.</summary>
        /// <param name="batch">An in-progress sprite batch.</param>
        /// <param name="white">A one by one white texture for the fills.</param>
        /// <param name="rect">The button's square, from <see cref="CloseRect"/>.</param>
        /// <param name="hovered">Whether the pointer is over it.</param>
        /// <param name="theme">The palette, or null for <see cref="GuiTheme.Default"/>.</param>
        public static void DrawClose(SpriteBatch batch, Texture2D white, in Rect rect, bool hovered,
            GuiTheme? theme = null)
        {
            if (batch is null || white is null) return;
            GuiTheme t = theme ?? GuiTheme.Default;
            GuiDraw.Fill(batch, white, rect, hovered ? t.Accent : t.Border);
            GuiDraw.Border(batch, white, rect, 1f, t.BorderHover);
            Vector4 arm = hovered ? t.Surface : t.Text;
            float inset = MathF.Min(rect.Width, rect.Height) * CrossInsetShare;
            GuiDraw.Line(batch, white, new Vector2(rect.X + inset, rect.Y + inset),
                new Vector2(rect.Right - inset, rect.Bottom - inset), 2f, arm);
            GuiDraw.Line(batch, white, new Vector2(rect.Right - inset, rect.Y + inset),
                new Vector2(rect.X + inset, rect.Bottom - inset), 2f, arm);
        }
    }
}
