using System;
using System.IO;
using KhaozEngine.Catalog;
using Microsoft.Data.Sqlite;

namespace KhaozEngine.Tests.Catalog.Sqlite;

/// <summary>
/// A temporary directory holding one catalog database and one pack root, both dying with the test, so no
/// test here shares a file with another.
/// <para>
/// It is a FILE database rather than <c>Data Source=:memory:</c> because the schema suite closes the store
/// and reopens it, which is the whole point of a ValidateOnly mode: an in-memory database dies with the held
/// connection, so a second open would see an empty one every time.
/// </para>
/// </summary>
internal sealed class TemporaryCatalogDatabase : IDisposable
{
    public TemporaryCatalogDatabase()
    {
        Root = Path.Combine(Path.GetTempPath(), "kec-sqlite-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Root);
        DatabasePath = Path.Combine(Root, "catalog.db");
        PackRoot = Path.Combine(Root, "pack");
    }

    /// <summary>The directory everything this test writes sits under.</summary>
    public string Root { get; }

    /// <summary>The catalog database file.</summary>
    public string DatabasePath { get; }

    /// <summary>The pack store root a publish writes its files to.</summary>
    public string PackRoot { get; }

    /// <summary>The connection string a store is opened with.</summary>
    public string ConnectionString => "Data Source=" + DatabasePath;

    /// <summary>A pack store over <see cref="PackRoot"/>, which a publish needs.</summary>
    public FileSystemPackStore Pack() => new(PackRoot);

    /// <summary>
    /// Runs one statement on a RAW connection, which is how the schema suite damages an object behind the
    /// store's back. It clears the pool afterwards, so the file is released before the store reopens it.
    /// </summary>
    /// <param name="sql">The statement to run.</param>
    public void Execute(string sql)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        SqliteConnection.ClearPool(connection);
    }

    /// <summary>
    /// One scalar on a RAW connection, which is how a test asserts on a table the store's own API does not
    /// expose. The whole statement is the argument rather than a table name, so every call site reads as a
    /// literal.
    /// </summary>
    /// <param name="sql">The statement to run, which must return one value.</param>
    public long Scalar(string sql)
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        long value;
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = sql;
            value = command.ExecuteScalar() is long held ? held : 0L;
        }

        SqliteConnection.ClearPool(connection);
        return value;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a green test over.
        }
    }
}
