using System;
using System.IO;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.Sqlite;

namespace KhaozEngine.Benchmarks.Items;

/// <summary>
/// The real store behind budgets 4 and 13: <see cref="SqliteMutationJournalStore"/> under
/// <see cref="MutationJournalExecutor"/>, at the engine's own maximum limits, because a 6.9 KB page has
/// to fit the projection section cap rather than a benchmark-shaped one.
/// </summary>
internal sealed class ItemsJournalScope : IDisposable
{
    private readonly SqliteMutationJournalStore store;
    private readonly bool deleteDatabase;

    private ItemsJournalScope(SqliteMutationJournalStore store, string databasePath, bool deleteDatabase)
    {
        this.store = store;
        DatabasePath = databasePath;
        this.deleteDatabase = deleteDatabase;
    }

    internal IMutationJournalStore Store => store;
    internal string DatabasePath { get; }

    internal static ItemsJournalScope Create(string? databasePath)
    {
        string database = databasePath ?? Path.Combine(Path.GetTempPath(), $"khaoz-items-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        var store = new SqliteMutationJournalStore(new SqliteMutationJournalStoreOptions($"Data Source={database};Pooling=False")
        {
            BusyTimeout = TimeSpan.FromSeconds(5),
            Limits = JournalLimits.Maximum,
        });
        return new ItemsJournalScope(store, database, databasePath is null);
    }

    internal long DatabaseBytes()
    {
        long bytes = 0;
        foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            string candidate = DatabasePath + suffix;
            if (File.Exists(candidate)) bytes += new FileInfo(candidate).Length;
        }

        return bytes;
    }

    public void Dispose()
    {
        store.Dispose();
        if (!deleteDatabase) return;
        foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            string path = DatabasePath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
