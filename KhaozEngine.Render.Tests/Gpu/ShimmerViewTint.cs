namespace KhaozEngine.Tests.Gpu
{
    /// <summary>The Shimmer debug view's tint read back from an RGBA8 image. The view mixes the grey dimmed scene toward
    /// (0.15, 0.55, 1.0) by the shimmer score (TemporalDebugMainGlsl), so blue less red is 0.85 of the score in 8-bit
    /// units, and the grey context alone reads 0.</summary>
    internal static class ShimmerViewTint
    {
        /// <summary>A blue excess at or above this is a pixel the view marks, a score of about 0.3 or more.</summary>
        public const int Tinted = 64;

        /// <summary>A blue excess at or below this is no tint, rounding allowed.</summary>
        public const int Untinted = 2;

        /// <summary>Blue less red at (<paramref name="x"/>, <paramref name="y"/>) of an image
        /// <paramref name="width"/> pixels wide.</summary>
        public static int BlueExcess(byte[] rgba, int width, int x, int y)
        {
            int i = (y * width + x) * 4;
            return rgba[i + 2] - rgba[i];
        }
    }
}
