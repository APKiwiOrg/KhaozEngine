using System;
using System.Collections.Generic;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The server-side surface <see cref="WorldPersistence"/> drives, so the same persistence wiring (load-on-join,
/// save-on-leave, periodic dirty snapshot, keyed <c>player:{accountId}</c>) serves both the single-<see cref="KhaozEngine.Ecs.World"/>
/// <see cref="WorldServer"/> and the multi-cell <see cref="ShardedWorldServer"/>. Player-keyed and cell-agnostic:
/// <see cref="SetPlayerState"/> places a loaded player at its saved position wherever that falls (a sharded host
/// relocates it to the containing cell on its next handoff pass).
/// </summary>
public interface IWorldPersistenceHost
{
    /// <summary>Raised after a player entity has spawned: (slot, accountId). Persistence loads the saved record here.</summary>
    event Action<int, string>? PlayerJoined;

    /// <summary>Raised just before a player despawns: (slot, accountId, final state). Persistence saves the final state here.</summary>
    event Action<int, string, PlayerMoveState>? PlayerLeaving;

    /// <summary>Overrides a joined player's authoritative state (load-on-join placement). No-op for an unknown slot.</summary>
    void SetPlayerState(int slot, in PlayerMoveState state);

    /// <summary>The slots of all currently joined players.</summary>
    IReadOnlyCollection<int> JoinedSlots { get; }

    /// <summary>The account id for a joined slot (connect token or fallback).</summary>
    bool TryGetAccountId(int slot, out string accountId);

    /// <summary>The current authoritative movement state for a joined slot.</summary>
    bool TryGetPlayerState(int slot, out PlayerMoveState state);
}
