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
/// <b>The subject key must be BINARY.</b> The find-or-create upsert's <c>ON CONFLICT(subject)</c> matches under the
/// collation of the key it lands on, not the one a statement names. Over a <c>NOCASE</c> key holding
/// <c>oidc:Alice</c>, a verified sign-in as <c>oidc:alice</c> would overwrite Alice's display name and return her
/// account. The ensure therefore refuses a table whose primary key or unique index on <c>subject</c> alone uses
/// another collation. A <c>BINARY</c> key over a column declared with another collation is adopted, since the key is
/// what matches.
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
    /// <exception cref="InvalidOperationException">The table exists without one of the four original columns, or
    /// with a subject key that is not <c>BINARY</c>. Nothing is changed either way.</exception>
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

        if (FirstNonBinarySubjectKey(db, tx, table) is { } collation)
            throw new InvalidOperationException(
                $"The account table's subject key compares under the {collation} collation, not BINARY. The " +
                "find-or-create upsert matches under the key's collation, so a sign-in could land on another account " +
                "whose subject differs only in case or padding and be handed that account. Rebuild the table with a " +
                "BINARY subject key, or name a different table in AccountTableOptions.");

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

    // The collation of the first unique key on subject alone that is not BINARY, or null when there is none. Such a
    // key (the primary key or a unique index) is what ON CONFLICT(subject) can match on, whatever collation the
    // conflict target spells. A key over several columns or over an expression, and a non-unique index, is never that
    // target. The pragmas take the table and index names as values, so nothing here builds SQL from them.
    private static string? FirstNonBinarySubjectKey(SqliteStoreConnection db, SqliteTransaction tx, string table)
    {
        var keyColumns = new Dictionary<string, List<(string? Column, string Collation)>>(StringComparer.Ordinal);
        using (SqliteCommand cmd = db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "SELECT il.name, ix.name, ix.coll FROM pragma_index_list($table) AS il, pragma_index_xinfo(il.name) AS ix " +
                "WHERE il.\"unique\" = 1 AND ix.key = 1;";
            cmd.Parameters.Add("$table", SqliteType.Text).Value = table;
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string index = reader.GetString(0);
                if (!keyColumns.TryGetValue(index, out List<(string? Column, string Collation)>? columns))
                    keyColumns[index] = columns = new List<(string? Column, string Collation)>();
                columns.Add((reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2)));
            }
        }

        foreach (List<(string? Column, string Collation)> columns in keyColumns.Values)
        {
            if (columns is [var only]
                && string.Equals(only.Column, "subject", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(only.Collation, "BINARY", StringComparison.OrdinalIgnoreCase))
                return only.Collation;
        }
        return null;
    }

    private static void Execute(SqliteStoreConnection db, SqliteTransaction tx, string sql)
    {
        using SqliteCommand cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
