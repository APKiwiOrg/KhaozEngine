using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.WorldStore;

namespace KhaozEngine.NetWorld;

// The multi-cell twin of WorldServer.ResumeSpawn.cs: identical seam, identical resolution order. The only
// difference is downstream of it - the resolved position also decides which CELL the entity is built in, so a
// rejoiner is now owned by the cell they left from rather than by the spawn cell until the next handoff pass.
public sealed partial class ShardedWorldServer
{
    private ResumePositionProvider? resumePosition;
    private PersistenceKeyResolver? persistenceKeyResolver;
    private readonly Dictionary<int, string> persistenceKeyBySlot = new();
    private readonly Dictionary<string, int> slotByPersistenceKey = new(System.StringComparer.Ordinal);

    /// <inheritdoc/>
    public void SetResumePositionProvider(ResumePositionProvider? provider) => resumePosition = provider;

    /// <inheritdoc/>
    public bool TrySetPersistenceKeyResolver(PersistenceKeyResolver? resolver)
    {
        if (resolver is null) return true;
        if (persistenceKeyResolver is not null) return false;
        persistenceKeyResolver = resolver;
        return true;
    }

    /// <inheritdoc/>
    public bool TryGetPersistenceKey(int slot, out string persistenceKey)
    {
        if (persistenceKeyBySlot.TryGetValue(slot, out persistenceKey!)) return true;
        return TryGetAccountId(slot, out persistenceKey);
    }

    private bool TryBindPersistenceKey(int slot, string accountId, string verifiedPersistenceKey,
        out string persistenceKey)
    {
        persistenceKey = accountId;
        if (ResumePositionCache.IsGuestAccount(accountId) || persistenceKeyResolver is null) return true;
        try
        {
            persistenceKey = persistenceKeyResolver(new PersistenceKeyRequest(slot, accountId, verifiedPersistenceKey));
        }
        catch (System.Exception)
        {
            persistenceKey = string.Empty;
            return false;
        }
        if (slotByPersistenceKey.ContainsKey(persistenceKey)) return false;
        persistenceKeyBySlot[slot] = persistenceKey;
        slotByPersistenceKey[persistenceKey] = slot;
        return true;
    }

    private void ReleasePersistenceKey(int slot)
    {
        if (!persistenceKeyBySlot.Remove(slot, out string? persistenceKey)) return;
        slotByPersistenceKey.Remove(persistenceKey);
    }

    // The ABSOLUTE spawn position for a joining slot: the resume hint for this account when one is known, else the
    // configured spawn, else the per-slot default spread. The caller ground-clamps it in the containing cell.
    private Vector3 JoinSpawn(int slot, string persistenceKey) =>
        resumePosition is not null && resumePosition(persistenceKey, out Vector3 resumed)
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
