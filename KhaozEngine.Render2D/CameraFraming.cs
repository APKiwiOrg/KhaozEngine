using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Pure framing math for multi-target cameras: the padded bounding box of a set of world points, and the
    /// camera position + zoom that frames a box in a viewport. No state, no easing - <see cref="GroupCamera"/>
    /// layers the smoothing on top. Headless, <see cref="System.Numerics"/> only.
    /// </summary>
    public static class CameraFraming
    {
        private const float Epsilon = 1e-4f;

        /// <summary>
        /// Axis-aligned bounding box of <paramref name="targets"/>, expanded on each side by
        /// <paramref name="paddingFraction"/> of the extent, then grown (about its center) to at least
        /// <paramref name="minViewSize"/> per axis. Throws if <paramref name="targets"/> is empty.
        /// </summary>
        public static Rect Bounds(IReadOnlyList<Vector2> targets, float paddingFraction, Vector2 minViewSize)
        {
            if (targets == null || targets.Count == 0)
                throw new ArgumentException("targets must be non-empty", nameof(targets));

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < targets.Count; i++)
            {
                Vector2 p = targets[i];
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }

            float cx = (minX + maxX) * 0.5f;
            float cy = (minY + maxY) * 0.5f;
            float w = (maxX - minX) * (1f + 2f * paddingFraction);
            float h = (maxY - minY) * (1f + 2f * paddingFraction);

            w = MathF.Max(w, MathF.Max(minViewSize.X, Epsilon));
            h = MathF.Max(h, MathF.Max(minViewSize.Y, Epsilon));

            return new Rect(cx - w * 0.5f, cy - h * 0.5f, w, h);
        }

        /// <summary>
        /// Position (the box center) and zoom (contain-fit <c>min(vw / width, vh / height)</c>, clamped to
        /// <paramref name="minZoom"/>/<paramref name="maxZoom"/>) that frames <paramref name="bounds"/>.
        /// Box dimensions are floored to a tiny epsilon so a zero-size box never divides by zero.
        /// </summary>
        public static (Vector2 Position, float Zoom) Solve(Rect bounds, int vw, int vh, float minZoom, float maxZoom)
        {
            float w = MathF.Max(bounds.Width, Epsilon);
            float h = MathF.Max(bounds.Height, Epsilon);
            float fit = MathF.Min(vw / w, vh / h);
            float zoom = Math.Clamp(fit, minZoom, maxZoom);
            var pos = new Vector2(bounds.X + bounds.Width * 0.5f, bounds.Y + bounds.Height * 0.5f);
            return (pos, zoom);
        }
    }
}
