using System.Numerics;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Replication;
using KhaozEngine.Sharding;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// The authoritative servers must advance their <see cref="World"/>'s change-tracking tick once per server tick,
/// clearing the per-tick change/event sets. Without it those sets grow unbounded for the life of the process
/// (one entry per <c>Set</c>/<c>Despawn</c>), which OOMs a long-running server. <see cref="World.Tick"/> only
/// advances via <see cref="World.AdvanceTick"/>, so asserting it moved proves the clear ran.
/// </summary>
public class ServerWorldTickTests
{
    private static float Flat(float x, float z) => 0f;

    [Fact]
    public void WorldServer_AdvancesWorldTick_OncePerTick_ByDefault()
    {
        var (st, _) = LoopbackTransport.CreatePair();
        var cfg = new WorldServerConfig();
        var server = new WorldServer(st, cfg, Flat, MoveTuning.Default);

        Assert.Equal(0ul, server.World.Tick);
        server.Tick(cfg.TickSeconds);
        server.Tick(cfg.TickSeconds);
        server.Tick(cfg.TickSeconds);
        Assert.Equal(3ul, server.World.Tick);
    }

    [Fact]
    public void WorldServer_AdvanceWorldTickFalse_LeavesWorldTickUntouched()
    {
        var (st, _) = LoopbackTransport.CreatePair();
        var cfg = new WorldServerConfig { AdvanceWorldTick = false };
        var server = new WorldServer(st, cfg, Flat, MoveTuning.Default);

        server.Tick(cfg.TickSeconds);
        server.Tick(cfg.TickSeconds);
        Assert.Equal(0ul, server.World.Tick);
    }

    [Fact]
    public void ShardedWorldServer_AdvancesOwningCellWorldTick_ByDefault()
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
        var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default);
        var client = new NetClient(ct);

        int slot = -1;
        for (int i = 0; i < 200 && slot < 0; i++)
        {
            client.Poll();
            server.Poll();
            server.Tick(cfg.TickSeconds);
            if (client.Slot >= 0 && server.TryGetPlayerNetId(client.Slot, out _)) slot = client.Slot;
        }
        Assert.True(slot >= 0, "client never joined");
        Assert.True(server.TryGetPlayerNetId(slot, out int netId));
        Assert.True(server.Host.TryGetOwner(netId, out CellSim cell, out _));
        Assert.True(cell.World.Tick > 0, "owning cell's world tick never advanced");
    }
}
