using System;
using System.Numerics;

namespace KhaozEngine.Primitives
{
    /// <summary>
    /// Pure point-to-point rect resolution. <see cref="Resolve"/> aligns a named point of a sized child
    /// with an independently named point of a target rect. Offsets are added afterwards.
    /// </summary>
    public static class Attach
    {
        /// <summary>Resolve the position of <paramref name="point"/> within <paramref name="rect"/>.</summary>
        public static Vector2 PointOf(Rect rect, AttachPoint point)
        {
            float x = point switch
            {
                AttachPoint.TopLeft or AttachPoint.Left or AttachPoint.BottomLeft => rect.X,
                AttachPoint.TopRight or AttachPoint.Right or AttachPoint.BottomRight => rect.Right,
                _ => rect.X + rect.Width * 0.5f,
            };
            float y = point switch
            {
                AttachPoint.TopLeft or AttachPoint.Top or AttachPoint.TopRight => rect.Y,
                AttachPoint.BottomLeft or AttachPoint.Bottom or AttachPoint.BottomRight => rect.Bottom,
                _ => rect.Y + rect.Height * 0.5f,
            };
            return new Vector2(x, y);
        }

        /// <summary>
        /// Places a child rect of <paramref name="width"/> x <paramref name="height"/> so its
        /// <paramref name="selfPoint"/> lies on <paramref name="target"/>'s <paramref name="targetPoint"/>
        /// plus the offsets. Use zero child size to resolve a point.
        /// </summary>
        public static Rect Resolve(Rect target, AttachPoint targetPoint, float width, float height,
            AttachPoint selfPoint, float offsetX = 0f, float offsetY = 0f)
        {
            Vector2 targetPosition = PointOf(target, targetPoint);
            Vector2 selfPosition = PointOf(new Rect(0f, 0f, width, height), selfPoint);
            return new Rect(targetPosition.X - selfPosition.X + offsetX,
                targetPosition.Y - selfPosition.Y + offsetY, width, height);
        }

        /// <summary>Resolve the smallest rect that covers both <paramref name="a"/> and <paramref name="b"/>.</summary>
        public static Rect Union(Rect a, Rect b)
        {
            float x = MathF.Min(a.X, b.X);
            float y = MathF.Min(a.Y, b.Y);
            return new Rect(x, y, MathF.Max(a.Right, b.Right) - x, MathF.Max(a.Bottom, b.Bottom) - y);
        }
    }
}
