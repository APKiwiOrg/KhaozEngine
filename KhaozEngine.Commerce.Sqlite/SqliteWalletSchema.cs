using System.Collections.Generic;
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

    private static readonly IReadOnlyDictionary<string, string[]> RequiredTables = new Dictionary<string, string[]>
    {
        ["wallet_ledger"] = new[] { "id", "account_id", "currency_id", "delta", "idempotency_key", "reason",
            "source_ref", "post_balance", "created_at" },
        ["wallet_balance"] = new[] { "account_id", "currency_id", "amount", "updated_at", "created_at" },
        ["grant_schedule"] = new[] { "account_id", "reward_id", "next_available_utc", "created_at", "updated_at" },
    };

    private static readonly string[] RequiredIndexes = { "ux_ledger_idem", "ix_ledger_acct" };

    /// <summary>
    /// Reads a complete schema without taking the write lock. Missing tables, columns or indexes are rechecked
    /// under one immediate transaction before idempotent DDL runs. Construction has exclusive use of the connection.
    /// </summary>
    internal static void Ensure(SqliteStoreConnection db)
    {
        SqliteSchemaWidening.Ensure(db.Connection, RequiredTables, tx => Widen(db, tx), RequiredIndexes);
    }

    private static void Widen(SqliteStoreConnection db, SqliteTransaction tx)
    {
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
    }
}
