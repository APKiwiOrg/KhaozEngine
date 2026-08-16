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

    /// <summary>Overrides a joined player's authoritative state (load-on-join placement). No-op for an unknown slot.
    /// When <paramref name="teleport"/> is true the host advances the player's monotonic teleport epoch
    /// (<see cref="MovementState.TeleportEpoch"/>) so the client cuts to the placed position rather than gliding.
    /// Load-on-join passes true only when the placement actually MOVES the player: a rejoiner whose join was seeded
    /// from <see cref="SetResumePositionProvider"/> is already standing on the loaded position, and reporting a
    /// teleport for a move of nothing is exactly what makes a quiet reconnect loud
    /// (<see cref="WorldPersistenceConfig.QuietRestoreDistance"/>). Normal per-tick movement never advances it.</summary>
    void SetPlayerState(int slot, in PlayerMoveState state, bool teleport = false);

    /// <summary>The slots of all currently joined players.</summary>
    IReadOnlyCollection<int> JoinedSlots { get; }

    /// <summary>The account id for a joined slot (connect token or fallback).</summary>
    bool TryGetAccountId(int slot, out string accountId);

    /// <summary>The current authoritative movement state for a joined slot.</summary>
    bool TryGetPlayerState(int slot, out PlayerMoveState state);

    /// <summary>
    /// Installs the hint a join consults to decide where a REJOINING player's entity is built, before the entity
    /// exists and therefore before the first snapshot carrying it goes out. Null clears it, which returns the head
    /// to spawning every join at its configured spawn position.
    /// <para>This is the server half of the reconnect contract. The client reads the RESUME SNAPSHOT to decide
    /// whether a rejoin moved the player (see <see cref="WorldClient.LocalTeleported"/>), so a head that serves
    /// the configured spawn first and applies the stored position afterwards has already told it the player
    /// teleported, and the restore then tells it a second time. Seeding the join from the hint makes the first
    /// snapshot the truth, and the load that follows corrects it only if it really disagrees (#642).</para>
    /// <para><see cref="WorldPersistence"/> installs one over its own <see cref="WorldPersistence.ResumeHints"/>
    /// at construction, so a persistence-backed server needs no wiring. A game installing its own should do it
    /// AFTER constructing the persistence layer. The default implementation is a no-op, for a host that spawns
    /// joins its own way.</para>
    /// </summary>
    void SetResumePositionProvider(ResumePositionProvider? provider) { }

    /// <summary>
    /// Where the host WOULD have built this slot with no resume hint at all: the configured spawn, ground-clamped
    /// the same way a join clamps it, in ABSOLUTE world metres. Deliberately hint-free, which is the whole point of
    /// it: it is the position a rejected record has to be reset to.
    /// <para>This is the other half of <see cref="SetResumePositionProvider"/>. Seeding the join means a REJECTED
    /// load can no longer just decline to place the player: they are already standing on the hint, which nothing
    /// validated. <see cref="WorldPersistence"/> calls this on quarantine and places the player here as a genuine
    /// teleport, because policy moved them (see <see cref="WorldPersistence"/>).</para>
    /// <para>Returns false for an unknown slot, and from the default implementation, which is what a host that
    /// installs no resume provider wants: with no seed there is nothing to undo, so the player is already on
    /// whatever spawn that host built them at.</para>
    /// </summary>
    bool TryGetConfiguredSpawn(int slot, out PlayerMoveState spawn) { spawn = default; return false; }
}
