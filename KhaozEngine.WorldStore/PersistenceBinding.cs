using System;
using System.Numerics;

namespace KhaozEngine.WorldStore;

/// <summary>
/// Decodes one stored record into the head's own state plus the game's opaque blob. Shaped as a named delegate
/// rather than a <c>Func</c> because it hands back three things, and a tuple would cost an allocation on a path
/// that runs once per join.
/// <para>Return false (or throw) for bytes that will not parse. Either answer routes the record straight to
/// quarantine, which is the point: an undecodable record used to fault the load task and strand the account's guard
/// forever, so progress silently stopped persisting.</para>
/// </summary>
/// <typeparam name="TState">The head's authoritative per-player movement state.</typeparam>
/// <param name="data">The exact bytes the store handed back.</param>
/// <param name="state">The decoded state, when the call returns true.</param>
/// <param name="game">The game's opaque durable blob carried by the record, or null.</param>
public delegate bool RecordDecoder<TState>(byte[] data, out TState state, out byte[]? game);

/// <summary>
/// Everything <see cref="StatePersistence{TState}"/> needs to know about a head's state that is not generic: how a
/// state becomes stored bytes, how stored bytes become a state, where a state sits in space, and what makes a
/// loaded record unacceptable. Four required delegates plus an optional distance override, no inheritance, so a head
/// supplies its record shape without the core
/// ever naming a record type.
/// <para>This is the whole seam between the shared machinery and a movement model. The save interval, the dirty
/// comparison, the load guard, quarantine, the guest policy and the rejoin hints are the same code for a continuous
/// world and a tile lattice, and this binding is what actually differs.</para>
/// <para><see cref="PositionOf"/> is the core's spatial currency for hints. A continuous head also uses the default
/// <see cref="Vector3.Distance(Vector3, Vector3)"/> quiet-restore comparison. A discrete binding can set
/// <see cref="RestoreDistance"/> so its native coordinates are compared before any lossy projection.</para>
/// </summary>
/// <typeparam name="TState">The head's authoritative per-player movement state.</typeparam>
/// <param name="PositionOf">Where a state sits, for the rejoin hint and for the quiet-restore distance test.</param>
/// <param name="Encode">Builds the stored record's bytes from a state plus the game's opaque blob (which may be
/// null). The result is compared byte-for-byte against the last save to decide whether the record is dirty, so it
/// must be deterministic for unchanged input or every pass re-saves.</param>
/// <param name="Decode">Turns stored bytes back into a state plus the blob.</param>
/// <param name="Validate">Vets a decoded record on the server thread before it is applied. A non-null return is the
/// quarantine reason and is reported verbatim through <see cref="StatePersistence{TState}.OnRecordQuarantined"/>.
/// This is the STATE-shaped half of validation (play-area bounds, a facing out of range). The opaque blob's own
/// verdict is <see cref="PersistenceCoreConfig.ValidateGameState"/>, which runs after this one and gets the slot and
/// account the blob belongs to.</param>
public sealed record PersistenceBinding<TState>(
    Func<TState, Vector3> PositionOf,
    Func<TState, byte[]?, byte[]> Encode,
    RecordDecoder<TState> Decode,
    Func<TState, byte[]?, string?> Validate)
{
    /// <summary>Optional distance between live and restored states in the binding's native coordinate space.
    /// Null uses Euclidean distance between their <see cref="PositionOf"/> values.</summary>
    public Func<TState, TState, double>? RestoreDistance { get; init; }
}
