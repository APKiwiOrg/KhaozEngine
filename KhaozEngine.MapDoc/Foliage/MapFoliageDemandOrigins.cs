using System.Runtime.CompilerServices;
using System.Threading;
using KhaozEngine.Diagnostics;
using KhaozEngine.MapDoc.Spaces;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Diagnostic provenance for profile copies whose effective allowances have been reduced.</summary>
internal static class MapFoliageDemandOrigins
{
    sealed record Entry(MapFoliageLimits Profile, decimal Samples);
    static ConditionalWeakTable<MapFoliageLimits, Entry>? s_origins;

    internal static void Carry(MapFoliageLimits derived, MapFoliageLimits original, long samples = 0)
    {
        if (CapacityDemandDiagnostics.IsObserved) Store(derived, original, samples);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Store(MapFoliageLimits derived, MapFoliageLimits original, long samples)
    {
        try
        {
            var table = Volatile.Read(ref s_origins);
            if (table is null) table = Interlocked.CompareExchange(ref s_origins, new(), null) ?? s_origins!;
            var entry = table.TryGetValue(original, out var parent) ? parent : new(original, 0);
            lock (table)
            {
                table.Remove(derived);
                table.Add(derived, new(entry.Profile, entry.Samples + samples));
            }
        }
        catch { /* Provenance cannot affect a capacity decision. */ }
    }

    internal static MapFoliageLimits Profile(MapFoliageLimits limits) =>
        Volatile.Read(ref s_origins)?.TryGetValue(limits, out var entry) == true ? entry.Profile : limits;

    internal static MapCapacityDemandOrigin Samples(MapFoliageLimits limits, long current)
    {
        if (Volatile.Read(ref s_origins)?.TryGetValue(limits, out var entry) == true)
            return new("MaxQuerySamples", entry.Samples + current, entry.Profile.MaxQuerySamples, entry.Profile);
        return new("MaxQuerySamples", current, limits.MaxQuerySamples, limits);
    }
}
