using KhaozEngine.Sqlite;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Commerce.Sqlite;

/// <summary>
/// The wallet tables on SQLite, and the ensure that creates them or widens the ones an older build created.
/// </summary>
/// <remarks>
/// <para>
/// Every time is unix milliseconds, the encoding <c>wallet_ledger.created_at</c> has always used. A
/// <c>wallet_balance</c> row carries <c>created_at</c> beside the <c>updated_at</c> every credit and debit moves. A
/// <c>grant_schedule</c> row carries <c>created_at</c> and an <c>updated_at</c> that moves only when a write changes
/// the stored instant. <c>wallet_ledger</c> is append only and its <c>created_at</c> is already the insert time.
/// </para>
/// <para>
/// <b>The widening is additive only.</b> A table an older build created gains each missing column as a nullable
/// column, and its rows keep NULL there, because no write since knows when the row was created. A fresh table is
/// declared with the same nullable columns in the same place, so a fresh file and a widened one have one shape.
/// </para>
/// </remarks>
internal static class SqliteWalletSchema
{
    private const string CreateTables = @"CREATE TABLE IF NOT EXISTS wallet_ledger (
                 id INTEGER PRIMARY KEY AUTOINCREMENT,
                 account_id TEXT NOT NULL, currency_id TEXT NOT NULL, delta INTEGER NOT NULL,
                 idempotency_key TEXT NOT NULL, reason INTEGER NOT NULL, source_ref TEXT NULL,
                 post_balance INTEGER NOT NULL, created_at INTEGER NOT NULL);
               CREATE UNIQUE INDEX IF NOT EXISTS ux_ledger_idem ON wallet_ledger(account_id, currency_id, idempotency_key);
               CREATE INDEX IF NOT EXISTS ix_ledger_acct ON wallet_ledger(account_id, currency_id, id DESC);
               CREATE TABLE IF NOT EXISTS wallet_balance (
                 account_id TEXT NOT NULL, currency_id TEXT NOT NULL, amount INTEGER NOT NULL,
                 updated_at INTEGER NOT NULL, created_at INTEGER NULL, PRIMARY KEY(account_id, currency_id));
               CREATE TABLE IF NOT EXISTS grant_schedule (
                 account_id TEXT NOT NULL, reward_id TEXT NOT NULL, next_available_utc INTEGER NOT NULL,
                 created_at INTEGER NULL, updated_at INTEGER NULL, PRIMARY KEY(account_id, reward_id));";

    // The columns the creation times added, in the order a fresh table declares them.
    private static readonly (string Table, string Column)[] AddedColumns =
    {
        ("wallet_balance", "created_at"),
        ("grant_schedule", "created_at"),
        ("grant_schedule", "updated_at"),
    };

    /// <summary>
    /// Creates the tables when absent and widens them when present, in one immediate transaction. Two processes
    /// opening one legacy file at once would otherwise both read a column as missing and the second add would fail on
    /// a duplicate column. <c>BEGIN IMMEDIATE</c> takes the write lock before the read, so the second waits and then
    /// finds the column. Runs from the store's constructor, before the store is published, so nothing else holds the
    /// connection.
    /// </summary>
    internal static void Ensure(SqliteStoreConnection db)
    {
        using SqliteTransaction tx = db.Connection.BeginTransaction(deferred: false);
        using SqliteCommand cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = CreateTables;
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column;";
        SqliteParameter table = cmd.Parameters.Add("$table", SqliteType.Text);
        SqliteParameter column = cmd.Parameters.Add("$column", SqliteType.Text);
        foreach ((string tableName, string columnName) in AddedColumns)
        {
            table.Value = tableName;
            column.Value = columnName;
            if ((long)cmd.ExecuteScalar()! != 0) continue;
            using SqliteCommand add = db.CreateCommand();
            add.Transaction = tx;
            add.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} INTEGER NULL;";
            add.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
