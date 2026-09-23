using System;
using System.Collections.Generic;
using KhaozEngine.Sqlite;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Accounts.Sqlite;

/// <summary>
/// The accounts table's layout on SQLite, and the ensure that creates it or adopts an existing one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The layout is Grimhollow's</b>, so its database migrates in place: <c>subject</c>, <c>display_name</c>,
/// <c>whitelisted</c>, <c>banned</c>, <c>ban_reason</c> and <c>ban_until</c>. A FRESH table differs in two places
/// only. <c>display_name</c> is nullable, because a provider that gave no name stores none, and <c>subject</c> is
/// <c>NOT NULL</c> with its collation spelled out. <c>BINARY</c> is SQLite's default, so the spelling documents
/// rather than changes it, and every statement the store runs repeats it on the comparison anyway.
/// </para>
/// <para>
/// <b>The ensure is additive only.</b> An existing table must already carry the four original columns, since a
/// table without them is some other table that happens to share the name. A missing <c>ban_reason</c> or
/// <c>ban_until</c> is added as a nullable column, which is Grimhollow's own widening. Nothing is renamed,
/// dropped, backfilled or selected beyond the six owned columns, so a game's own column (Grimhollow's
/// <c>debug</c>) survives untouched and keeps its default on every row this store inserts.
/// </para>
/// <para>
/// SQLite's <c>TEXT</c> has no width, so the 128, 128 and 256 limits live in <see cref="AccountStoreRules"/>,
/// which the store applies before every write.
/// </para>
/// </remarks>
internal static class SqliteAccountSchema
{
    // The four a Grimhollow table has had since its first build. The store cannot adopt a table missing one.
    private static readonly string[] CoreColumns = { "subject", "display_name", "whitelisted", "banned" };

    /// <summary>Every column the store reads, in the order it unpacks them.</summary>
    internal const string ReadColumns = "subject, display_name, whitelisted, banned, ban_reason, ban_until";

    /// <summary>
    /// Creates the table when absent and widens it when present, in one immediate transaction so two processes
    /// opening one file cannot both add the same column. Runs from the store's constructor, before the store is
    /// published, so no other operation can hold the connection.
    /// </summary>
    /// <returns>Whether <c>display_name</c> is <c>NOT NULL</c>, which only a legacy table declares.</returns>
    /// <exception cref="InvalidOperationException">The table exists without one of the four original
    /// columns.</exception>
    internal static bool Ensure(SqliteStoreConnection db, string table, string quoted)
    {
        using SqliteTransaction tx = db.BeginTransaction();
        Execute(db, tx, $"""
            CREATE TABLE IF NOT EXISTS {quoted} (
                subject TEXT NOT NULL COLLATE BINARY PRIMARY KEY,
                display_name TEXT NULL,
                whitelisted INTEGER NOT NULL,
                banned INTEGER NOT NULL,
                ban_reason TEXT NULL,
                ban_until TEXT NULL);
            """);

        Dictionary<string, bool> notNullByColumn = ReadNotNullByColumn(db, tx, table);
        foreach (string column in CoreColumns)
        {
            if (!notNullByColumn.ContainsKey(column))
                throw new InvalidOperationException(
                    $"The account table exists without its '{column}' column, so it is not an accounts table this " +
                    "store can adopt. Name a different table in AccountTableOptions.");
        }

        if (!notNullByColumn.ContainsKey("ban_reason"))
            Execute(db, tx, $"ALTER TABLE {quoted} ADD COLUMN ban_reason TEXT NULL;");
        if (!notNullByColumn.ContainsKey("ban_until"))
            Execute(db, tx, $"ALTER TABLE {quoted} ADD COLUMN ban_until TEXT NULL;");

        tx.Commit();
        return notNullByColumn["display_name"];
    }

    // Column name to its NOT NULL flag. The table-valued pragma takes the name as a bound parameter, so the one
    // statement here that names the table by value is parameterised too.
    private static Dictionary<string, bool> ReadNotNullByColumn(SqliteStoreConnection db, SqliteTransaction tx, string table)
    {
        var columns = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        using SqliteCommand cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT name, \"notnull\" FROM pragma_table_info($table);";
        cmd.Parameters.Add("$table", SqliteType.Text).Value = table;
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read()) columns[reader.GetString(0)] = reader.GetInt64(1) != 0;
        return columns;
    }

    private static void Execute(SqliteStoreConnection db, SqliteTransaction tx, string sql)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
