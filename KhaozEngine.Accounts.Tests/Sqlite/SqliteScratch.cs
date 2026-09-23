using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Tests.Accounts.Sqlite;

/// <summary>
/// Temp database files for one test instance: created on demand, optionally seeded with a legacy table, read raw
/// behind the store's back, and deleted with every store opened over them when the instance is disposed.
/// </summary>
/// <remarks>
/// Every raw connection here is unpooled, like the store's own, so nothing this helper opens can park a handle on
/// a file or take part in a pool operation that reaches a live store.
/// </remarks>
internal sealed class SqliteScratch : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ke-accounts-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> owned = new();

    public SqliteScratch() => Directory.CreateDirectory(root);

    /// <summary>A path in the scratch directory that nothing has created yet.</summary>
    public string NewPath() => Path.Combine(root, Guid.NewGuid().ToString("N") + ".db");

    /// <summary>A new database file, seeded with <paramref name="setupSql"/> when given.</summary>
    public string NewDatabase(string? setupSql = null)
    {
        string path = NewPath();
        Execute(path, setupSql ?? "SELECT 1;");
        return path;
    }

    /// <summary>The unpooled connection string for <paramref name="path"/>.</summary>
    public static string ConnectionString(string path) =>
        new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();

    /// <summary>Keeps <paramref name="disposable"/> until this instance is disposed.</summary>
    public T Own<T>(T disposable) where T : IDisposable
    {
        owned.Add(disposable);
        return disposable;
    }

    /// <summary>Runs <paramref name="sql"/> on a raw connection to <paramref name="path"/>.</summary>
    public static void Execute(string path, string sql)
    {
        using var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every row <paramref name="sql"/> returns, its columns as invariant text joined by <c>|</c>, with a
    /// null column written <c>NULL</c>.</summary>
    public static List<string> Query(string path, string sql)
    {
        var rows = new List<string>();
        using var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();
        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var columns = new string[reader.FieldCount];
            for (int i = 0; i < columns.Length; i++)
                columns[i] = reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!;
            rows.Add(string.Join('|', columns));
        }
        return rows;
    }

    /// <summary>The column names of <paramref name="table"/> in declaration order.</summary>
    public static List<string> Columns(string path, string table)
    {
        var names = new List<string>();
        names.AddRange(Query(path, $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid;"));
        return names;
    }

    public void Dispose()
    {
        foreach (IDisposable disposable in owned) disposable.Dispose();
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a green test over.
        }
    }
}

/// <summary>
/// Grimhollow's accounts table as its builds left it, transcribed from its SQLite store: the original four
/// columns, then the widening that added the ban pair and the per-account <c>debug</c> flag.
/// </summary>
internal static class GrimhollowSqliteLayout
{
    /// <summary>The first build's table.</summary>
    public const string Original =
        "CREATE TABLE accounts (subject TEXT PRIMARY KEY, display_name TEXT NOT NULL, " +
        "whitelisted INTEGER NOT NULL, banned INTEGER NOT NULL);";

    /// <summary>The table as the widening leaves it, built the way Grimhollow built it.</summary>
    public const string Widened = Original +
        "ALTER TABLE accounts ADD COLUMN ban_reason TEXT NULL;" +
        "ALTER TABLE accounts ADD COLUMN ban_until TEXT NULL;" +
        "ALTER TABLE accounts ADD COLUMN debug INTEGER NOT NULL DEFAULT 0;";
}
