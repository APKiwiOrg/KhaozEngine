using System;

namespace KhaozEngine.Game
{
    /// <summary>
    /// Pure weighted-progress arithmetic for the <see cref="BootPipeline"/>, factored out so the mapping of a
    /// per-step fraction onto the single overall bar is headless-testable with no pipeline, threads, or GPU. Each
    /// step owns a contiguous slice of 0..1 sized by its share of the total weight. A step reporting fraction
    /// <c>f</c> in [0,1] fills up to <c>sliceStart + sliceSize * f</c> of the whole bar. The slices partition the bar
    /// exactly (they sum to 1), so completing every step lands the bar at 1.
    /// </summary>
    internal static class BootProgressMath
    {
        /// <summary>
        /// The exclusive-prefix slice starts and the slice sizes for <paramref name="weights"/>, normalized so the
        /// sizes sum to 1. <c>starts[i]</c> is the overall fraction at which step <c>i</c> begins, and <c>sizes[i]</c> is
        /// its share of the bar. An empty input yields empty arrays. A non-positive total weight (all zero) falls back
        /// to equal slices so the bar still partitions cleanly rather than dividing by zero.
        /// </summary>
        public static (float[] Starts, float[] Sizes) Slices(ReadOnlySpan<float> weights)
        {
            int n = weights.Length;
            var starts = new float[n];
            var sizes = new float[n];
            if (n == 0) return (starts, sizes);

            float total = 0f;
            for (int i = 0; i < n; i++) total += weights[i] > 0f ? weights[i] : 0f;

            if (total <= 0f)
            {
                float even = 1f / n;
                for (int i = 0; i < n; i++) { starts[i] = i * even; sizes[i] = even; }
                return (starts, sizes);
            }

            float cursor = 0f;
            for (int i = 0; i < n; i++)
            {
                float w = weights[i] > 0f ? weights[i] : 0f;
                sizes[i] = w / total;
                starts[i] = cursor;
                cursor += sizes[i];
            }
            return (starts, sizes);
        }

        /// <summary>
        /// The overall bar fraction when step <paramref name="index"/> reports <paramref name="stepFraction"/> (in
        /// [0,1], clamped) within its slice: <c>starts[index] + sizes[index] * clamp01(stepFraction)</c>. Returns the
        /// slice start for a non-positive fraction and the slice end for a fraction &gt;= 1.
        /// </summary>
        public static float Overall(int index, float stepFraction, float[] starts, float[] sizes)
        {
            if (starts.Length == 0) return 0f;
            if (index < 0) index = 0;
            if (index >= starts.Length) index = starts.Length - 1;
            float f = stepFraction < 0f ? 0f : stepFraction > 1f ? 1f : stepFraction;
            return starts[index] + sizes[index] * f;
        }
    }
}
