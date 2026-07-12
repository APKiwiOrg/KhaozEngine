using System;
using System.IO;

namespace KhaozEngine.Replication;

/// <summary>
/// Shared per-entity delta wire encoding used by both the whole-world <see cref="ServerReplicator"/> and the
/// per-client area-of-interest <see cref="AoiDeltaReplicator"/>, so a single writer owns the exact byte layout
/// <see cref="ClientReplicationView.ApplyDelta"/> reads. A "captured" entity is its component data keyed by type id
/// (see <see cref="CapturedComponents"/>), the immutable per-seq snapshot both replicators diff against a client's
/// acknowledged baseline. Diffing is span-based over the capture buffer and the write path slices payloads straight to
/// the outgoing writer, so a delta allocates no per-component array.
/// </summary>
internal static class DeltaEncoding
{
    /// <summary>True if any component was added, removed, or its bytes changed between a baseline and current capture.</summary>
    public static bool EntityChanged(CapturedComponents baseComps, CapturedComponents curComps)
    {
        foreach (ushort tid in curComps.TypeIds)
        {
            if (!baseComps.TryGetSpan(tid, out ReadOnlySpan<byte> prev)) return true;
            curComps.TryGetSpan(tid, out ReadOnlySpan<byte> cur); // tid came from curComps, so this always succeeds
            if (!cur.SequenceEqual(prev)) return true;
        }
        foreach (ushort tid in baseComps.TypeIds)
            if (!curComps.Contains(tid)) return true;
        return false;
    }

    /// <summary>
    /// Writes one changed/new entity: <c>[netId][isNew][removedCompCount][removedTypeId...][(typeId,[len],data)...][0]</c>.
    /// A new entity carries all its components. An existing one lists components removed since its baseline and then
    /// only the added/changed ones (in registration order). The 7-bit length prefix is emitted only for consumer
    /// extension components (see <see cref="ReplicationRegistry.FirstExtensionTypeId"/>) so an older client can skip
    /// an id it never registered. <paramref name="baseComps"/> is null for a new entity. Each component's payload is
    /// sliced from the capture buffer straight to <paramref name="bw"/> - no re-serialization, no intermediate array.
    /// </summary>
    public static void WriteChangedEntity(BinaryWriter bw, ReplicationRegistry registry, long netId, bool isNew,
        CapturedComponents? baseComps, CapturedComponents curComps)
    {
        bw.Write(netId);
        bw.Write(isNew ? (byte)1 : (byte)0);

        // Removed components (existing entities only): in the baseline, gone from current. Count then emit in the same
        // (unchanged dictionary) key order - no List allocation. baseComps is null only when isNew, so guarding on
        // isNew also guards the null.
        int removedCount = 0;
        if (!isNew)
            foreach (ushort tid in baseComps!.TypeIds)
                if (!curComps.Contains(tid)) removedCount++;
        bw.Write(removedCount);
        if (!isNew)
            foreach (ushort tid in baseComps!.TypeIds)
                if (!curComps.Contains(tid)) bw.Write(tid);

        // Changed/added components, in registration order.
        foreach (ComponentCodec codec in registry.Ordered)
        {
            if (!curComps.TryGetSpan(codec.TypeId, out ReadOnlySpan<byte> data)) continue;
            bool include = baseComps is null
                || !baseComps.TryGetSpan(codec.TypeId, out ReadOnlySpan<byte> prev) || !data.SequenceEqual(prev);
            if (!include) continue;
            bw.Write(codec.TypeId);
            // Extension components carry a 7-bit length so an older client can skip an id it never registered;
            // built-ins stay unframed (the reader consumes exactly what write produced).
            if (codec.LengthPrefixed) bw.Write7BitEncodedInt(data.Length);
            bw.Write(data);
        }
        bw.Write((ushort)0); // end-of-entity terminator
    }
}
