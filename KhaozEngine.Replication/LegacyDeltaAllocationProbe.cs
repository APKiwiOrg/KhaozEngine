using System;

namespace KhaozEngine.Replication;

internal enum LegacyDeltaPhase { Capture, Projection, Body, Framing, Send, Serve, CaptureCacheHit }

/// <summary>Opt-in per-writer test meter. When absent, the serving path never reads the allocation counter.</summary>
internal sealed class LegacyDeltaAllocationProbe
{
    private readonly long[] bytes = new long[Enum.GetValues<LegacyDeltaPhase>().Length];

    internal long this[LegacyDeltaPhase phase] => bytes[(int)phase];

    internal static long Start(LegacyDeltaAllocationProbe? probe) =>
        probe is null ? 0 : GC.GetAllocatedBytesForCurrentThread();

    internal void Record(LegacyDeltaPhase phase, long start) =>
        bytes[(int)phase] += GC.GetAllocatedBytesForCurrentThread() - start;

    internal void Reset() => Array.Clear(bytes);
}
