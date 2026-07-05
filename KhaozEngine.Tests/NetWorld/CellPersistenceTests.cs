using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
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
        public long NextNetId { get; private set; } = 1;
        public event Action<CellCoord>? CellCreated;

        public IReadOnlyCollection<CellCoord> LiveCellCoords => new List<CellCoord>(Snapshots.Keys);
        public byte[]? SnapshotCell(CellCoord coord) => Snapshots.TryGetValue(coord, out byte[]? b) ? b : null;
        public IReadOnlyList<long> RestoreCell(CellCoord coord, byte[] snapshot)
        {
            Restored[coord] = snapshot;
            return RestoreIds.TryGetValue(coord, out List<long>? ids) ? ids : new List<long>();
        }
        public readonly Dictionary<CellCoord, List<long>> RestoreIds = new();
        public void EnsureCell(CellCoord coord) => CellCreated?.Invoke(coord);
        public void EnsureNextNetIdAtLeast(long atLeast) { if (atLeast > NextNetId) NextNetId = atLeast; }
        public void RaiseCellCreated(CellCoord coord) => CellCreated?.Invoke(coord);
        public void SetNextNetId(long v) => NextNetId = v;
    }

    // A store whose saves always fault (a store outage); loads pass through to an inner store (default: empty).
    private sealed class FaultingCellStore : IWorldStore
    {
        private readonly IWorldStore inner;
        public FaultingCellStore(IWorldStore? inner = null) => this.inner = inner ?? new InMemoryWorldStore();
        public Task<byte[]?> LoadAsync(string key, CancellationToken ct = default) => inner.LoadAsync(key, ct);
        public Task SaveAsync(string key, byte[] data, CancellationToken ct = default) => Task.FromException(new System.IO.IOException("store offline"));
        public Task<bool> DeleteAsync(string key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);
        public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => inner.ExistsAsync(key, ct);
    }

    // A store that faults every SaveAsync while FailSaves is set, and otherwise passes through to a real inner store.
    private sealed class ToggleFaultStore : IWorldStore
    {
        private readonly IWorldStore inner;
        public bool FailSaves;
        public ToggleFaultStore(IWorldStore inner) => this.inner = inner;
        public Task<byte[]?> LoadAsync(string key, CancellationToken ct = default) => inner.LoadAsync(key, ct);
        public Task SaveAsync(string key, byte[] data, CancellationToken ct = default) =>
            FailSaves ? Task.FromException(new System.IO.IOException("store offline")) : inner.SaveAsync(key, data, ct);
        public Task<bool> DeleteAsync(string key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);
        public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => inner.ExistsAsync(key, ct);
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
        host.RestoreIds[C00] = new List<long> { 7 };
        var seeder = new CellPersistence(host, store);
        seeder.SaveDirtyPass();                            // writes cell:0:0 (wrapped) to the store
        await seeder.FlushAsync();

        // Fresh persistence over the same store: creating the cell enqueues a load, applied only on Update.
        var host2 = new FakeHost();
        host2.RestoreIds[C00] = new List<long> { 7 };
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
        host.RestoreIds[C00] = new List<long> { 3 };
        var cp = new CellPersistence(host, store);
        host.RaiseCellCreated(C00);                        // load runs; restore enqueued; coord marked in-flight

        cp.SaveDirtyPass();                                // periodic pass fires while the load is in flight

        byte[]? blobAfter = await store.LoadAsync("cell:0:0");
        Assert.Equal(blobBefore, blobAfter);               // stored blob NOT clobbered by pre-restore state

        await cp.FlushAsync();                             // draining now applies the restore
        Assert.True(host.Restored.ContainsKey(C00));
    }

    [Fact]
    public async Task Load_SkipsBlobWithWrongSchemaVersion()
    {
        var store = new InMemoryWorldStore();
        var hostV1 = new FakeHost();
        hostV1.Snapshots[C00] = new byte[] { 0, 0, 0, 0 };
        var v1 = new CellPersistence(hostV1, store, new CellPersistenceConfig { SchemaVersion = 1 });
        v1.SaveDirtyPass();
        await v1.FlushAsync();

        // A reader on schema 2 with no bridging migration must treat the v1 blob as unusable: no restore enqueued.
        // (Opt out of engine migrations, else the built-in v1->v2 widening would bring it forward instead of skipping.)
        var hostV2 = new FakeHost();
        var v2 = new CellPersistence(hostV2, store, new CellPersistenceConfig { SchemaVersion = 2, IncludeEngineMigrations = false });
        hostV2.RaiseCellCreated(C00);
        await v2.FlushAsync();
        Assert.False(hostV2.Restored.ContainsKey(C00));   // skipped, not mis-decoded
    }

    [Fact]
    public async Task LoadMetaAsync_ResumesAllocatorAboveHighWater()
    {
        var store = new InMemoryWorldStore();
        await store.SaveAsync("world:meta", new WorldMetaRecord { NextNetId = 500 }.Encode());
        var host = new FakeHost();                         // starts NextNetId = 1
        var cp = new CellPersistence(host, store);
        await cp.LoadMetaAsync();
        Assert.Equal(500, host.NextNetId);
    }

    [Fact]
    public async Task PreloadAsync_InstantiatesEverySavedCell()
    {
        var store = new InMemoryWorldStore();
        var seedHost = new FakeHost();
        seedHost.Snapshots[new CellCoord(1, 2)] = new byte[] { 0, 0, 0, 0 };
        seedHost.Snapshots[new CellCoord(-3, 4)] = new byte[] { 0, 0, 0, 0 };
        var seeder = new CellPersistence(seedHost, store);
        seeder.SaveDirtyPass();
        await seeder.FlushAsync();

        var host = new FakeHost();
        var created = new List<CellCoord>();
        host.CellCreated += created.Add;
        var cp = new CellPersistence(host, store);
        await cp.PreloadAsync();
        Assert.Contains(new CellCoord(1, 2), created);
        Assert.Contains(new CellCoord(-3, 4), created);
    }

    // --- Pending-task hygiene on a store outage (ported from WorldPersistence 9.32.1) ---

    [Fact]
    public async Task StoreSaveFault_IsSurfacedViaEvent_AndFlushDoesNotThrow()
    {
        var store = new FaultingCellStore();
        var host = new FakeHost();
        host.Snapshots[C00] = new byte[] { 1, 0, 0, 0, 5, 5, 5, 5 };
        var cp = new CellPersistence(host, store);
        var errors = new List<Exception>();
        cp.OnStoreError += errors.Add;

        cp.SaveDirtyPass();                                  // queues a faulting cell save (+ meta save)
        await cp.FlushAsync();                               // must NOT rethrow: the boot sequence's Task.WhenAll used to surface the fault here

        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e is System.IO.IOException);
    }

    [Fact]
    public void StoreSaveFault_UpdatePrunesAndSurfaces_OncePerPass_PendingDoesNotPileUp()
    {
        var store = new FaultingCellStore();
        var host = new FakeHost();
        host.SetNextNetId(0);                                // isolate: no meta save, so only the cell save faults per pass
        host.Snapshots[C00] = new byte[] { 1, 0, 0, 0, 5, 5, 5, 5 };
        var cp = new CellPersistence(host, store);
        int errorCount = 0;
        cp.OnStoreError += _ => errorCount++;

        for (int i = 0; i < 5; i++) { cp.SaveDirtyPass(); cp.Update(0f); }

        // Each pass's faulted save is pruned + surfaced exactly once (5 total). The old Update pruned only
        // RanToCompletion, so faulted tasks piled up in pending forever and were never observed.
        Assert.Equal(5, errorCount);
    }

    [Fact]
    public async Task FaultedCellSave_StaysDirty_AndReSavesOnceStoreRecovers()
    {
        var inner = new InMemoryWorldStore();
        var store = new ToggleFaultStore(inner) { FailSaves = true };
        var host = new FakeHost();
        host.SetNextNetId(0);
        host.Snapshots[C00] = new byte[] { 1, 0, 0, 0, 7, 7, 7, 7 };
        var cp = new CellPersistence(host, store);
        var errors = new List<Exception>();
        cp.OnStoreError += errors.Add;

        cp.SaveDirtyPass();
        await cp.FlushAsync();                               // save faults, surfaced, nothing persisted, no throw
        Assert.NotEmpty(errors);
        Assert.Null(await inner.LoadAsync("cell:0:0"));      // the faulted save did not land

        store.FailSaves = false;                             // store recovers
        cp.SaveDirtyPass();                                  // cell is still dirty -> re-queued (baseline wasn't advanced on the fault)
        await cp.FlushAsync();
        Assert.NotNull(await inner.LoadAsync("cell:0:0"));   // now persisted
    }

    [Fact]
    public async Task QuarantineWriteFault_DoesNotBreakLoad_AndIsSurfaced()
    {
        var inner = new InMemoryWorldStore();
        await inner.SaveAsync("cell:0:0", new byte[] { 9, 9, 9, 9, 1, 2, 3, 4 });   // corrupt: bad magic -> quarantined on load
        var store = new ToggleFaultStore(inner) { FailSaves = true };               // the quarantine write itself will fault
        var host = new FakeHost();
        host.SetNextNetId(0);                                                        // isolate: no meta save
        var cp = new CellPersistence(host, store);
        var errors = new List<Exception>();
        var issues = new List<CellPersistenceIssue>();
        cp.OnStoreError += errors.Add;
        cp.Issue += issues.Add;

        host.RaiseCellCreated(C00);
        await cp.FlushAsync();                                                       // load -> corrupt -> quarantine write faults; must not throw

        Assert.False(host.Restored.ContainsKey(C00));                               // cell left fresh, not mis-restored
        Assert.NotEmpty(issues);                                                     // quarantine issue surfaced
        Assert.Contains(errors, e => e is System.IO.IOException);                    // faulted quarantine write surfaced, not swallowed
    }
}
