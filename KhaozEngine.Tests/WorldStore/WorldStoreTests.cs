using System;
using System.IO;
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
        foreach (string p in new[] { path, path + "-wal", path + "-shm" })
            try { File.Delete(p); } catch { /* best effort */ }
    }

    [Fact] public Task SaveLoad_RoundTrips() => WorldStoreConformance.SaveLoad_RoundTrips(store, "");
    [Fact] public Task Save_Overwrites() => WorldStoreConformance.Save_Overwrites(store, "");
    [Fact] public Task Load_Absent_ReturnsNull() => WorldStoreConformance.Load_Absent_ReturnsNull(store, "");
    [Fact] public Task Delete_PresentThenAbsent() => WorldStoreConformance.Delete_PresentThenAbsent(store, "");
    [Fact] public Task Exists_TracksPresence() => WorldStoreConformance.Exists_TracksPresence(store, "");
    [Fact] public Task Keys_AreIsolated() => WorldStoreConformance.Keys_AreIsolated(store, "");
    [Fact] public Task Bytes_AreExact() => WorldStoreConformance.Bytes_AreExact(store, "");
    [Fact] public Task Concurrent_DistinctKeys() => WorldStoreConformance.Concurrent_DistinctKeys(store, "");

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
}
