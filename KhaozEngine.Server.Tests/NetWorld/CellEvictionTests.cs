using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The persist-then-evict path: <see cref="CellEvictor"/> snapshots a cell, waits for the store write to land, and
/// only then removes it from the <see cref="ShardHost"/>. These tests pin the guarantees that make eviction safe -
/// nothing is removed before its bytes are durable, a recreated coord restores before it can tick (including the
/// handoff-into-an-evicted-coord case), the ownership index carries no stale entries, an in-flight load or save
/// defers the eviction, and dirty tracking stays honest across an evict and a recreate.
/// </summary>
public class CellEvictionTests
{
    private struct Node : IComponent { public int Amount; }
    private struct Pos : IComponent { public float X; public float Y; }

    private static ReplicationRegistry Registry()
    {
        var r = new ReplicationRegistry();
        r.Register<Node>(1, (n, bw) => bw.Write(n.Amount), br => new Node { Amount = br.ReadInt32() });
        r.Register<Pos>(2,
            (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
            br => new Pos { X = br.ReadSingle(), Y = br.ReadSingle() });
        return r;
    }

    private static bool PosAccessor(World world, Entity e, out float x, out float y)
    {
        if (world.TryGet(e, out Pos p)) { x = p.X; y = p.Y; return true; }
        x = y = 0f;
        return false;
    }

    /// <summary>A minimal real-ShardHost eviction host: the same shape a game server implements, with no transport.</summary>
    private sealed class GridHost : ICellEvictionHost
    {
        public readonly ShardHost Host;
        private readonly List<CellCoord> playerCells = new();
        private long nextNetId = 1;

        public GridHost()
        {
            Host = new ShardHost(cellSize: 100f, tickSeconds: 0.1f, Registry(), interestCellSize: 100f,
                overlapMargin: 20f, positionAccessor: PosAccessor);
            Host.CellCreated += c => CellCreated?.Invoke(c.Coord);
        }

        public event Action<CellCoord>? CellCreated;

        public IReadOnlyCollection<CellCoord> LiveCellCoords
        {
            get { var l = new List<CellCoord>(); foreach (CellSim c in Host.Cells) l.Add(c.Coord); return l; }
        }

        public byte[]? SnapshotCell(CellCoord coord) =>
            Host.TryGetCell(coord, out CellSim cell) ? cell.SnapshotOwned(new HashSet<long>()) : null;

        public IReadOnlyList<long> RestoreCell(CellCoord coord, byte[] snapshot) =>
            Host.TryGetCell(coord, out CellSim cell) ? cell.RestoreOwned(snapshot) : Array.Empty<long>();

        public CellRestoreResult TryRestoreCell(CellCoord coord, byte[] snapshot) =>
            Host.TryGetCell(coord, out CellSim cell)
                ? cell.TryRestoreOwned(snapshot)
                : new CellRestoreResult(true, Array.Empty<long>(), 0, null);

        public void EnsureCell(CellCoord coord) => Host.EnsureCell(coord);
        public long NextNetId => nextNetId;
        public void EnsureNextNetIdAtLeast(long atLeast) { if (atLeast > nextNetId) nextNetId = atLeast; }

        public bool CanEvictCell(CellCoord coord) => Host.CanRemoveCell(coord);
        public bool EvictCell(CellCoord coord) => Host.RemoveCell(coord);

        public bool TryReadEvictionSignals(CellCoord coord, out CellEvictionSignals signals)
        {
            signals = default;
            if (!Host.TryGetCell(coord, out CellSim cell)) return false;
            playerCells.Clear();
            Host.CollectBoundPlayerCells(playerCells);
            int here = 0, nearest = int.MaxValue;
            foreach (CellCoord p in playerCells)
            {
                if (p == coord) here++;
                int d = Math.Max(Math.Abs(p.X - coord.X), Math.Abs(p.Y - coord.Y));
                if (d < nearest) nearest = d;
            }
            signals = new CellEvictionSignals(coord, cell.OwnedCount, here, nearest, pinned: false);
            return true;
        }

        public long SpawnNode(float x, float y, int amount)
        {
            long id = nextNetId++;
            Entity e = Host.SpawnOwned(x, y, id, out CellSim cell);
            cell.World.Set(e, new Pos { X = x, Y = y });
            cell.World.Set(e, new Node { Amount = amount });
            return id;
        }
    }

    /// <summary>A store whose saves park until the test releases them. Loads pass straight through.</summary>
    private sealed class GatedSaveStore : IWorldStore
    {
        private readonly IWorldStore inner;
        private readonly List<TaskCompletionSource> gates = new();
        public GatedSaveStore(IWorldStore inner) => this.inner = inner;

        public int PendingSaves { get { lock (gates) return gates.Count; } }

        public void ReleaseSaves()
        {
            TaskCompletionSource[] open;
            lock (gates) { open = gates.ToArray(); gates.Clear(); }
            foreach (TaskCompletionSource g in open) g.SetResult();
        }

        public Task<byte[]?> LoadAsync(string key, CancellationToken ct = default) => inner.LoadAsync(key, ct);

        public async Task SaveAsync(string key, byte[] data, CancellationToken ct = default)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gates) gates.Add(gate);
            await gate.Task.ConfigureAwait(false);
            await inner.SaveAsync(key, data, ct).ConfigureAwait(false);
        }

