using System;

namespace KhaozEngine.WorldStore;

/// <summary>
/// The record-agnostic tunables of <see cref="StatePersistence{TState}"/>. Every knob here is about the persistence
/// MACHINERY (how often, under what key, whether a tokenless connection is written at all), never about the shape of
/// a record, which is <see cref="PersistenceBinding{TState}"/>'s job.
/// <para>A head-facing config type (<c>WorldPersistenceConfig</c>, <c>TileWorldPersistenceConfig</c>) owns the
/// documentation a game reads and projects itself onto this one at construction. That indirection is deliberate: the
/// public knobs keep their existing names, defaults and prose for the games already pinned to them, while the core
/// carries exactly one copy of the behaviour.</para>
/// </summary>
public sealed record PersistenceCoreConfig
{
    /// <summary>How often the periodic snapshot saves dirty players, seconds. A crash loses at most this much.</summary>
    public float SaveIntervalSeconds { get; init; } = 30f;

    /// <summary>Key namespace for player records. The stored key is <c>{KeyPrefix}{accountId}</c>.</summary>
    public string KeyPrefix { get; init; } = "player:";

    /// <summary>Key prefix a record that failed validation is copied to verbatim, so the intact original survives
    /// for offline inspection while the primary record is free to be overwritten by the fresh spawn. The quarantine
    /// key is <c>{QuarantineKeyPrefix}{KeyPrefix}{accountId}</c>.</summary>
    public string QuarantineKeyPrefix { get; init; } = "quarantine:";

    /// <summary>Whether a TOKENLESS connection is persisted at all. Default false: a head keys one
    /// <c>guest:{slot}</c>, a slot is a seat the next connection inherits, and a record filed under it names a chair
    /// rather than a player (#647). On, the key is a durable <c>guest:{guid}</c> minted for that one session and
    /// never the seat, which buys crash-safety within a session rather than a guest's return.</summary>
    public bool PersistGuests { get; init; }

    /// <summary>How many accounts <see cref="StatePersistence{TState}.Hints"/> holds, which is what lets a REJOINING
    /// player's entity be built where they left instead of at the configured spawn. Zero or less holds nothing,
    /// which turns the join seed off.</summary>
    public int ResumeHintCapacity { get; init; } = 1024;

    /// <summary>How far a loaded position may be from where the player already stands and still be applied QUIETLY,
    /// in whatever units the binding's position is expressed in. A rejoiner seeded from the hint is already on the
    /// loaded position, so the restore moves nothing and must not report a teleport (#642). Zero or less makes every
    /// restore a teleport.</summary>
    public float QuietRestoreDistance { get; init; } = 1f;

    /// <summary>Captures the game's opaque durable blob for a player about to be saved, given the runtime slot and
    /// the RESOLVED store key. Raised on the server thread at every save point. Null persists position only.
    /// <para>The key rather than the id the head handed over, because that is the durable id the record is actually
    /// filed under. The two differ only for a tokenless connection on a server that set <see cref="PersistGuests"/>,
    /// where the head's id is the seat and the key is the minted session id.</para></summary>
    public Func<int, string, byte[]?>? CaptureGameState { get; init; }

    /// <summary>Re-attaches a previously captured blob at load-on-join, given the runtime slot, the account id and
    /// the blob. Raised on the server thread as the loaded position is applied, and never for a player with no
    /// stored blob. Null discards any stored blob on load.</summary>
    public Action<int, string, byte[]?>? ApplyGameState { get; init; }

    /// <summary>Vets a loaded blob on the server thread before it is applied, given the runtime slot, the account id
    /// and the blob. A non-null return is the quarantine reason and rejects the WHOLE record, position included.
    /// Only raised for a record that actually carries a blob. Null accepts any blob.
    /// <para>Separate from <see cref="PersistenceBinding{TState}.Validate"/> because the blob's verdict needs to know
    /// WHOSE blob it is: a game reads the live per-player object by slot to decide. The state-shaped checks need no
    /// such context and run first.</para></summary>
    public Func<int, string, byte[]?, string?>? ValidateGameState { get; init; }

    /// <summary>Where the core's own diagnostic lines go: (message, exception or null). Null swallows them.
    /// <para>A sink rather than a logger reference because <c>KhaozEngine.WorldStore</c> is dependency-free and stays
    /// that way. A head wires this to its own logging (<c>KhaozEngine.Diagnostics</c> for the engine's own facades),
    /// so the output is identical to what this code emitted before it was generic. A null exception is an
    /// informational line, a non-null one is a warning about a store fault.</para></summary>
    public Action<string, Exception?>? Diagnostic { get; init; }
}
