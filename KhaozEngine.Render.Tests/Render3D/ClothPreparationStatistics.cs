using System;

namespace KhaozEngine.Tests.Render3D;

internal readonly record struct ClothPreparationStatistics(double? P50Ms, double? P95Ms, double? MaxMs)
{
    internal static ClothPreparationStatistics Summarize(long[]? samples, long frequency)
    {
        if (samples is null) return default;
        Array.Sort(samples);
        double ToMilliseconds(long ticks) => 1000.0 * ticks / frequency;
        double Percentile(double rank) => ToMilliseconds(samples[(int)Math.Ceiling(rank * samples.Length) - 1]);
        return new(Percentile(.50), Percentile(.95), ToMilliseconds(samples[^1]));
    }
}
