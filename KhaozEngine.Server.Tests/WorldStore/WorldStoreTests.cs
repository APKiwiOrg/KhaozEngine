using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Tests.Sqlite;
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
    /// <summary>The table as every build before creation times created it.</summary>
    private const string LegacyTable =
        "CREATE TABLE world_store (key TEXT PRIMARY KEY, data BLOB NOT NULL, updated_at INTEGER NOT NULL);";

    private const long LegacyUpdatedAt = 1_700_000_000_000;

    private readonly SqliteScratchFile file = new("ke-ws-");
    private readonly SqliteWorldStore store;

    public SqliteWorldStoreConformanceTests() => store = new SqliteWorldStore(file.ConnectionString);

    public void Dispose()
    {
        store.Dispose();
        // Deliberately strict: a disposed store holds no handle on its file, so this cannot fail. Swallowing the
        // IOException here is what hid #713 on the Windows legs for as long as it lived.
        file.Dispose();
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
        using var reopened = new SqliteWorldStore(file.ConnectionString);   // fresh store, same file
        Assert.Equal(new byte[] { 7, 8, 9 }, await reopened.LoadAsync("durable"));
    }

    /// <summary>The creation time is written by the insert arm of the upsert alone, on the single save and the batch
    /// alike, and the update time keeps moving on every save as it always has.</summary>
    [Fact]
    public async Task World_store_upsert_keeps_the_first_creation_time()
    {
        long before = SqliteScratchFile.NowMilliseconds();
        await store.SaveAsync("k", new byte[] { 1 });
        long after = SqliteScratchFile.NowMilliseconds();
        (long? created, long? updated) = Times(file, "k");
        Assert.Equal(updated, created);
        Assert.InRange(created ?? -1, before, after);

        await SqliteScratchFile.WaitForClockPastAsync(after);
        before = SqliteScratchFile.NowMilliseconds();
        await store.SaveAsync("k", new byte[] { 2 });
        after = SqliteScratchFile.NowMilliseconds();
        Assert.Equal(created, Times(file, "k").Created);
        Assert.InRange(Times(file, "k").Updated ?? -1, before, after);

        await SqliteScratchFile.WaitForClockPastAsync(after);
        before = SqliteScratchFile.NowMilliseconds();
        await store.SaveManyAsync(new (string Key, byte[] Data)[] { ("k", new byte[] { 3 }), ("fresh", new byte[] { 4 }) });
        after = SqliteScratchFile.NowMilliseconds();
        Assert.Equal(created, Times(file, "k").Created);
        Assert.InRange(Times(file, "k").Updated ?? -1, before, after);
        (long? freshCreated, long? freshUpdated) = Times(file, "fresh");
        Assert.Equal(freshUpdated, freshCreated);
        Assert.InRange(freshCreated ?? -1, before, after);
    }

    /// <summary>A file an older build wrote gains a nullable <c>created_at</c> in place. Its row keeps NULL, even
    /// after a later save, because no save after the insert knows when the row was created.</summary>
    [Fact]
    public async Task World_store_table_from_an_older_build_gains_created_at()
    {
        using var legacy = new SqliteScratchFile("ke-ws-legacy-");
        legacy.Execute(LegacyTable +
            $"INSERT INTO world_store (key, data, updated_at) VALUES ('legacy', x'01', {LegacyUpdatedAt});");

        using (var widened = new SqliteWorldStore(legacy.ConnectionString))
        {
            Assert.Equal((1, "INTEGER", false), legacy.Column("world_store", "created_at"));
            Assert.Equal(((long?)null, (long?)LegacyUpdatedAt), Times(legacy, "legacy"));
            Assert.Equal(new byte[] { 1 }, await widened.LoadAsync("legacy"));

            await widened.SaveAsync("legacy", new byte[] { 2 });
            (long? created, long? updated) = Times(legacy, "legacy");
            Assert.Null(created);
            Assert.NotEqual(LegacyUpdatedAt, updated);
        }

        using var reopened = new SqliteWorldStore(legacy.ConnectionString);
        Assert.Equal(1, legacy.Column("world_store", "created_at").Count);
    }

    /// <summary>Two processes opening one legacy file at once must not both add the column. The loser of that race
    /// would fail its open on a duplicate column, so the locked recheck and the add share one immediate transaction.</summary>
    [Fact]
    public async Task World_store_widening_survives_concurrent_openers()
    {
        using var legacy = new SqliteScratchFile("ke-ws-race-");
        legacy.Execute(LegacyTable);
        await SqliteScratchFile.OpenConcurrentlyAsync(() => new SqliteWorldStore(legacy.ConnectionString), openers: 2);
        Assert.Equal(1, legacy.Column("world_store", "created_at").Count);
    }

    private static (long? Created, long? Updated) Times(SqliteScratchFile database, string key)
        => database.ReadPair("SELECT created_at, updated_at FROM world_store WHERE key = $k;", ("$k", key));
}

