using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore;
using KhaozEngine.WorldStore.Sqlite;
using KhaozEngine.WorldStore.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.WorldStore;

/// <summary>The shared conformance suite against the dependency-free in-memory backend.</summary>
public class InMemoryWorldStoreConformanceTests
{
    private static IWorldStore New() => new InMemoryWorldStore();
    private static string Ns() => Guid.NewGuid().ToString("N");

    [Fact] public Task SaveLoad_RoundTrips() => WorldStoreConformance.SaveLoad_RoundTrips(New(), Ns());
    [Fact] public Task Save_Overwrites() => WorldStoreConformance.Save_Overwrites(New(), Ns());
    [Fact] public Task Load_Absent_ReturnsNull() => WorldStoreConformance.Load_Absent_ReturnsNull(New(), Ns());
    [Fact] public Task Delete_PresentThenAbsent() => WorldStoreConformance.Delete_PresentThenAbsent(New(), Ns());
    [Fact] public Task Exists_TracksPresence() => WorldStoreConformance.Exists_TracksPresence(New(), Ns());
    [Fact] public Task Keys_AreIsolated() => WorldStoreConformance.Keys_AreIsolated(New(), Ns());
    [Fact] public Task Bytes_AreExact() => WorldStoreConformance.Bytes_AreExact(New(), Ns());
    [Fact] public Task Concurrent_DistinctKeys() => WorldStoreConformance.Concurrent_DistinctKeys(New(), Ns());
    [Fact] public Task SaveMany_MatchesSequentialSaves() => WorldStoreConformance.SaveMany_MatchesSequentialSaves(New(), Ns());
    [Fact] public Task SaveMany_OverwritesExisting_AndInsertsNew_InOneBatch() => WorldStoreConformance.SaveMany_OverwritesExisting_AndInsertsNew_InOneBatch(New(), Ns());
    [Fact] public Task SaveMany_EmptyList_IsNoop() => WorldStoreConformance.SaveMany_EmptyList_IsNoop(New(), Ns());
    [Fact] public Task SaveMany_ParityWithSequentialSaveAsyncLoop() => WorldStoreConformance.SaveMany_ParityWithSequentialSaveAsyncLoop(New(), Ns(), Ns());

    [Fact]
    public async Task Load_ReturnsIndependentCopy()
    {
        IWorldStore store = new InMemoryWorldStore();
        await store.SaveAsync("k", new byte[] { 1, 2, 3 });
        byte[] first = (await store.LoadAsync("k"))!;
        first[0] = 99;                                  // mutate the returned array
        byte[] second = (await store.LoadAsync("k"))!;
        Assert.Equal(new byte[] { 1, 2, 3 }, second);   // stored state unaffected
    }
}

/// <summary>The shared conformance suite against the on-disk SQLite backend (a fresh temp DB per test).</summary>
public sealed class SqliteWorldStoreConformanceTests : IDisposable
{
    private readonly string path;
    private readonly SqliteWorldStore store;

    public SqliteWorldStoreConformanceTests()
    {
        path = Path.Combine(Path.GetTempPath(), "ke-ws-" + Guid.NewGuid().ToString("N") + ".db");
        store = new SqliteWorldStore($"Data Source={path}");
    }

    public void Dispose()
    {
        store.Dispose();
        // Deliberately strict: a disposed store holds no handle on its file, so this cannot fail. Swallowing the
        // IOException here is what hid #713 on the Windows legs for as long as it lived.
        foreach (string p in new[] { path, path + "-wal", path + "-shm" }) File.Delete(p);
    }

    [Fact] public Task SaveLoad_RoundTrips() => WorldStoreConformance.SaveLoad_RoundTrips(store, "");
    [Fact] public Task Save_Overwrites() => WorldStoreConformance.Save_Overwrites(store, "");
    [Fact] public Task Load_Absent_ReturnsNull() => WorldStoreConformance.Load_Absent_ReturnsNull(store, "");
    [Fact] public Task Delete_PresentThenAbsent() => WorldStoreConformance.Delete_PresentThenAbsent(store, "");
    [Fact] public Task Exists_TracksPresence() => WorldStoreConformance.Exists_TracksPresence(store, "");
    [Fact] public Task Keys_AreIsolated() => WorldStoreConformance.Keys_AreIsolated(store, "");
    [Fact] public Task Bytes_AreExact() => WorldStoreConformance.Bytes_AreExact(store, "");
    [Fact] public Task Concurrent_DistinctKeys() => WorldStoreConformance.Concurrent_DistinctKeys(store, "");
    [Fact] public Task SaveMany_MatchesSequentialSaves() => WorldStoreConformance.SaveMany_MatchesSequentialSaves(store, "many-");
    [Fact] public Task SaveMany_OverwritesExisting_AndInsertsNew_InOneBatch() => WorldStoreConformance.SaveMany_OverwritesExisting_AndInsertsNew_InOneBatch(store, "upsert-");
    [Fact] public Task SaveMany_EmptyList_IsNoop() => WorldStoreConformance.SaveMany_EmptyList_IsNoop(store, "empty-");
    [Fact] public Task SaveMany_ParityWithSequentialSaveAsyncLoop() => WorldStoreConformance.SaveMany_ParityWithSequentialSaveAsyncLoop(store, "parity-many-", "parity-loop-");

