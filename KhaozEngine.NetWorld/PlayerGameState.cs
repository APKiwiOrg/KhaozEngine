using System;

namespace KhaozEngine.NetWorld;

/// <summary>
/// Identifies the player whose durable game state is being captured or applied by <see cref="WorldPersistence"/>:
/// the runtime <see cref="Slot"/> (the server-side handle, an index into the host - use it to reach the live
/// per-player object while online) and the durable <see cref="AccountId"/> (the persistence key the record is
/// stored under). Passed to <see cref="PlayerGameStateCapture"/> (save, on the server thread) and
/// <see cref="PlayerGameStateApply"/> (load-on-join, on the server thread). A readonly struct so raising the hooks
/// allocates nothing; extra fields may be added later without breaking the delegate signatures.
/// </summary>
public readonly struct PlayerPersistenceContext
{
    /// <summary>The runtime slot of the player (the server-side handle; an index into the host).</summary>
    public int Slot { get; }

    /// <summary>The durable account id the player's record is keyed by (stable across sessions and cell handoffs).</summary>
    public string AccountId { get; }

    /// <summary>Constructs a context for the given runtime slot and durable account id.</summary>
    public PlayerPersistenceContext(int slot, string accountId)
    {
        Slot = slot;
        AccountId = accountId;
    }
}

/// <summary>
/// Captures the game's opaque per-player durable blob (XP, skills, inventory, quest log, …) for a player about to be
/// persisted. Raised on the server thread at each save point (save-on-leave and the periodic dirty snapshot), so it
/// may read the live per-player game object directly by <see cref="PlayerPersistenceContext.Slot"/>. Return the
/// serialized bytes, or null / empty for "no game state" (only position is persisted). The engine never interprets
/// the bytes - it stores them verbatim in the player record - so the game owns the format and its versioning. Keep
/// the serialization deterministic for unchanged state, otherwise every dirty pass re-saves.
/// </summary>
public delegate byte[]? PlayerGameStateCapture(in PlayerPersistenceContext context);

/// <summary>
/// Applies a previously-captured game blob to a just-joined player, on the server thread as the load-on-join state is
/// applied (right after the join placement). <paramref name="blob"/> is exactly what the matching
/// <see cref="PlayerGameStateCapture"/> returned; it is never raised for a player with no saved blob. The span is only
/// valid for the duration of the call - copy it (<c>blob.ToArray()</c>) to keep the bytes. The game deserializes here
/// and, if its schema evolved, runs its own migration (e.g. a <c>KhaozEngine.Persistence.MigrationChain</c>) before
/// attaching the state to the player at <see cref="PlayerPersistenceContext.Slot"/>.
/// </summary>
public delegate void PlayerGameStateApply(in PlayerPersistenceContext context, ReadOnlySpan<byte> blob);