        public Task<bool> DeleteAsync(string key, CancellationToken ct = default) => inner.DeleteAsync(key, ct);
        public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => inner.ExistsAsync(key, ct);
    }

    /// <summary>A store that faults every save while <see cref="FailSaves"/> is set.</summary>
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

    private static readonly CellCoord C22 = new(2, 2);

    // Requests an eviction and drives it to completion: the save lands, then the server-thread pass removes the cell.
    private static async Task EvictAsync(CellEvictor evictor, CellPersistence persistence, CellCoord coord)
    {
        Assert.True(evictor.RequestEvict(coord));
        await persistence.FlushAsync();
        evictor.Update(0f);
    }

    [Fact]
    public async Task EvictThenRecreate_RoundTripsOwnedEntities()
    {
        var store = new InMemoryWorldStore();
        var host = new GridHost();
        var persistence = new CellPersistence(host, store);
        var evictor = new CellEvictor(host, persistence);

        long nodeId = host.SpawnNode(250f, 250f, amount: 77);
        Assert.True(host.Host.TryGetCell(C22, out _));

        await EvictAsync(evictor, persistence, C22);

        Assert.False(host.Host.TryGetCell(C22, out _));      // the cell is gone
        Assert.Equal(1, evictor.EvictedCount);
        Assert.NotNull(await store.LoadAsync("cell:2:2"));   // and its state is durable

        // Recreating the coord restores the persisted entity synchronously, inside the create call.
        CellSim again = host.Host.EnsureCell(C22);
        Assert.True(again.TryGetOwned(nodeId, out Entity e));
        Assert.True(again.World.TryGet(e, out Node n));
        Assert.Equal(77, n.Amount);
        Assert.True(again.World.TryGet(e, out Pos p));
        Assert.Equal(250f, p.X);
        Assert.Equal(250f, p.Y);
        Assert.True(host.Host.TryGetOwner(nodeId, out CellSim owner, out _));
        Assert.Equal(C22, owner.Coord);
    }

    [Fact]
    public async Task HandoffIntoAnEvictedCoord_RestoresBeforeTheFirstTick()
    {
        var store = new InMemoryWorldStore();
        var host = new GridHost();
        var persistence = new CellPersistence(host, store);
        var evictor = new CellEvictor(host, persistence);

        long resident = host.SpawnNode(250f, 250f, amount: 5);   // lives in cell (2,2)
        long walker = host.SpawnNode(150f, 250f, amount: 9);     // lives in cell (1,2)

        await EvictAsync(evictor, persistence, C22);
        Assert.False(host.Host.TryGetCell(C22, out _));

        // The walker crosses into the evicted coord. ProcessHandoffs recreates the destination cell, which must
        // already hold the persisted resident by the time the migrated entity is adopted - never a blank cell.
        Assert.True(host.Host.TryGetOwner(walker, out CellSim from, out Entity we));
        from.World.Set(we, new Pos { X = 250f, Y = 250f });
        host.Host.ProcessHandoffs();

        Assert.True(host.Host.TryGetCell(C22, out CellSim dest));
        Assert.True(dest.TryGetOwned(resident, out Entity re));   // restored, before anything ticked
        Assert.True(dest.World.TryGet(re, out Node rn));
        Assert.Equal(5, rn.Amount);
        Assert.True(dest.TryGetOwned(walker, out _));             // and the migrant landed
        Assert.Equal(0, dest.TickCount);
        Assert.Equal(1, host.Host.OwnerCount(resident));
        Assert.Equal(1, host.Host.OwnerCount(walker));
    }

    [Fact]
    public async Task Evict_LeavesNoStaleOwnerCellEntriesForTheEvictedCellsEntities()
    {
        var store = new InMemoryWorldStore();
        var host = new GridHost();
        var persistence = new CellPersistence(host, store);
        var evictor = new CellEvictor(host, persistence);

        long a = host.SpawnNode(250f, 250f, 1);
        long b = host.SpawnNode(260f, 260f, 2);
        long elsewhere = host.SpawnNode(50f, 50f, 3);

        await EvictAsync(evictor, persistence, C22);

        Assert.False(host.Host.OwnerCellEntries.ContainsKey(a));
        Assert.False(host.Host.OwnerCellEntries.ContainsKey(b));
        foreach (KeyValuePair<long, CellCoord> kv in host.Host.OwnerCellEntries)
            Assert.NotEqual(C22, kv.Value);
        Assert.True(host.Host.OwnerCellEntries.ContainsKey(elsewhere));
        Assert.Equal(0, host.Host.OwnerCount(a));
        Assert.Equal(1, host.Host.OwnerCount(elsewhere));
    }

    [Fact]
    public async Task Evict_IsRefusedWhileTheCellsRestoreIsInFlight()
    {
        var inner = new InMemoryWorldStore();

        // First run: persist a cell, then throw the host away.
        var seed = new GridHost();
        long nodeId = seed.SpawnNode(250f, 250f, 42);
        var seedPersistence = new CellPersistence(seed, inner);
        seedPersistence.SaveDirtyPass();
        await seedPersistence.FlushAsync();

        // Second run over a gated store: creating the cell parks its load.
        var gated = new GatedWorldStore(inner);
        var host = new GridHost();
        var persistence = new CellPersistence(host, gated);
        var evictor = new CellEvictor(host, persistence);
        host.Host.EnsureCell(C22);
        Assert.Equal(1, gated.PendingLoads);

        Assert.True(persistence.IsBusy(C22));
        Assert.False(evictor.RequestEvict(C22));      // never snapshot a cell mid-restore
        Assert.True(host.Host.TryGetCell(C22, out _));

        gated.ReleaseLoads();
        await persistence.FlushAsync();
        Assert.False(persistence.IsBusy(C22));
        Assert.True(host.Host.TryGetCell(C22, out CellSim restored));
        Assert.True(restored.TryGetOwned(nodeId, out _));

        Assert.True(evictor.RequestEvict(C22));       // now it is evictable
    }

    [Fact]
    public async Task Evict_IsRefusedWhileASaveForThatCoordIsInFlight()
    {
        var gated = new GatedSaveStore(new InMemoryWorldStore());
        var host = new GridHost();
        var persistence = new CellPersistence(host, gated);
        var evictor = new CellEvictor(host, persistence);
        host.SpawnNode(250f, 250f, 11);

        persistence.SaveDirtyPass();                  // periodic dirty save parks in the store (cell blob + meta)
        Assert.True(gated.PendingSaves > 0);
        Assert.True(persistence.IsBusy(C22));
        Assert.False(evictor.RequestEvict(C22));

        gated.ReleaseSaves();
        await persistence.FlushAsync();
        gated.ReleaseSaves();                          // the flush's own trailing pass, if any
        await persistence.FlushAsync();
        Assert.False(persistence.IsBusy(C22));

        // An eviction already in flight is not requested twice either.
        Assert.True(evictor.RequestEvict(C22));
        Assert.False(evictor.RequestEvict(C22));
        Assert.True(host.Host.TryGetCell(C22, out _));   // still live: the save has not landed yet
        gated.ReleaseSaves();
        await persistence.FlushAsync();
        evictor.Update(0f);
        Assert.False(host.Host.TryGetCell(C22, out _));
    }

    [Fact]
    public async Task Evict_LeavesTheCellInPlaceWhenTheSaveFails()
    {
        var store = new ToggleFaultStore(new InMemoryWorldStore()) { FailSaves = true };
        var host = new GridHost();
        var persistence = new CellPersistence(host, store);
        var evictor = new CellEvictor(host, persistence);
        long nodeId = host.SpawnNode(250f, 250f, 3);
        var errors = new List<Exception>();
        persistence.OnStoreError += errors.Add;

        Assert.True(evictor.RequestEvict(C22));
        await persistence.FlushAsync();
        evictor.Update(0f);

        Assert.True(host.Host.TryGetCell(C22, out CellSim still));   // the cell stays: nothing was persisted
        Assert.True(still.TryGetOwned(nodeId, out _));
        Assert.Equal(0, evictor.EvictedCount);
        Assert.NotEmpty(errors);

        store.FailSaves = false;
        Assert.True(evictor.RequestEvict(C22));                      // retried once the store recovers
        await persistence.FlushAsync();
        evictor.Update(0f);
        Assert.False(host.Host.TryGetCell(C22, out _));
    }

    [Fact]
    public async Task Evict_SavesBytesMatchingAFreshSnapshot_AndDirtyTrackingSurvivesRecreate()
    {
        var store = new InMemoryWorldStore();
        var host = new GridHost();
        var persistence = new CellPersistence(host, store);
        var evictor = new CellEvictor(host, persistence);

        long nodeId = host.SpawnNode(250f, 250f, 13);
        byte[] expected = host.SnapshotCell(C22)!;

        await EvictAsync(evictor, persistence, C22);
        Assert.True(persistence.TryGetLastSaved(C22, out byte[] saved));
        Assert.Equal(expected, saved);                       // the persisted bytes are the cell's own snapshot

        // Recreate: the restored cell is byte-identical to what was saved, so it is NOT dirty and the next pass
        // writes nothing new.
        host.Host.EnsureCell(C22);
        Assert.Equal(expected, host.SnapshotCell(C22));
        byte[]? blobBefore = await store.LoadAsync("cell:2:2");
        persistence.SaveDirtyPass();
        await persistence.FlushAsync();
        Assert.Equal(blobBefore, await store.LoadAsync("cell:2:2"));

        // Modify it and the very next pass persists the change.
        Assert.True(host.Host.TryGetOwner(nodeId, out CellSim cell, out Entity e));
        cell.World.Set(e, new Node { Amount = 99 });
        persistence.SaveDirtyPass();
        await persistence.FlushAsync();
        byte[]? blobAfter = await store.LoadAsync("cell:2:2");
        Assert.NotEqual(blobBefore, blobAfter);
    }

    [Fact]
    public async Task EvictedCell_WithTheSnapshotCacheOff_RestoresFromTheStoreOnRecreate()
    {
        var store = new InMemoryWorldStore();
        var host = new GridHost();
        var persistence = new CellPersistence(host, store);
        var evictor = new CellEvictor(host, persistence, new CellEvictionConfig { MaxCachedSnapshots = 0 });

        long nodeId = host.SpawnNode(250f, 250f, 21);
        await EvictAsync(evictor, persistence, C22);
        Assert.Equal(0, evictor.CachedSnapshotCount);

        // No armed cache, so recreation falls back to the driver's normal async load path.
        host.Host.EnsureCell(C22);
        Assert.True(host.Host.TryGetCell(C22, out CellSim fresh));
        Assert.False(fresh.TryGetOwned(nodeId, out _));      // not restored yet: the load is in flight
        await persistence.FlushAsync();
        Assert.True(fresh.TryGetOwned(nodeId, out Entity e));
        Assert.True(fresh.World.TryGet(e, out Node n));
        Assert.Equal(21, n.Amount);
    }

    [Fact]
    public async Task DefaultPolicyScan_EvictsAnIdleCell_AndKeepsOneWithAPlayerInIt()
    {
        var store = new InMemoryWorldStore();
        var host = new GridHost();
        var persistence = new CellPersistence(host, store);
        var evictor = new CellEvictor(host, persistence, new CellEvictionConfig
        {
            ScanIntervalSeconds = 1f,
            Policy = new IdleCellEvictionPolicy { IdleSeconds = 10f, KeepRadius = 1 },
        });

        long playerId = host.SpawnNode(50f, 50f, 1);        // cell (0,0), stands in for a player entity
        host.Host.BindClient(slot: 0, playerNetId: playerId);
        host.SpawnNode(950f, 950f, 2);                       // cell (9,9), far away and unattended
        var far = new CellCoord(9, 9);

        // Below the idle threshold nothing moves.
        evictor.Update(5f);
        await persistence.FlushAsync();
        evictor.Update(0f);
        Assert.True(host.Host.TryGetCell(far, out _));

        // Past it, the far cell goes and the occupied one stays.
        evictor.Update(10f);
        await persistence.FlushAsync();
        evictor.Update(0f);
        Assert.False(host.Host.TryGetCell(far, out _));
        Assert.True(host.Host.TryGetCell(new CellCoord(0, 0), out _));
    }

    [Fact]
    public void ShardedWorldServer_PinsTheCellHoldingAJoinedPlayer()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var cfg = new ShardedWorldServerConfig
        {
            TickSeconds = 1f / 30f,
            CellSize = 10f,
            OverlapMargin = 4f,
            InterestRadius = 4f,
            MaxPlayers = 8,
            SpawnPosition = _ => new Vector3(5f, 0f, 5f),
        };
        var server = new ShardedWorldServer(st, cfg, (x, z) => 0f, MoveTuning.Default);
        ICellEvictionHost host = server;

        byte[] token = Encoding.UTF8.GetBytes("acct-1");
        var client = new NetClient(ct, TestHandshake.Wire(token));
        for (int i = 0; i < 60; i++) { client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); }

        var occupied = new CellCoord(0, 0);
        Assert.True(host.TryReadEvictionSignals(occupied, out CellEvictionSignals signals));
        Assert.Equal(1, signals.BoundPlayerCount);
        Assert.True(signals.Pinned);
        Assert.False(host.CanEvictCell(occupied));
        Assert.False(host.EvictCell(occupied));
        Assert.True(server.Host.TryGetCell(occupied, out _));
    }
}
