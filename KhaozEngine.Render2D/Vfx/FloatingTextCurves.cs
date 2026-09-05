using System;
using System.Numerics;

namespace KhaozEngine.Render2D.Vfx
{
    /// <summary>
    /// The pure functions behind floating text: what alpha, what scale and what offset an entry of a given age has
    /// under a given style. No state, no GPU, no allocation, so the whole animation is unit-testable and a store's
    /// entries carry an age and nothing else.
    /// <para>Shared by <see cref="FloatingTextRenderer"/> and by <see cref="FloatingBannerCurves"/>, which is why the
    /// alpha and scale rules are stated once here rather than twice.</para>
    /// </summary>
    public static class FloatingTextCurves
    {
        /// <summary>
        /// Alpha in 0..1 at <paramref name="age"/> seconds: up over <see cref="FloatingTextStyle.FadeInSeconds"/>
        /// from birth, down over <see cref="FloatingTextStyle.FadeOutSeconds"/> before the end of the lifetime, and
        /// the SMALLER of the two wherever they overlap, so a style whose two fades are longer than its life never
        /// exceeds either.
        /// <para>Zero before birth, zero at and after the lifetime, and zero for the whole life of a style whose
        /// lifetime is at or below zero, which is what makes a default-constructed style draw nothing.</para>
        /// </summary>
        public static float AlphaAt(float age, in FloatingTextStyle style)
        {
            float life = style.LifetimeSeconds;
            if (life <= 0f || age < 0f || age >= life) return 0f;

            float alpha = 1f;
            if (style.FadeInSeconds > 0f) alpha = Math.Min(alpha, age / style.FadeInSeconds);
            if (style.FadeOutSeconds > 0f) alpha = Math.Min(alpha, (life - age) / style.FadeOutSeconds);
            return Math.Clamp(alpha, 0f, 1f);
        }

        /// <summary>
        /// Text scale at <paramref name="age"/> seconds: a straight lerp from
        /// <see cref="FloatingTextStyle.StartScale"/> to <see cref="FloatingTextStyle.EndScale"/> across the
        /// lifetime, clamped at both ends so an over-aged entry does not keep growing. A style with a lifetime at or
        /// below zero holds its start scale, since it has no span to travel.
        /// </summary>
        public static float ScaleAt(float age, in FloatingTextStyle style)
        {
            if (style.LifetimeSeconds <= 0f) return style.StartScale;
            float t = Math.Clamp(age / style.LifetimeSeconds, 0f, 1f);
            return style.StartScale + (style.EndScale - style.StartScale) * t;
        }

        /// <summary>
        /// Design-space offset from where the entry was born at <paramref name="age"/> seconds: the drift integrated
        /// over the age, plus this entry's own step DOWN the stack.
        /// <para>The stack step is constant in age on purpose. It separates entries born on one frame, which drift
        /// alone cannot do because they share an age, and it is the OLDEST that ends up highest because index 0 takes
        /// no step at all. Entries born apart need no help: the drift has already moved the older one.</para>
        /// </summary>
        /// <param name="age">Seconds since birth. A negative age is treated as zero.</param>
        /// <param name="style">The entry's style.</param>
        /// <param name="stackIndex">The entry's <see cref="FloatingText.StackIndex"/>.</param>
        public static Vector2 OffsetAt(float age, in FloatingTextStyle style, int stackIndex)
        {
            float t = age < 0f ? 0f : age;
            return style.DriftPerSecond * t + new Vector2(0f, style.StackSpacing * stackIndex);
        }
    }
}
