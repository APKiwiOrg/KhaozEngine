using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Sqlite;

/// <summary>
/// A temp SQLite file a store opens by connection string while the test reads and writes the same file on its own
/// unpooled connection, so a fact can build a table the way an older build left it and read back columns the store
/// never returns. Disposing deletes the file and its side files.
/// </summary>
internal sealed class SqliteScratchFile : IDisposable
{
    public SqliteScratchFile(string stem)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), stem + Guid.NewGuid().ToString("N") + ".db");
        ConnectionString = $"Data Source={Path}";
    }

    /// <summary>The file on disk.</summary>
    public string Path { get; }

    /// <summary>The connection string a store under test opens.</summary>
    public string ConnectionString { get; }

    /// <summary>Runs <paramref name="sql"/> on a fresh unpooled connection.</summary>
    public void Execute(string sql)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>The first two columns of the single row <paramref name="sql"/> selects, each null when NULL.</summary>
    public (long? First, long? Second) ReadPair(string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read(), "The row the fact reads does not exist.");
        return (reader.IsDBNull(0) ? null : reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1));
    }

    /// <summary>How many columns named <paramref name="column"/> the table has, and the declared type and NOT NULL
    /// flag of the first.</summary>
    public (int Count, string? Type, bool NotNull) Column(string table, string column)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT type, \"notnull\" FROM pragma_table_info($table) WHERE name = $column;";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        using SqliteDataReader reader = command.ExecuteReader();
        int count = 0;
        string? type = null;
        bool notNull = false;
        while (reader.Read())
        {
            if (count++ > 0) continue;
            type = reader.GetString(0);
            notNull = reader.GetInt64(1) != 0;
        }
        return (count, type, notNull);
    }

    public void Dispose()
    {
        foreach (string suffix in new[] { "", "-journal", "-wal", "-shm" }) File.Delete(Path + suffix);
    }

    /// <summary>The wall clock in unix milliseconds, the encoding the commerce and world store SQLite tables use.</summary>
    public static long NowMilliseconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Returns once the wall clock reads a later millisecond than <paramref name="milliseconds"/>, so the
    /// next write cannot share a stamp with the last.</summary>
    public static async Task WaitForClockPastAsync(long milliseconds)
    {
        while (NowMilliseconds() <= milliseconds) await Task.Delay(1);
    }

    /// <summary>
    /// Opens <paramref name="openers"/> stores at once through <paramref name="open"/>, released together from a
    /// barrier, and disposes every one that opened. A store whose open threw fails the fact with that exception.
    /// </summary>
    public static async Task OpenConcurrentlyAsync<TStore>(Func<TStore> open, int openers) where TStore : IDisposable
    {
        using var start = new Barrier(openers);
        Task<TStore>[] opens = Enumerable.Range(0, openers)
            .Select(_ => Task.Factory.StartNew(() =>
            {
                start.SignalAndWait();
                return open();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToArray();
        try
        {
            await Task.WhenAll(opens);
        }
        finally
        {
            foreach (Task<TStore> opened in opens)
                if (opened.IsCompletedSuccessfully) opened.Result.Dispose();
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
}