/// <summary>The shared conformance suite against SQL Server / Azure SQL, gated behind KE_SQLSERVER_TEST_CONNSTRING
/// (skipped in CI where no SQL Server exists). Each test runs under a fresh key namespace to isolate the shared table.
/// <para>This is the only class that touches <c>dbo.world_store</c>, and facts in one class never run at once, so the
/// fact that rebuilds the table in its older shape cannot pull it from under another.</para></summary>
public sealed class SqlServerWorldStoreConformanceTests
{
    private static readonly DateTime LegacyUpdatedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    private static string ConnectionString => Environment.GetEnvironmentVariable("KE_SQLSERVER_TEST_CONNSTRING")!;
    private static IWorldStore New() => new SqlServerWorldStore(ConnectionString);
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

    /// <summary>The SQL Server twin of the SQLite fact: only the MERGE's insert arm writes the creation time, on the
    /// single save and the batch alike. Every time is the database clock the store stamps with.</summary>
    [SqlServerFact]
    public async Task World_store_upsert_keeps_the_first_creation_time()
    {
        string cs = ConnectionString;
        IWorldStore store = New();
        string key = Ns() + "k";
        string fresh = Ns() + "fresh";

        DateTime before = await SqlServerTableProbe.ServerNowAsync(cs);
        await store.SaveAsync(key, new byte[] { 1 });
        DateTime after = await SqlServerTableProbe.ServerNowAsync(cs);
        (DateTime? created, DateTime? updated) = await TimesAsync(cs, key);
        Assert.Equal(updated, created);
        Assert.InRange(created ?? DateTime.MinValue, before, after);

        await SqlServerTableProbe.WaitForServerClockPastAsync(cs, after);
        before = await SqlServerTableProbe.ServerNowAsync(cs);
        await store.SaveAsync(key, new byte[] { 2 });
        after = await SqlServerTableProbe.ServerNowAsync(cs);
        (DateTime? keptCreated, DateTime? movedUpdated) = await TimesAsync(cs, key);
        Assert.Equal(created, keptCreated);
        Assert.InRange(movedUpdated ?? DateTime.MinValue, before, after);

        await SqlServerTableProbe.WaitForServerClockPastAsync(cs, after);
        before = await SqlServerTableProbe.ServerNowAsync(cs);
        await store.SaveManyAsync(new (string Key, byte[] Data)[] { (key, new byte[] { 3 }), (fresh, new byte[] { 4 }) });
        after = await SqlServerTableProbe.ServerNowAsync(cs);
        (keptCreated, movedUpdated) = await TimesAsync(cs, key);
        Assert.Equal(created, keptCreated);
        Assert.InRange(movedUpdated ?? DateTime.MinValue, before, after);
        (DateTime? freshCreated, DateTime? freshUpdated) = await TimesAsync(cs, fresh);
        Assert.Equal(freshUpdated, freshCreated);
        Assert.InRange(freshCreated ?? DateTime.MinValue, before, after);
    }

    /// <summary>A table an older build created gains a nullable <c>created_at</c> in place, and its row keeps NULL
    /// after a later save. The fact drops and rebuilds <c>dbo.world_store</c>, so like the journal facts that rebuild
    /// their tables it runs only against a database named for journal tests. The rebuild and the row are two batches,
    /// because SQL Server compiles a whole batch against the table the other facts left behind, and the old shape is
    /// checked before the store opens.</summary>
    [SqlServerFact]
    public async Task World_store_table_from_an_older_build_gains_created_at()
    {
        string cs = SqlServerTableProbe.RequireMarkedDatabase(ConnectionString, "-journal-test-");
        await SqlServerTableProbe.ExecuteAsync(cs, """
            IF OBJECT_ID(N'dbo.world_store', N'U') IS NOT NULL DROP TABLE dbo.world_store;
            CREATE TABLE dbo.world_store ([key] NVARCHAR(450) NOT NULL PRIMARY KEY, data VARBINARY(MAX) NOT NULL, updated_at DATETIME2 NOT NULL);
            """);
        Assert.Null(await SqlServerTableProbe.ColumnIsNullableAsync(cs, "world_store", "created_at"));
        await SqlServerTableProbe.ExecuteAsync(cs,
            "INSERT INTO dbo.world_store ([key], data, updated_at) VALUES (N'legacy', 0x01, '2026-01-01T00:00:00');");

        IWorldStore widened = New();
        Assert.True(await SqlServerTableProbe.ColumnIsNullableAsync(cs, "world_store", "created_at"));
        Assert.Equal(((DateTime?)null, (DateTime?)LegacyUpdatedAt), await TimesAsync(cs, "legacy"));
        Assert.Equal(new byte[] { 1 }, await widened.LoadAsync("legacy"));

        await widened.SaveAsync("legacy", new byte[] { 2 });
        (DateTime? created, DateTime? updated) = await TimesAsync(cs, "legacy");
        Assert.Null(created);
        Assert.NotEqual(LegacyUpdatedAt, updated);

        _ = New();
        Assert.True(await SqlServerTableProbe.ColumnIsNullableAsync(cs, "world_store", "created_at"));
    }

    private static Task<(DateTime? First, DateTime? Second)> TimesAsync(string cs, string key)
        => SqlServerTableProbe.ReadPairAsync(cs, "SELECT created_at, updated_at FROM dbo.world_store WHERE [key] = @k;", ("@k", key));
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
