using System.Numerics;

namespace KhaozEngine.NetWorld;

// The join-spawn seam: where a joining player's entity is BUILT, which is also the position its first snapshot
// carries. A rejoining player is placed from the resume hint (see ResumePositionCache) when one is known, so the
// client's resume snapshot is already the truth and a persistence restore landing afterwards has nothing left to
// move. Held in its own partial so both heads carry the identical seam and ShardedWorldServer.cs stays inside the
// file-size ratchet. See ShardedWorldServer.ResumeSpawn.cs for the multi-cell twin.
public sealed partial class WorldServer
{
    private ResumePositionProvider? resumePosition;

    /// <inheritdoc/>
    public void SetResumePositionProvider(ResumePositionProvider? provider) => resumePosition = provider;

    // The ABSOLUTE spawn position for a joining slot: the resume hint for this account when one is known, else the
    // configured spawn, else the per-slot default spread. The caller ground-clamps it, so a hint from a record
    // written on other terrain still settles onto this server's ground rather than being taken literally.
    private Vector3 JoinSpawn(int slot, string accountId) =>
        resumePosition is not null && resumePosition(accountId, out Vector3 resumed)
            ? resumed
            : config.SpawnPosition?.Invoke(slot) ?? new Vector3(slot * 2f, 0f, 0f);
}
