# KhaozEngine.Sqlite

The shared SQLite store lifecycle. One type, `SqliteStoreConnection`: it holds an open
`Microsoft.Data.Sqlite` connection, runs the store's bootstrap DDL once, serializes every command behind a
lease, and disposes by clearing the provider's connection pool before closing the connection.

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

## Why the dispose is the point

`SqliteConnection.Dispose()` alone returns the native handle to the provider's connection pool instead of
closing it, so the database file stays open for as long as the pool holds it. Windows then refuses to delete
or exclusively open that file. POSIX unlinks it happily and hands the SAME live handle to the next store
opened on that path, which quietly serves the deleted database. `SqliteConnection.ClearPool` before the
dispose is the fix, and this package is where it lives, because the same line was copied wrong three times
before it was extracted (`SqliteWorldStore`, `SqliteWalletStore`, and a consumer's own accounts store).

Clearing the pool cannot close a connection out from under a second live store on the same file: an in-use
connection is not idle in the pool and is only disposed when its own owner releases it.

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
