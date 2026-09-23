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
/// <para>The held connection NEVER pools, whatever the connection string says, and that is why this type exists
/// rather than being a comment. A store holds its connection for its whole life, so the provider's pool has nothing
/// to offer it, and a pooled connection is exposed to two failures that have each broken a store here.</para>
///
/// <para>The first is the file. <c>SqliteConnection.Dispose()</c> on a pooled connection hands the native handle
/// back to the pool instead of closing it. Windows then refuses to delete or exclusively open the file, while POSIX
/// unlinks it happily and hands the SAME live handle to the next store opened on that path, which quietly serves
/// the deleted database. <c>SqliteWorldStore</c> (#713), <c>SqliteWalletStore</c> (#715) and a consumer's own
/// accounts store all shipped that leak.</para>
///
/// <para>The second is the provider's checkout. Microsoft.Data.Sqlite 10.0.9 marks a pooled connection active
/// before it records the owner, outside the pool lock, so a concurrent open or pool clear on the same file can read
/// a live store's connection as leaked and reclaim it (dotnet/efcore#39008). The reclaim either lends the handle to
/// a second owner, so two stores run statements on one native connection, or disposes it underneath the store.
/// Clearing a pool on dispose made every store's dispose one of those clears. A connection that is in no pool is
/// in neither path, and closing it releases the file.</para>
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
    /// Opens <paramref name="connectionString"/> without pooling and runs <paramref name="bootstrapSql"/> once, so
    /// the store is usable the moment the constructor returns. The connection is HELD for the lifetime of this
    /// object, which is what lets an in-memory <c>Data Source=:memory:</c> store keep its data.
    /// </summary>
    /// <param name="connectionString">The ADO.NET connection string, for example <c>Data Source=world.db</c>. A
    /// <c>Pooling</c> keyword in it is overridden to off.</param>
    /// <param name="bootstrapSql">The store's schema DDL, written to be idempotent (<c>CREATE TABLE IF NOT
    /// EXISTS</c>). Empty runs nothing.</param>
    public SqliteStoreConnection(string connectionString, string bootstrapSql)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentNullException.ThrowIfNull(bootstrapSql);
        connection = new SqliteConnection(
            new SqliteConnectionStringBuilder(connectionString) { Pooling = false }.ToString());
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

    /// <summary>Closes the database. The connection is in no pool, so closing it releases the OS handle on the
    /// file, and no other connection on the file is touched.</summary>
    public void Dispose()
    {
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
