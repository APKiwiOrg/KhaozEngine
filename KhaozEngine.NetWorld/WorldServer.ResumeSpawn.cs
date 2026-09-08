using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.WorldStore;
using KhaozEngine.Locomotion;

namespace KhaozEngine.NetWorld;

// The join-spawn seam: where a joining player's entity is BUILT, which is also the position its first snapshot
// carries. A rejoining player is placed from the resume hint (see ResumePositionCache) when one is known, so the
// client's resume snapshot is already the truth and a persistence restore landing afterwards has nothing left to
// move. Held in its own partial so both heads carry the identical seam and ShardedWorldServer.cs stays inside the
// file-size ratchet. See ShardedWorldServer.ResumeSpawn.cs for the multi-cell twin.
public sealed partial class WorldServer
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
    // configured spawn, else the per-slot default spread. The caller ground-clamps it, so a hint from a record
    // written on other terrain still settles onto this server's ground rather than being taken literally.
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
        if (!entityBySlot.ContainsKey(slot)) { spawn = default; return false; }
        // The same idle step OnJoin runs, so the reset settles onto the ground exactly as a fresh join does. The
        // simulator speaks the island's frame and the caller wants absolute, so it converts in and back out.
        spawn = ToAbsolute(simulator.Step(
            ToIsland(new PlayerMoveState { Position = ConfiguredSpawn(slot) }), MoveCommand.Idle, config.TickSeconds));
        return true;
    }
}
