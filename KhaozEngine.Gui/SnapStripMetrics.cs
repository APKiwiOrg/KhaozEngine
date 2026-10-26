namespace KhaozEngine.Gui
{
    /// <summary>Caller-owned dimensions and alignment for a fixed-pitch horizontal strip.</summary>
    /// <param name="ItemWidth">The finite, positive width of each item.</param>
    /// <param name="ItemSpacing">The finite, nonnegative gap between items, with no trailing gap.</param>
    /// <param name="Anchor">The finite fraction of viewport width at which an item's centre is placed,
    /// from zero to one inclusive. The default of 0.5 centres the item.</param>
    /// <remarks>Dimensions use the caller's drawing and pointer coordinate units.
    /// <see cref="SnapStripGeometry"/> validates metrics even for an empty strip. The default struct value
    /// has zero item width and is invalid.</remarks>
    public readonly record struct SnapStripMetrics(float ItemWidth, float ItemSpacing = 0f, float Anchor = 0.5f);
}
