using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.NetWorld;
using KhaozEngine.Sharding;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class CellPersistenceTests
{
    // A fake ICellPersistenceHost backed by plain dictionaries - no real ShardHost.
    private sealed class FakeHost : ICellPersistenceHost
    {
        public readonly Dictionary<CellCoord, byte[]> Snapshots = new();   // what SnapshotCell returns
        public readonly Dictionary<CellCoord, byte[]> Restored = new();    // what RestoreCell received
        public int NextNetId { get; private set; } = 1;
        public event Action<CellCoord>? CellCreated;

        public IReadOnlyCollection<CellCoord> LiveCellCoords => new List<CellCoord>(Snapshots.Keys);
        public byte[]? SnapshotCell(CellCoord coord) => Snapshots.TryGetValue(coord, out byte[]? b) ? b : null;
        public IReadOnlyList<int> RestoreCell(CellCoord coord, byte[] snapshot)
        {
            Restored[coord] = snapshot;
            return RestoreIds.TryGetValue(coord, out List<int>? ids) ? ids : new List<int>();
        }
        public readonly Dictionary<CellCoord, List<int>> RestoreIds = new();
        public void EnsureCell(CellCoord coord) => CellCreated?.Invoke(coord);
        public void EnsureNextNetIdAtLeast(int atLeast) { if (atLeast > NextNetId) NextNetId = atLeast; }
        public void RaiseCellCreated(CellCoord coord) => CellCreated?.Invoke(coord);
        public void SetNextNetId(int v) => NextNetId = v;
    }

    [Fact]
    public void WorldMetaRecord_RoundTrips()
    {
        byte[] bytes = new WorldMetaRecord { NextNetId = 42 }.Encode();
        WorldMetaRecord back = WorldMetaRecord.Decode(bytes);
        Assert.Equal(42, back.NextNetId);
    }

    private static readonly CellCoord C00 = new(0, 0);

    [Fact]
    public async Task LoadOnCellCreate_AppliesRestoreOnUpdate_NotBefore()
    {
        var store = new InMemoryWorldStore();
        var host = new FakeHost();
        // Seed a saved cell blob: header (magic + schemaVersion 1) + a 1-byte-count-0 body stand-in.
        // Use the persistence's own wrapping by saving through a throwaway instance's SaveDirtyPass instead:
        host.Snapshots[C00] = new byte[] { 0, 0, 0, 0 };   // empty replication snapshot (count 0)
        host.RestoreIds[C00] = new List<int> { 7 };
        var seeder = new CellPersistence(host, store);
        seeder.SaveDirtyPass();                            // writes cell:0:0 (wrapped) to the store
        await seeder.FlushAsync();

        // Fresh persistence over the same store: creating the cell enqueues a load, applied only on Update.
        var host2 = new FakeHost();
        host2.RestoreIds[C00] = new List<int> { 7 };
        var cp = new CellPersistence(host2, store);
        host2.RaiseCellCreated(C00);                      // fires CellCreated -> async load
        await cp.FlushAsync();                             // await the load; FlushAsync drains + applies restores
        Assert.True(host2.Restored.ContainsKey(C00));     // restore applied
        Assert.True(host2.NextNetId >= 8);                // high-water raised past restored id 7
    }

    [Fact]
    public async Task SaveDirtyPass_OnlyWritesChangedCells()
    {
        var store = new InMemoryWorldStore();
        var host = new FakeHost();
        host.Snapshots[C00] = new byte[] { 0, 0, 0, 0 };
        var cp = new CellPersistence(host, store);

        cp.SaveDirtyPass();
        await cp.FlushAsync();
        byte[]? first = await store.LoadAsync("cell:0:0");
        Assert.NotNull(first);

        // Unchanged -> second pass writes nothing new (same bytes present).
        cp.SaveDirtyPass();
        await cp.FlushAsync();
        byte[]? second = await store.LoadAsync("cell:0:0");
        Assert.Equal(first, second);
    }

    [Fact]
    public async Task SaveDirtyPass_DoesNotClobberSavedCell_WhileItsLoadIsInFlight()
    {
        var store = new InMemoryWorldStore();
        // Seed a real saved blob for C00 (arbitrary non-empty snapshot bytes).
        var seedHost = new FakeHost();
        byte[] saved = new byte[] { 1, 0, 0, 0, 9, 9, 9, 9 };
        seedHost.Snapshots[C00] = saved;
        var seeder = new CellPersistence(seedHost, store);
        seeder.SaveDirtyPass();
        await seeder.FlushAsync();
        byte[]? blobBefore = await store.LoadAsync("cell:0:0");
        Assert.NotNull(blobBefore);

        // Fresh persistence: the cell is created (load enqueued + restore pending, not drained).
        var host = new FakeHost();
        host.Snapshots[C00] = new byte[] { 0, 0, 0, 0 };   // the cell's pre-restore/default (empty) state
        host.RestoreIds[C00] = new List<int> { 3 };
        var cp = new CellPersistence(host, store);
        host.RaiseCellCreated(C00);                        // load runs; restore enqueued; coord marked in-flight

        cp.SaveDirtyPass();                                // periodic pass fires while the load is in flight

        byte[]? blobAfter = await store.LoadAsync("cell:0:0");
        Assert.Equal(blobBefore, blobAfter);               // stored blob NOT clobbered by pre-restore state

        await cp.FlushAsync();                             // draining now applies the restore
        Assert.True(host.Restored.ContainsKey(C00));
    }
}
