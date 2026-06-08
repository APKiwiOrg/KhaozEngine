using Microsoft.Xna.Framework;
using KhaozEngine.Input;

namespace KhaozEngine.UI;

/// <summary>
/// Predefined anchor points for UI positioning within the virtual resolution space.
/// </summary>
public enum Anchor
{
    TopLeft,
    TopCenter,
    TopRight,
    CenterLeft,
    Center,
    CenterRight,
    BottomLeft,
    BottomCenter,
    BottomRight
}

/// <summary>
/// Resolves UI element positions based on anchor points, offsets, and safe area insets.
/// All calculations operate in virtual resolution coordinates with dynamic height.
/// </summary>
public static class AnchorResolver
{
    /// <summary>
    /// Resolves an anchor point to a virtual-resolution position, accounting for safe area insets.
    /// Uses the dynamic virtual height from the VirtualResolution instance.
    /// </summary>
    /// <param name="anchor">The anchor point.</param>
    /// <param name="offset">Pixel offset from the anchor point in virtual coordinates.</param>
    /// <param name="vr">The virtual resolution instance (provides dynamic height).</param>
    /// <param name="safeArea">Safe area insets to respect.</param>
    /// <returns>The resolved position in virtual resolution coordinates.</returns>
    public static Vector2 Resolve(Anchor anchor, Vector2 offset, VirtualResolution vr, SafeAreaInsets safeArea)
    {
        float safeLeft = safeArea.Left;
        float safeTop = safeArea.Top;
        float safeRight = vr.Width - safeArea.Right;
        float safeBottom = vr.Height - safeArea.Bottom;
        float safeCenterX = (safeLeft + safeRight) / 2f;
        float safeCenterY = (safeTop + safeBottom) / 2f;

        Vector2 anchorPosition = anchor switch
        {
            Anchor.TopLeft => new Vector2(safeLeft, safeTop),
            Anchor.TopCenter => new Vector2(safeCenterX, safeTop),
            Anchor.TopRight => new Vector2(safeRight, safeTop),
            Anchor.CenterLeft => new Vector2(safeLeft, safeCenterY),
            Anchor.Center => new Vector2(safeCenterX, safeCenterY),
            Anchor.CenterRight => new Vector2(safeRight, safeCenterY),
            Anchor.BottomLeft => new Vector2(safeLeft, safeBottom),
            Anchor.BottomCenter => new Vector2(safeCenterX, safeBottom),
            Anchor.BottomRight => new Vector2(safeRight, safeBottom),
            _ => Vector2.Zero
        };

        return anchorPosition + offset;
    }

    /// <summary>
    /// Resolves an anchor point with no safe area insets.
    /// </summary>
    public static Vector2 Resolve(Anchor anchor, Vector2 offset, VirtualResolution vr)
    {
        return Resolve(anchor, offset, vr, SafeAreaInsets.Zero);
    }

    /// <summary>
    /// Resolves a fractional anchor position (0,0 = top-left, 1,1 = bottom-right)
    /// with safe area insets applied and dynamic height.
    /// </summary>
    public static Vector2 ResolveFractional(float fractionX, float fractionY, Vector2 offset,
        VirtualResolution vr, SafeAreaInsets safeArea)
    {
        float safeLeft = safeArea.Left;
        float safeTop = safeArea.Top;
        float safeWidth = vr.Width - safeArea.Left - safeArea.Right;
        float safeHeight = vr.Height - safeArea.Top - safeArea.Bottom;

        float x = safeLeft + safeWidth * fractionX;
        float y = safeTop + safeHeight * fractionY;

        return new Vector2(x, y) + offset;
    }

    /// <summary>
    /// Resolves a fractional anchor with no safe area insets.
    /// </summary>
    public static Vector2 ResolveFractional(float fractionX, float fractionY, Vector2 offset, VirtualResolution vr)
    {
        return ResolveFractional(fractionX, fractionY, offset, vr, SafeAreaInsets.Zero);
    }
}
