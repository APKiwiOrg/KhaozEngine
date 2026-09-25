using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;

namespace KhaozEngine.WorldStore.SqlServer;

/// <summary>
/// The journal RESET: every stream, event, snapshot, projection section, replay receipt and receipt stream range
/// deleted in one transaction, with <c>dbo.journal_metadata</c> and its store epoch left exactly as they were. The
/// journal comes back empty and keeps its identity.
/// <para>
/// <b>It exists for a release that wipes all game data.</b> The journal refuses a plain delete of an operation
/// receipt through <c>trg_journal_operation_delete_guard</c>, which is right for a game's own code, so the wipe
/// belongs in the engine that owns that guard. The reset opens the guard the way the store's own purge does, inside
/// its transaction, and drops it again before the commit. No caller ever names it.
/// </para>
/// <para>
/// <b>It is a separate type taking a connection string rather than a member on
/// <see cref="IMutationJournalMaintenance"/>.</b> Every host holds its journal through the store and maintenance
/// seams, and a delete-everything verb does not belong on the object an executor or an admin endpoint was handed for
/// ordinary work. A caller reaches the reset by naming this type, which is a second, deliberate step. The SQLite twin
/// is <c>SqliteJournalReset</c>. The in-memory store has no reset, as the content catalog's in-memory store has none:
/// its journal lives exactly as long as the instance, so a new instance is the wipe, and that instance carries a new
/// epoch.
/// </para>
/// <para>
/// <b>What it does NOT do is stop a host.</b> Drain and stop every journal writer, and every client that may retry an
/// operation, before calling it. The reset deletes every replay receipt, so a retry of an operation committed before
/// it resolves <c>NotFound</c> and is not recognized as a replay. A host still holding a stream in memory holds state
/// the journal no longer has.
/// </para>
/// </summary>
public static class SqlServerJournalReset
{
    /// <summary>How long a reset waits for the maintenance lock before refusing, the store's own default command timeout.</summary>
    public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>What the reset transaction's statements are given beyond the lock timeout. A whole-table delete of a large journal is slow.</summary>
    private static readonly TimeSpan StatementAllowance = TimeSpan.FromMinutes(10);

    /// <summary>Resets the journal, waiting up to <see cref="DefaultLockTimeout"/> for the schema and maintenance locks.</summary>
    /// <param name="connectionString">The ADO.NET connection string for the journal's database.</param>
    /// <param name="cancellationToken">Cancels the work. Nothing is committed on the way out.</param>
    /// <returns>The rows deleted from each data table, and the store epoch that was kept.</returns>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is null, empty or whitespace.</exception>
    /// <exception cref="JournalStoreException">The database carries no journal or not the version-two journal, or a host key the deletes would fire (<c>SchemaMismatch</c>), schema initialization, a journal writer or a maintenance call held its lock past the timeout (<c>Timeout</c>), a host row still references the journal through a <c>NO ACTION</c> key (<c>ConstraintViolation</c>), or the server failed.</exception>
    public static Task<JournalResetResult> ResetAsync(string connectionString, CancellationToken cancellationToken = default)
        => ResetAsync(connectionString, DefaultLockTimeout, cancellationToken);

