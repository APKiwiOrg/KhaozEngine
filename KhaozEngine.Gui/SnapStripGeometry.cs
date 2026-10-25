using System;

namespace KhaozEngine.Gui
{
    /// <summary>Pure extents and anchored scroll stops for a fixed-pitch horizontal strip.</summary>
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
