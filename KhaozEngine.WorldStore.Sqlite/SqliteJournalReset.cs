using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.WorldStore.Sqlite;

/// <summary>
/// The journal RESET: every stream, event, snapshot, projection section, replay receipt and receipt stream range
/// deleted in one transaction, with <c>journal_metadata</c> and its store epoch left exactly as they were. The
/// journal comes back empty and keeps its identity.
/// <para>
/// <b>It exists for a release that wipes all game data.</b> The journal refuses a plain delete of an operation
/// receipt through <c>trg_journal_operation_delete_guard</c>, which is right for a game's own code, so the wipe
/// belongs in the engine that owns that guard. The reset opens the guard the way the store's own purge does and
/// closes it again. No caller ever names it.
/// </para>
/// <para>
/// <b>It is a separate type taking a connection string rather than a member on
/// <see cref="IMutationJournalMaintenance"/>.</b> Every host holds its journal through the store and maintenance
/// seams, and a delete-everything verb does not belong on the object an executor or an admin endpoint was handed for
/// ordinary work. A caller reaches the reset by naming this type, which is a second, deliberate step. The SQL Server
/// twin is <c>SqlServerJournalReset</c>. The in-memory store has no reset, as the content catalog's in-memory store has
/// none: its journal lives exactly as long as the instance, so a new instance is the wipe, and that instance carries
/// a new epoch.
/// </para>
/// <para>
/// <b>What it does NOT do is stop a host.</b> Drain and stop every journal writer, and every client that may retry an
/// operation, before calling it. The reset deletes every replay receipt, so a retry of an operation committed before
/// it resolves <c>NotFound</c> and is not recognized as a replay. A host still holding a stream in memory holds state
/// the journal no longer has.
/// </para>
/// </summary>
public static class SqliteJournalReset
{
    /// <summary>How long a reset waits for another connection's write transaction before refusing, the store's own default busy timeout.</summary>
    public static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Resets the journal, waiting up to <see cref="DefaultLockTimeout"/> for a writer on another connection.</summary>
    /// <param name="connectionString">The ADO.NET connection string naming the journal's database file.</param>
    /// <param name="cancellationToken">Cancels the work. Nothing is committed on the way out.</param>
    /// <returns>The rows deleted from each data table, and the store epoch that was kept.</returns>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is null, empty or whitespace.</exception>
    /// <exception cref="JournalStoreException">The file is missing (<c>Unavailable</c>), carries no journal or not the version-two journal (<c>SchemaMismatch</c>), or another connection held the write lock past the timeout (<c>Timeout</c>).</exception>
    public static Task<JournalResetResult> ResetAsync(string connectionString, CancellationToken cancellationToken = default)
        => ResetAsync(connectionString, DefaultLockTimeout, cancellationToken);

    /// <summary>
    /// Deletes every row of the journal's six data tables in one immediate transaction, children before parents, and
    /// keeps <c>journal_metadata</c> untouched.
    /// <para>
    /// <b>It validates before it deletes.</b> The journal is opened with <see cref="SqliteJournalSchemaMode.ValidateOnly"/>,
    /// so a file with no journal, a version-one journal or a malformed one is refused with <c>SchemaMismatch</c> and
    /// is never created, migrated or repaired. A missing file is refused with <c>Unavailable</c> and no file is
    /// created: a mistyped path does not leave an empty database behind.
    /// </para>
    /// <para>
    /// <b>It refuses contention rather than waiting for it forever.</b> Every SQLite journal writer holds the file's
    /// write lock for its own transaction, and so does every maintenance call. The reset takes the same lock, waits up
    /// to <paramref name="lockTimeout"/>, and is then refused with <see cref="JournalStoreFailureKind.Timeout"/> and
    /// <see cref="JournalStoreFailureCertainty.DefinitelyNotCommitted"/>, having deleted nothing. An idle store
    /// holds no lock, so the lock is not proof that every host has stopped. Stopping them is the caller's job.
    /// </para>
    /// <para>
    /// It opens its OWN connection, so the connection string has to name a durable database. A plain
    /// <c>Data Source=:memory:</c> database belongs to the connection that opened it, so the reset would find no
    /// journal there and refuse.
    /// </para>
    /// </summary>
    /// <param name="connectionString">The ADO.NET connection string naming the journal's database file.</param>
    /// <param name="lockTimeout">How long to wait for another connection's write transaction. Positive. The provider retries in whole seconds, so the wait is rounded up to one.</param>
    /// <param name="cancellationToken">Cancels the work. Nothing is committed on the way out.</param>
    /// <returns>The rows deleted from each data table, and the store epoch that was kept.</returns>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lockTimeout"/> is not positive or does not fit SQLite's millisecond timeout.</exception>
    /// <exception cref="JournalStoreException">The file is missing (<c>Unavailable</c>), carries no journal or not the version-two journal (<c>SchemaMismatch</c>), or another connection held the write lock past the timeout (<c>Timeout</c>).</exception>
    public static async Task<JournalResetResult> ResetAsync(
        string connectionString,
        TimeSpan lockTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        if (lockTimeout <= TimeSpan.Zero || lockTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(lockTimeout), lockTimeout, "A reset's lock timeout must be positive and fit SQLite's millisecond timeout.");
        cancellationToken.ThrowIfCancellationRequested();

        using var store = new SqliteMutationJournalStore(new SqliteMutationJournalStoreOptions(ExistingFileOnly(connectionString))
        {
            SchemaMode = SqliteJournalSchemaMode.ValidateOnly,
            BusyTimeout = lockTimeout,
        });
        return await store.ResetJournalAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The connection string with its default create-if-missing open mode narrowed to read and write, so a path that
    /// names no file fails to open instead of creating an empty database. Any other mode the caller chose stands.
    /// </summary>
    private static string ExistingFileOnly(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        if (builder.Mode == SqliteOpenMode.ReadWriteCreate) builder.Mode = SqliteOpenMode.ReadWrite;
        return builder.ToString();
    }
}
