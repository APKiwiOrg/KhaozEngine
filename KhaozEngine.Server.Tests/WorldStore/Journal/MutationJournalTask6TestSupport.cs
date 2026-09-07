using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.Sqlite;
using KhaozEngine.WorldStore.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

internal static class MutationJournalTask6TestSupport
{
    internal const string StreamKey = "task6/player";

    internal static JournalOperationIdentity Identity(int suffix, byte[]? intent = null)
        => new(
            new Guid(0, 0, 0, 0, 0, 0, 0, 0, 0, 1, checked((byte)suffix)),
            "task6/account",
            "inventory.grant",
            intent ?? new byte[] { checked((byte)suffix) });

    internal static JournalInitialization Initialization(
        int suffix,
        byte snapshotValue = 0,
        params JournalProjectionWrite[] projections)
        => new(
            Identity(suffix),
            StreamKey,
            "player.v1",
            1,
            new[] { snapshotValue },
            projections,
            "result.v1",
            1,
            new byte[] { checked((byte)suffix) });

    internal static JournalCommit Commit(
        int suffix,
        long expectedVersion,
        byte[] eventValues,
        byte resultValue,
        params JournalProjectionWrite[] projections)
        => Commit(Identity(suffix), expectedVersion, eventValues, resultValue, projections);

    internal static JournalCommit Commit(
        JournalOperationIdentity identity,
        long expectedVersion,
        byte[] eventValues,
        byte resultValue,
        params JournalProjectionWrite[] projections)
        => new(
            identity,
            new[]
            {
                new JournalStreamMutation(
                    StreamKey,
                    expectedVersion,
                    eventValues.Select(value => new JournalEvent("item.granted", 1, new[] { value })).ToArray()),
            },
            projections,
            "result.v1",
            1,
            new[] { resultValue });

    internal static JournalProjectionWrite Projection(string section, byte value)
        => new(StreamKey, section, $"{section}.v1", 1, new[] { value });

    internal static async Task<JournalEventPage> ReadAllAsync(IMutationJournalStore store, long afterVersion = 0)
        => await store.ReadEventsAsync(new JournalEventRead(StreamKey, afterVersion, null, 128, 1024 * 1024));

    internal static async Task AssertCorruptAsync(
        Func<Task> action,
        string expectedStreamKey = StreamKey)
    {
        JournalStoreException exception = await Assert.ThrowsAsync<JournalStoreException>(action);
        Assert.Equal(JournalStoreFailureKind.CorruptData, exception.Kind);
        Assert.Equal(JournalStoreFailureCertainty.CommittedDataUnreadable, exception.Certainty);
        Assert.Equal(JournalStoreFailureScope.OperationStreams, exception.Scope);
        Assert.Equal(expectedStreamKey, Assert.Single(exception.StreamKeys));
    }

    internal static async Task<JournalCompletion> WaitForCompletionAsync(MutationJournalExecutor executor)
    {
        for (int attempt = 0; attempt < 100_000; attempt++)
        {
            if (executor.TryDequeueCompletion(out JournalCompletion? completion)) return completion;
            await Task.Yield();
        }
        throw new Xunit.Sdk.XunitException("Journal executor did not produce a completion.");
    }
}

internal sealed class Task6ManualTimeProvider(DateTimeOffset initial) : TimeProvider
{
    private DateTimeOffset current = initial;
    public override DateTimeOffset GetUtcNow() => current;
    internal void Advance(TimeSpan duration) => current += duration;
}

internal sealed class Task6SqliteScope : IDisposable
{
    private readonly SqliteJournalTestDatabase database = new();

    internal Task6SqliteScope()
    {
        Path = database.NewPath();
        Clock = new Task6ManualTimeProvider(new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero));
    }

    internal string Path { get; }
    internal Task6ManualTimeProvider Clock { get; }
    internal SqliteJournalTestDatabase Database => database;

    internal SqliteMutationJournalStore Open(
        SqliteJournalTestHook? hook = null,
        TimeSpan? retryHorizon = null)
        => database.Open(
            Path,
            new SqliteMutationJournalStoreOptions(database.ConnectionString(Path))
            {
                MinimumRetryHorizon = retryHorizon ?? TimeSpan.FromHours(24),
                TimeProvider = Clock,
            },
            hook);

    public void Dispose() => database.Dispose();
}

internal sealed class Task6SqlServerScope : IDisposable
{
    private readonly string connectionString;
    private readonly List<SqlServerJournalPrefixStore> stores = new();

    internal Task6SqlServerScope()
    {
        connectionString = SqlServerJournalTestDatabase.RequireDedicatedTestDatabase(
            Environment.GetEnvironmentVariable("KE_SQLSERVER_TEST_CONNSTRING"));
        Prefix = $"task6/{Guid.NewGuid():N}/";
        Clock = new Task6ManualTimeProvider(new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero));
    }

    internal string Prefix { get; }
    internal Task6ManualTimeProvider Clock { get; }
    internal string ConnectionString => connectionString;

    internal SqlServerJournalPrefixStore Open(
        SqlServerJournalTestHook? hook = null,
        TimeSpan? retryHorizon = null,
        Task6ManualTimeProvider? clock = null)
    {
        Task6ManualTimeProvider selectedClock = clock ?? Clock;
        var inner = new SqlServerMutationJournalStore(
            new SqlServerMutationJournalStoreOptions(connectionString)
            {
                MinimumRetryHorizon = retryHorizon ?? TimeSpan.FromHours(24),
                TimeProvider = selectedClock,
            },
            hook);
        var store = new SqlServerJournalPrefixStore(inner, Prefix, selectedClock);
        stores.Add(store);
        return store;
    }

    public void Dispose()
    {
        Guid[] operations = stores.SelectMany(store => store.OwnedOperationIds).Distinct().ToArray();
        SqlServerJournalTestDatabase.CleanupAsync(connectionString, Prefix, operations).GetAwaiter().GetResult();
    }
}

internal sealed class Task6InjectedFailure : Exception;
