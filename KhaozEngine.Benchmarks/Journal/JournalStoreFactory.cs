using System;
using System.IO;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.Sqlite;
using KhaozEngine.WorldStore.SqlServer;

namespace KhaozEngine.Benchmarks.Journal;

internal sealed class JournalStoreScope : IDisposable
{
    private readonly IDisposable? disposable;
    private readonly bool deleteDatabase;

    private JournalStoreScope(
        IMutationJournalStore store,
        IMutationJournalMaintenance maintenance,
        string provider,
        string? databasePath,
        IDisposable? disposable,
        bool deleteDatabase)
    {
        Store = store;
        Maintenance = maintenance;
        Provider = provider;
        DatabasePath = databasePath;
        this.disposable = disposable;
        this.deleteDatabase = deleteDatabase;
    }

    internal IMutationJournalStore Store { get; }
    internal IMutationJournalMaintenance Maintenance { get; }
    internal string Provider { get; }
    internal string? DatabasePath { get; }

    internal static JournalStoreScope Create(JournalBenchmarkConfig config)
    {
        JournalLimits limits = CreateLimits(config.PayloadBytes);
        if (config.SqlServerConnectionString is not null)
        {
            var store = new SqlServerMutationJournalStore(new SqlServerMutationJournalStoreOptions(config.SqlServerConnectionString)
            {
                Limits = limits,
            });
            return new JournalStoreScope(store, store, "sqlserver", null, null, false);
        }

        string database = config.DatabasePath
            ?? Path.Combine(Path.GetTempPath(), $"khaoz-journal-{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        var sqlite = new SqliteMutationJournalStore(new SqliteMutationJournalStoreOptions(
            $"Data Source={database};Pooling=False")
        {
            BusyTimeout = TimeSpan.FromSeconds(5),
            Limits = limits,
        });
        return new JournalStoreScope(sqlite, sqlite, "sqlite", database, sqlite, config.DatabasePath is null);
    }

    internal static long? DatabaseBytes(string? path)
    {
        if (path is null) return null;
        long bytes = 0;
        foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            string candidate = path + suffix;
            if (File.Exists(candidate)) bytes += new FileInfo(candidate).Length;
        }
        return bytes;
    }

    public void Dispose()
    {
        disposable?.Dispose();
        if (!deleteDatabase || DatabasePath is null) return;
        foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            string path = DatabasePath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static JournalLimits CreateLimits(int payloadBytes)
        => new(
            normalizedIntentBytes: Math.Max(payloadBytes, 256),
            eventPayloadBytes: payloadBytes,
            resultBytes: Math.Max(payloadBytes, 256),
            projectionSectionBytes: Math.Max(payloadBytes, 256),
            snapshotBytes: Math.Max(payloadBytes, 256),
            aggregateCommitBytes: Math.Max(payloadBytes * 8, 2_048),
            aggregateEventReadBytes: JournalLimits.EngineMaximumAggregateEventReadBytes,
            aggregateProjectionBytesPerStream: Math.Max(payloadBytes * 8, 2_048));
}