    [Fact]
    public async Task SurvivesReopen_OnSameFile()
    {
        await store.SaveAsync("durable", new byte[] { 7, 8, 9 });
        using var reopened = new SqliteWorldStore($"Data Source={path}");   // fresh store, same file
        Assert.Equal(new byte[] { 7, 8, 9 }, await reopened.LoadAsync("durable"));
    }
}

/// <summary>The shared conformance suite against SQL Server / Azure SQL, gated behind KE_SQLSERVER_TEST_CONNSTRING
/// (skipped in CI where no SQL Server exists). Each test runs under a fresh key namespace to isolate the shared table.</summary>
public sealed class SqlServerWorldStoreConformanceTests
{
    private static IWorldStore New()
        => new SqlServerWorldStore(Environment.GetEnvironmentVariable("KE_SQLSERVER_TEST_CONNSTRING")!);
    private static string Ns() => Guid.NewGuid().ToString("N") + ":";

    [SqlServerFact] public Task SaveLoad_RoundTrips() => WorldStoreConformance.SaveLoad_RoundTrips(New(), Ns());
    [SqlServerFact] public Task Save_Overwrites() => WorldStoreConformance.Save_Overwrites(New(), Ns());
    [SqlServerFact] public Task Load_Absent_ReturnsNull() => WorldStoreConformance.Load_Absent_ReturnsNull(New(), Ns());
    [SqlServerFact] public Task Delete_PresentThenAbsent() => WorldStoreConformance.Delete_PresentThenAbsent(New(), Ns());
    [SqlServerFact] public Task Exists_TracksPresence() => WorldStoreConformance.Exists_TracksPresence(New(), Ns());
    [SqlServerFact] public Task Keys_AreIsolated() => WorldStoreConformance.Keys_AreIsolated(New(), Ns());
    [SqlServerFact] public Task Bytes_AreExact() => WorldStoreConformance.Bytes_AreExact(New(), Ns());
    [SqlServerFact] public Task Concurrent_DistinctKeys() => WorldStoreConformance.Concurrent_DistinctKeys(New(), Ns());
    [SqlServerFact] public Task SaveMany_MatchesSequentialSaves() => WorldStoreConformance.SaveMany_MatchesSequentialSaves(New(), Ns());
    [SqlServerFact] public Task SaveMany_OverwritesExisting_AndInsertsNew_InOneBatch() => WorldStoreConformance.SaveMany_OverwritesExisting_AndInsertsNew_InOneBatch(New(), Ns());
    [SqlServerFact] public Task SaveMany_EmptyList_IsNoop() => WorldStoreConformance.SaveMany_EmptyList_IsNoop(New(), Ns());
    [SqlServerFact] public Task SaveMany_ParityWithSequentialSaveAsyncLoop() => WorldStoreConformance.SaveMany_ParityWithSequentialSaveAsyncLoop(New(), Ns(), Ns());
}

/// <summary>
/// Bare-bones <see cref="IWorldStore"/> implementing ONLY the four required members - no <c>SaveManyAsync</c>
/// override - so the shared conformance suite's <c>SaveMany_*</c> cases exercise <see cref="IWorldStore"/>'s
/// DEFAULT interface implementation (a loop of <see cref="IWorldStore.SaveAsync"/> calls). Proves every
/// pre-existing <see cref="IWorldStore"/> implementation - including a consumer-owned one written before
/// <c>SaveManyAsync</c> existed - keeps compiling and behaving correctly unchanged.
/// </summary>
public class MinimalWorldStoreDefaultSaveManyTests
{
    private sealed class MinimalWorldStore : IWorldStore
    {
        private readonly Dictionary<string, byte[]> data = new();
        public Task<byte[]?> LoadAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(data.TryGetValue(key, out byte[]? v) ? v : null);
        public Task SaveAsync(string key, byte[] value, CancellationToken ct = default)
        { data[key] = value; return Task.CompletedTask; }
        public Task<bool> DeleteAsync(string key, CancellationToken ct = default) => Task.FromResult(data.Remove(key));
        public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(data.ContainsKey(key));
    }

    private static IWorldStore New() => new MinimalWorldStore();
    private static string Ns() => Guid.NewGuid().ToString("N");

    [Fact] public Task SaveMany_MatchesSequentialSaves() => WorldStoreConformance.SaveMany_MatchesSequentialSaves(New(), Ns());
    [Fact] public Task SaveMany_OverwritesExisting_AndInsertsNew_InOneBatch() => WorldStoreConformance.SaveMany_OverwritesExisting_AndInsertsNew_InOneBatch(New(), Ns());
    [Fact] public Task SaveMany_EmptyList_IsNoop() => WorldStoreConformance.SaveMany_EmptyList_IsNoop(New(), Ns());
    [Fact] public Task SaveMany_ParityWithSequentialSaveAsyncLoop() => WorldStoreConformance.SaveMany_ParityWithSequentialSaveAsyncLoop(New(), Ns(), Ns());
}
