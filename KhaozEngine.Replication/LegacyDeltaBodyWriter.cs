using System;
using System.Collections.Generic;
using System.IO;
using Projection = System.Collections.Generic.Dictionary<long, KhaozEngine.Replication.CapturedComponents>;

namespace KhaozEngine.Replication;

/// <summary>Builds an AoI legacy body with optional space for the serving host's wire headers.</summary>
internal sealed class LegacyDeltaBodyWriter
{
    private const int RetainBelowBytes = 64 * 1024;
    private const int LowPassesBeforeRelease = 4;
    private int lowPasses;
    private long lastLowPass;
    private readonly MemoryStream wireStream = new();
    private readonly BinaryWriter wireWriter;
    private readonly List<long> scratchRemoved = new();
    private readonly List<long> scratchChanged = new();

    internal LegacyDeltaBodyWriter() => wireWriter = new BinaryWriter(wireStream);

    internal void Write(ReplicationRegistry registry, Projection projected, Projection? baseline,
        int baselineSeq, int sequence, int prefixLength, ref Exception? completionFailure)
    {
        wireStream.SetLength(0); // reset the reused wire scratch, keep its capacity
        wireStream.Position = prefixLength;
        BinaryWriter bw = wireWriter;
        bw.Write(baselineSeq);
        bw.Write(sequence);

        // Removed: in the last sent projection, gone from the interest set (left AoI or despawned). A full snapshot
        // (no baseline) removes implicitly, so it lists nothing. Reused scratch list, cleared per call.
        scratchRemoved.Clear();
        if (baseline is not null)
            foreach (var (netId, _) in baseline)
                if (!projected.ContainsKey(netId)) scratchRemoved.Add(netId);
        bw.Write(scratchRemoved.Count);
        foreach (long netId in scratchRemoved) bw.Write(netId);

        // New (absent from the last sent projection, so written whole) or changed (stayed + component delta).
        scratchChanged.Clear();
        foreach ((long netId, CapturedComponents comps) in projected)
        {
            if (baseline is null || !baseline.TryGetValue(netId, out CapturedComponents? previous)
                || DeltaEncoding.EntityChanged(previous, comps)) scratchChanged.Add(netId);
        }
        bw.Write(scratchChanged.Count);
        foreach (long netId in scratchChanged)
        {
            bool isNew = baseline is null || !baseline.ContainsKey(netId);
            DeltaEncoding.WriteChangedEntity(bw, registry, netId, isNew, isNew ? null : baseline![netId], projected[netId]);
        }

        if (completionFailure is Exception failure)
        {
            completionFailure = null;
            throw failure;
        }
        bw.Flush();
    }

    internal byte[] ToArray() => wireStream.ToArray();

    internal Span<byte> WrittenSpan => wireStream.GetBuffer().AsSpan(0, (int)wireStream.Length);

    // Sustained low use retires an outlier without thrashing on alternating sizes. A gap in the
    // completed serve pass ordinals breaks the streak, as does a pass that needs the larger buffer.
    internal void ReleaseOversize(long passOrdinal, int largestFrame)
    {
        if (wireStream.Capacity <= RetainBelowBytes || largestFrame > wireStream.Capacity / 4)
        {
            lowPasses = 0;
            return;
        }

        lowPasses = passOrdinal == unchecked(lastLowPass + 1) ? lowPasses + 1 : 1;
        lastLowPass = passOrdinal;
        if (lowPasses < LowPassesBeforeRelease) return;

        wireStream.SetLength(0);
        wireStream.Capacity = 0;
        lowPasses = 0;
    }
}
