using System;
using System.Numerics;

namespace KhaozEngine.Render2D.Vfx
{
    /// <summary>
    /// The pure functions behind a screen-fixed banner: where it is at a given age, and the easing that gets it
    /// there. Alpha and scale are <see cref="FloatingTextCurves"/>'s, unchanged and deliberately not restated, so a
    /// banner and an anchored line fade and zoom by one rule.
    /// </summary>
    public static class FloatingBannerCurves
    {
        /// <summary>
        /// Ease-out cubic on <paramref name="t"/>, clamped to 0..1: fast off the mark, settling into the end. A
        /// banner that reaches its corner late reads as sliding away, and one that reaches it early reads as landing,
        /// which is what a milestone wants.
        /// </summary>
        public static float Ease(float t)
        {
            float c = Math.Clamp(t, 0f, 1f);
            float inv = 1f - c;
            return 1f - inv * inv * inv;
        }

        /// <summary>
        /// The banner's centre at <paramref name="age"/> seconds: <paramref name="start"/> eased to
        /// <paramref name="end"/> across the lifetime. Exactly <paramref name="start"/> at birth and exactly
        /// <paramref name="end"/> at and after the lifetime, so neither endpoint is approximate.
        /// <para>A lifetime at or below zero holds the start point, since there is no span to travel. Its alpha is
        /// zero for that whole non-life anyway, under the same rule that makes a default-constructed style draw
        /// nothing.</para>
        /// </summary>
        public static Vector2 PositionAt(float age, Vector2 start, Vector2 end, in FloatingTextStyle style)
        {
            if (style.LifetimeSeconds <= 0f) return start;
            float t = Ease(age / style.LifetimeSeconds);
            return Vector2.Lerp(start, end, t);
        }
    }
}
