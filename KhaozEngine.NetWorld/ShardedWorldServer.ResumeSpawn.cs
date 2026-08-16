using System.Numerics;

namespace KhaozEngine.NetWorld;

// The multi-cell twin of WorldServer.ResumeSpawn.cs: identical seam, identical resolution order. The only
// difference is downstream of it - the resolved position also decides which CELL the entity is built in, so a
// rejoiner is now owned by the cell they left from rather than by the spawn cell until the next handoff pass.
public sealed partial class ShardedWorldServer
{
    private ResumePositionProvider? resumePosition;

    /// <inheritdoc/>
    public void SetResumePositionProvider(ResumePositionProvider? provider) => resumePosition = provider;

    // The ABSOLUTE spawn position for a joining slot: the resume hint for this account when one is known, else the
    // configured spawn, else the per-slot default spread. The caller ground-clamps it in the containing cell.
    private Vector3 JoinSpawn(int slot, string accountId) =>
        resumePosition is not null && resumePosition(accountId, out Vector3 resumed)
            ? resumed
            : config.SpawnPosition?.Invoke(slot) ?? new Vector3(slot * 2f, 0f, 0f);
}
