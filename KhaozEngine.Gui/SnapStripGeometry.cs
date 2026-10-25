using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gui
{
    /// <summary>Pure layout, picking and anchored scroll stops for a fixed-pitch horizontal strip.</summary>
    /// <remarks>Pitch is item width plus spacing. Content has neither endpoint padding nor a trailing gap.</remarks>
    public static class SnapStripGeometry
    {
        /// <summary>Computes the content width without a trailing gap.</summary>
        /// <param name="itemCount">The nonnegative number of items.</param>
        /// <param name="metrics">The item dimensions and viewport anchor, validated even for empty content.</param>
        /// <returns>The content width, or zero for an empty strip.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="itemCount"/> is negative,
        /// <paramref name="metrics"/> is invalid, or the derived pitch or extent exceeds a finite float.</exception>
        public static float ContentWidth(int itemCount, in SnapStripMetrics metrics)
        {
            if (itemCount < 0) throw new ArgumentOutOfRangeException(nameof(itemCount));
            float pitch = ValidateMetrics(metrics);
            if (itemCount == 0) return 0f;

            double width = (itemCount - 1d) * pitch + metrics.ItemWidth;
            if (width > float.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(itemCount), "The content width must fit in a finite float.");
            return (float)width;
        }

        /// <summary>Computes the maximum horizontal scroll offset for the content and viewport.</summary>
        /// <param name="viewportWidth">The finite, nonnegative viewport width in the metrics' units.</param>
        /// <param name="itemCount">The nonnegative number of items.</param>
        /// <param name="metrics">The item dimensions and viewport anchor, validated even for empty content.</param>
        /// <returns>The content width minus the viewport width, clamped to a minimum of zero.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="viewportWidth"/> is invalid,
        /// <paramref name="itemCount"/> is negative, <paramref name="metrics"/> is invalid,
        /// or the derived pitch or extent exceeds a finite float.</exception>
        public static float MaxScroll(float viewportWidth, int itemCount, in SnapStripMetrics metrics)
        {
            if (!float.IsFinite(viewportWidth) || viewportWidth < 0f)
                throw new ArgumentOutOfRangeException(nameof(viewportWidth));
            return MathF.Max(0f, ContentWidth(itemCount, metrics) - viewportWidth);
        }

        /// <summary>Places the requested item's centre at the viewport anchor, clamped to the scroll range.</summary>
        /// <param name="index">The requested item index, clamped to the available items.</param>
        /// <param name="viewportWidth">The finite, nonnegative viewport width in the metrics' units.</param>
        /// <param name="itemCount">The nonnegative number of items.</param>
        /// <param name="metrics">The item dimensions and viewport anchor, validated before returning zero.</param>
        /// <returns>The clamped scroll offset. Empty content and zero-width viewports return zero.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="viewportWidth"/> is invalid,
        /// <paramref name="itemCount"/> is negative, <paramref name="metrics"/> is invalid,
        /// or the derived pitch or extent exceeds a finite float.</exception>
        public static float CenterOffset(int index, float viewportWidth, int itemCount, in SnapStripMetrics metrics)
        {
            float maxScroll = MaxScroll(viewportWidth, itemCount, metrics);
            if (itemCount == 0 || viewportWidth == 0f) return 0f;

            int clampedIndex = Math.Clamp(index, 0, itemCount - 1);
            float pitch = metrics.ItemWidth + metrics.ItemSpacing;
            double offset = (double)clampedIndex * pitch + metrics.ItemWidth / 2d
                - (double)metrics.Anchor * viewportWidth;
            return (float)Math.Clamp(offset, 0d, maxScroll);
        }

        /// <summary>Finds the nearest actual clamped stop, resolving equal distances to the lower index.</summary>
        /// <param name="scrollOffset">The finite query offset, clamped to the scroll range.</param>
        /// <param name="viewportWidth">The finite, nonnegative viewport width in the metrics' units.</param>
        /// <param name="itemCount">The nonnegative number of items.</param>
        /// <param name="metrics">The item dimensions and viewport anchor.</param>
        /// <returns>The nearest index, or -1 for empty content. Zero is canonical at the start and when content
        /// fits. The last index is canonical at the end of scrollable content.</returns>
        /// <exception cref="ArgumentOutOfRangeException">An offset, dimension, count or metric is invalid,
        /// or the derived pitch or extent exceeds a finite float.</exception>
        public static int NearestIndex(float scrollOffset, float viewportWidth, int itemCount, in SnapStripMetrics metrics)
        {
            ValidateOffset(scrollOffset);
            float maxScroll = MaxScroll(viewportWidth, itemCount, metrics);
            if (itemCount == 0) return -1;

            float offset = Math.Clamp(scrollOffset, 0f, maxScroll);
            if (offset == 0f) return 0;
            if (offset == maxScroll) return itemCount - 1;

            float pitch = metrics.ItemWidth + metrics.ItemSpacing;
            double position = (offset + (double)metrics.Anchor * viewportWidth - metrics.ItemWidth / 2d) / pitch;
            int lower = (int)Math.Clamp(Math.Floor(position), 0d, itemCount - 1d);
            int upper = Math.Min(lower + 1, itemCount - 1);
            float lowerOffset = CenterOffset(lower, viewportWidth, itemCount, metrics);
            float upperOffset = CenterOffset(upper, viewportWidth, itemCount, metrics);
            lower = CanonicalIndex(lower, lowerOffset, maxScroll, itemCount);
            upper = CanonicalIndex(upper, upperOffset, maxScroll, itemCount);
            double lowerDistance = Math.Abs((double)offset - lowerOffset);
            double upperDistance = Math.Abs((double)offset - upperOffset);
            if (lowerDistance == upperDistance) return Math.Min(lower, upper);
            return lowerDistance < upperDistance ? lower : upper;
        }

        /// <summary>Places an item using the supplied offset verbatim, without clipping to the viewport.</summary>
        /// <param name="bounds">The viewport with finite origins and finite, nonnegative dimensions.</param>
        /// <param name="index">An item index in [0, <paramref name="itemCount"/>).</param>
        /// <param name="scrollOffset">The finite horizontal offset, which motion owns and clamps.</param>
        /// <param name="itemCount">The nonnegative number of items.</param>
        /// <param name="metrics">The item dimensions and viewport anchor.</param>
        /// <returns>The item's full width and the viewport's height, even outside the viewport.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The index, bounds, offset, count or metrics are invalid,
        /// or the derived pitch or extent exceeds a finite float.</exception>
        public static Rect ItemBounds(in Rect bounds, int index, float scrollOffset, int itemCount, in SnapStripMetrics metrics)
        {
            float pitch = ValidateLayout(bounds, scrollOffset, itemCount, metrics);
            if (index < 0 || index >= itemCount) throw new ArgumentOutOfRangeException(nameof(index));
            float left = (float)(bounds.X + (double)index * pitch - scrollOffset);
            return new Rect(left, bounds.Y, metrics.ItemWidth, bounds.Height);
        }

        /// <summary>Finds the item under a point using half-open item and viewport bounds.</summary>
        /// <param name="bounds">The viewport with finite origins and finite, nonnegative dimensions.</param>
        /// <param name="point">The finite point in the viewport's coordinate space.</param>
        /// <param name="scrollOffset">The finite horizontal offset, used without clamping.</param>
        /// <param name="itemCount">The nonnegative number of items.</param>
        /// <param name="metrics">The item dimensions and viewport anchor.</param>
        /// <returns>The item index, or -1 for gaps, empty content or points outside the viewport.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The bounds, point, offset, count or metrics are invalid,
        /// or the derived pitch or extent exceeds a finite float.</exception>
        public static int IndexAt(in Rect bounds, Vector2 point, float scrollOffset, int itemCount, in SnapStripMetrics metrics)
        {
            float pitch = ValidateLayout(bounds, scrollOffset, itemCount, metrics);
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
                throw new ArgumentOutOfRangeException(nameof(point));
            if (itemCount == 0 || !bounds.Contains(point)) return -1;

            double candidate = Math.Floor(((double)point.X - bounds.X + scrollOffset) / pitch);
            if (candidate < -1d || candidate > itemCount) return -1;
            int index = (int)Math.Clamp(candidate, 0d, itemCount - 1d);
            // Rounding a placed edge to float can move the inverse into a neighbouring cell.
            // Test the actual rectangles so left edges remain inclusive without admitting gaps.
            if (index > 0 && ItemBounds(bounds, index - 1, scrollOffset, itemCount, metrics).Contains(point))
                return index - 1;
            if (ItemBounds(bounds, index, scrollOffset, itemCount, metrics).Contains(point)) return index;
            if (index < itemCount - 1 && ItemBounds(bounds, index + 1, scrollOffset, itemCount, metrics).Contains(point))
                return index + 1;
            return -1;
        }

        /// <summary>Finds the contiguous range of items with positive-area viewport intersections.</summary>
        /// <param name="bounds">The viewport with finite origins and finite, nonnegative dimensions.</param>
        /// <param name="scrollOffset">The finite horizontal offset, used without clamping.</param>
        /// <param name="itemCount">The nonnegative number of items.</param>
        /// <param name="metrics">The item dimensions and viewport anchor.</param>
        /// <returns>The first visible index and count, or (0, 0) for an empty or zero-area view,
        /// including a viewport containing only a gap. Edge contact alone is not visible.</returns>
        /// <exception cref="ArgumentOutOfRangeException">The bounds, offset, count or metrics are invalid,
        /// or the derived pitch or extent exceeds a finite float.</exception>
        public static (int First, int Count) VisibleRange(in Rect bounds, float scrollOffset, int itemCount, in SnapStripMetrics metrics)
        {
            float pitch = ValidateLayout(bounds, scrollOffset, itemCount, metrics);
            if (itemCount == 0 || bounds.Width == 0f || bounds.Height == 0f) return (0, 0);

            // Strict inequalities exclude items whose right or left edge only touches the viewport.
            double first = Math.Floor(((double)scrollOffset - metrics.ItemWidth) / pitch) + 1d;
            double end = Math.Ceiling(((double)scrollOffset + bounds.Width) / pitch);
            int firstIndex = (int)Math.Clamp(first, 0d, itemCount);
            int endIndex = (int)Math.Clamp(end, 0d, itemCount);
            return endIndex > firstIndex ? (firstIndex, endIndex - firstIndex) : (0, 0);
        }

        static int CanonicalIndex(int index, float offset, float maxScroll, int itemCount)
            => offset == 0f ? 0 : offset == maxScroll ? itemCount - 1 : index;

        static float ValidateLayout(in Rect bounds, float scrollOffset, int itemCount, in SnapStripMetrics metrics)
        {
            if (!float.IsFinite(bounds.X) || !float.IsFinite(bounds.Y)
                || !float.IsFinite(bounds.Width) || bounds.Width < 0f
                || !float.IsFinite(bounds.Height) || bounds.Height < 0f)
                throw new ArgumentOutOfRangeException(nameof(bounds));
            ValidateOffset(scrollOffset);
            _ = ContentWidth(itemCount, metrics);
            return metrics.ItemWidth + metrics.ItemSpacing;
        }

        static void ValidateOffset(float scrollOffset)
        {
            if (!float.IsFinite(scrollOffset)) throw new ArgumentOutOfRangeException(nameof(scrollOffset));
        }

        static float ValidateMetrics(in SnapStripMetrics metrics)
        {
            if (!float.IsFinite(metrics.ItemWidth) || metrics.ItemWidth <= 0f)
                throw new ArgumentOutOfRangeException(nameof(metrics), "Item width must be finite and positive.");
            if (!float.IsFinite(metrics.ItemSpacing) || metrics.ItemSpacing < 0f)
                throw new ArgumentOutOfRangeException(nameof(metrics), "Item spacing must be finite and nonnegative.");
            if (!float.IsFinite(metrics.Anchor) || metrics.Anchor < 0f || metrics.Anchor > 1f)
                throw new ArgumentOutOfRangeException(nameof(metrics), "Anchor must be finite and between zero and one.");

            float pitch = metrics.ItemWidth + metrics.ItemSpacing;
            if (!float.IsFinite(pitch))
                throw new ArgumentOutOfRangeException(nameof(metrics), "The item pitch must fit in a finite float.");
            return pitch;
        }
    }
}
