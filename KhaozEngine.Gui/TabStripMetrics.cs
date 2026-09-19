using System;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// The shape of one WRAPPED tab strip: how big a tab is, how many fit a row, the gutter between them, and
    /// where the resulting block of tabs sits in its band. Supplied by the caller rather than read from
    /// <see cref="GuiTheme"/>, for the same reason <see cref="PanelFrameMetrics"/> is: a size is a per-widget
    /// decision (one game's strip is two centred rows of five 56 by 26 tabs, another's is one left-aligned row of
    /// five wide ones) while the palette is global, so keeping them apart lets two games with different themes
    /// share the same strip code.
    /// </summary>
    /// <remarks>
    /// The same five numbers a <see cref="TabBar"/> carries as properties (<see cref="TabBar.TabWidth"/>,
    /// <see cref="TabBar.TabHeight"/>, <see cref="TabBar.Spacing"/>, <see cref="TabBar.Columns"/> and
    /// <see cref="TabBar.BlockAlign"/>), as one value. <see cref="TabBar.Metrics"/> hands it over, so a consumer
    /// that hit-tests through <see cref="TabStrip"/> with no widget instance aims at the rect the widget draws.
    /// </remarks>
    /// <param name="TabWidth">One tab's width.</param>
    /// <param name="TabHeight">One tab's height.</param>
    /// <param name="Spacing">The gap between two tabs, on both axes.</param>
    /// <param name="Columns">Tabs per row, which is where the strip wraps. Zero puts every tab in one row.</param>
    /// <param name="Align">Where the block of tabs sits horizontally in its band. Left is the anchored default.</param>
    public readonly record struct TabStripMetrics(
        float TabWidth,
        float TabHeight,
        float Spacing,
        int Columns,
        GuiAlign Align = GuiAlign.Left)
    {
        /// <summary>How many columns a strip of <paramref name="count"/> tabs actually wraps at: a
        /// <see cref="Columns"/> wider than the roster shrinks to the roster, and zero means one row.</summary>
        /// <param name="count">How many tabs the strip is drawing.</param>
        /// <returns>The column count, never below one.</returns>
        public int ColumnsFor(int count)
        {
            int all = Math.Max(1, count);
            return Columns > 0 ? Math.Min(Columns, all) : all;
        }

        /// <summary>How many rows a strip of <paramref name="count"/> tabs wraps into.</summary>
        /// <param name="count">How many tabs the strip is drawing.</param>
        /// <returns>The row count, zero for an empty strip.</returns>
        public int RowsFor(int count) =>
            count <= 0 ? 0 : (count + ColumnsFor(count) - 1) / ColumnsFor(count);

        /// <summary>How wide the block of tabs is: a full row and the gutters inside it.</summary>
        /// <param name="count">How many tabs the strip is drawing.</param>
        /// <returns>The block's width in the band's units.</returns>
        public float BlockWidth(int count)
        {
            int columns = ColumnsFor(count);
            return columns * TabWidth + (columns - 1) * Spacing;
        }

        /// <summary>How tall the block of tabs is: every row and the gutters between them.</summary>
        /// <param name="count">How many tabs the strip is drawing.</param>
        /// <returns>The block's height in the band's units, zero for an empty strip.</returns>
        public float BlockHeight(int count)
        {
            int rows = RowsFor(count);
            return rows <= 0 ? 0f : rows * TabHeight + (rows - 1) * Spacing;
        }
    }
}