    /// <summary>
    /// Deletes every row of the journal's six data tables in one serializable transaction, children before parents,
    /// and keeps <c>dbo.journal_metadata</c> untouched.
    /// <para>
    /// <b>It validates before it deletes.</b> The journal is opened with
    /// <see cref="SqlServerJournalSchemaMode.ValidateOnly"/>, behind the schema's application lock, so a database with
    /// no journal, a version-one journal or a malformed one is refused with <c>SchemaMismatch</c> and is never created,
    /// migrated or repaired. That includes a delete guard trigger that is missing, altered or disabled.
    /// </para>
    /// <para>
    /// <b>It refuses contention rather than waiting for it forever.</b> Every journal commit and initialization holds
    /// the maintenance application lock shared for its transaction, and compaction, purge and epoch rotation hold it
    /// exclusive. The reset takes it exclusive as its transaction's first statement, waits up to
    /// <paramref name="lockTimeout"/>, and is then refused with <see cref="JournalStoreFailureKind.Timeout"/> and
    /// <see cref="JournalStoreFailureCertainty.DefinitelyNotCommitted"/>, having deleted nothing. An idle host holds
    /// no lock, so the lock is not proof that every host has stopped. Stopping them is the caller's job.
    /// </para>
    /// <para>
    /// <b>It refuses to fire anything outside the journal.</b> Under the lock and before the first delete it reads
    /// every foreign key a table outside the journal declares into a journal data table. A key declared
    /// <c>CASCADE</c>, <c>SET NULL</c> or <c>SET DEFAULT</c> is refused with a whole-store <c>SchemaMismatch</c> naming
    /// it, having deleted nothing. A <c>NO ACTION</c> key fires nothing: a host row that still references the journal
    /// fails the delete instead, and the whole reset rolls back with <c>ConstraintViolation</c>. A trigger on a journal
    /// table that is not the journal's own is already a <c>SchemaMismatch</c> of the validation above.
    /// </para>
    /// <para>
    /// The validation is bounded by the lock timeout, rounded up to whole seconds, for both its wait on the schema
    /// application lock and each of its statements, and it observes <paramref name="cancellationToken"/>. A host stuck
    /// in schema initialization therefore refuses the reset with <c>Timeout</c> once the lock timeout runs out. The
    /// statements of the reset's own transaction, the deletes included, are given the lock timeout plus ten minutes.
    /// The credential needs what a <c>ValidateOnly</c> runtime identity already holds: <c>VIEW DEFINITION</c>,
    /// <c>SELECT</c> and <c>DELETE</c> on the journal tables and the <c>public</c> role behind <c>sys.sp_getapplock</c>.
    /// It needs no DDL right beyond creating the session's own temporary guard table, which every login may do.
    /// </para>
    /// </summary>
    /// <param name="connectionString">The ADO.NET connection string for the journal's database.</param>
    /// <param name="lockTimeout">How long to wait for the maintenance lock, in whole milliseconds, and for the schema lock during validation, rounded up to whole seconds. Positive.</param>
    /// <param name="cancellationToken">Cancels the work. Nothing is committed on the way out.</param>
    /// <returns>The rows deleted from each data table, and the store epoch that was kept.</returns>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lockTimeout"/> is not positive or does not fit SQL Server's millisecond lock timeout.</exception>
    /// <exception cref="JournalStoreException">The database carries no journal or not the version-two journal, or a host key the deletes would fire (<c>SchemaMismatch</c>), schema initialization, a journal writer or a maintenance call held its lock past the timeout (<c>Timeout</c>), a host row still references the journal through a <c>NO ACTION</c> key (<c>ConstraintViolation</c>), or the server failed.</exception>
    public static async Task<JournalResetResult> ResetAsync(
        string connectionString,
        TimeSpan lockTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        if (lockTimeout <= TimeSpan.Zero || Math.Ceiling(lockTimeout.TotalMilliseconds) > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(lockTimeout), lockTimeout, "A reset's lock timeout must be positive and fit SQL Server's millisecond lock timeout.");
        cancellationToken.ThrowIfCancellationRequested();

        // The validation waits on the schema application lock, so it gets the lock timeout and the caller's token.
        // Only the reset's own transaction gets the longer statement allowance.
        var store = new SqlServerMutationJournalStore(
            new SqlServerMutationJournalStoreOptions(connectionString)
            {
                SchemaMode = SqlServerJournalSchemaMode.ValidateOnly,
                CommandTimeout = lockTimeout + StatementAllowance,
            },
            testHook: null,
            schemaTimeoutSeconds: checked((int)Math.Ceiling(lockTimeout.TotalSeconds)),
            schemaCancellation: cancellationToken);
        return await store.ResetJournalAsync(lockTimeout, cancellationToken).ConfigureAwait(false);
    }
}
