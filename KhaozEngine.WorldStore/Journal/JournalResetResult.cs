using System;

namespace KhaozEngine.WorldStore.Journal;

/// <summary>
/// What one journal RESET deleted, table by table, and the store epoch it kept. The SQLite and SQL Server resets
/// (<c>SqliteJournalReset</c> and <c>SqlServerJournalReset</c>) both answer with this, so an operator console prints
/// one line whichever backend it drove.
/// <para>
/// <b>The epoch is the one that STOOD, and it still stands.</b> A reset deletes every row of the journal's six data
/// tables and leaves <c>journal_metadata</c> exactly as it was: the schema version, the store epoch and the metadata
/// timestamp all survive. <see cref="StoreEpoch"/> says what that means for a projection cursor.
/// </para>
/// <para>
/// Every count is what the named table held when the reset deleted it, inside the reset's one transaction and under
/// the journal's exclusive write gate, so no writer can add or remove a row between the count and the delete. A
/// second reset over an emptied journal answers zero for every table.
/// </para>
/// <para>
/// <b>The record CANNOT be built into a state it could not have come from.</b> Every rule runs in the constructor
/// and every property is get-only, so a <c>with</c> expression cannot move a value past the rules. The rules refuse a
/// negative count and an empty epoch, and nothing else: a store whose rows break its own foreign keys still has to
/// be resettable, so the counts are not checked against each other.
/// </para>
/// </summary>
public sealed record JournalResetResult
{
    /// <summary>Builds a reset result. Every count is a row count and cannot be negative.</summary>
    /// <param name="storeEpoch">The store epoch <c>journal_metadata</c> held, which the reset kept.</param>
    /// <param name="streamsDeleted">Rows deleted from <c>journal_stream</c>, one per stream head.</param>
    /// <param name="eventsDeleted">Rows deleted from <c>journal_event</c>.</param>
    /// <param name="snapshotsDeleted">Rows deleted from <c>journal_snapshot</c>.</param>
    /// <param name="projectionsDeleted">Rows deleted from <c>journal_projection</c>, one per stream section.</param>
    /// <param name="operationsDeleted">Rows deleted from <c>journal_operation</c>, one per replay receipt.</param>
    /// <param name="operationStreamsDeleted">Rows deleted from <c>journal_operation_stream</c>, one per receipt stream range.</param>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="storeEpoch"/> is empty.</exception>
    public JournalResetResult(
        Guid storeEpoch,
        long streamsDeleted,
        long eventsDeleted,
        long snapshotsDeleted,
        long projectionsDeleted,
        long operationsDeleted,
        long operationStreamsDeleted)
    {
        if (storeEpoch == Guid.Empty)
            throw new ArgumentException("A journal store epoch is never empty, so a reset cannot report one.", nameof(storeEpoch));
        ArgumentOutOfRangeException.ThrowIfNegative(streamsDeleted);
        ArgumentOutOfRangeException.ThrowIfNegative(eventsDeleted);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotsDeleted);
        ArgumentOutOfRangeException.ThrowIfNegative(projectionsDeleted);
        ArgumentOutOfRangeException.ThrowIfNegative(operationsDeleted);
        ArgumentOutOfRangeException.ThrowIfNegative(operationStreamsDeleted);
        StoreEpoch = storeEpoch;
        StreamsDeleted = streamsDeleted;
        EventsDeleted = eventsDeleted;
        SnapshotsDeleted = snapshotsDeleted;
        ProjectionsDeleted = projectionsDeleted;
        OperationsDeleted = operationsDeleted;
        OperationStreamsDeleted = operationStreamsDeleted;
    }

    /// <summary>
    /// The store epoch before the reset, which is also the store epoch after it.
    /// <para>
    /// A projection cursor binds this epoch, a stream key and the head it captured. Because the epoch survives, the
    /// store cannot tell a cursor taken before the reset from one taken after it. A read of a deleted stream answers
    /// <c>NotFound</c>. Once a stream is initialized again under the same key, an old cursor ahead of the new head
    /// answers <c>ResetRequired</c>, but one at or below it reads as valid and skips every section at or below its
    /// captured head. A consumer drops every cursor it held before the reset and reads again with none. A caller that
    /// cannot reach every consumer rotates the epoch after the reset, which makes every old cursor answer
    /// <c>ResetRequired</c>.
    /// </para>
    /// </summary>
    public Guid StoreEpoch { get; }

    /// <summary>Rows deleted from <c>journal_stream</c>, one per stream head.</summary>
    public long StreamsDeleted { get; }

    /// <summary>Rows deleted from <c>journal_event</c>.</summary>
    public long EventsDeleted { get; }

    /// <summary>Rows deleted from <c>journal_snapshot</c>.</summary>
    public long SnapshotsDeleted { get; }

    /// <summary>Rows deleted from <c>journal_projection</c>, one per stream section.</summary>
    public long ProjectionsDeleted { get; }

    /// <summary>Rows deleted from <c>journal_operation</c>, one per replay receipt.</summary>
    public long OperationsDeleted { get; }

    /// <summary>Rows deleted from <c>journal_operation_stream</c>, one per receipt stream range.</summary>
    public long OperationStreamsDeleted { get; }

    /// <summary>Every row the reset deleted, across the six data tables. Zero for a reset of an empty journal.</summary>
    public long RowsDeleted
        => StreamsDeleted + EventsDeleted + SnapshotsDeleted + ProjectionsDeleted + OperationsDeleted + OperationStreamsDeleted;

    /// <summary>The one line an operator reads: every table's count and the epoch that was kept.</summary>
    public string Summary => FormattableString.Invariant(
        $"Journal reset. Deleted {StreamsDeleted} streams, {EventsDeleted} events, {SnapshotsDeleted} snapshots, {ProjectionsDeleted} projection sections, {OperationsDeleted} operation receipts and {OperationStreamsDeleted} receipt stream ranges. The store epoch {StoreEpoch:D} was kept.");
}
