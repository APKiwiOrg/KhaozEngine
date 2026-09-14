namespace KhaozEngine.Render2D;

/// <summary>
/// Where a stroke sits relative to the edges of the shape it outlines. Used by
/// <see cref="PrimitiveRenderer.StrokeConvexPolygon"/>.
/// </summary>
public enum StrokeAlignment
{
    /// <summary>The stroke lies entirely inside the shape: its outer edge is the shape's edge.</summary>
    Inside,

    /// <summary>The stroke straddles the shape's edge, half its thickness on each side.</summary>
    Center,

    /// <summary>The stroke lies entirely outside the shape: its inner edge is the shape's edge.</summary>
    Outside,
}
