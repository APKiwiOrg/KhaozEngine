using System;
using System.Numerics;
using System.Text;
using KhaozEngine.Ecs;
using KhaozEngine.Locomotion;
using KhaozEngine.Netcode;
using KhaozEngine.NetWorld;
using KhaozEngine.Sharding;
using KhaozEngine.WorldStore;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class ShardedWorldPersistenceTests
{
    private static float Flat(float x, float z) => 0f;

    private static ShardedWorldServerConfig Cfg(Func<int, Vector3>? spawn = null) => new()
    {
        TickSeconds = 1f / 30f,
        CellSize = 10f,
        OverlapMargin = 4f,
        InterestRadius = 4f,
        MaxPlayers = 8,
        SpawnPosition = spawn,
    };

    [Fact]
    public void LoadOnJoin_SpawnsAtSavedPosition_InTheContainingCell()
    {
        var store = new InMemoryWorldStore();
        byte[] token = Encoding.UTF8.GetBytes("acct-1");

        // Pre-seed a save at x=35 (cell 3), z=5 - a different cell from the default spawn at x=5 (cell 0).
        var saved = new PlayerMoveState { Position = new Vector3(35f, MoveTuning.Default.CapsuleHalfHeight, 5f) };
        store.SaveAsync("player:acct-1", PlayerRecord.From(saved).Encode()).GetAwaiter().GetResult();

        var (st, ct) = LoopbackTransport.CreatePair();
        var cfg = Cfg(_ => new Vector3(5f, 0f, 5f));          // default spawn cell (0,0)
        var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default);
        var persistence = new WorldPersistence(server, store, new WorldPersistenceConfig { SaveIntervalSeconds = 999f });
        var client = new NetClient(ct, token);

        // Join, then drive enough frames for the async load to apply AND the handoff to relocate the entity.
        for (int i = 0; i < 200; i++) { client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); persistence.Update(cfg.TickSeconds); }

        Assert.True(server.TryGetPlayerNetId(client.Slot, out int netId));
        Assert.True(server.Host.TryGetOwner(netId, out CellSim cell, out Entity e));
        Assert.Equal(new CellCoord(3, 0), cell.Coord);        // ended owned by the saved position's cell
        Vector3 pos = cell.World.Get<ReplicatedPosition>(e).Value;
        Assert.Equal(35f, pos.X, 2);
        Assert.Equal(5f, pos.Z, 2);
    }

    [Fact]
    public void SaveOnLeave_ThenRestart_RestoresPositionAcrossCells()
    {
        var store = new InMemoryWorldStore();          // shared across the two "runs" = a restart
        byte[] token = Encoding.UTF8.GetBytes("acct-roam");
        var east = new MoveCommand(new Vector2(1f, 0f), run: true, cameraYaw: 0f);

        // Run 1: join at cell 0, walk east into cell 1+, leave (save-on-leave from the owner cell).
        Vector3 leftAt;
        {
            var (st, ct) = LoopbackTransport.CreatePair();
            var cfg = Cfg(_ => new Vector3(8f, 0f, 5f));
            var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default);
            var persistence = new WorldPersistence(server, store, new WorldPersistenceConfig { SaveIntervalSeconds = 999f });
            var client = new NetClient(ct, token);
            for (int i = 0; i < 60; i++) { client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); persistence.Update(cfg.TickSeconds); }

            for (int i = 0; i < 120; i++)
            {
                client.Send(MoveProtocol.EncodeMove(i, east), NetChannelReliability.ReliableOrdered);
                client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); persistence.Update(cfg.TickSeconds);
            }
            Assert.True(server.TryGetPlayerState(client.Slot, out PlayerMoveState before));
            leftAt = before.Position;
            Assert.True(leftAt.X > 10f, "should have crossed into cell 1+");

            ct.Disconnect(default);                            // client drops -> server fires Left -> save-on-leave
            for (int i = 0; i < 10; i++) { server.Poll(); server.Tick(cfg.TickSeconds); persistence.Update(cfg.TickSeconds); }
            persistence.FlushAsync().GetAwaiter().GetResult();   // ensure save-on-leave landed
            Assert.Equal(0, server.PlayerCount);
        }

        // Run 2: fresh server, SAME store. Same account reconnects, lands back where it left, in that cell.
        {
            var (st, ct) = LoopbackTransport.CreatePair();
            var cfg = Cfg(_ => new Vector3(5f, 0f, 5f));        // default spawn is cell 0
            var server = new ShardedWorldServer(st, cfg, Flat, MoveTuning.Default);
            var persistence = new WorldPersistence(server, store, new WorldPersistenceConfig { SaveIntervalSeconds = 999f });
            var client = new NetClient(ct, token);
            for (int i = 0; i < 200; i++) { client.Poll(); server.Poll(); server.Tick(cfg.TickSeconds); persistence.Update(cfg.TickSeconds); }

            Assert.True(server.TryGetPlayerState(client.Slot, out PlayerMoveState restored));
            Assert.Equal(leftAt.X, restored.Position.X, 1);
            Assert.Equal(leftAt.Z, restored.Position.Z, 1);
            Assert.True(server.TryGetPlayerNetId(client.Slot, out int netId));
            Assert.True(server.Host.TryGetOwner(netId, out CellSim cell, out _));
            Assert.True(cell.Coord.X >= 1, "restored into the cell containing the saved position");
        }
    }
}
