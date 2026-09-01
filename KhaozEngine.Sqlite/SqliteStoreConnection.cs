using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Sqlite;

/// <summary>
/// The open-serialize-dispose lifecycle every SQLite-backed KhaozEngine store sits on: one held connection, one
/// semaphore that keeps SQLite from ever seeing two commands on it at once, and a dispose that genuinely releases
/// the file.
///
/// <para>The dispose is why this type exists rather than being a comment. Microsoft.Data.Sqlite pools connections by
/// default, so <c>SqliteConnection.Dispose()</c> hands the native handle back to the pool instead of closing it, and
/// the file stays open for as long as the pool holds it. Windows then refuses to delete or exclusively open it,
/// while POSIX unlinks it happily and hands the SAME live handle to the next store opened on that path, which
/// quietly serves the deleted database. <see cref="SqliteConnection.ClearPool"/> before the dispose is the whole
/// fix, and it is one line that was copied wrong three times over: <c>SqliteWorldStore</c> (#713),
/// <c>SqliteWalletStore</c> (#715) and a consumer's own accounts store all shipped the leak and all got the same
/// patch. There is one copy now, and a store that sits its schema on this one inherits it.</para>
///
/// <para>Clearing the pool cannot close a connection out from under a second live store on the same file: an
/// in-use connection is not idle in the pool, and is only disposed when its own owner releases it.</para>
///
/// <para>Sharing goes as far as the lifecycle and no further. The schema, the SQL and the record shape stay with
/// the store: this type takes the bootstrap DDL as a string, hands out commands and transactions on the connection
/// it holds, and knows nothing about what is in the database.</para>
/// </summary>
public sealed class SqliteStoreConnection : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>
    /// Opens <paramref name="connectionString"/> and runs <paramref name="bootstrapSql"/> once, so the store is
    /// usable the moment the constructor returns. The connection is HELD for the lifetime of this object, which is
    /// what lets an in-memory <c>Data Source=:memory:</c> store keep its data.
    /// </summary>
    /// <param name="connectionString">The ADO.NET connection string, for example <c>Data Source=world.db</c>.</param>
    /// <param name="bootstrapSql">The store's schema DDL, written to be idempotent (<c>CREATE TABLE IF NOT
    /// EXISTS</c>). Empty runs nothing.</param>
    public SqliteStoreConnection(string connectionString, string bootstrapSql)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentNullException.ThrowIfNull(bootstrapSql);
        connection = new SqliteConnection(connectionString);
        connection.Open();
        if (bootstrapSql.Length == 0) return;
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = bootstrapSql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>The held connection, for a store that needs the object itself. Every command issued on it must be
    /// under a lease from <see cref="EnterAsync"/>.</summary>
    public SqliteConnection Connection => connection;

    /// <summary>A fresh command on the held connection. Issue it under a lease.</summary>
    public SqliteCommand CreateCommand() => connection.CreateCommand();

    /// <summary>A transaction on the held connection. Take the lease FIRST: the gate is what keeps a second
    /// operation off the connection while this transaction is open.</summary>
    public SqliteTransaction BeginTransaction() => connection.BeginTransaction();

    /// <summary>
    /// Waits for exclusive use of the held connection and returns the lease that gives it back. Dispose the lease
    /// (a <c>using</c> declaration is the shape every store here uses) before returning from the operation.
    /// </summary>
    public async Task<SqliteStoreLease> EnterAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new SqliteStoreLease(gate);
    }

    /// <summary>Closes the database, releasing the OS handle on the file rather than parking it in the provider's
    /// connection pool. See the type doc for what the pool does to a file otherwise.</summary>
    public void Dispose()
    {
        SqliteConnection.ClearPool(connection);
        connection.Dispose();
        gate.Dispose();
    }
}

/// <summary>Exclusive use of a <see cref="SqliteStoreConnection"/>'s held connection, released by disposing it.
/// Returned by <see cref="SqliteStoreConnection.EnterAsync"/> and never constructed by a caller.</summary>
public readonly struct SqliteStoreLease : IDisposable
{
    private readonly SemaphoreSlim? gate;

    internal SqliteStoreLease(SemaphoreSlim gate) => this.gate = gate;

    /// <summary>Hands the connection back. A default-constructed lease holds nothing and releases nothing.</summary>
    public void Dispose() => gate?.Release();
}
