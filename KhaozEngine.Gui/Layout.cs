using System;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gui
{
    /// <summary>Where a child rect is anchored within its parent.</summary>
    public enum Anchor
    {
        TopLeft, Top, TopRight,
        Left, Center, Right,
        BottomLeft, Bottom, BottomRight,
        /// <summary>Fill the parent (minus margin); the size argument is ignored.</summary>
        Stretch,
    }

    /// <summary>
    /// Pure anchor-based rect resolution. <see cref="Resolve"/> places a sized child inside a parent rect
    /// (usually the design viewport, or a container panel) from an <see cref="Anchor"/> and a margin inset, so
    /// widgets can be positioned relative to whatever resolution the design viewport is at instead of taking
    /// absolute pixel coordinates. Margin always insets <em>away</em> from the anchored edge(s); on centered
    /// axes it offsets toward the anchor's edge (e.g. <see cref="Anchor.Top"/> margin pushes down).
    /// </summary>
    public static class Layout
    {
        /// <summary>
        /// Places a child at a fractional anchor within <paramref name="parent"/>. Fractions must
        /// lie in [0, 1], with 0 at the near edge, 0.5 at the center and 1 at the far edge. The same
        /// fraction of the child aligns with that point, keeping a fitting child inside its parent.
        /// Offsets are added afterwards. Use zero child size to resolve a point, and
        /// <see cref="SafeAreaInsets.Apply"/> on viewport bounds to respect obscured edges.
        /// </summary>
        public static Rect ResolveFractional(Rect parent, float fractionX, float fractionY,
            float width, float height, float offsetX = 0f, float offsetY = 0f)
        {
            if (!float.IsFinite(fractionX) || fractionX < 0 || fractionX > 1)
                throw new ArgumentOutOfRangeException(nameof(fractionX));
            if (!float.IsFinite(fractionY) || fractionY < 0 || fractionY > 1)
                throw new ArgumentOutOfRangeException(nameof(fractionY));
            return new Rect(parent.X + (parent.Width - width) * fractionX + offsetX,
                parent.Y + (parent.Height - height) * fractionY + offsetY, width, height);
        }

        /// <summary>Resolve a child rect of <paramref name="width"/> x <paramref name="height"/> anchored within <paramref name="parent"/>.</summary>
        public static Rect Resolve(Rect parent, Anchor anchor, float width, float height,
            float marginX = 0f, float marginY = 0f)
        {
            if (anchor == Anchor.Stretch)
                return new Rect(parent.X + marginX, parent.Y + marginY,
                                parent.Width - 2 * marginX, parent.Height - 2 * marginY);

            float x = anchor switch
            {
                Anchor.TopLeft or Anchor.Left or Anchor.BottomLeft => parent.X + marginX,
                Anchor.TopRight or Anchor.Right or Anchor.BottomRight => parent.Right - width - marginX,
                _ => parent.X + (parent.Width - width) * 0.5f,   // Top, Center, Bottom: horizontally centered
            };
            float y = anchor switch
            {
                Anchor.TopLeft or Anchor.Top or Anchor.TopRight => parent.Y + marginY,
                Anchor.BottomLeft or Anchor.Bottom or Anchor.BottomRight => parent.Bottom - height - marginY,
                _ => parent.Y + (parent.Height - height) * 0.5f, // Left, Center, Right: vertically centered
            };
            return new Rect(x, y, width, height);
        }
    }
}
