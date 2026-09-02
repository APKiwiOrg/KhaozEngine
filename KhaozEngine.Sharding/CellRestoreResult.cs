using System;
using System.Collections.Generic;

namespace KhaozEngine.Sharding;

/// <summary>
/// The outcome of <see cref="CellSim.TryRestoreOwned"/>: whether the snapshot decoded, the restored net ids, how many
/// unknown extension frames were retained for re-persist (retain-and-rewrite), and (on failure) the decode error. A
/// failed restore is rolled back inside <see cref="CellSim.TryRestoreOwned"/> (its partially spawned entities are
/// despawned), so the cell is left empty and the driver can quarantine the bytes instead of crash-looping on a
/// poisoned blob.
/// </summary>
public readonly struct CellRestoreResult
{
    public CellRestoreResult(bool ok, IReadOnlyList<long> netIds, int retainedFrameCount, string? error,
                             int skippedOwnedCount = 0)
    {
        Ok = ok;
        NetIds = netIds ?? Array.Empty<long>();
        RetainedFrameCount = retainedFrameCount;
        Error = error;
        SkippedOwnedCount = skippedOwnedCount;
    }

    /// <summary>True when the snapshot decoded and restored cleanly.</summary>
    public bool Ok { get; }

    /// <summary>The restored (now owned) net ids. Empty on failure.</summary>
    public IReadOnlyList<long> NetIds { get; }

    /// <summary>How many unknown extension frames were retained for verbatim re-persist. 0 unless the registry is a downgrade.</summary>
    public int RetainedFrameCount { get; }

    /// <summary>The decode error when <see cref="Ok"/> is false, else null.</summary>
    public string? Error { get; }

    /// <summary>
    /// How many entities in the blob were DROPPED because the cell already owns that
    /// <see cref="KhaozEngine.Replication.NetId"/>. 0 for every well-formed restore. Anything above 0 means the
    /// snapshot carried a stale copy of something live, which is what a persistence host that captures a cell
    /// without excluding its bound players produces, so a driver can alarm on it rather than silently restoring
    /// over a live player (#653).
    /// </summary>
    public int SkippedOwnedCount { get; }

    /// <summary>A rolled-back failure result carrying <paramref name="error"/>.</summary>
    public static CellRestoreResult Failed(string error) => new(false, Array.Empty<long>(), 0, error);
}
