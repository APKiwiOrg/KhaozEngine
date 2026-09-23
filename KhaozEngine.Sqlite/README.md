# KhaozEngine.Sqlite

The shared SQLite store lifecycle. One type, `SqliteStoreConnection`: it holds an open
`Microsoft.Data.Sqlite` connection that is never pooled, runs the store's bootstrap DDL once, serializes every
command behind a lease, and closes the connection on dispose.

```csharp
using KhaozEngine.Sqlite;
using Microsoft.Data.Sqlite;

public sealed class AccountsStore : IDisposable
{
    private readonly SqliteStoreConnection db;

    public AccountsStore(string connectionString) => db = new SqliteStoreConnection(connectionString,
        "CREATE TABLE IF NOT EXISTS accounts (id TEXT PRIMARY KEY, data BLOB NOT NULL);");

    public async Task<byte[]?> LoadAsync(string id, CancellationToken ct = default)
    {
        using SqliteStoreLease _ = await db.EnterAsync(ct);
        using SqliteCommand cmd = db.CreateCommand();
        cmd.CommandText = "SELECT data FROM accounts WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return await cmd.ExecuteScalarAsync(ct) as byte[];
    }

    public void Dispose() => db.Dispose();
}
```

## Why the connection is never pooled

The held connection opens with `Pooling` forced off, whatever the connection string says. A store holds its
connection for its whole life, so the provider's pool has nothing to offer it, and a pooled connection is
exposed to two failures.

The first is the file. `SqliteConnection.Dispose()` on a pooled connection returns the native handle to the
pool instead of closing it, so the database file stays open for as long as the pool holds it. Windows then
refuses to delete or exclusively open that file. POSIX unlinks it happily and hands the SAME live handle to
the next store opened on that path, which quietly serves the deleted database. The same leak shipped three
times before this package existed (`SqliteWorldStore`, `SqliteWalletStore`, and a consumer's own accounts
store).

The second is the provider's checkout. Microsoft.Data.Sqlite 10.0.9 marks a pooled connection active before
it records its owner, outside the pool lock, so a concurrent open or pool clear on the same file can reclaim
a live store's connection as leaked ([dotnet/efcore#39008](https://github.com/dotnet/efcore/issues/39008)).
The reclaim either lends the handle to a second owner, which then runs statements on the same native
connection ("cannot start a transaction within a transaction", or a rollback hook freed under a running
ROLLBACK), or disposes it underneath the store (`ObjectDisposedException` on `SQLitePCL.sqlite3`). The pool
clear that used to run on every dispose was one of those triggers, and so is any
`SqliteConnection.ClearAllPools()` elsewhere in the process.

A connection in no pool is in neither path. Disposing closes it, which releases the file, and touches no other
connection on it.

## What it does not do

Schema, SQL, transactions and the record shape stay with the store. This package owns the connection, the
gate and the dispose, and knows nothing about what is in the database.

- `Connection` is the held `SqliteConnection`, for a store that needs the object itself.
- `CreateCommand()` and `BeginTransaction()` are conveniences on it.
- `EnterAsync(ct)` returns a `SqliteStoreLease`. Take the lease before touching the connection, and dispose
  it before returning from the operation. A transaction is opened under a lease held for its whole life.

## Consumers

`KhaozEngine.WorldStore.Sqlite` and `KhaozEngine.Commerce.Sqlite` are both built on it. It is opt-in and in no
umbrella: a package that needs it references it directly, as those two do.
