using System.Collections.Generic;
using System.Numerics;
using System.Text;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Sharding;
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
        Assert.True(server.TryGetPlayerNetId(client.Slot, out int netId));
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
        int start = host.NextNetId;
        host.EnsureNextNetIdAtLeast(start + 10);
        Assert.Equal(start + 10, host.NextNetId);
        host.EnsureNextNetIdAtLeast(start);               // lower -> ignored
        Assert.Equal(start + 10, host.NextNetId);
    }
}
