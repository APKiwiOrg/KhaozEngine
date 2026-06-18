using System;

namespace KhaozEngine.Render2D
{
    /// <summary>Parallax scroll-math helpers.</summary>
    public static class Parallax
    {
        /// <summary>
        /// Non-negative remainder (<paramref name="value"/> mod <paramref name="size"/>, in
        /// <c>[0, size)</c>) for seamlessly tiling a repeating background: the game draws copies starting at
        /// <c>-Wrap(layerViewX, tileWidth)</c> across the viewport. Returns 0 when <paramref name="size"/> is
        /// non-positive (no divide-by-zero / NaN).
        /// </summary>
        public static float Wrap(float value, float size)
        {
            if (size <= 0f) return 0f;
            return value - size * MathF.Floor(value / size);
        }
    }
}
