using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.NetWorld;
using KhaozEngine.Sharding;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class CellRestoreAppliedTests
{
    private static readonly CellCoord Cell = new(2, 3);

    private sealed class Host : ICellPersistenceHost
    {
        public readonly Dictionary<CellCoord, byte[]> Snapshots = new();
        public readonly Dictionary<CellCoord, byte[]> Restored = new();
        public readonly Dictionary<CellCoord, IReadOnlyList<long>> RestoreIds = new();
        public bool RejectRestore;

        public event Action<CellCoord>? CellCreated;
        public IReadOnlyCollection<CellCoord> LiveCellCoords => Snapshots.Keys;
        public long NextNetId { get; private set; } = 1;

        public byte[]? SnapshotCell(CellCoord coord) =>
            Snapshots.TryGetValue(coord, out byte[]? snapshot) ? snapshot : null;

        public IReadOnlyList<long> RestoreCell(CellCoord coord, byte[] snapshot)
        {
            Restored[coord] = snapshot;
            return RestoreIds.TryGetValue(coord, out IReadOnlyList<long>? ids) ? ids : Array.Empty<long>();
        }

        public CellRestoreResult TryRestoreCell(CellCoord coord, byte[] snapshot)
        {
            if (RejectRestore) return CellRestoreResult.Failed("rejected");
            IReadOnlyList<long> ids = RestoreCell(coord, snapshot);
            return new CellRestoreResult(true, ids, 0, null);
        }

        public void EnsureCell(CellCoord coord) => CellCreated?.Invoke(coord);
        public void EnsureNextNetIdAtLeast(long atLeast)
        {
            if (atLeast > NextNetId) NextNetId = atLeast;
        }

        public void RaiseCellCreated(CellCoord coord) => CellCreated?.Invoke(coord);
    }

    [Fact]
    public async Task Delayed_restore_signals_after_apply_once_for_each_recreated_cell()
    {
        var inner = new InMemoryWorldStore();
        await SeedAsync(inner);
        var gated = new GatedWorldStore(inner);
        var host = new Host();
        host.RestoreIds[Cell] = new long[] { 41, 42 };
        var persistence = new CellPersistence(host, gated);
        var applied = new List<CellRestoreAppliedEvent>();
        int callbackThread = -1;
        persistence.CellRestoreApplied += value =>
        {
            Assert.True(host.Restored.ContainsKey(value.Coord));
            Assert.True(host.NextNetId >= 43);
            callbackThread = Environment.CurrentManagedThreadId;
            applied.Add(value);
        };

        host.RaiseCellCreated(Cell);
        Assert.Equal(1, gated.PendingLoads);
        persistence.Update(0f);
        Assert.Empty(applied);

        gated.ReleaseLoads();
        await WaitForLoadAsync(gated);
        int updateThread = await DrainUntilAsync(persistence, () => applied.Count == 1);

        Assert.Equal(updateThread, callbackThread);
        Assert.Equal(Cell, applied[0].Coord);
        Assert.Equal(new long[] { 41, 42 }, applied[0].NetIds);
        persistence.Update(0f);
        Assert.Single(applied);

        persistence.ForgetCell(Cell);
        host.Restored.Remove(Cell);
        host.RaiseCellCreated(Cell);
        Assert.Equal(1, gated.PendingLoads);
        gated.ReleaseLoads();
        await WaitForLoadAsync(gated);
        updateThread = await DrainUntilAsync(persistence, () => applied.Count == 2);

        Assert.Equal(updateThread, callbackThread);
        Assert.Equal(Cell, applied[1].Coord);
        Assert.Equal(new long[] { 41, 42 }, applied[1].NetIds);
        persistence.Update(0f);
        Assert.Equal(2, applied.Count);
    }

    [Fact]
    public async Task Missing_rejected_and_too_new_loads_do_not_signal_success()
    {
        var missingStore = new InMemoryWorldStore();
        var missingHost = new Host();
        var missing = new CellPersistence(missingHost, missingStore);
        int missingSignals = 0;
        missing.CellRestoreApplied += _ => missingSignals++;
        missingHost.RaiseCellCreated(Cell);
        await missing.FlushAsync();
        Assert.Equal(0, missingSignals);

        var inner = new InMemoryWorldStore();
        await SeedAsync(inner);
        var rejectedHost = new Host { RejectRestore = true };
        var rejected = new CellPersistence(rejectedHost, inner);
        int rejectedSignals = 0;
        rejected.CellRestoreApplied += _ => rejectedSignals++;
        rejectedHost.RaiseCellCreated(Cell);
        await rejected.FlushAsync();
        Assert.Equal(0, rejectedSignals);

        var staleHost = new Host();
        var stale = new CellPersistence(staleHost, inner, new CellPersistenceConfig
        {
            SchemaVersion = 3,
            IncludeEngineMigrations = false,
        });
        int staleSignals = 0;
        stale.CellRestoreApplied += _ => staleSignals++;
        staleHost.RaiseCellCreated(Cell);
        await stale.FlushAsync();
        Assert.Equal(0, staleSignals);
    }

    private static async Task SeedAsync(IWorldStore store)
    {
        var host = new Host();
        host.Snapshots[Cell] = new byte[] { 0, 0, 0, 0 };
        var persistence = new CellPersistence(host, store);
        persistence.SaveDirtyPass();
        await persistence.FlushAsync();
    }

    private static async Task WaitForLoadAsync(GatedWorldStore store)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await store.WaitForCompletedLoadAsync(timeout.Token);
    }

    private static async Task<int> DrainUntilAsync(CellPersistence persistence, Func<bool> complete)
    {
        var timeout = Stopwatch.StartNew();
        int thread = Environment.CurrentManagedThreadId;
        while (!complete() && timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            thread = Environment.CurrentManagedThreadId;
            persistence.Update(0f);
            if (!complete()) await Task.Delay(1);
        }
        Assert.True(complete());
        return thread;
    }
}
