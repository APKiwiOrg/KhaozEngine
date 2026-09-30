using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Sqlite;

/// <summary>Creates or widens a store's schema only when its declared tables, columns or indexes are missing.</summary>
public static class SqliteSchemaWidening
{
    /// <summary>
    /// Inspects the main database without a transaction. A complete schema returns without taking the write lock
    /// or invoking <paramref name="ensureSchema"/>. Otherwise an immediate transaction acquires the write lock,
    /// rechecks the requirements and invokes the callback only if something is still missing.
    /// </summary>
    /// <param name="connection">An already open, caller-owned connection. The caller must have exclusive use of it
    /// and no active transaction, either during unpublished construction or under its store lease. It stays open.</param>
    /// <param name="requiredTables">Table names and their required column names. An empty column array still
    /// requires the table. Names match case-insensitively and must use ASCII letters, digits and underscores, with
    /// a letter or underscore first. Requirements are copied before inspection. Definitions are not validated.</param>
    /// <param name="ensureSchema">Idempotent schema DDL using the supplied transaction. The callback must fulfill
    /// all requirements, guard column additions, and leave the transaction open. Success commits. Failure rolls
    /// back all callback changes.</param>
    /// <param name="requiredIndexes">Optional required index names, validated and matched like table names.</param>
    public static void Ensure(SqliteConnection connection, IReadOnlyDictionary<string, string[]> requiredTables,
        Action<SqliteTransaction> ensureSchema, IReadOnlyCollection<string>? requiredIndexes = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(requiredTables);
        ArgumentNullException.ThrowIfNull(ensureSchema);

        var tables = new List<KeyValuePair<string, string[]>>(requiredTables.Count);
        foreach ((string table, string[] columns) in requiredTables)
        {
            ValidateIdentifier(table, nameof(requiredTables));
            if (columns is null) throw new ArgumentException("Required columns cannot be null.", nameof(requiredTables));
            var snapshot = (string[])columns.Clone();
            foreach (string column in snapshot) ValidateIdentifier(column, nameof(requiredTables));
            tables.Add(new KeyValuePair<string, string[]>(table, snapshot));
        }
        string[] indexes = requiredIndexes is null ? Array.Empty<string>() : new List<string>(requiredIndexes).ToArray();
        foreach (string index in indexes) ValidateIdentifier(index, nameof(requiredIndexes));

        if (IsComplete(connection, null, tables, indexes)) return;
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        if (!IsComplete(connection, transaction, tables, indexes))
        {
            ensureSchema(transaction);
            if (!IsComplete(connection, transaction, tables, indexes))
                throw new InvalidOperationException("Schema callback left required tables, columns or indexes missing.");
        }
        transaction.Commit();
    }

    private static bool IsComplete(SqliteConnection connection, SqliteTransaction? transaction,
        List<KeyValuePair<string, string[]>> tables, string[] indexes)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        SqliteParameter name = command.Parameters.Add("$name", SqliteType.Text);
        command.CommandText = "SELECT name FROM pragma_table_xinfo($name, 'main') " +
            "WHERE EXISTS (SELECT 1 FROM main.sqlite_schema WHERE type = 'table' AND name = $name COLLATE NOCASE);";
        foreach ((string table, string[] requiredColumns) in tables)
        {
            name.Value = table;
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (SqliteDataReader reader = command.ExecuteReader())
                while (reader.Read()) columns.Add(reader.GetString(0));
            if (columns.Count == 0) return false;
            foreach (string column in requiredColumns)
                if (!columns.Contains(column)) return false;
        }
        command.CommandText = "SELECT COUNT(*) FROM main.sqlite_schema WHERE type = 'index' AND name = $name COLLATE NOCASE;";
        foreach (string index in indexes)
        {
            name.Value = index;
            if ((long)command.ExecuteScalar()! == 0) return false;
        }
        return true;
    }

    private static void ValidateIdentifier(string name, string parameterName)
    {
        if (string.IsNullOrEmpty(name) || !IsLetterOrUnderscore(name[0]))
            throw new ArgumentException("Schema identifiers must match [A-Za-z_][A-Za-z0-9_]*.", parameterName);
        foreach (char character in name)
            if (!IsLetterOrUnderscore(character) && character is not (>= '0' and <= '9'))
                throw new ArgumentException("Schema identifiers must match [A-Za-z_][A-Za-z0-9_]*.", parameterName);
    }

    private static bool IsLetterOrUnderscore(char character)
        => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';
}
