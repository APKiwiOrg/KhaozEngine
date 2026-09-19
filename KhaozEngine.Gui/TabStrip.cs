using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// The pure geometry of a tab strip, callable with NO widget instance: where tab <c>i</c> is in a band, and
    /// which tab a point is on. <see cref="TabBar"/> is the retained widget over exactly these functions, so a
    /// consumer that keeps no widget (a window that draws its own strip, a test harness aiming at a tab) gets the
    /// same rects the widget hit-tests and draws.
    /// </summary>
    /// <remarks>
    /// Statics rather than instance members for the reason <see cref="PanelFrame"/> is a static: nothing here
    /// holds state, and a hit test that has to construct a widget first is a hit test that can disagree with the
    /// one the widget does. <see cref="TabBar.TabRect"/> and <see cref="TabBar.ContentBounds"/> call straight into
    /// these, so there is one copy of the arithmetic.
    /// <para>Two layouts, matching the widget's two: the WRAPPED one, fixed-size tabs from a
    /// <see cref="TabStripMetrics"/> that wrap through its columns with a gutter between them, and the EVEN one,
    /// <c>count</c> tabs splitting a band with no gutter at all.</para>
    /// </remarks>
    public static class TabStrip
    {
        /// <summary>The footprint of the whole wrapped block inside its band: the rect every tab lands in, placed
        /// per <see cref="TabStripMetrics.Align"/>. A block wider than its band starts at the band's edge rather
        /// than outside it.</summary>
        /// <param name="band">The strip band the tabs lay out in.</param>
        /// <param name="count">How many tabs the strip is drawing.</param>
        /// <param name="metrics">The strip's shape.</param>
        /// <returns>The block's rect, which is what a caller reserves on the pointer.</returns>
        public static Rect BlockRect(in Rect band, int count, in TabStripMetrics metrics)
        {
            float width = metrics.BlockWidth(count);
            float x = metrics.Align switch
            {
                GuiAlign.Center => band.X + MathF.Max(0f, (band.Width - width) * 0.5f),
                GuiAlign.Right => band.X + MathF.Max(0f, band.Width - width),
                _ => band.X,
            };
            return new Rect(x, band.Y, width, metrics.BlockHeight(count));
        }

        /// <summary>One tab's rect in a wrapped strip, rows filling left to right then top to bottom.</summary>
        /// <param name="band">The strip band the tabs lay out in.</param>
        /// <param name="index">The tab, from zero.</param>
        /// <param name="count">How many tabs the strip is drawing.</param>
        /// <param name="metrics">The strip's shape.</param>
        /// <returns>The tab's rect, <see cref="TabStripMetrics.TabWidth"/> by
        /// <see cref="TabStripMetrics.TabHeight"/>.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside
        /// [0, <paramref name="count"/>).</exception>
        public static Rect TabRect(in Rect band, int index, int count, in TabStripMetrics metrics)
        {
            if (index < 0 || index >= count) throw new ArgumentOutOfRangeException(nameof(index));
            int columns = metrics.ColumnsFor(count);
            Rect block = BlockRect(band, count, metrics);
            return new Rect(
                block.X + index % columns * (metrics.TabWidth + metrics.Spacing),
                block.Y + index / columns * (metrics.TabHeight + metrics.Spacing),
                metrics.TabWidth,
                metrics.TabHeight);
        }

        /// <summary>The tab under a point in a wrapped strip. The inverse of <see cref="TabRect"/>, and it answers
        /// a DISABLED tab's index too: swallowing that tap is the caller's rule, not the geometry's.</summary>
        /// <param name="band">The strip band the tabs lay out in.</param>
        /// <param name="point">The pointer position, in the same space as <paramref name="band"/>.</param>
        /// <param name="count">How many tabs the strip is drawing.</param>
        /// <param name="metrics">The strip's shape.</param>
        /// <returns>The tab's index, or -1 for a gutter between tabs and for anywhere outside the block.</returns>
        public static int TabAt(in Rect band, Vector2 point, int count, in TabStripMetrics metrics)
        {
            for (int i = 0; i < count; i++)
            {
                if (TabRect(band, i, count, metrics).Contains(point)) return i;
            }
            return -1;
        }

        /// <summary>One tab's rect in an EVEN strip: <paramref name="count"/> tabs splitting
        /// <paramref name="band"/> with no gutter. Fractional edges (<c>X + Width * i/N</c> ..
        /// <c>X + Width * (i+1)/N</c>) so tabs abut with no cumulative rounding gap and the last tab's right edge
        /// equals the band's right exactly.</summary>
        /// <param name="band">The strip band the tabs split.</param>
        /// <param name="index">The tab, from zero.</param>
        /// <param name="count">How many tabs the strip is drawing.</param>
        /// <returns>The tab's rect, full band height.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not positive, or
        /// <paramref name="index"/> is outside [0, <paramref name="count"/>).</exception>
        public static Rect EvenTabRect(in Rect band, int index, int count)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (index < 0 || index >= count) throw new ArgumentOutOfRangeException(nameof(index));
            float left = band.X + band.Width * index / count;
            float right = band.X + band.Width * (index + 1) / count;
            return new Rect(left, band.Y, right - left, band.Height);
        }

        /// <summary>The tab under a point in an even strip. The inverse of <see cref="EvenTabRect"/>. An even
        /// strip has no gutter, so only a point off the band answers none.</summary>
        /// <param name="band">The strip band the tabs split.</param>
        /// <param name="point">The pointer position, in the same space as <paramref name="band"/>.</param>
        /// <param name="count">How many tabs the strip is drawing.</param>
        /// <returns>The tab's index, or -1 outside the band.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not positive.</exception>
        public static int EvenTabAt(in Rect band, Vector2 point, int count)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
            for (int i = 0; i < count; i++)
            {
                if (EvenTabRect(band, i, count).Contains(point)) return i;
            }
            return -1;
        }
    }
}
