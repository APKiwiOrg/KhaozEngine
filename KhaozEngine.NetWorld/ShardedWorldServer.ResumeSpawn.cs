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
            : ConfiguredSpawn(slot);

    // The hint-free half of JoinSpawn: the configured spawn, else the per-slot default spread. Kept separate so the
    // reset TryGetConfiguredSpawn hands back cannot itself be the rejected hint.
    private Vector3 ConfiguredSpawn(int slot) => config.SpawnPosition?.Invoke(slot) ?? new Vector3(slot * 2f, 0f, 0f);

    /// <inheritdoc/>
    public bool TryGetConfiguredSpawn(int slot, out PlayerMoveState spawn)
    {
        if (!netIdBySlot.ContainsKey(slot)) { spawn = default; return false; }
        // The same clamp OnJoin runs, in the frame of the cell that CONTAINS the configured spawn, coming back
        // absolute. The reset itself moves the entity through SetPlayerState, so the owning cell follows on the
        // next handoff pass exactly as any other out-of-cell placement does.
        Vector3 at = ConfiguredSpawn(slot);
        spawn = RuntimeFor(host.CellFor(at.X, at.Z)).SpawnClamp(new PlayerMoveState { Position = at }, config.TickSeconds);
        return true;
    }
}
