using System;

namespace KhaozEngine.Benchmarks.Journal;

internal sealed class JournalLatencySamples
{
    private const int Capacity = 65_536;
    private readonly double[] values = new double[Capacity];
    private ulong state;
    private long observed;

    internal JournalLatencySamples(int seed)
        => state = unchecked((ulong)(uint)seed) + 0xD1B54A32D192ED03UL;

    internal void Add(double value)
    {
        long index = observed++;
        if (index < values.Length)
        {
            values[index] = value;
            return;
        }

        long replacement = (long)(NextUInt64() % (ulong)(index + 1));
        if (replacement < values.Length) values[replacement] = value;
    }

    internal double Percentile(double percentile)
    {
        int count = (int)Math.Min(observed, values.Length);
        if (count == 0) return 0;
        var sorted = new double[count];
        Array.Copy(values, sorted, count);
        Array.Sort(sorted);
        int index = (int)Math.Ceiling(percentile * count) - 1;
        return sorted[Math.Clamp(index, 0, count - 1)];
    }

    private ulong NextUInt64()
    {
        ulong value = state;
        value ^= value >> 12;
        value ^= value << 25;
        value ^= value >> 27;
        state = value;
        return value * 0x2545F4914F6CDD1DUL;
    }
}
