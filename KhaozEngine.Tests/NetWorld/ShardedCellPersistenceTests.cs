using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
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

public class ShardedCellPersistenceTests
{
    private static float Flat(float x, float z) => 0f;

    private static ShardedWorldServerConfig Cfg() => new()
    {
        TickSeconds = 1f / 30f,
        CellSize = 10f,
        OverlapMargin = 4f,
        InterestRadius = 4f,
        MaxPlayers = 8,
        SpawnPosition = _ => new Vector3(5f, 0f, 5f),
    };

    [Fact]
    public void SnapshotCell_ExcludesPlayers()
    {
        var (st, ct) = LoopbackTransport.CreatePair();
        var cfg = Cfg();
        var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default);
        ICellPersistenceHost host = server;

        byte[] token = Encoding.UTF8.GetBytes("acct-1");
        var client = new NetClient(ct, token);
        for (int i = 0; i < 60; i++) { client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); }

        // The player spawns at world (5,_,5), which with CellSize 10 lands in cell (0,0).
        Assert.True(server.TryGetPlayerNetId(client.Slot, out long netId));
        Assert.True(server.Host.TryGetOwner(netId, out CellSim cell, out _));
        Assert.Equal(new CellCoord(0, 0), cell.Coord);

        // The snapshot must be empty (players persist separately, excluded from cell snapshots).
        byte[]? snap = host.SnapshotCell(new CellCoord(0, 0));
        Assert.NotNull(snap);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, snap);    // Replication snapshot with entity count 0
    }

    [Fact]
    public void EnsureNextNetIdAtLeast_RaisesButNeverLowers()
    {
        var (st, _) = LoopbackTransport.CreatePair();
        ICellPersistenceHost host = new ShardedWorldServer(st, Cfg(), Flat, MoveTuning.Default);
        long start = host.NextNetId;
        host.EnsureNextNetIdAtLeast(start + 10);
        Assert.Equal(start + 10, host.NextNetId);
        host.EnsureNextNetIdAtLeast(start);               // lower -> ignored
        Assert.Equal(start + 10, host.NextNetId);
    }

    // A minimal real-ShardHost host: no players, own NetId counter. Mirrors what a game server implements.
    private sealed class GridHost : ICellPersistenceHost
    {
        public readonly ShardHost Host;
        private long nextNetId = 1;
        public GridHost(ReplicationRegistry r) { Host = new ShardHost(10f, 1f / 30f, r); Host.CellCreated += c => CellCreated?.Invoke(c.Coord); }
        public event Action<CellCoord>? CellCreated;

        public IReadOnlyCollection<CellCoord> LiveCellCoords
        {
            get { var l = new List<CellCoord>(); foreach (CellSim c in Host.Cells) l.Add(c.Coord); return l; }
        }

        public byte[]? SnapshotCell(CellCoord coord) => Host.TryGetCell(coord, out CellSim cell) ? cell.SnapshotOwned(new HashSet<long>()) : null;
        public IReadOnlyList<long> RestoreCell(CellCoord coord, byte[] snapshot) => Host.TryGetCell(coord, out CellSim cell) ? cell.RestoreOwned(snapshot) : System.Array.Empty<long>();
        public void EnsureCell(CellCoord coord) => Host.EnsureCell(coord);
        public long NextNetId => nextNetId;
        public void EnsureNextNetIdAtLeast(long atLeast) { if (atLeast > nextNetId) nextNetId = atLeast; }
        public long SpawnNode(float x, float y, int amount)
        {
            long id = nextNetId++;
            Entity e = Host.SpawnAt(x, y, out CellSim cell);
            cell.World.Set(e, new NetId(id));
            cell.World.Set(e, new ResourceNodeC { Amount = amount });
            return id;
        }
    }

    private struct ResourceNodeC : IComponent { public int Amount; }

    private static ReplicationRegistry NodeRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<ResourceNodeC>(typeId: 1, write: (n, bw) => bw.Write(n.Amount), read: br => new ResourceNodeC { Amount = br.ReadInt32() });
        return r;
    }

    [Fact]
    public async Task NonPlayerEntity_SurvivesHostRebuild_WithNetIdAndNoCollision()
    {
        var store = new InMemoryWorldStore();
        ReplicationRegistry r = NodeRegistry();

        // First run: spawn a node at (25,25) -> cell (2,2), persist, shut down.
        var g1 = new GridHost(r);
        long nodeId = g1.SpawnNode(25f, 25f, 77);
        var cp1 = new CellPersistence(g1, store);
        cp1.SaveDirtyPass();
        await cp1.FlushAsync();

        // Second run: fresh host + store. Preload instantiates cell (2,2) -> restore.
        var g2 = new GridHost(r);
        var cp2 = new CellPersistence(g2, store);
        await cp2.LoadMetaAsync();
        await cp2.PreloadAsync();
        await cp2.FlushAsync();

        Assert.True(g2.Host.TryGetCell(new CellCoord(2, 2), out CellSim cell));
        Assert.True(cell.TryGetOwned(nodeId, out Entity e));
        Assert.True(cell.World.TryGet(e, out ResourceNodeC n));
        Assert.Equal(77, n.Amount);
        Assert.True(g2.NextNetId > nodeId);              // allocator resumed above the restored id
    }
}
