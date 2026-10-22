using System;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>All-or-nothing counter normalization shared by the native timestamp backends.</summary>
    internal static class GpuTimestampResults
    {
        /// <summary>
        /// Normalize a nonempty batch to elapsed ticks relative to its first query. Only the low validBits
        /// of each adjacent difference are meaningful. Each interval must be shorter than a complete wrap.
        /// Bounds and cumulative overflow are checked in a first pass, leaving the output untouched on failure.
        /// The second pass supports overlapping input/output and preserves any unused output tail.
        /// </summary>
        internal static bool TryNormalize(ReadOnlySpan<ulong> raw, int validBits, Span<ulong> relativeTicks)
        {
            if (validBits < 1 || validBits > 64 || raw.IsEmpty || relativeTicks.Length < raw.Length)
                return false;
            ulong mask = validBits == 64 ? ulong.MaxValue : (1ul << validBits) - 1;
            ulong elapsed = 0;
            for (int i = 1; i < raw.Length; i++)
            {
                ulong delta = unchecked(raw[i] - raw[i - 1]) & mask;
                if (delta > ulong.MaxValue - elapsed) return false;
                elapsed += delta;
            }

            // Write backwards if the destination overlaps future source values.
            if (raw.Overlaps(relativeTicks, out int offset) && offset > 0)
            {
                for (int i = raw.Length - 1; i > 0; i--)
                {
                    ulong delta = unchecked(raw[i] - raw[i - 1]) & mask;
                    relativeTicks[i] = elapsed;
                    elapsed -= delta;
                }
            }
            else
            {
                elapsed = 0;
                ulong previous = raw[0];
                for (int i = 1; i < raw.Length; i++)
                {
                    ulong current = raw[i];
                    elapsed += unchecked(current - previous) & mask;
                    relativeTicks[i] = elapsed;
                    previous = current;
                }
            }
            relativeTicks[0] = 0;
            return true;
        }
    }
}
