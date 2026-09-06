using System;

namespace KhaozEngine.Primitives;

/// <summary>
/// Insets in design-space units for notches, rounded corners and system UI. Platform hosts supply
/// these values after converting device insets into the viewport's design units. Apply them to
/// <see cref="IDesignViewport.DesignBounds"/> before resolving anchored UI.
/// </summary>
public readonly record struct SafeAreaInsets
{
    public float Top { get; }
    public float Bottom { get; }
    public float Left { get; }
    public float Right { get; }

    /// <summary>No obscured edges.</summary>
    public static readonly SafeAreaInsets Zero = default;

    /// <summary>Creates finite, non-negative design-space insets.</summary>
    public SafeAreaInsets(float top, float bottom, float left, float right)
    {
        Validate(top, nameof(top));
        Validate(bottom, nameof(bottom));
        Validate(left, nameof(left));
        Validate(right, nameof(right));
        Top = top;
        Bottom = bottom;
        Left = left;
        Right = right;
    }

    /// <summary>
    /// Shrinks <paramref name="bounds"/> by the insets. An axis consumed by opposing insets has
    /// zero size. Its origin is the inset top or left edge, clamped inside the original bounds.
    /// Insets are supplied by the host, this method does not query a platform or change a viewport.
    /// </summary>
    public Rect Apply(Rect bounds)
    {
        Validate(bounds.Width, nameof(bounds));
        Validate(bounds.Height, nameof(bounds));
        float left = MathF.Min(Left, bounds.Width);
        float top = MathF.Min(Top, bounds.Height);
        return new Rect(bounds.X + left, bounds.Y + top,
            MathF.Max(0, bounds.Width - left - Right),
            MathF.Max(0, bounds.Height - top - Bottom));
    }

    static void Validate(float value, string name)
    {
        if (!float.IsFinite(value) || value < 0)
            throw new ArgumentOutOfRangeException(name, value, "A finite, non-negative value is required.");
    }
}
