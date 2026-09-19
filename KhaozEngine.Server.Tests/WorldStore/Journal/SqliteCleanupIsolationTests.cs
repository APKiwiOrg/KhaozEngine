using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

[CollectionDefinition("SQLite cleanup isolation", DisableParallelization = true)]
public sealed class SqliteCleanupIsolationCollection;

[Collection("SQLite cleanup isolation")]
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
                databaseToClean.Open(databaseToClean.NewPath());
                phase.SignalAndWait();
                databaseToClean.Dispose();
                phase.SignalAndWait();
            }
        });
        Task independentStores = Task.Run(async () =>
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
                using (var independentStore = new SqliteWorldStore(connectionString))
                {
                    byte[] stored = { 1, 2, 3 };
                    await independentStore.SaveAsync("independent", stored);
                    using (var original = new SqliteConnection(connectionString))
                    {
                        original.Open();
                        using SqliteCommand create = original.CreateCommand();
                        create.CommandText = "CREATE TEMP TABLE pool_marker (id INTEGER);";
                        create.ExecuteNonQuery();
                    }

                    phase.SignalAndWait();
                    phase.SignalAndWait();
                    Assert.Equal(stored, await independentStore.LoadAsync("independent"));
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
                }
                foreach (string storeFile in new[] { path, path + "-wal", path + "-shm" })
                    if (File.Exists(storeFile)) File.Delete(storeFile);
            }
        });

        await Task.WhenAll(cleanup, independentStores);
        Assert.True(failures.IsEmpty, $"journal cleanup interrupted {failures.Count} independent stores");
    }
}
