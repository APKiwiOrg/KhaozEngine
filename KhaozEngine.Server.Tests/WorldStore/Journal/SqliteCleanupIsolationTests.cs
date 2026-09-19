using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

public sealed class SqliteCleanupIsolationTests
{
    [Fact]
    public async Task Database_cleanup_does_not_interrupt_an_independent_sqlite_store()
    {
        const int rounds = 50;
        using var phase = new Barrier(2);
        var failures = new ConcurrentQueue<Exception>();
        Task cleanup = Task.Run(() =>
        {
            for (int i = 0; i < rounds; i++)
            {
                var databaseToClean = new SqliteJournalTestDatabase();
                phase.SignalAndWait();
                databaseToClean.Dispose();
                phase.SignalAndWait();
            }
        });
        Task independentStores = Task.Run(() =>
        {
            for (int i = 0; i < rounds; i++)
            {
                string path = Path.Combine(
                    Path.GetTempPath(),
                    "ke-cleanup-isolation-" + Guid.NewGuid().ToString("N") + ".db");
                string connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Pooling = true,
                }.ToString();
                using (var original = new SqliteConnection(connectionString))
                {
                    original.Open();
                    using SqliteCommand create = original.CreateCommand();
                    create.CommandText = "CREATE TEMP TABLE pool_marker (id INTEGER);";
                    create.ExecuteNonQuery();
                }

                phase.SignalAndWait();
                phase.SignalAndWait();
                using (var reopened = new SqliteConnection(connectionString))
                {
                    try
                    {
                        reopened.Open();
                        using SqliteCommand read = reopened.CreateCommand();
                        read.CommandText = "SELECT COUNT(*) FROM temp.pool_marker;";
                        read.ExecuteScalar();
                    }
                    catch (Exception exception)
                    {
                        failures.Enqueue(exception);
                    }
                    finally
                    {
                        SqliteConnection.ClearPool(reopened);
                    }
                }
                if (File.Exists(path)) File.Delete(path);
            }
        });

        await Task.WhenAll(cleanup, independentStores);
        Assert.True(failures.IsEmpty, $"journal cleanup interrupted {failures.Count} independent stores");
    }
}
