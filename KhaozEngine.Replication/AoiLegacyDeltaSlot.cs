using System;
using Projection = System.Collections.Generic.Dictionary<long, KhaozEngine.Replication.CapturedComponents>;

namespace KhaozEngine.Replication;

/// <summary>Owns the committed projection and an empty spare for the next ordinary AoI build.</summary>
internal sealed class AoiLegacyDeltaSlot(int sequence, Projection projection) : LegacyDeltaSlot(sequence, projection)
{
    private const int OrdinaryCapacity = 1024;
    private const int LowUsesBeforeRelease = 4;
    private Projection? candidate;
    private int lowUses;

    internal Projection PrepareCandidate() => candidate ??= new Projection();

    // Keep the observed spare alive across slot removal without exposing its mutable entries.
    internal Func<int> ObserveCandidateCountForTests()
    {
        Projection observed = candidate ?? throw new InvalidOperationException("The slot has no spare projection.");
        return () => observed.Count;
    }

    internal void Commit(int sequence, Projection projection, bool recycle)
    {
        Projection retired = Projection;
        Sent(sequence, projection);
        if (!recycle)
        {
            // A nested build owns its projection. The shared spare may still be in use by the outer build.
            lowUses = 0;
            return;
        }

        int peakCapacity = Math.Max(retired.Capacity, projection.Capacity);
        lowUses = IsOversize(peakCapacity, projection.Count) ? Math.Min(lowUses + 1, LowUsesBeforeRelease) : 0;

        retired.Clear();
        candidate = lowUses == LowUsesBeforeRelease && IsOversize(retired.Capacity, projection.Count) ? null : retired;
    }

    private static bool IsOversize(int capacity, int used) => capacity > OrdinaryCapacity && used <= capacity / 4;
}
