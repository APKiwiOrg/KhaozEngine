using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using Microsoft.Data.SqlClient;

namespace KhaozEngine.Catalog.SqlServer;

/// <summary>
/// The connection half: a fresh pooled <see cref="SqlConnection"/> per call, an
/// <see cref="IsolationLevel.Serializable"/> transaction around every write, and the translation of a
/// serialization failure into the reason a caller already handles.
/// <para>
/// <b>There is no held connection and no in-process semaphore</b>, which is the shape
/// <c>SqlServerWalletStore</c> established and the right one for a backend whose second writer is in another
/// process. Opening is cheap because the driver pools, and a pooled connection per call is what lets two
/// consoles contend in the database rather than pretending they cannot.
/// </para>
/// <para>
/// <b>Serializable is the whole concurrency story, so every write takes it</b>, not just the publish. The
/// publish is where it matters most, because the commit re-reads the highest published version and then writes
/// against it, and a weaker level would let another publish slip between the read and the write. The id marks
/// want it for the same reason: reserve-before-issue is a read then a write of one row, and it is the one
/// invariant that cannot be recovered by retrying.
/// </para>
/// </summary>
public sealed partial class SqlServerContentAuthoringStore
{
    /// <summary>Deadlock victim. The database picked this session to roll back so another could proceed.</summary>
    const int DeadlockVictim = 1205;

    /// <summary>Snapshot update conflict, which a snapshot-isolation database answers with instead.</summary>
    const int SnapshotConflict = 3960;

    /// <summary>A fresh pooled connection, open. The caller owns it.</summary>
    async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return connection;
    }

    /// <summary>One read under its own connection and no transaction.</summary>
    async Task<T> ReadAsync<T>(
        Func<SqlServerCatalogScope, CancellationToken, Task<T>> body,
        CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await body(new SqlServerCatalogScope(connection, null), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One write under its own connection and one Serializable transaction. The transaction rolls back on the
    /// way out when the body threw, because a disposed uncommitted transaction is a rollback.
    /// </summary>
    async Task<T> WriteAsync<T>(
        Func<SqlServerCatalogScope, CancellationToken, Task<T>> body,
        CancellationToken cancellationToken)
    {
        await using SqlConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqlTransaction transaction = (SqlTransaction)await connection
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);

        try
        {
            T result = await body(new SqlServerCatalogScope(connection, transaction), cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (SqlException exception) when (IsContention(exception))
        {
            throw Contended(exception);
        }
    }

    /// <summary>The same, for a write that answers nothing.</summary>
    async Task WriteAsync(
        Func<SqlServerCatalogScope, CancellationToken, Task> body,
        CancellationToken cancellationToken)
        => await WriteAsync<bool>(
            async (scope, token) =>
            {
                await body(scope, token).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Whether the database refused this transaction because another one held what it needed. Both numbers
    /// mean the same thing to a caller, and neither is a defect in the plan.
    /// </summary>
    static bool IsContention(SqlException exception)
        => exception.Number is DeadlockVictim or SnapshotConflict;

    /// <summary>
    /// A serialization failure as the caller's 409. It carries the SAME reason as the optimistic base-version
    /// check, because the remedy is identical: re-read the baseline and prepare the plan again. A separate
    /// token would make one condition two things for a console to handle, and the console cannot tell them
    /// apart anyway.
    /// </summary>
    static ContentAuthoringException Contended(SqlException exception)
        => Moved(FormattableString.Invariant(
            $"The database refused this transaction because another writer held what it needed (SQL error {exception.Number}). Nothing was written. Re-read the baseline and prepare the plan again, exactly as for a base version that moved: {exception.Message}"));
}
