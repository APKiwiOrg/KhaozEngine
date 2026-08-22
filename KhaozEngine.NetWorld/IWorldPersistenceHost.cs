using System.Numerics;
using KhaozEngine.WorldStore;

namespace KhaozEngine.NetWorld;

/// <summary>
/// The server-side surface <see cref="WorldPersistence"/> drives, so the same persistence wiring (load-on-join,
/// save-on-leave, periodic dirty snapshot, keyed <c>player:{accountId}</c>) serves both the single-<see cref="KhaozEngine.Ecs.World"/>
/// <see cref="WorldServer"/> and the multi-cell <see cref="ShardedWorldServer"/>. Player-keyed and cell-agnostic:
/// <see cref="IPersistenceHost{TState}.SetPlayerState"/> places a loaded player at its saved position wherever that
/// falls (a sharded host relocates it to the containing cell on its next handoff pass).
/// <para>Every member except <see cref="SetResumePositionProvider"/> is inherited verbatim from
/// <see cref="IPersistenceHost{TState}"/> over <see cref="PlayerMoveState"/>, which is what lets one persistence
/// core serve this package and the tile stack alike. The signatures did not change when it moved, so an existing
/// implementer compiles untouched.</para>
/// </summary>
public interface IWorldPersistenceHost : IPersistenceHost<PlayerMoveState>
{
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

    // Bridges the generic seam onto this package's named delegate, so every existing implementer (WorldServer,
    // ShardedWorldServer, the sample, the test fakes) is unchanged: they still implement the ResumePositionProvider
    // overload and never see the generic one. Re-implementing a base interface member in a derived interface is
    // what makes this the most specific implementation, so the core's call through IPersistenceHost lands here.
    void IPersistenceHost<PlayerMoveState>.SetPositionHintProvider(PositionHintProvider? provider) =>
        SetResumePositionProvider(provider is null ? null : (string accountId, out Vector3 position) => provider(accountId, out position));
}
