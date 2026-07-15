using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.WorldStore;
using KhaozEngine.WorldStore.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.WorldStore;

/// <summary>
/// <see cref="SqliteWorldStore.SaveManyAsync"/>-specific coverage beyond the shared
/// <see cref="WorldStoreConformance"/> parity suite: the single-transaction, multi-row-upsert shape, and that a
/// mid-batch failure rolls back the WHOLE batch (nothing partially lands) rather than leaving the store in a state
/// no sequential loop of <see cref="IWorldStore.SaveAsync"/> calls could have produced. Uses the same in-memory
/// SQLite harness as <see cref="SqliteWorldStoreEnumerationTests"/> (a fresh <c>Data Source=:memory:</c> store per
/// test, no temp file to clean up).
/// </summary>
public class SqliteWorldStoreSaveManyTests
{
    [Fact]
    public async Task SaveManyAsync_InsertAndUpdate_LandInOneTransaction()
    {
        using var store = new SqliteWorldStore("Data Source=:memory:");
        await store.SaveAsync("existing", new byte[] { 1 });

        await store.SaveManyAsync(new (string Key, byte[] Data)[]
        {
            ("existing", new byte[] { 9, 9 }),   // update
            ("fresh", new byte[] { 2, 2 }),       // insert
        });

        Assert.Equal(new byte[] { 9, 9 }, await store.LoadAsync("existing"));
        Assert.Equal(new byte[] { 2, 2 }, await store.LoadAsync("fresh"));
    }

    [Fact]
    public async Task SaveManyAsync_MidBatchFailure_RollsBackTheWholeBatch()
    {
        using var store = new SqliteWorldStore("Data Source=:memory:");
        await store.SaveAsync("untouched", new byte[] { 7 });   // pre-existing row, must survive the rollback unchanged

        // A null data value leaves the "$d" parameter unset for the second row, so Microsoft.Data.Sqlite throws
        // (InvalidOperationException: "Value must be set") partway through the batch. If SaveManyAsync were a bare
        // loop of SaveAsync (like the interface default) the FIRST row ("ok-before") would already be durably saved
        // by the time the second row throws. Because the override runs every row inside one transaction, a failure
        // anywhere in the batch must leave NEITHER new row behind.
        var items = new List<(string Key, byte[] Data)>
        {
            ("ok-before", new byte[] { 1, 2, 3 }),
            ("bad-row", null!),
            ("ok-after", new byte[] { 7, 8, 9 }),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveManyAsync(items));

        Assert.Null(await store.LoadAsync("ok-before"));   // rolled back, not left behind
        Assert.Null(await store.LoadAsync("bad-row"));     // the row that faulted never landed either
        Assert.Null(await store.LoadAsync("ok-after"));    // never reached, and rolled back either way
        Assert.Equal(new byte[] { 7 }, await store.LoadAsync("untouched"));   // pre-existing state undisturbed
    }

    [Fact]
    public async Task SaveManyAsync_EmptyBatch_DoesNotOpenATransaction()
    {
        using var store = new SqliteWorldStore("Data Source=:memory:");
        await store.SaveManyAsync(Array.Empty<(string Key, byte[] Data)>());   // must not throw or hang the connection
        Assert.False(await store.ExistsAsync("anything"));

        // The connection is still usable afterward (no transaction left open).
        await store.SaveAsync("k", new byte[] { 1 });
        Assert.Equal(new byte[] { 1 }, await store.LoadAsync("k"));
    }
}
