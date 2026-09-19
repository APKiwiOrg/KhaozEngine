namespace KhaozEngine.Gui
{
    /// <summary>
    /// The sizes a <see cref="PanelFrame"/> is built from, supplied by the caller rather than read from
    /// <see cref="GuiTheme"/>. Sizes are a per-widget decision (one game's frame is a 6-unit bevelled band, another's
    /// is a 2-unit hairline) while the palette is global, so keeping them apart lets two games with different
    /// themes share the same frame code. <see cref="Default"/> is the shape the frame shipped with.
    /// </summary>
    /// <param name="FrameThickness">How far the frame band insets the inner rect on all four sides.</param>
    /// <param name="Bevel">Thickness of the lit lip drawn on the outermost ring of the band.</param>
    /// <param name="TitleHeight">Height of the title row.</param>
    /// <param name="CloseSize">Side length of the square close button.</param>
    /// <param name="TextPad">Horizontal padding between a text or button edge and the inner rect's edge.</param>
    public readonly record struct PanelFrameMetrics(
        float FrameThickness,
        float Bevel,
        float TitleHeight,
        float CloseSize,
        float TextPad)
    {
        /// <summary>
        /// The shipped shape: a 6-unit band with a 2-unit bevel, a 26-unit title row, a 20-unit close square and
        /// 8 units of text padding. A caller that passes no metrics gets this, so the frame has one look until a
        /// game asks for another (<c>PanelFrameMetrics.Default with { TitleHeight = 32f }</c>).
        /// </summary>
        public static PanelFrameMetrics Default { get; } = new(
            FrameThickness: 6f,
            Bevel: 2f,
            TitleHeight: 26f,
            CloseSize: 20f,
            TextPad: 8f);
    }
}
