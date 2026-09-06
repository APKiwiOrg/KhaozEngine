using System;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

public sealed partial class SqlServerMutationJournalFailureTests
{
    private SqlServerJournalPrefixStore CreateStore(SqlServerJournalTestHook? hook = null, string? prefix = null)
    {
        string connectionString = DedicatedConnectionString;
        var clock = new SqlServerJournalManualTimeProvider(new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero));
        var inner = new SqlServerMutationJournalStore(
            new SqlServerMutationJournalStoreOptions(connectionString) { TimeProvider = clock },
            hook);
        var store = new SqlServerJournalPrefixStore(inner, prefix ?? $"journal-test/{Guid.NewGuid():N}/", clock);
        ownedStores.Add(store);
        return store;
    }

    private static JournalInitialization Initialization(int suffix, byte[]? snapshot = null)
        => new(Identity(suffix), "player/a", "player.v1", 1, snapshot ?? Array.Empty<byte>(), Array.Empty<JournalProjectionWrite>(), "result.v1", 1, new byte[] { 1 });

    private static JournalCommit Commit(int suffix, bool projections = true)
    {
        JournalProjectionWrite[] writes = projections
            ? new[] { new JournalProjectionWrite("player/a", "bag", "bag.v1", 1, new byte[] { 9 }) }
            : Array.Empty<JournalProjectionWrite>();
        return new JournalCommit(
            Identity(suffix),
            new[] { new JournalStreamMutation("player/a", 0, new[] { new JournalEvent("state.changed", 1, new byte[] { 7 }) }) },
            writes,
            "result.v1",
            1,
            new byte[] { 41 });
    }

    private static JournalOperationIdentity Identity(int suffix)
        => new(new Guid(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, checked((byte)suffix)), "world/account", "bank.deposit", new byte[] { checked((byte)suffix) });

    private static async Task AssertCorrupt(Func<Task> action)
    {
        JournalStoreException exception = await Assert.ThrowsAsync<JournalStoreException>(action);
        Assert.Equal(JournalStoreFailureKind.CorruptData, exception.Kind);
        Assert.Equal(JournalStoreFailureCertainty.CommittedDataUnreadable, exception.Certainty);
    }

    public void Dispose()
    {
        if (ownedStores.Count == 0) return;
        foreach (SqlServerJournalPrefixStore store in ownedStores)
            SqlServerJournalTestDatabase.CleanupAsync(DedicatedConnectionString, store.Prefix, store.OwnedOperationIds).GetAwaiter().GetResult();
    }

    private sealed class InjectedJournalFailure : Exception;
}
