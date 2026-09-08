using System;
using System.Globalization;
using System.Text;

namespace KhaozEngine.Gpu
{
    /// <summary>
    /// Sample counts a device supports across every render target the engine places in one multisampled pass.
    /// Flags may be sparse. For example, a Vulkan device can support <see cref="One"/> and <see cref="Four"/>
    /// without supporting <see cref="Two"/>.
    /// </summary>
    [Flags]
    public enum GpuSampleCounts
    {
        None = 0,
        One = 1 << 0,
        Two = 1 << 1,
        Four = 1 << 2,
        Eight = 1 << 3,
        Sixteen = 1 << 4,
        ThirtyTwo = 1 << 5,
    }

    internal static class GpuSampleCountSet
    {
        internal const GpuSampleCounts All = GpuSampleCounts.One | GpuSampleCounts.Two | GpuSampleCounts.Four
            | GpuSampleCounts.Eight | GpuSampleCounts.Sixteen | GpuSampleCounts.ThirtyTwo;

        internal static GpuSampleCounts Normalize(GpuSampleCounts counts)
        {
            GpuSampleCounts known = counts & All;
            return known == GpuSampleCounts.None ? GpuSampleCounts.One : known | GpuSampleCounts.One;
        }

        internal static GpuSampleCounts ContiguousThrough(int maximum)
        {
            int cap = Math.Max(1, maximum);
            GpuSampleCounts counts = GpuSampleCounts.One;
            if (cap >= 2) counts |= GpuSampleCounts.Two;
            if (cap >= 4) counts |= GpuSampleCounts.Four;
            if (cap >= 8) counts |= GpuSampleCounts.Eight;
            if (cap >= 16) counts |= GpuSampleCounts.Sixteen;
            if (cap >= 32) counts |= GpuSampleCounts.ThirtyTwo;
            return counts;
        }

        internal static bool Contains(GpuSampleCounts counts, int sampleCount)
            => (Normalize(counts) & FlagOf(sampleCount)) != 0;

        internal static int HighestAtMost(GpuSampleCounts counts, int maximum)
        {
            int cap = Math.Max(1, maximum);
            GpuSampleCounts normalized = Normalize(counts);
            if (cap >= 32 && (normalized & GpuSampleCounts.ThirtyTwo) != 0) return 32;
            if (cap >= 16 && (normalized & GpuSampleCounts.Sixteen) != 0) return 16;
            if (cap >= 8 && (normalized & GpuSampleCounts.Eight) != 0) return 8;
            if (cap >= 4 && (normalized & GpuSampleCounts.Four) != 0) return 4;
            if (cap >= 2 && (normalized & GpuSampleCounts.Two) != 0) return 2;
            return 1;
        }

        internal static string Describe(GpuSampleCounts counts)
        {
            GpuSampleCounts normalized = Normalize(counts);
            var text = new StringBuilder();
            Append(text, normalized, GpuSampleCounts.One, 1);
            Append(text, normalized, GpuSampleCounts.Two, 2);
            Append(text, normalized, GpuSampleCounts.Four, 4);
            Append(text, normalized, GpuSampleCounts.Eight, 8);
            Append(text, normalized, GpuSampleCounts.Sixteen, 16);
            Append(text, normalized, GpuSampleCounts.ThirtyTwo, 32);
            return text.ToString();
        }

        static void Append(StringBuilder text, GpuSampleCounts counts, GpuSampleCounts flag, int value)
        {
            if ((counts & flag) == 0) return;
            if (text.Length > 0) text.Append(", ");
            text.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        static GpuSampleCounts FlagOf(int sampleCount) => sampleCount switch
        {
            1 => GpuSampleCounts.One,
            2 => GpuSampleCounts.Two,
            4 => GpuSampleCounts.Four,
            8 => GpuSampleCounts.Eight,
            16 => GpuSampleCounts.Sixteen,
            32 => GpuSampleCounts.ThirtyTwo,
            _ => GpuSampleCounts.None,
        };

    }
}
