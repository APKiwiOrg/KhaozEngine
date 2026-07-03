using System.Collections.Generic;
using System.IO;

namespace KhaozEngine.Replication;

/// <summary>
/// Shared per-entity delta wire encoding used by both the whole-world <see cref="ServerReplicator"/> and the
/// per-client area-of-interest <see cref="AoiDeltaReplicator"/>, so a single writer owns the exact byte layout
/// <see cref="ClientReplicationView.ApplyDelta"/> reads. A "captured" entity is its component data keyed by type id
/// (<see cref="ComponentCodec.CaptureData"/>), the immutable per-seq snapshot both replicators diff against a
/// client's acknowledged baseline.
/// </summary>
internal static class DeltaEncoding
{
    /// <summary>Byte-for-byte equality of two captured component payloads (a component is "changed" when this is false).</summary>
    public static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>True if any component was added, removed, or its bytes changed between a baseline and current capture.</summary>
    public static bool EntityChanged(IReadOnlyDictionary<ushort, byte[]> baseComps, IReadOnlyDictionary<ushort, byte[]> curComps)
    {
        foreach (KeyValuePair<ushort, byte[]> kv in curComps)
            if (!baseComps.TryGetValue(kv.Key, out byte[]? prev) || !BytesEqual(prev, kv.Value)) return true;
        foreach (ushort tid in baseComps.Keys)
            if (!curComps.ContainsKey(tid)) return true;
        return false;
    }

    /// <summary>
    /// Writes one changed/new entity: <c>[netId][isNew][removedCompCount][removedTypeId...][(typeId,[len],data)...][0]</c>.
    /// A new entity carries all its components; an existing one lists components removed since its baseline and then
    /// only the added/changed ones (in registration order). The 7-bit length prefix is emitted only for consumer
    /// extension components (see <see cref="ReplicationRegistry.FirstExtensionTypeId"/>) so an older client can skip
    /// an id it never registered. <paramref name="baseComps"/> is null for a new entity.
    /// </summary>
    public static void WriteChangedEntity(BinaryWriter bw, ReplicationRegistry registry, int netId, bool isNew,
        IReadOnlyDictionary<ushort, byte[]>? baseComps, IReadOnlyDictionary<ushort, byte[]> curComps)
    {
        bw.Write(netId);
        bw.Write(isNew ? (byte)1 : (byte)0);

        // Removed components (existing entities only): in the baseline, gone from current.
        var removedComps = new List<ushort>();
        if (!isNew && baseComps is not null)
            foreach (ushort tid in baseComps.Keys)
                if (!curComps.ContainsKey(tid)) removedComps.Add(tid);
        bw.Write(removedComps.Count);
        foreach (ushort tid in removedComps) bw.Write(tid);

        // Changed/added components, in registration order.
        foreach (ComponentCodec codec in registry.Ordered)
        {
            if (!curComps.TryGetValue(codec.TypeId, out byte[]? data)) continue;
            bool include = baseComps is null
                || !baseComps.TryGetValue(codec.TypeId, out byte[]? prev) || !BytesEqual(prev, data);
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
